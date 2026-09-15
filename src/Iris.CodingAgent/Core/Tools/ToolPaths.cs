using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Iris.CodingAgent.Utils;

namespace Iris.CodingAgent.Core.Tools;

/// <summary>Path resolution for tools.</summary>
public static partial class ToolPaths
{
    private const char NarrowNoBreakSpace = (char)0x202F;
    private const char RightSingleQuote = (char)0x2019;

    private static readonly PathInputOptions ToolPathOptions = new() { NormalizeUnicodeSpaces = true, StripAtPrefix = true };

    [GeneratedRegex(" (AM|PM)\\.", RegexOptions.IgnoreCase)]
    private static partial Regex AmPm();

    public static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    public static string ExpandPath(string filePath) => PathUtils.NormalizePath(filePath, ToolPathOptions);

    /// <summary>Resolve a path relative to the given cwd. Handles ~ expansion and absolute paths.</summary>
    public static string ResolveToCwd(string filePath, string cwd) => PathUtils.ResolvePath(filePath, cwd, ToolPathOptions);

    private static string NfdVariant(string path)
    {
        try
        {
            return path.Normalize(NormalizationForm.FormD);
        }
        catch (Exception)
        {
            return path;
        }
    }

    public static string ResolveReadPath(string filePath, string cwd)
    {
        var resolved = ResolveToCwd(filePath, cwd);
        if (PathExists(resolved)) return resolved;

        var amPm = AmPm().Replace(resolved, m => $"{NarrowNoBreakSpace}{m.Groups[1].Value}.");
        if (amPm != resolved && PathExists(amPm)) return amPm;

        var nfd = NfdVariant(resolved);
        if (nfd != resolved && PathExists(nfd)) return nfd;

        var curly = resolved.Replace('\'', RightSingleQuote);
        if (curly != resolved && PathExists(curly)) return curly;

        var nfdCurly = nfd.Replace('\'', RightSingleQuote);
        if (nfdCurly != resolved && PathExists(nfdCurly)) return nfdCurly;

        return resolved;
    }
}

/// <summary>Serializes file mutation operations targeting the same file.</summary>
public static class FileMutationQueue
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private static string GetKey(string filePath)
    {
        var resolved = Path.GetFullPath(filePath);
        try
        {
            var info = new FileInfo(resolved);
            if (info.Exists && info.LinkTarget is not null)
            {
                return info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? resolved;
            }
        }
        catch (IOException)
        {
        }
        return resolved;
    }

    public static async Task<T> RunAsync<T>(string filePath, Func<Task<T>> fn)
    {
        var key = GetKey(filePath);
        var gate = Locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            return await fn();
        }
        finally
        {
            gate.Release();
        }
    }
}
