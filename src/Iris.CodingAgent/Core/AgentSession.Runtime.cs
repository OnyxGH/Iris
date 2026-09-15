using Iris.Extensions;
using Iris.Agent;
using Iris.Ai;
using Iris.Ai.Utils;
using Iris.CodingAgent.Core.Compaction;
using Iris.CodingAgent.Core.Extensions;
using Iris.CodingAgent.Core.Tools;

namespace Iris.CodingAgent.Core;

public sealed partial class AgentSession
{
    /// <summary>Context handed to tools and extension handlers.</summary>
    public ExtensionContext CreateExtensionContext() => new()
    {
        Cwd = _cwd,
        SessionManager = SessionManager,
        ModelRegistry = new ModelRegistry(_modelRuntime),
        GetModel = () => Model,
        GetScopedModels = () => _scopedModels,
        GetThinkingLevel = () => ThinkingLevel,
        IsIdle = () => IsIdle,
        IsProjectTrusted = () => SettingsManager.IsProjectTrusted,
        GetSignal = () => Agent.Signal,
        Abort = () => _ = AbortAsync(),
        HasPendingMessages = () => PendingMessageCount > 0,
        GetSystemPrompt = () => SystemPrompt,
    };

    /// <summary>Bind an error listener and emit session_start / resources_discover to extensions.</summary>
    public async Task BindExtensionsAsync(Action<ExtensionError>? onError = null)
    {
        if (onError is not null) _extensionErrorListener = onError;
        ApplyExtensionBindings(_extensionRunner);
        await _extensionRunner.EmitAsync(RunnerEvent.Of("session_start", ("reason", _config.SessionStartReason)));
        await ExtendResourcesFromExtensionsAsync(_config.SessionStartReason == "reload" ? "reload" : "startup");
    }

    private void ApplyExtensionBindings(IExtensionRunner runner)
    {
        _extensionErrorSubscription?.Dispose();
        _extensionErrorSubscription = _extensionErrorListener is null ? null : runner.OnError(_extensionErrorListener);
    }

    private async Task ExtendResourcesFromExtensionsAsync(string reason)
    {
        if (!_extensionRunner.HasHandlers("resources_discover")) return;
        var discovered = await _extensionRunner.EmitResourcesDiscoverAsync(_cwd, reason);
        if (discovered.SkillPaths.Count == 0 && discovered.PromptPaths.Count == 0 && discovered.ThemePaths.Count == 0) return;

        List<(string, PathMetadata)> Build(IEnumerable<DiscoveredResourcePath> entries) => entries.Select(e =>
        {
            var source = e.ExtensionPath.StartsWith('<')
                ? $"extension:{e.ExtensionPath.Replace("<", "").Replace(">", "")}"
                : $"extension:{System.Text.RegularExpressions.Regex.Replace(Path.GetFileName(e.ExtensionPath), @"\.(ts|js)$", "")}";
            var baseDir = e.ExtensionPath.StartsWith('<') ? null : Path.GetDirectoryName(e.ExtensionPath);
            return (e.Path, new PathMetadata(source, "temporary", "top-level", baseDir));
        }).ToList();

        _resourceLoader.ExtendResources(new ResourceExtensionPaths(Build(discovered.SkillPaths), Build(discovered.PromptPaths), Build(discovered.ThemePaths)));
        _baseSystemPrompt = RebuildSystemPrompt(GetActiveToolNames());
        Agent.State.SystemPrompt = _baseSystemPrompt;
    }

    private void RefreshCurrentModelFromRegistry()
    {
        if (Model is not { } current) return;
        var refreshed = _modelRuntime.GetModel(current.Provider, current.Id);
        if (refreshed is not null && !ReferenceEquals(refreshed, current)) Agent.State.Model = refreshed;
    }

    public void RegisterProvider(string providerId, ProviderConfigInput config)
    {
        _modelRuntime.RegisterProvider(providerId, config);
        RefreshCurrentModelFromRegistry();
    }

    public void UnregisterProvider(string providerId)
    {
        _modelRuntime.UnregisterProvider(providerId);
        RefreshCurrentModelFromRegistry();
    }

    private bool IsAllowedTool(string name) =>
        (_allowedToolNames is null || _allowedToolNames.Contains(name)) && _excludedToolNames?.Contains(name) != true;

