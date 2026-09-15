using System.Diagnostics;
using System.Text.RegularExpressions;

namespace PiSharp.Tui;

public sealed record AutocompleteItem(string Value, string Label, string? Description = null);

public class SlashCommand
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? ArgumentHint { get; init; }

    /// <summary>Returns argument completions, or null when none are available.</summary>
    public Func<string, Task<List<AutocompleteItem>?>>? GetArgumentCompletions { get; init; }
}

public sealed record AutocompleteSuggestions(List<AutocompleteItem> Items, string Prefix);

public sealed record CompletionResult(List<string> Lines, int CursorLine, int CursorCol);

public interface IAutocompleteProvider
{
    /// <summary>Characters that naturally trigger this provider at token boundaries.</summary>
    IReadOnlyList<string>? TriggerCharacters => null;

    Task<AutocompleteSuggestions?> GetSuggestionsAsync(IReadOnlyList<string> lines, int cursorLine, int cursorCol, bool force, CancellationToken cancellationToken);

    CompletionResult ApplyCompletion(IReadOnlyList<string> lines, int cursorLine, int cursorCol, AutocompleteItem item, string prefix);

    bool ShouldTriggerFileCompletion(IReadOnlyList<string> lines, int cursorLine, int cursorCol) => true;
}

/// <summary>Slash command and file path completion. Port of pi-tui CombinedAutocompleteProvider.</summary>
public sealed partial class CombinedAutocompleteProvider : IAutocompleteProvider
{
    private static readonly HashSet<char> PathDelimiters = [' ', '\t', '"', '\'', '='];

    // Either a SlashCommand or an AutocompleteItem.
    private readonly List<object> _commands;
    private readonly string _basePath;
    private readonly string? _fdPath;

    public CombinedAutocompleteProvider(IEnumerable<object>? commands, string basePath, string? fdPath = null)
    {
        _commands = commands?.ToList() ?? [];
        _basePath = basePath;
        _fdPath = fdPath;
    }

    private static string ToDisplayPath(string value) => value.Replace('\\', '/');

    [GeneratedRegex(@"[.*+?^${}()|[\]\\]")]
    private static partial Regex RegexSpecial();

    [GeneratedRegex("^/+|/+$")]
    private static partial Regex OuterSlashes();

    private static string BuildFdPathQuery(string query)
    {
        var normalized = ToDisplayPath(query);
        if (!normalized.Contains('/')) return normalized;
        var hasTrailing = normalized.EndsWith('/');
        var trimmed = OuterSlashes().Replace(normalized, "");
        if (trimmed.Length == 0) return normalized;
        const string separatorPattern = "[\\\\/]";
        var segments = trimmed.Split('/').Where(s => s.Length > 0).Select(s => RegexSpecial().Replace(s, "\\$0")).ToList();
        if (segments.Count == 0) return normalized;
        var pattern = string.Join(separatorPattern, segments);
        if (hasTrailing) pattern += separatorPattern;
        return pattern;
    }

    private static int FindLastDelimiter(string text)
    {
        for (var i = text.Length - 1; i >= 0; i--)
        {
            if (PathDelimiters.Contains(text[i])) return i;
        }
        return -1;
    }

