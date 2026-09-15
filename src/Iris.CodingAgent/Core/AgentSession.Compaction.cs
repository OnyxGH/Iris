using System.Text.Json.Nodes;
using Iris.Agent;
using Iris.Ai;
using Iris.Ai.Utils;
using Iris.CodingAgent.Core.Compaction;
using Iris.CodingAgent.Core.Extensions;

namespace Iris.CodingAgent.Core;

public sealed partial class AgentSession
{
    private RetryCallbacks SummarizationRetryCallbacks(string source, string? reason = null) => new()
    {
        OnRetryScheduled = (attempt, maxAttempts, delayMs, errorMessage) =>
        {
            Emit(new SummarizationRetryScheduledEvent(attempt, maxAttempts, delayMs, errorMessage));
            return Task.CompletedTask;
        },
        OnRetryAttemptStart = () =>
        {
            Emit(new SummarizationRetryAttemptStartEvent(source, reason));
            return Task.CompletedTask;
        },
        OnRetryFinished = (_, _, _) =>
        {
            Emit(new SummarizationRetryFinishedEvent());
            return Task.CompletedTask;
        },
    };

    private Task<CompactionResult> RunDefaultCompactionAsync(CompactionPreparation preparation, RequestAuth auth, string? customInstructions, CancellationToken cancellationToken, string reason) =>
        Compactor.CompactAsync(preparation, new SummarizationRequest
        {
            Model = auth.Model,
            ApiKey = auth.ApiKey,
            Headers = auth.Headers,
            Env = auth.Env,
            CancellationToken = cancellationToken,
            ThinkingLevel = ThinkingLevel,
            StreamFn = Agent.StreamFunction,
            Retry = SettingsManager.RetrySettings,
            Callbacks = SummarizationRetryCallbacks("compaction", reason),
        }, customInstructions);

    private sealed record ExtensionCompaction(string Summary, string FirstKeptEntryId, long TokensBefore, Usage? Usage, JsonNode? Details);

    private static ExtensionCompaction? ReadExtensionCompaction(object? hookResult, out bool cancel)
    {
        cancel = false;
        switch (hookResult)
        {
            case CompactionResult result:
                return new ExtensionCompaction(result.Summary, result.FirstKeptEntryId, result.TokensBefore, result.Usage, result.Details);
            case IReadOnlyDictionary<string, object?> dict:
                cancel = dict.TryGetValue("cancel", out var c) && c is true;
                return dict.TryGetValue("compaction", out var comp) && comp is CompactionResult r
                    ? new ExtensionCompaction(r.Summary, r.FirstKeptEntryId, r.TokensBefore, r.Usage, r.Details)
                    : null;
            default:
                return null;
        }
    }

    private CompactionResult SaveCompaction(ExtensionCompaction compaction, bool fromExtension, out CompactionEntry? savedEntry)
    {
        SessionManager.AppendCompaction(compaction.Summary, compaction.FirstKeptEntryId, compaction.TokensBefore, compaction.Details, fromExtension, compaction.Usage);
        var sessionContext = SessionManager.BuildSessionContext();
        Agent.State.Messages = sessionContext.Messages;
        savedEntry = SessionManager.GetEntries().OfType<CompactionEntry>().FirstOrDefault(e => e.Summary == compaction.Summary);
        return new CompactionResult
        {
            Summary = compaction.Summary,
            FirstKeptEntryId = compaction.FirstKeptEntryId,
            TokensBefore = compaction.TokensBefore,
            EstimatedTokensAfter = sessionContext.Messages.Sum(Compactor.EstimateTokens),
            Usage = compaction.Usage,
            Details = compaction.Details,
        };
    }

    private void ClearManualCompactionState()
    {
        _compactionCts = null;
        ResolveIdleWaitIfIdle();
    }

