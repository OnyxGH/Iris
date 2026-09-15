using System.Text.Json.Nodes;
using Iris.Ai;
using Iris.Ai.Utils;

namespace Iris.Agent;

public delegate Task AgentEventSink(AgentEvent agentEvent);

/// <summary>Agent loop working with messages throughout; converts to LLM messages only at the call boundary. Port of agent-loop.ts.</summary>
public static class AgentLoop
{
    /// <summary>Start an agent loop with new prompt messages, returning an event stream.</summary>
    public static EventStream<AgentEvent, IReadOnlyList<Message>> Start(IReadOnlyList<Message> prompts, AgentContext context, AgentLoopConfig config, CancellationToken cancellationToken, StreamFn? streamFn)
    {
        var stream = CreateAgentStream();
        _ = Task.Run(async () =>
        {
            var messages = await RunAsync(prompts, context, config, e =>
            {
                stream.Push(e);
                return Task.CompletedTask;
            }, cancellationToken, streamFn);
            stream.End(messages, hasResult: true);
        });
        return stream;
    }

    /// <summary>Continue from the current context without adding a new message.</summary>
    public static EventStream<AgentEvent, IReadOnlyList<Message>> Continue(AgentContext context, AgentLoopConfig config, CancellationToken cancellationToken, StreamFn? streamFn)
    {
        ValidateContinue(context);
        var stream = CreateAgentStream();
        _ = Task.Run(async () =>
        {
            var messages = await RunContinueAsync(context, config, e =>
            {
                stream.Push(e);
                return Task.CompletedTask;
            }, cancellationToken, streamFn);
            stream.End(messages, hasResult: true);
        });
        return stream;
    }

    private static void ValidateContinue(AgentContext context)
    {
        if (context.Messages.Count == 0) throw new InvalidOperationException("Cannot continue: no messages in context");
        if (context.Messages[^1] is AssistantMessage) throw new InvalidOperationException("Cannot continue from message role: assistant");
    }

    private static EventStream<AgentEvent, IReadOnlyList<Message>> CreateAgentStream() =>
        new(e => e is AgentEndEvent, e => e is AgentEndEvent end ? end.Messages : []);

    public static async Task<IReadOnlyList<Message>> RunAsync(IReadOnlyList<Message> prompts, AgentContext context, AgentLoopConfig config, AgentEventSink emit, CancellationToken cancellationToken, StreamFn? streamFn)
    {
        var newMessages = new List<Message>(prompts);
        var currentContext = new AgentContext
        {
            SystemPrompt = context.SystemPrompt,
            Messages = [.. context.Messages, .. prompts],
            Tools = context.Tools,
        };

        await emit(new AgentStartEvent());
        await emit(new TurnStartEvent());
        foreach (var prompt in prompts)
        {
            await emit(new MessageStartEvent(prompt));
            await emit(new MessageEndEvent(prompt));
        }

        await RunLoopAsync(currentContext, newMessages, config, cancellationToken, emit, streamFn ?? DefaultStreamFn.Get());
        return newMessages;
    }

    public static async Task<IReadOnlyList<Message>> RunContinueAsync(AgentContext context, AgentLoopConfig config, AgentEventSink emit, CancellationToken cancellationToken, StreamFn? streamFn)
    {
        ValidateContinue(context);
        var newMessages = new List<Message>();
        var currentContext = new AgentContext { SystemPrompt = context.SystemPrompt, Messages = context.Messages, Tools = context.Tools };

        await emit(new AgentStartEvent());
        await emit(new TurnStartEvent());

        await RunLoopAsync(currentContext, newMessages, config, cancellationToken, emit, streamFn ?? DefaultStreamFn.Get());
        return newMessages;
    }

    private static async Task<List<Message>> Poll(Func<Task<List<Message>>>? source) =>
        source is null ? [] : await source() ?? [];

