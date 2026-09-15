using System.Text.Json.Nodes;
using Iris.Ai;
using Iris.Ai.Json;
using Iris.Tui;
using Iris.Tui.Components;

namespace Iris.CodingAgent.Modes.Interactive.Components;

/// <summary>Titled select list submenu with optional fuzzy search.</summary>
public sealed class SelectSubmenu : Container, IInputComponent
{
    private static readonly SelectListLayoutOptions DefaultLayout = new() { MinPrimaryColumnWidth = 12, MaxPrimaryColumnWidth = 32 };

    private SelectList _selectList;
    private readonly int _listChildIndex;
    private readonly List<SelectItem> _allOptions;
    private readonly SelectListLayoutOptions _layout;
    private readonly Input? _searchInput;
    private readonly Action<string> _onSelect;
    private readonly Action _onCancel;
    private readonly Action<string>? _onSelectionChange;

    public SelectSubmenu(string title, string description, List<SelectItem> options, string currentValue, Action<string> onSelect, Action onCancel, Action<string>? onSelectionChange = null, bool searchable = false, SelectListLayoutOptions? layout = null)
    {
        _allOptions = options;
        _layout = layout ?? DefaultLayout;
        _onSelect = onSelect;
        _onCancel = onCancel;
        _onSelectionChange = onSelectionChange;
        var theme = ThemeManager.Current;

        AddChild(new Text(theme.Bold(theme.Fg("accent", title)), 0, 0));
        if (description.Length > 0)
        {
            AddChild(new Spacer(1));
            AddChild(new Text(theme.Fg("muted", description), 0, 0));
        }
        if (searchable)
        {
            AddChild(new Spacer(1));
            _searchInput = new Input { OnSubmit = _ => _selectList.HandleInput("\r") };
            AddChild(_searchInput);
        }
        AddChild(new Spacer(1));
        _selectList = BuildSelectList(options, currentValue);
        _listChildIndex = Children.Count;
        AddChild(_selectList);
        AddChild(new Spacer(1));
        AddChild(new Text(theme.Fg("dim", searchable ? "  Type to filter · Enter to select · Esc to go back" : "  Enter to select · Esc to go back"), 0, 0));
    }

    private SelectList BuildSelectList(List<SelectItem> options, string preselect)
    {
        var list = new SelectList(options, Math.Min(options.Count, 10), ThemeManager.GetSelectListTheme(), _layout);
        var index = options.FindIndex(o => o.Value == preselect);
        if (index != -1) list.SetSelectedIndex(index);
        list.OnSelect = item => _onSelect(item.Value);
        list.OnCancel = _onCancel;
        if (_onSelectionChange is { } cb) list.OnSelectionChange = item => cb(item.Value);
        return list;
    }

    private void ApplyFilter(string query)
    {
        var filtered = query.Length > 0 ? Fuzzy.Filter(_allOptions, query, i => $"{i.Label} {i.Description ?? ""}") : _allOptions;
        var newList = BuildSelectList(filtered, "");
        Children[_listChildIndex] = newList;
        _selectList = newList;
    }

    public void HandleInput(string data)
    {
        if (_searchInput is null)
        {
            _selectList.HandleInput(data);
            return;
        }
        var kb = KeybindingsManager.Global;
        if (kb.Matches(data, "tui.select.up") || kb.Matches(data, "tui.select.down") || kb.Matches(data, "tui.select.confirm") || kb.Matches(data, "tui.select.cancel"))
        {
            _selectList.HandleInput(data);
        }
        else
        {
            _searchInput.HandleInput(data);
            ApplyFilter(_searchInput.GetValue());
        }
    }
}

public sealed class SteppedSubmenuStep
{
    public required string Key { get; init; }
    public required Func<Dictionary<string, string>, string> Title { get; init; }
    public required Func<Dictionary<string, string>, string> Description { get; init; }
    public required Func<Dictionary<string, string>, List<SelectItem>> Options { get; init; }
    public Func<Dictionary<string, string>, string?>? Preselect { get; init; }
    public bool Searchable { get; init; }
    public SelectListLayoutOptions? Layout { get; init; }
}

/// <summary>Multi-step submenu.</summary>
public sealed class SteppedSubmenu : Container, IInputComponent
{
    private readonly List<SteppedSubmenuStep> _steps;
    private readonly Action<Dictionary<string, string>> _onComplete;
    private readonly Action _onCancel;
    private readonly bool _loop;
    private IComponent _active;
    private Dictionary<string, string> _context;

