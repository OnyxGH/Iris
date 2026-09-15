using Iris.Ai;

namespace Iris.Agent;

/// <summary>Public agent state.</summary>
public sealed class AgentState
{
    private List<AgentTool> _tools = [];
    private List<Message> _messages = [];

    public string SystemPrompt { get; set; } = "";

    public Model Model { get; set; } = Agent.DefaultModel;

    public ThinkingLevel ThinkingLevel { get; set; } = ThinkingLevel.Off;

    /// <summary>Available tools. Assigning copies the list.</summary>
    public List<AgentTool> Tools
    {
        get => _tools;
        set => _tools = [.. value];
    }

    /// <summary>Conversation transcript. Assigning copies the list.</summary>
    public List<Message> Messages
    {
        get => _messages;
        set => _messages = [.. value];
    }

    /// <summary>True while the agent is processing a prompt or continuation.</summary>
    public bool IsStreaming { get; internal set; }

    /// <summary>Partial assistant message for the current streamed response, if any.</summary>
    public Message? StreamingMessage { get; internal set; }

    /// <summary>Tool call ids currently executing.</summary>
    public IReadOnlySet<string> PendingToolCalls { get; internal set; } = new HashSet<string>();

    /// <summary>Error message from the most recent failed or aborted assistant turn, if any.</summary>
    public string? ErrorMessage { get; internal set; }
}

public sealed class AgentOptions
{
    public string? SystemPrompt { get; init; }
    public Model? Model { get; init; }
    public ThinkingLevel? ThinkingLevel { get; init; }
    public List<AgentTool>? Tools { get; init; }
    public List<Message>? Messages { get; init; }
    public Func<IReadOnlyList<Message>, Task<List<Message>>>? ConvertToLlm { get; init; }
    public Func<List<Message>, CancellationToken, Task<List<Message>>>? TransformContext { get; init; }
    public StreamFn? StreamFn { get; init; }
    public Func<string, Task<string?>>? GetApiKey { get; init; }
    public Func<System.Text.Json.Nodes.JsonNode, Model, Task<System.Text.Json.Nodes.JsonNode?>>? OnPayload { get; init; }
    public Func<ProviderResponse, Model, Task>? OnResponse { get; init; }
    public Func<BeforeToolCallContext, CancellationToken, Task<BeforeToolCallResult?>>? BeforeToolCall { get; init; }
    public Func<AfterToolCallContext, CancellationToken, Task<AfterToolCallResult?>>? AfterToolCall { get; init; }
    public Func<ShouldStopAfterTurnContext, CancellationToken, Task<bool>>? ShouldStopAfterTurn { get; init; }
    public Func<CancellationToken, Task<AgentLoopTurnUpdate?>>? PrepareNextTurn { get; init; }
    public Func<PrepareNextTurnContext, CancellationToken, Task<AgentLoopTurnUpdate?>>? PrepareNextTurnWithContext { get; init; }
    public QueueMode? SteeringMode { get; init; }
    public QueueMode? FollowUpMode { get; init; }
    public string? SessionId { get; init; }
    public ThinkingBudgets? ThinkingBudgets { get; init; }
    public Transport? Transport { get; init; }
    public int? MaxRetryDelayMs { get; init; }
    public ToolExecutionMode? ToolExecution { get; init; }
}

internal sealed class PendingMessageQueue(QueueMode mode)
{
    private readonly List<Message> _messages = [];

    public QueueMode Mode { get; set; } = mode;

    public void Enqueue(Message message)
    {
        lock (_messages) _messages.Add(message);
    }

    public bool HasItems()
    {
        lock (_messages) return _messages.Count > 0;
    }

    public List<Message> Drain()
    {
        lock (_messages)
        {
            if (Mode == QueueMode.All)
            {
                var drained = _messages.ToList();
                _messages.Clear();
                return drained;
            }
            if (_messages.Count == 0) return [];
            var first = _messages[0];
            _messages.RemoveAt(0);
            return [first];
        }
    }

    public void Clear()
    {
        lock (_messages) _messages.Clear();
    }
}

/// <summary>
/// Stateful wrapper around the agent loop. Owns the transcript, emits lifecycle events, executes tools, and exposes
/// steering and follow-up queues. Port of agent.ts.
/// </summary>
public sealed class Agent
{
    /// <summary>Placeholder model used while no model is selected.</summary>
    public static readonly Model DefaultModel = new()
    {
        Id = "unknown", Name = "unknown", Api = "unknown", Provider = "unknown", BaseUrl = "", Reasoning = false, Input = [],
    };

