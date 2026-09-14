using System.Text.Json.Nodes;
using PiSharp.Ai;
using PiSharp.Ai.Json;
using PiSharp.Ai.Utils;

namespace PiSharp.CodingAgent.Core.Compaction;

public sealed class FileOperations
{
    public HashSet<string> Read { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Written { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Edited { get; } = new(StringComparer.Ordinal);

    // Insertion order is irrelevant: file lists are sorted before use.
}

/// <summary>Shared utilities for compaction and branch summarization. Port of core/compaction/utils.ts.</summary>
public static class CompactionUtils
{
    private const int ToolResultMaxChars = 2000;

    public const string SummarizationSystemPrompt =
        "You are a context summarization assistant. Your task is to read a conversation between a user and an AI assistant, then produce a structured summary following the exact format specified.\n\n" +
        "Do NOT continue the conversation. Do NOT respond to any questions in the conversation. ONLY output the structured summary.";

    /// <summary>Record read/write/edit tool calls (with a string path argument) from an assistant message.</summary>
    public static void ExtractFileOpsFromMessage(Message message, FileOperations fileOps)
    {
        if (message is not AssistantMessage assistant) return;
        foreach (var call in assistant.Content.OfType<ToolCall>())
        {
            if (PiJson.GetString(call.Arguments?["path"]) is not { Length: > 0 } path) continue;
            switch (call.Name)
            {
                case "read":
                    fileOps.Read.Add(path);
                    break;
                case "write":
                    fileOps.Written.Add(path);
                    break;
                case "edit":
                    fileOps.Edited.Add(path);
                    break;
            }
        }
    }

    /// <summary>Files only read (not modified) and modified files, each sorted by UTF-16 code units like Array.sort.</summary>
    public static (List<string> ReadFiles, List<string> ModifiedFiles) ComputeFileLists(FileOperations fileOps)
    {
        var modified = new HashSet<string>(fileOps.Edited.Concat(fileOps.Written), StringComparer.Ordinal);
        var readOnly = fileOps.Read.Where(f => !modified.Contains(f)).Order(StringComparer.Ordinal).ToList();
        return (readOnly, modified.Order(StringComparer.Ordinal).ToList());
    }

    public static string FormatFileOperations(IReadOnlyList<string> readFiles, IReadOnlyList<string> modifiedFiles)
    {
        var sections = new List<string>();
        if (readFiles.Count > 0) sections.Add($"<read-files>\n{string.Join("\n", readFiles)}\n</read-files>");
        if (modifiedFiles.Count > 0) sections.Add($"<modified-files>\n{string.Join("\n", modifiedFiles)}\n</modified-files>");
        return sections.Count == 0 ? "" : $"\n\n{string.Join("\n\n", sections)}";
    }

    private static string TruncateForSummary(string text, int maxChars)
    {
        if (text.Length <= maxChars) return text;
        return $"{text[..maxChars]}\n\n[... {text.Length - maxChars} more characters truncated]";
    }

    /// <summary>JSON.stringify for tool arguments.</summary>
    public static string Stringify(JsonNode? node) => node is null ? "null" : PiJson.Stringify(node);

    /// <summary>Serialize LLM messages to plain text so the summarizer does not continue the conversation.</summary>
    public static string SerializeConversation(IEnumerable<Message> messages)
    {
        var parts = new List<string>();
        foreach (var msg in messages)
        {
            switch (msg)
            {
                case UserMessage user:
                {
                    var content = TextUtils.ContentText(user.Content, "");
                    if (content.Length > 0) parts.Add($"[User]: {content}");
                    break;
                }
                case AssistantMessage assistant:
                {
                    var thinking = assistant.Content.OfType<ThinkingContent>().Select(t => t.Thinking).ToList();
                    var toolCalls = assistant.Content.OfType<ToolCall>()
                        .Select(c => $"{c.Name}({string.Join(", ", (c.Arguments ?? []).Select(kv => $"{kv.Key}={Stringify(kv.Value)}"))})")
                        .ToList();
                    if (thinking.Count > 0) parts.Add($"[Assistant thinking]: {string.Join("\n", thinking)}");
                    if (assistant.Content.Any(b => b is TextContent)) parts.Add($"[Assistant]: {TextUtils.ContentText(assistant.Content)}");
                    if (toolCalls.Count > 0) parts.Add($"[Assistant tool calls]: {string.Join("; ", toolCalls)}");
                    break;
                }
                case ToolResultMessage toolResult:
                {
                    var content = TextUtils.ContentText(toolResult.Content, "");
                    if (content.Length > 0) parts.Add($"[Tool result]: {TruncateForSummary(content, ToolResultMaxChars)}");
                    break;
                }
            }
        }
        return string.Join("\n\n", parts);
    }
}