    private static async Task RunLoopAsync(AgentContext initialContext, List<Message> newMessages, AgentLoopConfig initialConfig, CancellationToken ct, AgentEventSink emit, StreamFn streamFunction)
    {
        var currentContext = initialContext;
        var config = initialConfig;
        PrepareNextTurnContext? lastCompletedTurn = null;
        var pendingMessages = await Poll(config.GetSteeringMessages);

        while (true)
        {
            var hasMoreToolCalls = true;

            while (hasMoreToolCalls || pendingMessages.Count > 0)
            {
                if (lastCompletedTurn is not null)
                {
                    var update = config.PrepareNextTurn is null ? null : await config.PrepareNextTurn(lastCompletedTurn);
                    if (update is not null)
                    {
                        currentContext = update.Context ?? currentContext;
                        var nextConfig = config.CloneConfig();
                        nextConfig.Model = update.Model ?? config.Model;
                        nextConfig.Reasoning = update.ThinkingLevel is null
                            ? config.Reasoning
                            : update.ThinkingLevel == ThinkingLevel.Off ? null : update.ThinkingLevel;
                        config = nextConfig;
                    }
                    if (pendingMessages.Count == 0) pendingMessages = await Poll(config.GetSteeringMessages);
                    await emit(new TurnStartEvent());
                }

                if (pendingMessages.Count > 0)
                {
                    foreach (var message in pendingMessages)
                    {
                        await emit(new MessageStartEvent(message));
                        await emit(new MessageEndEvent(message));
                        currentContext.Messages.Add(message);
                        newMessages.Add(message);
                    }
                    pendingMessages = [];
                }

                var assistant = await StreamAssistantResponseAsync(currentContext, config, ct, emit, streamFunction);
                newMessages.Add(assistant);

                if (assistant.StopReason is StopReason.Error or StopReason.Aborted)
                {
                    await emit(new TurnEndEvent(assistant, []));
                    await emit(new AgentEndEvent(newMessages));
                    return;
                }

                var toolCalls = assistant.Content.OfType<ToolCall>().ToList();
                var toolResults = new List<ToolResultMessage>();
                hasMoreToolCalls = false;
                if (toolCalls.Count > 0)
                {
                    var batch = assistant.StopReason == StopReason.Length
                        ? await FailToolCallsFromTruncatedMessageAsync(toolCalls, emit)
                        : await ExecuteToolCallsAsync(currentContext, assistant, config, ct, emit);
                    toolResults.AddRange(batch.Messages);
                    hasMoreToolCalls = !batch.Terminate;
                    foreach (var result in toolResults)
                    {
                        currentContext.Messages.Add(result);
                        newMessages.Add(result);
                    }
                }

                await emit(new TurnEndEvent(assistant, toolResults));

                lastCompletedTurn = new PrepareNextTurnContext(assistant, toolResults, currentContext, newMessages);
                if (config.ShouldStopAfterTurn is not null && await config.ShouldStopAfterTurn(lastCompletedTurn))
                {
                    await emit(new AgentEndEvent(newMessages));
                    return;
                }

                pendingMessages = await Poll(config.GetSteeringMessages);
            }

            var followUps = await Poll(config.GetFollowUpMessages);
            if (followUps.Count > 0)
            {
                pendingMessages = followUps;
                continue;
            }
            break;
        }

        await emit(new AgentEndEvent(newMessages));
    }

    private static async Task<AssistantMessage> StreamAssistantResponseAsync(AgentContext context, AgentLoopConfig config, CancellationToken ct, AgentEventSink emit, StreamFn streamFunction)
    {
        var messages = context.Messages;
        if (config.TransformContext is not null) messages = await config.TransformContext(messages, ct);

        var llmMessages = await config.ConvertToLlm(messages);
        var llmContext = new Context
        {
            SystemPrompt = context.SystemPrompt,
            Messages = llmMessages,
            Tools = context.Tools?.Cast<Tool>().ToList(),
        };

        string? resolvedApiKey = null;
        if (config.GetApiKey is not null) resolvedApiKey = await config.GetApiKey(config.Model.Provider);
        if (string.IsNullOrEmpty(resolvedApiKey)) resolvedApiKey = config.ApiKey;

        var streamOptions = config.CloneConfig();
        streamOptions.ApiKey = resolvedApiKey;
        streamOptions.CancellationToken = ct;

        var response = await streamFunction(config.Model, llmContext, streamOptions);

        AssistantMessage? partial = null;
        var addedPartial = false;

        await foreach (var evt in response)
        {
            switch (evt)
            {
                case StartEvent start:
                    partial = start.Partial;
                    context.Messages.Add(partial);
                    addedPartial = true;
                    await emit(new MessageStartEvent(partial.ShallowCopy()));
                    break;
                case DoneEvent or ErrorEvent:
                {
                    var finalMessage = await response.Result();
                    if (addedPartial) context.Messages[^1] = finalMessage;
                    else context.Messages.Add(finalMessage);
                    if (!addedPartial) await emit(new MessageStartEvent(finalMessage.ShallowCopy()));
                    await emit(new MessageEndEvent(finalMessage));
                    return finalMessage;
                }
                default:
                    if (partial is not null)
                    {
                        partial = PartialOf(evt) ?? partial;
                        context.Messages[^1] = partial;
                        await emit(new MessageUpdateEvent(partial.ShallowCopy(), evt));
                    }
                    break;
            }
        }

        var final = await response.Result();
        if (addedPartial)
        {
            context.Messages[^1] = final;
        }
        else
        {
            context.Messages.Add(final);
            await emit(new MessageStartEvent(final.ShallowCopy()));
        }
        await emit(new MessageEndEvent(final));
        return final;
    }

