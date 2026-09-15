using System.Diagnostics;
using Iris.Ai;
using Iris.Ai.Models;
using Iris.CodingAgent.Core;
using Iris.CodingAgent.Utils;
using Iris.Tui;
using Iris.Tui.Components;

namespace Iris.CodingAgent.Modes.Interactive.Components;

/// <summary>Opens content in an external editor. Port of external-editor.ts.</summary>
public static class ExternalEditor
{
    public static async Task<string?> EditAsync(string command, string content)
    {
        var directory = Directory.CreateTempSubdirectory("pi-editor-").FullName;
        var filePath = Path.Combine(directory, "prompt.md");
        try
        {
            await File.WriteAllTextAsync(filePath, content);
            var parts = command.Split(' ');
            Console.Out.Write($"Launching external editor: {command}\nPi will resume when the editor exits.\n");
            Console.Out.Flush();
            ProcessStartInfo psi;
            if (OperatingSystem.IsWindows())
            {
                psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false };
                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add(string.Join(" ", parts.Append(ShellUtils.QuoteWindowsArgument(filePath))));
            }
            else
            {
                psi = new ProcessStartInfo(parts[0]) { UseShellExecute = false };
                foreach (var arg in parts.Skip(1)) psi.ArgumentList.Add(arg);
                psi.ArgumentList.Add(filePath);
            }
            int? exitCode;
            try
            {
                using var process = Process.Start(psi);
                if (process is null) return null;
                await process.WaitForExitAsync();
                exitCode = process.ExitCode;
            }
            catch
            {
                exitCode = null;
            }
            if (exitCode != 0) return null;
            var result = TextHelpers.StripBom(await File.ReadAllTextAsync(filePath));
            return result.EndsWith('\n') ? result[..^1] : result;
        }
        finally
        {
            try
            {
                Directory.Delete(directory, true);
            }
            catch
            {
                // Best effort.
            }
        }
    }
}

/// <summary>Shares concurrent catalog refreshes per runtime. Port of model-catalog-refresh.ts.</summary>
public static class ModelCatalogRefresh
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ModelRuntime, Active> ActiveByRuntime = new();

    private sealed class Active
    {
        public required CancellationTokenSource Controller;
        public required Task<ModelsRefreshResult> Task;
        public int Waiters;
    }

    public static async Task<ModelsRefreshResult> RefreshAsync(ModelRuntime runtime, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Active active;
        lock (ActiveByRuntime)
        {
            if (!ActiveByRuntime.TryGetValue(runtime, out active!))
            {
                var controller = new CancellationTokenSource();
                Active? created = null;
                var task = RunAsync();
                created = new Active { Controller = controller, Task = task };
                active = created;
                ActiveByRuntime.AddOrUpdate(runtime, active);

                async Task<ModelsRefreshResult> RunAsync()
                {
                    try
                    {
                        return await runtime.RefreshAsync(new ModelsRefreshOptions { CancellationToken = controller.Token });
                    }
                    finally
                    {
                        lock (ActiveByRuntime)
                        {
                            if (ActiveByRuntime.TryGetValue(runtime, out var current) && ReferenceEquals(current, created)) ActiveByRuntime.Remove(runtime);
                        }
                    }
                }
            }
            active.Waiters++;
        }
        try
        {
            return await active.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            lock (ActiveByRuntime)
            {
                active.Waiters--;
                if (active.Waiters == 0 && ActiveByRuntime.TryGetValue(runtime, out var current) && ReferenceEquals(current, active)) active.Controller.Cancel();
            }
        }
    }
}

/// <summary>Thinking level selector. Port of thinking-selector.ts.</summary>
public sealed class ThinkingSelectorComponent : Container, IInputComponent, IFocusable
{
    private static readonly SelectListLayoutOptions Layout = new() { MinPrimaryColumnWidth = 12, MaxPrimaryColumnWidth = 32 };

    private static readonly Dictionary<ThinkingLevel, string> LevelDescriptions = new()
    {
        [ThinkingLevel.Off] = "No reasoning",
        [ThinkingLevel.Minimal] = "Very brief reasoning (~1k tokens)",
        [ThinkingLevel.Low] = "Light reasoning (~2k tokens)",
        [ThinkingLevel.Medium] = "Moderate reasoning (~8k tokens)",
        [ThinkingLevel.High] = "Deep reasoning (~16k tokens)",
        [ThinkingLevel.XHigh] = "Extra-high reasoning (~32k tokens)",
        [ThinkingLevel.Max] = "Maximum reasoning",
    };

    private readonly Input _searchInput = new();
    private SelectList _selectList;
    private readonly int _selectListChildIndex;
    private readonly List<SelectItem> _allItems;
    private readonly Action<ThinkingLevel> _onSelect;
    private readonly Action _onCancel;
    private readonly Action<ThinkingLevel>? _onSelectAsDefault;
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

