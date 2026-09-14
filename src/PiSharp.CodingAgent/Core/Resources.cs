namespace PiSharp.CodingAgent.Core;

/// <summary>"user" | "project" | "temporary".</summary>
public sealed record SourceInfo(string Path, string Source, string Scope, string Origin, string? BaseDir = null)
{
    public static SourceInfo FromMetadata(string path, PathMetadata metadata) =>
        new(path, metadata.Source, metadata.Scope, metadata.Origin, metadata.BaseDir);

    public static SourceInfo Synthetic(string path, string source, string? scope = null, string? origin = null, string? baseDir = null) =>
        new(path, source, scope ?? "temporary", origin ?? "top-level", baseDir);
}

/// <summary>Where a resolved resource came from. Origin is "package" or "top-level".</summary>
public sealed record PathMetadata(string Source, string Scope, string Origin, string? BaseDir = null);

public sealed record ResourceCollision(string ResourceType, string Name, string WinnerPath, string LoserPath, string? WinnerSource = null, string? LoserSource = null);

/// <summary>Type is "warning" | "error" | "collision".</summary>
public sealed record ResourceDiagnostic(string Type, string Message, string? Path = null, ResourceCollision? Collision = null);

public sealed record ContextFile(string Path, string Content);

internal static class ResourcePaths
{
    public static bool IsUnderPath(string target, string root)
    {
        var normalizedRoot = System.IO.Path.GetFullPath(root);
        if (target == normalizedRoot) return true;
        var prefix = normalizedRoot.EndsWith(System.IO.Path.DirectorySeparatorChar) ? normalizedRoot : normalizedRoot + System.IO.Path.DirectorySeparatorChar;
        return target.StartsWith(prefix, StringComparison.Ordinal);
    }

    /// <summary>Whether the entry is a file, following symlinks. Returns null for broken links.</summary>
    public static (bool IsFile, bool IsDirectory)? Stat(FileSystemInfo entry)
    {
        if (entry.LinkTarget is null) return (entry is FileInfo, entry is DirectoryInfo);
        try
        {
            var target = entry.ResolveLinkTarget(returnFinalTarget: true);
            if (target is null || !target.Exists) return null;
            return (target is FileInfo, target is DirectoryInfo);
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static IEnumerable<FileSystemInfo> Entries(string dir) => new DirectoryInfo(dir).EnumerateFileSystemInfos();
}
