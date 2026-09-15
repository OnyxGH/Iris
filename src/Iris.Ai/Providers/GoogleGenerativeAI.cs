using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Iris.Ai.Json;
using Iris.Ai.Utils;

namespace Iris.Ai.Providers;

public sealed class GoogleThinkingOptions
{
    public bool Enabled { get; init; }
    /// <summary>-1 for dynamic, 0 to disable.</summary>
    public long? BudgetTokens { get; init; }
    /// <summary>THINKING_LEVEL_UNSPECIFIED | MINIMAL | LOW | MEDIUM | HIGH.</summary>
    public string? Level { get; init; }
}

public sealed class GoogleOptions : StreamOptions
{
    /// <summary>auto | none | any.</summary>
    public string? ToolChoice { get; set; }

    public GoogleThinkingOptions? Thinking { get; set; }
}

/// <summary>Shared utilities for Google Generative AI and Vertex.</summary>
public static class GoogleShared
{
    private static readonly Regex Base64Signature = new("^[A-Za-z0-9+/]+={0,2}$", RegexOptions.Compiled);
    private static readonly Regex GeminiVersion = new(@"^gemini(?:-live)?-(\d+)", RegexOptions.Compiled);
    private static readonly Regex NonIdChars = new("[^a-zA-Z0-9_-]", RegexOptions.Compiled);

    private static readonly HashSet<string> JsonSchemaMetaDeclarations =
        ["$schema", "$id", "$anchor", "$dynamicAnchor", "$vocabulary", "$comment", "$defs", "definitions"];

    /// <summary>Resolve a thinking level (or model mapping) to minimal/low/medium/high.</summary>
    public static string ResolveThinkingLevel(Model model, ThinkingLevel level)
    {
        if (level == ThinkingLevel.Off) return "high";
        var resolved = model.TryGetThinkingMapping(level, out var mapped) && mapped is not null ? mapped.ToLowerInvariant() : level.ToWire();
        return resolved switch
        {
            "minimal" or "low" or "medium" or "high" => resolved,
            _ => throw new InvalidOperationException($"Unsupported Google thinking level mapping for {model.Provider}/{model.Id}: {level.ToWire()} -> {mapped ?? "undefined"}"),
        };
    }