    /// <summary>
    /// Manually compact the session context (/compact, RPC, extensions). Aborts the current operation first and never
    /// retries or continues the interrupted turn.
    /// </summary>
    public async Task<CompactionResult> CompactAsync(string? customInstructions = null)
    {
        await AbortAsync();
        var cts = new CancellationTokenSource();
        _compactionCts = cts;
        Emit(new CompactionStartEvent("manual"));
        var fromExtension = false;

        try
        {
            var model = Model ?? throw new InvalidOperationException(AuthGuidance.FormatNoModelSelectedMessage());
            var settings = ToCompactionSettings(SettingsManager.GetCompactionSettings(model));
            var auth = await GetSummarizationRequestAuthAsync(model);
            var pathEntries = SessionManager.GetBranch();

            var preparation = Compactor.PrepareCompaction(pathEntries, settings);
            if (preparation is null)
            {
                if (pathEntries.Count > 0 && pathEntries[^1] is CompactionEntry) throw new InvalidOperationException("Already compacted");
                throw new InvalidOperationException("Nothing to compact (session too small)");
            }

            ExtensionCompaction? compaction = null;
            if (_extensionRunner.HasHandlers("session_before_compact"))
            {
                var hookResult = await _extensionRunner.EmitAsync(RunnerEvent.Of("session_before_compact",
                    ("preparation", preparation), ("branchEntries", pathEntries), ("customInstructions", customInstructions),
                    ("reason", "manual"), ("willRetry", false), ("signal", cts.Token)));
                compaction = ReadExtensionCompaction(hookResult, out var cancel);
                if (cancel) throw new OperationCanceledException("Compaction cancelled");
                fromExtension = compaction is not null;
            }

            if (compaction is null)
            {
                var result = await RunDefaultCompactionAsync(preparation, auth, customInstructions, cts.Token, "manual");
                compaction = new ExtensionCompaction(result.Summary, result.FirstKeptEntryId, result.TokensBefore, result.Usage, result.Details);
            }

            if (cts.IsCancellationRequested) throw new OperationCanceledException("Compaction cancelled");

            var compactionResult = SaveCompaction(compaction, fromExtension, out var savedEntry);
            if (savedEntry is not null)
            {
                await _extensionRunner.EmitAsync(RunnerEvent.Of("session_compact",
                    ("compactionEntry", savedEntry), ("fromExtension", fromExtension), ("reason", "manual"), ("willRetry", false)));
            }

            // compaction_end listeners may submit queued prompts, so expose idle state first.
            ClearManualCompactionState();
            Emit(new CompactionEndEvent("manual", compactionResult, Aborted: false, WillRetry: false));
            return compactionResult;
        }
        catch (Exception ex)
        {
            var aborted = ex is OperationCanceledException || ex.Message == "Compaction cancelled";
            var errorMessage = aborted ? null : $"Compaction failed: {ex.Message}";
            ClearManualCompactionState();
            Emit(new CompactionEndEvent("manual", null, aborted, WillRetry: false, errorMessage));
            await EmitSessionCompactFailedAsync("manual", errorMessage, aborted, willRetry: false, fromExtension);
            if (aborted && ex is OperationCanceledException) throw new InvalidOperationException("Compaction cancelled", ex);
            throw;
        }
        finally
        {
            ClearManualCompactionState();
            cts.Dispose();
        }
    }

    public void AbortCompaction()
    {
        TryCancel(_compactionCts);
        TryCancel(_autoCompactionCts);
    }

    public void AbortBranchSummary() => TryCancel(_branchSummaryCts);