    public ThinkingSelectorComponent(ThinkingLevel currentLevel, IReadOnlyList<ThinkingLevel> availableLevels, Action<ThinkingLevel> onSelect, Action onCancel, Action<ThinkingLevel>? onSelectAsDefault = null, ThinkingLevel? defaultThinkingLevel = null)
    {
        _onSelect = onSelect;
        _onCancel = onCancel;
        _onSelectAsDefault = onSelectAsDefault;
        _allItems = availableLevels.Select(level => new SelectItem(level.ToWire(), $"{(level == currentLevel ? "✓ " : "  ")}{level.ToWire()}",
            level == defaultThinkingLevel ? $"{LevelDescriptions[level]} · default" : LevelDescriptions[level])).ToList();

        var theme = ThemeManager.Current;
        AddChild(new DynamicBorder());
        AddChild(new Spacer(1));
        AddChild(new Text("Thinking Level", 0, 0));
        AddChild(new Spacer(1));
        AddChild(new Text($"{KeyHints.KeyDisplayText("app.thinking.cycle")} cycles thinking levels in-session", 0, 0));
        AddChild(new Spacer(1));
        _searchInput.OnSubmit = _ => _selectList.HandleInput("\r");
        AddChild(_searchInput);
        AddChild(new Spacer(1));
        _selectList = BuildSelectList(_allItems, currentLevel.ToWire());
        _selectListChildIndex = Children.Count;
        AddChild(_selectList);
        AddChild(new Spacer(1));
        AddChild(new Text(theme.Fg("dim", $"  {KeyHints.KeyDisplayText("tui.select.confirm")} to select · {KeyHints.KeyDisplayText("app.thinking.save")} to set as default · {KeyHints.KeyDisplayText("tui.select.cancel")} to cancel"), 0, 0));
        AddChild(new DynamicBorder());
    }

    private SelectList BuildSelectList(List<SelectItem> items, string? preselect)
    {
        var list = new SelectList(items, Math.Max(1, items.Count), ThemeManager.GetSelectListTheme(), Layout);
        var index = items.FindIndex(i => i.Value == preselect);
        if (index != -1) list.SetSelectedIndex(index);
        list.OnSelect = item => _onSelect(ThinkingLevelNames.ParseOrOff(item.Value));
        list.OnCancel = _onCancel;
        return list;
    }

    private void ApplyFilter(string query)
    {
        var filtered = query.Length > 0 ? Fuzzy.Filter(_allItems, query, i => $"{i.Value} {i.Description ?? ""}") : _allItems;
        var newList = BuildSelectList(filtered, _selectList.GetSelectedItem()?.Value);
        Children[_selectListChildIndex] = newList;
        _selectList = newList;
    }

    public void HandleInput(string keyData)
    {
        var kb = KeybindingsManager.Global;
        if (kb.Matches(keyData, "app.thinking.save") && _onSelectAsDefault is not null)
        {
            if (_selectList.GetSelectedItem() is { } item) _onSelectAsDefault(ThinkingLevelNames.ParseOrOff(item.Value));
            return;
        }
        if (kb.Matches(keyData, "tui.select.up") || kb.Matches(keyData, "tui.select.down") || kb.Matches(keyData, "tui.select.confirm") || kb.Matches(keyData, "tui.select.cancel"))
        {
            _selectList.HandleInput(keyData);
            return;
        }
        _searchInput.HandleInput(keyData);
        ApplyFilter(_searchInput.GetValue());
    }

    public SelectList GetSelectList() => _selectList;
}

internal static class ThinkingLevelNames
{
    public static ThinkingLevel ParseOrOff(string value) => value switch
    {
        "minimal" => ThinkingLevel.Minimal,
        "low" => ThinkingLevel.Low,
        "medium" => ThinkingLevel.Medium,
        "high" => ThinkingLevel.High,
        "xhigh" => ThinkingLevel.XHigh,
        "max" => ThinkingLevel.Max,
        _ => ThinkingLevel.Off,
    };
}

/// <summary>Fork-from-message selector. Port of user-message-selector.ts.</summary>
public sealed class UserMessageSelectorComponent : Container
{
    public sealed class UserMessageList(List<(string Id, string Text)> messages, string? initialSelectedId) : IInputComponent
    {
        private int _selectedIndex = initialSelectedId is not null && messages.FindIndex(m => m.Id == initialSelectedId) is >= 0 and var i ? i : Math.Max(0, messages.Count - 1);
        private const int MaxVisible = 10;

        public Action<string>? OnSelect { get; set; }
        public Action? OnCancel { get; set; }

