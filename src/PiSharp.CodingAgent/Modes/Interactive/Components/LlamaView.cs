using PiSharp.CodingAgent.Core.Tools;
using PiSharp.CodingAgent.Extensions.Llama;
using PiSharp.Tui;
using PiSharp.Tui.Components;

namespace PiSharp.CodingAgent.Modes.Interactive.Components;

public abstract record LlamaManagerAction
{
    public sealed record SelectModel(LlamaModelInfo Model) : LlamaManagerAction;

    public sealed record Close : LlamaManagerAction;
}

public sealed class LlamaProgressState(string title, string model, string message)
{
    public string Title { get; } = title;
    public string Model { get; } = model;
    public string Message { get; set; } = message;
    public double? Ratio { get; set; }
    public string? Detail { get; set; }

    public void Apply(LlamaProgress progress)
    {
        Message = progress.Message;
        Ratio = progress.Ratio;
        Detail = progress.Detail;
    }
}

internal static class LlamaFrames
{
    public static SelectListTheme SelectTheme()
    {
        var theme = ThemeManager.Current;
        return new SelectListTheme
        {
            SelectedPrefix = text => theme.Fg("accent", text),
            SelectedText = text => theme.Fg("accent", text),
            Description = text => theme.Fg("muted", text),
            ScrollInfo = text => theme.Fg("dim", text),
            NoMatch = text => theme.Fg("warning", text),
        };
    }

    public static Container Frame(string title, IEnumerable<IComponent> body, string? footer = null)
    {
        var theme = ThemeManager.Current;
        var container = new Container();
        container.AddChild(new DynamicBorder(text => theme.Fg("accent", text)));
        container.AddChild(new Text(theme.Fg("accent", theme.Bold(title)), 1, 0));
        foreach (var child in body) container.AddChild(child);
        if (footer is not null)
        {
            container.AddChild(new Spacer(1));
            container.AddChild(new Text(theme.Fg("dim", footer), 1, 0));
        }
        container.AddChild(new DynamicBorder(text => theme.Fg("accent", text)));
        return container;
    }
}

/// <summary>The /llama manager view shown in place of the editor. Port of LlamaView in extensions/llama/ui.ts.</summary>
public sealed class LlamaView : IInputComponent, IFocusable
{
    private readonly TuiBase _tui;
    private Container _content;
    private IInputComponent? _inputHandler;
    private IFocusable? _inputTarget;
    private TaskCompletionSource? _progressTcs;
    private bool _showingProgress;
    private bool _focused;

    public LlamaView(TuiBase tui)
    {
        _tui = tui;
        _content = LlamaFrames.Frame("llama.cpp models", [new Text(ThemeManager.Current.Fg("muted", "Loading…"), 1, 1)]);
    }

    public bool Focused
    {
        get => _focused;
        set
        {
            _focused = value;
            if (_inputTarget is not null) _inputTarget.Focused = value;
        }
    }

    private void SetContent(Container content, IInputComponent? inputHandler = null, IFocusable? inputTarget = null)
    {
        if (_inputTarget is not null) _inputTarget.Focused = false;
        _progressTcs = null;
        _showingProgress = false;
        _content = content;
        _inputHandler = inputHandler;
        _inputTarget = inputTarget;
        if (_inputTarget is not null) _inputTarget.Focused = _focused;
        _tui.RequestRender();
    }

    private static string ContextLabel(LlamaModelInfo model)
    {
        static string Format(long value) => value >= 1000 ? $"{Math.Round(value / 1000.0, MidpointRounding.AwayFromZero)}k" : value.ToString();
        if ((model.ContextSize ?? model.TrainContextSize) is { } context and > 0) return Format(context);
        var args = model.Status.Args ?? [];
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (args[index] is not ("--ctx-size" or "-c" or "-ctx")) continue;
            if (double.TryParse(args[index + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) && value > 0)
            {
                return Format((long)value);
            }
        }
        return "";
    }

    private static string ModelDescription(LlamaModelInfo model)
    {
        var details = new List<string>();
        if (model.IsLoaded) details.Add("loaded");
        else if (model.Status.Value != "unloaded") details.Add(model.Status.Value);
        var context = model.IsLoaded ? ContextLabel(model) : "";
        if (context.Length > 0) details.Add($"{context} context");
        return string.Join(" · ", details);
    }

