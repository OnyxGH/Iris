using System.Text.Json.Nodes;
using PiSharp.Agent;
using PiSharp.Ai;
using PiSharp.Ai.Json;
using PiSharp.CodingAgent.Core;
using PiSharp.CodingAgent.Core.Compaction;

namespace PiSharp.CodingAgent.Modes;

/// <summary>
/// Session events as emitted by the JSON and RPC stdout protocols. Port of modes/json-event.ts: message_update drops the
/// cumulative partial message and carries usage plus the delta event.
/// </summary>
public static class JsonEvents
{
    private static JsonNode? Node<T>(T value) => PiJson.ToNode(value);

    private static JsonNode? MessageNode(Message message) => PiJson.ToNode<Message>(message);

    public static JsonObject ToolResultNode(AgentToolResult result)
    {
        var obj = new JsonObject { ["content"] = PiJson.ToNode(result.Content) };
        if (result.Details is not null) obj["details"] = result.Details.DeepClone();
        if (result.Usage is not null) obj["usage"] = Node(result.Usage);
        if (result.AddedToolNames is not null) obj["addedToolNames"] = Node(result.AddedToolNames);
        if (result.Terminate is not null) obj["terminate"] = result.Terminate;
        return obj;
    }

    private static JsonObject AssistantMessageEventNode(AssistantMessageEvent evt)
    {
        var obj = new JsonObject { ["type"] = evt.Type };
        switch (evt)
        {
            case StartEvent:
                break;
            case TextStartEvent e:
                obj["contentIndex"] = e.ContentIndex;
                break;
            case TextDeltaEvent e:
                obj["contentIndex"] = e.ContentIndex;
                obj["delta"] = e.Delta;
                break;
            case TextEndEvent e:
                obj["contentIndex"] = e.ContentIndex;
                obj["content"] = e.Content;
                break;
            case ThinkingStartEvent e:
                obj["contentIndex"] = e.ContentIndex;
                break;
            case ThinkingDeltaEvent e:
                obj["contentIndex"] = e.ContentIndex;
                obj["delta"] = e.Delta;
                break;
            case ThinkingEndEvent e:
                obj["contentIndex"] = e.ContentIndex;
                obj["content"] = e.Content;
                break;
            case ToolCallStartEvent e:
            {
                obj["contentIndex"] = e.ContentIndex;
                if (e.ContentIndex >= e.Partial.Content.Count || e.Partial.Content[e.ContentIndex] is not ToolCall toolCall)
                {
                    throw new InvalidOperationException($"toolcall_start content at index {e.ContentIndex} is not a tool call");
                }
                obj["id"] = toolCall.Id;
                obj["toolName"] = toolCall.Name;
                break;
            }
            case ToolCallDeltaEvent e:
                obj["contentIndex"] = e.ContentIndex;
                obj["delta"] = e.Delta;
                break;
            case ToolCallEndEvent e:
                obj["contentIndex"] = e.ContentIndex;
                obj["toolCall"] = PiJson.ToNode<ContentBlock>(e.ToolCall);
                break;
            case DoneEvent e:
                obj["reason"] = Node(e.Reason);
                obj["message"] = MessageNode(e.Message);
                break;
            case ErrorEvent e:
                obj["reason"] = Node(e.Reason);
                obj["error"] = MessageNode(e.Error);
                break;
        }
        return obj;
    }

    private static JsonNode? CompactionResultNode(CompactionResult? result)
    {
        if (result is null) return null;
        var obj = new JsonObject
        {
            ["summary"] = result.Summary,
            ["firstKeptEntryId"] = result.FirstKeptEntryId,
            ["tokensBefore"] = result.TokensBefore,
        };
        if (result.EstimatedTokensAfter is { } after) obj["estimatedTokensAfter"] = after;
        if (result.Usage is not null) obj["usage"] = Node(result.Usage);
        if (result.Details is not null) obj["details"] = result.Details.DeepClone();
        return obj;
    }

