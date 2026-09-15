using System.Diagnostics;
using System.Text.Json.Nodes;
using Iris.Agent;
using Iris.Ai;
using Iris.CodingAgent.Utils;

namespace Iris.CodingAgent.Core.Tools;

public sealed class ShellExecOptions
{
    public required Action<ReadOnlyMemory<byte>> OnData { get; init; }
    public CancellationToken CancellationToken { get; init; }

    /// <summary>Timeout in seconds.</summary>
    public double? Timeout { get; init; }

    public IDictionary<string, string>? Env { get; init; }
}

/// <summary>Pluggable operations for the bash/powershell tools. Exec returns the exit code (null if killed).</summary>
public interface IBashOperations
{
    Task<int?> ExecAsync(string command, string cwd, ShellExecOptions options);
}

public sealed record BashSpawnContext(string Command, string Cwd, Dictionary<string, string> Env);

public sealed class BashToolOptions
{
    public IBashOperations? Operations { get; init; }

    /// <summary>Command prefix prepended to every command (for example shell setup commands).</summary>
    public string? CommandPrefix { get; init; }

    /// <summary>Optional explicit shell path from settings.</summary>
    public string? ShellPath { get; init; }

    /// <summary>Expose current session metadata as PI_* environment variables. Default: true.</summary>
    public bool ExposeSessionEnvironment { get; init; } = true;

    public Func<BashSpawnContext, BashSpawnContext>? SpawnHook { get; init; }
}

public sealed record ShellToolConfig(
    string Name,
    string Label,
    string ShellName,
    string Prompt,
    string PromptSnippet,
    IReadOnlyList<string>? PromptGuidelines,
    string TempFilePrefix);

/// <summary>Local process execution shared by the built-in shell tools.</summary>
public sealed class LocalShellOperations(string shellName, Func<ShellConfig> resolveShellConfig, string commandPrefix = "") : IBashOperations
{
    private const long MaxTimeoutMs = 2_147_483_647;
    private const int ExitStdioGraceMs = 100;

    public static double? ResolveTimeoutMs(double? timeout)
    {
        if (timeout is not { } t) return null;
        if (!double.IsFinite(t) || t <= 0) throw new InvalidOperationException("Invalid timeout: must be a finite number of seconds");
        var timeoutMs = t * 1000;
        if (timeoutMs > MaxTimeoutMs) throw new InvalidOperationException($"Invalid timeout: maximum is {NodeCompat.FormatNumber(MaxTimeoutMs / 1000.0)} seconds");
        return timeoutMs;
    }

    public static LocalShellOperations Bash(string? shellPath = null) => new("bash", () => ShellUtils.GetShellConfig(shellPath));

    public static LocalShellOperations PowerShell() =>
        new("PowerShell", ShellUtils.GetPowerShellConfig, "try { [Console]::OutputEncoding=[System.Text.Encoding]::UTF8 } catch {}\n");

    public async Task<int?> ExecAsync(string command, string cwd, ShellExecOptions options)
    {
        command = commandPrefix + command;
        var ct = options.CancellationToken;
        var timeoutMs = ResolveTimeoutMs(options.Timeout);
        if (ct.IsCancellationRequested) throw new InvalidOperationException("aborted");
        var shellConfig = resolveShellConfig();
        if (!Directory.Exists(cwd) && !File.Exists(cwd))
        {
            throw new InvalidOperationException($"Working directory does not exist: {cwd}\nCannot execute {shellName} commands.");
        }

        var commandFromStdin = shellConfig.CommandTransport == "stdin";
        var psi = new ProcessStartInfo(shellConfig.Shell)
        {
            WorkingDirectory = cwd,
            RedirectStandardInput = commandFromStdin,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in shellConfig.Args) psi.ArgumentList.Add(arg);
        if (!commandFromStdin) psi.ArgumentList.Add(command);
        psi.Environment.Clear();
        foreach (var (key, value) in options.Env ?? ShellUtils.GetShellEnv()) psi.Environment[key] = value;

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"spawn {shellConfig.Shell} failed");
        var pid = process.Id;
        ShellUtils.TrackDetachedChildPid(pid);
        if (commandFromStdin)
        {
            try
            {
                await process.StandardInput.WriteAsync(command);
                process.StandardInput.Close();
            }
            catch (IOException)
            {
            }
        }