        public void Invalidate()
        {
        }

        public List<string> Render(int width)
        {
            var theme = ThemeManager.Current;
            var lines = new List<string>();
            if (messages.Count == 0)
            {
                lines.Add(theme.Fg("muted", "  No user messages found"));
                return lines;
            }
            var start = Math.Max(0, Math.Min(_selectedIndex - MaxVisible / 2, messages.Count - MaxVisible));
            var end = Math.Min(start + MaxVisible, messages.Count);
            for (var i = start; i < end; i++)
            {
                var selected = i == _selectedIndex;
                var normalized = messages[i].Text.Replace('\n', ' ').Trim();
                var cursor = selected ? theme.Fg("accent", "› ") : "  ";
                var truncated = TextUtils.TruncateToWidth(normalized, width - 2);
                lines.Add(cursor + (selected ? theme.Bold(truncated) : truncated));
                lines.Add(theme.Fg("muted", $"  Message {i + 1} of {messages.Count}"));
                lines.Add("");
            }
            if (start > 0 || end < messages.Count) lines.Add(theme.Fg("muted", $"  ({_selectedIndex + 1}/{messages.Count})"));
            return lines;
        }

        public void HandleInput(string keyData)
        {
            var kb = KeybindingsManager.Global;
            if (kb.Matches(keyData, "tui.select.up")) _selectedIndex = _selectedIndex == 0 ? messages.Count - 1 : _selectedIndex - 1;
            else if (kb.Matches(keyData, "tui.select.down")) _selectedIndex = _selectedIndex == messages.Count - 1 ? 0 : _selectedIndex + 1;
            else if (kb.Matches(keyData, "tui.select.confirm"))
            {
                if (_selectedIndex >= 0 && _selectedIndex < messages.Count) OnSelect?.Invoke(messages[_selectedIndex].Id);
            }
            else if (kb.Matches(keyData, "tui.select.cancel")) OnCancel?.Invoke();
        }
    }

    private readonly UserMessageList _messageList;

    public UserMessageSelectorComponent(List<(string Id, string Text)> messages, Action<string> onSelect, Action onCancel, string? initialSelectedId = null)
    {
        var theme = ThemeManager.Current;
        AddChild(new Spacer(1));
        AddChild(new Text(theme.Bold("Fork from Message"), 1, 0));
        AddChild(new Text(theme.Fg("muted", "Select a user message to copy the active path up to that point into a new session"), 1, 0));
        AddChild(new Spacer(1));
        AddChild(new DynamicBorder());
        AddChild(new Spacer(1));
        _messageList = new UserMessageList(messages, initialSelectedId) { OnSelect = onSelect, OnCancel = onCancel };
        AddChild(_messageList);
        AddChild(new Spacer(1));
        AddChild(new DynamicBorder());
        if (messages.Count == 0) UiDispatcher.Current?.SetTimeout(onCancel, 100);
    }

    public UserMessageList GetMessageList() => _messageList;
}

/// <summary>Generic option selector dialog. Port of extension-selector.ts.</summary>
public sealed class ExtensionSelectorComponent : Container, IInputComponent, IDisposable
{
    private readonly List<string> _options;
    private int _selectedIndex;
    private readonly Container _listContainer = new();
    private readonly Action<string> _onSelect;
    private readonly Action _onCancel;
    private readonly Text _titleText;
    private CountdownTimer? _countdown;
    private readonly Action? _onToggleToolsExpanded;

    public ExtensionSelectorComponent(string title, List<string> options, Action<string> onSelect, Action onCancel, TuiBase? tui = null, int? timeoutMs = null, Action? onToggleToolsExpanded = null)
    {
        _options = options;
        _onSelect = onSelect;
        _onCancel = onCancel;
        _onToggleToolsExpanded = onToggleToolsExpanded;
        var theme = ThemeManager.Current;
        AddChild(new DynamicBorder());
        AddChild(new Spacer(1));
        _titleText = new Text(theme.Fg("accent", theme.Bold(title)), 1, 0);
        AddChild(_titleText);
        AddChild(new Spacer(1));
        if (timeoutMs is > 0 && tui is not null)
        {
            _countdown = new CountdownTimer(timeoutMs.Value, tui, s => _titleText.SetText(ThemeManager.Current.Fg("accent", ThemeManager.Current.Bold($"{title} ({s}s)"))), () => _onCancel());
        }
        AddChild(_listContainer);
        AddChild(new Spacer(1));
        AddChild(new Text(KeyHints.RawKeyHint("↑↓", "navigate") + "  " + KeyHints.KeyHint("tui.select.confirm", "select") + "  " + KeyHints.KeyHint("tui.select.cancel", "cancel"), 1, 0));
        AddChild(new Spacer(1));
        AddChild(new DynamicBorder());
        UpdateList();
    }