    /// <summary>Rebuild the definition-first tool registry (built-in, extension and SDK tools) and active tool set.</summary>
    public void RefreshToolRegistry(IEnumerable<string>? activeToolNames = null, bool includeAllExtensionTools = false)
    {
        var previousRegistryNames = _toolRegistry.Keys.ToHashSet();
        var previousActiveToolNames = GetActiveToolNames();

        var allCustomTools = _extensionRunner.GetAllRegisteredTools()
            .Concat(_customTools.Select(d => new RegisteredTool(d, SourceInfo.Synthetic($"<sdk:{d.Name}>", "sdk"))))
            .Where(t => IsAllowedTool(t.Definition.Name))
            .ToList();

        var builtIn = _baseToolDefinitions.Values
            .Where(d => IsAllowedTool(d.Name))
            .Select(d => new RegisteredTool(d, SourceInfo.Synthetic($"<builtin:{d.Name}>", "builtin")))
            .ToList();

        var definitionRegistry = builtIn.ToDictionary(t => t.Definition.Name, t => new ToolDefinitionEntry(t.Definition, t.SourceInfo));
        foreach (var tool in allCustomTools) definitionRegistry[tool.Definition.Name] = new ToolDefinitionEntry(tool.Definition, tool.SourceInfo);
        _toolDefinitions = definitionRegistry;

        _toolPromptSnippets = [];
        _toolPromptGuidelines = [];
        foreach (var (name, entry) in definitionRegistry)
        {
            if (NormalizePromptSnippet(entry.Definition.PromptSnippet) is { } snippet) _toolPromptSnippets[name] = snippet;
            var guidelines = NormalizePromptGuidelines(entry.Definition.PromptGuidelines);
            if (guidelines.Count > 0) _toolPromptGuidelines[name] = guidelines;
        }

        var wrappedExtensionTools = _extensionRunner.WrapTools(allCustomTools);
        var registry = _extensionRunner.WrapTools(builtIn).ToDictionary(t => t.Name);
        foreach (var tool in wrappedExtensionTools) registry[tool.Name] = tool;
        _toolRegistry = registry;

        var nextActive = (activeToolNames?.ToList() ?? previousActiveToolNames).Where(IsAllowedTool).ToList();
        if (_allowedToolNames is not null)
        {
            nextActive.AddRange(_toolRegistry.Keys.Where(_allowedToolNames.Contains));
        }
        else if (includeAllExtensionTools)
        {
            nextActive.AddRange(wrappedExtensionTools.Select(t => t.Name));
        }
        else if (activeToolNames is null)
        {
            nextActive.AddRange(_toolRegistry.Keys.Where(name => !previousRegistryNames.Contains(name)));
        }

        SetActiveToolsByName(nextActive.Distinct());
    }

    private void BuildRuntime(IEnumerable<string>? activeToolNames, bool includeAllExtensionTools)
    {
        _baseToolDefinitions = _config.BaseToolsOverride is { } overrides
            ? overrides.ToDictionary(kv => kv.Key, kv => ToolDefinition.FromAgentTool(kv.Value))
            : BuiltinTools.CreateAllToolDefinitions(_cwd, new ToolsOptions
            {
                Read = new ReadToolOptions { AutoResizeImages = SettingsManager.ImageAutoResize },
                Bash = new BashToolOptions { CommandPrefix = SettingsManager.ShellCommandPrefix, ShellPath = SettingsManager.ShellPath },
            });

        var loaded = _resourceLoader.Extensions;
        _extensionRunner = _config.ExtensionRunnerFactory?.Invoke(this)
            ?? (loaded.Extensions.Count > 0 ? new ExtensionRunner(loaded.Extensions, loaded.Runtime, this) : new NullExtensionRunner(CreateExtensionContext));
        if (_config.ExtensionRunnerRef is not null) _config.ExtensionRunnerRef.Current = _extensionRunner;
        ApplyExtensionBindings(_extensionRunner);

        var defaults = _config.BaseToolsOverride is { } o ? o.Keys.ToList() : [.. DefaultActiveToolNames];
        RefreshToolRegistry(activeToolNames ?? defaults, includeAllExtensionTools);
    }

    /// <summary>Reload settings, resources and the extension runtime, keeping the active tool set.</summary>
    public async Task ReloadAsync()
    {
        var oldRunner = _extensionRunner;
        await oldRunner.EmitAsync(RunnerEvent.Of("session_shutdown", ("reason", "reload")));
        oldRunner.Invalidate();
        await SettingsManager.ReloadAsync();
        SyncQueueModesFromSettings();
        await _resourceLoader.ReloadAsync();
        BuildRuntime(GetActiveToolNames(), includeAllExtensionTools: true);

        if (_extensionErrorListener is not null)
        {
            await _extensionRunner.EmitAsync(RunnerEvent.Of("session_start", ("reason", "reload")));
            await ExtendResourcesFromExtensionsAsync("reload");
        }
    }

