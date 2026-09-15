using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Iris.Ai.Json;
using Iris.Ai.Utils;

namespace Iris.Ai.Providers;

public sealed class OpenAIResponsesOptions : StreamOptions
{
    public ThinkingLevel? ReasoningEffort { get; set; }

    /// <summary>auto | detailed | concise.</summary>
    public string? ReasoningSummary { get; set; }

    public string? ServiceTier { get; set; }

    public JsonNode? ToolChoice { get; set; }
}

public sealed record ResolvedResponsesCompat(
    bool SupportsDeveloperRole,
    string SessionAffinityFormat,
    bool SupportsLongCacheRetention,
    bool SupportsStrictMode,
    bool SupportsOpenAIGrammarTools,
    bool SupportsAdditionalTools,
    bool SupportsToolSearch,
    bool SupportsExplicitPromptCacheMode,
    bool SupportsMaxOutputTokens);

public sealed class ConvertResponsesToolsOptions
{
    public bool? Strict { get; init; }
    public bool StrictSpecified { get; init; }
    public bool? SupportsStrictMode { get; init; }
    public bool? SupportsOpenAIGrammarTools { get; init; }
    public bool DeferLoading { get; init; }
}

public sealed class ConvertResponsesMessagesOptions
{
    public bool IncludeSystemPrompt { get; init; } = true;
    public IReadOnlyDictionary<string, string>? GrammarToolInputProperties { get; init; }
    public IReadOnlyDictionary<string, Tool>? DeferredTools { get; init; }
    /// <summary>"additional-tools" | "tool-search".</summary>
    public string? DeferredToolsMode { get; init; }
    public ConvertResponsesToolsOptions? ToolOptions { get; init; }
}

public sealed class ResponsesStreamOptions
{
    public string? ServiceTier { get; init; }
    public IReadOnlyDictionary<string, string>? GrammarToolInputProperties { get; init; }
    public Func<string?, string?, string?>? ResolveServiceTier { get; init; }
    public Action<Usage, string?>? ApplyServiceTierPricing { get; init; }
}

/// <summary>Shared OpenAI Responses conversion and stream processing.</summary>
public static class OpenAIResponsesShared
{
    private static readonly Regex NonIdChars = new("[^a-zA-Z0-9_-]", RegexOptions.Compiled);
    private static readonly Regex TrailingUnderscores = new("_+$", RegexOptions.Compiled);

