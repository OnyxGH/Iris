using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Iris.Ai.Json;
using Iris.Ai.Utils;

namespace Iris.Ai.Providers;

public sealed class OpenAICompletionsOptions : StreamOptions
{
    /// <summary>"auto", "none", "required" or a JSON tool choice object.</summary>
    public JsonNode? ToolChoice { get; set; }

    public ThinkingLevel? ReasoningEffort { get; set; }

    public ThinkingBudgets? ThinkingBudgets { get; set; }
}

/// <summary>Resolved compat with defaults applied.</summary>
internal sealed record ResolvedCompletionsCompat
{
    public bool SupportsStore { get; init; }
    public bool SupportsDeveloperRole { get; init; }
    public bool SupportsReasoningEffort { get; init; }
    public bool SupportsUsageInStreaming { get; init; }
    public bool SupportsFinishReason { get; init; }
    public string MaxTokensField { get; init; } = "max_completion_tokens";
    public bool RequiresToolResultName { get; init; }
    public bool RequiresAssistantAfterToolResult { get; init; }
    public bool RequiresThinkingAsText { get; init; }
    public bool RequiresReasoningContentOnAssistantMessages { get; init; }
    public string ThinkingFormat { get; init; } = "openai";
    public JsonObject OpenRouterRouting { get; init; } = new();
    public VercelGatewayRouting VercelGatewayRouting { get; init; } = new();
    public JsonObject ChatTemplateKwargs { get; init; } = new();
    public JsonObject ChatTemplateArgs { get; init; } = new();
    public bool ZaiToolStream { get; init; }
    public bool? SupportsThinkingTokenBudget { get; init; }
    public string? ThinkingTokenBudgetField { get; init; }
    public bool SupportsStrictMode { get; init; }
    public bool SupportsOpenAIGrammarTools { get; init; }
    public string? CacheControlFormat { get; init; }
    public bool SendSessionAffinityHeaders { get; init; }
    public string? DeferredToolsMode { get; init; }
    public string SessionAffinityFormat { get; init; } = "openai";
    public bool SupportsLongCacheRetention { get; init; }
    public double? VllmPriority { get; init; }
}

/// <summary>OpenAI Chat Completions streaming adapter.</summary>
public sealed class OpenAICompletionsApi : IApiStreams
{
    public static readonly OpenAICompletionsApi Instance = new();

    private static readonly string[] ReasoningFields = ["reasoning_content", "reasoning", "reasoning_text"];

    public AssistantMessageEventStream StreamSimple(Model model, Context context, SimpleStreamOptions? options = null)
    {
        GetClientApiKey(model.Provider, options?.ApiKey, options?.Headers);
        var opts = SimpleOptions.BuildBaseOptions(new OpenAICompletionsOptions(), model, context, options, options?.ApiKey);
        opts.ToolChoice = options?.ToolChoice switch
        {
            ToolChoice.Auto => "auto",
            ToolChoice.None => "none",
            _ => null,
        };
        ThinkingLevel? clamped = options?.Reasoning is { } r ? ModelUtils.ClampThinkingLevel(model, r) : null;
        opts.ReasoningEffort = clamped == ThinkingLevel.Off ? null : clamped;
        opts.ThinkingBudgets = options?.ThinkingBudgets;
        return Stream(model, context, opts);
    }

    public AssistantMessageEventStream Stream(Model model, Context context, StreamOptions? streamOptions = null)
    {
        var options = streamOptions as OpenAICompletionsOptions
            ?? (streamOptions is null ? new OpenAICompletionsOptions() : streamOptions.CopyTo(new OpenAICompletionsOptions()));
        var stream = new AssistantMessageEventStream();
        _ = Task.Run(() => RunAsync(model, context, options, stream));
        return stream;
    }