    private static int? FindUnclosedQuoteStart(string text)
    {
        var inQuotes = false;
        var quoteStart = -1;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '"') continue;
            inQuotes = !inQuotes;
            if (inQuotes) quoteStart = i;
        }
        return inQuotes ? quoteStart : null;
    }

    private static bool IsTokenStart(string text, int index) => index == 0 || PathDelimiters.Contains(text[index - 1]);

    private static string? ExtractQuotedPrefix(string text)
    {
        if (FindUnclosedQuoteStart(text) is not { } quoteStart) return null;
        if (quoteStart > 0 && text[quoteStart - 1] == '@')
        {
            return IsTokenStart(text, quoteStart - 1) ? text[(quoteStart - 1)..] : null;
        }
        return IsTokenStart(text, quoteStart) ? text[quoteStart..] : null;
    }

    private static (string RawPrefix, bool IsAtPrefix, bool IsQuotedPrefix) ParsePathPrefix(string prefix)
    {
        if (prefix.StartsWith("@\"", StringComparison.Ordinal)) return (prefix[2..], true, true);
        if (prefix.StartsWith('"')) return (prefix[1..], false, true);
        if (prefix.StartsWith('@')) return (prefix[1..], true, false);
        return (prefix, false, false);
    }

    private static string BuildCompletionValue(string path, bool isAtPrefix, bool isQuotedPrefix)
    {
        var needsQuotes = isQuotedPrefix || path.Contains(' ');
        var prefix = isAtPrefix ? "@" : "";
        return needsQuotes ? $"{prefix}\"{path}\"" : prefix + path;
    }

    private static async Task<List<(string Path, bool IsDirectory)>> WalkDirectoryWithFdAsync(string baseDir, string fdPath, string query, int maxResults, CancellationToken ct, int? maxDepth = null)
    {
        if (ct.IsCancellationRequested) return [];
        var psi = new ProcessStartInfo(fdPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var arg in new[] { "--base-directory", baseDir, "--max-results", maxResults.ToString(), "--type", "f", "--type", "d", "--follow", "--hidden", "--exclude", ".git", "--exclude", ".git/*", "--exclude", ".git/**" })
        {
            psi.ArgumentList.Add(arg);
        }
        if (maxDepth is { } depth)
        {
            psi.ArgumentList.Add("--max-depth");
            psi.ArgumentList.Add(depth.ToString());
        }
        if (ToDisplayPath(query).Contains('/')) psi.ArgumentList.Add("--full-path");
        if (query.Length > 0) psi.ArgumentList.Add(BuildFdPathQuery(query));

        try
        {
            using var process = Process.Start(psi);
            if (process is null) return [];
            _ = process.StandardError.ReadToEndAsync(CancellationToken.None);
            await using var registration = ct.Register(() =>
            {
                try
                {
                    if (!process.HasExited) process.Kill(true);
                }
                catch
                {
                    // ignored
                }
            });
            var stdout = await process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            await process.WaitForExitAsync(CancellationToken.None);
            if (ct.IsCancellationRequested || process.ExitCode != 0 || stdout.Length == 0) return [];

            var results = new List<(string, bool)>();
            foreach (var line in stdout.Trim().Split('\n').Where(l => l.Length > 0))
            {
                var displayLine = ToDisplayPath(line.TrimEnd('\r'));
                var hasTrailing = displayLine.EndsWith('/');
                var normalizedPath = hasTrailing ? displayLine[..^1] : displayLine;
                if (normalizedPath == ".git" || normalizedPath.StartsWith(".git/", StringComparison.Ordinal) || normalizedPath.Contains("/.git/")) continue;
                results.Add((displayLine, hasTrailing));
            }
            return results;
        }
        catch
        {
            return [];
        }
    }

    private static string CommandName(object command) => command switch
    {
        SlashCommand s => s.Name,
        AutocompleteItem a => a.Value,
        _ => "",
    };

    public async Task<AutocompleteSuggestions?> GetSuggestionsAsync(IReadOnlyList<string> lines, int cursorLine, int cursorCol, bool force, CancellationToken cancellationToken)
    {
        var currentLine = cursorLine < lines.Count ? lines[cursorLine] : "";
        var textBeforeCursor = currentLine[..Math.Min(cursorCol, currentLine.Length)];

        if (ExtractAtPrefix(textBeforeCursor) is { } atPrefix)
        {
            var (rawPrefix, _, isQuotedPrefix) = ParsePathPrefix(atPrefix);
            var suggestions = await GetFuzzyFileSuggestionsAsync(rawPrefix, isQuotedPrefix, cancellationToken);
            return suggestions.Count == 0 ? null : new AutocompleteSuggestions(suggestions, atPrefix);
        }

        if (!force && textBeforeCursor.StartsWith('/'))
        {
            var spaceIndex = textBeforeCursor.IndexOf(' ');
            if (spaceIndex == -1)
            {
                var prefix = textBeforeCursor[1..];
                var commandItems = _commands.Select(cmd =>
                {
                    var name = CommandName(cmd);
                    var hint = cmd is SlashCommand { ArgumentHint: { Length: > 0 } h } ? h : null;
                    var desc = cmd switch
                    {
                        SlashCommand s => s.Description ?? "",
                        AutocompleteItem a => a.Description ?? "",
                        _ => "",
                    };
                    var fullDesc = hint is not null ? (desc.Length > 0 ? $"{hint} — {desc}" : hint) : desc;
                    return (Name: name, Label: name, Description: fullDesc.Length > 0 ? fullDesc : null);
                }).ToList();

                var filtered = Fuzzy.Filter(commandItems, prefix, i => i.Name)
                    .Select(i => new AutocompleteItem(i.Name, i.Label, i.Description))
                    .ToList();
                return filtered.Count == 0 ? null : new AutocompleteSuggestions(filtered, textBeforeCursor);
            }

            var commandName = textBeforeCursor[1..spaceIndex];
            var argumentText = textBeforeCursor[(spaceIndex + 1)..];
            if (_commands.FirstOrDefault(c => CommandName(c) == commandName) is not SlashCommand { GetArgumentCompletions: { } getArgs }) return null;
            var argumentSuggestions = await getArgs(argumentText);
            return argumentSuggestions is { Count: > 0 } ? new AutocompleteSuggestions(argumentSuggestions, argumentText) : null;
        }

        if (ExtractPathPrefix(textBeforeCursor, force) is not { } pathMatch) return null;
        var fileSuggestions = GetFileSuggestions(pathMatch);
        return fileSuggestions.Count == 0 ? null : new AutocompleteSuggestions(fileSuggestions, pathMatch);
    }

    public CompletionResult ApplyCompletion(IReadOnlyList<string> lines, int cursorLine, int cursorCol, AutocompleteItem item, string prefix)
    {
        var currentLine = cursorLine < lines.Count ? lines[cursorLine] : "";
        var beforePrefix = currentLine[..Math.Max(0, Math.Min(currentLine.Length, cursorCol - prefix.Length))];
        var afterCursor = currentLine[Math.Min(cursorCol, currentLine.Length)..];
        var isQuotedPrefix = prefix.StartsWith('"') || prefix.StartsWith("@\"", StringComparison.Ordinal);
        var adjustedAfterCursor = isQuotedPrefix && item.Value.EndsWith('"') && afterCursor.StartsWith('"') ? afterCursor[1..] : afterCursor;
        var newLines = lines.ToList();

        var isSlashCommand = prefix.StartsWith('/') && beforePrefix.Trim().Length == 0 && !prefix[1..].Contains('/');
        if (isSlashCommand)
        {
            newLines[cursorLine] = $"{beforePrefix}/{item.Value} {adjustedAfterCursor}";
            return new CompletionResult(newLines, cursorLine, beforePrefix.Length + item.Value.Length + 2);
        }

        var isDirectory = item.Label.EndsWith('/');
        var cursorOffset = isDirectory && item.Value.EndsWith('"') ? item.Value.Length - 1 : item.Value.Length;

        if (prefix.StartsWith('@'))
        {
            var suffix = isDirectory ? "" : " ";
            newLines[cursorLine] = beforePrefix + item.Value + suffix + adjustedAfterCursor;
            return new CompletionResult(newLines, cursorLine, beforePrefix.Length + cursorOffset + suffix.Length);
        }

        newLines[cursorLine] = beforePrefix + item.Value + adjustedAfterCursor;
        return new CompletionResult(newLines, cursorLine, beforePrefix.Length + cursorOffset);
    }

    private static string? ExtractAtPrefix(string text)
    {
        if (ExtractQuotedPrefix(text) is { } quoted && quoted.StartsWith("@\"", StringComparison.Ordinal)) return quoted;
        var last = FindLastDelimiter(text);
        var tokenStart = last == -1 ? 0 : last + 1;
        return tokenStart < text.Length && text[tokenStart] == '@' ? text[tokenStart..] : null;
    }

    private static string? ExtractPathPrefix(string text, bool forceExtract)
    {
        if (ExtractQuotedPrefix(text) is { Length: > 0 } quoted) return quoted;
        var last = FindLastDelimiter(text);
        var pathPrefix = last == -1 ? text : text[(last + 1)..];
        if (forceExtract) return pathPrefix;
        if (pathPrefix.Contains('/') || pathPrefix.StartsWith('.') || pathPrefix.StartsWith("~/", StringComparison.Ordinal)) return pathPrefix;
        if (pathPrefix.Length == 0 && text.EndsWith(' ')) return pathPrefix;
        return null;
    }

    private static string HomeDir => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string ExpandHomePath(string path)
    {
        if (path.StartsWith("~/", StringComparison.Ordinal))
        {
            var expanded = NodePath.Join(HomeDir, path[2..]);
            return path.EndsWith('/') && !expanded.EndsWith('/') && !expanded.EndsWith(Path.DirectorySeparatorChar) ? expanded + "/" : expanded;
        }
        return path == "~" ? HomeDir : path;
    }

    private (string BaseDir, string Query, string DisplayBase)? ResolveScopedFuzzyQuery(string rawQuery)
    {
        var normalized = ToDisplayPath(rawQuery);
        var slashIndex = normalized.LastIndexOf('/');
        if (slashIndex == -1) return null;
        var displayBase = normalized[..(slashIndex + 1)];
        var query = normalized[(slashIndex + 1)..];
        string baseDir;
        if (displayBase.StartsWith("~/", StringComparison.Ordinal)) baseDir = ExpandHomePath(displayBase);
        else if (displayBase.StartsWith('/')) baseDir = displayBase;
        else baseDir = NodePath.Join(_basePath, displayBase);
        return Directory.Exists(baseDir) ? (baseDir, query, displayBase) : null;
    }

    private static string ScopedPathForDisplay(string displayBase, string relativePath)
    {
        var normalized = ToDisplayPath(relativePath);
        return displayBase == "/" ? "/" + normalized : ToDisplayPath(displayBase) + normalized;
    }

    private static bool IsAbsoluteLike(string path) => path.StartsWith('/') || Path.IsPathFullyQualified(path);

    private List<AutocompleteItem> GetFileSuggestions(string prefix)
    {
        try
        {
            var (rawPrefix, isAtPrefix, isQuotedPrefix) = ParsePathPrefix(prefix);
            var expandedPrefix = rawPrefix;
            if (expandedPrefix.StartsWith('~')) expandedPrefix = ExpandHomePath(expandedPrefix);

            var isRootPrefix = rawPrefix is "" or "./" or "../" or "~" or "~/" or "/" || (isAtPrefix && rawPrefix.Length == 0);
            string searchDir;
            string searchPrefix;
            var absolute = rawPrefix.StartsWith('~') || IsAbsoluteLike(expandedPrefix);
            if (isRootPrefix || rawPrefix.EndsWith('/'))
            {
                searchDir = absolute ? expandedPrefix : NodePath.Join(_basePath, expandedPrefix);
                searchPrefix = "";
            }
            else
            {
                var dir = NodePath.Dirname(expandedPrefix);
                var file = NodePath.Basename(expandedPrefix);
                searchDir = absolute ? dir : NodePath.Join(_basePath, dir);
                searchPrefix = file;
            }

            var suggestions = new List<AutocompleteItem>();
            foreach (var entry in new DirectoryInfo(searchDir).EnumerateFileSystemInfos())
            {
                var name = entry.Name;
                if (!name.StartsWith(searchPrefix, StringComparison.OrdinalIgnoreCase)) continue;

                var isDirectory = entry is DirectoryInfo;
                if (entry.LinkTarget is not null)
                {
                    try
                    {
                        isDirectory = entry.ResolveLinkTarget(true) is DirectoryInfo { Exists: true };
                    }
                    catch
                    {
                        isDirectory = false;
                    }
                }

                string relativePath;
                var displayPrefix = rawPrefix;
                if (displayPrefix.EndsWith('/'))
                {
                    relativePath = displayPrefix + name;
                }
                else if (displayPrefix.Contains('/') || displayPrefix.Contains('\\'))
                {
                    if (displayPrefix.StartsWith("~/", StringComparison.Ordinal))
                    {
                        var dir = NodePath.Dirname(displayPrefix[2..]);
                        relativePath = "~/" + (dir == "." ? name : NodePath.Join(dir, name));
                    }
                    else if (displayPrefix.StartsWith('/'))
                    {
                        var dir = NodePath.Dirname(displayPrefix);
                        relativePath = dir == "/" ? "/" + name : $"{dir}/{name}";
                    }
                    else
                    {
                        relativePath = NodePath.Join(NodePath.Dirname(displayPrefix), name);
                        if (displayPrefix.StartsWith("./", StringComparison.Ordinal) && !relativePath.StartsWith("./", StringComparison.Ordinal)) relativePath = "./" + relativePath;
                    }
                }
                else
                {
                    relativePath = displayPrefix.StartsWith('~') ? "~/" + name : name;
                }

                relativePath = ToDisplayPath(relativePath);
                var pathValue = isDirectory ? relativePath + "/" : relativePath;
                suggestions.Add(new AutocompleteItem(BuildCompletionValue(pathValue, isAtPrefix, isQuotedPrefix), name + (isDirectory ? "/" : "")));
            }

            suggestions.Sort((a, b) =>
            {
                var aDir = a.Value.EndsWith('/');
                var bDir = b.Value.EndsWith('/');
                if (aDir && !bDir) return -1;
                if (!aDir && bDir) return 1;
                return NodeCompare.LocaleCompare(a.Label, b.Label);
            });
            return suggestions;
        }
        catch
        {
            return [];
        }
    }

    private static int ScoreEntry(string filePath, string query, bool isDirectory)
    {
        var fileName = NodePath.Basename(filePath).ToLowerInvariant();
        var lowerQuery = query.ToLowerInvariant();
        var score = 0;
        if (fileName == lowerQuery) score = 100;
        else if (fileName.StartsWith(lowerQuery, StringComparison.Ordinal)) score = 80;
        else if (fileName.Contains(lowerQuery, StringComparison.Ordinal)) score = 50;
        else if (filePath.ToLowerInvariant().Contains(lowerQuery, StringComparison.Ordinal)) score = 30;
        if (isDirectory && score > 0) score += 10;
        return score;
    }

    private async Task<List<AutocompleteItem>> GetFuzzyFileSuggestionsAsync(string query, bool isQuotedPrefix, CancellationToken ct)
    {
        if (_fdPath is null || ct.IsCancellationRequested) return [];
        try
        {
            var scoped = ResolveScopedFuzzyQuery(query);
            var fdBaseDir = scoped?.BaseDir ?? _basePath;
            var fdQuery = scoped?.Query ?? query;
            var baseDirEntries = await WalkDirectoryWithFdAsync(fdBaseDir, _fdPath, fdQuery, 100, ct, 1);
            var recursiveEntries = await WalkDirectoryWithFdAsync(fdBaseDir, _fdPath, fdQuery, 100, ct);
            var seen = new HashSet<string>(baseDirEntries.Select(e => e.Path));
            var entries = baseDirEntries.Concat(recursiveEntries.Where(e => seen.Add(e.Path))).ToList();
            if (ct.IsCancellationRequested) return [];

            var scored = entries
                .Select(e => (e.Path, e.IsDirectory, Score: fdQuery.Length > 0 ? ScoreEntry(e.Path, fdQuery, e.IsDirectory) : 1))
                .Where(e => e.Score > 0)
                .ToList();
            scored.Sort((a, b) =>
            {
                var diff = b.Score - a.Score;
                if (diff != 0) return diff;
                var aDepth = ToDisplayPath(a.Path).Split('/').Count(s => s.Length > 0);
                var bDepth = ToDisplayPath(b.Path).Split('/').Count(s => s.Length > 0);
                if (aDepth != bDepth) return aDepth - bDepth;
                if (a.Path.Length != b.Path.Length) return a.Path.Length - b.Path.Length;
                return NodeCompare.LocaleCompare(a.Path, b.Path);
            });

            var suggestions = new List<AutocompleteItem>();
            foreach (var (entryPath, isDirectory, _) in scored.Take(20))
            {
                var withoutSlash = isDirectory ? entryPath[..^1] : entryPath;
                var displayPath = scoped is { } s ? ScopedPathForDisplay(s.DisplayBase, withoutSlash) : withoutSlash;
                var entryName = NodePath.Basename(withoutSlash);
                var completionPath = isDirectory ? displayPath + "/" : displayPath;
                suggestions.Add(new AutocompleteItem(BuildCompletionValue(completionPath, true, isQuotedPrefix), entryName + (isDirectory ? "/" : ""), displayPath));
            }
            return suggestions;
        }
        catch
        {
            return [];
        }
    }

    public bool ShouldTriggerFileCompletion(IReadOnlyList<string> lines, int cursorLine, int cursorCol)
    {
        var currentLine = cursorLine < lines.Count ? lines[cursorLine] : "";
        var textBeforeCursor = currentLine[..Math.Min(cursorCol, currentLine.Length)].Trim();
        return !(textBeforeCursor.StartsWith('/') && !textBeforeCursor.Contains(' '));
    }
}

