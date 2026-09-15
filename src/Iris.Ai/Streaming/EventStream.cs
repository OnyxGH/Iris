using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Iris.Ai;

/// <summary>
/// Push-based async event stream with a final result.
/// Producers call <see cref="Push"/> and <see cref="End"/>; consumers iterate with await foreach
/// and/or await <see cref="Result"/>.
/// </summary>
public class EventStream<T, TResult> : IAsyncEnumerable<T>
{
    private readonly Channel<T> _channel = Channel.CreateUnbounded<T>(new UnboundedChannelOptions
    {
        SingleReader = false,
        SingleWriter = false,
        AllowSynchronousContinuations = false,
    });

    private readonly TaskCompletionSource<TResult> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<T, bool> _isComplete;
    private readonly Func<T, TResult> _extractResult;
    private readonly object _gate = new();
    private bool _done;

    public EventStream(Func<T, bool> isComplete, Func<T, TResult> extractResult)
    {
        _isComplete = isComplete;
        _extractResult = extractResult;
    }

    public void Push(T item)
    {
        lock (_gate)
        {
            if (_done) return;
            if (_isComplete(item))
            {
                _done = true;
                _result.TrySetResult(_extractResult(item));
            }
            _channel.Writer.TryWrite(item);
        }
    }

    public void End(TResult? result = default, bool hasResult = false)
    {
        lock (_gate)
        {
            _done = true;
            if (hasResult || result is not null) _result.TrySetResult(result!);
            _channel.Writer.TryComplete();
        }
    }

    public Task<TResult> Result() => _result.Task;

    public async IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        var reader = _channel.Reader;
        while (await reader.WaitToReadAsync(cancellationToken))
        {
            while (reader.TryRead(out var item))
            {
                yield return item;
            }
        }
    }
}

public sealed class AssistantMessageEventStream : EventStream<AssistantMessageEvent, AssistantMessage>
{
    public AssistantMessageEventStream()
        : base(
            e => e is DoneEvent or ErrorEvent,
            e => e switch
            {
                DoneEvent d => d.Message,
                ErrorEvent err => err.Error,
                _ => throw new InvalidOperationException("Unexpected event type for final result"),
            })
    {
    }

    /// <summary>
    /// Run an async producer that writes to a new stream. Unhandled exceptions from the producer terminate the
    /// stream with an error message built from <paramref name="model"/>.
    /// </summary>
    public static AssistantMessageEventStream Run(Model model, Func<AssistantMessageEventStream, Task> producer)
    {
        var stream = new AssistantMessageEventStream();
        _ = Task.Run(async () =>
        {
            try
            {
                await producer(stream);
            }
            catch (Exception ex)
            {
                var message = AssistantMessage.CreateEmpty(model, StopReason.Error);
                message.ErrorMessage = ex.Message;
                stream.Push(new ErrorEvent(StopReason.Error, message));
                stream.End(message);
            }
        });
        return stream;
    }

    /// <summary>
    /// Returns a stream synchronously while running async setup behind it.
    /// Setup failures terminate the stream with an error event.
    /// </summary>
    public static AssistantMessageEventStream Lazy(Model model, Func<Task<AssistantMessageEventStream>> setup)
    {
        var outer = new AssistantMessageEventStream();
        _ = Task.Run(async () =>
        {
            AssistantMessageEventStream inner;
            try
            {
                inner = await setup();
            }
            catch (Exception ex)
            {
                var message = AssistantMessage.CreateEmpty(model, StopReason.Error);
                message.ErrorMessage = ex is OperationCanceledException ? "Request was aborted" : ex.Message;
                outer.Push(new ErrorEvent(StopReason.Error, message));
                outer.End(message);
                return;
            }
            await Forward(outer, inner);
        });
        return outer;
    }

    public static async Task Forward(AssistantMessageEventStream target, AssistantMessageEventStream source)
    {
        await foreach (var e in source)
        {
            target.Push(e);
        }
        target.End(await source.Result());
    }

    /// <summary>Create a stream that immediately fails with the given error.</summary>
    public static AssistantMessageEventStream FromError(Model model, string errorMessage, StopReason reason = StopReason.Error)
    {
        var stream = new AssistantMessageEventStream();
        var message = AssistantMessage.CreateEmpty(model, reason);
        message.ErrorMessage = errorMessage;
        stream.Push(new ErrorEvent(reason, message));
        stream.End(message);
        return stream;
    }
}

public static class AsyncEnumerableConfigureExtensions
{
    public static ConfiguredCancelableAsyncEnumerable<T> WithToken<T>(this IAsyncEnumerable<T> source, CancellationToken token) =>
        source.WithCancellation(token);
}