    private void UpdateList()
    {
        var theme = ThemeManager.Current;
        _listContainer.Clear();
        for (var i = 0; i < _options.Count; i++)
        {
            var text = i == _selectedIndex ? theme.Fg("accent", "→ ") + theme.Fg("accent", _options[i]) : $"  {theme.Fg("text", _options[i])}";
            _listContainer.AddChild(new Text(text, 1, 0));
        }
    }

    public void HandleInput(string keyData)
    {
        var kb = KeybindingsManager.Global;
        if (kb.Matches(keyData, "app.tools.expand")) _onToggleToolsExpanded?.Invoke();
        else if (kb.Matches(keyData, "tui.select.up") || keyData == "k")
        {
            _selectedIndex = Math.Max(0, _selectedIndex - 1);
            UpdateList();
        }
        else if (kb.Matches(keyData, "tui.select.down") || keyData == "j")
        {
            _selectedIndex = Math.Min(_options.Count - 1, _selectedIndex + 1);
            UpdateList();
        }
        else if (kb.Matches(keyData, "tui.select.confirm") || keyData == "\n")
        {
            if (_selectedIndex >= 0 && _selectedIndex < _options.Count) _onSelect(_options[_selectedIndex]);
        }
        else if (kb.Matches(keyData, "tui.select.cancel")) _onCancel();
    }

    public void Dispose()
    {
        _countdown?.Dispose();
        _countdown = null;
    }
}

/// <summary>Single-line input dialog. Port of extension-input.ts.</summary>
public sealed class ExtensionInputComponent : Container, IInputComponent, IFocusable, IDisposable
{
    private readonly Input _input = new();
    private readonly Action<string> _onSubmit;
    private readonly Action _onCancel;
    private CountdownTimer? _countdown;
    private bool _focused;

    public bool Focused
    {
        get => _focused;
        set
        {
            _focused = value;
            _input.Focused = value;
        }
    }

    public ExtensionInputComponent(string title, Action<string> onSubmit, Action onCancel, TuiBase? tui = null, int? timeoutMs = null)
    {
        _onSubmit = onSubmit;
        _onCancel = onCancel;
        var theme = ThemeManager.Current;
        AddChild(new DynamicBorder());
        AddChild(new Spacer(1));
        var titleText = new Text(theme.Fg("accent", title), 1, 0);
        AddChild(titleText);
        AddChild(new Spacer(1));
        if (timeoutMs is > 0 && tui is not null)
        {
            _countdown = new CountdownTimer(timeoutMs.Value, tui, s => titleText.SetText(ThemeManager.Current.Fg("accent", $"{title} ({s}s)")), () => _onCancel());
        }
        AddChild(_input);
        AddChild(new Spacer(1));
        AddChild(new Text($"{KeyHints.KeyHint("tui.select.confirm", "submit")}  {KeyHints.KeyHint("tui.select.cancel", "cancel")}", 1, 0));
        AddChild(new Spacer(1));
        AddChild(new DynamicBorder());
    }

    public void HandleInput(string keyData)
    {
        var kb = KeybindingsManager.Global;
        if (kb.Matches(keyData, "tui.select.confirm") || keyData == "\n") _onSubmit(_input.GetValue());
        else if (kb.Matches(keyData, "tui.select.cancel")) _onCancel();
        else _input.HandleInput(keyData);
    }

    public void Dispose()
    {
        _countdown?.Dispose();
        _countdown = null;
    }
}

/// <summary>Multi-line editor dialog. Port of extension-editor.ts.</summary>
public sealed class ExtensionEditorComponent : Container, IInputComponent, IFocusable
{
    private readonly Editor _editor;
    private readonly Action _onCancel;
    private readonly TuiBase _tui;
    private readonly KeybindingsManager _keybindings;
    private readonly string _externalEditorCommand;
    private bool _focused;

    public bool Focused
    {
        get => _focused;
        set
        {
            _focused = value;
            _editor.Focused = value;
        }
    }

