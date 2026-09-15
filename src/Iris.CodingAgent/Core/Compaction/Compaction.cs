using System.Text.Json.Nodes;
using Iris.Agent;
using Iris.Ai;
using Iris.Ai.Json;
using Iris.Ai.Utils;

namespace Iris.CodingAgent.Core.Compaction;

public sealed class CompactionSettings
{
    public bool Enabled { get; set; } = true;
    public long ReserveTokens { get; set; } = 16384;
    public long KeepRecentTokens { get; set; } = 20000;
}

/// <summary>Result of compact(). SessionManager assigns ids when saving.</summary>
public sealed class CompactionResult
{
    public required string Summary { get; init; }
    public required string FirstKeptEntryId { get; init; }
    public long TokensBefore { get; init; }
    public long? EstimatedTokensAfter { get; init; }
    public Usage? Usage { get; init; }

    /// <summary>{ readFiles, modifiedFiles } for built-in compactions; extension-specific otherwise.</summary>
    public JsonNode? Details { get; init; }
}

public sealed record ContextUsageEstimate(long Tokens, long UsageTokens, long TrailingTokens, int? LastUsageIndex);

public sealed record CutPointResult(int FirstKeptEntryIndex, int TurnStartIndex, bool IsSplitTurn);

public sealed class CompactionPreparation
{
    public required string FirstKeptEntryId { get; init; }
    public required List<Message> MessagesToSummarize { get; init; }
    public required List<Message> TurnPrefixMessages { get; init; }
    public bool IsSplitTurn { get; init; }
    public long TokensBefore { get; init; }
    public string? PreviousSummary { get; init; }
    public required FileOperations FileOps { get; init; }
    public required CompactionSettings Settings { get; init; }
}

/// <summary>Request plumbing shared by all summarization calls.</summary>
public sealed class SummarizationRequest
{
    public required Model Model { get; init; }
    public string? ApiKey { get; init; }
    public Dictionary<string, string?>? Headers { get; init; }
    public Dictionary<string, string>? Env { get; init; }
    public CancellationToken CancellationToken { get; init; }
    public ThinkingLevel? ThinkingLevel { get; init; }
    public StreamFn? StreamFn { get; init; }
    public RetryPolicy? Retry { get; init; }
    public RetryCallbacks? Callbacks { get; init; }

    /// <summary>Routing session id forwarded without enabling prompt caching.</summary>
    public string? SessionId { get; init; }
}

/// <summary>Context compaction for long sessions.</summary>
public static class Compactor
{
    private const int EstimatedImageChars = 4800;

    private const string SummarizationPrompt = """
        The messages above are a conversation to summarize. Create a structured context checkpoint summary that another LLM will use to continue the work.

        Use this EXACT format:

        ## Goal
        [What is the user trying to accomplish? Can be multiple items if the session covers different tasks.]

        ## Constraints & Preferences
        - [Any constraints, preferences, or requirements mentioned by user]
        - [Or "(none)" if none were mentioned]

        ## Progress
        ### Done
        - [x] [Completed tasks/changes]

        ### In Progress
        - [ ] [Current work]

        ### Blocked
        - [Issues preventing progress, if any]

        ## Key Decisions
        - **[Decision]**: [Brief rationale]

        ## Next Steps
        1. [Ordered list of what should happen next]

        ## Critical Context
        - [Any data, examples, or references needed to continue]
        - [Or "(none)" if not applicable]

        Keep each section concise. Preserve exact file paths, function names, and error messages.
        """;

    private const string UpdateSummarizationInstructions = """
        Update the existing structured summary with new information. RULES:
        - PRESERVE all existing information from the previous summary
        - ADD new progress, decisions, and context from the new messages
        - UPDATE the Progress section: move items from "In Progress" to "Done" when completed
        - UPDATE "Next Steps" based on what was accomplished
        - PRESERVE exact file paths, function names, and error messages
        - If something is no longer relevant, you may remove it

        Use this EXACT format:

        ## Goal
        [Preserve existing goals, add new ones if the task expanded]

        ## Constraints & Preferences
        - [Preserve existing, add new ones discovered]

        ## Progress
        ### Done
        - [x] [Include previously done items AND newly completed items]

        ### In Progress
        - [ ] [Current work - update based on progress]

        ### Blocked
        - [Current blockers - remove if resolved]

        ## Key Decisions
        - **[Decision]**: [Brief rationale] (preserve all previous, add new)

        ## Next Steps
        1. [Update based on current state]

        ## Critical Context
        - [Preserve important context, add new if needed]

        Keep each section concise. Preserve exact file paths, function names, and error messages.
        """;

