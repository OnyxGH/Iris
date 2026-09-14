using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using PiSharp.CodingAgent.Config;

namespace PiSharp.CodingAgent.Utils;

public sealed record ShellConfig(string Shell, IReadOnlyList<string> Args, string CommandTransport = "argv");

/// <summary>Shell resolution and process helpers. Port of utils/shell.ts.</summary>
public static partial class ShellUtils
{
    public static readonly string[] PowerShellArgs = ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command"];

    [GeneratedRegex(@"^[a-z]:\\windows\\(?:system32|sysnative)\\bash\.exe$")]
    private static partial Regex LegacyWslBash();

    private static bool IsLegacyWslBashPath(string path) => LegacyWslBash().IsMatch(path.Replace('/', '\\').ToLowerInvariant());

    private static ShellConfig BashShellConfig(string shell) =>
        IsLegacyWslBashPath(shell) ? new ShellConfig(shell, ["-s"], "stdin") : new ShellConfig(shell, ["-c"]);

    public static string? FindExecutableOnPath(string executable)
    {
        try
        {
            var psi = new ProcessStartInfo(OperatingSystem.IsWindows() ? "where" : "which", executable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(5000)) return null;
            if (process.ExitCode != 0) return null;
            var first = output.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
            if (string.IsNullOrEmpty(first)) return null;
            return !OperatingSystem.IsWindows() || File.Exists(first) ? first : null;
        }
        catch
        {
            return null;
        }
    }

    private static ShellConfig? _cachedDefaultShell;

    /// <summary>Resolve the bash shell: custom path, then Git Bash / PATH on Windows, then /bin/bash, bash, sh.</summary>
    public static ShellConfig GetShellConfig(string? customShellPath = null)
    {
        if (!string.IsNullOrEmpty(customShellPath))
        {
            if (File.Exists(customShellPath)) return BashShellConfig(customShellPath);
            throw new FileNotFoundException($"Custom shell path not found: {customShellPath}");
        }
        if (_cachedDefaultShell is not null) return _cachedDefaultShell;

        if (OperatingSystem.IsWindows())
        {
            var paths = new List<string>();
            var programFiles = Environment.GetEnvironmentVariable("ProgramFiles");
            if (!string.IsNullOrEmpty(programFiles)) paths.Add($"{programFiles}\\Git\\bin\\bash.exe");
            var programFilesX86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
            if (!string.IsNullOrEmpty(programFilesX86)) paths.Add($"{programFilesX86}\\Git\\bin\\bash.exe");
            foreach (var path in paths)
            {
                if (File.Exists(path)) return _cachedDefaultShell = BashShellConfig(path);
            }
            var onPath = FindExecutableOnPath("bash.exe");
            if (onPath is not null) return _cachedDefaultShell = BashShellConfig(onPath);
            throw new InvalidOperationException(
                "No bash shell found. Options:\n" +
                "  1. Install Git for Windows: https://git-scm.com/download/win\n" +
                "  2. Add your bash to PATH (Cygwin, MSYS2, etc.)\n" +
                "  3. Set shellPath in settings.json\n\n" +
                $"Searched Git Bash in:\n{string.Join("\n", paths.Select(p => $"  {p}"))}");
        }

        if (File.Exists("/bin/bash")) return _cachedDefaultShell = BashShellConfig("/bin/bash");
        var bash = FindExecutableOnPath("bash");
        if (bash is not null) return _cachedDefaultShell = BashShellConfig(bash);
        return _cachedDefaultShell = new ShellConfig("sh", ["-c"]);
    }

    public static ShellConfig GetPowerShellConfig()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The powershell tool is only available on Windows.");
        var shell = FindExecutableOnPath("pwsh.exe") ?? FindExecutableOnPath("powershell.exe")
            ?? throw new InvalidOperationException("No PowerShell executable found. Install PowerShell or add powershell.exe/pwsh.exe to PATH.");
        return new ShellConfig(shell, PowerShellArgs);
    }

    /// <summary>Process environment with the managed bin directory prepended to PATH.</summary>
    public static Dictionary<string, string> GetShellEnv()
    {
        var env = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            env[(string)entry.Key] = (string?)entry.Value ?? "";
        }
        var binDir = AppConfig.BinDir;
        var pathKey = env.Keys.FirstOrDefault(k => string.Equals(k, "PATH", StringComparison.OrdinalIgnoreCase)) ?? "PATH";
        var currentPath = env.GetValueOrDefault(pathKey) ?? "";
        var entries = currentPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        if (!entries.Contains(binDir)) env[pathKey] = string.Join(Path.PathSeparator, new[] { binDir, currentPath }.Where(s => s.Length > 0));
        return env;
    }

    /// <summary>Remove control characters (except tab/newline/CR), lone surrogates and U+FFF9..U+FFFB.</summary>
    public static string SanitizeBinaryOutput(string str)
    {
        var sb = new StringBuilder(str.Length);
        for (var i = 0; i < str.Length; i++)
        {
            var c = str[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < str.Length && char.IsLowSurrogate(str[i + 1]))
                {
                    sb.Append(c).Append(str[i + 1]);
                    i++;
                }
                continue;
            }
            if (char.IsLowSurrogate(c)) continue;
            if (c is '\t' or '\n' or '\r')
            {
                sb.Append(c);
                continue;
            }
            if (c <= 0x1f) continue;
            if (c >= 0xfff9 && c <= 0xfffb) continue;
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static readonly HashSet<int> TrackedDetachedChildPids = [];

    public static void TrackDetachedChildPid(int pid)
    {
        lock (TrackedDetachedChildPids) TrackedDetachedChildPids.Add(pid);
    }

    public static void UntrackDetachedChildPid(int pid)
    {
        lock (TrackedDetachedChildPids) TrackedDetachedChildPids.Remove(pid);
    }

    public static void KillTrackedDetachedChildren()
    {
        int[] pids;
        lock (TrackedDetachedChildPids)
        {
            pids = [.. TrackedDetachedChildPids];
            TrackedDetachedChildPids.Clear();
        }
        foreach (var pid in pids) KillProcessTree(pid);
    }

    /// <summary>Kill a process and all its children.</summary>
    public static void KillProcessTree(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Process already exited.
        }
    }

    /// <summary>Quote an argument for a Windows command line (CommandLineToArgvW rules).</summary>
    public static string QuoteWindowsArgument(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0) return arg;
        var sb = new StringBuilder("\"");
        for (var i = 0; i < arg.Length; i++)
        {
            var backslashes = 0;
            while (i < arg.Length && arg[i] == '\\')
            {
                backslashes++;
                i++;
            }
            if (i == arg.Length)
            {
                sb.Append('\\', backslashes * 2);
                break;
            }
            if (arg[i] == '"')
            {
                sb.Append('\\', backslashes * 2 + 1).Append('"');
            }
            else
            {
                sb.Append('\\', backslashes).Append(arg[i]);
            }
        }
        return sb.Append('"').ToString();
    }
}
