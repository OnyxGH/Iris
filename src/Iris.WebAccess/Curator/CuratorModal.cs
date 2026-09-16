using Iris.CodingAgent.Modes.Interactive;
using Iris.CodingAgent.Modes.Interactive.Components;
using Iris.Extensions;
using Iris.Tui;

namespace Iris.WebAccess.Curator;

/// <summary>What the user asked for when the curator modal closed.</summary>
internal enum CuratorAction
{
    Cancel,
    Submit,

    /// <summary>Submit, and summarize later searches of this run without opening the curator.</summary>
    SubmitAndAutoSummarize,
    Regenerate,
    Feedback,
    Edit,
}

internal sealed class CuratorSource(SearchResult result)
{
    public SearchResult Result { get; } = result;

    public bool Selected { get; set; } = true;
}

internal sealed class CuratorQuery(QueryResultData data)
{
    public QueryResultData Data { get; } = data;

    public List<CuratorSource> Sources { get; } = [.. data.Results.Select(r => new CuratorSource(r))];

    /// <summary>Failed queries start deselected: they carry no sources to summarize.</summary>
    public bool Selected { get; set; } = data.Error is null;

    public bool Expanded { get; set; } = true;

    public QueryResultData ToSelected() => new()
    {
        Query = Data.Query,
        Answer = Data.Answer,
        Results = [.. Sources.Where(s => s.Selected).Select(s => s.Result)],
        Error = Data.Error,
        Provider = Data.Provider,
    };
}

/// <summary>Selection and summary state shared across reopenings of the curator modal.</summary>
internal sealed class CuratorState(List<QueryResultData> results)
{
    public List<CuratorQuery> Queries { get; } = [.. results.Select(r => new CuratorQuery(r))];

    public string Summary { get; set; } = "";

    public SummaryMeta Meta { get; set; } = new(null, 0, 0, false);

    public string? Feedback { get; set; }

    public string? Status { get; set; }

    public int Cursor { get; set; }

    public int Scroll { get; set; }

    public int SummaryScroll { get; set; }

    public List<QueryResultData> Selected() => [.. Queries.Where(q => q.Selected).Select(q => q.ToSelected())];

    public int SelectedSourceCount() => Queries.Where(q => q.Selected).Sum(q => q.Sources.Count(s => s.Selected));
}

/// <summary>
/// The search curator: review the queries and sources a search returned, then approve the summary that is handed to the
/// model. Runs as a modal overlay in the TUI.
/// </summary>
internal sealed class CuratorModal : IInputComponent
{
    private readonly record struct Row(int QueryIndex, int SourceIndex);

    private readonly CuratorState _state;
    private readonly CustomUIContext<CuratorAction> _context;
    private readonly List<Row> _rows = [];
    private int _listHeight = 8;
    private int _summaryHeight = 6;

    public CuratorModal(CustomUIContext<CuratorAction> context, CuratorState state)
    {
        _context = context;
        _state = state;
        BuildRows();
        var rows = Math.Max(12, context.Tui.Terminal.Rows);
        // Leave room for the header, summary block, hints and borders.
        var body = Math.Max(6, (int)(rows * 0.8) - 12);
        _listHeight = Math.Max(3, Math.Min(body * 2 / 3, Math.Max(3, _rows.Count)));
        _summaryHeight = Math.Max(3, body - _listHeight);
    }

    private Theme Theme => ThemeManager.Current;

    private void BuildRows()
    {
        _rows.Clear();
        for (var q = 0; q < _state.Queries.Count; q++)
        {
            _rows.Add(new Row(q, -1));
            if (!_state.Queries[q].Expanded) continue;
            for (var s = 0; s < _state.Queries[q].Sources.Count; s++) _rows.Add(new Row(q, s));
        }
        if (_state.Cursor >= _rows.Count) _state.Cursor = Math.Max(0, _rows.Count - 1);
    }

    public void Invalidate()
    {
    }