        var timedOut = false;
        var dataGate = new object();
        var lastDataAt = Environment.TickCount64;
        using var readCts = new CancellationTokenSource();

        async Task Pump(Stream stream)
        {
            var buffer = new byte[16 * 1024];
            try
            {
                while (true)
                {
                    var read = await stream.ReadAsync(buffer, readCts.Token);
                    if (read == 0) break;
                    lock (dataGate)
                    {
                        lastDataAt = Environment.TickCount64;
                        options.OnData(buffer.AsMemory(0, read).ToArray());
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        var readers = Task.WhenAll(Pump(process.StandardOutput.BaseStream), Pump(process.StandardError.BaseStream));
        using var timeoutTimer = timeoutMs is { } ms
            ? new Timer(_ =>
            {
                timedOut = true;
                ShellUtils.KillProcessTree(pid);
            }, null, (long)ms, Timeout.Infinite)
            : null;
        using var abortRegistration = ct.Register(() => ShellUtils.KillProcessTree(pid));

        try
        {
            await process.WaitForExitAsync(CancellationToken.None);
            // Wait for the pipes to fall idle instead of hanging on handles inherited by detached descendants.
            var exitedAt = Environment.TickCount64;
            while (!readers.IsCompleted)
            {
                long idleSince;
                lock (dataGate) idleSince = Math.Max(lastDataAt, exitedAt);
                var remaining = ExitStdioGraceMs - (Environment.TickCount64 - idleSince);
                if (remaining <= 0) break;
                await Task.WhenAny(readers, Task.Delay((int)remaining));
            }
            readCts.Cancel();

            if (ct.IsCancellationRequested) throw new InvalidOperationException("aborted");
            if (timedOut) throw new InvalidOperationException($"timeout:{NodeCompat.FormatNumber(options.Timeout!.Value)}");
            return process.ExitCode;
        }
        finally
        {
            ShellUtils.UntrackDetachedChildPid(pid);
        }
    }
}

public static class ShellTool
{
    public const int UpdateThrottleMs = 100;

    public static readonly ShellToolConfig BashConfig = new(
        "bash", "bash", "bash", "$",
        "Execute bash commands (ls, grep, find, etc.)",
        ["You can inspect PI_* environment variables for current model and session details."],
        "iris-bash");

    public static readonly ShellToolConfig PowerShellConfig = new(
        "powershell", "powershell", "PowerShell", "PS>",
        "Execute PowerShell commands",
        ["You can inspect PI_* environment variables for current model and session details."],
        "iris-powershell");

    private static readonly JsonObject Schema = ToolSchema.Object([
        ("command", ToolSchema.String("Shell command to execute"), false),
        ("timeout", ToolSchema.Number("Timeout in seconds (optional, no default timeout)"), true),
    ]);

    private static readonly string[] SessionEnvKeys = ["IRIS_SESSION_ID", "IRIS_SESSION_FILE", "IRIS_PROVIDER", "IRIS_MODEL", "IRIS_REASONING_LEVEL"];

    private static BashSpawnContext ResolveSpawnContext(string command, string cwd, BashToolOptions options, ExtensionContext? ctx)
    {
        var env = ShellUtils.GetShellEnv();
        foreach (var key in SessionEnvKeys) env.Remove(key);
        if (options.ExposeSessionEnvironment && ctx is not null)
        {
            if (ctx.SessionManager is { } sessionManager)
            {
                env["IRIS_SESSION_ID"] = sessionManager.SessionId;
                if (!string.IsNullOrEmpty(sessionManager.SessionFile)) env["IRIS_SESSION_FILE"] = sessionManager.SessionFile;
            }
            if (ctx.Model is { } model)
            {
                env["IRIS_PROVIDER"] = model.Provider;
                env["IRIS_MODEL"] = model.Id;
            }
            if (ctx.ThinkingLevel is { } level) env["IRIS_REASONING_LEVEL"] = ThinkingLevels.ToWire(level);
        }
        var baseContext = new BashSpawnContext(command, cwd, env);
        return options.SpawnHook is null ? baseContext : options.SpawnHook(baseContext);
    }

    public static ToolDefinition CreateShellDefinition(string cwd, ShellToolConfig config, BashToolOptions? options = null)
    {
        options ??= new BashToolOptions();
        var ops = options.Operations ?? LocalShellOperations.Bash(options.ShellPath);
        return new ToolDefinition
        {
            Name = config.Name,
            Label = config.Label,
            Description = $"Execute a {config.ShellName} command in the current working directory. Returns stdout and stderr. Output is truncated to last {Truncate.DefaultMaxLines} lines or {Truncate.DefaultMaxBytes / 1024}KB (whichever is hit first). If truncated, full output is saved to a temp file. Optionally provide a timeout in seconds.",
            PromptSnippet = config.PromptSnippet,
            PromptGuidelines = options.ExposeSessionEnvironment && config.PromptGuidelines is not null ? [.. config.PromptGuidelines] : null,
            Parameters = Schema.DeepClone().AsObject(),
            ConstrainedSampling = ToolSchema.PreferJsonSchema,
            Execute = (_, args, ct, onUpdate, ctx) => ExecuteAsync(args, ct, onUpdate, ctx, cwd, config, options, ops),
        };
    }

    private static async Task<AgentToolResult> ExecuteAsync(
        JsonObject args, CancellationToken ct, AgentToolUpdateCallback? onUpdate, ExtensionContext? ctx,
        string cwd, ShellToolConfig config, BashToolOptions options, IBashOperations ops)
    {
        var command = ToolSchema.GetString(args, "command") ?? "";
        var timeout = ToolSchema.GetNumber(args, "timeout");
        var resolvedCommand = string.IsNullOrEmpty(options.CommandPrefix) ? command : $"{options.CommandPrefix}\n{command}";
        var spawnContext = ResolveSpawnContext(resolvedCommand, string.IsNullOrEmpty(ctx?.Cwd) ? cwd : ctx.Cwd, options, ctx);
        var output = new OutputAccumulator(tempFilePrefix: config.TempFilePrefix);
        var gate = new object();
        var acceptingOutput = true;
        Timer? updateTimer = null;
        var updateDirty = false;
        long lastUpdateAt = 0;

        void EmitOutputUpdate()
        {
            if (onUpdate is null || !updateDirty) return;
            updateDirty = false;
            lastUpdateAt = Environment.TickCount64;
            var snapshot = output.Snapshot(persistIfTruncated: true);
            var details = new JsonObject();
            if (snapshot.Truncation.Truncated) details["truncation"] = snapshot.Truncation.ToJson();
            if (snapshot.FullOutputPath is not null) details["fullOutputPath"] = snapshot.FullOutputPath;
            onUpdate(new AgentToolResult { Content = [new TextContent(snapshot.Content)], Details = details });
        }

        void ClearUpdateTimer()
        {
            updateTimer?.Dispose();
            updateTimer = null;
        }

        void ScheduleOutputUpdate()
        {
            if (onUpdate is null) return;
            updateDirty = true;
            var delay = UpdateThrottleMs - (Environment.TickCount64 - lastUpdateAt);
            if (delay <= 0)
            {
                ClearUpdateTimer();
                EmitOutputUpdate();
                return;
            }
            updateTimer ??= new Timer(_ =>
            {
                lock (gate)
                {
                    updateTimer?.Dispose();
                    updateTimer = null;
                    if (acceptingOutput) EmitOutputUpdate();
                }
            }, null, delay, Timeout.Infinite);
        }

        onUpdate?.Invoke(new AgentToolResult { Content = [] });

        void HandleData(ReadOnlyMemory<byte> data)
        {
            lock (gate)
            {
                if (!acceptingOutput) return;
                output.Append(data.Span);
                ScheduleOutputUpdate();
            }
        }

        async Task<OutputSnapshot> FinishOutputAsync()
        {
            OutputSnapshot snapshot;
            lock (gate)
            {
                acceptingOutput = false;
                output.Finish();
                ClearUpdateTimer();
                EmitOutputUpdate();
                snapshot = output.Snapshot(persistIfTruncated: true);
            }
            await output.CloseTempFileAsync();
            return snapshot;
        }

        (string Text, JsonObject? Details) FormatOutput(OutputSnapshot snapshot, string emptyText = "(no output)")
        {
            var truncation = snapshot.Truncation;
            var text = string.IsNullOrEmpty(snapshot.Content) ? emptyText : snapshot.Content;
            JsonObject? details = null;
            if (truncation.Truncated)
            {
                details = new JsonObject { ["truncation"] = truncation.ToJson() };
                if (snapshot.FullOutputPath is not null) details["fullOutputPath"] = snapshot.FullOutputPath;
                var startLine = truncation.TotalLines - truncation.OutputLines + 1;
                var endLine = truncation.TotalLines;
                if (truncation.LastLinePartial)
                {
                    var lastLineSize = Truncate.FormatSize(output.GetLastLineBytes());
                    text += $"\n\n[Showing last {Truncate.FormatSize(truncation.OutputBytes)} of line {endLine} (line is {lastLineSize}). Full output: {snapshot.FullOutputPath}]";
                }
                else if (truncation.TruncatedBy == "lines")
                {
                    text += $"\n\n[Showing lines {startLine}-{endLine} of {truncation.TotalLines}. Full output: {snapshot.FullOutputPath}]";
                }
                else
                {
                    text += $"\n\n[Showing lines {startLine}-{endLine} of {truncation.TotalLines} ({Truncate.FormatSize(Truncate.DefaultMaxBytes)} limit). Full output: {snapshot.FullOutputPath}]";
                }
            }
            return (text, details);
        }

        static string AppendStatus(string text, string status) => $"{(text.Length > 0 ? $"{text}\n\n" : "")}{status}";

        try
        {
            int? exitCode;
            try
            {
                exitCode = await ops.ExecAsync(spawnContext.Command, spawnContext.Cwd, new ShellExecOptions
                {
                    OnData = HandleData,
                    CancellationToken = ct,
                    Timeout = timeout,
                    Env = spawnContext.Env,
                });
            }
            catch (Exception ex)
            {
                var snapshot = await FinishOutputAsync();
                var (text, _) = FormatOutput(snapshot, "");
                if (ex.Message == "aborted") throw new InvalidOperationException(AppendStatus(text, "Command aborted"));
                if (ex.Message.StartsWith("timeout:", StringComparison.Ordinal))
                {
                    var timeoutSecs = ex.Message.Split(':')[1];
                    throw new InvalidOperationException(AppendStatus(text, $"Command timed out after {timeoutSecs} seconds"));
                }
                throw;
            }

            var finalSnapshot = await FinishOutputAsync();
            var (outputText, finalDetails) = FormatOutput(finalSnapshot);
            if (exitCode is { } code && code != 0)
            {
                throw new InvalidOperationException(AppendStatus(outputText, $"Command exited with code {code}"));
            }
            return AgentToolResult.Text(outputText, finalDetails);
        }
        finally
        {
            lock (gate) ClearUpdateTimer();
        }
    }

    public static ToolDefinition CreateBashDefinition(string cwd, BashToolOptions? options = null) =>
        CreateShellDefinition(cwd, BashConfig, options);

    public static ToolDefinition CreatePowerShellDefinition(string cwd, BashToolOptions? options = null)
    {
        options ??= new BashToolOptions();
        return CreateShellDefinition(cwd, PowerShellConfig, new BashToolOptions
        {
            Operations = options.Operations ?? LocalShellOperations.PowerShell(),
            ExposeSessionEnvironment = options.ExposeSessionEnvironment,
            SpawnHook = options.SpawnHook,
        });
    }
}