    public SteppedSubmenu(List<SteppedSubmenuStep> steps, Action<Dictionary<string, string>> onComplete, Action onCancel, bool loop = false, int startAtStep = 0, Dictionary<string, string>? initialContext = null)
    {
        _steps = steps;
        _onComplete = onComplete;
        _onCancel = onCancel;
        _loop = loop;
        _context = new Dictionary<string, string>(initialContext ?? []);
        _active = BuildStep(startAtStep);
    }

    private IComponent BuildStep(int stepIndex)
    {
        var step = _steps[stepIndex];
        var total = _steps.Count;
        var stepLabel = total > 1 ? $"Step {stepIndex + 1}/{total} · " : "";
        return new SelectSubmenu(step.Title(_context), stepLabel + step.Description(_context), step.Options(_context), step.Preselect?.Invoke(_context) ?? "",
            value =>
            {
                _context[step.Key] = value;
                if (stepIndex < total - 1)
                {
                    _active = BuildStep(stepIndex + 1);
                }
                else
                {
                    _onComplete(new Dictionary<string, string>(_context));
                    if (_loop)
                    {
                        _context = [];
                        _active = BuildStep(0);
                    }
                    else
                    {
                        _onCancel();
                    }
                }
            },
            () =>
            {
                if (stepIndex > 0)
                {
                    _context.Remove(step.Key);
                    _active = BuildStep(stepIndex - 1);
                }
                else
                {
                    _onCancel();
                }
            },
            null, step.Searchable, step.Layout);
    }

    public override List<string> Render(int width) => _active.Render(width);

    public void HandleInput(string data) => (_active as IInputComponent)?.HandleInput(data);

    public override void Invalidate() => _active.Invalidate();
}

public sealed class SettingsConfig
{
    public bool AutoCompact { get; init; }
    public required string DefaultModel { get; init; }
    public Model? CurrentModel { get; init; }
    public required IReadOnlyList<Model> AvailableDefaultModels { get; init; }
    public bool ShowImages { get; init; }
    public int ImageWidthCells { get; init; }
    public bool AutoResizeImages { get; init; }
    public bool BlockImages { get; init; }
    public bool EnableSkillCommands { get; init; }
    public required string SteeringMode { get; init; }
    public required string FollowUpMode { get; init; }
    public required string Transport { get; init; }
    public long HttpIdleTimeoutMs { get; init; }
    public ThinkingLevel ThinkingLevel { get; init; }
    public required Dictionary<string, ThinkingLevel> ModelThinkingLevels { get; init; }
    public required string CurrentTheme { get; init; }
    public required string TerminalTheme { get; init; }
    public required List<string> AvailableThemes { get; init; }
    public bool HideThinkingBlock { get; init; }
    public required string MermaidRenderingMode { get; init; }
    public bool ShowCacheMissNotices { get; init; }
    public bool CollapseChangelog { get; init; }
    public bool EnableInstallTelemetry { get; init; }
    public required string DoubleEscapeAction { get; init; }
    public required string TreeFilterMode { get; init; }
    public bool ShowHardwareCursor { get; init; }
    public int EditorPaddingX { get; init; }
    public int OutputPad { get; init; }
    public int AutocompleteMaxVisible { get; init; }
    public bool QuietStartup { get; init; }
    public required string DefaultProjectTrust { get; init; }
    public bool ClearOnShrink { get; init; }
    public bool ShowTerminalProgress { get; init; }
    public required string TuiMode { get; init; }
    public required string FullscreenExitOutput { get; init; }
    public required string FullscreenScrollbar { get; init; }
    public bool FullscreenCopyOnSelect { get; init; }
    public required JsonObject Warnings { get; init; }
}