    public Task<LlamaManagerAction> ShowModelsAsync(string serverUrl, List<LlamaModelInfo> models)
    {
        var theme = ThemeManager.Current;
        var sorted = models.ToList();
        sorted.Sort((left, right) =>
        {
            var loaded = (right.Status.Value == "loaded" ? 1 : 0) - (left.Status.Value == "loaded" ? 1 : 0);
            return loaded != 0 ? loaded : NodeCompare.LocaleCompare(left.Id, right.Id);
        });
        var byId = sorted.GroupBy(m => m.Id).ToDictionary(g => g.Key, g => g.Last());
        var items = sorted.Select(m => new SelectItem(m.Id, m.Id, ModelDescription(m))).ToList();
        var tcs = new TaskCompletionSource<LlamaManagerAction>(TaskCreationOptions.RunContinuationsAsynchronously);
        var list = new SelectList(items, Math.Min(items.Count, 12), LlamaFrames.SelectTheme(), new SelectListLayoutOptions { MinPrimaryColumnWidth = 36, MaxPrimaryColumnWidth = 56 })
        {
            OnSelect = item =>
            {
                if (byId.TryGetValue(item.Value, out var model)) tcs.TrySetResult(new LlamaManagerAction.SelectModel(model));
            },
            OnCancel = () => tcs.TrySetResult(new LlamaManagerAction.Close()),
        };
        SetContent(LlamaFrames.Frame("llama.cpp models", [new Text(theme.Fg("dim", serverUrl), 1, 0), new Spacer(1), list],
            $"{KeyHints.KeyHint("tui.select.confirm", "load/unload")} • {KeyHints.KeyHint("tui.select.cancel", "close")}"), list);
        return tcs.Task;
    }

    public Task<string?> SelectAsync(string title, List<string> options)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var list = new SelectList(options.Select(o => new SelectItem(o, o, null)).ToList(), Math.Min(options.Count, 12), LlamaFrames.SelectTheme())
        {
            OnSelect = item => tcs.TrySetResult(item.Value),
            OnCancel = () => tcs.TrySetResult(null),
        };
        SetContent(LlamaFrames.Frame(title, [new Spacer(1), list], $"{KeyHints.KeyHint("tui.select.confirm", "select")} • {KeyHints.KeyHint("tui.select.cancel", "cancel")}"), list);
        return tcs.Task;
    }

    public async Task<bool> ConfirmAsync(string title, string message) => await SelectAsync($"{title}\n{message}", ["Yes", "No"]) == "Yes";

    public async Task<bool> ConnectionErrorShouldRetryAsync(string serverUrl, string message) =>
        await SelectAsync($"llama.cpp unavailable\n{serverUrl}\n\n{message}", ["Retry", "Close"]) == "Retry";

    public void ShowStatus(string title, string message) =>
        SetContent(LlamaFrames.Frame(title, [new Spacer(1), new Text(ThemeManager.Current.Fg("muted", message), 1, 0)]));

    /// <summary>Show progress; the returned task completes when the user presses cancel.</summary>
    public Task ProgressAsync(LlamaProgressState state)
    {
        _progressTcs ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _showingProgress = true;
        UpdateProgress(state);
        return _progressTcs.Task;
    }

    public void UpdateProgress(LlamaProgressState state)
    {
        if (!_showingProgress) return;
        var theme = ThemeManager.Current;
        var body = new List<IComponent>
        {
            new Text(theme.Fg("text", state.Model), 1, 0),
            new Spacer(1),
            new Text(theme.Fg("muted", state.Message), 1, 0),
        };
        if (state.Ratio is { } ratio)
        {
            const int available = 40;
            var filled = (int)Math.Round(Math.Clamp(ratio, 0, 1) * available, MidpointRounding.AwayFromZero);
            body.Add(new Text(theme.Fg("accent", $"{new string('█', filled)}{new string('─', available - filled)} {Math.Round(ratio * 100, MidpointRounding.AwayFromZero)}%"), 1, 0));
        }
        if (state.Detail is not null) body.Add(new Text(theme.Fg("dim", state.Detail), 1, 0));
        _content = LlamaFrames.Frame(state.Title, body, KeyHints.KeyHint("tui.select.cancel", "stop"));
        _inputHandler = null;
        _tui.RequestRender();
    }

    public void HandleInput(string data)
    {
        if (_progressTcs is not null && KeybindingsManager.Global.Matches(data, "tui.select.cancel"))
        {
            var tcs = _progressTcs;
            _progressTcs = null;
            tcs.TrySetResult();
            return;
        }
        _inputHandler?.HandleInput(data);
        _tui.RequestRender();
    }

    public List<string> Render(int width) =>
        _content.Render(width).Select(line => TextUtils.VisibleWidth(line) > width ? TextUtils.TruncateToWidth(line, width, "") : line).ToList();

    public void Invalidate() => _content.Invalidate();

    /// <summary>Run a long operation with a progress view that the user can stop. Port of runWithProgress.</summary>
    public async Task<(bool Cancelled, T? Value)> RunWithProgressAsync<T>(string title, string model, string initialMessage, string cancelTitle, string cancelMessage,
        Func<CancellationToken, Action<LlamaProgress>, Task<T>> run, Func<Task> cancel)
    {
        using var cts = new CancellationTokenSource();
        var state = new LlamaProgressState(title, model, initialMessage);
        var runTask = run(cts.Token, progress =>
        {
            state.Apply(progress);
            UpdateProgress(state);
        });
        while (!runTask.IsCompleted)
        {
            var stopTask = ProgressAsync(state);
            if (await Task.WhenAny(runTask, stopTask) == runTask) break;
            var stop = await ConfirmAsync(cancelTitle, cancelMessage);
            if (!stop || runTask.IsCompleted) continue;
            try
            {
                await cancel();
            }
            finally
            {
                cts.Cancel();
            }
            try
            {
                await runTask;
            }
            catch
            {
                // Cancelled operation.
            }
            return (true, default);
        }
        return (false, await runTask);
    }
}
