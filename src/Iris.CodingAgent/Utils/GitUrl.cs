using System.Text;
using System.Text.RegularExpressions;

namespace Iris.CodingAgent.Utils;

/// <summary>
/// Minimal WHATWG URL parser covering what git source parsing relies on (scheme, userinfo, host, port, path, query,
/// fragment, dot-segment normalization and percent-encoding sets). Node's URL is the reference behavior.
/// </summary>
internal sealed class WhatwgUrl
{
    private static readonly Dictionary<string, int?> SpecialSchemes = new()
    {
        ["ftp"] = 21, ["file"] = null, ["http"] = 80, ["https"] = 443, ["ws"] = 80, ["wss"] = 443,
    };

    public string Scheme { get; private set; } = "";
    public string Protocol => Scheme + ":";
    public string Username { get; private set; } = "";
    public string Password { get; private set; } = "";
    public string? Host { get; private set; }
    public string Hostname => Host ?? "";
    public string Port { get; private set; } = "";
    public List<string> PathSegments { get; private set; } = [];
    public string? OpaquePath { get; private set; }
    public string? Query { get; private set; }
    public string? Fragment { get; private set; }

    public bool IsSpecial => SpecialSchemes.ContainsKey(Scheme);

    public string Pathname => OpaquePath ?? (PathSegments.Count == 0 && !IsSpecial && Host is null ? "" : "/" + string.Join("/", PathSegments));

    public string Hash => string.IsNullOrEmpty(Fragment) ? "" : "#" + Fragment;

    public string Search => string.IsNullOrEmpty(Query) ? "" : "?" + Query;

    private static bool InC0ControlSet(char c) => c <= 0x1f || c > 0x7e;