    private static void TryCancel(CancellationTokenSource? cts)
    {
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Automatic compaction after agent_end or before a prompt: overflow with retry (drop the failed message, compact,
    /// retry once), overflow without retry, or threshold. Returns whether the post-run loop should continue.
    /// </summary>
    private async Task<bool> CheckCompactionAsync(AssistantMessage assistantMessage, bool skipAbortedCheck = true)
    {
        var settings = ToCompactionSettings(SettingsManager.GetCompactionSettings(Model));
        if (!settings.Enabled) return false;
        if (skipAbortedCheck && assistantMessage.StopReason == StopReason.Aborted) return false;

        var contextWindow = Model?.ContextWindow ?? 0;
        var sameModel = Model is { } model && assistantMessage.Provider == model.Provider && assistantMessage.Model == model.Id;

        // Messages older than the latest compaction must not retrigger compaction.
        var compactionEntry = SessionManager.GetLatestCompactionEntry(SessionManager.GetBranch());
        var compactionTime = compactionEntry is null ? (long?)null : CodingAgentMessages.ParseTimestamp(compactionEntry.Timestamp);
        if (compactionTime is { } boundary && assistantMessage.Timestamp <= boundary) return false;

        var contextOverflow = sameModel && Overflow.IsContextOverflow(assistantMessage, contextWindow);
        var recoverableLength = sameModel && Overflow.IsRecoverableLength(assistantMessage, Model?.MaxTokens ?? 0);
        if (contextOverflow || recoverableLength)
        {
            var willRetry = assistantMessage.StopReason != StopReason.Stop;
            if (!willRetry) return await RunAutoCompactionAsync("overflow", willRetry: false);

            if (_overflowRecoveryAttempted)
            {
                var errorMessage = contextOverflow
                    ? "Context overflow recovery failed after one compact-and-retry attempt. Try reducing context or switching to a larger-context model."
                    : "Truncated response recovery failed after one compact-and-retry attempt.";
                Emit(new CompactionEndEvent("overflow", null, Aborted: false, WillRetry: false, errorMessage));
                await EmitSessionCompactFailedAsync("overflow", errorMessage, aborted: false, willRetry: false, fromExtension: false);
                return false;
            }

            _overflowRecoveryAttempted = true;
            RemoveTrailingAssistantFromState();
            return await RunAutoCompactionAsync("overflow", willRetry);
        }

        // Threshold: for errors or all-zero usage, estimate from the last valid response.
        long contextTokens;
        var directContextTokens = Compactor.CalculateContextTokens(assistantMessage.Usage);
        if (assistantMessage.StopReason == StopReason.Error || directContextTokens == 0)
        {
            var messages = Agent.State.Messages;
            var estimate = Compactor.EstimateContextTokens(messages);
            if (estimate.LastUsageIndex is { } usageIndex && compactionTime is { } compactedAt
                && messages[usageIndex] is AssistantMessage usageMessage && usageMessage.Timestamp <= compactedAt)
            {
                return false;
            }
            contextTokens = estimate.Tokens;
        }
        else
        {
            contextTokens = directContextTokens;
        }

        return Compactor.ShouldCompact(contextTokens, contextWindow, settings)
            && await RunAutoCompactionAsync("threshold", willRetry: false);
    }

    private void RemoveTrailingAssistantFromState()
    {
        var messages = Agent.State.Messages;
        if (messages.Count > 0 && messages[^1] is AssistantMessage) Agent.State.Messages = messages[..^1];
    }

    /// <summary>Threshold or overflow compaction. Returns whether the post-run loop should call continue.</summary>
    private async Task<bool> RunAutoCompactionAsync(string reason, bool willRetry)
    {
        var model = Model;
        var settings = ToCompactionSettings(SettingsManager.GetCompactionSettings(model));
        var started = false;
        var fromExtension = false;
        CancellationTokenSource? cts = null;

        try
        {
            if (model is null) return false;
            var auth = await GetSummarizationRequestAuthAsync(model);
            var pathEntries = SessionManager.GetBranch();
            var preparation = Compactor.PrepareCompaction(pathEntries, settings);
            if (preparation is null) return false;

            Emit(new CompactionStartEvent(reason));
            cts = new CancellationTokenSource();
            _autoCompactionCts = cts;
            started = true;

            ExtensionCompaction? compaction = null;
            if (_extensionRunner.HasHandlers("session_before_compact"))
            {
                var hookResult = await _extensionRunner.EmitAsync(RunnerEvent.Of("session_before_compact",
                    ("preparation", preparation), ("branchEntries", pathEntries), ("customInstructions", null),
                    ("reason", reason), ("willRetry", willRetry), ("signal", cts.Token)));
                compaction = ReadExtensionCompaction(hookResult, out var cancel);
                if (cancel)
                {
                    Emit(new CompactionEndEvent(reason, null, Aborted: true, WillRetry: false));
                    await EmitSessionCompactFailedAsync(reason, null, aborted: true, willRetry: false, fromExtension: false);
                    return false;
                }
                fromExtension = compaction is not null;
            }

            if (compaction is null)
            {
                var result = await RunDefaultCompactionAsync(preparation, auth, null, cts.Token, reason);
                compaction = new ExtensionCompaction(result.Summary, result.FirstKeptEntryId, result.TokensBefore, result.Usage, result.Details);
            }

            if (cts.IsCancellationRequested)
            {
                Emit(new CompactionEndEvent(reason, null, Aborted: true, WillRetry: false));
                await EmitSessionCompactFailedAsync(reason, null, aborted: true, willRetry: false, fromExtension);
                return false;
            }

            var compactionResult = SaveCompaction(compaction, fromExtension, out var savedEntry);
            if (savedEntry is not null)
            {
                await _extensionRunner.EmitAsync(RunnerEvent.Of("session_compact",
                    ("compactionEntry", savedEntry), ("fromExtension", fromExtension), ("reason", reason), ("willRetry", willRetry)));
            }
            Emit(new CompactionEndEvent(reason, compactionResult, Aborted: false, willRetry));

            if (willRetry)
            {
                // Rebuilding from the compaction can restore the persisted failed response; agent continue rejects a
                // trailing assistant, so drop it again.
                var messages = Agent.State.Messages;
                if (messages.Count > 0 && messages[^1] is AssistantMessage { StopReason: StopReason.Error or StopReason.Length }) Agent.State.Messages = messages[..^1];
                return true;
            }

            // Deliver messages queued while compacting.
            return Agent.HasQueuedMessages();
        }
        catch (Exception ex)
        {
            if (started)
            {
                var formatted = reason == "overflow" ? $"Context overflow recovery failed: {ex.Message}" : $"Auto-compaction failed: {ex.Message}";
                Emit(new CompactionEndEvent(reason, null, Aborted: false, WillRetry: false, formatted));
                await EmitSessionCompactFailedAsync(reason, formatted, aborted: false, willRetry: false, fromExtension);
            }
            return false;
        }
        finally
        {
            _autoCompactionCts = null;
            cts?.Dispose();
            ResolveIdleWaitIfIdle();
        }
    }

    public void SetAutoCompactionEnabled(bool enabled) => SettingsManager.SetCompactionEnabled(enabled);

    public bool AutoCompactionEnabled => SettingsManager.CompactionEnabled;

    // =========================================================================
    // Auto-retry
    // =========================================================================

    /// <summary>Overloaded, rate limit and server errors are retryable; context overflow is handled by compaction.</summary>
    private bool IsRetryableError(AssistantMessage message) =>
        !Overflow.IsContextOverflow(message, Model?.ContextWindow ?? 0) && AssistantRetry.IsRetryableAssistantError(message);

    private async Task<bool> PrepareRetryAsync(AssistantMessage message)
    {
        var settings = SettingsManager.RetrySettings;
        if (!settings.Enabled) return false;

        _retryAttempt++;
        if (_retryAttempt > settings.MaxRetries)
        {
            // Keep the completed attempt count so post-run handling can emit the final failure.
            _retryAttempt--;
            return false;
        }

        var delayMs = AssistantRetry.RetryDelayMs(settings, _retryAttempt);
        Emit(new AutoRetryStartEvent(_retryAttempt, settings.MaxRetries, delayMs, string.IsNullOrEmpty(message.ErrorMessage) ? "Unknown error" : message.ErrorMessage));

        // Remove the error from agent state (it stays in the session for history).
        RemoveTrailingAssistantFromState();

        var cts = new CancellationTokenSource();
        _retryCts = cts;
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(delayMs), cts.Token);
        }
        catch (OperationCanceledException)
        {
            var attempt = _retryAttempt;
            _retryAttempt = 0;
            Emit(new AutoRetryEndEvent(false, attempt, "Retry cancelled"));
            return false;
        }
        finally
        {
            _retryCts = null;
            cts.Dispose();
        }
        return true;
    }

    public void AbortRetry() => TryCancel(_retryCts);

    public bool IsRetrying => _retryCts is not null;

    public bool AutoRetryEnabled => SettingsManager.RetryEnabled;

    public void SetAutoRetryEnabled(bool enabled) => SettingsManager.SetRetryEnabled(enabled);
}