    public ExtensionEditorComponent(TuiBase tui, KeybindingsManager keybindings, string title, string? prefill, Action<string> onSubmit, Action onCancel, EditorOptions? options = null, string? externalEditorCommand = null)
    {
        _tui = tui;
        _keybindings = keybindings;
        _onCancel = onCancel;
        _externalEditorCommand = !string.IsNullOrEmpty(externalEditorCommand) ? externalEditorCommand
            : Environment.GetEnvironmentVariable("VISUAL") is { Length: > 0 } visual ? visual
            : Environment.GetEnvironmentVariable("EDITOR") is { Length: > 0 } editor ? editor
            : OperatingSystem.IsWindows() ? "notepad" : "nano";
        var theme = ThemeManager.Current;
        AddChild(new DynamicBorder());
        AddChild(new Spacer(1));
        AddChild(new Text(theme.Fg("accent", title), 1, 0));
        AddChild(new Spacer(1));
        _editor = new Editor(tui, ThemeManager.GetEditorTheme(), options);
        if (!string.IsNullOrEmpty(prefill)) _editor.SetText(prefill);
        _editor.OnSubmit = onSubmit;
        AddChild(_editor);
        AddChild(new Spacer(1));
        AddChild(new Text(KeyHints.KeyHint("tui.select.confirm", "submit") + "  " + KeyHints.KeyHint("tui.input.newLine", "newline") + "  " + KeyHints.KeyHint("tui.select.cancel", "cancel") + $"  {KeyHints.KeyHint("app.editor.external", "external editor")}", 1, 0));
        AddChild(new Spacer(1));
        AddChild(new DynamicBorder());
    }

    public void HandleInput(string keyData)
    {
        if (KeybindingsManager.Global.Matches(keyData, "tui.select.cancel"))
        {
            _onCancel();
            return;
        }
        if (_keybindings.Matches(keyData, "app.editor.external"))
        {
            _ = OpenExternalEditorAsync();
            return;
        }
        _editor.HandleInput(keyData);
    }

    private async Task OpenExternalEditorAsync()
    {
        var content = _editor.GetText();
        _tui.Stop();
        try
        {
            if (await ExternalEditor.EditAsync(_externalEditorCommand, content) is { } result) _editor.SetText(result);
        }
        finally
        {
            _tui.Start();
            _tui.RequestRender(true);
        }
    }
}

/// <summary>Model selector with search and scope toggle. Port of model-selector.ts.</summary>
public sealed class ModelSelectorComponent : Container, IInputComponent, IFocusable, IDisposable
{
    private sealed record ModelItem(string Provider, string Id, Model Model);

    private readonly Input _searchInput = new();
    private readonly Container _listContainer = new();
    private List<ModelItem> _allModels = [];
    private List<ModelItem> _scopedModelItems = [];
    private List<ModelItem> _activeModels = [];
    private List<ModelItem> _filteredModels = [];
    private int _selectedIndex;
    private readonly Model? _currentModel;
    private readonly ModelRuntime _modelRuntime;
    private readonly Action<Model> _onSelect;
    private readonly Action<Model>? _onSelectAsDefault;
    private readonly Action _onCancel;
    private string? _errorMessage;
    private string _refreshStatusMessage = "Refreshing model catalogs…";
    private bool _refreshStatusSuccess;
    private readonly TuiBase _tui;
    private IReadOnlyList<ScopedModel> _scopedModels;
    private readonly (string Provider, string Id)? _defaultModel;
    private string _scope;
    private readonly Text? _scopeText;
    private readonly Text? _scopeHintText;
    private readonly CancellationTokenSource _refreshCts = new();
    private bool _closed;
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

    public ModelSelectorComponent(TuiBase tui, Model? currentModel, ModelRuntime modelRuntime, IReadOnlyList<ScopedModel> scopedModels, Action<Model> onSelect, Action onCancel, string? initialSearchInput = null, Action<Model>? onSelectAsDefault = null, (string Provider, string Id)? defaultModel = null)
    {
        _tui = tui;
        _currentModel = currentModel;
        _modelRuntime = modelRuntime;
        _scopedModels = scopedModels;
        _defaultModel = defaultModel;
        _scope = scopedModels.Count > 0 ? "scoped" : "all";
        _onSelect = onSelect;
        _onSelectAsDefault = onSelectAsDefault;
        _onCancel = onCancel;
        var theme = ThemeManager.Current;

        AddChild(new DynamicBorder());
        AddChild(new Spacer(1));
        if (scopedModels.Count > 0)
        {
            _scopeText = new Text(GetScopeText(), 0, 0);
            AddChild(_scopeText);
            _scopeHintText = new Text(GetScopeHintText(), 0, 0);
            AddChild(_scopeHintText);
        }
        else
        {
            AddChild(new Text(theme.Fg("warning", "Only showing models from configured providers. Use /login to add providers."), 0, 0));
        }
        AddChild(new Spacer(1));

        if (!string.IsNullOrEmpty(initialSearchInput)) _searchInput.SetValue(initialSearchInput);
        _searchInput.OnSubmit = _ =>
        {
            if (_selectedIndex < _filteredModels.Count) HandleSelect(_filteredModels[_selectedIndex].Model);
        };
        AddChild(_searchInput);
        AddChild(new Spacer(1));
        AddChild(_listContainer);
        AddChild(new Spacer(1));
        if (_onSelectAsDefault is not null)
        {
            AddChild(new Text(theme.Fg("dim", $"  {KeyHints.KeyDisplayText("tui.select.confirm")} to select · {KeyHints.KeyDisplayText("app.models.save")} to set as default · {KeyHints.KeyDisplayText("tui.select.cancel")} to cancel"), 0, 0));
        }
        AddChild(new DynamicBorder());

        LoadModelsFromSnapshot();
        if (!string.IsNullOrEmpty(initialSearchInput)) FilterModels(initialSearchInput);
        else UpdateList();
        _tui.RequestRender();
        _ = RefreshModelsAsync();
    }