    private static string? Str(JsonObject? obj, string name) => obj?[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static long? Long(JsonObject? obj, string name) => obj?[name] is JsonValue v && IrisJson.TryGetNumber(v, out var d) ? (long)d : null;

    public static string EncodeTextSignatureV1(string id, string? phase)
    {
        var payload = new JsonObject { ["v"] = 1, ["id"] = id };
        if (!string.IsNullOrEmpty(phase)) payload["phase"] = phase;
        return IrisJson.Stringify(payload);
    }

    public static (string Id, string? Phase)? ParseTextSignature(string? signature)
    {
        if (string.IsNullOrEmpty(signature)) return null;
        if (signature.StartsWith('{'))
        {
            try
            {
                if (JsonNode.Parse(signature) is JsonObject parsed && Long(parsed, "v") == 1 && Str(parsed, "id") is { } id)
                {
                    var phase = Str(parsed, "phase");
                    return (id, phase is "commentary" or "final_answer" ? phase : null);
                }
            }
            catch (JsonException)
            {
            }
        }
        return (signature, null);
    }

    private static JsonNode ConvertToolResultOutput(Model model, IReadOnlyList<ContentBlock> content)
    {
        var textResult = string.Join("\n", content.OfType<TextContent>().Select(c => c.Text));
        var images = content.OfType<ImageContent>().ToList();
        var hasText = textResult.Length > 0;
        if (images.Count == 0 || !model.SupportsImages)
        {
            return TextUtils.SanitizeSurrogates(hasText ? textResult : images.Count > 0 ? "(see attached image)" : "(no tool output)");
        }
        var output = new JsonArray();
        if (hasText) output.Add(new JsonObject { ["type"] = "input_text", ["text"] = TextUtils.SanitizeSurrogates(textResult) });
        foreach (var image in images)
        {
            output.Add(new JsonObject { ["type"] = "input_image", ["detail"] = "auto", ["image_url"] = $"data:{image.MimeType};base64,{image.Data}" });
        }
        return output;
    }

    public static JsonArray ConvertMessages(Model model, Context context, IReadOnlySet<string> allowedToolCallProviders, ConvertResponsesMessagesOptions? options = null)
    {
        var messages = new JsonArray();
        var loadedToolNames = new HashSet<string>();

        string NormalizeIdPart(string part)
        {
            var sanitized = NonIdChars.Replace(part, "_");
            var normalized = sanitized.Length > 64 ? sanitized[..64] : sanitized;
            return TrailingUnderscores.Replace(normalized, "");
        }

        string BuildForeignResponsesItemId(string itemId)
        {
            var normalized = $"fc_{TextUtils.ShortHash(itemId)}";
            return normalized.Length > 64 ? normalized[..64] : normalized;
        }

        string NormalizeToolCallId(string id, Model _, AssistantMessage source)
        {
            if (!allowedToolCallProviders.Contains(model.Provider)) return NormalizeIdPart(id);
            if (!id.Contains('|')) return NormalizeIdPart(id);
            var parts = id.Split('|');
            var normalizedCallId = NormalizeIdPart(parts[0]);
            var isForeign = source.Provider != model.Provider || source.Api != model.Api;
            var itemId = parts.Length > 1 ? parts[1] : "";
            var normalizedItemId = isForeign ? BuildForeignResponsesItemId(itemId) : NormalizeIdPart(itemId);
            if (!normalizedItemId.StartsWith("fc_", StringComparison.Ordinal)) normalizedItemId = NormalizeIdPart($"fc_{normalizedItemId}");
            return $"{normalizedCallId}|{normalizedItemId}";
        }

        var transformed = MessageTransformer.Transform(context.Messages, model, NormalizeToolCallId);

        if ((options?.IncludeSystemPrompt ?? true) && !string.IsNullOrEmpty(context.SystemPrompt))
        {
            var supportsDeveloperRole = model.Compat?["supportsDeveloperRole"] is JsonValue dv && dv.TryGetValue<bool>(out var d) ? d : (bool?)null;
            var role = model.Reasoning && supportsDeveloperRole != false ? "developer" : "system";
            messages.Add(new JsonObject { ["role"] = role, ["content"] = TextUtils.SanitizeSurrogates(context.SystemPrompt) });
        }

        var msgIndex = 0;
        foreach (var msg in transformed)
        {
            switch (msg)
            {
                case UserMessage user:
                    if (user.Content.Text is not null)
                    {
                        messages.Add(new JsonObject
                        {
                            ["role"] = "user",
                            ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = TextUtils.SanitizeSurrogates(user.Content.Text) }),
                        });
                    }
                    else
                    {
                        var content = new JsonArray();
                        foreach (var item in user.Content.Blocks!)
                        {
                            if (item is TextContent t) content.Add(new JsonObject { ["type"] = "input_text", ["text"] = TextUtils.SanitizeSurrogates(t.Text) });
                            else if (item is ImageContent img) content.Add(new JsonObject { ["type"] = "input_image", ["detail"] = "auto", ["image_url"] = $"data:{img.MimeType};base64,{img.Data}" });
                        }
                        if (content.Count == 0)
                        {
                            msgIndex++;
                            continue;
                        }
                        messages.Add(new JsonObject { ["role"] = "user", ["content"] = content });
                    }
                    break;
                case AssistantMessage assistant:
                {
                    var output = new List<JsonNode>();
                    var isSameProviderAndApi = assistant.Provider == model.Provider && assistant.Api == model.Api;
                    var isSameModel = isSameProviderAndApi && assistant.Model == model.Id;
                    var isDifferentModel = isSameProviderAndApi && assistant.Model != model.Id;
                    var textBlockIndex = 0;

                    foreach (var block in assistant.Content)
                    {
                        switch (block)
                        {
                            case ThinkingContent thinking:
                                if (!string.IsNullOrEmpty(thinking.ThinkingSignature)) output.Add(JsonNode.Parse(thinking.ThinkingSignature)!);
                                break;
                            case TextContent text:
                            {
                                var parsed = ParseTextSignature(text.TextSignature);
                                var fallbackId = textBlockIndex == 0 ? $"msg_pi_{msgIndex}" : $"msg_pi_{msgIndex}_{textBlockIndex}";
                                textBlockIndex++;
                                var msgId = parsed?.Id;
                                if (string.IsNullOrEmpty(msgId)) msgId = fallbackId;
                                else if (msgId.Length > 64) msgId = $"msg_{TextUtils.ShortHash(msgId)}";
                                var item = new JsonObject
                                {
                                    ["type"] = "message",
                                    ["role"] = "assistant",
                                    ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = TextUtils.SanitizeSurrogates(text.Text), ["annotations"] = new JsonArray() }),
                                    ["status"] = "completed",
                                    ["id"] = msgId,
                                };
                                if (parsed?.Phase is not null) item["phase"] = parsed.Value.Phase;
                                output.Add(item);
                                break;
                            }
                            case ToolCall toolCall:
                            {
                                var parts = toolCall.Id.Split('|');
                                var callId = parts[0];
                                string? itemId = parts.Length > 1 ? parts[1] : null;
                                string? customProp = null;
                                var hasCustom = options?.GrammarToolInputProperties?.TryGetValue(toolCall.Name, out customProp) == true;
                                if ((isDifferentModel && itemId?.StartsWith("fc_", StringComparison.Ordinal) == true)
                                    || (!hasCustom && itemId?.StartsWith("fc_", StringComparison.Ordinal) != true))
                                {
                                    itemId = null;
                                }
                                var canReplayNamespace = isSameModel || options?.DeferredTools?.ContainsKey(toolCall.Name) == true;
                                JsonObject item;
                                if (hasCustom)
                                {
                                    item = new JsonObject { ["type"] = "custom_tool_call" };
                                    if (itemId is not null) item["id"] = itemId;
                                    item["call_id"] = callId;
                                    item["name"] = toolCall.Name;
                                    item["input"] = TextUtils.SanitizeSurrogates(ConstrainedSampling.GetGrammarToolInput(toolCall.Name, toolCall.Arguments, customProp!));
                                }
                                else
                                {
                                    item = new JsonObject { ["type"] = "function_call" };
                                    if (itemId is not null) item["id"] = itemId;
                                    item["call_id"] = callId;
                                    item["name"] = toolCall.Name;
                                    item["arguments"] = IrisJson.Stringify(toolCall.Arguments);
                                }
                                if (canReplayNamespace && toolCall.Namespace is not null) item["namespace"] = toolCall.Namespace;
                                output.Add(item);
                                break;
                            }
                        }
                    }
                    if (output.Count == 0)
                    {
                        msgIndex++;
                        continue;
                    }
                    foreach (var o in output) messages.Add(o);
                    break;
                }
                case ToolResultMessage tr:
                {
                    var callId = tr.ToolCallId.Split('|')[0];
                    var output = ConvertToolResultOutput(model, tr.Content);
                    var isCustom = options?.GrammarToolInputProperties?.ContainsKey(tr.ToolName) == true;
                    messages.Add(new JsonObject
                    {
                        ["type"] = isCustom ? "custom_tool_call_output" : "function_call_output",
                        ["call_id"] = callId,
                        ["output"] = output,
                    });

                    var deferredTools = new List<Tool>();
                    foreach (var name in tr.AddedToolNames ?? [])
                    {
                        if (options?.DeferredTools is null || !options.DeferredTools.TryGetValue(name, out var tool) || loadedToolNames.Contains(name)) continue;
                        loadedToolNames.Add(name);
                        deferredTools.Add(tool);
                    }
                    if (deferredTools.Count > 0 && options?.DeferredToolsMode == "additional-tools")
                    {
                        messages.Add(new JsonObject { ["type"] = "additional_tools", ["role"] = "developer", ["tools"] = ConvertTools(deferredTools, options.ToolOptions) });
                    }
                    else if (deferredTools.Count > 0 && options?.DeferredToolsMode == "tool-search")
                    {
                        var names = deferredTools.Select(t => t.Name).ToList();
                        var searchCallId = $"pi_tool_load_{TextUtils.ShortHash($"{tr.ToolCallId}:{string.Join(",", names)}")}";
                        messages.Add(new JsonObject
                        {
                            ["type"] = "tool_search_call",
                            ["call_id"] = searchCallId,
                            ["execution"] = "client",
                            ["status"] = "completed",
                            ["arguments"] = new JsonObject { ["query"] = string.Join(" ", names), ["limit"] = names.Count },
                        });
                        var toolOptions = options.ToolOptions;
                        messages.Add(new JsonObject
                        {
                            ["type"] = "tool_search_output",
                            ["call_id"] = searchCallId,
                            ["execution"] = "client",
                            ["status"] = "completed",
                            ["tools"] = ConvertTools(deferredTools, new ConvertResponsesToolsOptions
                            {
                                Strict = toolOptions?.Strict,
                                StrictSpecified = toolOptions?.StrictSpecified ?? false,
                                SupportsStrictMode = toolOptions?.SupportsStrictMode,
                                SupportsOpenAIGrammarTools = toolOptions?.SupportsOpenAIGrammarTools,
                                DeferLoading = true,
                            }),
                        });
                    }
                    break;
                }
            }
            msgIndex++;
        }

        return messages;
    }

    public static JsonArray ConvertTools(IReadOnlyList<Tool> tools, ConvertResponsesToolsOptions? options = null)
    {
        bool? defaultStrict = options?.StrictSpecified == true ? options.Strict : false;
        var supportsStrictMode = options?.SupportsStrictMode ?? true;
        var supportsGrammar = options?.SupportsOpenAIGrammarTools ?? false;
        var result = new JsonArray();
        foreach (var tool in tools)
        {
            var grammar = ConstrainedSampling.ResolveGrammarConstrainedSampling(tool, supportsGrammar);
            if (grammar is not null)
            {
                var custom = new JsonObject
                {
                    ["type"] = "custom",
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["format"] = new JsonObject { ["type"] = "grammar", ["syntax"] = grammar.Format, ["definition"] = grammar.Definition },
                };
                if (options?.DeferLoading == true) custom["defer_loading"] = true;
                result.Add(custom);
                continue;
            }
            var constrained = ConstrainedSampling.ResolveJsonSchemaStrictSampling(tool, supportsStrictMode);
            var strict = constrained ?? defaultStrict;
            var functionTool = new JsonObject
            {
                ["type"] = "function",
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["parameters"] = ConstrainedSampling.GetJsonSchemaToolParameters(tool, strict == true).DeepClone(),
            };
            if (options?.DeferLoading == true) functionTool["defer_loading"] = true;
            if (supportsStrictMode) functionTool["strict"] = strict is null ? null : JsonValue.Create(strict.Value);
            result.Add(functionTool);
        }
        return result;
    }

    private sealed class Slot
    {
        public required string Type { get; init; }
        public required ContentBlock Block { get; init; }
        public int ContentIndex { get; init; }
        public string? PartialJson { get; set; }
        public string? CustomInputProperty { get; set; }
        public GrammarToolInputJsonBuffer? JsonBuffer { get; set; }
    }

    public static async Task ProcessStreamAsync(
        IAsyncEnumerable<JsonObject> events,
        AssistantMessage output,
        AssistantMessageEventStream stream,
        Model model,
        ResponsesStreamOptions? options = null)
    {
        var sawTerminal = false;
        var slots = new Dictionary<long, Slot>();
        var reasoningBlocksById = new Dictionary<string, ThinkingContent>();

        void ApplyMessagePhaseStopReason(JsonObject item)
        {
            if (Str(item, "type") == "message" && Str(item, "phase") == "final_answer") output.StopReason = StopReason.Stop;
        }

        Slot? GetSlot(long outputIndex, string type) => slots.TryGetValue(outputIndex, out var slot) && slot.Type == type ? slot : null;

        void PushToolCallDelta(Slot slot, string? delta)
        {
            if (delta is null) return;
            stream.Push(new ToolCallDeltaEvent(slot.ContentIndex, delta, output));
        }

        string GetCustomInput(Slot slot) =>
            slot.CustomInputProperty is not null && ((ToolCall)slot.Block).Arguments[slot.CustomInputProperty] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

        string? AppendCustomInput(Slot slot, string nextInput, bool close)
        {
            if (slot.CustomInputProperty is null || slot.JsonBuffer is null) return null;
            var delta = ConstrainedSampling.AppendGrammarToolInputJsonDelta(slot.JsonBuffer, slot.CustomInputProperty, nextInput, close);
            ((ToolCall)slot.Block).Arguments = new JsonObject { [slot.CustomInputProperty] = nextInput };
            return delta;
        }

        Slot? CreateSlot(long outputIndex, JsonObject item)
        {
            switch (Str(item, "type"))
            {
                case "reasoning":
                {
                    var block = new ThinkingContent("");
                    output.Content.Add(block);
                    var slot = new Slot { Type = "thinking", Block = block, ContentIndex = output.Content.Count - 1 };
                    slots[outputIndex] = slot;
                    stream.Push(new ThinkingStartEvent(slot.ContentIndex, output));
                    return slot;
                }
                case "message":
                {
                    ApplyMessagePhaseStopReason(item);
                    var block = new TextContent("");
                    output.Content.Add(block);
                    var slot = new Slot { Type = "text", Block = block, ContentIndex = output.Content.Count - 1 };
                    slots[outputIndex] = slot;
                    stream.Push(new TextStartEvent(slot.ContentIndex, output));
                    return slot;
                }
                case "function_call":
                {
                    var block = new ToolCall
                    {
                        Id = $"{Str(item, "call_id")}|{Str(item, "id")}",
                        Name = Str(item, "name") ?? "",
                        Arguments = new JsonObject(),
                        Namespace = Str(item, "namespace"),
                    };
                    output.Content.Add(block);
                    var slot = new Slot { Type = "toolCall", Block = block, ContentIndex = output.Content.Count - 1, PartialJson = Str(item, "arguments") ?? "" };
                    slots[outputIndex] = slot;
                    stream.Push(new ToolCallStartEvent(slot.ContentIndex, output));
                    return slot;
                }
                case "custom_tool_call":
                {
                    var name = Str(item, "name") ?? "";
                    var inputProperty = options?.GrammarToolInputProperties?.GetValueOrDefault(name) ?? "input";
                    var block = new ToolCall
                    {
                        Id = $"{Str(item, "call_id")}|{Str(item, "id")}",
                        Name = name,
                        Arguments = new JsonObject { [inputProperty] = Str(item, "input") ?? "" },
                        Namespace = Str(item, "namespace"),
                    };
                    output.Content.Add(block);
                    var slot = new Slot
                    {
                        Type = "toolCall",
                        Block = block,
                        ContentIndex = output.Content.Count - 1,
                        CustomInputProperty = inputProperty,
                        JsonBuffer = new GrammarToolInputJsonBuffer(),
                    };
                    slots[outputIndex] = slot;
                    stream.Push(new ToolCallStartEvent(slot.ContentIndex, output));
                    return slot;
                }
                default:
                    return null;
            }
        }

        void BackfillReasoningSignatures(JsonArray? responseOutput)
        {
            foreach (var item in responseOutput?.OfType<JsonObject>() ?? [])
            {
                if (Str(item, "type") != "reasoning" || string.IsNullOrEmpty(Str(item, "encrypted_content"))) continue;
                if (Str(item, "id") is not { } id || !reasoningBlocksById.TryGetValue(id, out var block) || string.IsNullOrEmpty(block.ThinkingSignature)) continue;
                if (JsonNode.Parse(block.ThinkingSignature) is not JsonObject stored || !string.IsNullOrEmpty(Str(stored, "encrypted_content"))) continue;
                stored["encrypted_content"] = Str(item, "encrypted_content");
                block.ThinkingSignature = IrisJson.Stringify(stored);
            }
        }

        void FinalizeResponse(JsonObject? response)
        {
            sawTerminal = true;
            BackfillReasoningSignatures(response?["output"] as JsonArray);
            if (Str(response, "id") is { } id) output.ResponseId = id;
            if (response?["usage"] is JsonObject usage)
            {
                var inputDetails = usage["input_tokens_details"] as JsonObject;
                var cached = Long(inputDetails, "cached_tokens") ?? 0;
                var cacheWrite = Long(inputDetails, "cache_write_tokens") ?? 0;
                output.Usage = new Usage
                {
                    Input = Math.Max(0, (Long(usage, "input_tokens") ?? 0) - cached - cacheWrite),
                    Output = Long(usage, "output_tokens") ?? 0,
                    CacheRead = cached,
                    CacheWrite = cacheWrite,
                    Reasoning = Long(usage["output_tokens_details"] as JsonObject, "reasoning_tokens") ?? 0,
                    TotalTokens = Long(usage, "total_tokens") ?? 0,
                };
            }
            ModelUtils.CalculateCost(model, output.Usage);
            if (options?.ApplyServiceTierPricing is not null)
            {
                var responseTier = Str(response, "service_tier");
                var tier = options.ResolveServiceTier is not null ? options.ResolveServiceTier(responseTier, options.ServiceTier) : responseTier ?? options.ServiceTier;
                options.ApplyServiceTierPricing(output.Usage, tier);
            }
            var status = Str(response, "status");
            var incompleteReason = Str(response?["incomplete_details"] as JsonObject, "reason");
            output.RawStopReason = incompleteReason is not null ? $"{status}.{incompleteReason}" : status;
            var (stopReason, errorMessage) = MapStopReason(status, incompleteReason);
            output.StopReason = stopReason;
            output.ErrorMessage = errorMessage;
            if (output.Content.Any(b => b is ToolCall) && output.StopReason == StopReason.Stop) output.StopReason = StopReason.ToolUse;
        }

        await foreach (var evt in events)
        {
            var type = Str(evt, "type");
            var outputIndex = Long(evt, "output_index") ?? -1;
            switch (type)
            {
                case "response.created":
                    if (Str(evt["response"] as JsonObject, "id") is { } createdId) output.ResponseId = createdId;
                    break;
                case "response.output_item.added":
                    if (evt["item"] is JsonObject addedItem) CreateSlot(outputIndex, addedItem);
                    break;
                case "response.reasoning_summary_text.delta":
                case "response.reasoning_text.delta":
                {
                    var slot = GetSlot(outputIndex, "thinking");
                    if (slot is null) break;
                    var delta = Str(evt, "delta") ?? "";
                    ((ThinkingContent)slot.Block).Thinking += delta;
                    stream.Push(new ThinkingDeltaEvent(slot.ContentIndex, delta, output));
                    break;
                }
                case "response.reasoning_summary_part.done":
                {
                    var slot = GetSlot(outputIndex, "thinking");
                    if (slot is null) break;
                    ((ThinkingContent)slot.Block).Thinking += "\n\n";
                    stream.Push(new ThinkingDeltaEvent(slot.ContentIndex, "\n\n", output));
                    break;
                }
                case "response.output_text.delta":
                case "response.refusal.delta":
                {
                    var slot = GetSlot(outputIndex, "text");
                    if (slot is null) break;
                    var delta = Str(evt, "delta") ?? "";
                    ((TextContent)slot.Block).Text += delta;
                    stream.Push(new TextDeltaEvent(slot.ContentIndex, delta, output));
                    break;
                }
                case "response.function_call_arguments.delta":
                {
                    var slot = GetSlot(outputIndex, "toolCall");
                    if (slot?.PartialJson is null) break;
                    var delta = Str(evt, "delta") ?? "";
                    slot.PartialJson += delta;
                    ((ToolCall)slot.Block).Arguments = JsonParse.ParseStreamingJson(slot.PartialJson);
                    PushToolCallDelta(slot, delta);
                    break;
                }
                case "response.function_call_arguments.done":
                {
                    var slot = GetSlot(outputIndex, "toolCall");
                    if (slot?.PartialJson is null) break;
                    var previous = slot.PartialJson;
                    var arguments = Str(evt, "arguments") ?? "";
                    slot.PartialJson = arguments;
                    ((ToolCall)slot.Block).Arguments = JsonParse.ParseStreamingJson(arguments);
                    if (arguments.StartsWith(previous, StringComparison.Ordinal))
                    {
                        var delta = arguments[previous.Length..];
                        if (delta.Length > 0) PushToolCallDelta(slot, delta);
                    }
                    break;
                }
                case "response.custom_tool_call_input.delta":
                {
                    var slot = GetSlot(outputIndex, "toolCall");
                    if (slot?.CustomInputProperty is null) break;
                    PushToolCallDelta(slot, AppendCustomInput(slot, GetCustomInput(slot) + (Str(evt, "delta") ?? ""), false));
                    break;
                }
                case "response.custom_tool_call_input.done":
                {
                    var slot = GetSlot(outputIndex, "toolCall");
                    if (slot?.CustomInputProperty is null) break;
                    PushToolCallDelta(slot, AppendCustomInput(slot, Str(evt, "input") ?? "", true));
                    break;
                }
                case "response.output_item.done":
                {
                    if (evt["item"] is not JsonObject item) break;
                    ApplyMessagePhaseStopReason(item);
                    var slot = slots.TryGetValue(outputIndex, out var existing) ? existing : CreateSlot(outputIndex, item);
                    var itemType = Str(item, "type");
                    if (itemType == "reasoning" && slot?.Type == "thinking")
                    {
                        var block = (ThinkingContent)slot.Block;
                        var summaryText = string.Join("\n\n", (item["summary"] as JsonArray)?.OfType<JsonObject>().Select(s => Str(s, "text") ?? "") ?? []);
                        var contentText = string.Join("\n\n", (item["content"] as JsonArray)?.OfType<JsonObject>().Select(s => Str(s, "text") ?? "") ?? []);
                        block.Thinking = summaryText.Length > 0 ? summaryText : contentText.Length > 0 ? contentText : block.Thinking;
                        block.ThinkingSignature = IrisJson.Stringify(item);
                        if (Str(item, "id") is { } reasoningId) reasoningBlocksById[reasoningId] = block;
                        stream.Push(new ThinkingEndEvent(slot.ContentIndex, block.Thinking, output));
                        slots.Remove(outputIndex);
                    }
                    else if (itemType == "message" && slot?.Type == "text")
                    {
                        var block = (TextContent)slot.Block;
                        block.Text = string.Concat((item["content"] as JsonArray)?.OfType<JsonObject>()
                            .Select(c => Str(c, "type") == "output_text" ? Str(c, "text") : Str(c, "refusal")) ?? []);
                        block.TextSignature = EncodeTextSignatureV1(Str(item, "id") ?? "", Str(item, "phase"));
                        stream.Push(new TextEndEvent(slot.ContentIndex, block.Text, output));
                        slots.Remove(outputIndex);
                    }
                    else if (itemType == "function_call" && slot?.Type == "toolCall" && slot.PartialJson is not null)
                    {
                        var block = (ToolCall)slot.Block;
                        var args = Str(item, "arguments");
                        block.Arguments = JsonParse.ParseStreamingJson(!string.IsNullOrEmpty(args) ? args : !string.IsNullOrEmpty(slot.PartialJson) ? slot.PartialJson : "{}");
                        if (Str(item, "namespace") is { } ns) block.Namespace = ns;
                        slot.PartialJson = null;
                        stream.Push(new ToolCallEndEvent(slot.ContentIndex, block, output));
                        slots.Remove(outputIndex);
                    }
                    else if (itemType == "custom_tool_call" && slot?.Type == "toolCall" && slot.CustomInputProperty is not null)
                    {
                        var block = (ToolCall)slot.Block;
                        PushToolCallDelta(slot, AppendCustomInput(slot, Str(item, "input") ?? GetCustomInput(slot), true));
                        if (Str(item, "namespace") is { } ns) block.Namespace = ns;
                        slot.CustomInputProperty = null;
                        stream.Push(new ToolCallEndEvent(slot.ContentIndex, block, output));
                        slots.Remove(outputIndex);
                    }
                    break;
                }
                case "response.completed":
                case "response.incomplete":
                    FinalizeResponse(evt["response"] as JsonObject);
                    break;
                case "error":
                    throw new InvalidOperationException($"Error Code {evt["code"]?.ToString() ?? "undefined"}: {Str(evt, "message") ?? "undefined"}");
                case "response.failed":
                {
                    sawTerminal = true;
                    var response = evt["response"] as JsonObject;
                    output.RawStopReason = Str(response, "status");
                    var error = response?["error"] as JsonObject;
                    var details = response?["incomplete_details"] as JsonObject;
                    var msg = error is not null
                        ? $"{(Str(error, "code") is { Length: > 0 } code ? code : "unknown")}: {(Str(error, "message") is { Length: > 0 } m ? m : "no message")}"
                        : Str(details, "reason") is { Length: > 0 } reason ? $"incomplete: {reason}" : "Unknown error (no error details in response)";
                    throw new InvalidOperationException(msg);
                }
            }
        }
        if (!sawTerminal) throw new InvalidOperationException("OpenAI Responses stream ended before a terminal response event");
    }

    private static (StopReason, string?) MapStopReason(string? status, string? incompleteReason)
    {
        if (status is null) return (StopReason.Stop, null);
        return status switch
        {
            "completed" => (StopReason.Stop, null),
            "incomplete" when incompleteReason == "max_output_tokens" => (StopReason.Length, null),
            "incomplete" => (StopReason.Error, incompleteReason is not null ? $"Response incomplete: {incompleteReason}" : "Response incomplete without a provider reason"),
            "failed" or "cancelled" => (StopReason.Error, null),
            "in_progress" or "queued" => (StopReason.Stop, null),
            _ => throw new InvalidOperationException($"Unhandled stop reason: {status}"),
        };
    }

    /// <summary>Parse an OpenAI SDK style SSE stream into JSON event objects.</summary>
    public static async IAsyncEnumerable<JsonObject> ReadOpenAIEventsAsync(Stream body, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var sse in ProviderHttp.ReadSseAsync(body, ct))
        {
            if (sse.Data.StartsWith("[DONE]", StringComparison.Ordinal)) yield break;
            if (sse.Event is not null && sse.Event.StartsWith("thread.", StringComparison.Ordinal)) continue;
            if (string.IsNullOrWhiteSpace(sse.Data)) continue;
            JsonNode? node;
            try
            {
                node = JsonNode.Parse(sse.Data);
            }
            catch (JsonException)
            {
                throw new InvalidOperationException($"Could not parse message into JSON: {sse.Data}");
            }
            if (node is not JsonObject obj) continue;
            if (IrisJson.IsTruthy(obj["error"]))
            {
                var errorNode = obj["error"]!;
                var message = errorNode is JsonObject eo && eo["message"] is JsonValue mv && mv.TryGetValue<string>(out var ms) ? ms : IrisJson.Stringify(errorNode);
                throw new ProviderHttpException(null, null, message, errorNode);
            }
            yield return obj;
        }
    }
}