    private sealed class ActiveRun
    {
        public required TaskCompletionSource Completion { get; init; }
        public required CancellationTokenSource Cancellation { get; init; }
    }

    private readonly List<Func<AgentEvent, CancellationToken, Task>> _listeners = [];
    private readonly PendingMessageQueue _steeringQueue;
    private readonly PendingMessageQueue _followUpQueue;
    private ActiveRun? _activeRun;

    public Agent(AgentOptions? options = null)
    {
        options ??= new AgentOptions();
        State = new AgentState
        {
            SystemPrompt = options.SystemPrompt ?? "",
            Model = options.Model ?? DefaultModel,
            ThinkingLevel = options.ThinkingLevel ?? ThinkingLevel.Off,
            Tools = options.Tools ?? [],
            Messages = options.Messages ?? [],
        };
        ConvertToLlm = options.ConvertToLlm ?? DefaultConvertToLlm;
        TransformContext = options.TransformContext;
        StreamFunction = options.StreamFn ?? DefaultStreamFn.Get();
        GetApiKey = options.GetApiKey;
        OnPayload = options.OnPayload;
        OnResponse = options.OnResponse;
        BeforeToolCall = options.BeforeToolCall;
        AfterToolCall = options.AfterToolCall;
        ShouldStopAfterTurn = options.ShouldStopAfterTurn;
        PrepareNextTurn = options.PrepareNextTurn;
        PrepareNextTurnWithContext = options.PrepareNextTurnWithContext;
        _steeringQueue = new PendingMessageQueue(options.SteeringMode ?? QueueMode.OneAtATime);
        _followUpQueue = new PendingMessageQueue(options.FollowUpMode ?? QueueMode.OneAtATime);
        SessionId = options.SessionId;
        ThinkingBudgets = options.ThinkingBudgets;
        Transport = options.Transport ?? Ai.Transport.Auto;
        MaxRetryDelayMs = options.MaxRetryDelayMs;
        ToolExecution = options.ToolExecution ?? ToolExecutionMode.Parallel;
    }

    private static Task<List<Message>> DefaultConvertToLlm(IReadOnlyList<Message> messages) =>
        Task.FromResult(messages.Where(m => m is UserMessage or AssistantMessage or ToolResultMessage).ToList());

    public AgentState State { get; }

    public Func<IReadOnlyList<Message>, Task<List<Message>>> ConvertToLlm { get; set; }
    public Func<List<Message>, CancellationToken, Task<List<Message>>>? TransformContext { get; set; }
    public StreamFn StreamFunction { get; set; }
    public Func<string, Task<string?>>? GetApiKey { get; set; }
    public Func<System.Text.Json.Nodes.JsonNode, Model, Task<System.Text.Json.Nodes.JsonNode?>>? OnPayload { get; set; }
    public Func<ProviderResponse, Model, Task>? OnResponse { get; set; }
    public Func<BeforeToolCallContext, CancellationToken, Task<BeforeToolCallResult?>>? BeforeToolCall { get; set; }
    public Func<AfterToolCallContext, CancellationToken, Task<AfterToolCallResult?>>? AfterToolCall { get; set; }
    public Func<ShouldStopAfterTurnContext, CancellationToken, Task<bool>>? ShouldStopAfterTurn { get; set; }
    public Func<CancellationToken, Task<AgentLoopTurnUpdate?>>? PrepareNextTurn { get; set; }
    public Func<PrepareNextTurnContext, CancellationToken, Task<AgentLoopTurnUpdate?>>? PrepareNextTurnWithContext { get; set; }
    public string? SessionId { get; set; }
    public ThinkingBudgets? ThinkingBudgets { get; set; }
    public Transport Transport { get; set; }
    public int? MaxRetryDelayMs { get; set; }
    public ToolExecutionMode ToolExecution { get; set; }

    public QueueMode SteeringMode
    {
        get => _steeringQueue.Mode;
        set => _steeringQueue.Mode = value;
    }

    public QueueMode FollowUpMode
    {
        get => _followUpQueue.Mode;
        set => _followUpQueue.Mode = value;
    }

    /// <summary>Subscribe to lifecycle events. Listeners are awaited in subscription order. Dispose to unsubscribe.</summary>
    public IDisposable Subscribe(Func<AgentEvent, CancellationToken, Task> listener)
    {
        lock (_listeners) _listeners.Add(listener);
        return new Subscription(() =>
        {
            lock (_listeners) _listeners.Remove(listener);
        });
    }

