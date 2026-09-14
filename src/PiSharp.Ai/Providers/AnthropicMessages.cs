using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.Ai.Json;
using PiSharp.Ai.Utils;

namespace PiSharp.Ai.Providers;

public sealed class AnthropicOptions : StreamOptions
{
    public bool? ThinkingEnabled { get; set; }

    /// <summary>Token budget for extended thinking (older models only).</summary>
    public long? ThinkingBudgetTokens { get; set; }

    /// <summary>low | medium | high | xhigh | max (adaptive thinking models).</summary>
    public string? Effort { get; set; }

    /// <summary>summarized | omitted. Default summarized when thinking is enabled.</summary>
    public string? ThinkingDisplay { get; set; }

    public bool? InterleavedThinking { get; set; }

    /// <summary>"auto" | "any" | "none" or {"type":"tool","name":...}.</summary>
    public JsonNode? ToolChoice { get; set; }
}

internal sealed record ResolvedAnthropicCompat(
    bool SupportsEagerToolInputStreaming,
    bool SupportsLongCacheRetention,
    bool SendSessionAffinityHeaders,
    string? SessionAffinityFormat,
    bool SupportsCacheControlOnTools,
    bool SupportsTemperature,
    bool AllowEmptySignature,
    bool SupportsStrictTools,
    bool SupportsToolReferences);

/// <summary>Anthropic Messages streaming adapter. Port of api/anthropic-messages.ts.</summary>
public sealed class AnthropicMessagesApi : IApiStreams
{
    public static readonly AnthropicMessagesApi Instance = new();

    private const string ClaudeCodeVersion = "2.1.251";
    private const string FineGrainedToolStreamingBeta = "fine-grained-tool-streaming-2025-05-14";
    private const string InterleavedThinkingBeta = "interleaved-thinking-2025-05-14";
    private const string ServerSideFallbackBeta = "server-side-fallback-2026-07-01";
    private const string MidConversationOutputConfigBeta = "mid-conversation-output-config-2026-07-01";
    private const string ThinkingBindingControlsBeta = "thinking-binding-controls-2026-08-01";

    private static readonly string[] ClaudeCodeTools =
    [
        "Read", "Write", "Edit", "Bash", "Grep", "Glob", "AskUserQuestion", "EnterPlanMode", "ExitPlanMode", "KillShell",
        "NotebookEdit", "Skill", "Task", "TaskOutput", "TodoWrite", "WebFetch", "WebSearch",
    ];

    private static readonly Dictionary<string, string> CcToolLookup = ClaudeCodeTools.ToDictionary(t => t.ToLowerInvariant(), t => t);

    private static readonly HashSet<string> AnthropicMessageEvents =
        ["message_start", "message_delta", "message_stop", "content_block_start", "content_block_delta", "content_block_stop"];

    internal static string ToClaudeCodeName(string name) => CcToolLookup.GetValueOrDefault(name.ToLowerInvariant()) ?? name;

    internal static string FromClaudeCodeName(string name, IReadOnlyList<Tool>? tools)
    {
        if (tools is { Count: > 0 })
        {
            var match = tools.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match.Name;
        }
        return name;
    }

    private static CacheRetention ResolveCacheRetention(CacheRetention? retention, IReadOnlyDictionary<string, string>? env) =>
        SimpleOptions.ResolveCacheRetention(retention, env);

    private static JsonObject? GetCacheControl(Model model, CacheRetention? cacheRetention, IReadOnlyDictionary<string, string>? env)
    {
        var retention = ResolveCacheRetention(cacheRetention, env);
        if (retention == CacheRetention.None) return null;
        var control = new JsonObject { ["type"] = "ephemeral" };
        if (retention == CacheRetention.Long && GetCompat(model).SupportsLongCacheRetention) control["ttl"] = "1h";
        return control;
    }

    internal static ResolvedAnthropicCompat GetCompat(Model model)
    {
        var c = model.GetCompat<AnthropicMessagesCompat>();
        var isOpenRouter = model.Provider == "openrouter" || model.BaseUrl.Contains("openrouter.ai");
        return new ResolvedAnthropicCompat(
            c.SupportsEagerToolInputStreaming ?? true,
            c.SupportsLongCacheRetention ?? true,
            c.SendSessionAffinityHeaders ?? isOpenRouter,
            c.SessionAffinityFormat ?? (isOpenRouter ? "openrouter" : null),
            c.SupportsCacheControlOnTools ?? true,
            c.SupportsTemperature ?? true,
            c.AllowEmptySignature ?? false,
            c.SupportsStrictTools ?? false,
            c.SupportsToolReferences ?? DefaultSupportsToolReferences(model));
    }

    private static readonly Regex ClaudeVersion = new(@"^claude-(?:opus|sonnet|fable)-(\d+)(?:-(\d+))?(?:-|$)", RegexOptions.Compiled);

    private static bool DefaultSupportsToolReferences(Model model)
    {
        if (model.Provider != "anthropic" || model.Id.Contains("haiku")) return false;
        var match = ClaudeVersion.Match(model.Id);
        if (!match.Success) return false;
        var major = int.Parse(match.Groups[1].Value);
        var minor = match.Groups[2].Success && match.Groups[2].Value.Length < 8 ? int.Parse(match.Groups[2].Value) : 0;
        return major > 4 || (major == 4 && minor >= 5);
    }