    private static readonly string UpdateSummarizationPrompt =
        "The messages above are NEW conversation messages to incorporate into the existing summary provided in <previous-summary> tags.\n\n" + UpdateSummarizationInstructions;

    private const string TurnPrefixSummarizationPrompt = """
        This is the PREFIX of a turn that was too large to keep. The SUFFIX (recent work) is retained.

        Summarize the prefix to provide context for the retained suffix:

        ## Original Request
        [What did the user ask for in this turn?]

        ## Early Progress
        - [Key decisions and work done in the prefix]

        ## Context for Suffix
        - [Information needed to understand the retained recent work]

        Be concise. Focus on what's needed to understand the kept suffix.
        """;

    // Raw string literals take the source file's line endings; prompts must always use "\n".
    private static string Lf(string text) => text.Replace("\r\n", "\n");

    // ---- tokens ----

    public static long CalculateContextTokens(Usage usage) =>
        usage.TotalTokens != 0 ? usage.TotalTokens : usage.Input + usage.Output + usage.CacheRead + usage.CacheWrite;

    private static Usage? GetAssistantUsage(Message msg) =>
        msg is AssistantMessage { StopReason: not StopReason.Aborted and not StopReason.Error, Usage: { } usage } && CalculateContextTokens(usage) > 0
            ? usage
            : null;