    public List<string> Render(int width)
    {
        var theme = Theme;
        var inner = Math.Max(20, width - 2);
        var lines = new List<string> { theme.Fg("border", new string('─', width)) };

        var selectedQueries = _state.Queries.Count(q => q.Selected);
        var header = $" Search curator · {selectedQueries}/{_state.Queries.Count} queries, {_state.SelectedSourceCount()} sources";
        lines.Add(theme.Fg("accent", theme.Bold(TextUtils.TruncateToWidth(header, inner))));
        if (_state.Status is { Length: > 0 } status) lines.Add(" " + theme.Fg("warning", TextUtils.TruncateToWidth(status, inner)));
        lines.Add("");

        EnsureCursorVisible();
        for (var i = _state.Scroll; i < Math.Min(_rows.Count, _state.Scroll + _listHeight); i++) lines.Add(RenderRow(i, inner));
        for (var i = _rows.Count - _state.Scroll; i < _listHeight; i++) lines.Add("");
        if (_rows.Count > _listHeight)
        {
            lines.Add(" " + theme.Fg("dim", $"{_state.Scroll + 1}-{Math.Min(_rows.Count, _state.Scroll + _listHeight)} of {_rows.Count}"));
        }

        lines.Add("");
        var summaryTitle = _state.Meta.Model is { } model ? $"── Summary ({model}) " : _state.Summary.Length > 0 ? "── Summary (deterministic) " : "── Summary ";
        lines.Add(" " + theme.Fg("accent", TextUtils.TruncateToWidth(summaryTitle + new string('─', Math.Max(0, inner - summaryTitle.Length)), inner)));
        var summaryLines = _state.Summary.Length > 0
            ? _state.Summary.Split('\n').SelectMany(line => TextUtils.WrapTextWithAnsi(line, inner - 1)).ToList()
            : [theme.Fg("dim", "(no summary yet)")];
        _state.SummaryScroll = Math.Clamp(_state.SummaryScroll, 0, Math.Max(0, summaryLines.Count - _summaryHeight));
        for (var i = _state.SummaryScroll; i < Math.Min(summaryLines.Count, _state.SummaryScroll + _summaryHeight); i++) lines.Add(" " + summaryLines[i]);
        for (var i = summaryLines.Count - _state.SummaryScroll; i < _summaryHeight; i++) lines.Add("");
        if (summaryLines.Count > _summaryHeight)
        {
            lines.Add(" " + theme.Fg("dim", $"summary lines {_state.SummaryScroll + 1}-{Math.Min(summaryLines.Count, _state.SummaryScroll + _summaryHeight)} of {summaryLines.Count}"));
        }

        lines.Add("");
        lines.Add(" " + string.Join("  ", [
            KeyHints.RawKeyHint("↑↓", "move"),
            KeyHints.RawKeyHint("space", "toggle"),
            KeyHints.RawKeyHint("tab", "fold"),
            KeyHints.RawKeyHint("a/n", "all/none"),
        ]));
        lines.Add(" " + string.Join("  ", [
            KeyHints.RawKeyHint("g", "regenerate"),
            KeyHints.RawKeyHint("f", "feedback"),
            KeyHints.RawKeyHint("e", "edit"),
            KeyHints.RawKeyHint("pgup/pgdn", "scroll summary"),
        ]));
        lines.Add(" " + string.Join("  ", [
            KeyHints.RawKeyHint("enter", "submit"),
            KeyHints.RawKeyHint("s", "submit + auto-summarize the rest"),
            KeyHints.RawKeyHint("esc", "cancel"),
        ]));
        lines.Add(theme.Fg("border", new string('─', width)));
        // An overlay paints only what each line contains, so pad them to hide the transcript behind the modal.
        return [.. lines.Select(line => TextUtils.TruncateToWidth(line, width, "", pad: true))];
    }

