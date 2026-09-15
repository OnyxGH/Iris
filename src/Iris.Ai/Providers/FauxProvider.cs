using System.Text.Json.Nodes;
using Iris.Ai.Auth;
using Iris.Ai.Json;
using Iris.Ai.Models;
using Iris.Ai.Utils;

namespace Iris.Ai.Providers;

public sealed class FauxModelDefinition
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    public bool? Reasoning { get; init; }
    public List<string>? Input { get; init; }
    public ModelCost? Cost { get; init; }
    public long? ContextWindow { get; init; }
    public long? MaxTokens { get; init; }
}

public sealed class FauxProviderState
{
    public int CallCount { get; set; }
}

/// <summary>A scripted response: either a fixed message or a factory.</summary>
public sealed class FauxResponseStep
{
    private FauxResponseStep(AssistantMessage? message, Func<Context, SimpleStreamOptions?, FauxProviderState, Model, Task<AssistantMessage>>? factory)
    {
        Message = message;
        Factory = factory;
    }

    public AssistantMessage? Message { get; }

    public Func<Context, SimpleStreamOptions?, FauxProviderState, Model, Task<AssistantMessage>>? Factory { get; }

    public static implicit operator FauxResponseStep(AssistantMessage message) => new(message, null);

    public static FauxResponseStep From(Func<Context, SimpleStreamOptions?, FauxProviderState, Model, Task<AssistantMessage>> factory) => new(null, factory);

    public static FauxResponseStep From(Func<Context, SimpleStreamOptions?, FauxProviderState, Model, AssistantMessage> factory) =>
        new(null, (c, o, s, m) => Task.FromResult(factory(c, o, s, m)));
}

public sealed class FauxProviderOptions
{
    public string? Api { get; init; }
    public string? Provider { get; init; }
    public List<FauxModelDefinition>? Models { get; init; }
    public double? TokensPerSecond { get; init; }
    public int? MinTokenSize { get; init; }
    public int? MaxTokenSize { get; init; }
}

/// <summary>Scripted in-memory provider for tests. Port of providers/faux.ts (without deferred responses).</summary>
public sealed class FauxProvider : IApiStreams
{
    private const string DefaultApi = "faux";
    private const string DefaultProvider = "faux";
    private const string DefaultModelId = "faux-1";
    private const string DefaultModelName = "Faux Model";
    private const string DefaultBaseUrl = "http://localhost:0";

    private readonly Queue<FauxResponseStep> _pending = new();
    private readonly Dictionary<string, string> _promptCache = new();
    private readonly int _minTokenSize;
    private readonly int _maxTokenSize;
    private readonly double? _tokensPerSecond;
    private readonly object _gate = new();

    public FauxProvider(FauxProviderOptions? options = null)
    {
        options ??= new FauxProviderOptions();
        ApiId = options.Api ?? RandomId(DefaultApi);
        ProviderId = options.Provider ?? DefaultProvider;
        _minTokenSize = Math.Max(1, Math.Min(options.MinTokenSize ?? 3, options.MaxTokenSize ?? 5));
        _maxTokenSize = Math.Max(_minTokenSize, options.MaxTokenSize ?? 5);
        _tokensPerSecond = options.TokensPerSecond;

        var definitions = options.Models is { Count: > 0 }
            ? options.Models
            : [new FauxModelDefinition { Id = DefaultModelId, Name = DefaultModelName, Reasoning = false, Input = ["text", "image"], ContextWindow = 128000, MaxTokens = 16384 }];
        Models = definitions.Select(d => new Model
        {
            Id = d.Id,
            Name = d.Name ?? d.Id,
            Api = ApiId,
            Provider = ProviderId,
            BaseUrl = DefaultBaseUrl,
            Reasoning = d.Reasoning ?? false,
            Input = d.Input ?? ["text", "image"],
            Cost = d.Cost ?? new ModelCost(),
            ContextWindow = d.ContextWindow ?? 128000,
            MaxTokens = d.MaxTokens ?? 16384,
        }).ToList();

        Provider = ProviderFactory.Create(new CreateProviderOptions
        {
            Id = ProviderId,
            Auth = new ProviderAuth { ApiKey = new ApiKeyAuth { Name = "Faux", Resolve = _ => Task.FromResult<AuthResult?>(new AuthResult()) } },
            Models = Models,
            Api = this,
        });
    }