    private static string PercentEncode(string input, Func<char, bool> inSet)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(input))
        {
            var c = (char)b;
            if (b > 0x7e || inSet(c)) sb.Append('%').Append(b.ToString("X2"));
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static bool FragmentSet(char c) => InC0ControlSet(c) || c is ' ' or '"' or '<' or '>' or '`';

    private static bool QuerySet(char c) => InC0ControlSet(c) || c is ' ' or '"' or '#' or '<' or '>';

    private static bool SpecialQuerySet(char c) => QuerySet(c) || c == '\'';

    private static bool PathSet(char c) => QuerySet(c) || c is '?' or '^' or '`' or '{' or '}';

    private static bool UserinfoSet(char c) => PathSet(c) || c is '/' or ':' or ';' or '=' or '@' or (>= '[' and <= ']') or '|';

    private static bool IsSingleDot(string segment) => segment == "." || segment.Equals("%2e", StringComparison.OrdinalIgnoreCase);

    private static bool IsDoubleDot(string segment) =>
        segment is ".." || segment.Equals(".%2e", StringComparison.OrdinalIgnoreCase) || segment.Equals("%2e.", StringComparison.OrdinalIgnoreCase)
        || segment.Equals("%2e%2e", StringComparison.OrdinalIgnoreCase);

    public static WhatwgUrl? Parse(string input)
    {
        var s = input.Trim('\0', '\x01', '\x02', '\x03', '\x04', '\x05', '\x06', '\x07', '\x08', '\t', '\n', '\x0b', '\x0c', '\r', '\x0e', '\x0f',
            '\x10', '\x11', '\x12', '\x13', '\x14', '\x15', '\x16', '\x17', '\x18', '\x19', '\x1a', '\x1b', '\x1c', '\x1d', '\x1e', '\x1f', ' ');
        s = s.Replace("\t", "").Replace("\n", "").Replace("\r", "");
        var schemeMatch = Regex.Match(s, "^([A-Za-z][A-Za-z0-9+.-]*):");
        if (!schemeMatch.Success) return null;
        var url = new WhatwgUrl { Scheme = schemeMatch.Groups[1].Value.ToLowerInvariant() };
        var rest = s[schemeMatch.Length..];

        // Fragment and query.
        var hashIndex = rest.IndexOf('#');
        if (hashIndex >= 0)
        {
            url.Fragment = PercentEncode(rest[(hashIndex + 1)..], FragmentSet);
            rest = rest[..hashIndex];
        }

        if (url.IsSpecial)
        {
            if (url.Scheme == "file") return null;
            rest = rest.TrimStart('/', '\\');
            var end = rest.IndexOfAny(['/', '\\', '?']);
            var authority = end < 0 ? rest : rest[..end];
            rest = end < 0 ? "" : rest[end..];
            if (!url.ParseAuthority(authority, special: true)) return null;
            if (string.IsNullOrEmpty(url.Host)) return null;
        }
        else if (rest.StartsWith("//", StringComparison.Ordinal))
        {
            rest = rest[2..];
            var end = rest.IndexOfAny(['/', '?']);
            var authority = end < 0 ? rest : rest[..end];
            rest = end < 0 ? "" : rest[end..];
            if (!url.ParseAuthority(authority, special: false)) return null;
        }
        else if (!rest.StartsWith('/'))
        {
            var queryStart = rest.IndexOf('?');
            var opaque = queryStart < 0 ? rest : rest[..queryStart];
            if (queryStart >= 0) url.Query = PercentEncode(rest[(queryStart + 1)..], QuerySet);
            url.OpaquePath = PercentEncode(opaque, InC0ControlSet);
            return url;
        }

        var qIndex = rest.IndexOf('?');
        if (qIndex >= 0)
        {
            url.Query = PercentEncode(rest[(qIndex + 1)..], url.IsSpecial ? SpecialQuerySet : QuerySet);
            rest = rest[..qIndex];
        }
        url.SetPath(rest);
        return url;
    }

    private bool ParseAuthority(string authority, bool special)
    {
        var at = authority.LastIndexOf('@');
        if (at >= 0)
        {
            var userinfo = authority[..at];
            authority = authority[(at + 1)..];
            var colon = userinfo.IndexOf(':');
            Username = PercentEncode(colon < 0 ? userinfo : userinfo[..colon], UserinfoSet);
            Password = colon < 0 ? "" : PercentEncode(userinfo[(colon + 1)..], UserinfoSet);
            if (authority.Length == 0) return false;
        }
        string host;
        var portText = "";
        if (authority.StartsWith('['))
        {
            var close = authority.IndexOf(']');
            if (close < 0) return false;
            host = authority[..(close + 1)];
            var after = authority[(close + 1)..];
            if (after.Length > 0)
            {
                if (!after.StartsWith(':')) return false;
                portText = after[1..];
            }
        }
        else
        {
            var colon = authority.IndexOf(':');
            host = colon < 0 ? authority : authority[..colon];
            if (colon >= 0) portText = authority[(colon + 1)..];
        }
        if (portText.Length > 0)
        {
            if (!portText.All(char.IsAsciiDigit)) return false;
            var port = int.Parse(portText.TrimStart('0') is { Length: > 0 } trimmed ? trimmed : "0");
            if (portText.TrimStart('0').Length > 5 || port > 65535) return false;
            Port = SpecialSchemes.GetValueOrDefault(Scheme) == port ? "" : port.ToString();
        }
        if (special)
        {
            if (host.Length == 0) return false;
            string decoded;
            try
            {
                decoded = Uri.UnescapeDataString(host);
            }
            catch
            {
                return false;
            }
            if (decoded.IndexOfAny([' ', '#', '%', '/', ':', '<', '>', '?', '@', '\\', '^', '|', '\t', '\n', '\r', '\0']) >= 0 && !decoded.StartsWith('[')) return false;
            if (decoded.Any(c => c <= 0x1f || c == 0x7f)) return false;
            Host = decoded.ToLowerInvariant();
        }
        else
        {
            if (host.IndexOfAny([' ', '#', '/', ':', '<', '>', '?', '@', '\\', '^', '|', '\0']) >= 0 && !host.StartsWith('[')) return false;
            Host = PercentEncode(host, InC0ControlSet);
        }
        return true;
    }

    private void SetPath(string path)
    {
        var segments = new List<string>();
        if (path.Length > 0)
        {
            var normalized = IsSpecial ? path.Replace('\\', '/') : path;
            var parts = normalized.Split('/');
            for (var i = 1; i < parts.Length; i++)
            {
                var segment = PercentEncode(parts[i], PathSet);
                var isLast = i == parts.Length - 1;
                if (IsDoubleDot(segment))
                {
                    if (segments.Count > 0) segments.RemoveAt(segments.Count - 1);
                    if (isLast) segments.Add("");
                }
                else if (IsSingleDot(segment))
                {
                    if (isLast) segments.Add("");
                }
                else
                {
                    segments.Add(segment);
                }
            }
        }
        else if (IsSpecial)
        {
            segments.Add("");
        }
        PathSegments = segments;
    }

    /// <summary>Replace the path, as `url.pathname = value` does.</summary>
    public void SetPathname(string value)
    {
        if (OpaquePath is not null) return;
        SetPath(value.StartsWith('/') ? value : "/" + value);
    }

    public override string ToString()
    {
        var sb = new StringBuilder(Protocol);
        if (Host is not null)
        {
            sb.Append("//");
            if (Username.Length > 0 || Password.Length > 0)
            {
                sb.Append(Username);
                if (Password.Length > 0) sb.Append(':').Append(Password);
                sb.Append('@');
            }
            sb.Append(Host);
            if (Port.Length > 0) sb.Append(':').Append(Port);
        }
        sb.Append(Pathname);
        if (Query is not null) sb.Append('?').Append(Query);
        if (Fragment is not null) sb.Append('#').Append(Fragment);
        return sb.ToString();
    }
}