    private string RenderRow(int index, int inner)
    {
        var theme = Theme;
        var row = _rows[index];
        var query = _state.Queries[row.QueryIndex];
        var cursor = index == _state.Cursor ? theme.Fg("accent", "→ ") : "  ";
        if (row.SourceIndex < 0)
        {
            var box = query.Selected ? theme.Fg("success", "[x]") : theme.Fg("dim", "[ ]");
            var provider = query.Data.Provider is { Length: > 0 } p ? $" ({p})" : "";
            var label = $"\"{query.Data.Query}\"{provider}";
            if (query.Data.Error is { } error)
            {
                var head = $" {cursor}{box} " + TextUtils.TruncateToWidth(theme.Fg(query.Selected ? "text" : "dim", label), Math.Max(10, inner / 2));
                var message = error.ReplaceLineEndings(" ");
                return head + theme.Fg("error", TextUtils.TruncateToWidth($" · {message}", Math.Max(8, inner - TextUtils.VisibleWidth(head))));
            }
            var suffix = theme.Fg("dim", $" · {query.Sources.Count(s => s.Selected)}/{query.Sources.Count} sources");
            return $" {cursor}{box} " + TextUtils.TruncateToWidth(theme.Fg(query.Selected ? "text" : "dim", label), Math.Max(10, inner - 24)) + suffix;
        }

        var source = query.Sources[row.SourceIndex];
        var sourceBox = source.Selected && query.Selected ? theme.Fg("success", "[x]") : theme.Fg("dim", "[ ]");
        var domain = Uri.TryCreate(source.Result.Url, UriKind.Absolute, out var uri) ? uri.Host : source.Result.Url;
        var title = TextUtils.TruncateToWidth(source.Result.Title, Math.Max(10, inner - domain.Length - 14));
        return $" {cursor}   {sourceBox} " + theme.Fg(source.Selected && query.Selected ? "muted" : "dim", title) + theme.Fg("dim", $" · {domain}");
    }

    private void EnsureCursorVisible()
    {
        if (_state.Cursor < _state.Scroll) _state.Scroll = _state.Cursor;
        else if (_state.Cursor >= _state.Scroll + _listHeight) _state.Scroll = _state.Cursor - _listHeight + 1;
        _state.Scroll = Math.Clamp(_state.Scroll, 0, Math.Max(0, _rows.Count - _listHeight));
    }

    public void HandleInput(string data)
    {
        var keybindings = KeybindingsManager.Global;
        if (keybindings.Matches(data, "tui.select.cancel"))
        {
            _context.Done(CuratorAction.Cancel);
            return;
        }
        if (keybindings.Matches(data, "tui.select.confirm") || data == "\n")
        {
            _context.Done(CuratorAction.Submit);
            return;
        }
        if (keybindings.Matches(data, "tui.select.up") || data == "k") Move(-1);
        else if (keybindings.Matches(data, "tui.select.down") || data == "j") Move(1);
        else if (data == " ") Toggle();
        else if (data is "\t") ToggleExpanded();
        else if (data is "a" or "A") SetAll(true);
        else if (data is "n" or "N") SetAll(false);
        else if (data is "s" or "S") _context.Done(CuratorAction.SubmitAndAutoSummarize);
        else if (data is "g" or "G") _context.Done(CuratorAction.Regenerate);
        else if (data is "f" or "F") _context.Done(CuratorAction.Feedback);
        else if (data is "e" or "E") _context.Done(CuratorAction.Edit);
        else if (Keys.Matches(data, "pageup")) _state.SummaryScroll = Math.Max(0, _state.SummaryScroll - _summaryHeight);
        else if (Keys.Matches(data, "pagedown")) _state.SummaryScroll += _summaryHeight;
        else return;
        _context.Tui.RequestRender();
    }

    private void Move(int delta)
    {
        if (_rows.Count == 0) return;
        _state.Cursor = Math.Clamp(_state.Cursor + delta, 0, _rows.Count - 1);
    }

    private void Toggle()
    {
        if (_rows.Count == 0) return;
        var row = _rows[_state.Cursor];
        var query = _state.Queries[row.QueryIndex];
        if (row.SourceIndex < 0)
        {
            query.Selected = !query.Selected;
            return;
        }
        var source = query.Sources[row.SourceIndex];
        source.Selected = !source.Selected;
        // Selecting a source of a deselected query brings the query back.
        if (source.Selected) query.Selected = true;
        else if (query.Sources.All(s => !s.Selected)) query.Selected = false;
    }

    private void ToggleExpanded()
    {
        if (_rows.Count == 0) return;
        var query = _state.Queries[_rows[_state.Cursor].QueryIndex];
        query.Expanded = !query.Expanded;
        var target = _rows[_state.Cursor] with { SourceIndex = -1 };
        BuildRows();
        var index = _rows.FindIndex(r => r.QueryIndex == target.QueryIndex && r.SourceIndex == -1);
        if (index >= 0) _state.Cursor = index;
    }

    private void SetAll(bool selected)
    {
        foreach (var query in _state.Queries)
        {
            query.Selected = selected && query.Data.Error is null;
            foreach (var source in query.Sources) source.Selected = selected;
        }
    }
}
