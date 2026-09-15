using Iris.Ai;
using Iris.Tui;
using Iris.Tui.Components;

namespace Iris.CodingAgent.Modes.Interactive.Components;

/// <summary>Enable/disable and reorder models for cycling. Port of scoped-models-selector.ts.</summary>
public sealed class ScopedModelsSelectorComponent : Container, IInputComponent, IFocusable
{
    private sealed record ModelItem(string FullId, Model? Model, bool Enabled);

    private static bool IsEnabled(List<string>? enabled, string id) => enabled is null || enabled.Contains(id);

    private static List<string>? Normalize(List<string> result, List<string> allIds) =>
        result.Count == allIds.Count && result.All(allIds.Contains) ? null : result;

    private static List<string>? Toggle(List<string>? enabled, List<string> allIds, string id)
    {
        if (enabled is null) return allIds.Where(m => m != id).ToList();
        var index = enabled.IndexOf(id);
        if (index >= 0) return [.. enabled.Take(index), .. enabled.Skip(index + 1)];
        return Normalize([.. enabled, id], allIds);
    }

    private static List<string>? EnableAll(List<string>? enabled, List<string> allIds, List<string>? targets = null)
    {
        if (enabled is null) return null;
        var result = enabled.ToList();
        foreach (var id in targets ?? allIds)
        {
            if (!result.Contains(id)) result.Add(id);
        }
        return Normalize(result, allIds);
    }

    private static List<string> ClearAll(List<string>? enabled, List<string> allIds, List<string>? targets = null)
    {
        if (enabled is null) return targets is not null ? allIds.Where(id => !targets.Contains(id)).ToList() : [];
        var set = (targets ?? enabled).ToHashSet();
        return enabled.Where(id => !set.Contains(id)).ToList();
    }

    private readonly Dictionary<string, Model> _modelsById = [];
    private List<string> _allIds = [];
    private List<string>? _enabledIds;
    private List<ModelItem> _filteredItems;
    private int _selectedIndex;
    private readonly Input _searchInput = new();
    private readonly Container _listContainer = new();
    private readonly Text _footerText;
    private readonly Action<List<string>?> _onChange;
    private readonly Action<List<string>?> _onPersist;
    private readonly Action _onCancel;
    private const int MaxVisible = 8;
    private bool _isDirty;
    private readonly Text? _refreshStatusText;
    private bool _focused;

    public bool Focused
    {
        get => _focused;
        set
        {
            _focused = value;
            _searchInput.Focused = value;
        }
    }

    public ScopedModelsSelectorComponent(IReadOnlyList<Model> allModels, List<string>? enabledModelIds, string? refreshStatus, Action<List<string>?> onChange, Action<List<string>?> onPersist, Action onCancel)
    {
        _onChange = onChange;
        _onPersist = onPersist;
        _onCancel = onCancel;
        foreach (var model in allModels)
        {
            var fullId = $"{model.Provider}/{model.Id}";
            _modelsById[fullId] = model;
            _allIds.Add(fullId);
        }
        _enabledIds = enabledModelIds?.ToList();
        _filteredItems = BuildItems();

        var theme = ThemeManager.Current;
        AddChild(new DynamicBorder());
        AddChild(new Spacer(1));
        AddChild(new Text(theme.Fg("accent", theme.Bold("Model Configuration")), 0, 0));
        AddChild(new Text(theme.Fg("muted", $"Session-only. {KeyHints.KeyDisplayText("app.models.save")} to save to settings."), 0, 0));
        AddChild(new Spacer(1));
        AddChild(_searchInput);
        AddChild(new Spacer(1));
        AddChild(_listContainer);
        AddChild(new Spacer(1));
        if (!string.IsNullOrEmpty(refreshStatus))
        {
            _refreshStatusText = new Text(theme.Fg("muted", $"  {refreshStatus}"), 0, 0);
            AddChild(_refreshStatusText);
        }
        _footerText = new Text(FooterText(), 0, 0);
        AddChild(_footerText);
        AddChild(new DynamicBorder());
        UpdateList();
    }

