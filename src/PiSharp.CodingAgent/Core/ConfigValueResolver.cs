using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using PiSharp.CodingAgent.Utils;

namespace PiSharp.CodingAgent.Core;

/// <summary>
/// Resolve configuration values that may be shell commands ("!cmd"), environment references ("$VAR", "${VAR}") or
/// literals. Port of core/resolve-config-value.ts.
/// </summary>
public static partial class ConfigValueResolver
{
    private static readonly ConcurrentDictionary<string, string?> CommandResultCache = new();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex EnvVarName();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*")]
    private static partial Regex EnvVarNamePrefix();

    private abstract record TemplatePart;

    private sealed record LiteralPart(string Value) : TemplatePart;

    private sealed record EnvPart(string Name) : TemplatePart;

    private static void AppendLiteral(List<TemplatePart> parts, string value)
    {
        if (value.Length == 0) return;
        if (parts.Count > 0 && parts[^1] is LiteralPart previous) parts[^1] = new LiteralPart(previous.Value + value);
        else parts.Add(new LiteralPart(value));
    }

    private static List<TemplatePart> ParseTemplate(string config)
    {
        var parts = new List<TemplatePart>();
        var index = 0;
        while (index < config.Length)
        {
            var dollar = config.IndexOf('$', index);
            if (dollar < 0)
            {
                AppendLiteral(parts, config[index..]);
                break;
            }
            AppendLiteral(parts, config[index..dollar]);
            var next = dollar + 1 < config.Length ? config[dollar + 1] : '\0';
            if (next is '$' or '!')
            {
                AppendLiteral(parts, next.ToString());
                index = dollar + 2;
                continue;
            }
            if (next == '{')
            {
                var end = config.IndexOf('}', dollar + 2);
                if (end < 0)
                {
                    AppendLiteral(parts, "$");
                    index = dollar + 1;
                    continue;
                }
                var name = config[(dollar + 2)..end];
                if (EnvVarName().IsMatch(name)) parts.Add(new EnvPart(name));
                else AppendLiteral(parts, config[dollar..(end + 1)]);
                index = end + 1;
                continue;
            }
            var match = EnvVarNamePrefix().Match(config[(dollar + 1)..]);
            if (match.Success)
            {
                parts.Add(new EnvPart(match.Value));
                index = dollar + 1 + match.Value.Length;
                continue;
            }
            AppendLiteral(parts, "$");
            index = dollar + 1;
        }
        return parts;
    }