/// <summary>OpenAI Responses streaming adapter.</summary>
public sealed class OpenAIResponsesApi : IApiStreams
{
    public static readonly OpenAIResponsesApi Instance = new();

    private static readonly HashSet<string> OpenAIToolCallProviders = ["openai", "openai-codex", "opencode"];
    private const int MinOutputTokens = 16;

    private static bool HasHeader(IReadOnlyDictionary<string, string?>? headers, string name) =>
        headers is not null && headers.Any(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(kv.Value));

    private static string GetClientApiKey(string provider, string? apiKey, IReadOnlyDictionary<string, string?>? headers)
    {
        if (!string.IsNullOrEmpty(apiKey)) return apiKey;
        if (HasHeader(headers, "authorization") || HasHeader(headers, "cf-aig-authorization")) return "unused";
        throw new InvalidOperationException($"No API key for provider: {provider}");
    }

    public static ResolvedResponsesCompat GetCompat(Model model)
    {
        var c = model.GetCompat<OpenAIResponsesCompat>();
        var detectedAffinity = model.Provider == "openrouter" || model.BaseUrl.Contains("openrouter.ai") ? "openrouter" : "openai";
        return new ResolvedResponsesCompat(
            c.SupportsDeveloperRole ?? true,
            c.SessionAffinityFormat ?? detectedAffinity,
            c.SupportsLongCacheRetention ?? true,
            c.SupportsStrictMode ?? false,
            c.SupportsOpenAIGrammarTools ?? false,
            c.SupportsAdditionalTools ?? false,
            c.SupportsToolSearch ?? false,
            c.SupportsExplicitPromptCacheMode ?? false,
            c.SupportsMaxOutputTokens ?? true);
    }

