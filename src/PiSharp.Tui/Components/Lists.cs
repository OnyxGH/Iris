using System.Text.RegularExpressions;

namespace PiSharp.Tui.Components;

public sealed record SelectItem(string Value, string Label, string? Description = null);

public sealed class SelectListTheme
{
    public required Func<string, string> SelectedPrefix { get; init; }
    public required Func<string, string> SelectedText { get; init; }
    public required Func<string, string> Description { get; init; }
    public required Func<string, string> ScrollInfo { get; init; }
    public required Func<string, string> NoMatch { get; init; }
}

public sealed record SelectListTruncateContext(string Text, int MaxWidth, int ColumnWidth, SelectItem Item, bool IsSelected);

public sealed class SelectListLayoutOptions
{
    public int? MinPrimaryColumnWidth { get; init; }
    public int? MaxPrimaryColumnWidth { get; init; }
    public Func<SelectListTruncateContext, string>? TruncatePrimary { get; init; }
}

/// <summary>Scrollable selection list with optional descriptions. Port of pi-tui SelectList.</summary>
public sealed partial class SelectList : IInputComponent
{
    private const int DefaultPrimaryColumnWidth = 32;
    private const int PrimaryColumnGap = 2;
    private const int MinDescriptionWidth = 10;

    private readonly List<SelectItem> _items;
    private List<SelectItem> _filtered;
    private int _selectedIndex;
    private readonly int _maxVisible;
    private readonly SelectListTheme _theme;
    private readonly SelectListLayoutOptions _layout;

    public Action<SelectItem>? OnSelect { get; set; }
    public Action? OnCancel { get; set; }
    public Action<SelectItem>? OnSelectionChange { get; set; }

    public SelectList(List<SelectItem> items, int maxVisible, SelectListTheme theme, SelectListLayoutOptions? layout = null)
    {
        _items = items;
        _filtered = items;
        _maxVisible = maxVisible;
        _theme = theme;
        _layout = layout ?? new SelectListLayoutOptions();
    }

    [GeneratedRegex("[\r\n]+")]
    private static partial Regex NewLines();

    private static string NormalizeToSingleLine(string text) => NewLines().Replace(text, " ").Trim();