public sealed class SettingsCallbacks
{
    public Action<bool> OnAutoCompactChange { get; init; } = _ => { };
    public Action<bool> OnShowImagesChange { get; init; } = _ => { };
    public Action<int> OnImageWidthCellsChange { get; init; } = _ => { };
    public Action<bool> OnAutoResizeImagesChange { get; init; } = _ => { };
    public Action<bool> OnBlockImagesChange { get; init; } = _ => { };
    public Action<bool> OnEnableSkillCommandsChange { get; init; } = _ => { };
    public Action<string> OnSteeringModeChange { get; init; } = _ => { };
    public Action<string> OnFollowUpModeChange { get; init; } = _ => { };
    public Action<string> OnTransportChange { get; init; } = _ => { };
    public Action<long> OnHttpIdleTimeoutMsChange { get; init; } = _ => { };
    public Action<string, string, ThinkingLevel> OnModelThinkingLevelChange { get; init; } = (_, _, _) => { };
    public Action<string, string> OnModelThinkingLevelRemove { get; init; } = (_, _) => { };
    public Action<string> OnThemeChange { get; init; } = _ => { };
    public Action<string>? OnThemePreview { get; init; }
    public Action<bool> OnHideThinkingBlockChange { get; init; } = _ => { };
    public Action<string> OnMermaidRenderingModeChange { get; init; } = _ => { };
    public Action<bool> OnShowCacheMissNoticesChange { get; init; } = _ => { };
    public Action<bool> OnCollapseChangelogChange { get; init; } = _ => { };
    public Action<bool> OnEnableInstallTelemetryChange { get; init; } = _ => { };
    public Action<string> OnDoubleEscapeActionChange { get; init; } = _ => { };
    public Action<string> OnTreeFilterModeChange { get; init; } = _ => { };
    public Action<bool> OnShowHardwareCursorChange { get; init; } = _ => { };
    public Action<int> OnEditorPaddingXChange { get; init; } = _ => { };
    public Action<int> OnOutputPadChange { get; init; } = _ => { };
    public Action<int> OnAutocompleteMaxVisibleChange { get; init; } = _ => { };
    public Action<bool> OnQuietStartupChange { get; init; } = _ => { };
    public Action<string> OnDefaultProjectTrustChange { get; init; } = _ => { };
    public Action<bool> OnClearOnShrinkChange { get; init; } = _ => { };
    public Action<bool> OnShowTerminalProgressChange { get; init; } = _ => { };
    public Action<string> OnTuiModeChange { get; init; } = _ => { };
    public Action<string> OnFullscreenExitOutputChange { get; init; } = _ => { };
    public Action<string> OnFullscreenScrollbarChange { get; init; } = _ => { };
    public Action<bool> OnFullscreenCopyOnSelectChange { get; init; } = _ => { };
    public Action<JsonObject> OnWarningsChange { get; init; } = _ => { };
    public required Action OnCancel { get; init; }
}

/// <summary>/settings selector.</summary>
public sealed class SettingsSelectorComponent : Container, IInputComponent
{
    public static readonly (string Label, long TimeoutMs)[] HttpIdleTimeoutChoices = [("30 sec", 30_000), ("1 min", 60_000), ("2 min", 120_000), ("5 min", 300_000), ("disabled", 0)];

    public static string FormatHttpIdleTimeoutMs(long timeoutMs) =>
        HttpIdleTimeoutChoices.FirstOrDefault(c => c.TimeoutMs == timeoutMs).Label ?? $"{Iris.CodingAgent.Core.Tools.NodeCompat.FormatNumber(timeoutMs / 1000.0)} sec";

    private static readonly Dictionary<ThinkingLevel, string> ThinkingDescriptions = new()
    {
        [ThinkingLevel.Off] = "No reasoning",
        [ThinkingLevel.Minimal] = "Very brief reasoning (~1k tokens)",
        [ThinkingLevel.Low] = "Light reasoning (~2k tokens)",
        [ThinkingLevel.Medium] = "Moderate reasoning (~8k tokens)",
        [ThinkingLevel.High] = "Deep reasoning (~16k tokens)",
        [ThinkingLevel.XHigh] = "Extra-high reasoning (~32k tokens)",
        [ThinkingLevel.Max] = "Maximum reasoning",
    };

    private static readonly (string Value, string Label)[] DefaultProjectTrustLabels = [("ask", "Ask"), ("always", "Always trust"), ("never", "Never trust")];

    private static readonly SelectListLayoutOptions ModelPickerLayout = new() { MinPrimaryColumnWidth = 12, MaxPrimaryColumnWidth = 46 };
    private const string ClearOverrideValue = "__clear__";
    private const string AutomaticThemeValue = "/";

    private readonly SettingsList _settingsList;

    private sealed class WarningSettingsSubmenu : Container, IInputComponent
    {
        private readonly SettingsList _list;
        private JsonObject _state;

        public WarningSettingsSubmenu(JsonObject warnings, Action<JsonObject> onChange, Action onCancel)
        {
            _state = (JsonObject)warnings.DeepClone();
            var items = new List<SettingItem>
            {
                new() { Id = "anthropic-extra-usage", Label = "Anthropic extra usage", Description = "Warn when Anthropic subscription auth may use paid extra usage", CurrentValue = (IrisJson.GetBool(_state["anthropicExtraUsage"]) ?? true) ? "true" : "false", Values = ["true", "false"] },
            };
            _list = new SettingsList(items, Math.Min(items.Count, 10), ThemeManager.GetSettingsListTheme(), (id, value) =>
            {
                if (id != "anthropic-extra-usage") return;
                _state = (JsonObject)_state.DeepClone();
                _state["anthropicExtraUsage"] = value == "true";
                onChange((JsonObject)_state.DeepClone());
            }, onCancel);
            AddChild(_list);
        }