    public AssistantMessageEventStream StreamSimple(Model model, Context context, SimpleStreamOptions? options = null)
    {
        GetClientApiKey(model.Provider, options?.ApiKey, options?.Headers);
        var opts = SimpleOptions.BuildBaseOptions(new OpenAIResponsesOptions(), model, context, options, options?.ApiKey);
        opts.ToolChoice = options?.ToolChoice switch
        {
            ToolChoice.Auto => "auto",
            ToolChoice.None => "none",
            _ => null,
        };
        ThinkingLevel? clamped = options?.Reasoning is { } r ? ModelUtils.ClampThinkingLevel(model, r) : null;
        opts.ReasoningEffort = clamped == ThinkingLevel.Off ? null : clamped;
        return Stream(model, context, opts);
    }

    public AssistantMessageEventStream Stream(Model model, Context context, StreamOptions? streamOptions = null)
    {
        var options = streamOptions as OpenAIResponsesOptions
            ?? (streamOptions is null ? new OpenAIResponsesOptions() : streamOptions.CopyTo(new OpenAIResponsesOptions()));
        var stream = new AssistantMessageEventStream();
        _ = Task.Run(() => RunAsync(model, context, options, stream));
        return stream;
    }

    private static async Task RunAsync(Model model, Context context, OpenAIResponsesOptions options, AssistantMessageEventStream stream)
    {
        var ct = options.CancellationToken;
        var output = AssistantMessage.CreateEmpty(model);
        try
        {
            var apiKey = GetClientApiKey(model.Provider, options.ApiKey, options.Headers);
            var cacheRetention = SimpleOptions.ResolveCacheRetention(options.CacheRetention, options.Env);
            var cacheSessionId = cacheRetention == CacheRetention.None ? null : options.SessionId;
            var compat = GetCompat(model);
            var grammar = ConstrainedSampling.CreateGrammarToolInputProperties(context.Tools, compat.SupportsOpenAIGrammarTools);

            var headerList = new List<KeyValuePair<string, string?>>
            {
                new("Authorization", $"Bearer {apiKey}"),
                new("Accept", "application/json"),
                new("User-Agent", IrisUserAgent.Get()),
            };
            if (model.Headers is not null) headerList.AddRange(ProviderHttp.AsNullable(model.Headers)!);
            if (model.Provider == "github-copilot")
            {
                headerList.AddRange(GithubCopilotHeaders.BuildDynamicHeaders(context.Messages, GithubCopilotHeaders.HasVisionInput(context.Messages))
                    .Select(kv => new KeyValuePair<string, string?>(kv.Key, kv.Value)));
            }
            if (cacheSessionId is not null)
            {
                if (compat.SessionAffinityFormat == "openrouter")
                {
                    headerList.Add(new("x-session-id", cacheSessionId));
                }
                else
                {
                    if (compat.SessionAffinityFormat == "openai") headerList.Add(new("session_id", cacheSessionId));
                    headerList.Add(new("x-client-request-id", cacheSessionId));
                }
            }
            var headers = ProviderHttp.MergeHeaders(headerList, options.Headers);

            JsonNode payload = BuildParams(model, context, options, compat, grammar);
            if (options.OnPayload is not null)
            {
                var next = await options.OnPayload(payload, model);
                if (next is not null) payload = next;
            }

            var url = model.BaseUrl.TrimEnd('/') + "/responses";
            using var response = await ProviderHttp.PostJsonAsync(url, payload, headers, options, ProviderErrorStyle.OpenAI, ct);
            if (options.OnResponse is not null)
            {
                await options.OnResponse(new ProviderResponse((int)response.StatusCode, ProviderHttp.HeadersToRecord(response)), model);
            }
            stream.Push(new StartEvent(output));

            await using var body = await response.Content.ReadAsStreamAsync(ct);
            await OpenAIResponsesShared.ProcessStreamAsync(OpenAIResponsesShared.ReadOpenAIEventsAsync(body, ct), output, stream, model, new ResponsesStreamOptions
            {
                ServiceTier = options.ServiceTier,
                GrammarToolInputProperties = grammar,
                ApplyServiceTierPricing = (usage, tier) => ApplyServiceTierPricing(usage, tier, model),
            });

            ct.ThrowIfCancellationRequested();
            if (output.StopReason == StopReason.Pending) throw new InvalidOperationException("OpenAI Responses stream ended without a stop reason");
            if (output.StopReason is StopReason.Aborted or StopReason.Error) throw new InvalidOperationException(output.ErrorMessage ?? "An unknown error occurred");

            stream.Push(new DoneEvent(output.StopReason, output));
            stream.End();
        }
        catch (Exception error)
        {
            var aborted = ct.IsCancellationRequested;
            output.StopReason = aborted ? StopReason.Aborted : StopReason.Error;
            output.ErrorMessage = aborted && error is OperationCanceledException
                ? "Request was aborted"
                : ProviderErrors.Format(ProviderErrors.Normalize(error), $"{(model.Provider == "openai" ? "OpenAI" : model.Provider)} API error");
            stream.Push(new ErrorEvent(output.StopReason, output));
            stream.End();
        }
    }