    public void UpdateModels(IReadOnlyList<Model> models, List<string>? enabledModelIds = null, bool updateEnabled = false)
    {
        var selectedId = _selectedIndex < _filteredItems.Count ? _filteredItems[_selectedIndex].FullId : null;
        if (updateEnabled) _enabledIds = enabledModelIds?.ToList();
        _modelsById.Clear();
        _allIds = [];
        foreach (var model in models)
        {
            var fullId = $"{model.Provider}/{model.Id}";
            _modelsById[fullId] = model;
            _allIds.Add(fullId);
        }
        Refresh();
        var index = selectedId is not null ? _filteredItems.FindIndex(i => i.FullId == selectedId) : -1;
        if (index >= 0)
        {
            _selectedIndex = index;
            UpdateList();
        }
    }

    public void SetRefreshStatus(string message, string kind) => _refreshStatusText?.SetText(ThemeManager.Current.Fg(kind, $"  {message}"));

    private List<ModelItem> BuildItems()
    {
        var sorted = _enabledIds is null ? _allIds : [.. _enabledIds, .. _allIds.Where(id => !_enabledIds.Contains(id))];
        return sorted.Select(id => new ModelItem(id, _modelsById.GetValueOrDefault(id), IsEnabled(_enabledIds, id))).ToList();
    }

    private string FooterText()
    {
        var theme = ThemeManager.Current;
        var enabledCount = _enabledIds?.Count(_modelsById.ContainsKey) ?? _allIds.Count;
        var unavailable = _enabledIds?.Count(id => !_modelsById.ContainsKey(id)) ?? 0;
        var countText = _enabledIds is null ? "all enabled" : $"{enabledCount}/{_allIds.Count} enabled{(unavailable > 0 ? $" · {unavailable} unavailable" : "")}";
        var parts = new[]
        {
            $"{KeyHints.KeyDisplayText("tui.select.confirm")} toggle",
            $"{KeyHints.KeyDisplayText("app.models.enableAll")} all",
            $"{KeyHints.KeyDisplayText("app.models.clearAll")} clear",
            $"{KeyHints.KeyDisplayText("app.models.toggleProvider")} provider",
            $"{KeyHints.KeyDisplayText("app.models.reorderUp")}/{KeyHints.KeyDisplayText("app.models.reorderDown")} reorder",
            $"{KeyHints.KeyDisplayText("app.models.save")} save",
            countText,
        };
        return _isDirty ? theme.Fg("dim", $"  {string.Join(" · ", parts)} ") + theme.Fg("warning", "(unsaved)") : theme.Fg("dim", $"  {string.Join(" · ", parts)}");
    }

    private void Refresh()
    {
        var query = _searchInput.GetValue();
        var items = BuildItems();
        _filteredItems = query.Length > 0
            ? Fuzzy.Filter(items, query, i => i.Model is { } m ? $"{m.Id} {m.Provider} {m.Provider}/{m.Id} {m.Provider} {m.Id}{(string.IsNullOrEmpty(m.Name) ? "" : " " + m.Name)}" : i.FullId)
            : items;
        _selectedIndex = Math.Min(_selectedIndex, Math.Max(0, _filteredItems.Count - 1));
        UpdateList();
        _footerText.SetText(FooterText());
    }

    private void NotifyChange() => _onChange(_enabledIds?.ToList());

    private void UpdateList()
    {
        var theme = ThemeManager.Current;
        _listContainer.Clear();
        if (_filteredItems.Count == 0)
        {
            _listContainer.AddChild(new Text(theme.Fg("muted", "  No matching models"), 0, 0));
            return;
        }
        var start = Math.Max(0, Math.Min(_selectedIndex - MaxVisible / 2, _filteredItems.Count - MaxVisible));
        var end = Math.Min(start + MaxVisible, _filteredItems.Count);
        for (var i = start; i < end; i++)
        {
            var item = _filteredItems[i];
            var selected = i == _selectedIndex;
            var prefix = selected ? theme.Fg("accent", "→ ") : "  ";
            var id = item.Model?.Id ?? item.FullId;
            var styledId = item.Model is not null ? id : theme.Strikethrough(id);
            var modelText = selected ? theme.Fg("accent", styledId) : styledId;
            var badge = theme.Fg("muted", item.Model is not null ? $" [{item.Model.Provider}]" : " [unavailable]");
            var status = item.Model is not null && item.Enabled ? theme.Fg("accent", "✓ ") : "  ";
            _listContainer.AddChild(new Text($"{prefix}{status}{modelText}{badge}", 0, 0));
        }
        if (start > 0 || end < _filteredItems.Count) _listContainer.AddChild(new Text(theme.Fg("muted", $"  ({_selectedIndex + 1}/{_filteredItems.Count})"), 0, 0));
        var sel = _filteredItems[_selectedIndex];
        _listContainer.AddChild(new Spacer(1));
        _listContainer.AddChild(new Text(theme.Fg("muted", $"  {(sel.Model is not null ? $"Model Name: {sel.Model.Name}" : "Model unavailable")}"), 0, 0));
    }

