using PiSharp.Ai.Models;
using PiSharp.CodingAgent.Core.Extensions;
using PiSharp.CodingAgent.Core.Tools;
using PiSharp.CodingAgent.Utils;

namespace PiSharp.CodingAgent.Core;

/// <summary>Type is "info" | "warning" | "error".</summary>
public sealed record AgentSessionRuntimeDiagnostic(string Type, string Message);

/// <summary>cwd-bound runtime services for one effective session cwd.</summary>
public sealed record AgentSessionServices(
    string Cwd,
    string AgentDir,
    ModelRuntime ModelRuntime,
    SettingsManager SettingsManager,
    IResourceLoader ResourceLoader,
    List<AgentSessionRuntimeDiagnostic> Diagnostics);

public sealed record CreateAgentSessionRuntimeResult(AgentSession Session, AgentSessionServices Services, List<AgentSessionRuntimeDiagnostic> Diagnostics, string? ModelFallbackMessage);

public sealed record RuntimeTarget(string Cwd, string AgentDir, SessionManager SessionManager, string? SessionStartReason = null, string? PreviousSessionFile = null);

public delegate Task<CreateAgentSessionRuntimeResult> CreateAgentSessionRuntimeFactory(RuntimeTarget target);

public sealed class SessionImportFileNotFoundException(string filePath) : FileNotFoundException($"File not found: {filePath}", filePath);

/// <summary>Missing stored session cwd. Port of core/session-cwd.ts.</summary>
public sealed record SessionCwdIssue(string? SessionFile, string SessionCwd, string FallbackCwd)
{
    public static SessionCwdIssue? Find(SessionManager sessionManager, string fallbackCwd)
    {
        var sessionFile = sessionManager.SessionFile;
        if (string.IsNullOrEmpty(sessionFile)) return null;
        var sessionCwd = sessionManager.Cwd;
        if (string.IsNullOrEmpty(sessionCwd) || Directory.Exists(sessionCwd) || File.Exists(sessionCwd)) return null;
        return new SessionCwdIssue(sessionFile, sessionCwd, fallbackCwd);
    }

    public string ErrorMessage =>
        $"Stored session working directory does not exist: {SessionCwd}{(SessionFile is null ? "" : $"\nSession file: {SessionFile}")}\nCurrent working directory: {FallbackCwd}";

    public string PromptMessage => $"cwd from session file does not exist\n{SessionCwd}\n\ncontinue in current cwd\n{FallbackCwd}";
}

public sealed class MissingSessionCwdException(SessionCwdIssue issue) : InvalidOperationException(issue.ErrorMessage)
{
    public SessionCwdIssue Issue { get; } = issue;

    public static void AssertExists(SessionManager sessionManager, string fallbackCwd)
    {
        if (SessionCwdIssue.Find(sessionManager, fallbackCwd) is { } issue) throw new MissingSessionCwdException(issue);
    }
}

/// <summary>Port of core/agent-session-services.ts.</summary>
public static class AgentSessionServicesFactory
{
    public sealed class Options
    {
        public required string Cwd { get; init; }
        public string? AgentDir { get; init; }
        public SettingsManager? SettingsManager { get; init; }
        public ModelRuntime? ModelRuntime { get; init; }
        public IReadOnlyDictionary<string, object>? ExtensionFlagValues { get; init; }
        public Func<string, string, SettingsManager, DefaultResourceLoaderOptions>? ResourceLoaderOptions { get; init; }
    }