    private void LoadModelsFromSnapshot()
    {
        _allModels = SortModels(_modelRuntime.AvailableSnapshot.Select(m => new ModelItem(m.Provider, m.Id, m)).ToList());
        _scopedModels = _scopedModels.Select(s => _modelRuntime.GetModel(s.Model.Provider, s.Model.Id) is { } refreshed ? s with { Model = refreshed } : s).ToList();
        _scopedModelItems = _scopedModels.Select(s => new ModelItem(s.Model.Provider, s.Model.Id, s.Model)).ToList();
        _activeModels = _scope == "scoped" ? _scopedModelItems : _allModels;
        _filteredModels = _activeModels;
        var currentIndex = _filteredModels.FindIndex(i => ModelUtils.ModelsAreEqual(_currentModel, i.Model));
        _selectedIndex = currentIndex >= 0 ? currentIndex : Math.Min(_selectedIndex, Math.Max(0, _filteredModels.Count - 1));
    }

    private async Task RefreshModelsAsync()
    {
        var timedOut = false;
        using var timeout = new Timer(_ =>
        {
            timedOut = true;
            _refreshCts.Cancel();
        }, null, 15000, Timeout.Infinite);
        try
        {
            var result = await ModelCatalogRefresh.RefreshAsync(_modelRuntime, _refreshCts.Token);
            if (_closed) return;
            _refreshStatusMessage = "";
            if (result.Aborted && timedOut) _errorMessage = "Model refresh timed out; showing cached models.";
            else if (result.Errors.Count == 1) _errorMessage = $"Could not refresh {result.Errors.Keys.First()}; showing cached models.";
            else if (result.Errors.Count > 1) _errorMessage = $"Could not refresh {result.Errors.Count} model catalogs ({string.Join(", ", result.Errors.Keys)}); showing cached models.";
            else
            {
                _errorMessage = _modelRuntime.GetError();
                if (string.IsNullOrEmpty(_errorMessage))
                {
                    _refreshStatusMessage = "Model catalogs refreshed.";
                    _refreshStatusSuccess = true;
                }
            }
            LoadModelsFromSnapshot();
            FilterModels(_searchInput.GetValue());
            _tui.RequestRender();
        }
        catch (Exception ex)
        {
            if (_closed) return;
            _refreshStatusMessage = "";
            _errorMessage = timedOut ? "Model refresh timed out; showing cached models." : $"Could not refresh model catalogs: {ex.Message}";
            UpdateList();
            _tui.RequestRender();
        }
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        _refreshCts.Cancel();
    }

    private bool IsDefaultModel(Model model) => _defaultModel is { } d && d.Provider == model.Provider && d.Id == model.Id;

    private List<ModelItem> SortModels(List<ModelItem> models) =>
        // Enumerable.OrderBy is stable, matching JavaScript's Array.prototype.sort.
        models.OrderBy(m => m, Comparer<ModelItem>.Create((a, b) =>
        {
            var aCurrent = ModelUtils.ModelsAreEqual(_currentModel, a.Model);
            var bCurrent = ModelUtils.ModelsAreEqual(_currentModel, b.Model);
            if (aCurrent != bCurrent) return aCurrent ? -1 : 1;
            var aDefault = IsDefaultModel(a.Model);
            var bDefault = IsDefaultModel(b.Model);
            if (aDefault != bDefault) return aDefault ? -1 : 1;
            return NodeCompare.LocaleCompare(a.Provider, b.Provider);
        })).ToList();

    private string GetScopeText()
    {
        var theme = ThemeManager.Current;
        var all = _scope == "all" ? theme.Fg("accent", "all") : theme.Fg("muted", "all");
        var scoped = _scope == "scoped" ? theme.Fg("accent", "scoped") : theme.Fg("muted", "scoped");
        return $"{theme.Fg("muted", "Scope: ")}{all}{theme.Fg("muted", " | ")}{scoped}";
    }

    private static string GetScopeHintText() => KeyHints.KeyHint("tui.input.tab", "scope") + ThemeManager.Current.Fg("muted", " (all/scoped)");