    public static Usage? GetLastAssistantUsage(IReadOnlyList<SessionEntry> entries)
    {
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            if (entries[i] is SessionMessageEntry m && GetAssistantUsage(m.Message) is { } usage) return usage;
        }
        return null;
    }

    /// <summary>Last assistant usage plus chars/4 estimates for later messages.</summary>
    public static ContextUsageEstimate EstimateContextTokens(IReadOnlyList<Message> messages)
    {
        var index = -1;
        Usage? usage = null;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            usage = GetAssistantUsage(messages[i]);
            if (usage is not null)
            {
                index = i;
                break;
            }
        }

        if (usage is null)
        {
            var estimated = messages.Sum(EstimateTokens);
            return new ContextUsageEstimate(estimated, 0, estimated, null);
        }

        var usageTokens = CalculateContextTokens(usage);
        long trailing = 0;
        for (var i = index + 1; i < messages.Count; i++) trailing += EstimateTokens(messages[i]);
        return new ContextUsageEstimate(usageTokens + trailing, usageTokens, trailing, index);
    }

    public static bool ShouldCompact(long contextTokens, long contextWindow, CompactionSettings settings) =>
        settings.Enabled && contextTokens > contextWindow - settings.ReserveTokens;

    private static long TextAndImageChars(UserContent content) =>
        content.Text is { } text ? text.Length : TextAndImageChars(content.Blocks ?? []);

    private static long TextAndImageChars(IEnumerable<ContentBlock> blocks) =>
        blocks.Sum(b => b switch
        {
            TextContent t => (long)t.Text.Length,
            ImageContent => EstimatedImageChars,
            _ => 0L,
        });

    private static long CeilDiv4(long chars) => (chars + 3) / 4;

    /// <summary>Conservative chars/4 token estimate for a message.</summary>
    public static long EstimateTokens(Message message) => message switch
    {
        UserMessage u => CeilDiv4(TextAndImageChars(u.Content)),
        AssistantMessage a => CeilDiv4(a.Content.Sum(b => b switch
        {
            TextContent t => (long)t.Text.Length,
            ThinkingContent th => th.Thinking.Length,
            ToolCall c => c.Name.Length + CompactionUtils.Stringify(c.Arguments).Length,
            _ => 0L,
        })),
        CustomMessage c => CeilDiv4(TextAndImageChars(c.Content)),
        ToolResultMessage r => CeilDiv4(TextAndImageChars(r.Content)),
        BashExecutionMessage b => CeilDiv4(b.Command.Length + b.Output.Length),
        BranchSummaryMessage s => CeilDiv4(s.Summary.Length),
        CompactionSummaryMessage s => CeilDiv4(s.Summary.Length),
        _ => 0,
    };

    // ---- cut points ----

    private static List<Message> ContextMessages(SessionEntry entry) => SessionManager.SessionEntryToContextMessages(entry);

    private static bool IsCutPointMessage(Message message) =>
        message.Role is "user" or "assistant" or "bashExecution" or "custom" or "branchSummary" or "compactionSummary";

    private static bool IsTurnStartMessage(Message message) =>
        message.Role is "user" or "bashExecution" or "custom" or "branchSummary" or "compactionSummary";

    private static bool IsTurnStartEntry(SessionEntry entry) =>
        entry is not CompactionEntry && ContextMessages(entry).Any(IsTurnStartMessage);

    private static List<int> FindValidCutPoints(IReadOnlyList<SessionEntry> entries, int startIndex, int endIndex)
    {
        var cutPoints = new List<int>();
        for (var i = startIndex; i < endIndex; i++)
        {
            if (entries[i] is CompactionEntry) continue;
            if (ContextMessages(entries[i]).Any(IsCutPointMessage)) cutPoints.Add(i);
        }
        return cutPoints;
    }

    public static int FindTurnStartIndex(IReadOnlyList<SessionEntry> entries, int entryIndex, int startIndex)
    {
        for (var i = entryIndex; i >= startIndex; i--)
        {
            if (IsTurnStartEntry(entries[i])) return i;
        }
        return -1;
    }

    /// <summary>Walk back from the newest entry until keepRecentTokens is reached and cut at the nearest valid point.</summary>
    public static CutPointResult FindCutPoint(IReadOnlyList<SessionEntry> entries, int startIndex, int endIndex, long keepRecentTokens)
    {
        var cutPoints = FindValidCutPoints(entries, startIndex, endIndex);
        if (cutPoints.Count == 0) return new CutPointResult(startIndex, -1, false);

        long accumulated = 0;
        var cutIndex = cutPoints[0];
        for (var i = endIndex - 1; i >= startIndex; i--)
        {
            var messageTokens = ContextMessages(entries[i]).Sum(EstimateTokens);
            if (messageTokens == 0) continue;
            accumulated += messageTokens;
            if (accumulated >= keepRecentTokens)
            {
                foreach (var c in cutPoints)
                {
                    if (c >= i)
                    {
                        cutIndex = c;
                        break;
                    }
                }
                break;
            }
        }

        // Include adjacent metadata entries that do not affect context.
        while (cutIndex > startIndex)
        {
            var prev = entries[cutIndex - 1];
            if (prev is CompactionEntry || ContextMessages(prev).Count > 0) break;
            cutIndex--;
        }

        var startsTurn = IsTurnStartEntry(entries[cutIndex]);
        var turnStartIndex = startsTurn ? -1 : FindTurnStartIndex(entries, cutIndex, startIndex);
        return new CutPointResult(cutIndex, turnStartIndex, !startsTurn && turnStartIndex != -1);
    }

    // ---- summarization ----

    /// <summary>Error message when a summarization response must not be persisted (errors and length stops).</summary>
    public static string? GetSummarizationFailure(AssistantMessage response, string label) => response.StopReason switch
    {
        StopReason.Error => $"{label} failed: {(string.IsNullOrEmpty(response.ErrorMessage) ? "Unknown error" : response.ErrorMessage)}",
        StopReason.Length => $"{label} failed: generation hit the token cap and the summary is incomplete",
        _ => null,
    };

    private static SimpleStreamOptions CreateSummarizationOptions(SummarizationRequest request, long maxTokens)
    {
        var options = new SimpleStreamOptions
        {
            MaxTokens = (int)Math.Min(int.MaxValue, maxTokens),
            CancellationToken = request.CancellationToken,
            ApiKey = request.ApiKey,
            Headers = request.Headers,
            Env = request.Env,
            SessionId = request.SessionId,
        };
        if (request.Model.Reasoning && request.ThinkingLevel is { } level && level != ThinkingLevel.Off) options.Reasoning = level;
        return options;
    }

    /// <summary>Single LLM call for every summary, wrapped in the retry policy. Prompt caching is disabled.</summary>
    public static Task<AssistantMessage> CompleteSummarizationAsync(Model model, Context context, SimpleStreamOptions options, StreamFn? streamFn, RetryPolicy? retry, RetryCallbacks? callbacks)
    {
        var requestOptions = options.CloneSimple();
        requestOptions.CacheRetention = CacheRetention.None;
        requestOptions.SessionId ??= UuidV7.New();

        async Task<AssistantMessage> Produce() => streamFn is not null
            ? await (await streamFn(model, context, requestOptions)).Result()
            : await IrisAi.CompleteSimpleAsync(model, context, requestOptions);

        return AssistantRetry.RetryAssistantCallAsync(Produce, retry, requestOptions.CancellationToken, callbacks);
    }

    private static Context BuildSummarizationContext(string promptText) => new()
    {
        SystemPrompt = CompactionUtils.SummarizationSystemPrompt,
        Messages = [new UserMessage { Content = UserContent.FromBlocks([new TextContent(promptText)]), Timestamp = TimeUtil.NowMs() }],
    };

    private static long ClampToModelMax(long budget, Model model) => model.MaxTokens > 0 ? Math.Min(budget, model.MaxTokens) : budget;

    /// <summary>Generate or update a structured summary and return the provider usage.</summary>
    public static async Task<(string Text, Usage Usage)> GenerateSummaryWithUsageAsync(
        IReadOnlyList<Message> currentMessages, long reserveTokens, SummarizationRequest request,
        string? customInstructions = null, string? previousSummary = null)
    {
        var maxTokens = ClampToModelMax((long)Math.Floor(0.8 * reserveTokens), request.Model);
        var basePrompt = Lf(previousSummary is not null ? UpdateSummarizationPrompt : SummarizationPrompt);
        if (!string.IsNullOrEmpty(customInstructions)) basePrompt = $"{basePrompt}\n\nAdditional focus: {customInstructions}";

        var conversationText = CompactionUtils.SerializeConversation(CodingAgentMessages.ConvertToLlm(currentMessages));
        var promptText = $"<conversation>\n{conversationText}\n</conversation>\n\n";
        if (previousSummary is not null) promptText += $"<previous-summary>\n{previousSummary}\n</previous-summary>\n\n";
        promptText += basePrompt;

        var response = await CompleteSummarizationAsync(request.Model, BuildSummarizationContext(promptText), CreateSummarizationOptions(request, maxTokens),
            request.StreamFn, request.Retry, request.Callbacks);

        if (GetSummarizationFailure(response, "Summarization") is { } failure) throw new InvalidOperationException(failure);
        if (response.Content.Any(b => b is ToolCall)) throw new InvalidOperationException("Summarization attempted to call a tool");
        return (TextUtils.ContentText(response.Content), response.Usage);
    }

    public static async Task<string> GenerateSummaryAsync(IReadOnlyList<Message> currentMessages, long reserveTokens, SummarizationRequest request,
        string? customInstructions = null, string? previousSummary = null) =>
        (await GenerateSummaryWithUsageAsync(currentMessages, reserveTokens, request, customInstructions, previousSummary)).Text;

    private static async Task<(string Text, Usage Usage)> GenerateTurnPrefixSummaryAsync(IReadOnlyList<Message> messages, long reserveTokens, SummarizationRequest request)
    {
        var maxTokens = ClampToModelMax((long)Math.Floor(0.5 * reserveTokens), request.Model);
        var conversationText = CompactionUtils.SerializeConversation(CodingAgentMessages.ConvertToLlm(messages));
        var promptText = $"<conversation>\n{conversationText}\n</conversation>\n\n{Lf(TurnPrefixSummarizationPrompt)}";

        var response = await CompleteSummarizationAsync(request.Model, BuildSummarizationContext(promptText), CreateSummarizationOptions(request, maxTokens),
            request.StreamFn, request.Retry, request.Callbacks);

        if (GetSummarizationFailure(response, "Turn prefix summarization") is { } failure) throw new InvalidOperationException(failure);
        if (response.Content.Any(b => b is ToolCall)) throw new InvalidOperationException("Turn prefix summarization attempted to call a tool");
        return (TextUtils.ContentText(response.Content), response.Usage);
    }

    // ---- preparation ----

    private static FileOperations ExtractFileOperations(IEnumerable<Message> messages, IReadOnlyList<SessionEntry> entries, int prevCompactionIndex)
    {
        var fileOps = new FileOperations();
        if (prevCompactionIndex >= 0 && entries[prevCompactionIndex] is CompactionEntry { FromHook: not true, Details: JsonObject details })
        {
            if (details["readFiles"] is JsonArray read)
            {
                foreach (var f in read) if (IrisJson.GetString(f) is { } s) fileOps.Read.Add(s);
            }
            if (details["modifiedFiles"] is JsonArray modified)
            {
                foreach (var f in modified) if (IrisJson.GetString(f) is { } s) fileOps.Edited.Add(s);
            }
        }
        foreach (var msg in messages) CompactionUtils.ExtractFileOpsFromMessage(msg, fileOps);
        return fileOps;
    }

    private static Message? GetMessageFromEntryForCompaction(SessionEntry entry) =>
        entry is CompactionEntry ? null : ContextMessages(entry).FirstOrDefault();

    public static CompactionPreparation? PrepareCompaction(IReadOnlyList<SessionEntry> pathEntries, CompactionSettings settings)
    {
        if (pathEntries.Count > 0 && pathEntries[^1] is CompactionEntry) return null;

        var prevCompactionIndex = -1;
        for (var i = pathEntries.Count - 1; i >= 0; i--)
        {
            if (pathEntries[i] is CompactionEntry)
            {
                prevCompactionIndex = i;
                break;
            }
        }

        string? previousSummary = null;
        var boundaryStart = 0;
        if (prevCompactionIndex >= 0)
        {
            var prev = (CompactionEntry)pathEntries[prevCompactionIndex];
            previousSummary = prev.Summary;
            var firstKeptIndex = -1;
            for (var i = 0; i < pathEntries.Count; i++)
            {
                if (pathEntries[i].Id == prev.FirstKeptEntryId)
                {
                    firstKeptIndex = i;
                    break;
                }
            }
            boundaryStart = firstKeptIndex >= 0 ? firstKeptIndex : prevCompactionIndex + 1;
        }
        var boundaryEnd = pathEntries.Count;

        var leafId = pathEntries.Count > 0 ? pathEntries[^1].Id : null;
        var tokensBefore = EstimateContextTokens(SessionManager.BuildSessionContext(pathEntries, leafId).Messages).Tokens;
        var cutPoint = FindCutPoint(pathEntries, boundaryStart, boundaryEnd, settings.KeepRecentTokens);

        if (cutPoint.FirstKeptEntryIndex >= pathEntries.Count || string.IsNullOrEmpty(pathEntries[cutPoint.FirstKeptEntryIndex].Id)) return null;
        var firstKeptEntryId = pathEntries[cutPoint.FirstKeptEntryIndex].Id;

        var historyEnd = cutPoint.IsSplitTurn ? cutPoint.TurnStartIndex : cutPoint.FirstKeptEntryIndex;
        var messagesToSummarize = new List<Message>();
        for (var i = boundaryStart; i < historyEnd; i++)
        {
            if (GetMessageFromEntryForCompaction(pathEntries[i]) is { } msg) messagesToSummarize.Add(msg);
        }

        var turnPrefixMessages = new List<Message>();
        if (cutPoint.IsSplitTurn)
        {
            for (var i = cutPoint.TurnStartIndex; i < cutPoint.FirstKeptEntryIndex; i++)
            {
                if (GetMessageFromEntryForCompaction(pathEntries[i]) is { } msg) turnPrefixMessages.Add(msg);
            }
        }

        if (messagesToSummarize.Count == 0 && turnPrefixMessages.Count == 0) return null;

        var fileOps = ExtractFileOperations(messagesToSummarize, pathEntries, prevCompactionIndex);
        if (cutPoint.IsSplitTurn)
        {
            foreach (var msg in turnPrefixMessages) CompactionUtils.ExtractFileOpsFromMessage(msg, fileOps);
        }

        return new CompactionPreparation
        {
            FirstKeptEntryId = firstKeptEntryId,
            MessagesToSummarize = messagesToSummarize,
            TurnPrefixMessages = turnPrefixMessages,
            IsSplitTurn = cutPoint.IsSplitTurn,
            TokensBefore = tokensBefore,
            PreviousSummary = previousSummary,
            FileOps = fileOps,
            Settings = settings,
        };
    }

    private static Usage CombineUsage(Usage first, Usage second) => new()
    {
        Input = first.Input + second.Input,
        Output = first.Output + second.Output,
        CacheRead = first.CacheRead + second.CacheRead,
        CacheWrite = first.CacheWrite + second.CacheWrite,
        CacheWrite1h = first.CacheWrite1h is null && second.CacheWrite1h is null ? null : (first.CacheWrite1h ?? 0) + (second.CacheWrite1h ?? 0),
        Reasoning = first.Reasoning is null && second.Reasoning is null ? null : (first.Reasoning ?? 0) + (second.Reasoning ?? 0),
        TotalTokens = first.TotalTokens + second.TotalTokens,
        Cost = new UsageCost
        {
            Input = first.Cost.Input + second.Cost.Input,
            Output = first.Cost.Output + second.Cost.Output,
            CacheRead = first.Cost.CacheRead + second.Cost.CacheRead,
            CacheWrite = first.Cost.CacheWrite + second.Cost.CacheWrite,
            Total = first.Cost.Total + second.Cost.Total,
        },
    };

    /// <summary>Generate summaries for a prepared compaction.</summary>
    public static async Task<CompactionResult> CompactAsync(CompactionPreparation preparation, SummarizationRequest request, string? customInstructions = null)
    {
        string summary;
        Usage summaryUsage;
        var reserveTokens = preparation.Settings.ReserveTokens;

        if (preparation.IsSplitTurn && preparation.TurnPrefixMessages.Count > 0)
        {
            var historyText = "No prior history.";
            Usage? historyUsage = null;
            if (preparation.MessagesToSummarize.Count > 0)
            {
                (historyText, var usage) = await GenerateSummaryWithUsageAsync(preparation.MessagesToSummarize, reserveTokens, request, customInstructions, preparation.PreviousSummary);
                historyUsage = usage;
            }
            var turnPrefix = await GenerateTurnPrefixSummaryAsync(preparation.TurnPrefixMessages, reserveTokens, request);
            summary = $"{historyText}\n\n---\n\n**Turn Context (split turn):**\n\n{turnPrefix.Text}";
            summaryUsage = historyUsage is null ? turnPrefix.Usage : CombineUsage(historyUsage, turnPrefix.Usage);
        }
        else
        {
            (summary, summaryUsage) = await GenerateSummaryWithUsageAsync(preparation.MessagesToSummarize, reserveTokens, request, customInstructions, preparation.PreviousSummary);
        }

        var (readFiles, modifiedFiles) = CompactionUtils.ComputeFileLists(preparation.FileOps);
        summary += CompactionUtils.FormatFileOperations(readFiles, modifiedFiles);

        if (string.IsNullOrEmpty(preparation.FirstKeptEntryId)) throw new InvalidOperationException("First kept entry has no UUID - session may need migration");

        return new CompactionResult
        {
            Summary = summary,
            FirstKeptEntryId = preparation.FirstKeptEntryId,
            TokensBefore = preparation.TokensBefore,
            Usage = summaryUsage,
            Details = new JsonObject
            {
                ["readFiles"] = new JsonArray(readFiles.Select(f => (JsonNode)JsonValue.Create(f)).ToArray()),
                ["modifiedFiles"] = new JsonArray(modifiedFiles.Select(f => (JsonNode)JsonValue.Create(f)).ToArray()),
            },
        };
    }
}
