using System.Text.Json.Nodes;
using PiSharp.Ai;
using PiSharp.Ai.Json;
using PiSharp.Ai.Utils;

namespace PiSharp.CodingAgent.Core.Compaction;

public sealed class BranchSummaryResult
{
    public string? Summary { get; init; }
    public Usage? Usage { get; init; }
    public List<string>? ReadFiles { get; init; }
    public List<string>? ModifiedFiles { get; init; }
    public bool Aborted { get; init; }
    public string? Error { get; init; }
}

public sealed record BranchPreparation(List<Message> Messages, FileOperations FileOps, long TotalTokens);

public sealed record CollectEntriesResult(List<SessionEntry> Entries, string? CommonAncestorId);

/// <summary>Summaries of abandoned branches during tree navigation. Port of core/compaction/branch-summarization.ts.</summary>
public static class BranchSummarization
{
    private const string BranchSummaryPreamble = "The user explored a different conversation branch before returning here.\nSummary of that exploration:\n\n";

    private const string BranchSummaryPrompt = """
        Create a structured summary of this conversation branch for context when returning later.

        Use this EXACT format:

        ## Goal
        [What was the user trying to accomplish in this branch?]

        ## Constraints & Preferences
        - [Any constraints, preferences, or requirements mentioned]
        - [Or "(none)" if none were mentioned]

        ## Progress
        ### Done
        - [x] [Completed tasks/changes]

        ### In Progress
        - [ ] [Work that was started but not finished]

        ### Blocked
        - [Issues preventing progress, if any]

        ## Key Decisions
        - **[Decision]**: [Brief rationale]

        ## Next Steps
        1. [What should happen next to continue this work]

        Keep each section concise. Preserve exact file paths, function names, and error messages.
        """;

    /// <summary>Entries from the old leaf back to (excluding) the common ancestor with the target, in chronological order.</summary>
    public static CollectEntriesResult CollectEntriesForBranchSummary(SessionManager session, string? oldLeafId, string targetId)
    {
        if (string.IsNullOrEmpty(oldLeafId)) return new CollectEntriesResult([], null);

        var oldPath = session.GetBranch(oldLeafId).Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        var targetPath = session.GetBranch(targetId);
        string? commonAncestorId = null;
        for (var i = targetPath.Count - 1; i >= 0; i--)
        {
            if (oldPath.Contains(targetPath[i].Id))
            {
                commonAncestorId = targetPath[i].Id;
                break;
            }
        }

        var entries = new List<SessionEntry>();
        var current = oldLeafId;
        while (!string.IsNullOrEmpty(current) && current != commonAncestorId)
        {
            var entry = session.GetEntry(current);
            if (entry is null) break;
            entries.Add(entry);
            current = entry.ParentId;
        }
        entries.Reverse();
        return new CollectEntriesResult(entries, commonAncestorId);
    }

    private static Message? GetMessageFromEntry(SessionEntry entry) => entry switch
    {
        SessionMessageEntry { Message: ToolResultMessage } => null,
        SessionMessageEntry m => m.Message,
        CustomMessageEntry c => CodingAgentMessages.CreateCustomMessage(c.CustomType, c.Content, c.Display, c.Details, c.Timestamp),
        BranchSummaryEntry b => CodingAgentMessages.CreateBranchSummaryMessage(b.Summary, b.FromId, b.Timestamp),
        CompactionEntry c => CodingAgentMessages.CreateCompactionSummaryMessage(c.Summary, c.TokensBefore, c.Timestamp),
        _ => null,
    };