    public void SetFilter(string filter)
    {
        _filtered = _items.Where(i => i.Value.StartsWith(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        _selectedIndex = 0;
    }

    public void SetSelectedIndex(int index) => _selectedIndex = Math.Max(0, Math.Min(index, _filtered.Count - 1));

    public void Invalidate()
    {
    }

    public List<string> Render(int width)
    {
        if (_filtered.Count == 0) return [_theme.NoMatch("  No matching commands")];
        var primaryWidth = GetPrimaryColumnWidth();
        var (start, end) = GetVisibleRange();
        var lines = new List<string>();
        for (var i = start; i < end; i++)
        {
            var item = _filtered[i];
            var desc = item.Description is { Length: > 0 } d ? NormalizeToSingleLine(d) : null;
            lines.Add(RenderItem(item, i == _selectedIndex, width, desc, primaryWidth));
        }
        if (start > 0 || end < _filtered.Count)
        {
            lines.Add(_theme.ScrollInfo(TextUtils.TruncateToWidth($"  ({_selectedIndex + 1}/{_filtered.Count})", width - 2, "")));
        }
        return lines;
    }

    public void HandleInput(string data)
    {
        var kb = KeybindingsManager.Global;
        if (kb.Matches(data, "tui.select.up"))
        {
            _selectedIndex = _selectedIndex == 0 ? _filtered.Count - 1 : _selectedIndex - 1;
            NotifySelectionChange();
        }
        else if (kb.Matches(data, "tui.select.down"))
        {
            _selectedIndex = _selectedIndex == _filtered.Count - 1 ? 0 : _selectedIndex + 1;
            NotifySelectionChange();
        }
        else if (kb.Matches(data, "tui.select.confirm"))
        {
            if (GetSelectedItem() is { } item) OnSelect?.Invoke(item);
        }
        else if (kb.Matches(data, "tui.select.cancel"))
        {
            OnCancel?.Invoke();
        }
    }

    private (int Start, int End) GetVisibleRange()
    {
        var start = Math.Max(0, Math.Min(_selectedIndex - _maxVisible / 2, _filtered.Count - _maxVisible));
        return (start, Math.Min(start + _maxVisible, _filtered.Count));
    }

    private string RenderItem(SelectItem item, bool isSelected, int width, string? description, int primaryWidth)
    {
        var prefix = isSelected ? "→ " : "  ";
        var prefixWidth = TextUtils.VisibleWidth(prefix);

        if (description is { Length: > 0 } && width > 40)
        {
            var effective = Math.Max(1, Math.Min(primaryWidth, width - prefixWidth - 4));
            var maxPrimary = Math.Max(1, effective - PrimaryColumnGap);
            var value = TruncatePrimary(item, isSelected, maxPrimary, effective);
            var valueWidth = TextUtils.VisibleWidth(value);
            var spacing = new string(' ', Math.Max(1, effective - valueWidth));
            var remaining = width - (prefixWidth + valueWidth + spacing.Length) - 2;
            if (remaining > MinDescriptionWidth)
            {
                var desc = TextUtils.TruncateToWidth(description, remaining, "");
                if (isSelected) return _theme.SelectedText($"{prefix}{value}{spacing}{desc}");
                return prefix + value + _theme.Description(spacing + desc);
            }
        }

        var maxWidth = width - prefixWidth - 2;
        var truncated = TruncatePrimary(item, isSelected, maxWidth, maxWidth);
        return isSelected ? _theme.SelectedText($"{prefix}{truncated}") : prefix + truncated;
    }

    private int GetPrimaryColumnWidth()
    {
        var rawMin = _layout.MinPrimaryColumnWidth ?? _layout.MaxPrimaryColumnWidth ?? DefaultPrimaryColumnWidth;
        var rawMax = _layout.MaxPrimaryColumnWidth ?? _layout.MinPrimaryColumnWidth ?? DefaultPrimaryColumnWidth;
        var min = Math.Max(1, Math.Min(rawMin, rawMax));
        var max = Math.Max(1, Math.Max(rawMin, rawMax));
        var widest = _filtered.Aggregate(0, (w, item) => Math.Max(w, TextUtils.VisibleWidth(DisplayValue(item)) + PrimaryColumnGap));
        return Math.Max(min, Math.Min(widest, max));
    }

    private string TruncatePrimary(SelectItem item, bool isSelected, int maxWidth, int columnWidth)
    {
        var display = DisplayValue(item);
        var truncated = _layout.TruncatePrimary is { } fn
            ? fn(new SelectListTruncateContext(display, maxWidth, columnWidth, item, isSelected))
            : TextUtils.TruncateToWidth(display, maxWidth, "");
        return TextUtils.TruncateToWidth(truncated, maxWidth, "");
    }

    private static string DisplayValue(SelectItem item) => item.Label.Length > 0 ? item.Label : item.Value;

    private void NotifySelectionChange()
    {
        if (GetSelectedItem() is { } item) OnSelectionChange?.Invoke(item);
    }

    public SelectItem? GetSelectedItem() => _selectedIndex >= 0 && _selectedIndex < _filtered.Count ? _filtered[_selectedIndex] : null;
}

public sealed class SettingItem
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public string? Description { get; init; }
    public required string CurrentValue { get; set; }

    /// <summary>When set, Enter/Space cycles through these values.</summary>
    public IReadOnlyList<string>? Values { get; init; }

    /// <summary>When set, Enter opens this submenu. done(selectedValue, navigateTo).</summary>
    public Func<string, Action<string?, string?>, IComponent>? Submenu { get; init; }
}

public sealed class SettingsListTheme
{
    public required Func<string, bool, string> Label { get; init; }
    public required Func<string, bool, string> Value { get; init; }
    public required Func<string, string> Description { get; init; }
    public required string Cursor { get; init; }
    public required Func<string, string> Hint { get; init; }
}

/// <summary>Settings list with value cycling, submenus and optional search. Port of pi-tui SettingsList.</summary>
public sealed class SettingsList : IInputComponent
{
    private readonly List<SettingItem> _items;
    private List<SettingItem> _filtered;
    private readonly SettingsListTheme _theme;
    private int _selectedIndex;
    private readonly int _maxVisible;
    private readonly Action<string, string> _onChange;
    private readonly Action _onCancel;
    private readonly Input? _searchInput;
    private readonly bool _searchEnabled;
    private IComponent? _submenu;
    private int? _submenuItemIndex;
    private string? _navigateAfterClose;

    public SettingsList(List<SettingItem> items, int maxVisible, SettingsListTheme theme, Action<string, string> onChange, Action onCancel, bool enableSearch = false)
    {
        _items = items;
        _filtered = items;
        _maxVisible = maxVisible;
        _theme = theme;
        _onChange = onChange;
        _onCancel = onCancel;
        _searchEnabled = enableSearch;
        if (enableSearch) _searchInput = new Input();
    }

    public void UpdateValue(string id, string newValue)
    {
        if (_items.FirstOrDefault(i => i.Id == id) is { } item) item.CurrentValue = newValue;
    }

    public void SelectItem(string id)
    {
        var list = _searchEnabled ? _filtered : _items;
        var index = list.FindIndex(i => i.Id == id);
        if (index != -1) _selectedIndex = index;
    }

    public void Invalidate() => _submenu?.Invalidate();

    public List<string> Render(int width) => _submenu is not null ? _submenu.Render(width) : RenderMainList(width);

    private List<SettingItem> DisplayItems => _searchEnabled ? _filtered : _items;