    private void SetScope(string scope)
    {
        if (_scope == scope) return;
        _scope = scope;
        _activeModels = _scope == "scoped" ? _scopedModelItems : _allModels;
        var currentIndex = _activeModels.FindIndex(i => ModelUtils.ModelsAreEqual(_currentModel, i.Model));
        _selectedIndex = currentIndex >= 0 ? currentIndex : 0;
        FilterModels(_searchInput.GetValue());
        _scopeText?.SetText(GetScopeText());
    }

    private void FilterModels(string query)
    {
        if (query.Length > 0)
        {
            var filtered = Fuzzy.Filter(_activeModels, query, item =>
                $"{item.Provider} {item.Provider}/{item.Id} {item.Provider} {item.Id}{(string.IsNullOrEmpty(item.Model.Name) ? "" : " " + item.Model.Name)}{(IsDefaultModel(item.Model) ? " default" : "")}");
            var normalized = query.Trim().ToLowerInvariant();
            if (normalized.Length > 0 && "default".StartsWith(normalized, StringComparison.Ordinal))
            {
                var defaults = _activeModels.Where(i => IsDefaultModel(i.Model)).ToList();
                var keys = defaults.Select(i => $"{i.Provider}\0{i.Id}").ToHashSet();
                _filteredModels = [.. defaults, .. filtered.Where(i => !keys.Contains($"{i.Provider}\0{i.Id}"))];
            }
            else
            {
                _filteredModels = filtered;
            }
        }
        else
        {
            _filteredModels = _activeModels;
        }
        _selectedIndex = query.Length > 0 ? 0 : Math.Min(_selectedIndex, Math.Max(0, _filteredModels.Count - 1));
        UpdateList();
    }

    private void UpdateList()
    {
        var theme = ThemeManager.Current;
        _listContainer.Clear();
        const int maxVisible = 10;
        var start = Math.Max(0, Math.Min(_selectedIndex - maxVisible / 2, _filteredModels.Count - maxVisible));
        var end = Math.Min(start + maxVisible, _filteredModels.Count);
        for (var i = start; i < end; i++)
        {
            var item = _filteredModels[i];
            var selected = i == _selectedIndex;
            var line = (selected ? theme.Fg("accent", "→ ") : "  ")
                + (ModelUtils.ModelsAreEqual(_currentModel, item.Model) ? theme.Fg("accent", "✓ ") : "  ")
                + (selected ? theme.Fg("accent", item.Id) : item.Id)
                + " " + theme.Fg("muted", $"[{item.Provider}]")
                + (IsDefaultModel(item.Model) ? theme.Fg("muted", " · default") : "");
            _listContainer.AddChild(new Text(line, 0, 0));
        }
        if (start > 0 || end < _filteredModels.Count) _listContainer.AddChild(new Text(theme.Fg("muted", $"  ({_selectedIndex + 1}/{_filteredModels.Count})"), 0, 0));

        if (!string.IsNullOrEmpty(_errorMessage))
        {
            foreach (var line in _errorMessage.Split('\n')) _listContainer.AddChild(new Text(theme.Fg("error", line), 0, 0));
        }
        else if (_filteredModels.Count == 0)
        {
            _listContainer.AddChild(new Text(theme.Fg("muted", "  No matching models"), 0, 0));
        }
        else
        {
            _listContainer.AddChild(new Spacer(1));
            _listContainer.AddChild(new Text(theme.Fg("muted", $"  Model Name: {_filteredModels[_selectedIndex].Model.Name}"), 0, 0));
        }
        if (_refreshStatusMessage.Length > 0)
        {
            _listContainer.AddChild(new Spacer(1));
            _listContainer.AddChild(new Text(theme.Fg(_refreshStatusSuccess ? "success" : "muted", $"  {_refreshStatusMessage}"), 0, 0));
        }
    }

    public void HandleInput(string keyData)
    {
        var kb = KeybindingsManager.Global;
        if (kb.Matches(keyData, "tui.input.tab"))
        {
            if (_scopedModelItems.Count > 0)
            {
                SetScope(_scope == "all" ? "scoped" : "all");
                _scopeHintText?.SetText(GetScopeHintText());
            }
            return;
        }
        if (kb.Matches(keyData, "tui.select.up"))
        {
            if (_filteredModels.Count == 0) return;
            _selectedIndex = _selectedIndex == 0 ? _filteredModels.Count - 1 : _selectedIndex - 1;
            UpdateList();
        }
        else if (kb.Matches(keyData, "tui.select.down"))
        {
            if (_filteredModels.Count == 0) return;
            _selectedIndex = _selectedIndex == _filteredModels.Count - 1 ? 0 : _selectedIndex + 1;
            UpdateList();
        }
        else if (kb.Matches(keyData, "tui.select.confirm"))
        {
            if (_selectedIndex < _filteredModels.Count) HandleSelect(_filteredModels[_selectedIndex].Model);
        }
        else if (kb.Matches(keyData, "tui.select.cancel"))
        {
            Dispose();
            _onCancel();
        }
        else if (kb.Matches(keyData, "app.models.save") && _onSelectAsDefault is not null)
        {
            if (_selectedIndex < _filteredModels.Count)
            {
                Dispose();
                _onSelectAsDefault(_filteredModels[_selectedIndex].Model);
            }
        }
        else
        {
            _searchInput.HandleInput(keyData);
            FilterModels(_searchInput.GetValue());
        }
    }