    public static JsonObject ToJson(AgentSessionEvent evt)
    {
        var obj = new JsonObject { ["type"] = evt.Type };
        switch (evt)
        {
            case AgentCoreSessionEvent { Event: var core }:
                switch (core)
                {
                    case MessageUpdateEvent update:
                        if (update.Message is not AssistantMessage assistant) throw new InvalidOperationException("message_update message is not an assistant message");
                        obj["usage"] = Node(assistant.Usage);
                        obj["assistantMessageEvent"] = AssistantMessageEventNode(update.AssistantMessageEvent);
                        break;
                    case MessageStartEvent e:
                        obj["message"] = MessageNode(e.Message);
                        break;
                    case MessageEndEvent e:
                        obj["message"] = MessageNode(e.Message);
                        break;
                    case TurnEndEvent e:
                        obj["message"] = MessageNode(e.Message);
                        obj["toolResults"] = new JsonArray(e.ToolResults.Select(r => MessageNode(r)).ToArray());
                        break;
                    case ToolExecutionStartEvent e:
                        obj["toolCallId"] = e.ToolCallId;
                        obj["toolName"] = e.ToolName;
                        obj["args"] = e.Args.DeepClone();
                        break;
                    case ToolExecutionUpdateEvent e:
                        obj["toolCallId"] = e.ToolCallId;
                        obj["toolName"] = e.ToolName;
                        obj["args"] = e.Args.DeepClone();
                        obj["partialResult"] = ToolResultNode(e.PartialResult);
                        break;
                    case ToolExecutionEndEvent e:
                        obj["toolCallId"] = e.ToolCallId;
                        obj["toolName"] = e.ToolName;
                        obj["result"] = ToolResultNode(e.Result);
                        obj["isError"] = e.IsError;
                        break;
                }
                break;
            case SessionAgentEndEvent e:
                obj["messages"] = new JsonArray(e.Messages.Select(MessageNode).ToArray());
                obj["willRetry"] = e.WillRetry;
                break;
            case QueueUpdateEvent e:
                obj["steering"] = Node(e.Steering);
                obj["followUp"] = Node(e.FollowUp);
                break;
            case CompactionStartEvent e:
                obj["reason"] = e.Reason;
                break;
            case EntryAppendedEvent e:
                obj["entry"] = PiJson.ToNode<FileEntry>(e.Entry);
                break;
            case SessionInfoChangedEvent e:
                if (e.Name is not null) obj["name"] = e.Name;
                break;
            case ThinkingLevelChangedEvent e:
                obj["level"] = e.Level.ToWire();
                break;
            case CompactionEndEvent e:
                obj["reason"] = e.Reason;
                if (e.Result is not null) obj["result"] = CompactionResultNode(e.Result);
                obj["aborted"] = e.Aborted;
                obj["willRetry"] = e.WillRetry;
                if (e.ErrorMessage is not null) obj["errorMessage"] = e.ErrorMessage;
                break;
            case AutoRetryStartEvent e:
                obj["attempt"] = e.Attempt;
                obj["maxAttempts"] = e.MaxAttempts;
                obj["delayMs"] = e.DelayMs;
                obj["errorMessage"] = e.ErrorMessage;
                break;
            case AutoRetryEndEvent e:
                obj["success"] = e.Success;
                obj["attempt"] = e.Attempt;
                if (e.FinalError is not null) obj["finalError"] = e.FinalError;
                break;
            case SummarizationRetryScheduledEvent e:
                obj["attempt"] = e.Attempt;
                obj["maxAttempts"] = e.MaxAttempts;
                obj["delayMs"] = e.DelayMs;
                obj["errorMessage"] = e.ErrorMessage;
                break;
            case SummarizationRetryAttemptStartEvent e:
                obj["source"] = e.Source;
                if (e.Reason is not null) obj["reason"] = e.Reason;
                break;
            case BashExecutionUpdateEvent e:
                if (e.Id is not null) obj["id"] = e.Id;
                obj["delta"] = e.Delta;
                break;
        }
        return obj;
    }
}