    private static AssistantMessage? PartialOf(AssistantMessageEvent evt) => evt switch
    {
        TextStartEvent e => e.Partial,
        TextDeltaEvent e => e.Partial,
        TextEndEvent e => e.Partial,
        ThinkingStartEvent e => e.Partial,
        ThinkingDeltaEvent e => e.Partial,
        ThinkingEndEvent e => e.Partial,
        ToolCallStartEvent e => e.Partial,
        ToolCallDeltaEvent e => e.Partial,
        ToolCallEndEvent e => e.Partial,
        _ => null,
    };

    private sealed record ExecutedToolCallBatch(List<ToolResultMessage> Messages, bool Terminate);

    private sealed record FinalizedToolCall(ToolCall ToolCall, AgentToolResult Result, bool IsError);

    private abstract record Preparation;

    private sealed record PreparedToolCall(ToolCall ToolCall, AgentTool Tool, JsonObject Args) : Preparation;

    private sealed record ImmediateOutcome(AgentToolResult Result, bool IsError) : Preparation;

    private static async Task<ExecutedToolCallBatch> FailToolCallsFromTruncatedMessageAsync(List<ToolCall> toolCalls, AgentEventSink emit)
    {
        var messages = new List<ToolResultMessage>();
        foreach (var toolCall in toolCalls)
        {
            await emit(new ToolExecutionStartEvent(toolCall.Id, toolCall.Name, toolCall.Arguments));
            var finalized = new FinalizedToolCall(toolCall, CreateErrorToolResult(
                $"Tool call \"{toolCall.Name}\" was not executed: the response hit the output token limit, so its arguments may be truncated. Re-issue the tool call with complete arguments."), true);
            await EmitToolExecutionEndAsync(finalized, emit);
            var message = CreateToolResultMessage(finalized);
            await EmitToolResultMessageAsync(message, emit);
            messages.Add(message);
        }
        return new ExecutedToolCallBatch(messages, false);
    }

    private static Task<ExecutedToolCallBatch> ExecuteToolCallsAsync(AgentContext context, AssistantMessage assistant, AgentLoopConfig config, CancellationToken ct, AgentEventSink emit)
    {
        var toolCalls = assistant.Content.OfType<ToolCall>().ToList();
        var hasSequential = toolCalls.Any(tc => context.Tools?.FirstOrDefault(t => t.Name == tc.Name)?.ExecutionMode == ToolExecutionMode.Sequential);
        return config.ToolExecution == ToolExecutionMode.Sequential || hasSequential
            ? ExecuteSequentialAsync(context, assistant, toolCalls, config, ct, emit)
            : ExecuteParallelAsync(context, assistant, toolCalls, config, ct, emit);
    }

