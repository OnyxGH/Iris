using System.Collections.Concurrent;

namespace Iris.Tui;

/// <summary>
/// Single-threaded event loop standing in for Node's: terminal input, timers, render requests and async continuations
/// run on one thread so TUI components never see concurrent access. Install with <see cref="Run"/>.
/// </summary>
public sealed class UiDispatcher : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
    private int _threadId = -1;

    [ThreadStatic]
    private static UiDispatcher? _current;

    /// <summary>The dispatcher running on the current thread, if any.</summary>
    public static UiDispatcher? Current => _current;

    public bool IsOnDispatcherThread => Environment.CurrentManagedThreadId == _threadId;

    /// <summary>Unhandled exceptions from posted callbacks. Default: rethrow on the loop (terminates Run).</summary>
    public Action<Exception>? UnhandledException { get; set; }

    public override void Post(SendOrPostCallback d, object? state)
    {
        if (!_queue.IsAddingCompleted) _queue.Add((d, state));
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (IsOnDispatcherThread)
        {
            d(state);
            return;
        }
        using var done = new ManualResetEventSlim();
        Exception? error = null;
        Post(_ =>
        {
            try
            {
                d(state);
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                done.Set();
            }
        }, null);
        done.Wait();
        if (error is not null) throw new AggregateException(error);
    }

    public override SynchronizationContext CreateCopy() => this;

    public void Post(Action action) => Post(_ => action(), null);

    /// <summary>Run action on the dispatcher thread: inline when already there, otherwise queued.</summary>
    public void Invoke(Action action)
    {
        if (IsOnDispatcherThread) action();
        else Post(action);
    }

    /// <summary>Run an async function on the dispatcher thread and complete when it finishes.</summary>
    public Task InvokeAsync(Func<Task> action)
    {
        if (IsOnDispatcherThread) return action();
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(async () =>
        {
            try
            {
                await action();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    /// <summary>Equivalent of setTimeout; dispose to cancel.</summary>
    public IDisposable SetTimeout(Action action, int delayMs) => new DispatcherTimer(this, action, Math.Max(0, delayMs), Timeout.Infinite);

    /// <summary>Equivalent of setInterval; dispose to cancel.</summary>
    public IDisposable SetInterval(Action action, int intervalMs) => new DispatcherTimer(this, action, intervalMs, intervalMs);

    private sealed class DispatcherTimer : IDisposable
    {
        private readonly Timer _timer;
        private volatile bool _disposed;

        public DispatcherTimer(UiDispatcher dispatcher, Action action, int dueTime, int period)
        {
            _timer = new Timer(_ =>
            {
                if (_disposed) return;
                dispatcher.Post(() =>
                {
                    if (!_disposed) action();
                });
            }, null, dueTime, period);
        }

        public void Dispose()
        {
            _disposed = true;
            _timer.Dispose();
        }
    }

    /// <summary>Run the loop on the calling thread until the task returned by main completes; returns its result.</summary>
    public static T Run<T>(Func<Task<T>> main)
    {
        using var dispatcher = new UiDispatcher();
        var previousContext = SynchronizationContext.Current;
        dispatcher._threadId = Environment.CurrentManagedThreadId;
        _current = dispatcher;
        SynchronizationContext.SetSynchronizationContext(dispatcher);
        try
        {
            var task = main();
            task.ContinueWith(_ => dispatcher._queue.CompleteAdding(), TaskScheduler.Default);
            foreach (var (callback, state) in dispatcher._queue.GetConsumingEnumerable())
            {
                try
                {
                    callback(state);
                }
                catch (Exception ex) when (dispatcher.UnhandledException is not null)
                {
                    dispatcher.UnhandledException(ex);
                }
            }
            return task.GetAwaiter().GetResult();
        }
        finally
        {
            _current = null;
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    public void Dispose() => _queue.Dispose();
}