    /// <summary>Walk newest to oldest adding messages within the token budget (0 = unlimited), collecting file operations.</summary>
    public static BranchPreparation PrepareBranchEntries(IReadOnlyList<SessionEntry> entries, long tokenBudget = 0)
    {
        var messages = new List<Message>();
        var fileOps = new FileOperations();
        long totalTokens = 0;

        foreach (var entry in entries)
        {
            if (entry is not BranchSummaryEntry { FromHook: not true, Details: JsonObject details }) continue;
            if (details["readFiles"] is JsonArray read)
            {
                foreach (var f in read) if (PiJson.GetString(f) is { } s) fileOps.Read.Add(s);
            }
            if (details["modifiedFiles"] is JsonArray modified)
            {
                foreach (var f in modified) if (PiJson.GetString(f) is { } s) fileOps.Edited.Add(s);
            }
        }

        for (var i = entries.Count - 1; i >= 0; i--)
        {
            var entry = entries[i];
            if (GetMessageFromEntry(entry) is not { } message) continue;
            CompactionUtils.ExtractFileOpsFromMessage(message, fileOps);
            var tokens = Compactor.EstimateTokens(message);

            if (tokenBudget > 0 && totalTokens + tokens > tokenBudget)
            {
                // Summaries are important context: squeeze them in if there is still some room.
                if (entry is CompactionEntry or BranchSummaryEntry && totalTokens < tokenBudget * 0.9)
                {
                    messages.Insert(0, message);
                    totalTokens += tokens;
                }
                break;
            }
            messages.Insert(0, message);
            totalTokens += tokens;
        }
        return new BranchPreparation(messages, fileOps, totalTokens);
    }

    public static async Task<BranchSummaryResult> GenerateBranchSummaryAsync(
        IReadOnlyList<SessionEntry> entries,
        SummarizationRequest request,
        string? customInstructions = null,
        bool replaceInstructions = false,
        long reserveTokens = 16384)
    {
        var model = request.Model;
        var contextWindow = model.ContextWindow != 0 ? model.ContextWindow : 128000;
        var (messages, fileOps, _) = PrepareBranchEntries(entries, contextWindow - reserveTokens);
        if (messages.Count == 0) return new BranchSummaryResult { Summary = "No content to summarize" };

        var conversationText = CompactionUtils.SerializeConversation(CodingAgentMessages.ConvertToLlm(messages));
        var prompt = BranchSummaryPrompt.Replace("\r\n", "\n");
        var instructions = replaceInstructions && !string.IsNullOrEmpty(customInstructions)
            ? customInstructions
            : !string.IsNullOrEmpty(customInstructions) ? $"{prompt}\n\nAdditional focus: {customInstructions}" : prompt;
        var promptText = $"<conversation>\n{conversationText}\n</conversation>\n\n{instructions}";

        var context = new Context
        {
            SystemPrompt = CompactionUtils.SummarizationSystemPrompt,
            Messages = [new UserMessage { Content = UserContent.FromBlocks([new TextContent(promptText)]), Timestamp = TimeUtil.NowMs() }],
        };
        var maxTokens = model.MaxTokens > 0 ? Math.Min(4096, model.MaxTokens) : 4096;
        var options = new SimpleStreamOptions
        {
            ApiKey = request.ApiKey,
            Headers = request.Headers,
            Env = request.Env,
            CancellationToken = request.CancellationToken,
            MaxTokens = (int)maxTokens,
        };
        var response = await Compactor.CompleteSummarizationAsync(model, context, options, request.StreamFn, request.Retry, request.Callbacks);

        if (response.StopReason == StopReason.Aborted) return new BranchSummaryResult { Aborted = true };
        if (Compactor.GetSummarizationFailure(response, "Branch summarization") is { } failure) return new BranchSummaryResult { Error = failure };
        if (response.Content.Any(b => b is ToolCall)) return new BranchSummaryResult { Error = "Branch summarization attempted to call a tool" };

        var summary = BranchSummaryPreamble + TextUtils.ContentText(response.Content);
        var (readFiles, modifiedFiles) = CompactionUtils.ComputeFileLists(fileOps);
        summary += CompactionUtils.FormatFileOperations(readFiles, modifiedFiles);

        return new BranchSummaryResult
        {
            Summary = string.IsNullOrEmpty(summary) ? "No summary generated" : summary,
            Usage = response.Usage,
            ReadFiles = readFiles,
            ModifiedFiles = modifiedFiles,
        };
    }
}