    private static async Task<ExecutedToolCallBatch> ExecuteSequentialAsync(AgentContext context, AssistantMessage assistant, List<ToolCall> toolCalls, AgentLoopConfig config, CancellationToken ct, AgentEventSink emit)
    {
        var finalizedCalls = new List<FinalizedToolCall>();
        var messages = new List<ToolResultMessage>();

        foreach (var toolCall in toolCalls)
        {
            await emit(new ToolExecutionStartEvent(toolCall.Id, toolCall.Name, toolCall.Arguments));
            var preparation = await PrepareToolCallAsync(context, assistant, toolCall, config, ct);
            FinalizedToolCall finalized;
            if (preparation is ImmediateOutcome immediate)
            {
                finalized = new FinalizedToolCall(toolCall, immediate.Result, immediate.IsError);
            }
            else
            {
                var prepared = (PreparedToolCall)preparation;
                var executed = await ExecutePreparedToolCallAsync(prepared, ct, emit);
                finalized = await FinalizeExecutedToolCallAsync(context, assistant, prepared, executed, config, ct);
            }

            await EmitToolExecutionEndAsync(finalized, emit);
            var message = CreateToolResultMessage(finalized);
            await EmitToolResultMessageAsync(message, emit);
            finalizedCalls.Add(finalized);
            messages.Add(message);
            if (ct.IsCancellationRequested) break;
        }

        return new ExecutedToolCallBatch(messages, ShouldTerminate(finalizedCalls));
    }

    private static async Task<ExecutedToolCallBatch> ExecuteParallelAsync(AgentContext context, AssistantMessage assistant, List<ToolCall> toolCalls, AgentLoopConfig config, CancellationToken ct, AgentEventSink emit)
    {
        var entries = new List<Func<Task<FinalizedToolCall>>>();
        // Serialize event emission from concurrently finishing tools so sinks see a consistent order.
        var emitLock = new SemaphoreSlim(1, 1);
        async Task SafeEmit(AgentEvent e)
        {
            await emitLock.WaitAsync();
            try
            {
                await emit(e);
            }
            finally
            {
                emitLock.Release();
            }
        }

        foreach (var toolCall in toolCalls)
        {
            await emit(new ToolExecutionStartEvent(toolCall.Id, toolCall.Name, toolCall.Arguments));
            var preparation = await PrepareToolCallAsync(context, assistant, toolCall, config, ct);
            if (preparation is ImmediateOutcome immediate)
            {
                var finalized = new FinalizedToolCall(toolCall, immediate.Result, immediate.IsError);
                await EmitToolExecutionEndAsync(finalized, emit);
                entries.Add(() => Task.FromResult(finalized));
                if (ct.IsCancellationRequested) break;
                continue;
            }

            var prepared = (PreparedToolCall)preparation;
            entries.Add(async () =>
            {
                if (ct.IsCancellationRequested)
                {
                    var aborted = new FinalizedToolCall(toolCall, CreateErrorToolResult("Operation aborted"), true);
                    await EmitToolExecutionEndAsync(aborted, SafeEmit);
                    return aborted;
                }
                var executed = await ExecutePreparedToolCallAsync(prepared, ct, SafeEmit);
                var finalized = await FinalizeExecutedToolCallAsync(context, assistant, prepared, executed, config, ct);
                await EmitToolExecutionEndAsync(finalized, SafeEmit);
                return finalized;
            });
            if (ct.IsCancellationRequested) break;
        }

        var ordered = await Task.WhenAll(entries.Select(e => Task.Run(e)));
        var messages = new List<ToolResultMessage>();
        foreach (var finalized in ordered)
        {
            var message = CreateToolResultMessage(finalized);
            await EmitToolResultMessageAsync(message, emit);
            messages.Add(message);
        }
        return new ExecutedToolCallBatch(messages, ShouldTerminate(ordered));
    }

    private static bool ShouldTerminate(IReadOnlyCollection<FinalizedToolCall> calls) =>
        calls.Count > 0 && calls.All(c => c.Result.Terminate == true);

    private static async Task<Preparation> PrepareToolCallAsync(AgentContext context, AssistantMessage assistant, ToolCall toolCall, AgentLoopConfig config, CancellationToken ct)
    {
        var tool = context.Tools?.FirstOrDefault(t => t.Name == toolCall.Name);
        if (tool is null) return new ImmediateOutcome(CreateErrorToolResult($"Tool {toolCall.Name} not found"), true);

        try
        {
            var preparedCall = toolCall;
            if (tool.PrepareArguments is not null)
            {
                var preparedArgs = tool.PrepareArguments(toolCall.Arguments);
                if (!ReferenceEquals(preparedArgs, toolCall.Arguments))
                {
                    preparedCall = toolCall.Clone();
                    preparedCall.Arguments = preparedArgs;
                }
            }
            var validatedArgs = ToolValidation.ValidateToolArguments(tool, preparedCall);
            if (config.BeforeToolCall is not null)
            {
                var before = await config.BeforeToolCall(new BeforeToolCallContext(assistant, toolCall, validatedArgs, context), ct);
                if (ct.IsCancellationRequested) return new ImmediateOutcome(CreateErrorToolResult("Operation aborted"), true);
                if (before?.Block == true)
                {
                    var result = CreateErrorToolResult(string.IsNullOrEmpty(before.Reason) ? "Tool execution was blocked" : before.Reason);
                    if (before.Terminate == true) result.Terminate = true;
                    return new ImmediateOutcome(result, true);
                }
            }
            if (ct.IsCancellationRequested) return new ImmediateOutcome(CreateErrorToolResult("Operation aborted"), true);
            return new PreparedToolCall(toolCall, tool, validatedArgs);
        }
        catch (Exception ex)
        {
            return new ImmediateOutcome(CreateErrorToolResult(ex.Message), true);
        }
    }