/// <summary>Hosted git provider recognition. Port of the parts of hosted-git-info 9 used by pi's git source parsing.</summary>
internal static class HostedGitInfo
{
    public sealed record Info(string Type, string Domain, string? User, string Project, string? Committish);

    private sealed record Host(string Name, string Domain, string[] Protocols, Func<WhatwgUrl, (string? User, string Project, string Committish)?> Extract);

    private static readonly Host[] Hosts =
    [
        new("github", "github.com", ["git:", "http:", "git+ssh:", "git+https:", "ssh:", "https:"], url =>
        {
            var parts = url.Pathname.Split('/');
            var user = parts.ElementAtOrDefault(1);
            var project = parts.ElementAtOrDefault(2);
            var type = parts.ElementAtOrDefault(3);
            var committish = parts.ElementAtOrDefault(4);
            if (!string.IsNullOrEmpty(type) && type != "tree") return null;
            if (string.IsNullOrEmpty(type)) committish = url.Hash.Length > 0 ? url.Hash[1..] : "";
            if (project is not null && project.EndsWith(".git", StringComparison.Ordinal)) project = project[..^4];
            if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(project)) return null;
            return (user, project, committish ?? "");
        }),
        new("bitbucket", "bitbucket.org", ["git+ssh:", "git+https:", "ssh:", "https:"], url =>
        {
            var parts = url.Pathname.Split('/');
            var user = parts.ElementAtOrDefault(1);
            var project = parts.ElementAtOrDefault(2);
            if (parts.ElementAtOrDefault(3) == "get") return null;
            if (project is not null && project.EndsWith(".git", StringComparison.Ordinal)) project = project[..^4];
            if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(project)) return null;
            return (user, project, url.Hash.Length > 0 ? url.Hash[1..] : "");
        }),
        new("gitlab", "gitlab.com", ["git+ssh:", "git+https:", "ssh:", "https:"], url =>
        {
            var path = url.Pathname.Length > 0 ? url.Pathname[1..] : "";
            if (path.Contains("/-/") || path.Contains("/archive.tar.gz")) return null;
            var segments = path.Split('/').ToList();
            var project = segments[^1];
            segments.RemoveAt(segments.Count - 1);
            if (project.EndsWith(".git", StringComparison.Ordinal)) project = project[..^4];
            var user = string.Join("/", segments);
            if (user.Length == 0 || project.Length == 0) return null;
            return (user, project, url.Hash.Length > 0 ? url.Hash[1..] : "");
        }),
        new("gist", "gist.github.com", ["git:", "git+ssh:", "git+https:", "ssh:", "https:"], url =>
        {
            var parts = url.Pathname.Split('/');
            string? user = parts.ElementAtOrDefault(1);
            var project = parts.ElementAtOrDefault(2);
            if (parts.ElementAtOrDefault(3) == "raw") return null;
            if (string.IsNullOrEmpty(project))
            {
                if (string.IsNullOrEmpty(user)) return null;
                project = user;
                user = null;
            }
            if (project.EndsWith(".git", StringComparison.Ordinal)) project = project[..^4];
            return (user, project, url.Hash.Length > 0 ? url.Hash[1..] : "");
        }),
        new("sourcehut", "git.sr.ht", ["git+ssh:", "https:"], url =>
        {
            var parts = url.Pathname.Split('/');
            var user = parts.ElementAtOrDefault(1);
            var project = parts.ElementAtOrDefault(2);
            if (parts.ElementAtOrDefault(3) == "archive") return null;
            if (project is not null && project.EndsWith(".git", StringComparison.Ordinal)) project = project[..^4];
            if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(project)) return null;
            return (user, project, url.Hash.Length > 0 ? url.Hash[1..] : "");
        }),
    ];

    private static readonly HashSet<string> Protocols =
    [
        "git+ssh:", "ssh:", "git+https:", "git:", "http:", "https:", "git+http:", .. Hosts.Select(h => h.Name + ":"),
    ];

    private static bool IsGitHubShorthand(string arg)
    {
        var firstHash = arg.IndexOf('#');
        var firstSlash = arg.IndexOf('/');
        var secondSlash = firstSlash < 0 ? arg.IndexOf('/', 0) : arg.IndexOf('/', firstSlash + 1);
        if (firstSlash < 0) secondSlash = arg.IndexOf('/');
        var firstColon = arg.IndexOf(':');
        var firstSpace = Regex.Match(arg, @"\s");
        var firstAt = arg.IndexOf('@');

        var spaceOnlyAfterHash = !firstSpace.Success || (firstHash > -1 && firstSpace.Index > firstHash);
        var atOnlyAfterHash = firstAt == -1 || (firstHash > -1 && firstAt > firstHash);
        var colonOnlyAfterHash = firstColon == -1 || (firstHash > -1 && firstColon > firstHash);
        var secondSlashOnlyAfterHash = secondSlash == -1 || (firstHash > -1 && secondSlash > firstHash);
        var hasSlash = firstSlash > 0;
        var doesNotEndWithSlash = firstHash > -1 ? (firstHash == 0 || arg[firstHash - 1] != '/') : !arg.EndsWith('/');
        var doesNotStartWithDot = !arg.StartsWith('.');
        return spaceOnlyAfterHash && hasSlash && doesNotEndWithSlash && doesNotStartWithDot && atOnlyAfterHash && colonOnlyAfterHash && secondSlashOnlyAfterHash;
    }

    private static int LastIndexOfBefore(string str, char c, char beforeChar)
    {
        var startPosition = str.IndexOf(beforeChar);
        return startPosition > -1 ? (startPosition == 0 ? (str[0] == c ? 0 : -1) : str.LastIndexOf(c, startPosition)) : str.LastIndexOf(c);
    }

    private static string CorrectProtocol(string arg)
    {
        var firstColon = arg.IndexOf(':');
        var proto = arg[..(firstColon + 1)];
        if (Protocols.Contains(proto)) return arg;
        var substrStart = firstColon < 0 ? Math.Max(0, arg.Length - 1) : firstColon;
        if (substrStart < arg.Length && arg.Substring(substrStart, Math.Min(3, arg.Length - substrStart)) == "://") return arg;
        var firstAt = arg.IndexOf('@');
        if (firstAt > -1) return firstAt > firstColon ? $"git+ssh://{arg}" : arg;
        return $"{arg[..(firstColon + 1)]}//{arg[(firstColon + 1)..]}";
    }

    private static string CorrectUrl(string giturl)
    {
        var firstAt = LastIndexOfBefore(giturl, '@', '#');
        var lastColonBeforeHash = LastIndexOfBefore(giturl, ':', '#');
        if (lastColonBeforeHash > firstAt) giturl = giturl[..lastColonBeforeHash] + "/" + giturl[(lastColonBeforeHash + 1)..];
        if (LastIndexOfBefore(giturl, ':', '#') == -1 && !giturl.Contains("//", StringComparison.Ordinal)) giturl = $"git+ssh://{giturl}";
        return giturl;
    }

    private static string? DecodeUriComponent(string value)
    {
        try
        {
            var bytes = new List<byte>();
            var sb = new StringBuilder();
            for (var i = 0; i < value.Length; i++)
            {
                if (value[i] == '%')
                {
                    if (i + 2 >= value.Length || !Uri.IsHexDigit(value[i + 1]) || !Uri.IsHexDigit(value[i + 2])) return null;
                    bytes.Add(Convert.ToByte(value.Substring(i + 1, 2), 16));
                    i += 2;
                    continue;
                }
                if (bytes.Count > 0)
                {
                    sb.Append(new UTF8Encoding(false, true).GetString(bytes.ToArray()));
                    bytes.Clear();
                }
                sb.Append(value[i]);
            }
            if (bytes.Count > 0) sb.Append(new UTF8Encoding(false, true).GetString(bytes.ToArray()));
            return sb.ToString();
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    public static Info? FromUrl(string? giturl)
    {
        if (string.IsNullOrEmpty(giturl)) return null;
        var corrected = IsGitHubShorthand(giturl) ? $"github:{giturl}" : giturl;
        var withProtocol = CorrectProtocol(corrected);
        var parsed = WhatwgUrl.Parse(withProtocol) ?? WhatwgUrl.Parse(CorrectUrl(withProtocol));
        if (parsed is null) return null;

        var shortcut = Hosts.FirstOrDefault(h => h.Name + ":" == parsed.Protocol);
        var hostname = parsed.Hostname.StartsWith("www.", StringComparison.Ordinal) ? parsed.Hostname[4..] : parsed.Hostname;
        var byDomain = Hosts.FirstOrDefault(h => h.Domain == hostname);
        var host = shortcut ?? byDomain;
        if (host is null) return null;

        string? user;
        string project;
        string? committish = null;
        if (shortcut is not null)
        {
            var pathname = parsed.Pathname.StartsWith('/') ? parsed.Pathname[1..] : parsed.Pathname;
            var firstAt = pathname.IndexOf('@');
            if (firstAt > -1) pathname = pathname[(firstAt + 1)..];
            var lastSlash = pathname.LastIndexOf('/');
            if (lastSlash > -1)
            {
                user = DecodeUriComponent(pathname[..lastSlash]);
                if (user is null) return null;
                if (user.Length == 0) user = null;
                project = DecodeUriComponent(pathname[(lastSlash + 1)..]) ?? throw new InvalidOperationException();
            }
            else
            {
                user = null;
                project = DecodeUriComponent(pathname) ?? "";
                if (DecodeUriComponent(pathname) is null) return null;
            }
            if (project.EndsWith(".git", StringComparison.Ordinal)) project = project[..^4];
            if (parsed.Hash.Length > 0)
            {
                committish = DecodeUriComponent(parsed.Hash[1..]);
                if (committish is null) return null;
            }
        }
        else
        {
            if (!host.Protocols.Contains(parsed.Protocol)) return null;
            if (host.Extract(parsed) is not { } segments) return null;
            if (segments.User is null) user = null;
            else if ((user = DecodeUriComponent(segments.User)) is null) return null;
            if (DecodeUriComponent(segments.Project) is not { } decodedProject) return null;
            project = decodedProject;
            if (DecodeUriComponent(segments.Committish) is not { } decodedCommittish) return null;
            committish = decodedCommittish;
        }
        return new Info(host.Name, host.Domain, user, project, committish);
    }
}

/// <summary>Parsed git package source. Port of GitSource from utils/git.ts.</summary>
public sealed record GitSource(string Repo, string Host, string Path, string? Ref, bool Pinned);

/// <summary>Git package source parsing. Port of utils/git.ts.</summary>
public static partial class GitUrlParser
{
    private static (string Repo, string? Ref) SplitRef(string url)
    {
        var scpLike = Regex.Match(url, "^git@([^:]+):(.+)$", RegexOptions.Singleline);
        if (scpLike.Success)
        {
            var pathWithMaybeRef = scpLike.Groups[2].Value;
            var refSeparator = pathWithMaybeRef.IndexOf('@');
            if (refSeparator < 0) return (url, null);
            var repoPath = pathWithMaybeRef[..refSeparator];
            var @ref = pathWithMaybeRef[(refSeparator + 1)..];
            if (repoPath.Length == 0 || @ref.Length == 0) return (url, null);
            return ($"git@{scpLike.Groups[1].Value}:{repoPath}", @ref);
        }
        if (url.Contains("://", StringComparison.Ordinal))
        {
            var parsed = WhatwgUrl.Parse(url);
            if (parsed is null) return (url, null);
            var pathWithMaybeRef = parsed.Pathname.TrimStart('/');
            var refSeparator = pathWithMaybeRef.IndexOf('@');
            if (refSeparator < 0) return (url, null);
            var repoPath = pathWithMaybeRef[..refSeparator];
            var @ref = pathWithMaybeRef[(refSeparator + 1)..];
            if (repoPath.Length == 0 || @ref.Length == 0) return (url, null);
            parsed.SetPathname("/" + repoPath);
            var serialized = parsed.ToString();
            return (serialized.EndsWith('/') ? serialized[..^1] : serialized, @ref);
        }
        var slashIndex = url.IndexOf('/');
        if (slashIndex < 0) return (url, null);
        var host = url[..slashIndex];
        var rest = url[(slashIndex + 1)..];
        var separator = rest.IndexOf('@');
        if (separator < 0) return (url, null);
        var path = rest[..separator];
        var refValue = rest[(separator + 1)..];
        if (path.Length == 0 || refValue.Length == 0) return (url, null);
        return ($"{host}/{path}", refValue);
    }

    private static bool HasUnsafeGitInstallPart(string value, bool allowSlash)
    {
        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(value);
            if (Regex.IsMatch(value, "%(?![0-9A-Fa-f]{2})")) return true;
        }
        catch
        {
            return true;
        }
        foreach (var candidate in new[] { value, decoded })
        {
            if (candidate.Contains('\0') || candidate.Contains('\\') || candidate.StartsWith('/')) return true;
            if (!allowSlash && candidate.Contains('/')) return true;
            if (candidate.Split('/').Contains("..")) return true;
        }
        return false;
    }

    private static GitSource? Build(string repo, string host, string path, string? @ref)
    {
        if (path.StartsWith('/')) return null;
        var normalizedPath = Regex.Replace(Regex.Replace(path, @"\.git$", ""), "^/+", "");
        if (host.Length == 0 || normalizedPath.Length == 0 || normalizedPath.Split('/').Length < 2) return null;
        if (HasUnsafeGitInstallPart(host, false) || HasUnsafeGitInstallPart(normalizedPath, true)) return null;
        return new GitSource(repo, host, normalizedPath, string.IsNullOrEmpty(@ref) ? null : @ref, !string.IsNullOrEmpty(@ref));
    }

    private static GitSource? ParseGeneric(string url)
    {
        var (repoWithoutRef, @ref) = SplitRef(url);
        var repo = repoWithoutRef;
        string host;
        string path;
        var scpLike = Regex.Match(repoWithoutRef, "^git@([^:]+):(.+)$", RegexOptions.Singleline);
        if (scpLike.Success)
        {
            host = scpLike.Groups[1].Value;
            path = scpLike.Groups[2].Value;
        }
        else if (repoWithoutRef.StartsWith("https://", StringComparison.Ordinal) || repoWithoutRef.StartsWith("http://", StringComparison.Ordinal)
            || repoWithoutRef.StartsWith("ssh://", StringComparison.Ordinal) || repoWithoutRef.StartsWith("git://", StringComparison.Ordinal))
        {
            var parsed = WhatwgUrl.Parse(repoWithoutRef);
            if (parsed is null) return null;
            host = parsed.Hostname;
            path = parsed.Pathname.TrimStart('/');
        }
        else
        {
            var slashIndex = repoWithoutRef.IndexOf('/');
            if (slashIndex < 0) return null;
            host = repoWithoutRef[..slashIndex];
            path = repoWithoutRef[(slashIndex + 1)..];
            if (!host.Contains('.') && host != "localhost") return null;
            repo = $"https://{repoWithoutRef}";
        }
        return Build(repo, host, path, @ref);
    }

    [GeneratedRegex("^(https?|ssh|git)://", RegexOptions.IgnoreCase)]
    private static partial Regex ExplicitProtocol();

    /// <summary>With a git: prefix accept historical shorthand forms; without it only explicit protocol URLs.</summary>
    public static GitSource? Parse(string source)
    {
        var trimmed = source.Trim();
        var hasGitPrefix = trimmed.StartsWith("git:", StringComparison.Ordinal);
        var url = hasGitPrefix ? trimmed[4..].Trim() : trimmed;
        if (!hasGitPrefix && !ExplicitProtocol().IsMatch(url)) return null;

        var (splitRepo, splitRef) = SplitRef(url);
        var hostedCandidates = new[] { splitRef is not null ? $"{splitRepo}#{splitRef}" : null, url }.Where(v => !string.IsNullOrEmpty(v));
        foreach (var candidate in hostedCandidates)
        {
            if (HostedGitInfo.FromUrl(candidate) is not { } info) continue;
            if (splitRef is not null && info.Project.Contains('@')) continue;
            var useHttpsPrefix = !splitRepo.StartsWith("http://", StringComparison.Ordinal) && !splitRepo.StartsWith("https://", StringComparison.Ordinal)
                && !splitRepo.StartsWith("ssh://", StringComparison.Ordinal) && !splitRepo.StartsWith("git://", StringComparison.Ordinal)
                && !splitRepo.StartsWith("git@", StringComparison.Ordinal);
            return Build(useHttpsPrefix ? $"https://{splitRepo}" : splitRepo, info.Domain, $"{info.User ?? "null"}/{info.Project}", string.IsNullOrEmpty(info.Committish) ? splitRef : info.Committish);
        }

        var httpsCandidates = new[] { splitRef is not null ? $"https://{splitRepo}#{splitRef}" : null, $"https://{url}" }.Where(v => !string.IsNullOrEmpty(v));
        foreach (var candidate in httpsCandidates)
        {
            if (HostedGitInfo.FromUrl(candidate) is not { } info) continue;
            if (splitRef is not null && info.Project.Contains('@')) continue;
            return Build($"https://{splitRepo}", info.Domain, $"{info.User ?? "null"}/{info.Project}", string.IsNullOrEmpty(info.Committish) ? splitRef : info.Committish);
        }

        return ParseGeneric(url);
    }
}