    public string ApiId { get; }

    public string ProviderId { get; }

    public IReadOnlyList<Model> Models { get; }

    public IProvider Provider { get; }

    public FauxProviderState State { get; } = new();

    public Model GetModel() => Models[0];

    public Model? GetModel(string modelId) => Models.FirstOrDefault(m => m.Id == modelId);

    public void SetResponses(IEnumerable<FauxResponseStep> responses)
    {
        lock (_gate)
        {
            _pending.Clear();
            foreach (var r in responses) _pending.Enqueue(r);
        }
    }

    public void AppendResponses(IEnumerable<FauxResponseStep> responses)
    {
        lock (_gate)
        {
            foreach (var r in responses) _pending.Enqueue(r);
        }
    }

    public int PendingResponseCount
    {
        get
        {
            lock (_gate) return _pending.Count;
        }
    }

    /// <summary>Register this provider's API in the global registry. Dispose to unregister.</summary>
    public IDisposable RegisterGlobally()
    {
        var sourceId = RandomId("faux-provider");
        ApiRegistry.Register(ApiId, this, sourceId);
        return new Unregister(() => ApiRegistry.UnregisterSource(sourceId));
    }

    private sealed class Unregister(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    // ---- message builders ----

    public static TextContent Text(string text) => new(text);

    public static ThinkingContent Thinking(string thinking) => new(thinking);

    public static ToolCall ToolCall(string name, JsonObject arguments, string? id = null) =>
        new() { Id = id ?? RandomId("tool"), Name = name, Arguments = arguments };

    public static AssistantMessage AssistantMessage(string text, StopReason stopReason = StopReason.Stop, string? errorMessage = null, string? responseId = null, long? timestamp = null) =>
        AssistantMessage([Text(text)], stopReason, errorMessage, responseId, timestamp);

    public static AssistantMessage AssistantMessage(IEnumerable<ContentBlock> content, StopReason stopReason = StopReason.Stop, string? errorMessage = null, string? responseId = null, long? timestamp = null) => new()
    {
        Content = content.ToList(),
        Api = DefaultApi,
        Provider = DefaultProvider,
        Model = DefaultModelId,
        Usage = new Usage(),
        StopReason = stopReason,
        ErrorMessage = errorMessage,
        ResponseId = responseId,
        Timestamp = timestamp ?? TimeUtil.NowMs(),
    };

    // ---- streaming ----

    public AssistantMessageEventStream Stream(Model model, Context context, StreamOptions? options = null) =>
        StreamSimple(model, context, options as SimpleStreamOptions ?? (options is null ? null : options.CopyTo(new SimpleStreamOptions())));

    public AssistantMessageEventStream StreamSimple(Model model, Context context, SimpleStreamOptions? options = null)
    {
        var outer = new AssistantMessageEventStream();
        FauxResponseStep? step;
        lock (_gate)
        {
            _pending.TryDequeue(out step);
            State.CallCount++;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (options?.OnResponse is not null) await options.OnResponse(new ProviderResponse(200, new Dictionary<string, string>()), model);
                if (step is null)
                {
                    var message = WithUsageEstimate(CreateErrorMessage("No more faux responses queued", model.Id), context, options);
                    outer.Push(new ErrorEvent(StopReason.Error, message));
                    outer.End(message);
                    return;
                }
                var resolved = step.Factory is not null ? await step.Factory(context, options, State, model) : step.Message!;
                var cloned = resolved.Clone();
                cloned.Api = ApiId;
                cloned.Provider = ProviderId;
                cloned.Model = model.Id;
                var final = WithUsageEstimate(cloned, context, options);
                await StreamWithDeltasAsync(outer, final, options?.CancellationToken ?? default);
            }
            catch (Exception ex)
            {
                var message = CreateErrorMessage(ex.Message, model.Id);
                outer.Push(new ErrorEvent(StopReason.Error, message));
                outer.End(message);
            }
        });
        return outer;
    }

    private AssistantMessage CreateErrorMessage(string error, string modelId) => new()
    {
        Api = ApiId,
        Provider = ProviderId,
        Model = modelId,
        StopReason = StopReason.Error,
        ErrorMessage = error,
        Timestamp = TimeUtil.NowMs(),
    };

    private static long EstimateTokens(string text) => (long)Math.Ceiling(text.Length / 4.0);

    private static string RandomId(string prefix) => $"{prefix}:{TimeUtil.NowMs()}:{TextUtils.ToBase36((ulong)Random.Shared.NextInt64(long.MaxValue))}";

    private static string ContentToText(IEnumerable<ContentBlock> content) =>
        string.Join("\n", content.Select(b => b switch
        {
            TextContent t => t.Text,
            ImageContent i => $"[image:{i.MimeType}:{i.Data.Length}]",
            _ => "",
        }));

    private static string AssistantContentToText(IEnumerable<ContentBlock> content) =>
        string.Join("\n", content.Select(b => b switch
        {
            TextContent t => t.Text,
            ThinkingContent th => th.Thinking,
            ToolCall tc => $"{tc.Name}:{PiJson.Stringify(tc.Arguments)}",
            _ => "",
        }));

    private static string MessageToText(Message message) => message switch
    {
        UserMessage u => u.Content.Text ?? ContentToText(u.Content.Blocks!),
        AssistantMessage a => AssistantContentToText(a.Content),
        ToolResultMessage tr => string.Join("\n", new[] { tr.ToolName }.Concat(tr.Content.Select(b => ContentToText([b])))),
        _ => "",
    };

    private static string SerializeContext(Context context)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(context.SystemPrompt)) parts.Add($"system:{context.SystemPrompt}");
        foreach (var message in context.Messages) parts.Add($"{message.Role}:{MessageToText(message)}");
        if (context.Tools is { Count: > 0 }) parts.Add($"tools:{PiJson.Serialize(context.Tools)}");
        return string.Join("\n\n", parts);
    }

    private AssistantMessage WithUsageEstimate(AssistantMessage message, Context context, StreamOptions? options)
    {
        var promptText = SerializeContext(context);
        var promptTokens = EstimateTokens(promptText);
        var outputTokens = EstimateTokens(AssistantContentToText(message.Content));
        long input = promptTokens, cacheRead = 0, cacheWrite = 0;
        var sessionId = options?.SessionId;
        if (sessionId is not null && options?.CacheRetention != CacheRetention.None)
        {
            lock (_promptCache)
            {
                if (_promptCache.TryGetValue(sessionId, out var previous) && previous.Length > 0)
                {
                    var length = Math.Min(previous.Length, promptText.Length);
                    var cached = 0;
                    while (cached < length && previous[cached] == promptText[cached]) cached++;
                    cacheRead = EstimateTokens(previous[..cached]);
                    cacheWrite = EstimateTokens(promptText[cached..]);
                    input = Math.Max(0, promptTokens - cacheRead);
                }
                else
                {
                    cacheWrite = promptTokens;
                }
                _promptCache[sessionId] = promptText;
            }
        }
        message.Usage = new Usage
        {
            Input = input,
            Output = outputTokens,
            CacheRead = cacheRead,
            CacheWrite = cacheWrite,
            TotalTokens = input + outputTokens + cacheRead + cacheWrite,
        };
        return message;
    }

    private List<string> Split(string text)
    {
        var chunks = new List<string>();
        var index = 0;
        while (index < text.Length)
        {
            var tokenSize = _minTokenSize + Random.Shared.Next(_maxTokenSize - _minTokenSize + 1);
            var charSize = Math.Max(1, tokenSize * 4);
            chunks.Add(text.Slice(index, index + charSize));
            index += charSize;
        }
        return chunks.Count > 0 ? chunks : [""];
    }

    private async Task ScheduleChunk(string chunk)
    {
        if (_tokensPerSecond is not > 0)
        {
            await Task.Yield();
            return;
        }
        await Task.Delay(TimeSpan.FromMilliseconds(EstimateTokens(chunk) / _tokensPerSecond.Value * 1000));
    }

    private static AssistantMessage Snapshot(AssistantMessage partial) => partial.Clone();

    private async Task StreamWithDeltasAsync(AssistantMessageEventStream stream, AssistantMessage message, CancellationToken ct)
    {
        var partial = message.Clone();
        partial.Content = [];
        partial.StopReason = StopReason.Pending;

        bool AbortIfNeeded()
        {
            if (!ct.IsCancellationRequested) return false;
            var aborted = partial.Clone();
            aborted.StopReason = StopReason.Aborted;
            aborted.ErrorMessage = "Request was aborted";
            aborted.Timestamp = TimeUtil.NowMs();
            stream.Push(new ErrorEvent(StopReason.Aborted, aborted));
            stream.End(aborted);
            return true;
        }

        if (AbortIfNeeded()) return;
        stream.Push(new StartEvent(Snapshot(partial)));

        for (var index = 0; index < message.Content.Count; index++)
        {
            if (AbortIfNeeded()) return;
            switch (message.Content[index])
            {
                case ThinkingContent thinking:
                {
                    var block = new ThinkingContent("");
                    partial.Content.Add(block);
                    stream.Push(new ThinkingStartEvent(index, Snapshot(partial)));
                    foreach (var chunk in Split(thinking.Thinking))
                    {
                        await ScheduleChunk(chunk);
                        if (AbortIfNeeded()) return;
                        block.Thinking += chunk;
                        stream.Push(new ThinkingDeltaEvent(index, chunk, Snapshot(partial)));
                    }
                    stream.Push(new ThinkingEndEvent(index, thinking.Thinking, Snapshot(partial)));
                    break;
                }
                case TextContent text:
                {
                    var block = new TextContent("");
                    partial.Content.Add(block);
                    stream.Push(new TextStartEvent(index, Snapshot(partial)));
                    foreach (var chunk in Split(text.Text))
                    {
                        await ScheduleChunk(chunk);
                        if (AbortIfNeeded()) return;
                        block.Text += chunk;
                        stream.Push(new TextDeltaEvent(index, chunk, Snapshot(partial)));
                    }
                    stream.Push(new TextEndEvent(index, text.Text, Snapshot(partial)));
                    break;
                }
                case ToolCall toolCall:
                {
                    var block = new ToolCall { Id = toolCall.Id, Name = toolCall.Name, Arguments = new JsonObject() };
                    partial.Content.Add(block);
                    stream.Push(new ToolCallStartEvent(index, Snapshot(partial)));
                    foreach (var chunk in Split(PiJson.Stringify(toolCall.Arguments)))
                    {
                        await ScheduleChunk(chunk);
                        if (AbortIfNeeded()) return;
                        stream.Push(new ToolCallDeltaEvent(index, chunk, Snapshot(partial)));
                    }
                    block.Arguments = (JsonObject)toolCall.Arguments.DeepClone();
                    stream.Push(new ToolCallEndEvent(index, toolCall, Snapshot(partial)));
                    break;
                }
            }
        }

        if (message.StopReason == StopReason.Pending) throw new InvalidOperationException("Faux response ended without a stop reason");
        if (message.StopReason is StopReason.Error or StopReason.Aborted)
        {
            stream.Push(new ErrorEvent(message.StopReason, message));
            stream.End(message);
            return;
        }
        stream.Push(new DoneEvent(message.StopReason, message));
        stream.End(message);
    }
}