    // =========================================================================
    // Bash execution
    // =========================================================================

    /// <summary>Execute a user bash command ("!" prefix) and record it; excludeFromContext hides it from the LLM ("!!").</summary>
    public async Task<BashResult> ExecuteBashAsync(string command, Action<string>? onChunk = null, bool excludeFromContext = false, string? id = null, IBashOperations? operations = null)
    {
        var cts = new CancellationTokenSource();
        lock (_bashCts) _bashCts.Add(cts);
        var prefix = SettingsManager.ShellCommandPrefix;
        var resolvedCommand = string.IsNullOrEmpty(prefix) ? command : $"{prefix}\n{command}";
        try
        {
            var result = await BashExecutor.ExecuteWithOperationsAsync(
                resolvedCommand,
                SessionManager.Cwd,
                operations ?? LocalShellOperations.Bash(SettingsManager.ShellPath),
                delta =>
                {
                    onChunk?.Invoke(delta);
                    Emit(new BashExecutionUpdateEvent(id, delta));
                },
                cts.Token);
            RecordBashResult(command, result, excludeFromContext);
            return result;
        }
        finally
        {
            lock (_bashCts) _bashCts.Remove(cts);
            cts.Dispose();
        }
    }

    /// <summary>Record a bash result in history (deferred while streaming to keep tool call/result ordering intact).</summary>
    public void RecordBashResult(string command, BashResult result, bool excludeFromContext = false)
    {
        var message = new BashExecutionMessage
        {
            Command = command,
            Output = result.Output,
            ExitCode = result.ExitCode,
            Cancelled = result.Cancelled,
            Truncated = result.Truncated,
            FullOutputPath = result.FullOutputPath,
            Timestamp = TimeUtil.NowMs(),
            ExcludeFromContext = excludeFromContext ? true : null,
        };
        if (IsStreaming)
        {
            _pendingBashMessages.Add(message);
        }
        else
        {
            Agent.State.Messages.Add(message);
            SessionManager.AppendMessage(message);
        }
    }

    public void AbortBash()
    {
        CancellationTokenSource[] all;
        lock (_bashCts) all = [.. _bashCts];
        foreach (var cts in all) TryCancel(cts);
    }

    public bool IsBashRunning
    {
        get
        {
            lock (_bashCts) return _bashCts.Count > 0;
        }
    }

    public bool HasPendingBashMessages => _pendingBashMessages.Count > 0;

    private void FlushPendingBashMessages()
    {
        if (_pendingBashMessages.Count == 0) return;
        foreach (var message in _pendingBashMessages)
        {
            Agent.State.Messages.Add(message);
            SessionManager.AppendMessage(message);
        }
        _pendingBashMessages = [];
    }

    // =========================================================================
    // Session management and tree navigation
    // =========================================================================

    public void SetSessionName(string name)
    {
        SessionManager.AppendSessionInfo(name);
        var sessionName = SessionManager.SessionName;
        Emit(new SessionInfoChangedEvent(sessionName));
        _ = _extensionRunner.EmitAsync(RunnerEvent.Of("session_info_changed", ("name", sessionName)));
    }