    public IDisposable Subscribe(Action<AgentEvent> listener) => Subscribe((e, _) =>
    {
        listener(e);
        return Task.CompletedTask;
    });

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }

    public void Steer(Message message) => _steeringQueue.Enqueue(message);

    public void FollowUp(Message message) => _followUpQueue.Enqueue(message);

    public void ClearSteeringQueue() => _steeringQueue.Clear();

    public void ClearFollowUpQueue() => _followUpQueue.Clear();

    public void ClearAllQueues()
    {
        ClearSteeringQueue();
        ClearFollowUpQueue();
    }

    public bool HasQueuedMessages() => _steeringQueue.HasItems() || _followUpQueue.HasItems();

    /// <summary>Cancellation token for the current run, if any.</summary>
    public CancellationToken? Signal => _activeRun?.Cancellation.Token;

    public bool IsRunning => _activeRun is not null;

    public void Abort()
    {
        try
        {
            _activeRun?.Cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>Completes when the current run and all awaited listeners have finished.</summary>
    public Task WaitForIdleAsync() => _activeRun?.Completion.Task ?? Task.CompletedTask;

    public void Reset()
    {
        if (_activeRun is not null) throw new InvalidOperationException("Agent is already processing. Wait for completion before resetting.");
        State.Messages = [];
        State.IsStreaming = false;
        State.StreamingMessage = null;
        State.PendingToolCalls = new HashSet<string>();
        State.ErrorMessage = null;
        ClearFollowUpQueue();
        ClearSteeringQueue();
    }

    public Task PromptAsync(string input, IReadOnlyList<ImageContent>? images = null)
    {
        var content = new List<ContentBlock> { new TextContent(input) };
        if (images is { Count: > 0 }) content.AddRange(images);
        return PromptAsync([new UserMessage(UserContent.FromBlocks(content))]);
    }

    public Task PromptAsync(Message message) => PromptAsync([message]);

    public async Task PromptAsync(IReadOnlyList<Message> messages)
    {
        if (_activeRun is not null)
            throw new InvalidOperationException("Agent is already processing a prompt. Use steer() or followUp() to queue messages, or wait for completion.");
        await RunPromptMessagesAsync(messages);
    }

    /// <summary>Continue from the current transcript. The last message must be a user or tool-result message.</summary>
    public async Task ContinueAsync()
    {
        if (_activeRun is not null) throw new InvalidOperationException("Agent is already processing. Wait for completion before continuing.");
        var last = State.Messages.LastOrDefault() ?? throw new InvalidOperationException("No messages to continue from");
        if (last is AssistantMessage)
        {
            var queuedSteering = _steeringQueue.Drain();
            if (queuedSteering.Count > 0)
            {
                await RunPromptMessagesAsync(queuedSteering, skipInitialSteeringPoll: true);
                return;
            }
            var queuedFollowUps = _followUpQueue.Drain();
            if (queuedFollowUps.Count > 0)
            {
                await RunPromptMessagesAsync(queuedFollowUps);
                return;
            }
            throw new InvalidOperationException("Cannot continue from message role: assistant");
        }
        await RunWithLifecycleAsync(ct => AgentLoop.RunContinueAsync(CreateContextSnapshot(), CreateLoopConfig(), ProcessEventAsync, ct, StreamFunction));
    }

    private Task RunPromptMessagesAsync(IReadOnlyList<Message> messages, bool skipInitialSteeringPoll = false) =>
        RunWithLifecycleAsync(ct => AgentLoop.RunAsync(messages, CreateContextSnapshot(), CreateLoopConfig(skipInitialSteeringPoll), ProcessEventAsync, ct, StreamFunction));

    private AgentContext CreateContextSnapshot() => new()
    {
        SystemPrompt = State.SystemPrompt,
        Messages = [.. State.Messages],
        Tools = [.. State.Tools],
    };

    private AgentLoopConfig CreateLoopConfig(bool skipInitialSteeringPoll = false)
    {
        var skip = skipInitialSteeringPoll;
        var shouldStop = ShouldStopAfterTurn;
        return new AgentLoopConfig
        {
            Model = State.Model,
            Reasoning = State.ThinkingLevel == ThinkingLevel.Off ? null : State.ThinkingLevel,
            SessionId = SessionId,
            OnPayload = OnPayload,
            OnResponse = OnResponse,
            Transport = Transport,
            ThinkingBudgets = ThinkingBudgets,
            MaxRetryDelayMs = MaxRetryDelayMs,
            ToolExecution = ToolExecution,
            BeforeToolCall = BeforeToolCall,
            AfterToolCall = AfterToolCall,
            ShouldStopAfterTurn = shouldStop is null ? null : ctx => shouldStop(ctx, Signal ?? CancellationToken.None),
            PrepareNextTurn = PrepareNextTurnWithContext is not null || PrepareNextTurn is not null
                ? async ctx =>
                {
                    if (PrepareNextTurnWithContext is not null) return await PrepareNextTurnWithContext(ctx, Signal ?? CancellationToken.None);
                    return PrepareNextTurn is null ? null : await PrepareNextTurn(Signal ?? CancellationToken.None);
                }
                : null,
            ConvertToLlm = ConvertToLlm,
            TransformContext = TransformContext,
            GetApiKey = GetApiKey,
            GetSteeringMessages = () =>
            {
                if (skip)
                {
                    skip = false;
                    return Task.FromResult(new List<Message>());
                }
                return Task.FromResult(_steeringQueue.Drain());
            },
            GetFollowUpMessages = () => Task.FromResult(_followUpQueue.Drain()),
        };
    }

    private async Task RunWithLifecycleAsync(Func<CancellationToken, Task> executor)
    {
        if (_activeRun is not null) throw new InvalidOperationException("Agent is already processing.");
        var run = new ActiveRun
        {
            Completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            Cancellation = new CancellationTokenSource(),
        };
        _activeRun = run;
        State.IsStreaming = true;
        State.StreamingMessage = null;
        State.ErrorMessage = null;

        try
        {
            await executor(run.Cancellation.Token);
        }
        catch (Exception ex)
        {
            await HandleRunFailureAsync(ex, run.Cancellation.IsCancellationRequested);
        }
        finally
        {
            FinishRun(run);
        }
    }

    private async Task HandleRunFailureAsync(Exception error, bool aborted)
    {
        var failure = new AssistantMessage
        {
            Content = [new TextContent("")],
            Api = State.Model.Api,
            Provider = State.Model.Provider,
            Model = State.Model.Id,
            Usage = new Usage(),
            StopReason = aborted ? StopReason.Aborted : StopReason.Error,
            ErrorMessage = error.Message,
            Timestamp = TimeUtil.NowMs(),
        };
        await ProcessEventAsync(new MessageStartEvent(failure));
        await ProcessEventAsync(new MessageEndEvent(failure));
        await ProcessEventAsync(new TurnEndEvent(failure, []));
        await ProcessEventAsync(new AgentEndEvent([failure]));
    }

    private void FinishRun(ActiveRun run)
    {
        State.IsStreaming = false;
        State.StreamingMessage = null;
        State.PendingToolCalls = new HashSet<string>();
        _activeRun = null;
        run.Completion.TrySetResult();
        run.Cancellation.Dispose();
    }

    private readonly object _stateGate = new();

    private async Task ProcessEventAsync(AgentEvent agentEvent)
    {
        lock (_stateGate)
        {
            switch (agentEvent)
            {
                case MessageStartEvent start:
                    State.StreamingMessage = start.Message;
                    break;
                case MessageUpdateEvent update:
                    State.StreamingMessage = update.Message;
                    break;
                case MessageEndEvent end:
                    State.StreamingMessage = null;
                    State.Messages.Add(end.Message);
                    break;
                case ToolExecutionStartEvent toolStart:
                    State.PendingToolCalls = new HashSet<string>(State.PendingToolCalls) { toolStart.ToolCallId };
                    break;
                case ToolExecutionEndEvent toolEnd:
                {
                    var pending = new HashSet<string>(State.PendingToolCalls);
                    pending.Remove(toolEnd.ToolCallId);
                    State.PendingToolCalls = pending;
                    break;
                }
                case TurnEndEvent turnEnd:
                    if (turnEnd.Message is AssistantMessage { ErrorMessage: { } errorMessage }) State.ErrorMessage = errorMessage;
                    break;
                case AgentEndEvent:
                    State.StreamingMessage = null;
                    break;
            }
        }

        var run = _activeRun ?? throw new InvalidOperationException("Agent listener invoked outside active run");
        List<Func<AgentEvent, CancellationToken, Task>> listeners;
        lock (_listeners) listeners = [.. _listeners];
        foreach (var listener in listeners)
        {
            await listener(agentEvent, run.Cancellation.Token);
        }
    }
}