/// <summary>Minimal Node path semantics (join/dirname/basename) on the host platform.</summary>
public static class NodePath
{
    private static bool IsSep(char c) => c == '/' || (OperatingSystem.IsWindows() && c == '\\');

    public static string Join(params string[] parts)
    {
        var joined = string.Join("/", parts.Where(p => p.Length > 0));
        return joined.Length == 0 ? "." : Normalize(joined);
    }

    public static string Normalize(string path)
    {
        if (path.Length == 0) return ".";
        var sep = OperatingSystem.IsWindows() ? '\\' : '/';
        var root = "";
        var rest = path;
        if (OperatingSystem.IsWindows() && path.Length >= 2 && path[1] == ':')
        {
            root = path[..2];
            rest = path[2..];
        }
        var isAbsolute = rest.Length > 0 && IsSep(rest[0]);
        var trailing = rest.Length > 0 && IsSep(rest[^1]);
        var segments = new List<string>();
        foreach (var segment in rest.Split(OperatingSystem.IsWindows() ? ['/', '\\'] : ['/']))
        {
            if (segment.Length == 0 || segment == ".") continue;
            if (segment == "..")
            {
                if (segments.Count > 0 && segments[^1] != "..") segments.RemoveAt(segments.Count - 1);
                else if (!isAbsolute) segments.Add("..");
                continue;
            }
            segments.Add(segment);
        }
        var body = string.Join(sep, segments);
        if (isAbsolute) body = sep + body;
        if (body.Length == 0) body = isAbsolute ? sep.ToString() : ".";
        if (trailing && !body.EndsWith(sep)) body += sep;
        return root + body;
    }

    public static string Dirname(string path)
    {
        if (path.Length == 0) return ".";
        var end = path.Length;
        while (end > 1 && IsSep(path[end - 1])) end--;
        var i = end - 1;
        while (i >= 0 && !IsSep(path[i])) i--;
        if (i < 0) return ".";
        if (i == 0) return path[..1];
        var result = path[..i];
        while (result.Length > 1 && IsSep(result[^1])) result = result[..^1];
        return result;
    }

    public static string Basename(string path)
    {
        var end = path.Length;
        while (end > 0 && IsSep(path[end - 1])) end--;
        var i = end - 1;
        while (i >= 0 && !IsSep(path[i])) i--;
        return path[(i + 1)..end];
    }
}

/// <summary>Approximates JavaScript String.prototype.localeCompare with default ICU collation.</summary>
public static class NodeCompare
{
    private static readonly StringComparer Comparer = StringComparer.Create(System.Globalization.CultureInfo.InvariantCulture, System.Globalization.CompareOptions.None);

    public static int LocaleCompare(string a, string b) => Math.Sign(Comparer.Compare(a, b));
}