    private static bool HasHeader(IReadOnlyDictionary<string, string?>? headers, string name) =>
        headers is not null && headers.Any(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(kv.Value));

    private static void AssertRequestAuth(string provider, string? apiKey, IReadOnlyDictionary<string, string?>? headers)
    {
        if (!string.IsNullOrEmpty(apiKey)) return;
        if (HasHeader(headers, "authorization") || HasHeader(headers, "x-api-key") || HasHeader(headers, "cf-aig-authorization")) return;
        throw new InvalidOperationException($"No API key for provider: {provider}");
    }

    private static string MapThinkingLevelToEffort(Model model, ThinkingLevel level)
    {
        if (model.TryGetThinkingMapping(level, out var mapped) && mapped is not null) return mapped;
        return level switch
        {
            ThinkingLevel.Minimal or ThinkingLevel.Low => "low",
            ThinkingLevel.Medium => "medium",
            _ => "high",
        };
    }

    public AssistantMessageEventStream StreamSimple(Model model, Context context, SimpleStreamOptions? options = null)
    {
        AssertRequestAuth(model.Provider, options?.ApiKey, options?.Headers);
        var baseOptions = SimpleOptions.BuildBaseOptions(new AnthropicOptions(), model, context, options, options?.ApiKey);
        baseOptions.ToolChoice = options?.ToolChoice switch
        {
            ToolChoice.Auto => "auto",
            ToolChoice.None => "none",
            _ => null,
        };

        if (options?.Reasoning is null)
        {
            baseOptions.ThinkingEnabled = false;
            return Stream(model, context, baseOptions);
        }

        var compat = model.GetCompat<AnthropicMessagesCompat>();
        if (compat.ForceAdaptiveThinking == true)
        {
            baseOptions.ThinkingEnabled = true;
            baseOptions.Effort = MapThinkingLevelToEffort(model, options.Reasoning.Value);
            return Stream(model, context, baseOptions);
        }

        // BuildBaseOptions always sets MaxTokens (clamped); TS passes the clamped base value too.
        var adjusted = SimpleOptions.AdjustMaxTokensForThinking(baseOptions.MaxTokens, model.MaxTokens, options.Reasoning.Value, options.ThinkingBudgets);
        var maxTokens = SimpleOptions.ClampMaxTokensToContext(model, context, adjusted.MaxTokens);
        baseOptions.MaxTokens = (int)maxTokens;
        baseOptions.ThinkingEnabled = true;
        baseOptions.ThinkingBudgetTokens = Math.Min(adjusted.ThinkingBudget, Math.Max(0, maxTokens - 1024));
        return Stream(model, context, baseOptions);
    }

    public AssistantMessageEventStream Stream(Model model, Context context, StreamOptions? streamOptions = null)
    {
        var options = streamOptions as AnthropicOptions
            ?? (streamOptions is null ? new AnthropicOptions() : streamOptions.CopyTo(new AnthropicOptions()));
        var stream = new AssistantMessageEventStream();
        _ = Task.Run(() => RunAsync(model, context, options, stream));
        return stream;
    }

    private sealed class BlockState
    {
        public required ContentBlock Block { get; init; }
        public int Index { get; init; }
        public string PartialJson { get; set; } = "";
    }