    public void HandleInput(string data)
    {
        var kb = KeybindingsManager.Global;
        if (kb.Matches(data, "tui.select.up"))
        {
            if (_filteredItems.Count == 0) return;
            _selectedIndex = _selectedIndex == 0 ? _filteredItems.Count - 1 : _selectedIndex - 1;
            UpdateList();
            return;
        }
        if (kb.Matches(data, "tui.select.down"))
        {
            if (_filteredItems.Count == 0) return;
            _selectedIndex = _selectedIndex == _filteredItems.Count - 1 ? 0 : _selectedIndex + 1;
            UpdateList();
            return;
        }

        var up = kb.Matches(data, "app.models.reorderUp");
        var down = kb.Matches(data, "app.models.reorderDown");
        if (up || down)
        {
            if (_enabledIds is null || _selectedIndex >= _filteredItems.Count) return;
            var item = _filteredItems[_selectedIndex];
            if (!IsEnabled(_enabledIds, item.FullId)) return;
            var delta = up ? -1 : 1;
            var current = _enabledIds.IndexOf(item.FullId);
            var next = current + delta;
            if (current < 0 || next < 0 || next >= _enabledIds.Count) return;
            (_enabledIds[current], _enabledIds[next]) = (_enabledIds[next], _enabledIds[current]);
            _isDirty = true;
            _selectedIndex += delta;
            Refresh();
            NotifyChange();
            return;
        }

        if (kb.Matches(data, "tui.select.confirm"))
        {
            if (_selectedIndex >= _filteredItems.Count) return;
            _enabledIds = Toggle(_enabledIds, _allIds, _filteredItems[_selectedIndex].FullId);
            _isDirty = true;
            Refresh();
            NotifyChange();
            return;
        }
        if (kb.Matches(data, "app.models.enableAll"))
        {
            var targets = _searchInput.GetValue().Length > 0 ? _filteredItems.Select(i => i.FullId).ToList() : null;
            _enabledIds = EnableAll(_enabledIds, _allIds, targets);
            _isDirty = true;
            Refresh();
            NotifyChange();
            return;
        }
        if (kb.Matches(data, "app.models.clearAll"))
        {
            var targets = _searchInput.GetValue().Length > 0 ? _filteredItems.Select(i => i.FullId).ToList() : null;
            _enabledIds = ClearAll(_enabledIds, _allIds, targets);
            _isDirty = true;
            Refresh();
            NotifyChange();
            return;
        }
        if (kb.Matches(data, "app.models.toggleProvider"))
        {
            if (_selectedIndex >= _filteredItems.Count || _filteredItems[_selectedIndex].Model is not { } model) return;
            var providerIds = _allIds.Where(id => _modelsById[id].Provider == model.Provider).ToList();
            var allEnabled = providerIds.All(id => IsEnabled(_enabledIds, id));
            _enabledIds = allEnabled ? ClearAll(_enabledIds, _allIds, providerIds) : EnableAll(_enabledIds, _allIds, providerIds);
            _isDirty = true;
            Refresh();
            NotifyChange();
            return;
        }
        if (kb.Matches(data, "app.models.save"))
        {
            _onPersist(_enabledIds?.ToList());
            _isDirty = false;
            _footerText.SetText(FooterText());
            return;
        }
        if (Keys.Matches(data, "ctrl+c"))
        {
            if (_searchInput.GetValue().Length > 0)
            {
                _searchInput.SetValue("");
                Refresh();
            }
            else
            {
                _onCancel();
            }
            return;
        }
        if (Keys.Matches(data, "escape"))
        {
            _onCancel();
            return;
        }
        _searchInput.HandleInput(data);
        Refresh();
    }
}
