using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Iris.WebAccess;

internal sealed class CredentialResolutionException(string provider, string category)
    : Exception($"{provider} credential resolution failed: {category}")
{
    public string Provider { get; } = provider;
    public string Category { get; } = category;
}

/// <summary>
/// API keys from web-search.json or the environment. A configured value may be a literal, "$NAME" / "${NAME}" (an
/// environment variable), "!command" (the command's trimmed stdout, e.g. a password manager CLI), or "$$..." / "$!..."
/// to escape a literal starting with "$" or "!". A plain environment variable wins over a literal configured value.
/// </summary>
internal static partial class Credentials
{
    private const int CommandTimeoutMs = 5_000;
    private const int MaxCredentialBytes = 16_384;

    private static readonly string[] CommandEnvironmentNames =
    [
        "HOME", "USER", "LOGNAME", "OP_SERVICE_ACCOUNT_TOKEN", "PATH", "LANG", "LC_ALL", "LC_CTYPE", "TERM", "TMPDIR",
        "XDG_CONFIG_HOME", "XDG_RUNTIME_DIR", "DBUS_SESSION_BUS_ADDRESS", "SSH_AUTH_SOCK", "WSL_DISTRO_NAME", "WSL_INTEROP",
        // Windows needs these for most command line tools to start.
        "SystemRoot", "windir", "ComSpec", "PATHEXT", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "TEMP", "TMP", "ProgramData",
        "ProgramFiles", "ProgramFiles(x86)",
    ];

    [GeneratedRegex(@"^\$(?:([A-Za-z_][A-Za-z0-9_]*)|\{([A-Za-z_][A-Za-z0-9_]*)\})$")]
    private static partial Regex EnvironmentSource();

    [GeneratedRegex(@"^OP_SESSION_[A-Za-z0-9_]+$")]
    private static partial Regex OnePasswordSession();

    public static string Redact(string text, string? credential) =>
        string.IsNullOrEmpty(credential) ? text : text.Replace(credential, "[redacted]", StringComparison.Ordinal);

    public static bool HasSource(string configKey, string environmentKey)
    {
        var source = Configured(configKey);
        if (source is not null) return true;
        return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(environmentKey));
    }

    public static async Task<string?> ResolveAsync(string provider, string configKey, string environmentKey, CancellationToken cancellationToken)
    {
        var source = Configured(configKey);
        if (source is not null && (source.StartsWith("$$", StringComparison.Ordinal) || source.StartsWith("$!", StringComparison.Ordinal))) return source[1..];
        if (source is not null && source.StartsWith('!')) return await RunCommandAsync(provider, source[1..].Trim(), cancellationToken);
        if (source is not null && source.StartsWith('$'))
        {
            var match = EnvironmentSource().Match(source);
            if (!match.Success) throw new CredentialResolutionException(provider, "invalid-source");
            var name = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            var value = Environment.GetEnvironmentVariable(name)?.Trim();
            return string.IsNullOrEmpty(value) ? throw new CredentialResolutionException(provider, "environment-empty") : value;
        }
        var fromEnvironment = Environment.GetEnvironmentVariable(environmentKey)?.Trim();
        return string.IsNullOrEmpty(fromEnvironment) ? source : fromEnvironment;
    }

    private static string? Configured(string configKey) =>
        WebConfig.Get(configKey) is JsonValue value && value.GetValueKind() == JsonValueKind.String && value.GetValue<string>().Trim() is { Length: > 0 } text ? text : null;

    private static async Task<string> RunCommandAsync(string provider, string command, CancellationToken cancellationToken)
    {
        if (command.Length == 0) throw new CredentialResolutionException(provider, "invalid-source");
        var startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe") { ArgumentList = { "/d", "/s", "/c", command } }
            : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", command } };
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.Environment.Clear();
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var name = (string)entry.Key;
            if (CommandEnvironmentNames.Contains(name, StringComparer.OrdinalIgnoreCase) || OnePasswordSession().IsMatch(name)) startInfo.Environment[name] = entry.Value as string;
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch
        {
            throw new CredentialResolutionException(provider, "command-failed");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommandTimeoutMs);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        var buffer = new char[MaxCredentialBytes + 1];
        var output = new StringBuilder();
        try
        {
            int read;
            while ((read = await process.StandardOutput.ReadAsync(buffer, timeout.Token)) > 0)
            {
                output.Append(buffer, 0, read);
                if (Encoding.UTF8.GetByteCount(output.ToString()) > MaxCredentialBytes)
                {
                    Kill(process);
                    throw new CredentialResolutionException(provider, "command-output-too-large");
                }
            }
            await process.WaitForExitAsync(timeout.Token);
            await stderr;
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw new CredentialResolutionException(provider, cancellationToken.IsCancellationRequested ? "command-aborted" : "command-timeout");
        }

        if (process.ExitCode != 0) throw new CredentialResolutionException(provider, "command-failed");
        var value = output.ToString().Trim();
        if (value.Length == 0) throw new CredentialResolutionException(provider, "command-empty");
        if (value.Any(c => c < 0x20 || c == 0x7f)) throw new CredentialResolutionException(provider, "command-invalid-output");
        return value;
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Already exited.
        }
    }
}