    private static async Task<(AgentToolResult Result, bool IsError)> ExecutePreparedToolCallAsync(PreparedToolCall prepared, CancellationToken ct, AgentEventSink emit)
    {
        var updateEvents = new List<Task>();
        var accepting = true;
        var gate = new object();

        void OnUpdate(AgentToolResult partial)
        {
            lock (gate)
            {
                if (!accepting) return;
                updateEvents.Add(emit(new ToolExecutionUpdateEvent(prepared.ToolCall.Id, prepared.ToolCall.Name, prepared.ToolCall.Arguments, partial)));
            }
        }

        try
        {
            var result = await prepared.Tool.Execute(prepared.ToolCall.Id, prepared.Args, ct, OnUpdate);
            lock (gate) accepting = false;
            await Task.WhenAll(updateEvents);
            return (result, false);
        }
        catch (Exception ex)
        {
            lock (gate) accepting = false;
            await Task.WhenAll(updateEvents);
            var message = ex is OperationCanceledException && ct.IsCancellationRequested ? "Operation aborted" : ex.Message;
            return (CreateErrorToolResult(message), true);
        }
    }

    private static async Task<FinalizedToolCall> FinalizeExecutedToolCallAsync(AgentContext context, AssistantMessage assistant, PreparedToolCall prepared, (AgentToolResult Result, bool IsError) executed, AgentLoopConfig config, CancellationToken ct)
    {
        var result = executed.Result;
        var isError = executed.IsError;
        if (config.AfterToolCall is not null)
        {
            try
            {
                var after = await config.AfterToolCall(new AfterToolCallContext(assistant, prepared.ToolCall, prepared.Args, result, isError, context), ct);
                if (after is not null)
                {
                    result = new AgentToolResult
                    {
                        Content = after.Content ?? result.Content,
                        Details = after.Details ?? result.Details,
                        Usage = after.Usage ?? result.Usage,
                        Terminate = after.Terminate ?? result.Terminate,
                        AddedToolNames = result.AddedToolNames,
                    };
                    isError = after.IsError ?? isError;
                }
            }
            catch (Exception ex)
            {
                result = CreateErrorToolResult(ex.Message);
                isError = true;
            }
        }
        return new FinalizedToolCall(prepared.ToolCall, result, isError);
    }

    private static AgentToolResult CreateErrorToolResult(string message) =>
        new() { Content = [new TextContent(message)], Details = new JsonObject() };

    private static Task EmitToolExecutionEndAsync(FinalizedToolCall finalized, AgentEventSink emit) =>
        emit(new ToolExecutionEndEvent(finalized.ToolCall.Id, finalized.ToolCall.Name, finalized.Result, finalized.IsError));

    private static ToolResultMessage CreateToolResultMessage(FinalizedToolCall finalized) => new()
    {
        ToolCallId = finalized.ToolCall.Id,
        ToolName = finalized.ToolCall.Name,
        Content = finalized.Result.Content ?? [],
        Details = finalized.Result.Details,
        Usage = finalized.Result.Usage,
        AddedToolNames = finalized.Result.AddedToolNames is { Count: > 0 } names ? names : null,
        IsError = finalized.IsError,
        Timestamp = TimeUtil.NowMs(),
    };

    private static async Task EmitToolResultMessageAsync(ToolResultMessage message, AgentEventSink emit)
    {
        await emit(new MessageStartEvent(message));
        await emit(new MessageEndEvent(message));
    }
}