    public static async Task<AgentSessionServices> CreateAsync(Options options)
    {
        var cwd = PathUtils.ResolvePath(options.Cwd);
        var agentDir = options.AgentDir is not null ? PathUtils.ResolvePath(options.AgentDir) : Config.AppConfig.AgentDir;
        var modelRuntime = options.ModelRuntime ?? await ModelRuntime.CreateAsync(new CreateModelRuntimeOptions
        {
            AuthPath = Path.Combine(agentDir, "auth.json"),
            ModelsPath = Path.Combine(agentDir, "models.json"),
        });
        global::PiSharp.CodingAgent.Extensions.Llama.LlamaProvider.EnsureRegistered(modelRuntime);
        var settingsManager = options.SettingsManager ?? SettingsManager.Create(cwd, agentDir);
        var loaderOptions = options.ResourceLoaderOptions?.Invoke(cwd, agentDir, settingsManager)
            ?? new DefaultResourceLoaderOptions { Cwd = cwd, AgentDir = agentDir, SettingsManager = settingsManager };
        var resourceLoader = new DefaultResourceLoader(loaderOptions);
        await resourceLoader.ReloadAsync();

        var diagnostics = new List<AgentSessionRuntimeDiagnostic>();
        await modelRuntime.RefreshAsync(new ModelsRefreshOptions { AllowNetwork = false });

        // Extension flags are registered by extensions; without an extension runtime every flag is unknown.
        if (options.ExtensionFlagValues is { Count: > 0 } flags)
        {
            var names = flags.Keys.Select(n => $"--{n}").ToList();
            diagnostics.Add(new AgentSessionRuntimeDiagnostic("error", $"Unknown option{(names.Count == 1 ? "" : "s")}: {string.Join(", ", names)}"));
        }

        foreach (var entry in resourceLoader.ExtensionEntries)
        {
            diagnostics.Add(new AgentSessionRuntimeDiagnostic("warning", $"Extension \"{entry.Path}\" was not loaded: extensions are not supported by PiSharp yet"));
        }

        return new AgentSessionServices(cwd, agentDir, modelRuntime, settingsManager, resourceLoader, diagnostics);
    }
}

/// <summary>
/// Owns the current AgentSession plus its cwd-bound services, and replaces them for /new, /resume, /fork and import.
/// Port of core/agent-session-runtime.ts.
/// </summary>
public sealed class AgentSessionRuntime
{
    private Func<AgentSession, Task>? _rebindSession;
    private Action? _beforeSessionInvalidate;
    private readonly CreateAgentSessionRuntimeFactory _createRuntime;

    public AgentSessionRuntime(AgentSession session, AgentSessionServices services, CreateAgentSessionRuntimeFactory createRuntime, List<AgentSessionRuntimeDiagnostic>? diagnostics = null, string? modelFallbackMessage = null)
    {
        Session = session;
        Services = services;
        _createRuntime = createRuntime;
        Diagnostics = diagnostics ?? [];
        ModelFallbackMessage = modelFallbackMessage;
    }

    public AgentSession Session { get; private set; }
    public AgentSessionServices Services { get; private set; }
    public string Cwd => Services.Cwd;
    public List<AgentSessionRuntimeDiagnostic> Diagnostics { get; private set; }
    public string? ModelFallbackMessage { get; private set; }

    public void SetRebindSession(Func<AgentSession, Task>? rebind) => _rebindSession = rebind;

    public void SetBeforeSessionInvalidate(Action? callback) => _beforeSessionInvalidate = callback;

    public static async Task<AgentSessionRuntime> CreateAsync(CreateAgentSessionRuntimeFactory createRuntime, RuntimeTarget target)
    {
        MissingSessionCwdException.AssertExists(target.SessionManager, target.Cwd);
        var result = await createRuntime(target);
        return new AgentSessionRuntime(result.Session, result.Services, createRuntime, result.Diagnostics, result.ModelFallbackMessage);
    }

    private static bool IsCancelled(object? hookResult) =>
        hookResult is IReadOnlyDictionary<string, object?> dict && dict.GetValueOrDefault("cancel") is true;

    private async Task<bool> EmitBeforeSwitchAsync(string reason, string? targetSessionFile = null)
    {
        var runner = Session.ExtensionRunner;
        if (!runner.HasHandlers("session_before_switch")) return false;
        return IsCancelled(await runner.EmitAsync(ExtensionEvent.Of("session_before_switch", ("reason", reason), ("targetSessionFile", targetSessionFile))));
    }

