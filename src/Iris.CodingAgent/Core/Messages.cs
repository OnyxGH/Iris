using System.Text.Json.Nodes;
using Iris.Ai;
using Iris.Ai.Json;

namespace Iris.CodingAgent.Core;

/// <summary>Message type for bash executions via the ! command.</summary>
public sealed class BashExecutionMessage : Message
{
    public override string Role => "bashExecution";

    public string Command { get; set; } = "";

    public string Output { get; set; } = "";

    public int? ExitCode { get; set; }

    public bool Cancelled { get; set; }

    public bool Truncated { get; set; }

    public string? FullOutputPath { get; set; }

    /// <summary>If true, excluded from LLM context (!! prefix).</summary>
    public bool? ExcludeFromContext { get; set; }
}

/// <summary>Extension-injected message.</summary>
public sealed class CustomMessage : Message
{
    public override string Role => "custom";

    public string CustomType { get; set; } = "";

    public UserContent Content { get; set; } = UserContent.FromText("");

    public bool Display { get; set; }

    public JsonNode? Details { get; set; }
}

public sealed class BranchSummaryMessage : Message
{
    public override string Role => "branchSummary";

    public string Summary { get; set; } = "";

    /// <summary>Entry id the branch came back from (null serialized explicitly).</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)]
    public string? FromId { get; set; }
}

public sealed class CompactionSummaryMessage : Message
{
    public override string Role => "compactionSummary";

    public string Summary { get; set; } = "";

    public long TokensBefore { get; set; }
}

/// <summary>Custom message types and conversion to LLM messages. Port of core/messages.ts.</summary>
public static class CodingAgentMessages
{
    public const string CompactionSummaryPrefix = "The conversation history before this point was compacted into the following summary:\n\n<summary>\n";
    public const string CompactionSummarySuffix = "\n</summary>";
    public const string BranchSummaryPrefix = "The following is a summary of a branch that this conversation came back from:\n\n<summary>\n";
    public const string BranchSummarySuffix = "</summary>";

    static CodingAgentMessages() => Register();

    private static int _registered;

    /// <summary>Register the coding-agent message roles with the JSON converter.</summary>
    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1) return;
        MessageTypeRegistry.Register<BashExecutionMessage>("bashExecution");
        MessageTypeRegistry.Register<CustomMessage>("custom");
        MessageTypeRegistry.Register<BranchSummaryMessage>("branchSummary");
        MessageTypeRegistry.Register<CompactionSummaryMessage>("compactionSummary");
    }

    public static string BashExecutionToText(BashExecutionMessage msg)
    {
        var text = $"Ran `{msg.Command}`\n";
        text += !string.IsNullOrEmpty(msg.Output) ? $"```\n{msg.Output}\n```" : "(no output)";
        if (msg.Cancelled) text += "\n\n(command cancelled)";
        else if (msg.ExitCode is not null and not 0) text += $"\n\nCommand exited with code {msg.ExitCode}";
        if (msg.Truncated && !string.IsNullOrEmpty(msg.FullOutputPath)) text += $"\n\n[Output truncated. Full output: {msg.FullOutputPath}]";
        return text;
    }

    public static long ParseTimestamp(string timestamp) =>
        DateTimeOffset.TryParse(timestamp, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var date)
            ? date.ToUnixTimeMilliseconds()
            : 0;

    public static BranchSummaryMessage CreateBranchSummaryMessage(string summary, string? fromId, string timestamp) =>
        new() { Summary = summary, FromId = fromId, Timestamp = ParseTimestamp(timestamp) };

    public static CompactionSummaryMessage CreateCompactionSummaryMessage(string summary, long tokensBefore, string timestamp) =>
        new() { Summary = summary, TokensBefore = tokensBefore, Timestamp = ParseTimestamp(timestamp) };

    public static CustomMessage CreateCustomMessage(string customType, UserContent content, bool display, JsonNode? details, string timestamp) =>
        new() { CustomType = customType, Content = content, Display = display, Details = details, Timestamp = ParseTimestamp(timestamp) };

    /// <summary>Transform agent messages (including custom types) to LLM-compatible messages.</summary>
    public static List<Message> ConvertToLlm(IEnumerable<Message> messages)
    {
        var result = new List<Message>();
        foreach (var m in messages)
        {
            switch (m)
            {
                case BashExecutionMessage bash:
                    if (bash.ExcludeFromContext == true) break;
                    result.Add(new UserMessage(UserContent.FromBlocks([new TextContent(BashExecutionToText(bash))]), bash.Timestamp));
                    break;
                case CustomMessage custom:
                {
                    var content = custom.Content.Text is not null ? [new TextContent(custom.Content.Text)] : custom.Content.Blocks!;
                    result.Add(new UserMessage(UserContent.FromBlocks(content), custom.Timestamp));
                    break;
                }
                case BranchSummaryMessage branch:
                    result.Add(new UserMessage(UserContent.FromBlocks([new TextContent(BranchSummaryPrefix + branch.Summary + BranchSummarySuffix)]), branch.Timestamp));
                    break;
                case CompactionSummaryMessage compaction:
                    result.Add(new UserMessage(UserContent.FromBlocks([new TextContent(CompactionSummaryPrefix + compaction.Summary + CompactionSummarySuffix)]), compaction.Timestamp));
                    break;
                case UserMessage or AssistantMessage or ToolResultMessage:
                    result.Add(m);
                    break;
            }
        }
        return result;
    }
}