    private static async Task RunAsync(Model model, Context context, AnthropicOptions options, AssistantMessageEventStream stream)
    {
        var ct = options.CancellationToken;
        var compatRaw = model.GetCompat<AnthropicMessagesCompat>();
        var output = AssistantMessage.CreateEmpty(model);
        if (compatRaw.SupportsMidConvoEffort == true) output.ProviderThinkingLevel = options.Effort ?? "high";

        try
        {
            var apiKey = options.ApiKey;
            AssertRequestAuth(model.Provider, apiKey, options.Headers);
            var cacheRetention = ResolveCacheRetention(options.CacheRetention, options.Env);
            var cacheSessionId = cacheRetention == CacheRetention.None ? null : options.SessionId;
            var (headers, isOAuth) = BuildHeaders(model, context, apiKey, options.Headers, cacheSessionId);

            var payload = BuildParams(model, context, isOAuth, options);
            JsonNode requestBody = payload;
            if (options.OnPayload is not null)
            {
                var next = await options.OnPayload(payload, model);
                if (next is JsonObject nextObj)
                {
                    nextObj["stream"] = true;
                    requestBody = nextObj;
                }
            }

            // The SDK moves `betas` from the body into the anthropic-beta header.
            if (requestBody is JsonObject bodyObj && bodyObj["betas"] is JsonArray betas)
            {
                bodyObj.Remove("betas");
                if (betas.Count > 0) headers["anthropic-beta"] = string.Join(",", betas.Select(b => b!.GetValue<string>()));
            }

            var url = model.BaseUrl.TrimEnd('/') + "/v1/messages?beta=true";
            using var response = await ProviderHttp.PostJsonAsync(url, requestBody, headers, options, ProviderErrorStyle.Anthropic, ct);
            if (options.OnResponse is not null)
            {
                await options.OnResponse(new ProviderResponse((int)response.StatusCode, ProviderHttp.HeadersToRecord(response)), model);
            }
            stream.Push(new StartEvent(output));

            var usageModel = model;
            JsonArray? inputTransformations = null;
            var blocks = new List<BlockState>();
            var sawMessageStart = false;
            var sawMessageEnd = false;

            BlockState? FindBlock(int index, out int contentIndex)
            {
                for (var i = 0; i < blocks.Count; i++)
                {
                    if (blocks[i].Index == index)
                    {
                        contentIndex = output.Content.IndexOf(blocks[i].Block);
                        return blocks[i];
                    }
                }
                contentIndex = -1;
                return null;
            }

            await using var body = await response.Content.ReadAsStreamAsync(ct);
            await foreach (var sse in ProviderHttp.ReadSseAsync(body, ct))
            {
                if (sse.Event == "error") throw new InvalidOperationException(sse.Data);
                if (sse.Event is null || !AnthropicMessageEvents.Contains(sse.Event)) continue;

                JsonObject evt;
                try
                {
                    evt = JsonParse.ParseJsonWithRepair(sse.Data) as JsonObject ?? throw new JsonException("Event is not an object");
                }
                catch (Exception ex) when (ex is JsonException or FormatException)
                {
                    throw new InvalidOperationException($"Could not parse Anthropic SSE event {sse.Event}: {ex.Message}; data={sse.Data}");
                }

                var type = Str(evt, "type");
                if (type == "message_start") sawMessageStart = true;
                else if (type == "message_stop") sawMessageEnd = true;

                switch (type)
                {
                    case "message_start":
                    {
                        var message = evt["message"] as JsonObject ?? new JsonObject();
                        output.ResponseId = Str(message, "id");
                        if (message["input_transformations"] is JsonArray transformations) inputTransformations = transformations;
                        output.Model = Str(message, "model") ?? output.Model;
                        if (output.Model != model.Id)
                        {
                            var fallback = compatRaw.AllowedFallbackModels?.FirstOrDefault(f => f.Provider == model.Provider && f.Model == output.Model);
                            if (fallback is not null)
                            {
                                usageModel = model.Clone();
                                usageModel.Id = output.Model;
                                usageModel.Cost = fallback.Cost;
                            }
                        }
                        var usage = message["usage"] as JsonObject;
                        output.Usage.Input = Long(usage, "input_tokens") ?? 0;
                        output.Usage.Output = Long(usage, "output_tokens") ?? 0;
                        output.Usage.CacheRead = Long(usage, "cache_read_input_tokens") ?? 0;
                        output.Usage.CacheWrite = Long(usage, "cache_creation_input_tokens") ?? 0;
                        output.Usage.CacheWrite1h = Long(usage?["cache_creation"] as JsonObject, "ephemeral_1h_input_tokens") ?? 0;
                        output.Usage.TotalTokens = output.Usage.Input + output.Usage.Output + output.Usage.CacheRead + output.Usage.CacheWrite;
                        ModelUtils.CalculateCost(usageModel, output.Usage);
                        break;
                    }
                    case "content_block_start":
                    {
                        var index = (int)(Long(evt, "index") ?? 0);
                        var block = evt["content_block"] as JsonObject ?? new JsonObject();
                        switch (Str(block, "type"))
                        {
                            case "fallback":
                                if (output.Content.Count > 0) throw new InvalidOperationException("Anthropic performed an unsupported mid-output model fallback");
                                break;
                            case "text":
                            {
                                var text = new TextContent(Str(block, "text") ?? "");
                                output.Content.Add(text);
                                blocks.Add(new BlockState { Block = text, Index = index });
                                stream.Push(new TextStartEvent(output.Content.Count - 1, output));
                                break;
                            }
                            case "thinking":
                            {
                                var thinking = new ThinkingContent(Str(block, "thinking") ?? "") { ThinkingSignature = Str(block, "signature") ?? "" };
                                output.Content.Add(thinking);
                                blocks.Add(new BlockState { Block = thinking, Index = index });
                                stream.Push(new ThinkingStartEvent(output.Content.Count - 1, output));
                                break;
                            }
                            case "redacted_thinking":
                            {
                                var thinking = new ThinkingContent("[Reasoning redacted]") { ThinkingSignature = Str(block, "data"), Redacted = true };
                                output.Content.Add(thinking);
                                blocks.Add(new BlockState { Block = thinking, Index = index });
                                stream.Push(new ThinkingStartEvent(output.Content.Count - 1, output));
                                break;
                            }
                            case "tool_use":
                            {
                                var name = Str(block, "name") ?? "";
                                var toolCall = new ToolCall
                                {
                                    Id = Str(block, "id") ?? "",
                                    Name = isOAuth ? FromClaudeCodeName(name, context.Tools) : name,
                                    Arguments = block["input"] is JsonObject input ? (JsonObject)input.DeepClone() : new JsonObject(),
                                };
                                output.Content.Add(toolCall);
                                blocks.Add(new BlockState { Block = toolCall, Index = index });
                                stream.Push(new ToolCallStartEvent(output.Content.Count - 1, output));
                                break;
                            }
                        }
                        break;
                    }
                    case "content_block_delta":
                    {
                        var index = (int)(Long(evt, "index") ?? 0);
                        var delta = evt["delta"] as JsonObject ?? new JsonObject();
                        var state = FindBlock(index, out var contentIndex);
                        if (state is null) break;
                        switch (Str(delta, "type"))
                        {
                            case "text_delta" when state.Block is TextContent text:
                            {
                                var d = Str(delta, "text") ?? "";
                                text.Text += d;
                                stream.Push(new TextDeltaEvent(contentIndex, d, output));
                                break;
                            }
                            case "thinking_delta" when state.Block is ThinkingContent thinking:
                            {
                                var d = Str(delta, "thinking") ?? "";
                                thinking.Thinking += d;
                                stream.Push(new ThinkingDeltaEvent(contentIndex, d, output));
                                break;
                            }
                            case "input_json_delta" when state.Block is ToolCall toolCall:
                            {
                                var d = Str(delta, "partial_json") ?? "";
                                state.PartialJson += d;
                                toolCall.Arguments = JsonParse.ParseStreamingJson(state.PartialJson);
                                stream.Push(new ToolCallDeltaEvent(contentIndex, d, output));
                                break;
                            }
                            case "signature_delta" when state.Block is ThinkingContent thinking:
                                thinking.ThinkingSignature = (thinking.ThinkingSignature ?? "") + (Str(delta, "signature") ?? "");
                                break;
                        }
                        break;
                    }
                    case "content_block_stop":
                    {
                        var index = (int)(Long(evt, "index") ?? 0);
                        var state = FindBlock(index, out var contentIndex);
                        if (state is null) break;
                        blocks.Remove(state);
                        switch (state.Block)
                        {
                            case TextContent text:
                                stream.Push(new TextEndEvent(contentIndex, text.Text, output));
                                break;
                            case ThinkingContent thinking:
                                stream.Push(new ThinkingEndEvent(contentIndex, thinking.Thinking, output));
                                break;
                            case ToolCall toolCall:
                                toolCall.Arguments = JsonParse.ParseStreamingJson(state.PartialJson);
                                stream.Push(new ToolCallEndEvent(contentIndex, toolCall, output));
                                break;
                        }
                        break;
                    }
                    case "message_delta":
                    {
                        if (evt["input_transformations"] is JsonArray transformations) inputTransformations = transformations;
                        var delta = evt["delta"] as JsonObject;
                        if (Str(delta, "stop_reason") is { } stopReason)
                        {
                            output.RawStopReason = stopReason;
                            var (mapped, errorMessage) = MapStopReason(stopReason, delta?["stop_details"] as JsonObject);
                            output.StopReason = mapped;
                            if (errorMessage is not null) output.ErrorMessage = errorMessage;
                        }
                        if (evt["usage"] is JsonObject usage)
                        {
                            if (Long(usage, "input_tokens") is { } input) output.Usage.Input = input;
                            if (Long(usage, "output_tokens") is { } outTokens) output.Usage.Output = outTokens;
                            if (Long(usage, "cache_read_input_tokens") is { } cacheRead) output.Usage.CacheRead = cacheRead;
                            if (Long(usage, "cache_creation_input_tokens") is { } cacheWrite) output.Usage.CacheWrite = cacheWrite;
                            if (Long(usage["output_tokens_details"] as JsonObject, "thinking_tokens") is { } thinkingTokens) output.Usage.Reasoning = thinkingTokens;
                        }
                        output.Usage.TotalTokens = output.Usage.Input + output.Usage.Output + output.Usage.CacheRead + output.Usage.CacheWrite;
                        ModelUtils.CalculateCost(usageModel, output.Usage);
                        break;
                    }
                }
            }

            if (sawMessageStart && !sawMessageEnd) throw new InvalidOperationException("Anthropic stream ended before message_stop");
            ct.ThrowIfCancellationRequested();
            if (output.StopReason == StopReason.Pending) throw new InvalidOperationException("Anthropic stream ended without a stop reason");
            if (output.StopReason is StopReason.Aborted or StopReason.Error)
                throw new InvalidOperationException(output.ErrorMessage ?? "An unknown error occurred");

            if (inputTransformations is { Count: > 0 })
            {
                var transformations = new JsonArray();
                foreach (var t in inputTransformations.OfType<JsonObject>())
                {
                    var entry = new JsonObject();
                    if (t["type"] is { } tt) entry["type"] = tt.DeepClone();
                    if (t["path"] is { } tp) entry["path"] = tp.DeepClone();
                    if (t["reason"] is { } tr) entry["reason"] = tr.DeepClone();
                    transformations.Add(entry);
                }
                output.Diagnostics = [.. output.Diagnostics ?? [], new AssistantMessageDiagnostic
                {
                    Type = "anthropic_input_transformations",
                    Timestamp = TimeUtil.NowMs(),
                    Details = new JsonObject { ["transformations"] = transformations },
                }];
            }

            stream.Push(new DoneEvent(output.StopReason, output));
            stream.End();
        }
        catch (Exception error)
        {
            var aborted = ct.IsCancellationRequested;
            output.StopReason = aborted ? StopReason.Aborted : StopReason.Error;
            output.ErrorMessage = aborted && error is OperationCanceledException ? "Request was aborted" : ProviderErrors.DescribeException(error);
            stream.Push(new ErrorEvent(output.StopReason, output));
            stream.End();
        }
    }

