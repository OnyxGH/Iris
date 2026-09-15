namespace Iris.Ai.Providers;

/// <summary>Cross-provider message normalization.</summary>
public static class MessageTransformer
{
    private const string NonVisionUserImagePlaceholder = "(image omitted: model does not support images)";
    private const string NonVisionToolImagePlaceholder = "(tool image omitted: model does not support images)";

    private static List<ContentBlock> ReplaceImagesWithPlaceholder(IEnumerable<ContentBlock> content, string placeholder)
    {
        var result = new List<ContentBlock>();
        var previousWasPlaceholder = false;
        foreach (var block in content)
        {
            if (block is ImageContent)
            {
                if (!previousWasPlaceholder) result.Add(new TextContent(placeholder));
                previousWasPlaceholder = true;
                continue;
            }
            result.Add(block);
            previousWasPlaceholder = block is TextContent t && t.Text == placeholder;
        }
        return result;
    }

    private static Message DowngradeUnsupportedImages(Message msg, Model model)
    {
        if (model.SupportsImages) return msg;
        switch (msg)
        {
            case UserMessage { Content.Blocks: not null } user:
            {
                var clone = (UserMessage)user.CloneMessage();
                clone.Content = UserContent.FromBlocks(ReplaceImagesWithPlaceholder(user.Content.Blocks!, NonVisionUserImagePlaceholder));
                return clone;
            }
            case ToolResultMessage tr:
            {
                var clone = (ToolResultMessage)tr.CloneMessage();
                clone.Content = ReplaceImagesWithPlaceholder(tr.Content, NonVisionToolImagePlaceholder);
                return clone;
            }
            default:
                return msg;
        }
    }

    /// <summary>
    /// Normalize tool call IDs for cross-provider compatibility, convert foreign thinking to text, drop errored
    /// assistant turns and synthesize results for orphaned tool calls.
    /// </summary>
    public static List<Message> Transform(
        IReadOnlyList<Message> messages,
        Model model,
        Func<string, Model, AssistantMessage, string>? normalizeToolCallId = null)
    {
        var toolCallIdMap = new Dictionary<string, string>();

        var transformed = new List<Message>(messages.Count);
        foreach (var original in messages)
        {
            var msg = DowngradeUnsupportedImages(original, model);
            switch (msg)
            {
                case UserMessage:
                    transformed.Add(msg);
                    break;
                case ToolResultMessage tr:
                    if (toolCallIdMap.TryGetValue(tr.ToolCallId, out var normalizedId) && normalizedId != tr.ToolCallId)
                    {
                        var clone = (ToolResultMessage)tr.CloneMessage();
                        clone.ToolCallId = normalizedId;
                        transformed.Add(clone);
                    }
                    else
                    {
                        transformed.Add(tr);
                    }
                    break;
                case AssistantMessage assistant:
                {
                    var isSameModel = assistant.Provider == model.Provider && assistant.Api == model.Api && assistant.Model == model.Id;
                    var content = new List<ContentBlock>();
                    foreach (var block in assistant.Content)
                    {
                        switch (block)
                        {
                            case ThinkingContent thinking:
                                if (thinking.Redacted == true)
                                {
                                    if (isSameModel) content.Add(thinking);
                                    break;
                                }
                                if (isSameModel && !string.IsNullOrEmpty(thinking.ThinkingSignature))
                                {
                                    content.Add(thinking);
                                    break;
                                }
                                if (string.IsNullOrWhiteSpace(thinking.Thinking)) break;
                                content.Add(isSameModel ? thinking : new TextContent(thinking.Thinking));
                                break;
                            case TextContent text:
                                content.Add(isSameModel ? text : new TextContent(text.Text));
                                break;
                            case ToolCall toolCall:
                            {
                                var normalized = toolCall;
                                if (!isSameModel && toolCall.ThoughtSignature is not null)
                                {
                                    normalized = toolCall.Clone();
                                    normalized.ThoughtSignature = null;
                                }
                                if (!isSameModel && normalizeToolCallId is not null)
                                {
                                    var newId = normalizeToolCallId(toolCall.Id, model, assistant);
                                    if (newId != toolCall.Id)
                                    {
                                        toolCallIdMap[toolCall.Id] = newId;
                                        if (ReferenceEquals(normalized, toolCall)) normalized = toolCall.Clone();
                                        normalized.Id = newId;
                                    }
                                }
                                content.Add(normalized);
                                break;
                            }
                            default:
                                content.Add(block);
                                break;
                        }
                    }
                    var copy = (AssistantMessage)assistant.CloneMessage();
                    copy.Content = content;
                    transformed.Add(copy);
                    break;
                }
                default:
                    transformed.Add(msg);
                    break;
            }
        }

        var result = new List<Message>(transformed.Count);
        var pendingToolCalls = new List<ToolCall>();
        var existingToolResultIds = new HashSet<string>();

        void InsertSyntheticToolResults()
        {
            if (pendingToolCalls.Count == 0) return;
            foreach (var tc in pendingToolCalls)
            {
                if (!existingToolResultIds.Contains(tc.Id))
                {
                    result.Add(new ToolResultMessage
                    {
                        ToolCallId = tc.Id,
                        ToolName = tc.Name,
                        Content = [new TextContent("No result provided")],
                        IsError = true,
                        Timestamp = TimeUtil.NowMs(),
                    });
                }
            }
            pendingToolCalls = [];
            existingToolResultIds = [];
        }

        foreach (var msg in transformed)
        {
            switch (msg)
            {
                case AssistantMessage assistant:
                    InsertSyntheticToolResults();
                    if (assistant.StopReason is StopReason.Error or StopReason.Aborted) continue;
                    var toolCalls = assistant.Content.OfType<ToolCall>().ToList();
                    if (toolCalls.Count > 0)
                    {
                        pendingToolCalls = toolCalls;
                        existingToolResultIds = [];
                    }
                    result.Add(msg);
                    break;
                case ToolResultMessage tr:
                    existingToolResultIds.Add(tr.ToolCallId);
                    result.Add(msg);
                    break;
                case UserMessage:
                    InsertSyntheticToolResults();
                    result.Add(msg);
                    break;
                default:
                    result.Add(msg);
                    break;
            }
        }

        InsertSyntheticToolResults();
        return result;
    }
}