        public void HandleInput(string data) => _list.HandleInput(data);
    }

    private static List<SelectItem> ThemeItems(List<string> themes, string current) =>
        themes.Select(name => new SelectItem(name, $"{(name == current ? "✓ " : "  ")}{name}")).ToList();

    private static string PreferredTheme(List<string> themes, string? preferred, string fallback) =>
        preferred is not null && themes.Contains(preferred) ? preferred : themes.Contains(fallback) ? fallback : themes.FirstOrDefault() ?? fallback;

    private sealed class ThemeSubmenu : Container, IInputComponent
    {
        private IComponent? _input;
        private readonly SettingsCallbacks _callbacks;
        private readonly List<string> _themes;
        private readonly string _terminalTheme;
        private readonly Action<string?, string?> _onDone;
        private readonly string _originalSetting;
        private string _mode;
        private string _singleTheme;
        private string _lightTheme;
        private string _darkTheme;

        public ThemeSubmenu(string currentSetting, string terminalTheme, List<string> themes, SettingsCallbacks callbacks, Action<string?, string?> onDone)
        {
            _callbacks = callbacks;
            _themes = themes;
            _terminalTheme = terminalTheme;
            _onDone = onDone;
            _originalSetting = currentSetting;
            var auto = ThemeManager.ParseAutoThemeSetting(currentSetting);
            var automatic = auto ?? (PreferredTheme(themes, currentSetting.Contains('/') ? null : currentSetting, "dark") is var t ? (t, t) : default);
            var fixedTheme = auto is not null || currentSetting.Contains('/') ? null : currentSetting;
            _mode = auto is not null ? "automatic" : "single";
            _lightTheme = automatic.Item1;
            _darkTheme = automatic.Item2;
            _singleTheme = PreferredTheme(themes, fixedTheme ?? (auto is not null ? ActiveAutomaticTheme() : null), "dark");
            if (_mode == "automatic") ShowAutomaticMenu();
            else ShowSingleMenu();
        }

        public void HandleInput(string data) => (_input as IInputComponent)?.HandleInput(data);

        private void SetContent(IComponent render, IComponent? input = null)
        {
            Clear();
            AddChild(render);
            _input = input ?? render;
        }

        private void ShowSingleMenu()
        {
            _mode = "single";
            var items = new List<SelectItem> { new(AutomaticThemeValue, "  Automatic", "Use separate themes for light and dark terminal appearance") };
            items.AddRange(ThemeItems(_themes, _singleTheme));
            SetContent(new SelectSubmenu("Theme", "Select a theme, or choose Automatic to follow terminal appearance.", items, _singleTheme,
                value =>
                {
                    if (value == AutomaticThemeValue)
                    {
                        _mode = "automatic";
                        _callbacks.OnThemePreview?.Invoke(ThemeSetting());
                        ShowAutomaticMenu();
                        return;
                    }
                    _singleTheme = value;
                    _onDone(value, null);
                },
                Cancel,
                value => _callbacks.OnThemePreview?.Invoke(value == AutomaticThemeValue ? AutomaticSetting() : value)));
        }

        private void ShowAutomaticMenu()
        {
            _mode = "automatic";
            var theme = ThemeManager.Current;
            var content = new Container();
            content.AddChild(new Text(theme.Bold(theme.Fg("accent", "Automatic Theme")), 0, 0));
            content.AddChild(new Spacer(1));
            content.AddChild(new Text(theme.Fg("muted", "Choose themes for terminal light and dark appearance."), 0, 0));
            content.AddChild(new Text(theme.Fg("muted", "Light/dark detection requires terminal support."), 0, 0));
            content.AddChild(new Spacer(1));
            var items = new List<SettingItem>
            {
                new()
                {
                    Id = "light-theme", Label = "Light theme", Description = "Theme to use in automatic mode when the terminal is light", CurrentValue = _lightTheme,
                    Submenu = (current, done) => CreateThemeSelect("Light Theme", "Select the theme to use for light terminal appearance", current, done, value =>
                    {
                        _lightTheme = value;
                        _callbacks.OnThemePreview?.Invoke(ThemeSetting());
                        done(value, null);
                    }),
                },
                new()
                {
                    Id = "dark-theme", Label = "Dark theme", Description = "Theme to use in automatic mode when the terminal is dark", CurrentValue = _darkTheme,
                    Submenu = (current, done) => CreateThemeSelect("Dark Theme", "Select the theme to use for dark terminal appearance", current, done, value =>
                    {
                        _darkTheme = value;
                        _callbacks.OnThemePreview?.Invoke(ThemeSetting());
                        done(value, null);
                    }),
                },
                new() { Id = "apply", Label = "Apply", Description = "Save and go back", CurrentValue = "save and go back", Values = ["save and go back"] },
                new() { Id = "single-mode", Label = "Change mode", Description = "Switch to one theme for light and dark", CurrentValue = "switch to single theme", Values = ["switch to single theme"] },
            };
            var list = new SettingsList(items, Math.Min(items.Count, 10), ThemeManager.GetSettingsListTheme(), (id, _) =>
            {
                if (id == "single-mode")
                {
                    _mode = "single";
                    _singleTheme = ActiveAutomaticTheme();
                    _callbacks.OnThemePreview?.Invoke(_singleTheme);
                    ShowSingleMenu();
                }
                else if (id == "apply")
                {
                    _onDone(AutomaticSetting(), null);
                }
            }, Cancel);
            content.AddChild(list);
            SetContent(content, list);
        }