    internal static JsonObject BuildParams(Model model, Context context, OpenAIResponsesOptions? options, ResolvedResponsesCompat? compatIn = null, IReadOnlyDictionary<string, string>? grammar = null)
    {
        var compat = compatIn ?? GetCompat(model);
        grammar ??= ConstrainedSampling.CreateGrammarToolInputProperties(context.Tools, compat.SupportsOpenAIGrammarTools);
        var deferredMode = compat.SupportsAdditionalTools ? "additional-tools" : compat.SupportsToolSearch ? "tool-search" : null;
        var (immediate, deferred) = AnthropicMessagesApi.SplitDeferredTools(context.Messages, context.Tools, deferredMode is not null, n => n);
        var toolOptions = new ConvertResponsesToolsOptions { SupportsStrictMode = compat.SupportsStrictMode, SupportsOpenAIGrammarTools = compat.SupportsOpenAIGrammarTools };
        var messages = OpenAIResponsesShared.ConvertMessages(model, context, OpenAIToolCallProviders, new ConvertResponsesMessagesOptions
        {
            GrammarToolInputProperties = grammar,
            DeferredTools = deferred,
            DeferredToolsMode = deferredMode,
            ToolOptions = toolOptions,
        });

        var cacheRetention = SimpleOptions.ResolveCacheRetention(options?.CacheRetention, options?.Env);
        var p = new JsonObject
        {
            ["model"] = model.Id,
            ["input"] = messages,
            ["stream"] = true,
        };
        if (cacheRetention != CacheRetention.None && options?.SessionId is { } sessionId)
        {
            var runes = sessionId.EnumerateRunes().ToList();
            p["prompt_cache_key"] = runes.Count <= 64 ? sessionId : string.Concat(runes.Take(64).Select(r => r.ToString()));
        }
        if (cacheRetention == CacheRetention.Long && compat.SupportsLongCacheRetention && !compat.SupportsExplicitPromptCacheMode) p["prompt_cache_retention"] = "24h";
        if (compat.SupportsExplicitPromptCacheMode)
        {
            if (cacheRetention == CacheRetention.None) p["prompt_cache_options"] = new JsonObject { ["mode"] = "explicit" };
            else if (cacheRetention == CacheRetention.Long && compat.SupportsLongCacheRetention) p["prompt_cache_options"] = new JsonObject { ["ttl"] = "30m" };
        }
        p["store"] = false;

        if (options?.MaxTokens is > 0 && compat.SupportsMaxOutputTokens) p["max_output_tokens"] = Math.Max(options.MaxTokens.Value, MinOutputTokens);
        if (options?.Temperature is not null) p["temperature"] = options.Temperature;
        if (options?.ServiceTier is not null) p["service_tier"] = options.ServiceTier;
        if (immediate.Count > 0) p["tools"] = OpenAIResponsesShared.ConvertTools(immediate, toolOptions);
        if (options?.ToolChoice is not null) p["tool_choice"] = options.ToolChoice.DeepClone();

        if (model.Reasoning)
        {
            if (options?.ReasoningEffort is not null || !string.IsNullOrEmpty(options?.ReasoningSummary))
            {
                string effort;
                if (options?.ReasoningEffort is { } level)
                {
                    effort = model.TryGetThinkingMapping(level, out var mapped) && mapped is not null ? mapped : level.ToWire();
                }
                else
                {
                    effort = "medium";
                }
                p["reasoning"] = new JsonObject { ["effort"] = effort, ["summary"] = string.IsNullOrEmpty(options?.ReasoningSummary) ? "auto" : options.ReasoningSummary };
                p["include"] = new JsonArray("reasoning.encrypted_content");
            }
            else if (model.Provider != "github-copilot" && !(model.TryGetThinkingMapping(ThinkingLevel.Off, out var off) && off is null))
            {
                model.TryGetThinkingMapping(ThinkingLevel.Off, out var offValue);
                p["reasoning"] = new JsonObject { ["effort"] = offValue ?? "none" };
            }
            if (model.Provider == "xai") p["include"] = new JsonArray("reasoning.encrypted_content");
        }

        if (options?.SamplingParams is not null)
        {
            foreach (var (k, v) in options.SamplingParams) p[k] = v?.DeepClone();
        }
        return p;
    }

    private static void ApplyServiceTierPricing(Usage usage, string? serviceTier, Model model)
    {
        var multiplier = serviceTier switch
        {
            "flex" => 0.5,
            "priority" => model.Id == "gpt-5.5" ? 2.5 : 2,
            _ => 1,
        };
        if (multiplier == 1) return;
        usage.Cost.Input *= multiplier;
        usage.Cost.Output *= multiplier;
        usage.Cost.CacheRead *= multiplier;
        usage.Cost.CacheWrite *= multiplier;
        usage.Cost.Total = usage.Cost.Input + usage.Cost.Output + usage.Cost.CacheRead + usage.Cost.CacheWrite;
    }
}