    private static string? Str(JsonObject? obj, string name) => obj?[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static long? Long(JsonObject? obj, string name) => obj?[name] is JsonValue v && PiJson.TryGetNumber(v, out var d) ? (long)d : null;

    private static (Dictionary<string, string> Headers, bool IsOAuth) BuildHeaders(Model model, Context context, string? apiKey, IReadOnlyDictionary<string, string?>? optionsHeaders, string? sessionId)
    {
        var sdkDefaults = new Dictionary<string, string?>
        {
            ["anthropic-version"] = "2023-06-01",
            ["content-type"] = "application/json",
        };

        if (model.Provider == "github-copilot")
        {
            var dynamicHeaders = GithubCopilotHeaders.BuildDynamicHeaders(context.Messages, GithubCopilotHeaders.HasVisionInput(context.Messages));
            var auth = new Dictionary<string, string?>();
            if (!string.IsNullOrEmpty(apiKey)) auth["Authorization"] = $"Bearer {apiKey}";
            return (ProviderHttp.MergeHeaders(
                sdkDefaults, auth,
                new Dictionary<string, string?> { ["User-Agent"] = PiUserAgent.Get() },
                new Dictionary<string, string?> { ["accept"] = "application/json", ["anthropic-dangerous-direct-browser-access"] = "true" },
                ProviderHttp.AsNullable(model.Headers),
                dynamicHeaders.ToDictionary(kv => kv.Key, kv => (string?)kv.Value),
                optionsHeaders), false);
        }

        if (!string.IsNullOrEmpty(apiKey) && apiKey.Contains("sk-ant-oat"))
        {
            return (ProviderHttp.MergeHeaders(
                sdkDefaults,
                new Dictionary<string, string?> { ["Authorization"] = $"Bearer {apiKey}" },
                new Dictionary<string, string?> { ["User-Agent"] = PiUserAgent.Get() },
                new Dictionary<string, string?>
                {
                    ["accept"] = "application/json",
                    ["anthropic-dangerous-direct-browser-access"] = "true",
                    ["user-agent"] = $"claude-cli/{ClaudeCodeVersion}",
                    ["x-app"] = "cli",
                },
                ProviderHttp.AsNullable(model.Headers),
                optionsHeaders), true);
        }

        var compat = GetCompat(model);
        var affinity = new Dictionary<string, string?>();
        if (sessionId is not null && compat.SendSessionAffinityHeaders)
        {
            affinity[compat.SessionAffinityFormat == "openrouter" ? "x-session-id" : "x-session-affinity"] = sessionId;
        }
        var keyHeader = new Dictionary<string, string?>();
        if (!string.IsNullOrEmpty(apiKey)) keyHeader["x-api-key"] = apiKey;
        return (ProviderHttp.MergeHeaders(
            sdkDefaults, keyHeader,
            new Dictionary<string, string?> { ["User-Agent"] = PiUserAgent.Get() },
            new Dictionary<string, string?> { ["accept"] = "application/json", ["anthropic-dangerous-direct-browser-access"] = "true" },
            affinity,
            ProviderHttp.AsNullable(model.Headers),
            optionsHeaders), false);
    }

    private static List<string> GetBetaFeatures(Model model, Context context, bool isOAuth, AnthropicOptions? options)
    {
        string? configured = null;
        var configuredNull = false;
        foreach (var source in new IEnumerable<KeyValuePair<string, string?>>?[] { ProviderHttp.AsNullable(model.Headers), options?.Headers })
        {
            foreach (var (name, value) in source ?? [])
            {
                if (string.Equals(name, "anthropic-beta", StringComparison.OrdinalIgnoreCase))
                {
                    configured = value;
                    configuredNull = value is null;
                }
            }
        }
        if (configuredNull) return [];
        if (configured is not null)
        {
            return configured.Split(',').Select(f => f.Trim()).Where(f => f.Length > 0).Distinct().ToList();
        }

        var compatRaw = model.GetCompat<AnthropicMessagesCompat>();
        var features = new List<string>();
        if (isOAuth) features.AddRange(["claude-code-20250219", "oauth-2025-04-20"]);
        if (context.Tools is { Count: > 0 } && !GetCompat(model).SupportsEagerToolInputStreaming) features.Add(FineGrainedToolStreamingBeta);
        if (model.Reasoning && options?.ThinkingEnabled == true && (options.InterleavedThinking ?? true) && compatRaw.ForceAdaptiveThinking != true)
            features.Add(InterleavedThinkingBeta);
        if (compatRaw.AllowedFallbackModels is { Count: > 0 }) features.Add(ServerSideFallbackBeta);
        if (compatRaw.SupportsMidConvoEffort == true) features.AddRange([MidConversationOutputConfigBeta, ThinkingBindingControlsBeta]);
        return features.Distinct().ToList();
    }

    private static readonly Regex NonIdChars = new("[^a-zA-Z0-9_-]", RegexOptions.Compiled);

    internal static string NormalizeToolCallId(string id)
    {
        var normalized = NonIdChars.Replace(id, "_");
        return normalized.Length > 64 ? normalized[..64] : normalized;
    }

    internal static (List<Tool> Immediate, Dictionary<string, Tool> Deferred) SplitDeferredTools(IReadOnlyList<Message> messages, IReadOnlyList<Tool>? tools, bool enabled, Func<string, string> normalizeName)
    {
        var unique = new Dictionary<string, Tool>();
        var order = new List<string>();
        foreach (var tool in tools ?? [])
        {
            var key = normalizeName(tool.Name);
            if (!unique.ContainsKey(key)) order.Add(key);
            unique[key] = tool;
        }
        if (!enabled) return (order.Select(k => unique[k]).ToList(), new Dictionary<string, Tool>());

        var deferredNames = new HashSet<string>();
        var usedNames = new HashSet<string>();
        foreach (var message in messages)
        {
            if (message is AssistantMessage assistant)
            {
                foreach (var block in assistant.Content.OfType<ToolCall>()) usedNames.Add(normalizeName(block.Name));
            }
            else if (message is ToolResultMessage tr)
            {
                foreach (var name in tr.AddedToolNames ?? [])
                {
                    var normalized = normalizeName(name);
                    if (!usedNames.Contains(normalized)) deferredNames.Add(normalized);
                }
            }
        }

        var immediate = new List<Tool>();
        var deferred = new Dictionary<string, Tool>();
        foreach (var key in order)
        {
            if (deferredNames.Contains(key)) deferred[key] = unique[key];
            else immediate.Add(unique[key]);
        }
        return (immediate, deferred);
    }

    internal static JsonObject BuildParams(Model model, Context context, bool isOAuth, AnthropicOptions? options)
    {
        var cacheControl = GetCacheControl(model, options?.CacheRetention, options?.Env);
        var compat = GetCompat(model);
        var compatRaw = model.GetCompat<AnthropicMessagesCompat>();
        var transformed = MessageTransformer.Transform(context.Messages, model, (id, _, _) => NormalizeToolCallId(id));
        Func<string, string> normalizeToolName = isOAuth ? ToClaudeCodeName : name => name;
        var (immediateTools, deferredMap) = SplitDeferredTools(transformed, context.Tools, compat.SupportsToolReferences, normalizeToolName);
        var deferredTools = deferredMap.Values.ToList();
        if (immediateTools.Count == 0 && deferredTools.Count > 0)
        {
            immediateTools = deferredTools;
            deferredTools = [];
        }
        var deferredToolNames = deferredTools.Select(t => normalizeToolName(t.Name)).ToHashSet();
        var (messages, assistantLevels) = ConvertMessages(transformed, isOAuth, cacheControl, compat.AllowEmptySignature, deferredToolNames, normalizeToolName,
            compatRaw.SupportsMidConvoEffort == true ? model.Provider : null);
        var activeEffort = options?.Effort ?? "high";
        var betaFeatures = GetBetaFeatures(model, context, isOAuth, options);

        var p = new JsonObject
        {
            ["model"] = model.Id,
            ["messages"] = compatRaw.SupportsMidConvoEffort == true ? InsertThinkingLevelMessages(messages, assistantLevels, activeEffort) : messages,
            ["max_tokens"] = options?.MaxTokens ?? model.MaxTokens,
            ["stream"] = true,
        };
        if (betaFeatures.Count > 0) p["betas"] = new JsonArray(betaFeatures.Select(f => (JsonNode)f).ToArray());

        JsonObject SystemBlock(string text)
        {
            var block = new JsonObject { ["type"] = "text", ["text"] = text };
            if (cacheControl is not null) block["cache_control"] = cacheControl.DeepClone();
            return block;
        }

        if (isOAuth)
        {
            var system = new JsonArray(SystemBlock("You are Claude Code, Anthropic's official CLI for Claude."));
            if (!string.IsNullOrEmpty(context.SystemPrompt)) system.Add(SystemBlock(TextUtils.SanitizeSurrogates(context.SystemPrompt)));
            p["system"] = system;
        }
        else if (!string.IsNullOrEmpty(context.SystemPrompt))
        {
            p["system"] = new JsonArray(SystemBlock(TextUtils.SanitizeSurrogates(context.SystemPrompt)));
        }

        if (options?.Temperature is not null && options.ThinkingEnabled != true && compatRaw.SupportsMidConvoEffort != true && compat.SupportsTemperature)
        {
            p["temperature"] = options.Temperature;
        }

        if (immediateTools.Count > 0 || deferredTools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var t in ConvertTools(immediateTools, isOAuth, compat.SupportsEagerToolInputStreaming, compat.SupportsStrictTools, compat.SupportsCacheControlOnTools ? cacheControl : null, false)) tools.Add(t);
            foreach (var t in ConvertTools(deferredTools, isOAuth, compat.SupportsEagerToolInputStreaming, compat.SupportsStrictTools, null, true)) tools.Add(t);
            p["tools"] = tools;
        }

        if (compatRaw.SupportsMidConvoEffort == true)
        {
            p["thinking"] = new JsonObject
            {
                ["type"] = "adaptive",
                ["display"] = options?.ThinkingDisplay ?? "summarized",
                ["block_binding"] = new JsonObject { ["prefix_mismatch_behavior"] = "drop_block" },
            };
            p["output_config"] = new JsonObject { ["effort"] = "high" };
        }
        else if (model.Reasoning)
        {
            if (options?.ThinkingEnabled == true)
            {
                var display = options.ThinkingDisplay ?? "summarized";
                if (compatRaw.ForceAdaptiveThinking == true)
                {
                    p["thinking"] = new JsonObject { ["type"] = "adaptive", ["display"] = display };
                    if (options.Effort is not null) p["output_config"] = new JsonObject { ["effort"] = options.Effort };
                }
                else
                {
                    p["thinking"] = new JsonObject
                    {
                        ["type"] = "enabled",
                        ["budget_tokens"] = options.ThinkingBudgetTokens is > 0 ? options.ThinkingBudgetTokens : 1024,
                        ["display"] = display,
                    };
                }
            }
            else if (options?.ThinkingEnabled == false && !(model.TryGetThinkingMapping(ThinkingLevel.Off, out var off) && off is null))
            {
                p["thinking"] = new JsonObject { ["type"] = "disabled" };
            }
        }

        if (options?.Metadata?["user_id"] is JsonValue uid && uid.TryGetValue<string>(out var userId))
        {
            p["metadata"] = new JsonObject { ["user_id"] = userId };
        }

        if (options?.ToolChoice is not null)
        {
            p["tool_choice"] = options.ToolChoice is JsonValue tcv && tcv.TryGetValue<string>(out var choice)
                ? new JsonObject { ["type"] = choice }
                : options.ToolChoice.DeepClone();
        }

        if (compatRaw.AllowedFallbackModels is { Count: > 0 } fallbacks)
        {
            p["fallbacks"] = new JsonArray(fallbacks.Select(f => (JsonNode)new JsonObject { ["model"] = f.Model }).ToArray());
        }

        return p;
    }