    private static bool HasHeader(IReadOnlyDictionary<string, string?>? headers, string name)
    {
        if (headers is null) return false;
        foreach (var (key, value) in headers)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value)) return true;
        }
        return false;
    }

    private static string GetClientApiKey(string provider, string? apiKey, IReadOnlyDictionary<string, string?>? headers)
    {
        if (!string.IsNullOrEmpty(apiKey)) return apiKey;
        if (HasHeader(headers, "authorization") || HasHeader(headers, "cf-aig-authorization")) return "unused";
        throw new InvalidOperationException($"No API key for provider: {provider}");
    }

    private sealed class StreamingToolCall
    {
        public required ToolCall Block { get; init; }
        public string? PartialArgs { get; set; }
        public string? CustomInputProperty { get; set; }
        public GrammarToolInputJsonBuffer? JsonBuffer { get; set; }
        public int? StreamIndex { get; set; }
    }

    private static async Task RunAsync(Model model, Context context, OpenAICompletionsOptions options, AssistantMessageEventStream stream)
    {
        var ct = options.CancellationToken;
        var output = AssistantMessage.CreateEmpty(model);
        List<JsonObject>? streamedReasoningDetails = null;
        var toolCallStates = new Dictionary<ToolCall, StreamingToolCall>(ReferenceEqualityComparer.Instance);

        void ApplyStreamedReasoningDetails(ThinkingContent block)
        {
            if (streamedReasoningDetails is not null) block.ThinkingSignature = IrisJson.Stringify(new JsonArray(streamedReasoningDetails.Select(d => (JsonNode)d.DeepClone()).ToArray()));
        }

        try
        {
            var apiKey = GetClientApiKey(model.Provider, options.ApiKey, options.Headers);
            var compat = GetCompat(model);
            var grammarToolInputProperties = ConstrainedSampling.CreateGrammarToolInputProperties(context.Tools, compat.SupportsOpenAIGrammarTools);
            var cacheRetention = SimpleOptions.ResolveCacheRetention(options.CacheRetention, options.Env);
            var cacheSessionId = cacheRetention == CacheRetention.None ? null : options.SessionId;
            var headers = BuildHeaders(model, context, apiKey, options.Headers, cacheSessionId, compat);
            JsonNode payload = BuildParams(model, context, options, compat, cacheRetention, grammarToolInputProperties);
            if (options.OnPayload is not null)
            {
                var next = await options.OnPayload(payload, model);
                if (next is not null) payload = next;
            }

            var url = model.BaseUrl.TrimEnd('/') + "/chat/completions";
            using var response = await ProviderHttp.PostJsonAsync(url, payload, headers, options, ProviderErrorStyle.OpenAI, ct);
            if (options.OnResponse is not null)
            {
                await options.OnResponse(new ProviderResponse((int)response.StatusCode, ProviderHttp.HeadersToRecord(response)), model);
            }
            stream.Push(new StartEvent(output));

            TextContent? textBlock = null;
            ThinkingContent? thinkingBlock = null;
            var hasFinishReason = false;
            var toolCallBlocksByIndex = new Dictionary<int, StreamingToolCall>();
            var toolCallBlocksById = new Dictionary<string, StreamingToolCall>();
            var blocks = output.Content;
            int IndexOf(ContentBlock block) => blocks.IndexOf(block);

            string GetCustomToolCallInput(StreamingToolCall state)
            {
                if (state.CustomInputProperty is null) return "";
                return state.Block.Arguments[state.CustomInputProperty] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
            }

            string? AppendCustomToolCallInput(StreamingToolCall state, string nextInput, bool close)
            {
                if (state.CustomInputProperty is null || state.JsonBuffer is null) return null;
                var delta = ConstrainedSampling.AppendGrammarToolInputJsonDelta(state.JsonBuffer, state.CustomInputProperty, nextInput, close);
                state.Block.Arguments = new JsonObject { [state.CustomInputProperty] = nextInput };
                return delta;
            }

            void FinishBlock(ContentBlock block)
            {
                var contentIndex = IndexOf(block);
                if (contentIndex == -1) return;
                switch (block)
                {
                    case TextContent text:
                        stream.Push(new TextEndEvent(contentIndex, text.Text, output));
                        break;
                    case ThinkingContent thinking:
                        ApplyStreamedReasoningDetails(thinking);
                        stream.Push(new ThinkingEndEvent(contentIndex, thinking.Thinking, output));
                        break;
                    case ToolCall toolCall:
                        var state = toolCallStates[toolCall];
                        if (state.CustomInputProperty is not null)
                        {
                            var delta = AppendCustomToolCallInput(state, GetCustomToolCallInput(state), true);
                            if (delta is not null) stream.Push(new ToolCallDeltaEvent(contentIndex, delta, output));
                        }
                        else
                        {
                            toolCall.Arguments = JsonParse.ParseStreamingJson(state.PartialArgs);
                        }
                        stream.Push(new ToolCallEndEvent(contentIndex, toolCall, output));
                        break;
                }
            }

            TextContent EnsureTextBlock()
            {
                if (textBlock is null)
                {
                    textBlock = new TextContent("");
                    blocks.Add(textBlock);
                    stream.Push(new TextStartEvent(IndexOf(textBlock), output));
                }
                return textBlock;
            }

            ThinkingContent EnsureThinkingBlock(string thinkingSignature)
            {
                if (thinkingBlock is null)
                {
                    thinkingBlock = new ThinkingContent("") { ThinkingSignature = thinkingSignature };
                    blocks.Add(thinkingBlock);
                    stream.Push(new ThinkingStartEvent(IndexOf(thinkingBlock), output));
                }
                return thinkingBlock;
            }

            StreamingToolCall EnsureToolCallBlock(JsonObject toolCall)
            {
                int? streamIndex = toolCall["index"] is JsonValue iv && iv.TryGetValue<int>(out var idx) ? idx : null;
                var function = toolCall["function"] as JsonObject;
                var custom = toolCall["custom"] as JsonObject;
                var name = GetString(function, "name") ?? GetString(custom, "name") ?? "";
                var id = GetString(toolCall, "id");
                StreamingToolCall? state = null;
                if (streamIndex is not null) toolCallBlocksByIndex.TryGetValue(streamIndex.Value, out state);
                if (state is null && !string.IsNullOrEmpty(id)) toolCallBlocksById.TryGetValue(id, out state);
                if (state is null)
                {
                    string? customInputProperty = custom is not null && function is null
                        ? grammarToolInputProperties.GetValueOrDefault(name) ?? "input"
                        : null;
                    var hasCustomInput = customInputProperty is not null;
                    var block = new ToolCall
                    {
                        Id = id ?? "",
                        Name = name,
                        Arguments = hasCustomInput ? new JsonObject { [customInputProperty!] = "" } : new JsonObject(),
                    };
                    state = new StreamingToolCall
                    {
                        Block = block,
                        PartialArgs = hasCustomInput ? null : "",
                        CustomInputProperty = customInputProperty,
                        JsonBuffer = hasCustomInput ? new GrammarToolInputJsonBuffer() : null,
                        StreamIndex = streamIndex,
                    };
                    toolCallStates[block] = state;
                    if (streamIndex is not null) toolCallBlocksByIndex[streamIndex.Value] = state;
                    if (!string.IsNullOrEmpty(id)) toolCallBlocksById[id] = state;
                    blocks.Add(block);
                    stream.Push(new ToolCallStartEvent(IndexOf(block), output));
                }
                if (streamIndex is not null && state.StreamIndex is null)
                {
                    state.StreamIndex = streamIndex;
                    toolCallBlocksByIndex[streamIndex.Value] = state;
                }
                if (!string.IsNullOrEmpty(id)) toolCallBlocksById[id] = state;
                if (string.IsNullOrEmpty(state.Block.Name) && !string.IsNullOrEmpty(name)) state.Block.Name = name;
                if (custom is not null && function is null && state.CustomInputProperty is null)
                {
                    var prop = grammarToolInputProperties.GetValueOrDefault(state.Block.Name) ?? "input";
                    state.Block.Arguments = new JsonObject { [prop] = "" };
                    state.CustomInputProperty = prop;
                    state.JsonBuffer = new GrammarToolInputJsonBuffer();
                    state.PartialArgs = null;
                }
                return state;
            }

            await using var body = await response.Content.ReadAsStreamAsync(ct);
            await foreach (var sse in ProviderHttp.ReadSseAsync(body, ct))
            {
                if (sse.Data.StartsWith("[DONE]", StringComparison.Ordinal)) break;
                if (sse.Event is not null && sse.Event.StartsWith("thread.", StringComparison.Ordinal)) continue;
                if (string.IsNullOrWhiteSpace(sse.Data)) continue;

                JsonNode? chunkNode;
                try
                {
                    chunkNode = JsonNode.Parse(sse.Data);
                }
                catch (JsonException)
                {
                    throw new InvalidOperationException($"Could not parse message into JSON: {sse.Data}");
                }
                if (chunkNode is not JsonObject chunk) continue;
                if (IrisJson.IsTruthy(chunk["error"]))
                {
                    var errorNode = chunk["error"]!;
                    var message = errorNode is JsonObject eo && eo["message"] is JsonValue mv && mv.TryGetValue<string>(out var ms)
                        ? ms
                        : IrisJson.Stringify(errorNode);
                    throw new ProviderHttpException(null, null, message, errorNode);
                }

                if (string.IsNullOrEmpty(output.ResponseId) && GetString(chunk, "id") is { Length: > 0 } chunkId) output.ResponseId = chunkId;
                if (GetString(chunk, "model") is { Length: > 0 } chunkModel && chunkModel != model.Id && string.IsNullOrEmpty(output.ResponseModel))
                {
                    output.ResponseModel = chunkModel;
                }
                if (chunk["usage"] is JsonObject usageObj) output.Usage = ParseChunkUsage(usageObj, model);

                var choice = chunk["choices"] is JsonArray choices && choices.Count > 0 ? choices[0] as JsonObject : null;
                if (choice is null) continue;

                if (chunk["usage"] is null && choice["usage"] is JsonObject choiceUsage) output.Usage = ParseChunkUsage(choiceUsage, model);

                if (GetString(choice, "finish_reason") is { } finishReason)
                {
                    output.RawStopReason = finishReason;
                    var (stopReason, errorMessage) = MapStopReason(finishReason);
                    output.StopReason = stopReason;
                    if (errorMessage is not null) output.ErrorMessage = errorMessage;
                    hasFinishReason = true;
                }

                if (choice["delta"] is not JsonObject delta) continue;

                if (GetString(delta, "content") is { Length: > 0 } contentDelta)
                {
                    var block = EnsureTextBlock();
                    block.Text += contentDelta;
                    stream.Push(new TextDeltaEvent(IndexOf(block), contentDelta, output));
                }

                string? foundReasoningField = null;
                foreach (var field in ReasoningFields)
                {
                    if (GetString(delta, field) is { Length: > 0 })
                    {
                        foundReasoningField = field;
                        break;
                    }
                }
                if (foundReasoningField is not null)
                {
                    var reasoningDelta = GetString(delta, foundReasoningField)!;
                    var signature = model.Provider == "opencode-go" && foundReasoningField == "reasoning" ? "reasoning_content" : foundReasoningField;
                    var block = EnsureThinkingBlock(signature);
                    block.Thinking += reasoningDelta;
                    stream.Push(new ThinkingDeltaEvent(IndexOf(block), reasoningDelta, output));
                }

                if (delta["tool_calls"] is JsonArray toolCalls)
                {
                    foreach (var toolCallNode in toolCalls)
                    {
                        if (toolCallNode is not JsonObject toolCall) continue;
                        var state = EnsureToolCallBlock(toolCall);
                        var id = GetString(toolCall, "id");
                        if (string.IsNullOrEmpty(state.Block.Id) && !string.IsNullOrEmpty(id))
                        {
                            state.Block.Id = id;
                            toolCallBlocksById[id] = state;
                        }
                        var function = toolCall["function"] as JsonObject;
                        var custom = toolCall["custom"] as JsonObject;
                        var name = GetString(function, "name") ?? GetString(custom, "name");
                        if (string.IsNullOrEmpty(state.Block.Name) && !string.IsNullOrEmpty(name)) state.Block.Name = name;

                        var argsDelta = "";
                        if (GetString(function, "arguments") is { Length: > 0 } arguments)
                        {
                            argsDelta = arguments;
                            state.PartialArgs = (state.PartialArgs ?? "") + arguments;
                            state.Block.Arguments = JsonParse.ParseStreamingJson(state.PartialArgs);
                        }
                        else if (GetString(custom, "input") is { Length: > 0 } customInput)
                        {
                            var nextInput = GetCustomToolCallInput(state) + customInput;
                            argsDelta = AppendCustomToolCallInput(state, nextInput, false) ?? "";
                        }
                        stream.Push(new ToolCallDeltaEvent(IndexOf(state.Block), argsDelta, output));
                    }
                }

                if (delta["reasoning_details"] is JsonArray reasoningDetails)
                {
                    foreach (var detail in reasoningDetails)
                    {
                        if (detail is not JsonObject detailObj || !IsOpenAIReasoningDetail(detailObj)) continue;
                        EnsureThinkingBlock("");
                        streamedReasoningDetails ??= [];
                        AppendOpenAIReasoningDetail(streamedReasoningDetails, detailObj);
                    }
                }
            }

            foreach (var block in blocks.ToList()) FinishBlock(block);

            ct.ThrowIfCancellationRequested();
            if (output.StopReason == StopReason.Aborted) throw new InvalidOperationException("Request was aborted");
            if (!hasFinishReason && !compat.SupportsFinishReason)
            {
                output.StopReason = output.Content.Any(b => b is ToolCall) ? StopReason.ToolUse : StopReason.Stop;
            }
            if (output.StopReason == StopReason.Error)
            {
                throw new InvalidOperationException(output.ErrorMessage ?? "Provider returned an error stop reason");
            }
            if ((compat.SupportsFinishReason && !hasFinishReason) || output.StopReason == StopReason.Pending)
            {
                throw new InvalidOperationException("Stream ended without finish_reason");
            }

            stream.Push(new DoneEvent(output.StopReason, output));
            stream.End();
        }
        catch (Exception error)
        {
            foreach (var block in output.Content)
            {
                if (block is ThinkingContent thinking) ApplyStreamedReasoningDetails(thinking);
            }
            var aborted = ct.IsCancellationRequested;
            output.StopReason = aborted ? StopReason.Aborted : StopReason.Error;
            output.ErrorMessage = aborted && error is OperationCanceledException
                ? "Request was aborted"
                : ProviderErrors.FormatException(error);
            if (error is ProviderHttpException { Error: JsonObject eo } && eo["metadata"] is JsonObject meta && meta["raw"] is { } raw)
            {
                var rawText = raw is JsonValue rv && rv.TryGetValue<string>(out var rs) ? rs : IrisJson.Stringify(raw);
                if (!output.ErrorMessage.Contains(rawText, StringComparison.Ordinal)) output.ErrorMessage += "\n" + rawText;
            }
            stream.Push(new ErrorEvent(output.StopReason, output));
            stream.End();
        }
    }

    private static string? GetString(JsonObject? obj, string name) =>
        obj?[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static Dictionary<string, string> BuildHeaders(Model model, Context context, string apiKey,IReadOnlyDictionary<string, string?>? optionsHeaders, string? sessionId, ResolvedCompletionsCompat compat)
    {
        var headers = new List<KeyValuePair<string, string?>>
        {
            new("Authorization", $"Bearer {apiKey}"),
            new("User-Agent", IrisUserAgent.Get()),
            new("Accept", "application/json"),
        };
        if (model.Headers is not null) headers.AddRange(ProviderHttp.AsNullable(model.Headers)!);
        if (model.Provider == "github-copilot")
        {
            headers.AddRange(GithubCopilotHeaders.BuildDynamicHeaders(context.Messages, GithubCopilotHeaders.HasVisionInput(context.Messages))
                .Select(kv => new KeyValuePair<string, string?>(kv.Key, kv.Value)));
        }
        if (sessionId is not null && compat.SendSessionAffinityHeaders)
        {
            if (compat.SessionAffinityFormat == "openrouter")
            {
                headers.Add(new("x-session-id", sessionId));
            }
            else
            {
                if (compat.SessionAffinityFormat == "openai") headers.Add(new("session_id", sessionId));
                headers.Add(new("x-client-request-id", sessionId));
                headers.Add(new("x-session-affinity", sessionId));
            }
        }
        return ProviderHttp.MergeHeaders(headers, optionsHeaders);
    }

    internal static JsonObject BuildParams(
        Model model,
        Context context,
        OpenAICompletionsOptions? options,
        ResolvedCompletionsCompat? compatIn = null,
        CacheRetention? cacheRetentionIn = null,
        IReadOnlyDictionary<string, string>? grammarToolInputProperties = null)
    {
        var compat = compatIn ?? GetCompat(model);
        var cacheRetention = cacheRetentionIn ?? SimpleOptions.ResolveCacheRetention(options?.CacheRetention, options?.Env);
        grammarToolInputProperties ??= ConstrainedSampling.CreateGrammarToolInputProperties(context.Tools, compat.SupportsOpenAIGrammarTools);
        var messages = ConvertMessages(model, context, compat, grammarToolInputProperties);
        var cacheControl = GetCompatCacheControl(compat, cacheRetention);

        var p = new JsonObject
        {
            ["model"] = model.Id,
            ["messages"] = messages,
            ["stream"] = true,
        };

        var usePromptCacheKey = (model.BaseUrl.Contains("api.openai.com", StringComparison.Ordinal) && cacheRetention != CacheRetention.None)
            || (cacheRetention == CacheRetention.Long && compat.SupportsLongCacheRetention);
        if (usePromptCacheKey && ClampPromptCacheKey(options?.SessionId) is { } cacheKey) p["prompt_cache_key"] = cacheKey;
        if (cacheRetention == CacheRetention.Long && compat.SupportsLongCacheRetention) p["prompt_cache_retention"] = "24h";

        if (compat.SupportsUsageInStreaming) p["stream_options"] = new JsonObject { ["include_usage"] = true };
        if (compat.SupportsStore) p["store"] = false;

        if (options?.MaxTokens is > 0)
        {
            if (compat.MaxTokensField == "max_tokens") p["max_tokens"] = options.MaxTokens;
            else p["max_completion_tokens"] = options.MaxTokens;
        }

        if (options?.Temperature is not null) p["temperature"] = options.Temperature;

        var deferredToolNames = compat.DeferredToolsMode == "kimi" ? GetDeferredToolNames(context.Messages) : [];
        var activeTools = context.Tools?.Where(t => !deferredToolNames.Contains(t.Name)).ToList();
        JsonArray? tools = null;
        if (activeTools is { Count: > 0 })
        {
            tools = ConvertTools(activeTools, compat);
            p["tools"] = tools;
            if (compat.ZaiToolStream) p["tool_stream"] = true;
        }
        else if (HasToolHistory(context.Messages))
        {
            tools = new JsonArray();
            p["tools"] = tools;
        }

        if (cacheControl is not null) ApplyAnthropicCacheControl(messages, tools, cacheControl);

        if (options?.ToolChoice is not null) p["tool_choice"] = options.ToolChoice.DeepClone();
        if (compat.VllmPriority is not null) p["priority"] = compat.VllmPriority;

        var thinkingTokenBudgetField = compat.ThinkingTokenBudgetField ?? (compat.SupportsThinkingTokenBudget == true ? "thinking_token_budget" : null);
        var thinkingBudget = ResolveClampedThinkingBudget(model, options, p);
        var effort = options?.ReasoningEffort;

        string? MappedOr(ThinkingLevel level, string fallback, out bool isNullMapping)
        {
            isNullMapping = false;
            if (model.TryGetThinkingMapping(level, out var mapped))
            {
                if (mapped is null)
                {
                    isNullMapping = true;
                    return null;
                }
                return mapped;
            }
            return fallback;
        }

        bool OffIsNotNull() => !(model.TryGetThinkingMapping(ThinkingLevel.Off, out var off) && off is null);
        string? OffValue() => model.TryGetThinkingMapping(ThinkingLevel.Off, out var off) ? off : null;

        if (compat.ThinkingFormat == "zai" && model.Reasoning)
        {
            p["thinking"] = effort is not null
                ? new JsonObject { ["type"] = "enabled", ["clear_thinking"] = false }
                : new JsonObject { ["type"] = "disabled" };
            if (effort is not null && compat.SupportsReasoningEffort)
            {
                var value = MappedOr(effort.Value, effort.Value.ToWire(), out _);
                if (value is not null) p["reasoning_effort"] = value;
            }
        }
        else if (compat.ThinkingFormat == "qwen" && model.Reasoning)
        {
            p["enable_thinking"] = effort is not null;
            if (effort is not null && compat.SupportsReasoningEffort)
            {
                // `??` in TS: null mapping falls back to the effort name.
                var value = MappedOr(effort.Value, effort.Value.ToWire(), out var isNull) ?? (isNull ? effort.Value.ToWire() : null);
                if (value is not null) p["reasoning_effort"] = value;
            }
        }
        else if (compat.ThinkingFormat == "qwen-chat-template" && model.Reasoning)
        {
            p["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = effort is not null, ["preserve_thinking"] = true };
        }
        else if (compat.ThinkingFormat == "chat-template" && model.Reasoning)
        {
            var values = BuildChatTemplateValues(model, options, compat.ChatTemplateKwargs, thinkingBudget);
            if (values is not null) p["chat_template_kwargs"] = values;
        }
        else if (compat.ThinkingFormat == "baseten" && model.Reasoning)
        {
            var values = BuildChatTemplateValues(model, options, compat.ChatTemplateArgs, thinkingBudget);
            if (values is not null) p["chat_template_args"] = values;
            if (compat.SupportsReasoningEffort)
            {
                string? value;
                if (effort is not null) value = MappedOr(effort.Value, effort.Value.ToWire(), out _);
                else value = model.TryGetThinkingMapping(ThinkingLevel.Off, out var off) ? off : null;
                if (value is not null) p["reasoning_effort"] = value;
            }
        }
        else if (compat.ThinkingFormat == "deepseek" && model.Reasoning)
        {
            if (effort is not null) p["thinking"] = new JsonObject { ["type"] = "enabled" };
            else if (OffIsNotNull()) p["thinking"] = new JsonObject { ["type"] = "disabled" };
            if (effort is not null && compat.SupportsReasoningEffort)
            {
                p["reasoning_effort"] = MappedOr(effort.Value, effort.Value.ToWire(), out var isNull) ?? effort.Value.ToWire();
            }
        }
        else if (compat.ThinkingFormat == "openrouter" && model.Reasoning)
        {
            if (effort is not null)
            {
                p["reasoning"] = new JsonObject { ["effort"] = MappedOr(effort.Value, effort.Value.ToWire(), out _) ?? effort.Value.ToWire() };
            }
            else if (OffIsNotNull())
            {
                p["reasoning"] = new JsonObject { ["effort"] = OffValue() ?? "none" };
            }
        }
        else if (compat.ThinkingFormat == "ant-ling" && model.Reasoning && effort is not null)
        {
            if (model.TryGetThinkingMapping(effort.Value, out var mapped) && mapped is not null)
            {
                p["reasoning"] = new JsonObject { ["effort"] = mapped };
            }
        }
        else if (compat.ThinkingFormat == "together" && model.Reasoning)
        {
            p["reasoning"] = new JsonObject { ["enabled"] = effort is not null };
            if (effort is not null && compat.SupportsReasoningEffort)
            {
                p["reasoning_effort"] = MappedOr(effort.Value, effort.Value.ToWire(), out _) ?? effort.Value.ToWire();
            }
        }
        else if (compat.ThinkingFormat == "string-thinking" && model.Reasoning)
        {
            if (effort is not null) p["thinking"] = MappedOr(effort.Value, effort.Value.ToWire(), out _) ?? effort.Value.ToWire();
            else if (OffIsNotNull()) p["thinking"] = OffValue() ?? "none";
        }
        else if (effort is not null && model.Reasoning && compat.SupportsReasoningEffort)
        {
            p["reasoning_effort"] = MappedOr(effort.Value, effort.Value.ToWire(), out _) ?? effort.Value.ToWire();
        }
        else if (effort is null && model.Reasoning && compat.SupportsReasoningEffort)
        {
            if (OffValue() is { } offValue) p["reasoning_effort"] = offValue;
        }

        if (thinkingTokenBudgetField is not null && thinkingBudget is not null) p[thinkingTokenBudgetField] = thinkingBudget;

        if (model.GetCompat<OpenAICompletionsCompat>().OpenRouterRouting is { } routing) p["provider"] = routing.DeepClone();

        if (model.GetCompat<OpenAICompletionsCompat>().VercelGatewayRouting is { } vercel && (vercel.Only is not null || vercel.Order is not null))
        {
            var gateway = new JsonObject();
            if (vercel.Only is not null) gateway["only"] = new JsonArray(vercel.Only.Select(x => (JsonNode)x).ToArray());
            if (vercel.Order is not null) gateway["order"] = new JsonArray(vercel.Order.Select(x => (JsonNode)x).ToArray());
            p["providerOptions"] = new JsonObject { ["gateway"] = gateway };
        }

        if (options?.SamplingParams is not null)
        {
            foreach (var (k, v) in options.SamplingParams) p[k] = v?.DeepClone();
        }

        return p;
    }

    private static string? ClampPromptCacheKey(string? key)
    {
        if (key is null) return null;
        var runes = key.EnumerateRunes().ToList();
        return runes.Count <= 64 ? key : string.Concat(runes.Take(64).Select(r => r.ToString()));
    }

    private static long? ResolveClampedThinkingBudget(Model model, OpenAICompletionsOptions? options, JsonObject p)
    {
        if (options?.ReasoningEffort is null || !model.Reasoning) return null;
        long ceiling = p["max_tokens"] is JsonValue a && a.TryGetValue<long>(out var mt) ? mt
            : p["max_completion_tokens"] is JsonValue b && b.TryGetValue<long>(out var mct) ? mct
            : model.MaxTokens;
        var budget = SimpleOptions.ClampThinkingBudgetToAnswerRoom(SimpleOptions.ThinkingBudgetForLevel(options.ReasoningEffort.Value, options.ThinkingBudgets), ceiling);
        return budget > 0 ? budget : null;
    }

    private static JsonObject? BuildChatTemplateValues(Model model, OpenAICompletionsOptions? options, JsonObject values, long? thinkingBudget)
    {
        var resolved = new JsonObject();
        foreach (var (key, value) in values)
        {
            if (ResolveChatTemplateKwargValue(model, options, value, thinkingBudget, out var result)) resolved[key] = result;
        }
        return resolved.Count > 0 ? resolved : null;
    }

    private static bool ResolveChatTemplateKwargValue(Model model, OpenAICompletionsOptions? options, JsonNode? value, long? thinkingBudget, out JsonNode? result)
    {
        result = null;
        if (value is not JsonObject obj)
        {
            result = value?.DeepClone();
            return true;
        }
        var effort = options?.ReasoningEffort;
        var omitWhenOff = obj["omitWhenOff"] is JsonValue ov && ov.TryGetValue<bool>(out var ob) && ob;
        if (effort is null && omitWhenOff) return false;
        var variable = obj["$var"] is JsonValue vv && vv.TryGetValue<string>(out var vs) ? vs : null;
        if (variable == "thinking.enabled")
        {
            result = effort is not null;
            return true;
        }
        if (variable == "thinking.budget")
        {
            if (thinkingBudget is null) return false;
            result = thinkingBudget;
            return true;
        }

        var level = effort ?? ThinkingLevel.Off;
        if (!model.TryGetThinkingMapping(level, out var mapped))
        {
            if (effort is null) return false;
            result = effort.Value.ToWire();
            return true;
        }
        if (mapped is null) return false;
        result = mapped;
        return true;
    }

    private static JsonObject? GetCompatCacheControl(ResolvedCompletionsCompat compat, CacheRetention cacheRetention)
    {
        if (compat.CacheControlFormat != "anthropic" || cacheRetention == CacheRetention.None) return null;
        var control = new JsonObject { ["type"] = "ephemeral" };
        if (cacheRetention == CacheRetention.Long && compat.SupportsLongCacheRetention) control["ttl"] = "1h";
        return control;
    }

    private static void ApplyAnthropicCacheControl(JsonArray messages, JsonArray? tools, JsonObject cacheControl)
    {
        foreach (var message in messages)
        {
            if (message is JsonObject m && GetString(m, "role") is "system" or "developer")
            {
                AddCacheControlToTextContent(m, cacheControl);
                break;
            }
        }
        if (tools is { Count: > 0 } && tools[^1] is JsonObject lastTool) lastTool["cache_control"] = cacheControl.DeepClone();
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i] is JsonObject m && GetString(m, "role") is "user" or "assistant" or "tool")
            {
                if (AddCacheControlToTextContent(m, cacheControl)) return;
            }
        }
    }

    private static bool AddCacheControlToTextContent(JsonObject message, JsonObject cacheControl)
    {
        var content = message["content"];
        if (content is JsonValue v && v.TryGetValue<string>(out var text))
        {
            if (text.Length == 0) return false;
            message["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text, ["cache_control"] = cacheControl.DeepClone() });
            return true;
        }
        if (content is not JsonArray parts) return false;
        for (var i = parts.Count - 1; i >= 0; i--)
        {
            if (parts[i] is JsonObject part && GetString(part, "type") == "text")
            {
                part["cache_control"] = cacheControl.DeepClone();
                return true;
            }
        }
        return false;
    }

    private static bool HasToolHistory(IEnumerable<Message> messages) =>
        messages.Any(m => m is ToolResultMessage || (m is AssistantMessage a && a.Content.Any(b => b is ToolCall)));

    private static HashSet<string> GetDeferredToolNames(IEnumerable<Message> messages) =>
        messages.OfType<ToolResultMessage>().SelectMany(m => m.AddedToolNames ?? []).ToHashSet();

    private static readonly Regex NonIdChars = new("[^a-zA-Z0-9_-]", RegexOptions.Compiled);

    internal static JsonArray ConvertMessages(Model model, Context context, ResolvedCompletionsCompat compat, IReadOnlyDictionary<string, string>? grammarToolInputProperties = null)
    {
        var result = new JsonArray();

        string NormalizeToolCallId(string id, Model _, AssistantMessage __)
        {
            if (id.Contains('|'))
            {
                var separator = id.IndexOf('|');
                var callId = NonIdChars.Replace(id[..separator], "_");
                var itemId = NonIdChars.Replace(id[(separator + 1)..], "_");
                var combined = itemId.Length > 0 ? $"{callId}_{itemId}" : callId;
                if (combined.Length <= 40) return combined;
                var hash = TextUtils.ShortHash(id).Slice(0, 8);
                var prefix = callId.Slice(0, Math.Max(1, 40 - hash.Length - 1));
                return $"{prefix}_{hash}";
            }
            if (model.Provider == "openai") return id.Length > 40 ? id[..40] : id;
            return id;
        }

        var transformed = MessageTransformer.Transform(context.Messages, model, NormalizeToolCallId);

        if (!string.IsNullOrEmpty(context.SystemPrompt))
        {
            var useDeveloperRole = model.Reasoning && compat.SupportsDeveloperRole;
            result.Add(new JsonObject
            {
                ["role"] = useDeveloperRole ? "developer" : "system",
                ["content"] = TextUtils.SanitizeSurrogates(context.SystemPrompt),
            });
        }

        string? lastRole = null;

        for (var i = 0; i < transformed.Count; i++)
        {
            var msg = transformed[i];
            if (compat.RequiresAssistantAfterToolResult && lastRole == "toolResult" && msg is UserMessage)
            {
                result.Add(new JsonObject { ["role"] = "assistant", ["content"] = "I have processed the tool results." });
            }

            switch (msg)
            {
                case UserMessage user:
                {
                    if (user.Content.Text is not null)
                    {
                        result.Add(new JsonObject { ["role"] = "user", ["content"] = TextUtils.SanitizeSurrogates(user.Content.Text) });
                    }
                    else
                    {
                        var parts = new JsonArray();
                        foreach (var item in user.Content.Blocks!)
                        {
                            if (item is TextContent t)
                                parts.Add(new JsonObject { ["type"] = "text", ["text"] = TextUtils.SanitizeSurrogates(t.Text) });
                            else if (item is ImageContent img)
                                parts.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = $"data:{img.MimeType};base64,{img.Data}" } });
                        }
                        if (parts.Count == 0) continue;
                        result.Add(new JsonObject { ["role"] = "user", ["content"] = parts });
                    }
                    break;
                }
                case AssistantMessage assistant:
                {
                    var assistantMsg = new JsonObject
                    {
                        ["role"] = "assistant",
                        ["content"] = compat.RequiresAssistantAfterToolResult ? "" : null,
                    };

                    var textParts = assistant.Content.OfType<TextContent>()
                        .Where(b => b.Text.Trim().Length > 0)
                        .Select(b => TextUtils.SanitizeSurrogates(b.Text))
                        .ToList();
                    var assistantText = string.Concat(textParts);
                    var thinkingBlocks = assistant.Content.OfType<ThinkingContent>().ToList();
                    var toolCalls = assistant.Content.OfType<ToolCall>().ToList();
                    var signedReasoningDetails = thinkingBlocks.Select(b => ParseOpenAIReasoningDetails(b.ThinkingSignature)).FirstOrDefault(d => d is not null);
                    var legacyDetails = toolCalls.Select(tc => ParseLegacyEncryptedReasoningDetail(tc.ThoughtSignature)).Where(d => d is not null).Cast<JsonObject>().ToList();
                    var preservedReasoningDetails = signedReasoningDetails ?? (legacyDetails.Count > 0 ? legacyDetails : null);

                    var nonEmptyThinking = thinkingBlocks.Where(b => b.Thinking.Trim().Length > 0).ToList();
                    if (nonEmptyThinking.Count > 0)
                    {
                        if (compat.RequiresThinkingAsText)
                        {
                            var thinkingText = string.Join("\n\n", nonEmptyThinking.Select(b => TextUtils.SanitizeSurrogates(b.Thinking)));
                            var arr = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = thinkingText });
                            foreach (var part in textParts) arr.Add(new JsonObject { ["type"] = "text", ["text"] = part });
                            assistantMsg["content"] = arr;
                        }
                        else
                        {
                            if (assistantText.Length > 0) assistantMsg["content"] = assistantText;
                            if (preservedReasoningDetails is null)
                            {
                                var signature = nonEmptyThinking[0].ThinkingSignature;
                                if (model.Provider == "opencode-go" && signature == "reasoning") signature = "reasoning_content";
                                if (signature is "reasoning" or "reasoning_content" or "reasoning_text")
                                {
                                    assistantMsg[signature] = string.Join("\n", nonEmptyThinking.Select(b => b.Thinking));
                                }
                            }
                        }
                    }
                    else if (assistantText.Length > 0)
                    {
                        assistantMsg["content"] = assistantText;
                    }

                    if (toolCalls.Count > 0)
                    {
                        var calls = new JsonArray();
                        foreach (var tc in toolCalls)
                        {
                            if (grammarToolInputProperties is not null && grammarToolInputProperties.TryGetValue(tc.Name, out var customProp))
                            {
                                calls.Add(new JsonObject
                                {
                                    ["id"] = tc.Id,
                                    ["type"] = "custom",
                                    ["custom"] = new JsonObject
                                    {
                                        ["name"] = tc.Name,
                                        ["input"] = TextUtils.SanitizeSurrogates(ConstrainedSampling.GetGrammarToolInput(tc.Name, tc.Arguments, customProp)),
                                    },
                                });
                            }
                            else
                            {
                                calls.Add(new JsonObject
                                {
                                    ["id"] = tc.Id,
                                    ["type"] = "function",
                                    ["function"] = new JsonObject { ["name"] = tc.Name, ["arguments"] = IrisJson.Stringify(tc.Arguments) },
                                });
                            }
                        }
                        assistantMsg["tool_calls"] = calls;
                    }
                    if (preservedReasoningDetails is not null)
                    {
                        assistantMsg["reasoning_details"] = new JsonArray(preservedReasoningDetails.Select(d => (JsonNode)d.DeepClone()).ToArray());
                    }
                    if (compat.RequiresReasoningContentOnAssistantMessages && model.Reasoning && !assistantMsg.ContainsKey("reasoning_content"))
                    {
                        assistantMsg["reasoning_content"] = "";
                    }

                    var contentNode = assistantMsg["content"];
                    var hasContent = contentNode switch
                    {
                        JsonValue cv when cv.TryGetValue<string>(out var cs) => cs.Length > 0,
                        JsonArray ca => ca.Count > 0,
                        _ => false,
                    };
                    if (!hasContent && !assistantMsg.ContainsKey("tool_calls")) continue;
                    result.Add(assistantMsg);
                    break;
                }
                case ToolResultMessage:
                {
                    var imageBlocks = new List<JsonObject>();
                    var deferredToolNames = new HashSet<string>();
                    var j = i;
                    for (; j < transformed.Count && transformed[j] is ToolResultMessage toolMsg; j++)
                    {
                        var textResult = string.Join("\n", toolMsg.Content.OfType<TextContent>().Select(b => b.Text));
                        var hasImages = toolMsg.Content.Any(c => c is ImageContent);
                        var toolResultText = textResult.Length > 0 ? textResult : hasImages ? "(see attached image)" : "(no tool output)";
                        var toolResultMsg = new JsonObject
                        {
                            ["role"] = "tool",
                            ["content"] = TextUtils.SanitizeSurrogates(toolResultText),
                            ["tool_call_id"] = toolMsg.ToolCallId,
                        };
                        if (compat.RequiresToolResultName && !string.IsNullOrEmpty(toolMsg.ToolName)) toolResultMsg["name"] = toolMsg.ToolName;
                        result.Add(toolResultMsg);

                        if (compat.DeferredToolsMode == "kimi")
                        {
                            foreach (var name in toolMsg.AddedToolNames ?? []) deferredToolNames.Add(name);
                        }

                        if (hasImages && model.SupportsImages)
                        {
                            foreach (var img in toolMsg.Content.OfType<ImageContent>())
                            {
                                imageBlocks.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = $"data:{img.MimeType};base64,{img.Data}" } });
                            }
                        }
                    }

                    i = j - 1;

                    if (imageBlocks.Count > 0)
                    {
                        if (compat.RequiresAssistantAfterToolResult)
                        {
                            result.Add(new JsonObject { ["role"] = "assistant", ["content"] = "I have processed the tool results." });
                        }
                        var content = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Attached image(s) from tool result:" });
                        foreach (var ib in imageBlocks) content.Add(ib);
                        result.Add(new JsonObject { ["role"] = "user", ["content"] = content });
                        lastRole = "user";
                    }
                    else
                    {
                        lastRole = "toolResult";
                    }

                    if (deferredToolNames.Count > 0 && context.Tools is not null)
                    {
                        var byName = context.Tools.ToDictionary(t => t.Name);
                        var deferredTools = deferredToolNames.Where(byName.ContainsKey).Select(n => byName[n]).ToList();
                        if (deferredTools.Count > 0)
                        {
                            result.Add(new JsonObject { ["role"] = "system", ["tools"] = ConvertTools(deferredTools, compat) });
                        }
                    }
                    continue;
                }
            }

            lastRole = msg.Role;
        }

        return result;
    }

    private static JsonArray ConvertTools(IEnumerable<Tool> tools, ResolvedCompletionsCompat compat)
    {
        var result = new JsonArray();
        foreach (var tool in tools)
        {
            var grammar = ConstrainedSampling.ResolveGrammarConstrainedSampling(tool, compat.SupportsOpenAIGrammarTools);
            if (grammar is not null)
            {
                result.Add(new JsonObject
                {
                    ["type"] = "custom",
                    ["custom"] = new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["format"] = new JsonObject
                        {
                            ["type"] = "grammar",
                            ["grammar"] = new JsonObject { ["syntax"] = grammar.Format, ["definition"] = grammar.Definition },
                        },
                    },
                });
                continue;
            }

            var strict = ConstrainedSampling.ResolveJsonSchemaStrictSampling(tool, compat.SupportsStrictMode);
            var function = new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["parameters"] = ConstrainedSampling.GetJsonSchemaToolParameters(tool, strict).DeepClone(),
            };
            if (compat.SupportsStrictMode) function["strict"] = strict ?? false;
            result.Add(new JsonObject { ["type"] = "function", ["function"] = function });
        }
        return result;
    }

    private static long GetLong(JsonObject? obj, string name) =>
        obj?[name] is JsonValue v && IrisJson.TryGetNumber(v, out var d) ? (long)d : 0;

    private static long? GetLongOrNull(JsonObject? obj, string name) =>
        obj?[name] is JsonValue v && IrisJson.TryGetNumber(v, out var d) ? (long)d : null;

    internal static Usage ParseChunkUsage(JsonObject raw, Model model)
    {
        var promptTokens = GetLong(raw, "prompt_tokens");
        var details = raw["prompt_tokens_details"] as JsonObject;
        var cacheRead = GetLongOrNull(details, "cached_tokens") ?? GetLongOrNull(raw, "prompt_cache_hit_tokens") ?? GetLongOrNull(raw, "cached_tokens") ?? 0;
        var cacheWrite = GetLong(details, "cache_write_tokens");
        var input = Math.Max(0, promptTokens - cacheRead - cacheWrite);
        var outputTokens = GetLong(raw, "completion_tokens");
        var usage = new Usage
        {
            Input = input,
            Output = outputTokens,
            CacheRead = cacheRead,
            CacheWrite = cacheWrite,
            Reasoning = GetLong(raw["completion_tokens_details"] as JsonObject, "reasoning_tokens"),
            TotalTokens = input + outputTokens + cacheRead + cacheWrite,
        };
        ModelUtils.CalculateCost(model, usage);
        return usage;
    }

    private static (StopReason, string?) MapStopReason(string reason) => reason switch
    {
        "stop" or "end" => (StopReason.Stop, null),
        "length" => (StopReason.Length, null),
        "function_call" or "tool_calls" => (StopReason.ToolUse, null),
        "content_filter" => (StopReason.Error, "Provider finish_reason: content_filter"),
        "network_error" => (StopReason.Error, "Provider finish_reason: network_error"),
        _ => (StopReason.Error, $"Provider finish_reason: {reason}"),
    };

    // ---- reasoning_details helpers ----

    private static bool IsOpenAIReasoningDetail(JsonObject d)
    {
        bool OptString(string key) => !d.ContainsKey(key) || d[key] is null || (d[key] is JsonValue v && v.TryGetValue<string>(out _));
        bool OptNumber(string key) => !d.ContainsKey(key) || (d[key] is JsonValue v && v.TryGetValue<double>(out _));
        if (!OptString("id") || !(d.ContainsKey("format") ? d["format"] is JsonValue fv && fv.TryGetValue<string>(out _) : true) || !OptNumber("index")) return false;
        var type = GetString(d, "type");
        return type switch
        {
            "reasoning.summary" => GetString(d, "summary") is not null,
            "reasoning.encrypted" => GetString(d, "data") is not null,
            "reasoning.text" => GetString(d, "text") is not null && OptString("signature"),
            _ => false,
        };
    }

    private static List<JsonObject>? ParseOpenAIReasoningDetails(string? signature)
    {
        if (string.IsNullOrEmpty(signature)) return null;
        try
        {
            if (JsonNode.Parse(signature) is JsonArray arr && arr.Count > 0 && arr.All(x => x is JsonObject o && IsOpenAIReasoningDetail(o)))
            {
                return arr.Select(x => (JsonObject)x!.DeepClone()).ToList();
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }

    private static JsonObject? ParseLegacyEncryptedReasoningDetail(string? signature)
    {
        if (string.IsNullOrEmpty(signature)) return null;
        try
        {
            if (JsonNode.Parse(signature) is JsonObject o && IsOpenAIReasoningDetail(o) && GetString(o, "type") == "reasoning.encrypted"
                && GetString(o, "id") is { Length: > 0 } && GetString(o, "data") is { Length: > 0 })
            {
                return o;
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }

    private static void AppendOpenAIReasoningDetail(List<JsonObject> details, JsonObject detail)
    {
        var last = details.Count > 0 ? details[^1] : null;
        var type = GetString(detail, "type");
        if (last is not null && type == "reasoning.text" && GetString(last, "type") == "reasoning.text")
        {
            last["text"] = GetString(last, "text") + GetString(detail, "text");
            if (string.IsNullOrEmpty(GetString(last, "signature")) && detail["signature"] is not null) last["signature"] = detail["signature"]!.DeepClone();
            FillMissingCommon(last, detail);
            return;
        }
        if (last is not null && type == "reasoning.summary" && GetString(last, "type") == "reasoning.summary")
        {
            last["summary"] = GetString(last, "summary") + GetString(detail, "summary");
            FillMissingCommon(last, detail);
            return;
        }
        details.Add((JsonObject)detail.DeepClone());
    }

    private static void FillMissingCommon(JsonObject target, JsonObject source)
    {
        if (target["id"] is null && source["id"] is not null) target["id"] = source["id"]!.DeepClone();
        if (string.IsNullOrEmpty(GetString(target, "format")) && source["format"] is not null) target["format"] = source["format"]!.DeepClone();
        if (target["index"] is null && source["index"] is not null) target["index"] = source["index"]!.DeepClone();
    }

    // ---- compat detection ----

    internal static ResolvedCompletionsCompat DetectCompat(Model model)
    {
        var provider = model.Provider;
        var baseUrl = model.BaseUrl;

        var isZai = provider is "zai" or "zai-coding-cn" || baseUrl.Contains("api.z.ai") || baseUrl.Contains("open.bigmodel.cn");
        var isTogether = provider == "together" || baseUrl.Contains("api.together.ai") || baseUrl.Contains("api.together.xyz");
        var isMoonshot = provider is "moonshotai" or "moonshotai-cn" || baseUrl.Contains("api.moonshot.");
        var isOpenRouter = provider == "openrouter" || baseUrl.Contains("openrouter.ai");
        var isCloudflareWorkersAI = provider == "cloudflare-workers-ai" || baseUrl.Contains("api.cloudflare.com");
        var isCloudflareAiGateway = provider == "cloudflare-ai-gateway" || baseUrl.Contains("gateway.ai.cloudflare.com");
        var isNvidia = provider == "nvidia" || baseUrl.Contains("integrate.api.nvidia.com");
        var isAntLing = provider == "ant-ling" || baseUrl.Contains("api.ant-ling.com");
        var isDeepSeek = provider == "deepseek" || baseUrl.ToLowerInvariant().Contains("deepseek.com");

        var isNonStandard = isNvidia || provider == "cerebras" || baseUrl.Contains("cerebras.ai") || provider == "xai"
            || baseUrl.Contains("api.x.ai") || isTogether || baseUrl.Contains("chutes.ai") || isDeepSeek || isZai || isMoonshot
            || provider == "opencode" || baseUrl.Contains("opencode.ai") || isCloudflareWorkersAI || isCloudflareAiGateway || isAntLing;

        var useMaxTokens = baseUrl.Contains("chutes.ai") || isDeepSeek || isMoonshot || isCloudflareAiGateway || isTogether || isNvidia || isAntLing || isZai;
        var isGrok = provider == "xai" || baseUrl.Contains("api.x.ai");
        var isOpenRouterDeveloperRoleModel = isOpenRouter && (model.Id.StartsWith("anthropic/") || model.Id.StartsWith("openai/"));
        var cacheControlFormat = provider == "openrouter" && model.Id.StartsWith("anthropic/") ? "anthropic" : null;

        return new ResolvedCompletionsCompat
        {
            SupportsStore = !isNonStandard,
            SupportsDeveloperRole = isOpenRouterDeveloperRoleModel || (!isNonStandard && !isOpenRouter),
            SupportsReasoningEffort = !isGrok && !isZai && !isMoonshot && !isTogether && !isCloudflareAiGateway && !isNvidia && !isAntLing,
            SupportsUsageInStreaming = true,
            SupportsFinishReason = true,
            MaxTokensField = useMaxTokens ? "max_tokens" : "max_completion_tokens",
            RequiresToolResultName = false,
            RequiresAssistantAfterToolResult = false,
            RequiresThinkingAsText = false,
            RequiresReasoningContentOnAssistantMessages = isDeepSeek,
            ThinkingFormat = isDeepSeek ? "deepseek" : isZai ? "zai" : isTogether ? "together" : isAntLing ? "ant-ling" : isOpenRouter ? "openrouter" : "openai",
            ZaiToolStream = false,
            SupportsThinkingTokenBudget = false,
            ThinkingTokenBudgetField = null,
            SupportsStrictMode = !isMoonshot && !isTogether && !isCloudflareAiGateway && !isNvidia,
            SupportsOpenAIGrammarTools = false,
            CacheControlFormat = cacheControlFormat,
            SendSessionAffinityHeaders = isOpenRouter,
            DeferredToolsMode = null,
            SessionAffinityFormat = isOpenRouter ? "openrouter" : "openai",
            SupportsLongCacheRetention = !(isTogether || isCloudflareWorkersAI || isCloudflareAiGateway || isNvidia || isAntLing),
        };
    }

    internal static ResolvedCompletionsCompat GetCompat(Model model)
    {
        var detected = DetectCompat(model);
        if (model.Compat is null) return detected;
        var c = model.GetCompat<OpenAICompletionsCompat>();
        return new ResolvedCompletionsCompat
        {
            SupportsStore = c.SupportsStore ?? detected.SupportsStore,
            SupportsDeveloperRole = c.SupportsDeveloperRole ?? detected.SupportsDeveloperRole,
            SupportsReasoningEffort = c.SupportsReasoningEffort ?? detected.SupportsReasoningEffort,
            SupportsUsageInStreaming = c.SupportsUsageInStreaming ?? detected.SupportsUsageInStreaming,
            SupportsFinishReason = c.SupportsFinishReason ?? detected.SupportsFinishReason,
            MaxTokensField = c.MaxTokensField ?? detected.MaxTokensField,
            RequiresToolResultName = c.RequiresToolResultName ?? detected.RequiresToolResultName,
            RequiresAssistantAfterToolResult = c.RequiresAssistantAfterToolResult ?? detected.RequiresAssistantAfterToolResult,
            RequiresThinkingAsText = c.RequiresThinkingAsText ?? detected.RequiresThinkingAsText,
            RequiresReasoningContentOnAssistantMessages = c.RequiresReasoningContentOnAssistantMessages ?? detected.RequiresReasoningContentOnAssistantMessages,
            ThinkingFormat = c.ThinkingFormat ?? detected.ThinkingFormat,
            OpenRouterRouting = c.OpenRouterRouting ?? new JsonObject(),
            VercelGatewayRouting = c.VercelGatewayRouting ?? detected.VercelGatewayRouting,
            ChatTemplateKwargs = c.ChatTemplateKwargs ?? detected.ChatTemplateKwargs,
            ChatTemplateArgs = c.ChatTemplateArgs ?? detected.ChatTemplateArgs,
            ZaiToolStream = c.ZaiToolStream ?? detected.ZaiToolStream,
            SupportsThinkingTokenBudget = c.SupportsThinkingTokenBudget ?? detected.SupportsThinkingTokenBudget,
            ThinkingTokenBudgetField = c.ThinkingTokenBudgetField ?? detected.ThinkingTokenBudgetField,
            SupportsStrictMode = c.SupportsStrictMode ?? detected.SupportsStrictMode,
            SupportsOpenAIGrammarTools = c.SupportsOpenAIGrammarTools ?? detected.SupportsOpenAIGrammarTools,
            CacheControlFormat = c.CacheControlFormat ?? detected.CacheControlFormat,
            SendSessionAffinityHeaders = c.SendSessionAffinityHeaders ?? detected.SendSessionAffinityHeaders,
            DeferredToolsMode = c.DeferredToolsMode ?? detected.DeferredToolsMode,
            SessionAffinityFormat = c.SessionAffinityFormat ?? detected.SessionAffinityFormat,
            SupportsLongCacheRetention = c.SupportsLongCacheRetention ?? detected.SupportsLongCacheRetention,
            VllmPriority = c.VllmPriority,
        };
    }
}