    private async Task<bool> EmitBeforeForkAsync(string entryId, string position)
    {
        var runner = Session.ExtensionRunner;
        if (!runner.HasHandlers("session_before_fork")) return false;
        return IsCancelled(await runner.EmitAsync(ExtensionEvent.Of("session_before_fork", ("entryId", entryId), ("position", position))));
    }

    private async Task TeardownCurrentAsync(string reason, string? targetSessionFile = null)
    {
        // Settle the active response first so the aborted turn is persisted to the outgoing session.
        await Session.AbortAsync();
        await Session.ExtensionRunner.EmitAsync(ExtensionEvent.Of("session_shutdown", ("reason", reason), ("targetSessionFile", targetSessionFile)));
        _beforeSessionInvalidate?.Invoke();
        Session.Dispose();
    }

    private void Apply(CreateAgentSessionRuntimeResult result)
    {
        Session = result.Session;
        Services = result.Services;
        Diagnostics = result.Diagnostics;
        ModelFallbackMessage = result.ModelFallbackMessage;
    }

    private async Task FinishSessionReplacementAsync()
    {
        if (_rebindSession is not null) await _rebindSession(Session);
    }

    public async Task<bool> SwitchSessionAsync(string sessionPath, string? cwdOverride = null)
    {
        if (await EmitBeforeSwitchAsync("resume", sessionPath)) return false;
        var previousSessionFile = Session.SessionFile;
        var sessionManager = SessionManager.Open(sessionPath, null, cwdOverride);
        MissingSessionCwdException.AssertExists(sessionManager, Cwd);
        await TeardownCurrentAsync("resume", sessionManager.SessionFile);
        Apply(await _createRuntime(new RuntimeTarget(sessionManager.Cwd, Services.AgentDir, sessionManager, "resume", previousSessionFile)));
        await FinishSessionReplacementAsync();
        return true;
    }

    public async Task<bool> NewSessionAsync(string? parentSession = null, Func<SessionManager, Task>? setup = null)
    {
        if (await EmitBeforeSwitchAsync("new")) return false;
        var previousSessionFile = Session.SessionFile;
        var sessionManager = Session.SessionManager.IsPersisted ? SessionManager.Create(Cwd, Session.SessionManager.SessionDir) : SessionManager.InMemory(Cwd);
        if (parentSession is not null) sessionManager.NewSession(new NewSessionOptions { ParentSession = parentSession });

        await TeardownCurrentAsync("new", sessionManager.SessionFile);
        Apply(await _createRuntime(new RuntimeTarget(Cwd, Services.AgentDir, sessionManager, "new", previousSessionFile)));
        if (setup is not null)
        {
            await setup(Session.SessionManager);
            Session.Agent.State.Messages = Session.SessionManager.BuildSessionContext().Messages;
        }
        await FinishSessionReplacementAsync();
        return true;
    }

