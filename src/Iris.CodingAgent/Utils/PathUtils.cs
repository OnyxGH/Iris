using System.Text.RegularExpressions;

namespace Iris.CodingAgent.Utils;

public sealed class PathInputOptions
{
    public bool Trim { get; init; }
    public bool ExpandTilde { get; init; } = true;
    public string? HomeDir { get; init; }
    public bool StripAtPrefix { get; init; }
    public bool NormalizeUnicodeSpaces { get; init; }
}

/// <summary>Path normalization helpers. Port of utils/paths.ts.</summary>
public static partial class PathUtils
{
    [GeneratedRegex("[\\u00A0\\u2000-\\u200A\\u202F\\u205F\\u3000]")]
    private static partial Regex UnicodeSpaces();

    [GeneratedRegex(@"^/(?:mnt/|cygdrive/)?([a-z])(?:/(.*))?$", RegexOptions.IgnoreCase)]
    private static partial Regex ShellDrivePath();

    public static string CanonicalizePath(string path)
    {
        try
        {
            var info = new FileInfo(path);
            var target = info.Exists || Directory.Exists(path) ? info.ResolveLinkTarget(returnFinalTarget: true) : null;
            return target?.FullName ?? Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }

    public static string? GetFileRevision(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            return $"{info.Length}:{info.LastWriteTimeUtc.Ticks}:{info.CreationTimeUtc.Ticks}";
        }
        catch
        {
            return null;
        }
    }

    /// <summary>True if the value is not a remote URL.</summary>
    public static bool IsLocalPath(string value)
    {
        var trimmed = value.Trim();
        return !(trimmed.StartsWith("http:") || trimmed.StartsWith("https:") || trimmed.StartsWith("ssh:"));
    }

    /// <summary>Convert Git Bash, MSYS, Cygwin and WSL drive paths to native Windows paths.</summary>
    public static string NormalizeWindowsShellPath(string filePath)
    {
        if (!filePath.StartsWith('/') || filePath.StartsWith("//") || filePath.Contains('\\')) return filePath;
        var match = ShellDrivePath().Match(filePath);
        if (!match.Success) return filePath;
        var suffix = match.Groups[2].Success ? match.Groups[2].Value.Replace('/', '\\') : "";
        return $"{match.Groups[1].Value.ToUpperInvariant()}:\\{suffix}";
    }

    public static string NormalizePath(string input, PathInputOptions? options = null)
    {
        options ??= new PathInputOptions();
        var normalized = options.Trim ? input.Trim() : input;
        if (options.NormalizeUnicodeSpaces) normalized = UnicodeSpaces().Replace(normalized, " ");
        if (options.StripAtPrefix && normalized.StartsWith('@')) normalized = normalized[1..];
        if (OperatingSystem.IsWindows()) normalized = NormalizeWindowsShellPath(normalized);

        if (options.ExpandTilde)
        {
            var home = options.HomeDir ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (normalized == "~") return home;
            if (normalized.StartsWith("~/") || (OperatingSystem.IsWindows() && normalized.StartsWith("~\\")))
            {
                return Path.Combine(home, normalized[2..]);
            }
        }

        if (normalized.StartsWith("file://", StringComparison.Ordinal) && Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
        {
            return uri.LocalPath;
        }
        return normalized;
    }

    public static string ResolvePath(string input, string? baseDir = null, PathInputOptions? options = null)
    {
        var normalized = NormalizePath(input, options);
        var normalizedBase = NormalizePath(baseDir ?? Directory.GetCurrentDirectory());
        return Path.IsPathRooted(normalized) ? Path.GetFullPath(normalized) : Path.GetFullPath(Path.Combine(normalizedBase, normalized));
    }

    public static string? GetCwdRelativePath(string filePath, string cwd)
    {
        var resolvedCwd = ResolvePath(cwd);
        var resolvedPath = ResolvePath(filePath, resolvedCwd);
        var relative = Path.GetRelativePath(resolvedCwd, resolvedPath);
        var inside = relative == "." || (relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar) && !Path.IsPathRooted(relative));
        return inside ? (relative.Length == 0 ? "." : relative) : null;
    }

    public static string FormatPathRelativeToCwdOrAbsolute(string filePath, string cwd)
    {
        var absolute = ResolvePath(filePath, cwd);
        return (GetCwdRelativePath(absolute, cwd) ?? absolute).Replace(Path.DirectorySeparatorChar, '/');
    }
}