    private void HandleSelect(Model model)
    {
        Dispose();
        _onSelect(model);
    }
}

/// <summary>Project trust selector. Port of trust-selector.ts.</summary>
public sealed class TrustSelectorComponent : Container, IInputComponent
{
    private int _selectedIndex;
    private readonly Container _listContainer = new();
    private readonly List<ProjectTrustOption> _options;
    private readonly (string Path, bool Decision)? _savedDecision;
    private readonly Action<ProjectTrustOption> _onSelect;
    private readonly Action _onCancel;

    public TrustSelectorComponent(string cwd, (string Path, bool Decision)? savedDecision, bool projectTrusted, Action<ProjectTrustOption> onSelect, Action onCancel)
    {
        _savedDecision = savedDecision;
        _options = ProjectTrustStore.GetOptions(cwd);
        _selectedIndex = Math.Max(0, _options.FindIndex(IsSavedOption));
        _onSelect = onSelect;
        _onCancel = onCancel;
        var theme = ThemeManager.Current;
        AddChild(new DynamicBorder());
        AddChild(new Spacer(1));
        AddChild(new Text(theme.Fg("accent", theme.Bold("Project trust")), 1, 0));
        AddChild(new Text(theme.Fg("muted", cwd), 1, 0));
        AddChild(new Spacer(1));
        AddChild(new Text(theme.Fg("muted", $"Saved decision: {FormatDecision(_options.FirstOrDefault()?.SavedPath, savedDecision)}"), 1, 0));
        AddChild(new Text(theme.Fg("muted", $"Current session: {(projectTrusted ? "trusted" : "untrusted")}"), 1, 0));
        AddChild(new Spacer(1));
        AddChild(_listContainer);
        AddChild(new Spacer(1));
        AddChild(new Text(KeyHints.RawKeyHint("↑↓", "navigate") + "  " + KeyHints.KeyHint("tui.select.confirm", "save") + "  " + KeyHints.KeyHint("tui.select.cancel", "cancel"), 1, 0));
        AddChild(new Spacer(1));
        AddChild(new DynamicBorder());
        UpdateList();
    }

    private static string FormatDecision(string? trustPath, (string Path, bool Decision)? decision)
    {
        if (decision is not { } d) return "none";
        var label = d.Decision ? "trusted" : "untrusted";
        return trustPath is not null && d.Path != trustPath ? $"{label} (inherited from {d.Path})" : $"{label} ({d.Path})";
    }

    private bool IsSavedOption(ProjectTrustOption option) =>
        option.SavedPath is not null && _savedDecision is { } d && d.Decision == option.Trusted && d.Path == option.SavedPath;

    private void UpdateList()
    {
        var theme = ThemeManager.Current;
        _listContainer.Clear();
        for (var i = 0; i < _options.Count; i++)
        {
            var option = _options[i];
            var selected = i == _selectedIndex;
            var marker = IsSavedOption(option) ? theme.Fg("accent", "✓ ") : "  ";
            var prefix = selected ? theme.Fg("accent", "→ ") : "  ";
            var label = selected ? theme.Fg("accent", option.Label) : theme.Fg("text", option.Label);
            _listContainer.AddChild(new Text($"{prefix}{marker}{label}", 1, 0));
        }
    }

    public void HandleInput(string keyData)
    {
        var kb = KeybindingsManager.Global;
        if (kb.Matches(keyData, "tui.select.up") || keyData == "k")
        {
            _selectedIndex = Math.Max(0, _selectedIndex - 1);
            UpdateList();
        }
        else if (kb.Matches(keyData, "tui.select.down") || keyData == "j")
        {
            _selectedIndex = Math.Min(_options.Count - 1, _selectedIndex + 1);
            UpdateList();
        }
        else if (kb.Matches(keyData, "tui.select.confirm") || keyData == "\n")
        {
            if (_selectedIndex < _options.Count) _onSelect(_options[_selectedIndex]);
        }
        else if (kb.Matches(keyData, "tui.select.cancel"))
        {
            _onCancel();
        }
    }
}