    public static bool IsThinkingPart(JsonObject part) => part["thought"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    public static string? RetainThoughtSignature(string? existing, string? incoming) => !string.IsNullOrEmpty(incoming) ? incoming : existing;

    private static bool IsValidThoughtSignature(string? signature) =>
        !string.IsNullOrEmpty(signature) && signature.Length % 4 == 0 && Base64Signature.IsMatch(signature);

    private static string? ResolveThoughtSignature(bool sameProviderAndModel, string? signature) =>
        sameProviderAndModel && IsValidThoughtSignature(signature) ? signature : null;

    private static int? GetGeminiMajorVersion(string modelId)
    {
        var match = GeminiVersion.Match(modelId.ToLowerInvariant());
        return match.Success ? int.Parse(match.Groups[1].Value) : null;
    }

    public static bool RequiresToolCallId(string modelId)
    {
        var major = GetGeminiMajorVersion(modelId);
        return modelId.StartsWith("claude-", StringComparison.Ordinal) || modelId.StartsWith("gpt-oss-", StringComparison.Ordinal) || major >= 3;
    }

    private static bool SupportsMultimodalFunctionResponse(string modelId)
    {
        var major = GetGeminiMajorVersion(modelId);
        return major is null || major >= 3;
    }

    public static bool SupportsStrictToolSampling(string modelId) => GetGeminiMajorVersion(modelId) >= 3;

    public static JsonArray ConvertMessages(Model model, Context context)
    {
        var contents = new JsonArray();
        string NormalizeToolCallId(string id, Model _, AssistantMessage __)
        {
            if (!RequiresToolCallId(model.Id)) return id;
            var normalized = NonIdChars.Replace(id, "_");
            return normalized.Length > 64 ? normalized[..64] : normalized;
        }

        var transformed = MessageTransformer.Transform(context.Messages, model, NormalizeToolCallId);
        foreach (var msg in transformed)
        {
            switch (msg)
            {
                case UserMessage user:
                    if (user.Content.Text is not null)
                    {
                        contents.Add(new JsonObject { ["role"] = "user", ["parts"] = new JsonArray(new JsonObject { ["text"] = TextUtils.SanitizeSurrogates(user.Content.Text) }) });
                    }
                    else
                    {
                        var parts = new JsonArray();
                        foreach (var item in user.Content.Blocks!)
                        {
                            if (item is TextContent t) parts.Add(new JsonObject { ["text"] = TextUtils.SanitizeSurrogates(t.Text) });
                            else if (item is ImageContent img) parts.Add(new JsonObject { ["inlineData"] = new JsonObject { ["mimeType"] = img.MimeType, ["data"] = img.Data } });
                        }
                        if (parts.Count == 0) continue;
                        contents.Add(new JsonObject { ["role"] = "user", ["parts"] = parts });
                    }
                    break;
                case AssistantMessage assistant:
                {
                    var parts = new JsonArray();
                    var same = assistant.Provider == model.Provider && assistant.Model == model.Id;
                    foreach (var block in assistant.Content)
                    {
                        switch (block)
                        {
                            case TextContent text:
                            {
                                var sig = ResolveThoughtSignature(same, text.TextSignature);
                                if (string.IsNullOrWhiteSpace(text.Text) && sig is null) continue;
                                var part = new JsonObject { ["text"] = TextUtils.SanitizeSurrogates(text.Text) };
                                if (sig is not null) part["thoughtSignature"] = sig;
                                parts.Add(part);
                                break;
                            }
                            case ThinkingContent thinking:
                                if (same)
                                {
                                    var sig = ResolveThoughtSignature(same, thinking.ThinkingSignature);
                                    if (string.IsNullOrWhiteSpace(thinking.Thinking) && sig is null) continue;
                                    var part = new JsonObject { ["thought"] = true, ["text"] = TextUtils.SanitizeSurrogates(thinking.Thinking) };
                                    if (sig is not null) part["thoughtSignature"] = sig;
                                    parts.Add(part);
                                }
                                else
                                {
                                    if (string.IsNullOrWhiteSpace(thinking.Thinking)) continue;
                                    parts.Add(new JsonObject { ["text"] = TextUtils.SanitizeSurrogates(thinking.Thinking) });
                                }
                                break;
                            case ToolCall toolCall:
                            {
                                var sig = ResolveThoughtSignature(same, toolCall.ThoughtSignature);
                                var functionCall = new JsonObject { ["name"] = toolCall.Name, ["args"] = toolCall.Arguments.DeepClone() };
                                if (RequiresToolCallId(model.Id)) functionCall["id"] = toolCall.Id;
                                var part = new JsonObject { ["functionCall"] = functionCall };
                                if (sig is not null) part["thoughtSignature"] = sig;
                                parts.Add(part);
                                break;
                            }
                        }
                    }
                    if (parts.Count == 0) continue;
                    contents.Add(new JsonObject { ["role"] = "model", ["parts"] = parts });
                    break;
                }
                case ToolResultMessage tr:
                {
                    var textResult = string.Join("\n", tr.Content.OfType<TextContent>().Select(c => c.Text));
                    var images = model.SupportsImages ? tr.Content.OfType<ImageContent>().ToList() : [];
                    var hasText = textResult.Length > 0;
                    var hasImages = images.Count > 0;
                    var multimodal = SupportsMultimodalFunctionResponse(model.Id);
                    var responseValue = hasText ? TextUtils.SanitizeSurrogates(textResult) : hasImages ? "(see attached image)" : "";

                    JsonArray ImageParts() => new(images.Select(img => (JsonNode)new JsonObject { ["inlineData"] = new JsonObject { ["mimeType"] = img.MimeType, ["data"] = img.Data } }).ToArray());

                    var functionResponse = new JsonObject
                    {
                        ["name"] = tr.ToolName,
                        ["response"] = tr.IsError ? new JsonObject { ["error"] = responseValue } : new JsonObject { ["output"] = responseValue },
                    };
                    if (hasImages && multimodal) functionResponse["parts"] = ImageParts();
                    if (RequiresToolCallId(model.Id)) functionResponse["id"] = tr.ToolCallId;
                    var functionResponsePart = new JsonObject { ["functionResponse"] = functionResponse };

                    if (contents.Count > 0 && contents[^1] is JsonObject last && last["role"]?.GetValue<string>() == "user"
                        && last["parts"] is JsonArray lastParts && lastParts.Any(p => p is JsonObject po && po.ContainsKey("functionResponse")))
                    {
                        lastParts.Add(functionResponsePart);
                    }
                    else
                    {
                        contents.Add(new JsonObject { ["role"] = "user", ["parts"] = new JsonArray(functionResponsePart) });
                    }

                    if (hasImages && !multimodal)
                    {
                        var parts = ImageParts();
                        parts.Insert(0, new JsonObject { ["text"] = "Tool result image:" });
                        contents.Add(new JsonObject { ["role"] = "user", ["parts"] = parts });
                    }
                    break;
                }
            }
        }
        return contents;
    }

    private static JsonNode? SanitizeForOpenApi(JsonNode? schema)
    {
        if (schema is JsonArray arr) return arr.DeepClone();
        if (schema is not JsonObject obj) return schema?.DeepClone();
        var result = new JsonObject();
        foreach (var (key, value) in obj)
        {
            if (JsonSchemaMetaDeclarations.Contains(key)) continue;
            result[key] = SanitizeForOpenApi(value);
        }
        return result;
    }

    public static JsonArray? ConvertTools(IReadOnlyList<Tool> tools, bool useParameters = false, bool supportsStrictMode = true)
    {
        if (tools.Count == 0) return null;
        var declarations = new JsonArray();
        foreach (var tool in tools)
        {
            var strict = ConstrainedSampling.ResolveJsonSchemaStrictSampling(tool, supportsStrictMode);
            var parameters = ConstrainedSampling.GetJsonSchemaToolParameters(tool, strict);
            var declaration = new JsonObject { ["name"] = tool.Name, ["description"] = tool.Description };
            if (useParameters) declaration["parameters"] = SanitizeForOpenApi(parameters);
            else declaration["parametersJsonSchema"] = parameters.DeepClone();
            declarations.Add(declaration);
        }
        return new JsonArray(new JsonObject { ["functionDeclarations"] = declarations });
    }

    public static string MapToolChoice(string choice) => choice switch
    {
        "none" => "NONE",
        "any" => "ANY",
        _ => "AUTO",
    };

    public static string? ResolveFunctionCallingMode(IReadOnlyList<Tool> tools, string? toolChoice, bool supportsStrictMode)
    {
        var useStrict = tools.Any(t => ConstrainedSampling.ResolveJsonSchemaStrictSampling(t, supportsStrictMode) == true);
        if (toolChoice is "none" or "any") return MapToolChoice(toolChoice);
        if (useStrict) return "VALIDATED";
        return toolChoice is not null ? MapToolChoice(toolChoice) : null;
    }

    public static StopReason MapStopReason(string reason) => reason switch
    {
        "STOP" => StopReason.Stop,
        "MAX_TOKENS" => StopReason.Length,
        "BLOCKLIST" or "PROHIBITED_CONTENT" or "SPII" or "SAFETY" or "IMAGE_SAFETY" or "IMAGE_PROHIBITED_CONTENT" or "IMAGE_RECITATION"
            or "IMAGE_OTHER" or "RECITATION" or "FINISH_REASON_UNSPECIFIED" or "OTHER" or "LANGUAGE" or "MALFORMED_FUNCTION_CALL"
            or "UNEXPECTED_TOOL_CALL" or "TOO_MANY_TOOL_CALLS" or "NO_IMAGE" => StopReason.Error,
        _ => throw new InvalidOperationException($"Unhandled stop reason: {reason}"),
    };
}

/// <summary>Google Generative AI (Gemini API) streaming adapter.</summary>
public sealed class GoogleGenerativeAIApi : IApiStreams
{
    public static readonly GoogleGenerativeAIApi Instance = new();