    /// <summary>Fork at an entry. position "before" forks before a user message (returning its text); "at" includes the entry.</summary>
    public async Task<(bool Cancelled, string? SelectedText)> ForkAsync(string entryId, string position = "before")
    {
        if (await EmitBeforeForkAsync(entryId, position)) return (true, null);

        var selectedEntry = Session.SessionManager.GetEntry(entryId) ?? throw new InvalidOperationException("Invalid entry ID for forking");
        string? targetLeafId;
        string? selectedText = null;
        if (position == "at")
        {
            targetLeafId = selectedEntry.Id;
        }
        else
        {
            if (selectedEntry is not SessionMessageEntry { Message: PiSharp.Ai.UserMessage user }) throw new InvalidOperationException("Invalid entry ID for forking");
            targetLeafId = selectedEntry.ParentId;
            selectedText = user.Content.Text ?? string.Concat((user.Content.Blocks ?? []).OfType<PiSharp.Ai.TextContent>().Select(t => t.Text));
        }

        var previousSessionFile = Session.SessionFile;
        if (Session.SessionManager.IsPersisted)
        {
            var currentSessionFile = Session.SessionFile ?? throw new InvalidOperationException("Persisted session is missing a session file");
            var sessionDir = Session.SessionManager.SessionDir;
            if (targetLeafId is null)
            {
                var fresh = SessionManager.Create(Cwd, sessionDir);
                fresh.NewSession(new NewSessionOptions { ParentSession = currentSessionFile });
                await TeardownCurrentAsync("fork", fresh.SessionFile);
                Apply(await _createRuntime(new RuntimeTarget(Cwd, Services.AgentDir, fresh, "fork", previousSessionFile)));
                await FinishSessionReplacementAsync();
                return (false, selectedText);
            }

            if (!File.Exists(currentSessionFile))
            {
                throw new InvalidOperationException("This session has not been saved yet. Wait for the first assistant response before cloning or forking it.");
            }
            var sessionManager = SessionManager.Open(currentSessionFile, sessionDir);
            if (sessionManager.CreateBranchedSession(targetLeafId) is null) throw new InvalidOperationException("Failed to create forked session");
            await TeardownCurrentAsync("fork", sessionManager.SessionFile);
            Apply(await _createRuntime(new RuntimeTarget(sessionManager.Cwd, Services.AgentDir, sessionManager, "fork", previousSessionFile)));
            await FinishSessionReplacementAsync();
            return (false, selectedText);
        }

        var inMemory = Session.SessionManager;
        await TeardownCurrentAsync("fork", inMemory.SessionFile);
        if (targetLeafId is null) inMemory.NewSession(new NewSessionOptions { ParentSession = previousSessionFile });
        else inMemory.CreateBranchedSession(targetLeafId);
        Apply(await _createRuntime(new RuntimeTarget(Cwd, Services.AgentDir, inMemory, "fork", previousSessionFile)));
        await FinishSessionReplacementAsync();
        return (false, selectedText);
    }

    /// <summary>Copy a session JSONL file into the session directory and switch to it.</summary>
    public async Task<bool> ImportFromJsonlAsync(string inputPath, string? cwdOverride = null)
    {
        var resolvedPath = PathUtils.ResolvePath(inputPath);
        if (!File.Exists(resolvedPath)) throw new SessionImportFileNotFoundException(resolvedPath);

        var sessionDir = Session.SessionManager.SessionDir;
        Directory.CreateDirectory(sessionDir);
        var destinationPath = Path.Combine(sessionDir, Path.GetFileName(resolvedPath));
        var alreadyStored = Path.GetFullPath(destinationPath) == resolvedPath;
        if (!alreadyStored)
        {
            var name = Path.GetFileNameWithoutExtension(destinationPath);
            var ext = Path.GetExtension(destinationPath);
            var suffix = 1;
            while (File.Exists(destinationPath)) destinationPath = Path.Combine(sessionDir, $"{name}-{suffix++}{ext}");
        }
        if (await EmitBeforeSwitchAsync("resume", destinationPath)) return false;

        var previousSessionFile = Session.SessionFile;
        if (!alreadyStored) File.Copy(resolvedPath, destinationPath, overwrite: false);

        var sessionManager = SessionManager.Open(destinationPath, sessionDir, cwdOverride);
        MissingSessionCwdException.AssertExists(sessionManager, Cwd);
        await TeardownCurrentAsync("resume", sessionManager.SessionFile);
        Apply(await _createRuntime(new RuntimeTarget(sessionManager.Cwd, Services.AgentDir, sessionManager, "resume", previousSessionFile)));
        await FinishSessionReplacementAsync();
        return true;
    }

    public async Task DisposeAsync()
    {
        await Session.ExtensionRunner.EmitAsync(ExtensionEvent.Of("session_shutdown", ("reason", "quit")));
        _beforeSessionInvalidate?.Invoke();
        Session.Dispose();
    }
}