    private static string? ResolveEnv(string name, IReadOnlyDictionary<string, string>? env)
    {
        if (env is not null && env.TryGetValue(name, out var scoped) && !string.IsNullOrEmpty(scoped)) return scoped;
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static string? ResolveTemplate(List<TemplatePart> parts, IReadOnlyDictionary<string, string>? env)
    {
        var resolved = "";
        foreach (var part in parts)
        {
            if (part is LiteralPart literal)
            {
                resolved += literal.Value;
                continue;
            }
            var value = ResolveEnv(((EnvPart)part).Name, env);
            if (value is null) return null;
            resolved += value;
        }
        return resolved;
    }

    public static bool IsCommandConfigValue(string config) => config.StartsWith('!');

    public static string? GetConfigValueEnvVarName(string config)
    {
        if (IsCommandConfigValue(config)) return null;
        var parts = ParseTemplate(config);
        return parts is [EnvPart env] ? env.Name : null;
    }

    public static List<string> GetConfigValueEnvVarNames(string config) =>
        IsCommandConfigValue(config) ? [] : ParseTemplate(config).OfType<EnvPart>().Select(p => p.Name).Distinct().ToList();

    public static List<string> GetMissingConfigValueEnvVarNames(string config, IReadOnlyDictionary<string, string>? env = null) =>
        GetConfigValueEnvVarNames(config).Where(name => ResolveEnv(name, env) is null).ToList();

    public static bool IsConfigValueConfigured(string config, IReadOnlyDictionary<string, string>? env = null) =>
        GetMissingConfigValueEnvVarNames(config, env).Count == 0;

    /// <summary>Resolve a config value; "!" commands are executed once and cached.</summary>
    public static string? Resolve(string config, IReadOnlyDictionary<string, string>? env = null) =>
        IsCommandConfigValue(config) ? ExecuteCommand(config) : ResolveTemplate(ParseTemplate(config), env);

    public static string? ResolveUncached(string config, IReadOnlyDictionary<string, string>? env = null) =>
        IsCommandConfigValue(config) ? ExecuteCommandUncached(config) : ResolveTemplate(ParseTemplate(config), env);

    public static string ResolveOrThrow(string config, string description, IReadOnlyDictionary<string, string>? env = null)
    {
        var resolved = ResolveUncached(config, env);
        if (resolved is not null) return resolved;
        if (IsCommandConfigValue(config)) throw new InvalidOperationException($"Failed to resolve {description} from shell command: {config[1..]}");
        var missing = GetMissingConfigValueEnvVarNames(config, env);
        if (missing.Count == 1) throw new InvalidOperationException($"Failed to resolve {description} from environment variable: {missing[0]}");
        if (missing.Count > 1) throw new InvalidOperationException($"Failed to resolve {description} from environment variables: {string.Join(", ", missing)}");
        throw new InvalidOperationException($"Failed to resolve {description}");
    }

    public static Dictionary<string, string>? ResolveHeaders(IReadOnlyDictionary<string, string>? headers, IReadOnlyDictionary<string, string>? env = null)
    {
        if (headers is null) return null;
        var resolved = new Dictionary<string, string>();
        foreach (var (key, value) in headers)
        {
            var v = Resolve(value, env);
            if (!string.IsNullOrEmpty(v)) resolved[key] = v;
        }
        return resolved.Count > 0 ? resolved : null;
    }

    public static Dictionary<string, string>? ResolveHeadersOrThrow(IReadOnlyDictionary<string, string>? headers, string description, IReadOnlyDictionary<string, string>? env = null)
    {
        if (headers is null) return null;
        var resolved = new Dictionary<string, string>();
        foreach (var (key, value) in headers) resolved[key] = ResolveOrThrow(value, $"{description} header \"{key}\"", env);
        return resolved.Count > 0 ? resolved : null;
    }

    public static void ClearCache() => CommandResultCache.Clear();

    private static string? ExecuteCommand(string config) => CommandResultCache.GetOrAdd(config, ExecuteCommandUncached);

    private static string? ExecuteCommandUncached(string config)
    {
        var command = config[1..];
        if (OperatingSystem.IsWindows())
        {
            var (executed, value) = ExecuteWithConfiguredShell(command);
            return executed ? value : ExecuteWithDefaultShell(command);
        }
        return ExecuteWithDefaultShell(command);
    }

    private static (bool Executed, string? Value) ExecuteWithConfiguredShell(string command)
    {
        ShellConfig shell;
        try
        {
            shell = ShellUtils.GetShellConfig();
        }
        catch
        {
            return (false, null);
        }
        var fromStdin = shell.CommandTransport == "stdin";
        var args = fromStdin ? shell.Args : [.. shell.Args, command];
        return RunProcess(shell.Shell, args, fromStdin ? command : null);
    }

    private static string? ExecuteWithDefaultShell(string command)
    {
        var (_, value) = OperatingSystem.IsWindows()
            ? RunProcess(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", ["/d", "/s", "/c", command], null)
            : RunProcess("/bin/sh", ["-c", command], null);
        return value;
    }

    private static (bool Executed, string? Value) RunProcess(string file, IReadOnlyList<string> args, string? stdin)
    {
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = stdin is not null,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in args) psi.ArgumentList.Add(arg);
            using var process = Process.Start(psi);
            if (process is null) return (false, null);
            if (stdin is not null)
            {
                process.StandardInput.Write(stdin);
                process.StandardInput.Close();
            }
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(10_000))
            {
                try
                {
                    process.Kill(true);
                }
                catch
                {
                }
                return (true, null);
            }
            if (process.ExitCode != 0) return (true, null);
            var value = stdoutTask.GetAwaiter().GetResult().Trim();
            return (true, value.Length > 0 ? value : null);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (false, null);
        }
        catch
        {
            return (false, null);
        }
    }
}
