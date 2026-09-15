using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace PiSharp.CodingAgent.Utils;

/// <summary>
/// Child process spawning with cross-spawn semantics on Windows (PATH/PATHEXT resolution and cmd.exe shims for .cmd/.bat
/// files such as npm.cmd). Port of utils/child-process.ts spawnProcess/spawnProcessSync.
/// </summary>
public static partial class ProcessRunner
{
    [GeneratedRegex(@"([()\][%!^""`<>&|;, *?])")]
    private static partial Regex MetaChars();

    [GeneratedRegex(@"node_modules[\\/]\.bin[\\/][^\\/]+\.cmd$", RegexOptions.IgnoreCase)]
    private static partial Regex CmdShim();

    private static string? ResolveWindowsCommand(string command, IDictionary<string, string?> env)
    {
        if (Path.IsPathRooted(command) || command.Contains('/') || command.Contains('\\'))
        {
            if (File.Exists(command)) return command;
            foreach (var ext in PathExtensions(env))
            {
                if (File.Exists(command + ext)) return command + ext;
            }
            return null;
        }
        var pathValue = env.FirstOrDefault(kv => kv.Key.Equals("PATH", StringComparison.OrdinalIgnoreCase)).Value ?? "";
        var dirs = new List<string> { Directory.GetCurrentDirectory() };
        dirs.AddRange(pathValue.Split(';', StringSplitOptions.RemoveEmptyEntries));
        foreach (var dir in dirs)
        {
            foreach (var ext in new[] { "" }.Concat(PathExtensions(env)))
            {
                var candidate = Path.Combine(dir.Trim('"'), command + ext);
                if (File.Exists(candidate) && (ext.Length > 0 || Path.HasExtension(command))) return candidate;
            }
        }
        return null;
    }

    private static IEnumerable<string> PathExtensions(IDictionary<string, string?> env)
    {
        var value = env.FirstOrDefault(kv => kv.Key.Equals("PATHEXT", StringComparison.OrdinalIgnoreCase)).Value;
        return (string.IsNullOrEmpty(value) ? ".COM;.EXE;.BAT;.CMD" : value).Split(';', StringSplitOptions.RemoveEmptyEntries).Select(e => e.ToLowerInvariant());
    }

    private static string EscapeCommand(string arg) => MetaChars().Replace(arg, "^$1");

    private static string EscapeArgument(string arg, bool doubleEscapeMetaChars)
    {
        arg = Regex.Replace(arg, @"(\\*)""", m => m.Groups[1].Value + m.Groups[1].Value + "\\\"");
        arg = Regex.Replace(arg, @"(\\*)$", m => m.Groups[1].Value + m.Groups[1].Value);
        arg = $"\"{arg}\"";
        arg = MetaChars().Replace(arg, "^$1");
        if (doubleEscapeMetaChars) arg = MetaChars().Replace(arg, "^$1");
        return arg;
    }

    private static string QuoteNativeArgument(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0) return arg;
        var sb = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            if (c == '"') sb.Append('\\', backslashes * 2 + 1);
            else sb.Append('\\', backslashes);
            backslashes = 0;
            sb.Append(c);
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }

    public static ProcessStartInfo CreateStartInfo(string command, IReadOnlyList<string> args, string? cwd = null, IReadOnlyDictionary<string, string>? env = null)
    {
        var info = new ProcessStartInfo { UseShellExecute = false, WorkingDirectory = cwd ?? "" };
        if (env is not null)
        {
            foreach (var (key, value) in env) info.Environment[key] = value;
        }
        if (!OperatingSystem.IsWindows())
        {
            info.FileName = command;
            foreach (var arg in args) info.ArgumentList.Add(arg);
            return info;
        }

        var resolved = ResolveWindowsCommand(command, info.Environment) ?? command;
        var extension = Path.GetExtension(resolved).ToLowerInvariant();
        if (extension is ".cmd" or ".bat")
        {
            var doubleEscape = CmdShim().IsMatch(resolved);
            var shellCommand = string.Join(" ", new[] { EscapeCommand(Path.GetFullPath(resolved)) }.Concat(args.Select(a => EscapeArgument(a, doubleEscape))));
            info.FileName = Environment.GetEnvironmentVariable("comspec") ?? "cmd.exe";
            info.Arguments = $"/d /s /c \"{shellCommand}\"";
        }
        else
        {
            info.FileName = resolved;
            info.Arguments = string.Join(" ", args.Select(QuoteNativeArgument));
        }
        return info;
    }

    /// <summary>Run with inherited stdio; throws when the process cannot start or exits non-zero.</summary>
    public static async Task RunAsync(string command, IReadOnlyList<string> args, string? cwd = null, bool stdoutToStderr = false, CancellationToken ct = default)
    {
        var info = CreateStartInfo(command, args, cwd);
        if (stdoutToStderr)
        {
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
        }
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Failed to start {command}");
        if (stdoutToStderr)
        {
            var outTask = process.StandardOutput.BaseStream.CopyToAsync(Console.OpenStandardError(), ct);
            var errTask = process.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError(), ct);
            await Task.WhenAll(outTask, errTask);
        }
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0) throw new InvalidOperationException($"{command} {string.Join(" ", args)} failed with code {process.ExitCode}");
    }

    /// <summary>Run capturing output; returns trimmed stdout. Throws on timeout or a non-zero exit.</summary>
    public static async Task<string> RunCaptureAsync(string command, IReadOnlyList<string> args, string? cwd = null, int? timeoutMs = null, IReadOnlyDictionary<string, string>? env = null)
    {
        var info = CreateStartInfo(command, args, cwd, env);
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.StandardOutputEncoding = Encoding.UTF8;
        info.StandardErrorEncoding = Encoding.UTF8;
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Failed to start {command}");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var cts = timeoutMs is { } ms ? new CancellationTokenSource(ms) : new CancellationTokenSource();
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Already exited.
            }
            throw new TimeoutException($"{command} {string.Join(" ", args)} timed out after {timeoutMs}ms");
        }
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{command} {string.Join(" ", args)} failed with code {process.ExitCode}: {(stderr.Length > 0 ? stderr : stdout)}");
        }
        return stdout.Trim();
    }

    /// <summary>Synchronous capture used for quick queries such as `npm root -g`.</summary>
    public static string RunSync(string command, IReadOnlyList<string> args)
    {
        var info = CreateStartInfo(command, args);
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        try
        {
            using var process = Process.Start(info) ?? throw new InvalidOperationException("process did not start");
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            var stdout = stdoutTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0) throw new InvalidOperationException(stderr.Length > 0 ? stderr : stdout);
            return (stdout.Length > 0 ? stdout : stderr).Trim();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to run {command} {string.Join(" ", args)}: {ex.Message}");
        }
    }
}