    /// <summary>Navigate to another node in the session tree, optionally summarizing the abandoned branch.</summary>
    public async Task<NavigateTreeResult> NavigateTreeAsync(string targetId, bool summarize = false, string? customInstructions = null, bool? replaceInstructions = null, string? label = null)
    {
        if (IsStreaming) throw new InvalidOperationException("Wait for the current response to finish before navigating the session tree.");
        if (IsCompacting) throw new InvalidOperationException("Wait for the current compaction or tree navigation to finish before navigating the session tree.");

        var oldLeafId = SessionManager.LeafId;
        if (targetId == oldLeafId) return new NavigateTreeResult(Cancelled: false);
        if (summarize && Model is null) throw new InvalidOperationException("No model available for summarization");

        var targetEntry = SessionManager.GetEntry(targetId) ?? throw new InvalidOperationException($"Entry {targetId} not found");
        var (entriesToSummarize, commonAncestorId) = BranchSummarization.CollectEntriesForBranchSummary(SessionManager, oldLeafId, targetId);

        var cts = new CancellationTokenSource();
        _branchSummaryCts = cts;
        try
        {
            (string Summary, System.Text.Json.Nodes.JsonNode? Details, Usage? Usage)? extensionSummary = null;
            var fromExtension = false;

            if (_extensionRunner.HasHandlers("session_before_tree"))
            {
                var hookResult = await _extensionRunner.EmitAsync(RunnerEvent.Of("session_before_tree",
                    ("preparation", new Dictionary<string, object?>
                    {
                        ["targetId"] = targetId, ["oldLeafId"] = oldLeafId, ["commonAncestorId"] = commonAncestorId,
                        ["entriesToSummarize"] = entriesToSummarize, ["userWantsSummary"] = summarize,
                        ["customInstructions"] = customInstructions, ["replaceInstructions"] = replaceInstructions, ["label"] = label,
                    }),
                    ("signal", cts.Token)));
                if (hookResult is IReadOnlyDictionary<string, object?> result)
                {
                    if (result.GetValueOrDefault("cancel") is true) return new NavigateTreeResult(Cancelled: true);
                    if (summarize && result.GetValueOrDefault("summary") is BranchSummaryResult { Summary: { } s } summaryResult)
                    {
                        extensionSummary = (s, null, summaryResult.Usage);
                        fromExtension = true;
                    }
                    if (result.TryGetValue("customInstructions", out var ci)) customInstructions = ci as string;
                    if (result.TryGetValue("replaceInstructions", out var ri) && ri is bool rb) replaceInstructions = rb;
                    if (result.TryGetValue("label", out var l)) label = l as string;
                }
            }

            string? summaryText = null;
            System.Text.Json.Nodes.JsonNode? summaryDetails = null;
            Usage? summaryUsage = null;
            if (summarize && entriesToSummarize.Count > 0 && extensionSummary is null)
            {
                var auth = await GetSummarizationRequestAuthAsync(Model!);
                var result = await BranchSummarization.GenerateBranchSummaryAsync(entriesToSummarize, new SummarizationRequest
                {
                    Model = auth.Model,
                    ApiKey = auth.ApiKey,
                    Headers = auth.Headers,
                    Env = auth.Env,
                    CancellationToken = cts.Token,
                    StreamFn = Agent.StreamFunction,
                    Retry = SettingsManager.RetrySettings,
                    Callbacks = SummarizationRetryCallbacks("branchSummary"),
                }, customInstructions, replaceInstructions == true, SettingsManager.BranchSummarySettings.ReserveTokens);

                if (result.Aborted) return new NavigateTreeResult(Cancelled: true, Aborted: true);
                if (result.Error is not null) throw new InvalidOperationException(result.Error);
                summaryText = result.Summary;
                summaryUsage = result.Usage;
                summaryDetails = new System.Text.Json.Nodes.JsonObject
                {
                    ["readFiles"] = new System.Text.Json.Nodes.JsonArray((result.ReadFiles ?? []).Select(f => (System.Text.Json.Nodes.JsonNode)f).ToArray()),
                    ["modifiedFiles"] = new System.Text.Json.Nodes.JsonArray((result.ModifiedFiles ?? []).Select(f => (System.Text.Json.Nodes.JsonNode)f).ToArray()),
                };
            }
            else if (extensionSummary is { } ext)
            {
                (summaryText, summaryDetails, summaryUsage) = ext;
            }

            string? newLeafId;
            string? editorText = null;
            switch (targetEntry)
            {
                case SessionMessageEntry { Message: UserMessage user }:
                    newLeafId = targetEntry.ParentId;
                    editorText = TextUtils.ContentText(user.Content, "");
                    break;
                case CustomMessageEntry custom:
                    newLeafId = targetEntry.ParentId;
                    editorText = TextUtils.ContentText(custom.Content, "");
                    break;
                default:
                    newLeafId = targetId;
                    break;
            }

            BranchSummaryEntry? summaryEntry = null;
            if (!string.IsNullOrEmpty(summaryText))
            {
                var summaryId = SessionManager.BranchWithSummary(newLeafId, summaryText, summaryDetails, fromExtension, summaryUsage);
                summaryEntry = SessionManager.GetEntry(summaryId) as BranchSummaryEntry;
                if (!string.IsNullOrEmpty(label)) SessionManager.AppendLabelChange(summaryId, label);
            }
            else if (newLeafId is null)
            {
                SessionManager.ResetLeaf();
            }
            else
            {
                SessionManager.Branch(newLeafId);
            }

            if (!string.IsNullOrEmpty(label) && string.IsNullOrEmpty(summaryText)) SessionManager.AppendLabelChange(targetId, label);

            Agent.State.Messages = SessionManager.BuildSessionContext().Messages;

            await _extensionRunner.EmitAsync(RunnerEvent.Of("session_tree",
                ("newLeafId", SessionManager.LeafId), ("oldLeafId", oldLeafId), ("summaryEntry", summaryEntry),
                ("fromExtension", string.IsNullOrEmpty(summaryText) ? null : fromExtension)));

            return new NavigateTreeResult(Cancelled: false, editorText, SummaryEntry: summaryEntry);
        }
        finally
        {
            _branchSummaryCts = null;
            cts.Dispose();
            ResolveIdleWaitIfIdle();
        }
    }