    private static JsonNode ConvertContentBlocks(IReadOnlyList<ContentBlock> content)
    {
        var hasImages = content.Any(c => c is ImageContent);
        if (!hasImages)
        {
            return TextUtils.SanitizeSurrogates(string.Join("\n", content.OfType<TextContent>().Select(c => c.Text)));
        }
        var blocks = new JsonArray();
        foreach (var block in content)
        {
            if (block is TextContent text) blocks.Add(new JsonObject { ["type"] = "text", ["text"] = TextUtils.SanitizeSurrogates(text.Text) });
            else if (block is ImageContent image) blocks.Add(ImageBlock(image));
        }
        if (!blocks.Any(b => Str(b as JsonObject, "type") == "text"))
        {
            blocks.Insert(0, new JsonObject { ["type"] = "text", ["text"] = "(see attached image)" });
        }
        return blocks;
    }

    private static JsonObject ImageBlock(ImageContent image) => new()
    {
        ["type"] = "image",
        ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = image.MimeType, ["data"] = image.Data },
    };

    private static (JsonArray Messages, Dictionary<int, string> AssistantLevels) ConvertMessages(
        IReadOnlyList<Message> messages, bool isOAuth, JsonObject? cacheControl, bool allowEmptySignature,
        IReadOnlySet<string> deferredToolNames, Func<string, string> normalizeToolName, string? managedProvider)
    {
        var result = new JsonArray();
        var assistantLevels = new Dictionary<int, string>();
        var loadedToolNames = new HashSet<string>();

        for (var i = 0; i < messages.Count; i++)
        {
            var msg = messages[i];
            switch (msg)
            {
                case UserMessage user:
                    if (user.Content.Text is not null)
                    {
                        if (user.Content.Text.Trim().Length > 0)
                            result.Add(new JsonObject { ["role"] = "user", ["content"] = TextUtils.SanitizeSurrogates(user.Content.Text) });
                    }
                    else
                    {
                        var blocks = new JsonArray();
                        foreach (var item in user.Content.Blocks!)
                        {
                            if (item is TextContent t)
                            {
                                if (t.Text.Trim().Length > 0) blocks.Add(new JsonObject { ["type"] = "text", ["text"] = TextUtils.SanitizeSurrogates(t.Text) });
                            }
                            else if (item is ImageContent img)
                            {
                                blocks.Add(ImageBlock(img));
                            }
                        }
                        if (blocks.Count == 0) continue;
                        result.Add(new JsonObject { ["role"] = "user", ["content"] = blocks });
                    }
                    break;
                case AssistantMessage assistant:
                {
                    var blocks = new JsonArray();
                    foreach (var block in assistant.Content)
                    {
                        switch (block)
                        {
                            case TextContent text:
                                if (text.Text.Trim().Length == 0) continue;
                                blocks.Add(new JsonObject { ["type"] = "text", ["text"] = TextUtils.SanitizeSurrogates(text.Text) });
                                break;
                            case ThinkingContent thinking:
                            {
                                if (thinking.Redacted == true)
                                {
                                    blocks.Add(new JsonObject { ["type"] = "redacted_thinking", ["data"] = thinking.ThinkingSignature });
                                    continue;
                                }
                                var signature = thinking.ThinkingSignature;
                                var hasSignature = !string.IsNullOrWhiteSpace(signature);
                                if (thinking.Thinking.Trim().Length == 0 && !hasSignature) continue;
                                if (!hasSignature)
                                {
                                    blocks.Add(allowEmptySignature
                                        ? new JsonObject { ["type"] = "thinking", ["thinking"] = TextUtils.SanitizeSurrogates(thinking.Thinking), ["signature"] = "" }
                                        : new JsonObject { ["type"] = "text", ["text"] = TextUtils.SanitizeSurrogates(thinking.Thinking) });
                                }
                                else
                                {
                                    blocks.Add(new JsonObject { ["type"] = "thinking", ["thinking"] = TextUtils.SanitizeSurrogates(thinking.Thinking), ["signature"] = signature });
                                }
                                break;
                            }
                            case ToolCall toolCall:
                                blocks.Add(new JsonObject
                                {
                                    ["type"] = "tool_use",
                                    ["id"] = toolCall.Id,
                                    ["name"] = isOAuth ? ToClaudeCodeName(toolCall.Name) : toolCall.Name,
                                    ["input"] = toolCall.Arguments.DeepClone(),
                                });
                                break;
                        }
                    }
                    if (blocks.Count == 0) continue;
                    var messageIndex = result.Count;
                    result.Add(new JsonObject { ["role"] = "assistant", ["content"] = blocks });
                    if (managedProvider is not null && assistant.Api == KnownApis.AnthropicMessages && assistant.Provider == managedProvider
                        && assistant.ProviderThinkingLevel is "low" or "medium" or "high" or "xhigh" or "max")
                    {
                        assistantLevels[messageIndex] = assistant.ProviderThinkingLevel;
                    }
                    break;
                }
                case ToolResultMessage:
                {
                    var toolResults = new List<JsonNode>();
                    var sibling = new List<JsonNode>();
                    var j = i;
                    while (j < messages.Count && messages[j] is ToolResultMessage tr)
                    {
                        var references = new JsonArray();
                        foreach (var name in tr.AddedToolNames ?? [])
                        {
                            var normalized = normalizeToolName(name);
                            if (!deferredToolNames.Contains(normalized) || loadedToolNames.Contains(normalized)) continue;
                            loadedToolNames.Add(normalized);
                            references.Add(new JsonObject { ["type"] = "tool_reference", ["tool_name"] = isOAuth ? ToClaudeCodeName(name) : name });
                        }
                        var converted = ConvertContentBlocks(tr.Content);
                        toolResults.Add(new JsonObject
                        {
                            ["type"] = "tool_result",
                            ["tool_use_id"] = tr.ToolCallId,
                            ["content"] = references.Count > 0 ? references : converted,
                            ["is_error"] = tr.IsError,
                        });
                        if (references.Count > 0)
                        {
                            if (converted is JsonValue cv && cv.TryGetValue<string>(out var s)) sibling.Add(new JsonObject { ["type"] = "text", ["text"] = s });
                            else if (converted is JsonArray arr) sibling.AddRange(arr.Select(x => x!.DeepClone()));
                        }
                        j++;
                    }
                    i = j - 1;
                    var content = new JsonArray();
                    foreach (var r in toolResults) content.Add(r);
                    foreach (var s in sibling) content.Add(s);
                    result.Add(new JsonObject { ["role"] = "user", ["content"] = content });
                    break;
                }
            }
        }

        if (cacheControl is not null && result.Count > 0 && result[^1] is JsonObject last && Str(last, "role") == "user")
        {
            if (last["content"] is JsonArray arr && arr.Count > 0 && arr[^1] is JsonObject lastBlock && Str(lastBlock, "type") is "text" or "image" or "tool_result")
            {
                lastBlock["cache_control"] = cacheControl.DeepClone();
            }
            else if (last["content"] is JsonValue sv && sv.TryGetValue<string>(out var text))
            {
                last["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text, ["cache_control"] = cacheControl.DeepClone() });
            }
        }

        return (result, assistantLevels);
    }

    private static JsonArray InsertThinkingLevelMessages(JsonArray messages, Dictionary<int, string> assistantLevels, string activeEffort)
    {
        var result = new JsonArray();
        for (var index = 0; index < messages.Count; index++)
        {
            if (assistantLevels.TryGetValue(index, out var historical))
            {
                result.Add(new JsonObject { ["role"] = "system", ["content"] = new JsonArray(), ["output_config"] = new JsonObject { ["effort"] = historical } });
            }
            result.Add(messages[index]!.DeepClone());
        }
        result.Add(new JsonObject { ["role"] = "system", ["content"] = new JsonArray(), ["output_config"] = new JsonObject { ["effort"] = activeEffort } });
        return result;
    }

    private static IEnumerable<JsonObject> ConvertTools(IReadOnlyList<Tool> tools, bool isOAuth, bool eager, bool supportsStrictTools, JsonObject? cacheControl, bool deferLoading)
    {
        for (var index = 0; index < tools.Count; index++)
        {
            var tool = tools[index];
            var strict = ConstrainedSampling.ResolveJsonSchemaStrictSampling(tool, supportsStrictTools);
            var parameters = ConstrainedSampling.GetJsonSchemaToolParameters(tool, strict);
            JsonObject inputSchema;
            if (strict == true)
            {
                inputSchema = (JsonObject)parameters.DeepClone();
                inputSchema["type"] = "object";
                inputSchema["properties"] = parameters["properties"]?.DeepClone() ?? new JsonObject();
                inputSchema["required"] = parameters["required"]?.DeepClone() ?? new JsonArray();
            }
            else
            {
                inputSchema = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = parameters["properties"]?.DeepClone() ?? new JsonObject(),
                    ["required"] = parameters["required"]?.DeepClone() ?? new JsonArray(),
                };
            }

            var result = new JsonObject
            {
                ["name"] = isOAuth ? ToClaudeCodeName(tool.Name) : tool.Name,
                ["description"] = tool.Description,
            };
            if (eager) result["eager_input_streaming"] = true;
            if (strict == true) result["strict"] = true;
            result["input_schema"] = inputSchema;
            if (deferLoading) result["defer_loading"] = true;
            if (cacheControl is not null && index == tools.Count - 1) result["cache_control"] = cacheControl.DeepClone();
            yield return result;
        }
    }

    private static (StopReason, string?) MapStopReason(string reason, JsonObject? stopDetails) => reason switch
    {
        "end_turn" => (StopReason.Stop, null),
        "max_tokens" => (StopReason.Length, null),
        "tool_use" => (StopReason.ToolUse, null),
        "refusal" => (StopReason.Error, Str(stopDetails, "explanation") is { Length: > 0 } explanation ? explanation : "The model refused to complete the request"),
        "pause_turn" => (StopReason.Stop, null),
        "stop_sequence" => (StopReason.Stop, null),
        "sensitive" => (StopReason.Error, "Provider stopped with: sensitive"),
        _ => throw new InvalidOperationException($"Unhandled stop reason: {reason}"),
    };
}