    private static long _toolCallCounter;

    private static readonly Regex Gemma4 = new("gemma-?4", RegexOptions.Compiled);
    private static readonly Regex Gemini3Pro = new(@"gemini-3(?:\.\d+)?-pro", RegexOptions.Compiled);
    private static readonly Regex Gemini3Flash = new(@"gemini-3(?:\.\d+)?-flash", RegexOptions.Compiled);

    private static bool IsGemma4(Model m) => Gemma4.IsMatch(m.Id.ToLowerInvariant());
    private static bool IsGemini3Pro(Model m) => Gemini3Pro.IsMatch(m.Id.ToLowerInvariant());
    private static bool IsGemini3Flash(Model m)
    {
        var id = m.Id.ToLowerInvariant();
        return Gemini3Flash.IsMatch(id) || id is "gemini-flash-latest" or "gemini-flash-lite-latest";
    }

    public AssistantMessageEventStream StreamSimple(Model model, Context context, SimpleStreamOptions? options = null)
    {
        var apiKey = options?.ApiKey;
        if (string.IsNullOrEmpty(apiKey)) throw new InvalidOperationException($"No API key for provider: {model.Provider}");
        var opts = SimpleOptions.BuildBaseOptions(new GoogleOptions(), model, context, options, apiKey);
        opts.ToolChoice = options?.ToolChoice switch
        {
            ToolChoice.Auto => "auto",
            ToolChoice.None => "none",
            _ => null,
        };
        if (options?.Reasoning is null)
        {
            opts.Thinking = new GoogleThinkingOptions { Enabled = false };
            return Stream(model, context, opts);
        }
        var clamped = ModelUtils.ClampThinkingLevel(model, options.Reasoning.Value);
        var resolved = GoogleShared.ResolveThinkingLevel(model, clamped);
        if (IsGemini3Pro(model) || IsGemini3Flash(model) || IsGemma4(model))
        {
            opts.Thinking = new GoogleThinkingOptions { Enabled = true, Level = GetThinkingLevel(resolved, model) };
            return Stream(model, context, opts);
        }
        opts.Thinking = new GoogleThinkingOptions { Enabled = true, BudgetTokens = GetGoogleBudget(model, resolved, options.ThinkingBudgets) };
        return Stream(model, context, opts);
    }