        private SelectSubmenu CreateThemeSelect(string title, string description, string current, Action<string?, string?> done, Action<string> onSelect) =>
            new(title, description, ThemeItems(_themes, current), current, onSelect,
                () =>
                {
                    _callbacks.OnThemePreview?.Invoke(ThemeSetting());
                    done(null, null);
                },
                value => _callbacks.OnThemePreview?.Invoke(value));

        private string ThemeSetting() => _mode == "automatic" ? AutomaticSetting() : _singleTheme;

        private string ActiveAutomaticTheme() => _terminalTheme == "light" ? _lightTheme : _darkTheme;

        private string AutomaticSetting() => $"{_lightTheme}/{_darkTheme}";

        private void Cancel()
        {
            _callbacks.OnThemePreview?.Invoke(_originalSetting);
            _onDone(null, null);
        }
    }

    public SettingsSelectorComponent(SettingsConfig config, SettingsCallbacks callbacks)
    {
        var supportsImages = TerminalImage.GetCapabilities().Images is not null;
        var followUpKey = KeyHints.KeyDisplayText("app.message.followUp");
        var cycleThinkingKey = KeyHints.KeyDisplayText("app.thinking.cycle");
        var currentWarnings = (JsonObject)config.Warnings.DeepClone();
        var currentModelThinkingLevels = new Dictionary<string, ThinkingLevel>(config.ModelThinkingLevels);
        static string ModelKey(Model m) => $"{m.Provider}/{m.Id}";
        var defaultModelByValue = new Dictionary<string, Model>();
        foreach (var m in config.AvailableDefaultModels) defaultModelByValue[ModelKey(m)] = m;
        var currentDefaultModelKey = defaultModelByValue.ContainsKey(config.DefaultModel) ? config.DefaultModel : null;
        var currentModelKey = config.CurrentModel is { } cm ? ModelKey(cm) : null;
        static string Bool(bool b) => b ? "true" : "false";
        string OverridesSummary() => currentModelThinkingLevels.Count == 0 ? "none" : $"{currentModelThinkingLevels.Count} configured";

        var items = new List<SettingItem>
        {
            new() { Id = "autocompact", Label = "Auto-compact", Description = "Automatically compact context when it gets too large", CurrentValue = Bool(config.AutoCompact), Values = ["true", "false"] },
            new() { Id = "steering-mode", Label = "Steering mode", Description = "Enter while streaming queues steering messages. 'one-at-a-time': deliver one, wait for response. 'all': deliver all at once.", CurrentValue = config.SteeringMode, Values = ["one-at-a-time", "all"] },
            new() { Id = "follow-up-mode", Label = "Follow-up mode", Description = $"{followUpKey} queues follow-up messages until agent stops. 'one-at-a-time': deliver one, wait for response. 'all': deliver all at once.", CurrentValue = config.FollowUpMode, Values = ["one-at-a-time", "all"] },
            new() { Id = "transport", Label = "Transport", Description = "Preferred transport for providers that support multiple transports", CurrentValue = config.Transport, Values = ["sse", "websocket", "websocket-cached", "auto"] },
            new() { Id = "http-idle-timeout", Label = "HTTP idle timeout", Description = "Maximum idle gap while waiting for HTTP headers or body chunks. Disable for local models that pause longer than five minutes.", CurrentValue = FormatHttpIdleTimeoutMs(config.HttpIdleTimeoutMs), Values = HttpIdleTimeoutChoices.Select(c => c.Label).ToList() },
            new() { Id = "hide-thinking", Label = "Hide thinking", Description = "Hide thinking blocks in assistant responses", CurrentValue = Bool(config.HideThinkingBlock), Values = ["true", "false"] },
            new() { Id = "mermaid-rendering", Label = "Mermaid diagrams", Description = "Render Mermaid code blocks as Unicode diagrams", CurrentValue = config.MermaidRenderingMode, Values = ["off", "final", "streaming"] },
            new() { Id = "cache-miss-notices", Label = "Cache miss notices", Description = "Show transcript notices for cache costs and provider recovery diagnostics", CurrentValue = Bool(config.ShowCacheMissNotices), Values = ["true", "false"] },
            new() { Id = "collapse-changelog", Label = "Collapse changelog", Description = "Show condensed changelog after updates", CurrentValue = Bool(config.CollapseChangelog), Values = ["true", "false"] },
            new() { Id = "quiet-startup", Label = "Quiet startup", Description = "Disable verbose printing at startup", CurrentValue = Bool(config.QuietStartup), Values = ["true", "false"] },
            new() { Id = "install-telemetry", Label = "Install telemetry", Description = "Send an anonymous version/update ping after changelog-detected updates", CurrentValue = Bool(config.EnableInstallTelemetry), Values = ["true", "false"] },
            new() { Id = "default-project-trust", Label = "Default project trust", Description = "Fallback behavior when no extension or saved trust decision decides project trust", CurrentValue = DefaultProjectTrustLabels.FirstOrDefault(l => l.Value == config.DefaultProjectTrust).Label ?? "Ask", Values = DefaultProjectTrustLabels.Select(l => l.Label).ToList() },
            new() { Id = "double-escape-action", Label = "Double-escape action", Description = "Action when pressing Escape twice with empty editor", CurrentValue = config.DoubleEscapeAction, Values = ["tree", "fork", "none"] },
            new() { Id = "tree-filter-mode", Label = "Tree filter mode", Description = "Default filter when opening /tree", CurrentValue = config.TreeFilterMode, Values = ["default", "no-tools", "user-only", "labeled-only", "all"] },
            new()
            {
                Id = "warnings", Label = "Warnings", Description = "Enable or disable individual warnings", CurrentValue = "configure",
                Submenu = (_, done) => new WarningSettingsSubmenu(currentWarnings, warnings =>
                {
                    currentWarnings = warnings;
                    callbacks.OnWarningsChange(warnings);
                }, () => done(null, null)),
            },
            new()
            {
                Id = "model-thinking", Label = "Default thinking level per model", Description = $"Override the default thinking level for specific models. {cycleThinkingKey} cycles in-session.", CurrentValue = OverridesSummary(),
                Submenu = (_, done) =>
                {
                    var steps = new List<SteppedSubmenuStep>
                    {
                        new()
                        {
                            Key = "model",
                            Title = _ => "Per-Model Thinking Level",
                            Description = _ => "Select a model to configure",
                            Options = _ =>
                            {
                                var sorted = config.AvailableDefaultModels.OrderBy(m => m, Comparer<Model>.Create((a, b) =>
                                {
                                    var aKey = ModelKey(a);
                                    var bKey = ModelKey(b);
                                    if (aKey == currentModelKey) return -1;
                                    if (bKey == currentModelKey) return 1;
                                    if (aKey == currentDefaultModelKey) return -1;
                                    if (bKey == currentDefaultModelKey) return 1;
                                    return Iris.Tui.NodeCompare.LocaleCompare(a.Provider, b.Provider);
                                })).ToList();
                                var list = sorted.Select(m => new SelectItem(ModelKey(m), $"{m.Id} {ThemeManager.Current.Fg("muted", $"[{m.Provider}]")}", currentModelThinkingLevels.TryGetValue(ModelKey(m), out var level) ? level.ToWire() : null)).ToList();
                                if (list.Count == 0) list.Add(new SelectItem("__none__", "No models available", "Log in to a provider or configure an API key first"));
                                return list;
                            },
                            Preselect = _ => currentModelKey ?? currentDefaultModelKey,
                            Searchable = true,
                            Layout = ModelPickerLayout,
                        },
                        new()
                        {
                            Key = "level",
                            Title = ctx => $"Thinking Level for {(defaultModelByValue.TryGetValue(ctx["model"], out var m) ? $"{m.Id} [{m.Provider}]" : ctx["model"])}",
                            Description = _ => "Select default thinking level for this model",
                            Options = ctx =>
                            {
                                if (!defaultModelByValue.TryGetValue(ctx["model"], out var model)) return [];
                                var levels = model.Reasoning ? ModelUtils.GetSupportedThinkingLevels(model) : [ThinkingLevel.Off];
                                var active = currentModelThinkingLevels.TryGetValue(ctx["model"], out var a) ? a : (ThinkingLevel?)null;
                                var list = levels.Select(level => new SelectItem(level.ToWire(), $"{(level == active ? "✓ " : "  ")}{level.ToWire()}", ThinkingDescriptions[level])).ToList();
                                if (active is not null) list.Add(new SelectItem(ClearOverrideValue, "  (clear override)", $"Revert to global default ({config.ThinkingLevel.ToWire()})"));
                                return list;
                            },
                            Preselect = ctx => currentModelThinkingLevels.TryGetValue(ctx["model"], out var level) ? level.ToWire() : null,
                        },
                    };
                    return new SteppedSubmenu(steps, selections =>
                    {
                        if (!defaultModelByValue.TryGetValue(selections["model"], out var model)) return;
                        if (selections["level"] == ClearOverrideValue)
                        {
                            callbacks.OnModelThinkingLevelRemove(model.Provider, model.Id);
                            currentModelThinkingLevels.Remove(selections["model"]);
                        }
                        else
                        {
                            var level = ThinkingLevelNames.ParseOrOff(selections["level"]);
                            callbacks.OnModelThinkingLevelChange(model.Provider, model.Id, level);
                            currentModelThinkingLevels[selections["model"]] = level;
                        }
                    }, () => done(OverridesSummary(), null), loop: true);
                },
            },
            new() { Id = "tui-mode", Label = "TUI mode", Description = "Interface layout; fullscreen mode is experimental", CurrentValue = config.TuiMode, Values = ["regular", "fullscreen"] },
            new() { Id = "fullscreen-exit-output", Label = "Fullscreen exit output", Description = "Print the transcript or only a session resume hint when exiting fullscreen mode", CurrentValue = config.FullscreenExitOutput, Values = ["transcript", "resume-hint"] },
            new() { Id = "fullscreen-scrollbar", Label = "Fullscreen scrollbar", Description = "Scrollbar behavior in fullscreen mode; has no effect in regular mode", CurrentValue = config.FullscreenScrollbar, Values = ["auto", "always", "hidden"] },
            new() { Id = "fullscreen-copy-on-select", Label = "Fullscreen copy on select", Description = "Automatically copy selected text in fullscreen mode; disable to copy selections with Ctrl+X", CurrentValue = Bool(config.FullscreenCopyOnSelect), Values = ["true", "false"] },
            new() { Id = "theme", Label = "Theme", Description = "Color theme for the interface", CurrentValue = config.CurrentTheme, Submenu = (current, done) => new ThemeSubmenu(current, config.TerminalTheme, config.AvailableThemes, callbacks, done) },
        };

        if (supportsImages)
        {
            items.Insert(1, new SettingItem { Id = "show-images", Label = "Show images", Description = "Render images inline in terminal", CurrentValue = Bool(config.ShowImages), Values = ["true", "false"] });
            items.Insert(2, new SettingItem { Id = "image-width-cells", Label = "Image width", Description = "Preferred inline image width in terminal cells", CurrentValue = config.ImageWidthCells.ToString(), Values = ["60", "80", "120"] });
        }
        items.Insert(supportsImages ? 3 : 1, new SettingItem { Id = "auto-resize-images", Label = "Auto-resize images", Description = "Resize large images to 2000x2000 max for better model compatibility", CurrentValue = Bool(config.AutoResizeImages), Values = ["true", "false"] });

        void InsertAfter(string afterId, SettingItem item) => items.Insert(items.FindIndex(i => i.Id == afterId) + 1, item);
        InsertAfter("auto-resize-images", new SettingItem { Id = "block-images", Label = "Block images", Description = "Prevent images from being sent to LLM providers", CurrentValue = Bool(config.BlockImages), Values = ["true", "false"] });
        InsertAfter("block-images", new SettingItem { Id = "skill-commands", Label = "Skill commands", Description = "Register skills as /skill:name commands", CurrentValue = Bool(config.EnableSkillCommands), Values = ["true", "false"] });
        InsertAfter("skill-commands", new SettingItem { Id = "show-hardware-cursor", Label = "Show hardware cursor", Description = "Show the terminal cursor while still positioning it for IME support", CurrentValue = Bool(config.ShowHardwareCursor), Values = ["true", "false"] });
        InsertAfter("show-hardware-cursor", new SettingItem { Id = "editor-padding", Label = "Editor padding", Description = "Horizontal padding for input editor (0-3)", CurrentValue = config.EditorPaddingX.ToString(), Values = ["0", "1", "2", "3"] });
        InsertAfter("editor-padding", new SettingItem { Id = "output-padding", Label = "Output padding", Description = "Horizontal padding for user messages, assistant messages, and thinking", CurrentValue = config.OutputPad.ToString(), Values = ["0", "1"] });
        InsertAfter("output-padding", new SettingItem { Id = "autocomplete-max-visible", Label = "Autocomplete max items", Description = "Max visible items in autocomplete dropdown (3-20)", CurrentValue = config.AutocompleteMaxVisible.ToString(), Values = ["3", "5", "7", "10", "15", "20"] });
        InsertAfter("autocomplete-max-visible", new SettingItem { Id = "clear-on-shrink", Label = "Clear on shrink", Description = "Clear empty rows when content shrinks (may cause flicker)", CurrentValue = Bool(config.ClearOnShrink), Values = ["true", "false"] });
        InsertAfter("clear-on-shrink", new SettingItem { Id = "terminal-progress", Label = "Terminal progress", Description = "Show OSC 9;4 progress indicators in the terminal tab bar", CurrentValue = Bool(config.ShowTerminalProgress), Values = ["true", "false"] });

        AddChild(new DynamicBorder());
        _settingsList = new SettingsList(items, 10, ThemeManager.GetSettingsListTheme(), (id, value) =>
        {
            switch (id)
            {
                case "autocompact": callbacks.OnAutoCompactChange(value == "true"); break;
                case "show-images": callbacks.OnShowImagesChange(value == "true"); break;
                case "image-width-cells": callbacks.OnImageWidthCellsChange(int.Parse(value)); break;
                case "auto-resize-images": callbacks.OnAutoResizeImagesChange(value == "true"); break;
                case "block-images": callbacks.OnBlockImagesChange(value == "true"); break;
                case "skill-commands": callbacks.OnEnableSkillCommandsChange(value == "true"); break;
                case "steering-mode": callbacks.OnSteeringModeChange(value); break;
                case "follow-up-mode": callbacks.OnFollowUpModeChange(value); break;
                case "transport": callbacks.OnTransportChange(value); break;
                case "http-idle-timeout":
                    foreach (var choice in HttpIdleTimeoutChoices)
                    {
                        if (choice.Label == value) callbacks.OnHttpIdleTimeoutMsChange(choice.TimeoutMs);
                    }
                    break;
                case "hide-thinking": callbacks.OnHideThinkingBlockChange(value == "true"); break;
                case "mermaid-rendering": callbacks.OnMermaidRenderingModeChange(value); break;
                case "cache-miss-notices": callbacks.OnShowCacheMissNoticesChange(value == "true"); break;
                case "collapse-changelog": callbacks.OnCollapseChangelogChange(value == "true"); break;
                case "quiet-startup": callbacks.OnQuietStartupChange(value == "true"); break;
                case "install-telemetry": callbacks.OnEnableInstallTelemetryChange(value == "true"); break;
                case "default-project-trust":
                    if (DefaultProjectTrustLabels.FirstOrDefault(l => l.Label == value).Value is { } trust) callbacks.OnDefaultProjectTrustChange(trust);
                    break;
                case "double-escape-action": callbacks.OnDoubleEscapeActionChange(value); break;
                case "tree-filter-mode": callbacks.OnTreeFilterModeChange(value); break;
                case "show-hardware-cursor": callbacks.OnShowHardwareCursorChange(value == "true"); break;
                case "editor-padding": callbacks.OnEditorPaddingXChange(int.Parse(value)); break;
                case "output-padding": callbacks.OnOutputPadChange(value == "0" ? 0 : 1); break;
                case "autocomplete-max-visible": callbacks.OnAutocompleteMaxVisibleChange(int.Parse(value)); break;
                case "clear-on-shrink": callbacks.OnClearOnShrinkChange(value == "true"); break;
                case "terminal-progress": callbacks.OnShowTerminalProgressChange(value == "true"); break;
                case "tui-mode": callbacks.OnTuiModeChange(value); break;
                case "fullscreen-exit-output": callbacks.OnFullscreenExitOutputChange(value); break;
                case "fullscreen-scrollbar": callbacks.OnFullscreenScrollbarChange(value); break;
                case "fullscreen-copy-on-select": callbacks.OnFullscreenCopyOnSelectChange(value == "true"); break;
                case "theme": callbacks.OnThemeChange(value); break;
            }
        }, callbacks.OnCancel, enableSearch: true);
        AddChild(_settingsList);
        AddChild(new DynamicBorder());
    }

    public void HandleInput(string data) => _settingsList.HandleInput(data);

    public SettingsList GetSettingsList() => _settingsList;
}