    private List<string> RenderMainList(int width)
    {
        var lines = new List<string>();
        if (_searchEnabled && _searchInput is not null)
        {
            lines.AddRange(_searchInput.Render(width));
            lines.Add("");
        }

        if (_items.Count == 0)
        {
            lines.Add(_theme.Hint("  No settings available"));
            if (_searchEnabled) AddHintLine(lines, width);
            return lines;
        }

        var display = DisplayItems;
        if (display.Count == 0)
        {
            lines.Add(TextUtils.TruncateToWidth(_theme.Hint("  No matching settings"), width));
            AddHintLine(lines, width);
            return lines;
        }

        var (start, end) = GetVisibleRange(display);
        var maxLabelWidth = Math.Min(36, _items.Max(i => TextUtils.VisibleWidth(i.Label)));
        for (var i = start; i < end; i++)
        {
            var item = display[i];
            var selected = i == _selectedIndex;
            var prefix = selected ? _theme.Cursor : "  ";
            var prefixWidth = TextUtils.VisibleWidth(prefix);
            var labelPadded = item.Label + new string(' ', Math.Max(0, maxLabelWidth - TextUtils.VisibleWidth(item.Label)));
            var labelText = _theme.Label(labelPadded, selected);
            const string separator = "  ";
            var valueMaxWidth = width - (prefixWidth + maxLabelWidth + separator.Length) - 2;
            var valueText = _theme.Value(TextUtils.TruncateToWidth(item.CurrentValue, valueMaxWidth, ""), selected);
            lines.Add(TextUtils.TruncateToWidth(prefix + labelText + separator + valueText, width));
        }

        if (start > 0 || end < display.Count)
        {
            lines.Add(_theme.Hint(TextUtils.TruncateToWidth($"  ({_selectedIndex + 1}/{display.Count})", width - 2, "")));
        }

        if (_selectedIndex < display.Count && display[_selectedIndex].Description is { Length: > 0 } description)
        {
            lines.Add("");
            foreach (var line in TextUtils.WrapTextWithAnsi(description, width - 4)) lines.Add(_theme.Description($"  {line}"));
        }

        AddHintLine(lines, width);
        return lines;
    }

    public void HandleInput(string data)
    {
        if (_submenu is IInputComponent submenuInput)
        {
            submenuInput.HandleInput(data);
            return;
        }
        if (_submenu is not null) return;

        var kb = KeybindingsManager.Global;
        var display = DisplayItems;
        if (kb.Matches(data, "tui.select.up"))
        {
            if (display.Count == 0) return;
            _selectedIndex = _selectedIndex == 0 ? display.Count - 1 : _selectedIndex - 1;
        }
        else if (kb.Matches(data, "tui.select.down"))
        {
            if (display.Count == 0) return;
            _selectedIndex = _selectedIndex == display.Count - 1 ? 0 : _selectedIndex + 1;
        }
        else if (kb.Matches(data, "tui.select.confirm") || (data == " " && (!_searchEnabled || _searchInput?.GetValue().Length == 0)))
        {
            ActivateItem();
        }
        else if (kb.Matches(data, "tui.select.cancel"))
        {
            _onCancel();
        }
        else if (_searchEnabled && _searchInput is not null)
        {
            _searchInput.HandleInput(data);
            _filtered = Fuzzy.Filter(_items, _searchInput.GetValue(), i => i.Label);
            _selectedIndex = 0;
        }
    }

    private (int Start, int End) GetVisibleRange(List<SettingItem> display)
    {
        var start = Math.Max(0, Math.Min(_selectedIndex - _maxVisible / 2, display.Count - _maxVisible));
        return (start, Math.Min(start + _maxVisible, display.Count));
    }

    private void ActivateItem()
    {
        var display = DisplayItems;
        if (_selectedIndex < 0 || _selectedIndex >= display.Count) return;
        var item = display[_selectedIndex];
        if (item.Submenu is not null)
        {
            _submenuItemIndex = _selectedIndex;
            _submenu = item.Submenu(item.CurrentValue, (selected, navigateTo) =>
            {
                if (selected is not null)
                {
                    item.CurrentValue = selected;
                    _onChange(item.Id, selected);
                }
                if (!string.IsNullOrEmpty(navigateTo)) _navigateAfterClose = navigateTo;
                CloseSubmenu();
            });
        }
        else if (item.Values is { Count: > 0 } values)
        {
            var index = values.ToList().IndexOf(item.CurrentValue);
            var next = values[(index + 1) % values.Count];
            item.CurrentValue = next;
            _onChange(item.Id, next);
        }
    }

    private void CloseSubmenu()
    {
        _submenu = null;
        if (_navigateAfterClose is { } id)
        {
            _navigateAfterClose = null;
            _submenuItemIndex = null;
            SelectItem(id);
            ActivateItem();
        }
        else if (_submenuItemIndex is { } index)
        {
            _selectedIndex = index;
            _submenuItemIndex = null;
        }
    }

    private void AddHintLine(List<string> lines, int width)
    {
        lines.Add("");
        lines.Add(TextUtils.TruncateToWidth(_theme.Hint(_searchEnabled
            ? "  Type to search · Enter/Space to change · Esc to cancel"
            : "  Enter/Space to change · Esc to cancel"), width));
    }
}