    public AssistantMessageEventStream Stream(Model model, Context context, StreamOptions? streamOptions = null)
    {
        var options = streamOptions as GoogleOptions
            ?? (streamOptions is null ? new GoogleOptions() : streamOptions.CopyTo(new GoogleOptions()));
        var stream = new AssistantMessageEventStream();
        _ = Task.Run(() => RunAsync(model, context, options, stream));
        return stream;
    }

    private static string? Str(JsonObject? obj, string name) => obj?[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static long Long(JsonObject? obj, string name) => obj?[name] is JsonValue v && IrisJson.TryGetNumber(v, out var d) ? (long)d : 0;

    private static async Task RunAsync(Model model, Context context, GoogleOptions options, AssistantMessageEventStream stream)
    {
        var ct = options.CancellationToken;
        var output = AssistantMessage.CreateEmpty(model);
        output.Api = KnownApis.GoogleGenerativeAI;
        try
        {
            var apiKey = options.ApiKey;
            if (string.IsNullOrEmpty(apiKey)) throw new InvalidOperationException($"No API key for provider: {model.Provider}");

            JsonNode payload = BuildParams(model, context, options);
            if (options.OnPayload is not null)
            {
                var next = await options.OnPayload(payload, model);
                if (next is not null) payload = next;
            }

            var (url, body) = ToRestRequest(model, (JsonObject)payload);
            var headers = ProviderHttp.MergeHeaders(
                new Dictionary<string, string?> { ["x-goog-api-key"] = apiKey, ["Content-Type"] = "application/json" },
                new Dictionary<string, string?> { ["User-Agent"] = IrisUserAgent.Get() },
                ProviderHttp.AsNullable(model.Headers),
                options.Headers);

            using var response = await PostGoogleAsync(url, body, headers, options, ct);
            stream.Push(new StartEvent(output));

            ContentBlock? currentBlock = null;
            var blocks = output.Content;
            int BlockIndex() => blocks.Count - 1;

            void EndCurrent()
            {
                if (currentBlock is TextContent t) stream.Push(new TextEndEvent(BlockIndex(), t.Text, output));
                else if (currentBlock is ThinkingContent th) stream.Push(new ThinkingEndEvent(BlockIndex(), th.Thinking, output));
            }

            await using var responseBody = await response.Content.ReadAsStreamAsync(ct);
            await foreach (var sse in ProviderHttp.ReadSseAsync(responseBody, ct))
            {
                if (string.IsNullOrWhiteSpace(sse.Data)) continue;
                if (JsonNode.Parse(sse.Data) is not JsonObject chunk) continue;
                if (chunk["error"] is JsonObject errorObj)
                {
                    var code = Long(errorObj, "code");
                    var msg = $"got status: {Str(errorObj, "status")}. {IrisJson.Stringify(chunk)}";
                    throw new ProviderHttpException(code is >= 400 and < 600 ? (int)code : null, null, msg);
                }

                if (string.IsNullOrEmpty(output.ResponseId) && Str(chunk, "responseId") is { Length: > 0 } rid) output.ResponseId = rid;
                var candidate = (chunk["candidates"] as JsonArray)?.FirstOrDefault() as JsonObject;
                if (candidate?["content"] is JsonObject content && content["parts"] is JsonArray parts)
                {
                    foreach (var partNode in parts)
                    {
                        if (partNode is not JsonObject part) continue;
                        if (Str(part, "text") is { } text)
                        {
                            var isThinking = GoogleShared.IsThinkingPart(part);
                            if (currentBlock is null || (isThinking && currentBlock is not ThinkingContent) || (!isThinking && currentBlock is not TextContent))
                            {
                                EndCurrent();
                                if (isThinking)
                                {
                                    currentBlock = new ThinkingContent("");
                                    blocks.Add(currentBlock);
                                    stream.Push(new ThinkingStartEvent(BlockIndex(), output));
                                }
                                else
                                {
                                    currentBlock = new TextContent("");
                                    blocks.Add(currentBlock);
                                    stream.Push(new TextStartEvent(BlockIndex(), output));
                                }
                            }
                            if (currentBlock is ThinkingContent thinkingBlock)
                            {
                                thinkingBlock.Thinking += text;
                                thinkingBlock.ThinkingSignature = GoogleShared.RetainThoughtSignature(thinkingBlock.ThinkingSignature, Str(part, "thoughtSignature"));
                                stream.Push(new ThinkingDeltaEvent(BlockIndex(), text, output));
                            }
                            else if (currentBlock is TextContent textBlock)
                            {
                                textBlock.Text += text;
                                textBlock.TextSignature = GoogleShared.RetainThoughtSignature(textBlock.TextSignature, Str(part, "thoughtSignature"));
                                stream.Push(new TextDeltaEvent(BlockIndex(), text, output));
                            }
                        }

                        if (part["functionCall"] is JsonObject functionCall)
                        {
                            if (currentBlock is not null)
                            {
                                EndCurrent();
                                currentBlock = null;
                            }
                            var providedId = Str(functionCall, "id");
                            var name = Str(functionCall, "name") ?? "";
                            var needsNewId = string.IsNullOrEmpty(providedId) || output.Content.Any(b => b is ToolCall tc && tc.Id == providedId);
                            var toolCallId = needsNewId ? $"{name}_{TimeUtil.NowMs()}_{Interlocked.Increment(ref _toolCallCounter)}" : providedId!;
                            var toolCall = new ToolCall
                            {
                                Id = toolCallId,
                                Name = name,
                                Arguments = functionCall["args"] is JsonObject args ? (JsonObject)args.DeepClone() : new JsonObject(),
                                ThoughtSignature = Str(part, "thoughtSignature") is { Length: > 0 } ts ? ts : null,
                            };
                            output.Content.Add(toolCall);
                            stream.Push(new ToolCallStartEvent(BlockIndex(), output));
                            stream.Push(new ToolCallDeltaEvent(BlockIndex(), IrisJson.Stringify(toolCall.Arguments), output));
                            stream.Push(new ToolCallEndEvent(BlockIndex(), toolCall, output));
                        }
                    }
                }

                if (Str(candidate, "finishReason") is { Length: > 0 } finishReason)
                {
                    output.RawStopReason = finishReason;
                    output.StopReason = GoogleShared.MapStopReason(finishReason);
                    if (output.Content.Any(b => b is ToolCall) && output.StopReason == StopReason.Stop) output.StopReason = StopReason.ToolUse;
                }

                if (chunk["usageMetadata"] is JsonObject usage)
                {
                    output.Usage = new Usage
                    {
                        Input = Long(usage, "promptTokenCount") - Long(usage, "cachedContentTokenCount"),
                        Output = Long(usage, "candidatesTokenCount") + Long(usage, "thoughtsTokenCount"),
                        CacheRead = Long(usage, "cachedContentTokenCount"),
                        CacheWrite = 0,
                        Reasoning = Long(usage, "thoughtsTokenCount"),
                        TotalTokens = Long(usage, "totalTokenCount"),
                    };
                    ModelUtils.CalculateCost(model, output.Usage);
                }
            }

            EndCurrent();
            ct.ThrowIfCancellationRequested();
            if (output.StopReason == StopReason.Pending) throw new InvalidOperationException("Google stream ended without a finish reason");
            if (output.StopReason is StopReason.Aborted or StopReason.Error)
            {
                throw new InvalidOperationException(output.RawStopReason is not null ? $"Provider stopped with: {output.RawStopReason}" : "An unknown error occurred");
            }
            stream.Push(new DoneEvent(output.StopReason, output));
            stream.End();
        }
        catch (Exception error)
        {
            var aborted = ct.IsCancellationRequested;
            output.StopReason = aborted ? StopReason.Aborted : StopReason.Error;
            output.ErrorMessage = aborted && error is OperationCanceledException ? "Request was aborted" : ProviderErrors.FormatException(error);
            stream.Push(new ErrorEvent(output.StopReason, output));
            stream.End();
        }
    }

    private static async Task<HttpResponseMessage> PostGoogleAsync(string url, JsonObject body, IReadOnlyDictionary<string, string> headers, GoogleOptions options, CancellationToken ct)
    {
        try
        {
            return await ProviderHttp.PostJsonAsync(url, body, headers, options, ProviderErrorStyle.OpenAI, ct);
        }
        catch (ProviderHttpException http) when (http.Status is not null)
        {
            // @google/genai formats HTTP errors as the JSON error body.
            JsonNode? parsed = null;
            try
            {
                parsed = string.IsNullOrWhiteSpace(http.RawBody) ? null : JsonNode.Parse(http.RawBody);
            }
            catch (JsonException)
            {
            }
            var message = parsed is not null
                ? IrisJson.Stringify(parsed)
                : IrisJson.Stringify(new JsonObject { ["error"] = new JsonObject { ["message"] = http.RawBody ?? "", ["code"] = http.Status } });
            throw new ProviderHttpException(http.Status, http.Headers, message);
        }
    }

    /// <summary>Convert SDK-style GenerateContentParameters into the REST request (URL + body).</summary>
    internal static (string Url, JsonObject Body) ToRestRequest(Model model, JsonObject parameters)
    {
        var modelName = Str(parameters, "model") ?? model.Id;
        var modelPath = modelName.StartsWith("models/", StringComparison.Ordinal) || modelName.StartsWith("tunedModels/", StringComparison.Ordinal) ? modelName : $"models/{modelName}";
        var baseUrl = string.IsNullOrEmpty(model.BaseUrl) ? "https://generativelanguage.googleapis.com/v1beta" : model.BaseUrl.TrimEnd('/');
        var url = $"{baseUrl}/{modelPath}:streamGenerateContent?alt=sse";

        var body = new JsonObject { ["contents"] = parameters["contents"]?.DeepClone() ?? new JsonArray() };
        if (parameters["config"] is JsonObject config)
        {
            var generationConfig = new JsonObject();
            foreach (var (key, value) in config)
            {
                switch (key)
                {
                    case "systemInstruction":
                        body["systemInstruction"] = value is JsonValue sv && sv.TryGetValue<string>(out var systemText)
                            ? new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = systemText }), ["role"] = "user" }
                            : value?.DeepClone();
                        break;
                    case "tools":
                    case "toolConfig":
                    case "safetySettings":
                    case "cachedContent":
                        body[key] = value?.DeepClone();
                        break;
                    default:
                        generationConfig[key] = value?.DeepClone();
                        break;
                }
            }
            if (generationConfig.Count > 0) body["generationConfig"] = generationConfig;
        }
        return (url, body);
    }

    internal static JsonObject BuildParams(Model model, Context context, GoogleOptions? options)
    {
        options ??= new GoogleOptions();
        var contents = GoogleShared.ConvertMessages(model, context);
        var config = new JsonObject();
        if (options.Temperature is not null) config["temperature"] = options.Temperature;
        if (options.MaxTokens is not null) config["maxOutputTokens"] = options.MaxTokens;

        var supportsStrict = GoogleShared.SupportsStrictToolSampling(model.Id);
        var mode = context.Tools is { Count: > 0 } ? GoogleShared.ResolveFunctionCallingMode(context.Tools, options.ToolChoice, supportsStrict) : null;
        if (!string.IsNullOrEmpty(context.SystemPrompt)) config["systemInstruction"] = TextUtils.SanitizeSurrogates(context.SystemPrompt);
        if (context.Tools is { Count: > 0 }) config["tools"] = GoogleShared.ConvertTools(context.Tools, false, supportsStrict);
        if (mode is not null) config["toolConfig"] = new JsonObject { ["functionCallingConfig"] = new JsonObject { ["mode"] = mode } };

        if (options.Thinking?.Enabled == true && model.Reasoning)
        {
            var thinkingConfig = new JsonObject { ["includeThoughts"] = true };
            if (options.Thinking.Level is not null) thinkingConfig["thinkingLevel"] = options.Thinking.Level;
            else if (options.Thinking.BudgetTokens is not null) thinkingConfig["thinkingBudget"] = options.Thinking.BudgetTokens;
            config["thinkingConfig"] = thinkingConfig;
        }
        else if (model.Reasoning && options.Thinking is { Enabled: false })
        {
            config["thinkingConfig"] = IsGemini3Pro(model)
                ? new JsonObject { ["thinkingLevel"] = "LOW" }
                : IsGemini3Flash(model) || IsGemma4(model)
                    ? new JsonObject { ["thinkingLevel"] = "MINIMAL" }
                    : new JsonObject { ["thinkingBudget"] = 0 };
        }

        if (options.CancellationToken.IsCancellationRequested) throw new OperationCanceledException("Request aborted");

        return new JsonObject { ["model"] = model.Id, ["contents"] = contents, ["config"] = config };
    }

    private static string GetThinkingLevel(string effort, Model model)
    {
        if (IsGemini3Pro(model)) return effort is "minimal" or "low" ? "LOW" : "HIGH";
        if (IsGemma4(model)) return effort is "minimal" or "low" ? "MINIMAL" : "HIGH";
        return effort switch
        {
            "minimal" => "MINIMAL",
            "low" => "LOW",
            "medium" => "MEDIUM",
            _ => "HIGH",
        };
    }

    private static long GetGoogleBudget(Model model, string level, ThinkingBudgets? custom)
    {
        int? customValue = level switch
        {
            "minimal" => custom?.Minimal,
            "low" => custom?.Low,
            "medium" => custom?.Medium,
            _ => custom?.High,
        };
        if (customValue is not null) return customValue.Value;

        long Pick(long minimal, long low, long medium, long high) => level switch
        {
            "minimal" => minimal,
            "low" => low,
            "medium" => medium,
            _ => high,
        };
        if (model.Id.Contains("2.5-pro")) return Pick(128, 2048, 8192, 32768);
        if (model.Id.Contains("2.5-flash-lite")) return Pick(512, 2048, 8192, 24576);
        if (model.Id.Contains("2.5-flash")) return Pick(128, 2048, 8192, 24576);
        return -1;
    }
}