    /// <summary>User messages for the fork selector.</summary>
    public List<(string EntryId, string Text)> GetUserMessagesForForking() =>
        SessionManager.GetEntries()
            .OfType<SessionMessageEntry>()
            .Where(e => e.Message is UserMessage)
            .Select(e => (e.Id, TextUtils.ContentText(((UserMessage)e.Message).Content, "")))
            .Where(t => t.Item2.Length > 0)
            .ToList();

    /// <summary>Statistics over all session entries, including compacted history.</summary>
    public SessionStats GetSessionStats()
    {
        int userMessages = 0, assistantMessages = 0, toolResults = 0, totalMessages = 0, toolCalls = 0;
        long input = 0, output = 0, cacheRead = 0, cacheWrite = 0;
        double cost = 0;

        void Add(Usage usage)
        {
            input += usage.Input;
            output += usage.Output;
            cacheRead += usage.CacheRead;
            cacheWrite += usage.CacheWrite;
            cost += usage.Cost.Total;
        }

        foreach (var entry in SessionManager.GetEntries())
        {
            if (entry is BranchSummaryEntry { Usage: { } bu }) Add(bu);
            if (entry is CompactionEntry { Usage: { } cu }) Add(cu);
            if (entry is not SessionMessageEntry messageEntry) continue;
            totalMessages++;
            switch (messageEntry.Message)
            {
                case UserMessage:
                    userMessages++;
                    break;
                case ToolResultMessage toolResult:
                    toolResults++;
                    if (toolResult.Usage is { } tu) Add(tu);
                    break;
                case AssistantMessage assistant:
                    assistantMessages++;
                    toolCalls += assistant.Content.Count(c => c is ToolCall);
                    Add(assistant.Usage);
                    break;
            }
        }

        return new SessionStats(SessionFile, SessionId, userMessages, assistantMessages, toolCalls, toolResults, totalMessages,
            new SessionTokenStats(input, output, cacheRead, cacheWrite, input + output + cacheRead + cacheWrite), cost, GetContextUsage());
    }

    public ContextUsage? GetContextUsage()
    {
        if (Model is not { } model || model.ContextWindow <= 0) return null;
        var contextWindow = model.ContextWindow;

        // After compaction the last usage reflects the pre-compaction size; only trust usage from a later response.
        var branchEntries = SessionManager.GetBranch();
        if (SessionManager.GetLatestCompactionEntry(branchEntries) is { } latestCompaction)
        {
            var compactionIndex = branchEntries.LastIndexOf(latestCompaction);
            var hasPostCompactionUsage = false;
            for (var i = branchEntries.Count - 1; i > compactionIndex; i--)
            {
                if (branchEntries[i] is SessionMessageEntry { Message: AssistantMessage { StopReason: not StopReason.Aborted and not StopReason.Error } assistant }
                    && Compactor.CalculateContextTokens(assistant.Usage) > 0)
                {
                    hasPostCompactionUsage = true;
                    break;
                }
            }
            if (!hasPostCompactionUsage) return new ContextUsage(null, contextWindow, null);
        }

        var estimate = Compactor.EstimateContextTokens(Messages);
        return new ContextUsage(estimate.Tokens, contextWindow, (double)estimate.Tokens / contextWindow * 100);
    }

    /// <summary>Text of the last assistant message (skipping empty aborted ones), for /copy.</summary>
    public string? GetLastAssistantText()
    {
        var last = Messages.OfType<AssistantMessage>().LastOrDefault(m => !(m.StopReason == StopReason.Aborted && m.Content.Count == 0));
        if (last is null) return null;
        var text = string.Concat(last.Content.OfType<TextContent>().Select(t => t.Text)).Trim();
        return text.Length > 0 ? text : null;
    }

    public bool HasExtensionHandlers(string eventType) => _extensionRunner.HasHandlers(eventType);
}
