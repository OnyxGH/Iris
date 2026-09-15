using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Iris.Agent;
using Iris.Ai;
using Iris.Ai.Json;
using Iris.CodingAgent.Config;
using Iris.CodingAgent.Core;
using Iris.CodingAgent.Core.Tools;
using Iris.CodingAgent.Modes.Interactive.Components;
using Iris.CodingAgent.Utils;
using Iris.Tui;
using Iris.Tui.Components;

namespace Iris.CodingAgent.Modes.Interactive;

public sealed class InteractiveModeOptions
{
    public List<string>? MigratedProviders { get; init; }
    public List<AgentSessionRuntimeDiagnostic>? StartupDiagnostics { get; init; }
    public string? ModelFallbackMessage { get; init; }
    public string? AutoTrustOnReloadCwd { get; init; }
    public string? InitialMessage { get; init; }
    public List<ImageContent>? InitialImages { get; init; }
    public List<string>? InitialMessages { get; init; }
    public bool Verbose { get; init; }
    public string? InitialThemeSetting { get; init; }
}

/// <summary>Text toggling between collapsed and expanded content. Port of interactive-mode.ts ExpandableText.</summary>
internal sealed class ExpandableText(Func<string> collapsed, Func<string> expanded, bool isExpanded = false, int paddingX = 0, int paddingY = 0)
    : Text(isExpanded ? expanded() : collapsed(), paddingX, paddingY), IExpandable
{
    public void SetExpanded(bool value) => SetText(value ? expanded() : collapsed());
}

/// <summary>Interactive TUI mode. Port of modes/interactive/interactive-mode.ts (regular main-screen mode).</summary>
public sealed partial class InteractiveMode
{
    private const string AnthropicSubscriptionAuthWarning =
        "Anthropic subscription auth is active. Third-party harness usage draws from extra usage and is billed per token, not your Claude plan limits. Manage extra usage at https://claude.ai/settings/usage. Disable this warning in /settings.";

    private sealed record CompactionQueuedMessage(string Text, string Mode);

    private readonly AgentSessionRuntime _runtimeHost;
    private readonly UiDispatcher _dispatcher;
    private readonly TuiMainScreen _ui;
    private readonly Container _loadedResourcesContainer = new();
    private readonly Container _chatContainer = new();
    private readonly Container _documentContainer = new();
    private readonly Container _pendingMessagesContainer = new();
    private readonly Container _statusContainer = new();
    private readonly CustomEditor _defaultEditor;
    private IEditorComponent _editor;
    private IAutocompleteProvider? _autocompleteProvider;
    private string? _fdPath;
    private readonly Container _editorContainer = new();
    private object? _activeSelectorToken;
    private Action? _activeSelectorDispose;
    private readonly FooterComponent _footer;
    private readonly Container _footerContainer = new();
    private readonly FooterDataProvider _footerDataProvider;
    private readonly KeybindingsManager _keybindings;
    private readonly string _version = AppConfig.Version;
    private bool _isInitialized;
    private Action<string>? _onInputCallback;
    private readonly Queue<string> _pendingUserInputs = new();
    private StatusIndicator? _activeStatusIndicator;
    private bool _activeWorkingIndicatorEmbedded;
    private readonly IdleStatus _idleStatus = new();
    private string? _workingMessage;
    private bool _workingVisible = true;
    private LoaderIndicatorOptions? _workingIndicatorOptions;
    private const string DefaultWorkingMessage = "Working";
    private const string DefaultHiddenThinkingLabel = "Thinking...";
    private string _hiddenThinkingLabel = DefaultHiddenThinkingLabel;

    private long _lastSigintTime;
    private long _lastEscapeTime;
    private string? _changelogMarkdown;
    private bool _startupNoticesShown;
    private bool _anthropicSubscriptionWarningShown;

    private Spacer? _lastStatusSpacer;
    private Text? _lastStatusText;
    private bool _managedToolStatusStarted;

    private AssistantMessageComponent? _streamingComponent;
    private AssistantMessage? _streamingMessage;
    private readonly Dictionary<string, ToolExecutionComponent> _pendingTools = [];
    private bool _toolOutputExpanded;
    private bool _hideThinkingBlock;
    private int _outputPad = 1;
    private readonly Dictionary<string, string> _skillCommands = [];
    private IDisposable? _unsubscribe;
    private readonly List<IDisposable> _signalRegistrations = [];
    private bool _isBashMode;
    private BashExecutionComponent? _bashComponent;
    private readonly List<BashExecutionComponent> _pendingBashComponents = [];
    private Action? _autoCompactionEscapeHandler;
    private Action? _retryEscapeHandler;
    private List<CompactionQueuedMessage> _compactionQueuedMessages = [];
    private bool _shutdownRequested = false;
    private ExtensionSelectorComponent? _extensionSelector;
    private ExtensionInputComponent? _extensionInput;
    private ExtensionEditorComponent? _extensionEditor;
    private readonly Container _widgetContainerAbove = new();
    private readonly Container _widgetContainerBelow = new();
    private readonly Container _headerContainer = new();
    private IComponent? _builtInHeader;
    private readonly InteractiveModeOptions _options;
    private string? _autoTrustOnReloadCwd;
    private readonly InteractiveThemeController _themeController;
    private bool _isShuttingDown;
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private AgentSession Session => _runtimeHost.Session;
    private Iris.Agent.Agent Agent => Session.Agent;
    private SessionManager SessionManager => Session.SessionManager;
    private SettingsManager SettingsManager => Session.SettingsManager;
    private static Theme Theme => ThemeManager.Current;

    /// <summary>Must be constructed on the UI dispatcher thread.</summary>
    public InteractiveMode(AgentSessionRuntime runtimeHost, InteractiveModeOptions? options = null)
    {
        _dispatcher = UiDispatcher.Current ?? throw new InvalidOperationException("InteractiveMode must be created on a UiDispatcher thread");
        _runtimeHost = runtimeHost;
        _options = options ?? new InteractiveModeOptions();
        ApplyCapabilityOverrides();
        _autoTrustOnReloadCwd = _options.AutoTrustOnReloadCwd;
        _runtimeHost.SetBeforeSessionInvalidate(() => _dispatcher.Send(_ => ResetExtensionUI(), null));
        _runtimeHost.SetRebindSession(_ => _dispatcher.InvokeAsync(async () =>
        {
            await RebindCurrentSessionAsync(renderBeforeBind: true);
            await _themeController!.ApplyFromSettingsAsync();
        }));

        _ui = new TuiMainScreen(new ProcessTerminal(), _dispatcher, SettingsManager.ShowHardwareCursor, AppConfig.AgentDir);
        _ui.SetClearOnShrink(SettingsManager.ClearOnShrink);
        ThemeManager.SetRegisteredThemes(Session.ResourceLoader.GetThemes().Themes.Select(ThemeManager.CreateThemeFromResource));
        _themeController = new InteractiveThemeController(_ui, () => SettingsManager, message => ShowError(message), () => UpdateEditorBorderColor(), _options.InitialThemeSetting);
        _documentContainer.AddChild(_headerContainer);
        _documentContainer.AddChild(_loadedResourcesContainer);
        _documentContainer.AddChild(_chatContainer);

        _keybindings = AppKeybindings.Create();
        KeybindingsManager.Global = _keybindings;
        _defaultEditor = new CustomEditor(_ui, ThemeManager.GetEditorTheme(), _keybindings,
            new EditorOptions { PaddingX = SettingsManager.EditorPaddingX, AutocompleteMaxVisible = SettingsManager.AutocompleteMaxVisible }, embedWorkingStatus: true);
        _editor = _defaultEditor;
        _editorContainer.AddChild(_editor);
        _footerDataProvider = new FooterDataProvider(SessionManager.Cwd);
        _footer = new FooterComponent(Session, _footerDataProvider);
        _footer.SetAutoCompactEnabled(Session.AutoCompactionEnabled);
        _footerContainer.AddChild(_footer);

        _hideThinkingBlock = SettingsManager.HideThinkingBlock;
        _outputPad = SettingsManager.OutputPad;
    }

    private void ApplyCapabilityOverrides()
    {
        var overrides = SettingsManager.TerminalCapabilityOverrides;
        TerminalImage.SetCapabilityOverrides(
            PiJson.GetBool(overrides["trueColor"]),
            PiJson.GetBool(overrides["hyperlinks"]),
            overrides.ContainsKey("images"),
            PiJson.GetString(overrides["images"]));
    }

    // ----- Autocomplete -----

    private static string? GetAutocompleteSourceTag(SourceInfo? sourceInfo)
    {
        if (sourceInfo is null) return null;
        var scopePrefix = sourceInfo.Scope == "user" ? "u" : sourceInfo.Scope == "project" ? "p" : "t";
        var source = sourceInfo.Source.Trim();
        if (source is "auto" or "local" or "cli") return scopePrefix;
        if (source.StartsWith("npm:", StringComparison.Ordinal)) return $"{scopePrefix}:{source}";
        // Simplified git source label (package sources are not ported yet): git:host/owner/repo[@ref].
        var gitMatch = Regex.Match(source, @"^(?:git:)?\s*(?:(?:https?|ssh|git)://)?(?:[^@/]+@)?([^/:]+)[/:](.+?)(?:\.git)?(?:[#@]([^/]+))?$");
        if ((source.StartsWith("git:", StringComparison.Ordinal) || Regex.IsMatch(source, "^(https?|ssh|git)://", RegexOptions.IgnoreCase)) && gitMatch.Success)
        {
            return $"{scopePrefix}:git:{gitMatch.Groups[1].Value}/{gitMatch.Groups[2].Value}{(gitMatch.Groups[3].Success ? "@" + gitMatch.Groups[3].Value : "")}";
        }
        return scopePrefix;
    }

    private static string? PrefixAutocompleteDescription(string? description, SourceInfo? sourceInfo)
    {
        var tag = GetAutocompleteSourceTag(sourceInfo);
        if (tag is null) return description;
        return string.IsNullOrEmpty(description) ? $"[{tag}]" : $"[{tag}] {description}";
    }

    private static List<AutocompleteItem>? FuzzyItems<T>(IReadOnlyList<T> items, string prefix, Func<T, string> text, Func<T, AutocompleteItem> toItem)
    {
        var filtered = Fuzzy.Filter(items, prefix, text);
        return filtered.Count == 0 ? null : filtered.Select(toItem).ToList();
    }

    private IAutocompleteProvider CreateBaseAutocompleteProvider()
    {
        var slashCommands = SystemPrompt.BuiltinSlashCommands.Select(c => new SlashCommand
        {
            Name = c.Name,
            Description = c.Description,
            ArgumentHint = c.ArgumentHint,
            GetArgumentCompletions = c.Name switch
            {
                "model" => prefix =>
                {
                    var models = Session.ScopedModels.Count > 0 ? Session.ScopedModels.Select(s => s.Model).ToList() : Session.ModelRuntime.AvailableSnapshot.ToList();
                    if (models.Count == 0) return Task.FromResult<List<AutocompleteItem>?>(null);
                    return Task.FromResult(FuzzyItems(models, prefix,
                        m => $"{m.Id} {m.Provider} {m.Provider}/{m.Id} {m.Provider} {m.Id}{(string.IsNullOrEmpty(m.Name) ? "" : " " + m.Name)}",
                        m => new AutocompleteItem($"{m.Provider}/{m.Id}", m.Id, m.Provider)));
                },
                "thinking" => prefix => Task.FromResult(FuzzyItems(Session.GetAvailableThinkingLevels(), prefix, l => l.ToWire(), l => new AutocompleteItem(l.ToWire(), l.ToWire()))),
                "login" => prefix => Task.FromResult(GetLoginArgumentCompletions(prefix)),
                _ => null,
            },
        }).ToList<object>();

        var templateCommands = Session.PromptTemplates.Select(t => (object)new SlashCommand { Name = t.Name, Description = PrefixAutocompleteDescription(t.Description, t.SourceInfo), ArgumentHint = t.ArgumentHint });
        var builtinNames = SystemPrompt.BuiltinSlashCommands.Select(c => c.Name).ToHashSet();
        var extensionCommands = Session.ExtensionRunner.GetRegisteredCommands()
            .Where(c => !builtinNames.Contains(c.InvocationName))
            .Select(c => (object)new SlashCommand { Name = c.InvocationName, Description = PrefixAutocompleteDescription(c.Description, c.SourceInfo) })
            // pi registers /llama through its hidden built-in llama.cpp extension.
            .Prepend(new SlashCommand { Name = "llama", Description = "Manage llama.cpp router models" });

        _skillCommands.Clear();
        var skillCommands = new List<object>();
        if (SettingsManager.EnableSkillCommands)
        {
            foreach (var skill in Session.ResourceLoader.GetSkills().Skills)
            {
                var name = $"skill:{skill.Name}";
                _skillCommands[name] = skill.FilePath;
                skillCommands.Add(new SlashCommand { Name = name, Description = PrefixAutocompleteDescription(skill.Description, skill.SourceInfo) });
            }
        }

        return new CombinedAutocompleteProvider([.. slashCommands, .. templateCommands, .. extensionCommands, .. skillCommands], SessionManager.Cwd, _fdPath);
    }

    private void SetupAutocompleteProvider()
    {
        _autocompleteProvider = CreateBaseAutocompleteProvider();
        _defaultEditor.SetAutocompleteProvider(_autocompleteProvider);
        if (!ReferenceEquals(_editor, _defaultEditor)) _editor.SetAutocompleteProvider(_autocompleteProvider);
    }

    [GeneratedRegexAttribute(@"##\s+\[?(\d+\.\d+\.\d+)\]?")]
    private static partial Regex ChangelogVersion();

    private void ShowStartupNoticesIfNeeded()
    {
        if (_startupNoticesShown) return;
        _startupNoticesShown = true;
        if (string.IsNullOrEmpty(_changelogMarkdown)) return;
        if (_chatContainer.Children.Count > 0) _chatContainer.AddChild(new Spacer(1));
        _chatContainer.AddChild(new DynamicBorder());
        if (SettingsManager.CollapseChangelog)
        {
            var match = ChangelogVersion().Match(_changelogMarkdown);
            var latest = match.Success ? match.Groups[1].Value : _version;
            _chatContainer.AddChild(new Text($"Updated to v{latest}. Use {Theme.Bold("/changelog")} to view full changelog.", 1, 0));
        }
        else
        {
            _chatContainer.AddChild(new Text(Theme.Bold(Theme.Fg("accent", "What's New")), 1, 0));
            _chatContainer.AddChild(new Spacer(1));
            _chatContainer.AddChild(new MarkdownComponent(_changelogMarkdown.Trim(), 1, 0, GetMarkdownThemeWithSettings()));
            _chatContainer.AddChild(new Spacer(1));
        }
        _chatContainer.AddChild(new DynamicBorder());
    }

    // ----- Init / run -----

    public async Task InitAsync()
    {
        if (_isInitialized) return;
        RegisterSignalHandlers();
        _changelogMarkdown = GetChangelogForDisplay();

        if (Session.ScopedModels.Count > 0 && (_options.Verbose || !SettingsManager.QuietStartup))
        {
            var modelList = string.Join(", ", Session.ScopedModels.Select(sm => $"{sm.Model.Id}{(sm.ThinkingLevel is { } tl ? $":{tl.ToWire()}" : "")}"));
            var cycleKeys = _keybindings.GetKeys("app.model.cycleForward");
            var cycleHint = cycleKeys.Count > 0 ? Theme.Fg("muted", $" ({KeyHints.FormatKeyText(string.Join("/", cycleKeys), true)} to cycle)") : "";
            Console.Out.WriteLine(Theme.Fg("dim", $"Model scope: {modelList}{cycleHint}"));
        }

        RenderWidgets();
        foreach (var component in new IComponent[] { _documentContainer, _pendingMessagesContainer, _statusContainer, _widgetContainerAbove, _editorContainer, _widgetContainerBelow, _footerContainer })
        {
            _ui.AddChild(component);
        }
        _defaultEditor.OnAction("app.clear", HandleCtrlC);
        _defaultEditor.OnCtrlD = HandleCtrlD;
        _defaultEditor.OnSubmit = HandleStartupSubmit;
        _ui.SetFocus(_editor);

        _ui.Start();
        _isInitialized = true;

        await _themeController.ApplyFromSettingsAsync();

        if (_options.Verbose || !SettingsManager.QuietStartup)
        {
            string Hint(string keybinding, string description) => KeyHints.KeyHint(keybinding, description);
            string Raw(string key, string description) => KeyHints.RawKeyHint(key, description);
            string KeyText(string keybinding) => KeyHints.KeyText(keybinding);

            var expandedInstructions = string.Join("\n",
                Hint("app.interrupt", "to interrupt"),
                Hint("app.clear", "to clear"),
                Raw($"{KeyText("app.clear")} twice", "to exit"),
                Hint("app.exit", "to exit (empty)"),
                Hint("app.suspend", "to suspend"),
                Hint("tui.editor.deleteToLineEnd", "to delete to end"),
                Hint("app.thinking.cycle", "to cycle thinking level"),
                Raw($"{KeyText("app.model.cycleForward")}/{KeyText("app.model.cycleBackward")}", "to cycle models"),
                Hint("app.model.select", "to select model"),
                Hint("app.tools.expand", "to expand tools"),
                Hint("app.thinking.toggle", "to expand thinking"),
                Hint("app.editor.external", "for external editor"),
                Raw("/", "for commands"),
                Raw("!", "to run bash"),
                Raw("!!", "to run bash (no context)"),
                Hint("app.message.followUp", "to queue follow-up"),
                Hint("app.message.dequeue", "to edit all queued messages"),
                Hint("app.clipboard.pasteImage", "to paste image (with text fallback)"),
                Raw("drop files", "to attach"));
            var compactInstructions = string.Join(Theme.Fg("muted", " · "),
                Hint("app.interrupt", "interrupt"),
                Raw($"{KeyText("app.clear")}/{KeyText("app.exit")}", "clear/exit"),
                Raw("/", "commands"),
                Raw("!", "bash"),
                Hint("app.tools.expand", "more"));
            var compactOnboarding = Theme.Fg("dim", $"Press {KeyText("app.tools.expand")} to show full startup help and loaded resources.");
            var onboarding = Theme.Fg("dim", "Pi can explain its own features and look up its docs. Ask it how to use or extend Pi.");
            _builtInHeader = new ExpandableText(
                () => $"{compactInstructions}\n{compactOnboarding}\n\n{onboarding}",
                () => $"{expandedInstructions}\n\n{onboarding}",
                GetStartupExpansionState(), 1, 0);
            _headerContainer.AddChild(new Spacer(1));
            // Iris: gradient banner instead of the one-line pi logo.
            _headerContainer.AddChild(new IrisBanner(_version));
            _headerContainer.AddChild(new Spacer(1));
            _headerContainer.AddChild(_builtInHeader);
            _headerContainer.AddChild(new Spacer(1));
        }
        else
        {
            _builtInHeader = new Text("", 0, 0);
            _headerContainer.AddChild(_builtInHeader);
        }
        _ui.RequestRender();

        var fdTask = ToolsManager.EnsureToolAsync("fd", status => _dispatcher.Invoke(() => ShowManagedToolStatus(status)));
        var rgTask = ToolsManager.EnsureToolAsync("rg", status => _dispatcher.Invoke(() => ShowManagedToolStatus(status)));
        await Task.WhenAll(fdTask, rgTask);
        _fdPath = await fdTask;

        SetupKeyHandlers();
        SetupEditorSubmitHandler();
        _ui.RequestRender();

        await RebindCurrentSessionAsync();
        RenderInitialMessages();

        ThemeManager.OnThemeChange(() =>
        {
            _ui.Invalidate();
            UpdateEditorBorderColor();
            _ui.RequestRender();
        });
        _footerDataProvider.OnBranchChange(() => _ui.RequestRender());
        UpdateAvailableProviderCount();
        _ui.RenderNow();
    }

    private void UpdateTerminalTitle()
    {
        var cwdBasename = Path.GetFileName(SessionManager.Cwd.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var sessionName = SessionManager.SessionName;
        _ui.Terminal.SetTitle(!string.IsNullOrEmpty(sessionName) ? $"{AppConfig.AppTitle} - {sessionName} - {cwdBasename}" : $"{AppConfig.AppTitle} - {cwdBasename}");
    }

    /// <summary>Run interactive mode until shutdown; returns the process exit code.</summary>
    public async Task<int> RunAsync()
    {
        await InitAsync();

        if (Environment.GetEnvironmentVariable("PI_OFFLINE") is not { Length: > 0 })
        {
            _ = RefreshModelCatalogsInBackgroundAsync();
        }

        _ = CheckForNewVersionInBackgroundAsync();
        _ = CheckForPackageUpdatesInBackgroundAsync();

        foreach (var diagnostic in _options.StartupDiagnostics ?? [])
        {
            if (diagnostic.Type == "error") ShowError(diagnostic.Message);
            else if (diagnostic.Type == "warning") ShowWarning(diagnostic.Message);
            else ShowStatus(diagnostic.Message);
        }
        if (_options.MigratedProviders is { Count: > 0 } migrated) ShowWarning($"Migrated credentials to auth.json: {string.Join(", ", migrated)}");
        if (Session.ModelRuntime.GetError() is { Length: > 0 } modelsError) ShowError($"models.json error: {modelsError}");
        if (!string.IsNullOrEmpty(_options.ModelFallbackMessage)) ShowWarning(_options.ModelFallbackMessage);
        _ = MaybeWarnAboutAnthropicSubscriptionAuthAsync();

        _ = RunPromptLoopAsync();
        return await _exit.Task;
    }

    private async Task CheckForNewVersionInBackgroundAsync()
    {
        if (await VersionCheck.CheckForNewVersionAsync(_version) is { } release) ShowNewVersionNotification(release);
    }

    private async Task CheckForPackageUpdatesInBackgroundAsync()
    {
        try
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PI_OFFLINE"))) return;
            List<PackageUpdate> updates;
            try
            {
                // npm and git run as child processes; keep them off the UI thread.
                var packageManager = new PackageManager(SessionManager.Cwd, AppConfig.AgentDir, SettingsManager);
                updates = await Task.Run(packageManager.CheckForAvailableUpdatesAsync);
            }
            catch
            {
                updates = [];
            }
            if (updates.Count > 0) ShowPackageUpdateNotification(updates.Select(u => u.DisplayName).ToList());
        }
        finally
        {
            // On Windows, npm can overwrite the shared console title while checking package versions.
            if (OperatingSystem.IsWindows() && _isInitialized) UpdateTerminalTitle();
        }
    }

    public void ShowNewVersionNotification(LatestRelease release)
    {
        var action = Theme.Fg("accent", $"{AppConfig.AppName} update");
        var updateInstruction = Theme.Fg("muted", $"New version {release.Version} is available. Run ") + action;
        _chatContainer.AddChild(new Spacer(1));
        _chatContainer.AddChild(new DynamicBorder(text => Theme.Fg("warning", text)));
        _chatContainer.AddChild(new Text($"{Theme.Bold(Theme.Fg("warning", "Update Available"))}\n{updateInstruction}", 1, 0));
        if (release.Note?.Trim() is { Length: > 0 } note)
        {
            _chatContainer.AddChild(new Spacer(1));
            _chatContainer.AddChild(new MarkdownComponent(note, 1, 0, GetMarkdownThemeWithSettings(), new DefaultTextStyle { Color = text => Theme.Fg("muted", text) }));
            _chatContainer.AddChild(new Spacer(1));
        }
        _chatContainer.AddChild(new DynamicBorder(text => Theme.Fg("warning", text)));
        _ui.RequestRender();
    }

    public void ShowPackageUpdateNotification(List<string> packages)
    {
        var action = Theme.Fg("accent", $"{AppConfig.AppName} update --extensions");
        var updateInstruction = Theme.Fg("muted", "Package updates are available. Run ") + action;
        var packageLines = string.Join("\n", packages.Select(pkg => $"- {pkg}"));
        _chatContainer.AddChild(new Spacer(1));
        _chatContainer.AddChild(new DynamicBorder(text => Theme.Fg("warning", text)));
        _chatContainer.AddChild(new Text($"{Theme.Bold(Theme.Fg("warning", "Package Updates Available"))}\n{updateInstruction}\n{Theme.Fg("muted", "Packages:")}\n{packageLines}", 1, 0));
        _chatContainer.AddChild(new DynamicBorder(text => Theme.Fg("warning", text)));
        _ui.RequestRender();
    }

    private async Task RefreshModelCatalogsInBackgroundAsync()
    {
        using var cts = new CancellationTokenSource(15_000);
        try
        {
            await ModelCatalogRefresh.RefreshAsync(Session.ModelRuntime, cts.Token);
            UpdateAvailableProviderCount();
        }
        catch
        {
            // Best effort.
        }
    }

    private async Task RunPromptLoopAsync()
    {
        if (!string.IsNullOrEmpty(_options.InitialMessage))
        {
            try
            {
                await Session.PromptAsync(_options.InitialMessage, new PromptOptions { Images = _options.InitialImages });
            }
            catch (Exception ex)
            {
                ShowError(ex.Message);
            }
        }
        foreach (var message in _options.InitialMessages ?? [])
        {
            try
            {
                await Session.PromptAsync(message);
            }
            catch (Exception ex)
            {
                ShowError(ex.Message);
            }
        }
        while (!_isShuttingDown)
        {
            var userInput = await GetUserInputAsync();
            try
            {
                await Session.PromptAsync(userInput);
            }
            catch (Exception ex)
            {
                ShowError(ex.Message);
            }
        }
    }

    private string? GetChangelogForDisplay()
    {
        if (Session.State.Messages.Count > 0) return null;
        var lastVersion = SettingsManager.LastChangelogVersion;
        var entries = Changelog.Parse(AppConfig.ChangelogPath);
        if (string.IsNullOrEmpty(lastVersion))
        {
            SettingsManager.SetLastChangelogVersion(_version);
            return null;
        }
        var newEntries = Changelog.GetNewEntries(entries, lastVersion);
        if (newEntries.Count == 0) return null;
        SettingsManager.SetLastChangelogVersion(_version);
        return string.Join("\n\n", newEntries.Select(e => Changelog.NormalizeLinks(e.Content, e)));
    }

    private MarkdownTheme GetMarkdownThemeWithSettings()
    {
        var baseTheme = ThemeManager.GetMarkdownTheme();
        return new MarkdownTheme
        {
            Heading = baseTheme.Heading, Link = baseTheme.Link, LinkUrl = baseTheme.LinkUrl, Code = baseTheme.Code, CodeBlock = baseTheme.CodeBlock,
            CodeBlockBorder = baseTheme.CodeBlockBorder, Quote = baseTheme.Quote, QuoteBorder = baseTheme.QuoteBorder, Hr = baseTheme.Hr,
            ListBullet = baseTheme.ListBullet, Bold = baseTheme.Bold, Italic = baseTheme.Italic, Strikethrough = baseTheme.Strikethrough,
            Underline = baseTheme.Underline, HighlightCode = baseTheme.HighlightCode, CodeBlockIndent = SettingsManager.CodeBlockIndent,
        };
    }

    private static string FormatDisplayPath(string p)
    {
        var home = AppConfig.HomeDir;
        return p.StartsWith(home, StringComparison.Ordinal) ? "~" + p[home.Length..] : p;
    }

    private string FormatContextPath(string p)
    {
        var cwd = Path.GetFullPath(SessionManager.Cwd);
        var absolute = Path.IsPathRooted(p) ? Path.GetFullPath(p) : Path.GetFullPath(Path.Combine(cwd, p));
        return PathUtils.GetCwdRelativePath(absolute, cwd) ?? FormatDisplayPath(absolute);
    }

    private bool GetStartupExpansionState() => _options.Verbose || _toolOutputExpanded;

    private static bool IsPackageSource(SourceInfo? sourceInfo) => sourceInfo?.Source is { } s && (s.StartsWith("npm:", StringComparison.Ordinal) || s.StartsWith("git:", StringComparison.Ordinal));

    private string GetShortPath(string fullPath, SourceInfo? sourceInfo)
    {
        var normalized = fullPath.Replace('\\', '/');
        if (sourceInfo?.BaseDir is { } baseDir && IsPackageSource(sourceInfo))
        {
            var relative = Path.GetRelativePath(Path.GetFullPath(baseDir), Path.GetFullPath(fullPath));
            if (relative.Length > 0 && relative != "." && !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative)) return relative.Replace('\\', '/');
        }
        var source = sourceInfo?.Source ?? "";
        var npm = Regex.Match(normalized, "node_modules/(@?[^/]+(?:/[^/]+)?)/(.*)");
        if (npm.Success && source.StartsWith("npm:", StringComparison.Ordinal)) return npm.Groups[2].Value;
        var git = Regex.Match(normalized, "git/[^/]+/[^/]+/(.*)");
        if (git.Success && source.StartsWith("git:", StringComparison.Ordinal)) return git.Groups[1].Value;
        return FormatDisplayPath(fullPath);
    }

    private string GetCompactPathLabel(string resourcePath, SourceInfo? sourceInfo)
    {
        var shortPath = GetShortPath(resourcePath, sourceInfo);
        var segments = shortPath.Replace('\\', '/').Split('/').Where(s => s.Length > 0 && s != "~").ToList();
        return segments.Count > 0 ? segments[^1] : shortPath;
    }

    private static string GetScopeGroup(SourceInfo? sourceInfo)
    {
        var source = sourceInfo?.Source ?? "local";
        var scope = sourceInfo?.Scope ?? "project";
        if (source == "cli" || scope == "temporary") return "path";
        return scope is "user" or "project" ? scope : "path";
    }

    private sealed record ScopeGroup(string Scope, List<(string Path, SourceInfo? SourceInfo)> Paths, SortedDictionary<string, List<(string Path, SourceInfo? SourceInfo)>> Packages);

    private static List<ScopeGroup> BuildScopeGroups(IEnumerable<(string Path, SourceInfo? SourceInfo)> items)
    {
        var comparer = Comparer<string>.Create(NodeCompare.LocaleCompare);
        var groups = new Dictionary<string, ScopeGroup>
        {
            ["user"] = new("user", [], new(comparer)),
            ["project"] = new("project", [], new(comparer)),
            ["path"] = new("path", [], new(comparer)),
        };
        foreach (var item in items)
        {
            var group = groups[GetScopeGroup(item.SourceInfo)];
            var source = item.SourceInfo?.Source ?? "local";
            if (IsPackageSource(item.SourceInfo))
            {
                if (!group.Packages.TryGetValue(source, out var list)) group.Packages[source] = list = [];
                list.Add(item);
            }
            else
            {
                group.Paths.Add(item);
            }
        }
        return new[] { groups["project"], groups["user"], groups["path"] }.Where(g => g.Paths.Count > 0 || g.Packages.Count > 0).ToList();
    }

    private static string FormatScopeGroups(List<ScopeGroup> groups, Func<(string Path, SourceInfo? SourceInfo), string> formatPath, Func<(string Path, SourceInfo? SourceInfo), string, string> formatPackagePath)
    {
        var lines = new List<string>();
        foreach (var group in groups)
        {
            lines.Add($"  {Theme.Fg("accent", group.Scope)}");
            foreach (var item in group.Paths.OrderBy(i => i.Path, Comparer<string>.Create(NodeCompare.LocaleCompare))) lines.Add(Theme.Fg("dim", $"    {formatPath(item)}"));
            foreach (var (source, items) in group.Packages)
            {
                lines.Add($"    {Theme.Fg("mdLink", source)}");
                foreach (var item in items.OrderBy(i => i.Path, Comparer<string>.Create(NodeCompare.LocaleCompare))) lines.Add(Theme.Fg("dim", $"      {formatPackagePath(item, source)}"));
            }
        }
        return string.Join("\n", lines);
    }

    private static SourceInfo? FindSourceInfoForPath(string p, Dictionary<string, SourceInfo> sourceInfos)
    {
        if (sourceInfos.TryGetValue(p, out var exact)) return exact;
        var current = p;
        while (current.Contains('/'))
        {
            current = current[..current.LastIndexOf('/')];
            if (sourceInfos.TryGetValue(current, out var parent)) return parent;
        }
        return null;
    }

    private string FormatPathWithSource(string p, SourceInfo? sourceInfo)
    {
        if (sourceInfo is null) return FormatDisplayPath(p);
        var shortPath = GetShortPath(p, sourceInfo);
        var source = sourceInfo.Source;
        var scope = sourceInfo.Scope;
        string label;
        string? scopeLabel = null;
        if (source == "local")
        {
            label = scope switch { "user" => "user", "project" => "project", _ => "path" };
            if (scope == "temporary") scopeLabel = "temp";
        }
        else if (source == "cli")
        {
            label = "path";
            if (scope == "temporary") scopeLabel = "temp";
        }
        else
        {
            label = source;
            scopeLabel = scope switch { "user" => "user", "project" => "project", "temporary" => "temp", _ => null };
        }
        return $"{(scopeLabel is not null ? $"{label} ({scopeLabel})" : label)} {shortPath}";
    }

    private string FormatDiagnostics(IEnumerable<ResourceDiagnostic> diagnostics, Dictionary<string, SourceInfo> sourceInfos)
    {
        var lines = new List<string>();
        var collisions = new Dictionary<string, List<ResourceDiagnostic>>();
        var others = new List<ResourceDiagnostic>();
        foreach (var d in diagnostics)
        {
            if (d.Type == "collision" && d.Collision is not null)
            {
                if (!collisions.TryGetValue(d.Collision.Name, out var list)) collisions[d.Collision.Name] = list = [];
                list.Add(d);
            }
            else
            {
                others.Add(d);
            }
        }
        foreach (var (name, list) in collisions)
        {
            var first = list[0].Collision!;
            lines.Add(Theme.Fg("warning", $"  \"{name}\" collision:"));
            lines.Add(Theme.Fg("dim", $"    {Theme.Fg("success", "✓")} {FormatPathWithSource(first.WinnerPath, FindSourceInfoForPath(first.WinnerPath, sourceInfos))}"));
            foreach (var d in list)
            {
                lines.Add(Theme.Fg("dim", $"    {Theme.Fg("warning", "✗")} {FormatPathWithSource(d.Collision!.LoserPath, FindSourceInfoForPath(d.Collision.LoserPath, sourceInfos))} (skipped)"));
            }
        }
        foreach (var d in others)
        {
            var color = d.Type == "error" ? "error" : "warning";
            if (d.Path is not null)
            {
                lines.Add(Theme.Fg(color, $"  {FormatPathWithSource(d.Path, FindSourceInfoForPath(d.Path, sourceInfos))}"));
                lines.Add(Theme.Fg(color, $"    {d.Message}"));
            }
            else
            {
                lines.Add(Theme.Fg(color, $"  {d.Message}"));
            }
        }
        return string.Join("\n", lines);
    }

    private void ShowLoadedResources(bool force = false, bool showDiagnosticsWhenQuiet = false)
    {
        _loadedResourcesContainer.Clear();
        var showListing = force || _options.Verbose || !SettingsManager.QuietStartup;
        var showDiagnostics = showListing || showDiagnosticsWhenQuiet;
        if (!showListing && !showDiagnostics) return;

        string SectionHeader(string name, string color = "mdHeading") => Theme.Fg(color, $"[{name}]");
        string CompactList(IEnumerable<string> items, bool sort = true)
        {
            var labels = items.Select(i => i.Trim()).Where(i => i.Length > 0).ToList();
            if (sort) labels.Sort(NodeCompare.LocaleCompare);
            return Theme.Fg("dim", $"  {string.Join(", ", labels)}");
        }
        void AddSection(string name, string collapsedBody, string? expandedBody = null)
        {
            _loadedResourcesContainer.AddChild(new ExpandableText(() => $"{SectionHeader(name)}\n{collapsedBody}", () => $"{SectionHeader(name)}\n{expandedBody ?? collapsedBody}", GetStartupExpansionState()));
            _loadedResourcesContainer.AddChild(new Spacer(1));
        }

        var skillsResult = Session.ResourceLoader.GetSkills();
        var promptsResult = Session.ResourceLoader.GetPrompts();
        var themesResult = Session.ResourceLoader.GetThemes();
        var sourceInfos = new Dictionary<string, SourceInfo>();
        foreach (var skill in skillsResult.Skills) sourceInfos[skill.FilePath] = skill.SourceInfo;
        foreach (var prompt in promptsResult.Prompts) sourceInfos[prompt.FilePath] = prompt.SourceInfo;
        foreach (var t in themesResult.Themes)
        {
            if (t.SourceInfo is not null) sourceInfos[t.SourcePath] = t.SourceInfo;
        }

        if (showListing)
        {
            var contextFiles = new List<string>();
            if (Session.ResourceLoader.GetSystemPromptSource() is { } systemPromptSource) contextFiles.Add(systemPromptSource);
            contextFiles.AddRange(Session.ResourceLoader.GetAppendSystemPromptSources());
            contextFiles.AddRange(Session.ResourceLoader.GetAgentsFiles().Select(f => f.Path));
            if (contextFiles.Count > 0)
            {
                _loadedResourcesContainer.AddChild(new Spacer(1));
                AddSection("Context", CompactList(contextFiles.Select(FormatContextPath), sort: false), string.Join("\n", contextFiles.Select(f => Theme.Fg("dim", $"  {FormatDisplayPath(f)}"))));
            }

            if (skillsResult.Skills.Count > 0)
            {
                var groups = BuildScopeGroups(skillsResult.Skills.Select(s => (s.FilePath, (SourceInfo?)s.SourceInfo)));
                AddSection("Skills", CompactList(skillsResult.Skills.Select(s => s.Name)), FormatScopeGroups(groups, i => FormatDisplayPath(i.Path), (i, _) => GetShortPath(i.Path, i.SourceInfo)));
            }

            var templates = Session.PromptTemplates;
            if (templates.Count > 0)
            {
                var byPath = templates.GroupBy(t => t.FilePath).ToDictionary(g => g.Key, g => g.First());
                var groups = BuildScopeGroups(templates.Select(t => (t.FilePath, (SourceInfo?)t.SourceInfo)));
                string Format((string Path, SourceInfo? SourceInfo) item) => byPath.TryGetValue(item.Path, out var t) ? $"/{t.Name}" : FormatDisplayPath(item.Path);
                AddSection("Prompts", CompactList(templates.Select(t => $"/{t.Name}")), FormatScopeGroups(groups, Format, (i, _) => Format(i)));
            }

            var customThemes = themesResult.Themes.ToList();
            if (customThemes.Count > 0)
            {
                var groups = BuildScopeGroups(customThemes.Select(t => (t.SourcePath, t.SourceInfo)));
                AddSection("Themes", CompactList(customThemes.Select(t => t.Name ?? GetCompactPathLabel(t.SourcePath, t.SourceInfo))), FormatScopeGroups(groups, i => FormatDisplayPath(i.Path), (i, _) => GetShortPath(i.Path, i.SourceInfo)));
            }
        }

        if (showDiagnostics)
        {
            void AddDiagnostics(string title, IReadOnlyList<ResourceDiagnostic> diagnostics)
            {
                if (diagnostics.Count == 0) return;
                _loadedResourcesContainer.AddChild(new Text($"{Theme.Fg("warning", title)}\n{FormatDiagnostics(diagnostics, sourceInfos)}", 0, 0));
                _loadedResourcesContainer.AddChild(new Spacer(1));
            }
            AddDiagnostics("[Skill conflicts]", skillsResult.Diagnostics);
            AddDiagnostics("[Prompt conflicts]", promptsResult.Diagnostics);
            AddDiagnostics("[Theme conflicts]", themesResult.Diagnostics);
        }
    }

    private async Task BindCurrentSessionExtensionsAsync()
    {
        await Session.BindExtensionsAsync(error => _dispatcher.Invoke(() => ShowExtensionError(error.ExtensionPath, error.Error, null)));
        ThemeManager.SetRegisteredThemes(Session.ResourceLoader.GetThemes().Themes.Select(ThemeManager.CreateThemeFromResource));
        SetupAutocompleteProvider();
        ShowLoadedResources(false, true);
        ShowStartupNoticesIfNeeded();
    }

    private void ApplyRuntimeSettings()
    {
        ApplyCapabilityOverrides();
        _footer.SetSession(Session);
        _footer.SetAutoCompactEnabled(Session.AutoCompactionEnabled);
        _footerDataProvider.SetCwd(SessionManager.Cwd);
        _hideThinkingBlock = SettingsManager.HideThinkingBlock;
        _outputPad = SettingsManager.OutputPad;
        _ui.SetShowHardwareCursor(SettingsManager.ShowHardwareCursor);
        var clearOnShrink = SettingsManager.ClearOnShrink;
        _ui.SetClearOnShrink(clearOnShrink);
        if (!clearOnShrink && _activeStatusIndicator is null) _statusContainer.Clear();
        _defaultEditor.SetPaddingX(SettingsManager.EditorPaddingX);
        _defaultEditor.SetAutocompleteMaxVisible(SettingsManager.AutocompleteMaxVisible);
        if (!ReferenceEquals(_editor, _defaultEditor))
        {
            _editor.SetPaddingX(SettingsManager.EditorPaddingX);
            _editor.SetAutocompleteMaxVisible(SettingsManager.AutocompleteMaxVisible);
        }
    }

    private async Task RebindCurrentSessionAsync(bool renderBeforeBind = false)
    {
        var session = Session;
        _unsubscribe?.Dispose();
        _unsubscribe = null;
        ApplyRuntimeSettings();
        if (renderBeforeBind)
        {
            RenderCurrentSessionState();
            SubscribeToAgent();
        }
        await BindCurrentSessionExtensionsAsync();
        if (!ReferenceEquals(Session, session)) return;
        if (!renderBeforeBind) SubscribeToAgent();
        UpdateAvailableProviderCount();
        UpdateEditorBorderColor();
        UpdateTerminalTitle();
    }

    private void HandleFatalRuntimeError(string prefix, Exception error)
    {
        ShowError($"{prefix}: {error.Message}");
        ThemeManager.StopThemeWatcher();
        Stop();
        FinishProcess(1);
    }

    private void RenderCurrentSessionState()
    {
        _loadedResourcesContainer.Clear();
        _chatContainer.Clear();
        _pendingMessagesContainer.Clear();
        _compactionQueuedMessages = [];
        _streamingComponent = null;
        _streamingMessage = null;
        _pendingTools.Clear();
        RenderInitialMessages();
    }

    private ToolRenderers? GetRegisteredToolDefinition(string toolName) =>
        BuiltInToolRenderers.WithBuiltInRenderers(toolName, Session.GetToolDefinition(toolName)?.Renderers as ToolRenderers);

    private IReadOnlyList<MarkdownTransformer> GetMarkdownTransformers() => [];

    // ----- Status indicators -----

    private bool SetEditorWorkingStatusIndicator(StatusIndicator? indicator)
    {
        _defaultEditor.SetWorkingStatusIndicator(null);
        if (_editor is not CustomEditor { EmbedWorkingStatus: true } custom) return false;
        custom.SetWorkingStatusIndicator(indicator);
        return true;
    }

    private void ShowStatusIndicator(StatusIndicator indicator)
    {
        _activeStatusIndicator?.Dispose();
        _activeStatusIndicator = indicator;
        _activeWorkingIndicatorEmbedded = false;
        _statusContainer.Clear();
        SetEditorWorkingStatusIndicator(null);
        if (SetEditorWorkingStatusIndicator(indicator))
        {
            _activeWorkingIndicatorEmbedded = true;
            return;
        }
        _statusContainer.AddChild(indicator);
    }

    private void ClearStatusIndicator(string? kind = null)
    {
        if (kind is not null && _activeStatusIndicator?.Kind != kind) return;
        var cleared = _activeStatusIndicator;
        var wasEmbedded = _activeWorkingIndicatorEmbedded;
        cleared?.Dispose();
        _activeStatusIndicator = null;
        _activeWorkingIndicatorEmbedded = false;
        _statusContainer.Clear();
        SetEditorWorkingStatusIndicator(null);
        if (cleared is not null && !wasEmbedded && _ui.GetClearOnShrink()) _statusContainer.AddChild(_idleStatus);
    }

    private void ShowWorkingStatusIndicator()
    {
        Func<string, string>? colorFn = _editor is CustomEditor { EmbedWorkingStatus: true } custom
            ? text => (custom.BorderColor ?? Theme.GetThinkingBorderColor(Session.ThinkingLevel))(text)
            : null;
        ShowStatusIndicator(new WorkingStatusIndicator(_ui, _workingMessage ?? DefaultWorkingMessage, _workingIndicatorOptions, colorFn));
    }

    private void SetHiddenThinkingLabel(string? label = null)
    {
        _hiddenThinkingLabel = label ?? DefaultHiddenThinkingLabel;
        foreach (var child in _chatContainer.Children.OfType<AssistantMessageComponent>()) child.SetHiddenThinkingLabel(_hiddenThinkingLabel);
        _streamingComponent?.SetHiddenThinkingLabel(_hiddenThinkingLabel);
        _ui.RequestRender();
    }

    private void RenderWidgets()
    {
        _widgetContainerAbove.Clear();
        _widgetContainerAbove.AddChild(new Spacer(1));
        _widgetContainerBelow.Clear();
        _ui.RequestRender();
    }

    private void ResetExtensionUI()
    {
        if (_extensionSelector is not null) HideExtensionSelector();
        if (_extensionInput is not null) HideExtensionInput();
        if (_extensionEditor is not null) HideExtensionEditor();
        _ui.HideOverlay();
        _footerDataProvider.ClearExtensionStatuses();
        _footer.Invalidate();
        SetupAutocompleteProvider();
        _defaultEditor.OnExtensionShortcut = null;
        UpdateTerminalTitle();
        _workingMessage = null;
        _workingVisible = true;
        _workingIndicatorOptions = null;
        if (_activeStatusIndicator?.Kind == "working")
        {
            _activeStatusIndicator.SetIndicator(null);
            _activeStatusIndicator.SetMessage($"{DefaultWorkingMessage} ({KeyHints.KeyText("app.interrupt")} to interrupt)");
        }
        SetHiddenThinkingLabel();
    }

    private void ShowExtensionError(string extensionPath, string error, string? stack)
    {
        _chatContainer.AddChild(new Text(Theme.Fg("error", $"Extension \"{extensionPath}\" error: {error}"), 1, 0));
        if (!string.IsNullOrEmpty(stack))
        {
            var stackLines = string.Join("\n", stack.Split('\n').Skip(1).Select(line => Theme.Fg("dim", $"  {line.Trim()}")));
            if (stackLines.Length > 0) _chatContainer.AddChild(new Text(stackLines, 1, 0));
        }
        _ui.RequestRender();
    }

    // ----- Dialogs -----

    private Task<string?> ShowExtensionSelectorAsync(string title, List<string> options)
    {
        var tcs = new TaskCompletionSource<string?>();
        _extensionSelector = new ExtensionSelectorComponent(title, options,
            option =>
            {
                HideExtensionSelector();
                tcs.TrySetResult(option);
            },
            () =>
            {
                HideExtensionSelector();
                tcs.TrySetResult(null);
            },
            _ui, null, ToggleToolOutputExpansion);
        DisposeActiveSelector();
        _editorContainer.Clear();
        _editorContainer.AddChild(_extensionSelector);
        _ui.SetFocus(_extensionSelector);
        _ui.RequestRender();
        return tcs.Task;
    }

    private void HideExtensionSelector()
    {
        _extensionSelector?.Dispose();
        _editorContainer.Clear();
        _editorContainer.AddChild(_editor);
        _extensionSelector = null;
        _ui.SetFocus(_editor);
        _ui.RequestRender();
    }

    private async Task<bool> ShowExtensionConfirmAsync(string title, string message) =>
        await ShowExtensionSelectorAsync($"{title}\n{message}", ["Yes", "No"]) == "Yes";

    /// <summary>Project trust prompt used for runtimes created while interactive mode is running. Callable from any thread.</summary>
    public async Task<ProjectTrustOption?> PromptProjectTrustAsync(string title, List<ProjectTrustOption> options)
    {
        ProjectTrustOption? selected = null;
        await _dispatcher.InvokeAsync(async () =>
        {
            var label = await ShowExtensionSelectorAsync(title, options.Select(o => o.Label).ToList());
            selected = options.FirstOrDefault(o => o.Label == label);
        });
        return selected;
    }

    private async Task<string?> PromptForMissingSessionCwdAsync(MissingSessionCwdException error) =>
        await ShowExtensionConfirmAsync("Session cwd not found", error.Issue.PromptMessage) ? error.Issue.FallbackCwd : null;

    private Task<string?> ShowExtensionInputAsync(string title)
    {
        var tcs = new TaskCompletionSource<string?>();
        _extensionInput = new ExtensionInputComponent(title,
            value =>
            {
                HideExtensionInput();
                tcs.TrySetResult(value);
            },
            () =>
            {
                HideExtensionInput();
                tcs.TrySetResult(null);
            },
            _ui);
        DisposeActiveSelector();
        _editorContainer.Clear();
        _editorContainer.AddChild(_extensionInput);
        _ui.SetFocus(_extensionInput);
        _ui.RequestRender();
        return tcs.Task;
    }

    private void HideExtensionInput()
    {
        _extensionInput?.Dispose();
        _editorContainer.Clear();
        _editorContainer.AddChild(_editor);
        _extensionInput = null;
        _ui.SetFocus(_editor);
        _ui.RequestRender();
    }

    private Task<string?> ShowExtensionEditorAsync(string title, string? prefill = null)
    {
        var tcs = new TaskCompletionSource<string?>();
        _extensionEditor = new ExtensionEditorComponent(_ui, _keybindings, title, prefill,
            value =>
            {
                HideExtensionEditor();
                tcs.TrySetResult(value);
            },
            () =>
            {
                HideExtensionEditor();
                tcs.TrySetResult(null);
            },
            null, SettingsManager.ExternalEditorCommand);
        DisposeActiveSelector();
        _editorContainer.Clear();
        _editorContainer.AddChild(_extensionEditor);
        _ui.SetFocus(_extensionEditor);
        _ui.RequestRender();
        return tcs.Task;
    }

    private void HideExtensionEditor()
    {
        _editorContainer.Clear();
        _editorContainer.AddChild(_editor);
        _extensionEditor = null;
        _ui.SetFocus(_editor);
        _ui.RequestRender();
    }

    // ----- Key handlers -----

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private void SetupKeyHandlers()
    {
        _defaultEditor.OnEscape = () =>
        {
            if (Session.IsStreaming)
            {
                RestoreQueuedMessagesToEditor(abort: true);
            }
            else if (Session.IsBashRunning)
            {
                Session.AbortBash();
            }
            else if (_isBashMode)
            {
                _editor.SetText("");
                _isBashMode = false;
                UpdateEditorBorderColor();
            }
            else if (_editor.GetText().Trim().Length == 0)
            {
                var action = SettingsManager.DoubleEscapeAction;
                if (action == "none") return;
                var now = NowMs();
                if (now - _lastEscapeTime < 500)
                {
                    if (action == "tree") ShowTreeSelector();
                    else ShowUserMessageSelector();
                    _lastEscapeTime = 0;
                }
                else
                {
                    _lastEscapeTime = now;
                }
            }
        };

        _defaultEditor.OnAction("app.clear", HandleCtrlC);
        _defaultEditor.OnCtrlD = HandleCtrlD;
        _defaultEditor.OnAction("app.suspend", HandleCtrlZ);
        _defaultEditor.OnAction("app.thinking.cycle", CycleThinkingLevel);
        _defaultEditor.OnAction("app.model.cycleForward", () => _ = CycleModelAsync(true));
        _defaultEditor.OnAction("app.model.cycleBackward", () => _ = CycleModelAsync(false));
        _ui.OnDebug = HandleDebugCommand;
        _defaultEditor.OnAction("app.model.select", () => ShowModelSelector());
        _defaultEditor.OnAction("app.tools.expand", ToggleToolOutputExpansion);
        _defaultEditor.OnAction("app.thinking.toggle", ToggleThinkingBlockVisibility);
        _defaultEditor.OnAction("app.editor.external", () => _ = HandleOpenExternalEditorAsync());
        _defaultEditor.OnAction("app.message.copy", () => _ = HandleCopyCommandAsync());
        _defaultEditor.OnAction("app.message.followUp", () => _ = HandleFollowUpAsync());
        _defaultEditor.OnAction("app.message.dequeue", HandleDequeue);
        _defaultEditor.OnAction("app.session.new", () => _ = HandleClearCommandAsync());
        _defaultEditor.OnAction("app.session.tree", () => ShowTreeSelector());
        _defaultEditor.OnAction("app.session.fork", ShowUserMessageSelector);
        _defaultEditor.OnAction("app.session.resume", ShowSessionSelector);

        _defaultEditor.OnChange = text =>
        {
            var wasBashMode = _isBashMode;
            _isBashMode = text.TrimStart().StartsWith('!');
            if (wasBashMode != _isBashMode) UpdateEditorBorderColor();
        };
        _defaultEditor.OnPasteImage = () => _ = HandleClipboardPasteAsync();
    }

    private async Task HandleClipboardPasteAsync()
    {
        try
        {
            if (await Clipboard.ReadImageAsync() is { } image)
            {
                var ext = Clipboard.ExtensionForImageMimeType(image.MimeType) ?? "png";
                var filePath = Path.Combine(Path.GetTempPath(), $"pi-clipboard-{Guid.NewGuid()}.{ext}");
                await File.WriteAllBytesAsync(filePath, image.Bytes);
                _editor.InsertTextAtCursor(filePath);
                _ui.RequestRender();
                return;
            }
            if (await Clipboard.ReadTextAsync() is { Length: > 0 } text)
            {
                _editor.InsertTextAtCursor(text);
                _ui.RequestRender();
            }
        }
        catch
        {
            // Ignore clipboard errors.
        }
    }

    private void HandleStartupSubmit(string text)
    {
        _editor.SetText(text);
        ShowStatus("Startup is still in progress");
    }

    private void SubscribeToAgent() =>
        _unsubscribe = Session.Subscribe(evt => _dispatcher.Invoke(() => _ = HandleEventAsync(evt)));

    private async Task HandleEventAsync(AgentSessionEvent evt)
    {
        if (!_isInitialized) await InitAsync();
        _footer.Invalidate();
        var agentEvent = (evt as AgentCoreSessionEvent)?.Event;

        switch (evt.Type)
        {
            case "agent_start":
                _pendingTools.Clear();
                if (_retryEscapeHandler is not null)
                {
                    _defaultEditor.OnEscape = _retryEscapeHandler;
                    _retryEscapeHandler = null;
                }
                break;

            case "turn_start":
                if (SettingsManager.ShowTerminalProgress) _ui.Terminal.SetProgress(true);
                if (_workingVisible)
                {
                    if (_activeStatusIndicator?.Kind != "working") ShowWorkingStatusIndicator();
                }
                else
                {
                    ClearStatusIndicator();
                }
                _ui.RequestRender();
                break;

            case "queue_update":
                UpdatePendingMessagesDisplay();
                _ui.RequestRender();
                break;

            case "session_info_changed":
                UpdateTerminalTitle();
                _footer.Invalidate();
                _ui.RequestRender();
                break;

            case "thinking_level_changed":
                _footer.Invalidate();
                UpdateEditorBorderColor();
                break;

            case "message_start" when agentEvent is MessageStartEvent start:
                _footer.TokenRate.Handle(start);
                if (start.Message is CustomMessage)
                {
                    AddMessageToChat(start.Message);
                    _ui.RequestRender();
                }
                else if (start.Message is UserMessage)
                {
                    AddMessageToChat(start.Message);
                    UpdatePendingMessagesDisplay();
                    _ui.RequestRender();
                }
                else if (start.Message is AssistantMessage assistant)
                {
                    _streamingComponent = new AssistantMessageComponent(null, _hideThinkingBlock, GetMarkdownThemeWithSettings(), _hiddenThinkingLabel, _outputPad, GetMarkdownTransformers());
                    _streamingMessage = assistant;
                    _chatContainer.AddChild(_streamingComponent);
                    _streamingComponent.UpdateContent(assistant, true);
                    _ui.RequestRender();
                }
                break;

            case "message_update" when agentEvent is MessageUpdateEvent { Message: AssistantMessage updated }:
                _footer.TokenRate.Handle(agentEvent);
                if (_streamingComponent is null) break;
                _ = RefreshLlamaModelMetadataAsync();
                _streamingMessage = updated;
                _streamingComponent.UpdateContent(updated, true);
                foreach (var toolCall in updated.Content.OfType<ToolCall>())
                {
                    if (!_pendingTools.TryGetValue(toolCall.Id, out var component))
                    {
                        component = new ToolExecutionComponent(toolCall.Name, toolCall.Id, toolCall.Arguments, SettingsManager.ShowImages, SettingsManager.ImageWidthCells, GetRegisteredToolDefinition(toolCall.Name), _ui, SessionManager.Cwd);
                        component.SetExpanded(_toolOutputExpanded);
                        _chatContainer.AddChild(component);
                        _pendingTools[toolCall.Id] = component;
                    }
                    else
                    {
                        component.UpdateArgs(toolCall.Arguments);
                    }
                }
                _ui.RequestRender();
                break;

            case "message_end" when agentEvent is MessageEndEvent end:
                _footer.TokenRate.Handle(end);
                if (end.Message is UserMessage) break;
                if (_streamingComponent is not null && end.Message is AssistantMessage finished)
                {
                    _streamingMessage = finished;
                    string? errorMessage = null;
                    if (finished.StopReason == StopReason.Aborted)
                    {
                        var retryAttempt = Session.RetryAttempt;
                        errorMessage = retryAttempt > 0 ? $"Aborted after {retryAttempt} retry attempt{(retryAttempt > 1 ? "s" : "")}" : "Operation aborted";
                        finished.ErrorMessage = errorMessage;
                    }
                    _streamingComponent.UpdateContent(finished, false);
                    if (finished.StopReason is StopReason.Aborted or StopReason.Error)
                    {
                        errorMessage ??= string.IsNullOrEmpty(finished.ErrorMessage) ? "Error" : finished.ErrorMessage;
                        foreach (var component in _pendingTools.Values) component.UpdateResult(new AgentToolResult { Content = [new TextContent(errorMessage)] }, true);
                        _pendingTools.Clear();
                    }
                    else
                    {
                        foreach (var component in _pendingTools.Values) component.SetArgsComplete();
                        MaybeShowAssistantDiagnostics(finished);
                        MaybeShowCacheMissNotice(finished);
                    }
                    _streamingComponent = null;
                    _streamingMessage = null;
                    _footer.Invalidate();
                }
                _ui.RequestRender();
                break;

            case "tool_execution_start" when agentEvent is ToolExecutionStartEvent toolStart:
            {
                if (!_pendingTools.TryGetValue(toolStart.ToolCallId, out var component))
                {
                    component = new ToolExecutionComponent(toolStart.ToolName, toolStart.ToolCallId, toolStart.Args, SettingsManager.ShowImages, SettingsManager.ImageWidthCells, GetRegisteredToolDefinition(toolStart.ToolName), _ui, SessionManager.Cwd);
                    component.SetExpanded(_toolOutputExpanded);
                    _chatContainer.AddChild(component);
                    _pendingTools[toolStart.ToolCallId] = component;
                }
                component.MarkExecutionStarted();
                _ui.RequestRender();
                break;
            }

            case "tool_execution_update" when agentEvent is ToolExecutionUpdateEvent toolUpdate:
                if (_pendingTools.TryGetValue(toolUpdate.ToolCallId, out var updating))
                {
                    updating.UpdateResult(toolUpdate.PartialResult, false, true);
                    _ui.RequestRender();
                }
                break;

            case "tool_execution_end" when agentEvent is ToolExecutionEndEvent toolEnd:
                if (_pendingTools.TryGetValue(toolEnd.ToolCallId, out var ending))
                {
                    ending.UpdateResult(toolEnd.Result, toolEnd.IsError);
                    _pendingTools.Remove(toolEnd.ToolCallId);
                    _ui.RequestRender();
                }
                break;

            case "agent_end":
                if (SettingsManager.ShowTerminalProgress) _ui.Terminal.SetProgress(false);
                ClearStatusIndicator("working");
                if (_streamingComponent is not null)
                {
                    _chatContainer.RemoveChild(_streamingComponent);
                    _streamingComponent = null;
                    _streamingMessage = null;
                }
                _pendingTools.Clear();
                _ui.RequestRender();
                break;

            case "agent_settled":
                await CheckShutdownRequestedAsync();
                break;

            case "compaction_start" when evt is CompactionStartEvent compactionStart:
                if (SettingsManager.ShowTerminalProgress) _ui.Terminal.SetProgress(true);
                _autoCompactionEscapeHandler = _defaultEditor.OnEscape;
                _defaultEditor.OnEscape = () => Session.AbortCompaction();
                ShowStatusIndicator(new CompactionStatusIndicator(_ui, compactionStart.Reason));
                _ui.RequestRender();
                break;

            case "compaction_end" when evt is CompactionEndEvent compactionEnd:
                if (SettingsManager.ShowTerminalProgress) _ui.Terminal.SetProgress(false);
                if (_autoCompactionEscapeHandler is not null)
                {
                    _defaultEditor.OnEscape = _autoCompactionEscapeHandler;
                    _autoCompactionEscapeHandler = null;
                }
                ClearStatusIndicator("compaction");
                if (compactionEnd.Aborted)
                {
                    if (compactionEnd.Reason == "manual") ShowError("Compaction cancelled");
                    else ShowStatus("Auto-compaction cancelled");
                }
                else if (compactionEnd.Result is { } result)
                {
                    var entries = SessionManager.BuildContextEntries();
                    if (entries.Count == 0 || entries[0] is not CompactionEntry) throw new InvalidOperationException("Completed compaction is missing from the session context");
                    _chatContainer.Clear();
                    RenderSessionEntries(entries.Skip(1).ToList());
                    AddMessageToChat(new CompactionSummaryMessage { Summary = result.Summary, TokensBefore = result.TokensBefore, Timestamp = NowMs() });
                    if (result.Usage is { } usage) AddCompactionCostNotice("compaction", usage);
                    _footer.Invalidate();
                }
                else if (!string.IsNullOrEmpty(compactionEnd.ErrorMessage))
                {
                    if (compactionEnd.Reason == "manual")
                    {
                        ShowError(compactionEnd.ErrorMessage);
                    }
                    else
                    {
                        _chatContainer.AddChild(new Spacer(1));
                        _chatContainer.AddChild(new Text(Theme.Fg("error", compactionEnd.ErrorMessage), 1, 0));
                    }
                }
                _ = FlushCompactionQueueAsync(compactionEnd.WillRetry);
                _ui.RequestRender();
                break;

            case "auto_retry_start" when evt is AutoRetryStartEvent retryStart:
                _retryEscapeHandler = _defaultEditor.OnEscape;
                _defaultEditor.OnEscape = () => Session.AbortRetry();
                ShowStatusIndicator(new RetryStatusIndicator(_ui, retryStart.Attempt, retryStart.MaxAttempts, (int)retryStart.DelayMs));
                _ui.RequestRender();
                break;

            case "auto_retry_end" when evt is AutoRetryEndEvent retryEnd:
                if (_retryEscapeHandler is not null)
                {
                    _defaultEditor.OnEscape = _retryEscapeHandler;
                    _retryEscapeHandler = null;
                }
                ClearStatusIndicator("retry");
                if (!retryEnd.Success) ShowError($"Retry failed after {retryEnd.Attempt} attempts: {(string.IsNullOrEmpty(retryEnd.FinalError) ? "Unknown error" : retryEnd.FinalError)}");
                _ui.RequestRender();
                break;

            case "summarization_retry_scheduled" when evt is SummarizationRetryScheduledEvent scheduled:
                ShowError(scheduled.ErrorMessage);
                ShowStatusIndicator(new RetryStatusIndicator(_ui, scheduled.Attempt, scheduled.MaxAttempts, (int)scheduled.DelayMs));
                _ui.RequestRender();
                break;

            case "summarization_retry_attempt_start" when evt is SummarizationRetryAttemptStartEvent attemptStart:
                ClearStatusIndicator("retry");
                if (attemptStart.Source == "branchSummary") ShowStatusIndicator(new BranchSummaryStatusIndicator(_ui));
                else ShowStatusIndicator(new CompactionStatusIndicator(_ui, attemptStart.Reason ?? "manual"));
                _ui.RequestRender();
                break;

            case "summarization_retry_finished":
                ClearStatusIndicator("retry");
                _ui.RequestRender();
                break;
        }
    }

    private static string GetUserMessageText(Message message) => message is UserMessage user
        ? user.Content.IsText ? user.Content.Text! : string.Concat(user.Content.AsBlocks().OfType<TextContent>().Select(t => t.Text))
        : "";

    private void ShowManagedToolStatus(ToolStatus status)
    {
        if (!_managedToolStatusStarted)
        {
            _chatContainer.AddChild(new Spacer(1));
            _managedToolStatusStarted = true;
        }
        var warning = status.Type == "warning";
        _chatContainer.AddChild(new Text(Theme.Fg(warning ? "warning" : "dim", warning ? $"Warning: {status.Message}" : status.Message), 1, 0));
        _lastStatusSpacer = null;
        _lastStatusText = null;
        _ui.RequestRender();
    }

    private void ShowStatus(string message)
    {
        var children = _chatContainer.Children;
        var last = children.Count > 0 ? children[^1] : null;
        var secondLast = children.Count > 1 ? children[^2] : null;
        if (last is not null && secondLast is not null && ReferenceEquals(last, _lastStatusText) && ReferenceEquals(secondLast, _lastStatusSpacer))
        {
            _lastStatusText!.SetText(Theme.Fg("dim", message));
            _ui.RequestRender();
            return;
        }
        var spacer = new Spacer(1);
        var text = new Text(Theme.Fg("dim", message), 1, 0);
        _chatContainer.AddChild(spacer);
        _chatContainer.AddChild(text);
        _lastStatusSpacer = spacer;
        _lastStatusText = text;
        _ui.RequestRender();
    }

    private void AddMessageToChat(Message message, bool populateHistory = false)
    {
        switch (message)
        {
            case BashExecutionMessage bash:
            {
                var component = new BashExecutionComponent(bash.Command, _ui, bash.ExcludeFromContext == true);
                if (bash.Output.Length > 0) component.AppendOutput(bash.Output);
                component.SetComplete(bash.ExitCode, bash.Cancelled, bash.Truncated ? new TruncationResult { Truncated = true } : null, bash.FullOutputPath);
                _chatContainer.AddChild(component);
                break;
            }
            case CustomMessage custom:
                if (custom.Display)
                {
                    var component = new CustomMessageComponent(custom, null, GetMarkdownThemeWithSettings(), _outputPad);
                    component.SetExpanded(_toolOutputExpanded);
                    _chatContainer.AddChild(component);
                }
                break;
            case CompactionSummaryMessage compaction:
            {
                _chatContainer.AddChild(new Spacer(1));
                var component = new CompactionSummaryMessageComponent(compaction, GetMarkdownThemeWithSettings());
                component.SetExpanded(_toolOutputExpanded);
                _chatContainer.AddChild(component);
                break;
            }
            case BranchSummaryMessage branch:
            {
                _chatContainer.AddChild(new Spacer(1));
                var component = new BranchSummaryMessageComponent(branch, GetMarkdownThemeWithSettings());
                component.SetExpanded(_toolOutputExpanded);
                _chatContainer.AddChild(component);
                break;
            }
            case UserMessage:
            {
                var textContent = GetUserMessageText(message);
                if (textContent.Length == 0) break;
                if (_chatContainer.Children.Count > 0) _chatContainer.AddChild(new Spacer(1));
                if (AgentSession.ParseSkillBlock(textContent) is { } skillBlock)
                {
                    var component = new SkillInvocationMessageComponent(skillBlock, GetMarkdownThemeWithSettings());
                    component.SetExpanded(_toolOutputExpanded);
                    _chatContainer.AddChild(component);
                    if (!string.IsNullOrEmpty(skillBlock.UserMessage))
                    {
                        _chatContainer.AddChild(new Spacer(1));
                        _chatContainer.AddChild(new UserMessageComponent(skillBlock.UserMessage, GetMarkdownThemeWithSettings(), _outputPad, GetMarkdownTransformers()));
                    }
                }
                else
                {
                    _chatContainer.AddChild(new UserMessageComponent(textContent, GetMarkdownThemeWithSettings(), _outputPad, GetMarkdownTransformers()));
                }
                if (populateHistory) _editor.AddToHistory(textContent);
                break;
            }
            case AssistantMessage assistant:
                _chatContainer.AddChild(new AssistantMessageComponent(assistant, _hideThinkingBlock, GetMarkdownThemeWithSettings(), _hiddenThinkingLabel, _outputPad, GetMarkdownTransformers()));
                break;
        }
    }

    private abstract record RenderItem;

    private sealed record MessageItem(Message Message) : RenderItem;

    private sealed record CostNoticeItem(string Kind, Usage Usage) : RenderItem;

    private void RenderSessionItems(List<RenderItem> items, bool updateFooter = false, bool populateHistory = false)
    {
        _pendingTools.Clear();
        var renderedPendingTools = new Dictionary<string, ToolExecutionComponent>();
        var cacheMisses = SettingsManager.ShowCacheMissNotices ? CacheStats.CollectCacheMisses(SessionManager.GetEntries(), Session.ModelRuntime) : [];
        if (updateFooter)
        {
            _footer.Invalidate();
            UpdateEditorBorderColor();
        }

        foreach (var item in items)
        {
            if (item is CostNoticeItem notice)
            {
                AddCompactionCostNotice(notice.Kind, notice.Usage);
                continue;
            }
            var message = ((MessageItem)item).Message;
            if (message is AssistantMessage assistant)
            {
                AddMessageToChat(assistant);
                foreach (var toolCall in assistant.Content.OfType<ToolCall>())
                {
                    var component = new ToolExecutionComponent(toolCall.Name, toolCall.Id, toolCall.Arguments, SettingsManager.ShowImages, SettingsManager.ImageWidthCells, GetRegisteredToolDefinition(toolCall.Name), _ui, SessionManager.Cwd);
                    component.SetExpanded(_toolOutputExpanded);
                    _chatContainer.AddChild(component);
                    if (assistant.StopReason is StopReason.Aborted or StopReason.Error)
                    {
                        string errorMessage;
                        if (assistant.StopReason == StopReason.Aborted)
                        {
                            var retryAttempt = Session.RetryAttempt;
                            errorMessage = retryAttempt > 0 ? $"Aborted after {retryAttempt} retry attempt{(retryAttempt > 1 ? "s" : "")}" : "Operation aborted";
                        }
                        else
                        {
                            errorMessage = string.IsNullOrEmpty(assistant.ErrorMessage) ? "Error" : assistant.ErrorMessage;
                        }
                        component.UpdateResult(new AgentToolResult { Content = [new TextContent(errorMessage)] }, true);
                    }
                    else
                    {
                        renderedPendingTools[toolCall.Id] = component;
                    }
                }
                if (assistant.StopReason is not (StopReason.Aborted or StopReason.Error))
                {
                    MaybeShowAssistantDiagnostics(assistant);
                    if (cacheMisses.TryGetValue(assistant, out var miss)) AddCacheMissNotice(miss);
                }
            }
            else if (message is ToolResultMessage toolResult)
            {
                if (renderedPendingTools.Remove(toolResult.ToolCallId, out var component))
                {
                    component.UpdateResult(new AgentToolResult { Content = toolResult.Content, Details = toolResult.Details }, toolResult.IsError);
                }
            }
            else
            {
                AddMessageToChat(message, populateHistory);
            }
        }

        foreach (var (id, component) in renderedPendingTools) _pendingTools[id] = component;
        _ui.RequestRender();
    }

    private void RenderSessionEntries(List<SessionEntry> entries, bool updateFooter = false, bool populateHistory = false)
    {
        var items = new List<RenderItem>();
        foreach (var entry in entries)
        {
            if (entry is CustomEntry) continue;
            var messages = SessionManager.SessionEntryToContextMessages(entry);
            items.AddRange(messages.Select(m => (RenderItem)new MessageItem(m)));
            if (messages.Count > 0)
            {
                if (entry is CompactionEntry { Usage: { } cu }) items.Add(new CostNoticeItem("compaction", cu));
                else if (entry is BranchSummaryEntry { Usage: { } bu }) items.Add(new CostNoticeItem("branch_summary", bu));
            }
        }
        RenderSessionItems(items, updateFooter, populateHistory);
    }

    private void AddCompactionCostNotice(string kind, Usage usage)
    {
        if (!SettingsManager.ShowCacheMissNotices) return;
        var tokens = usage.Input + usage.Output + usage.CacheRead + usage.CacheWrite;
        var cost = usage.Cost.Total >= 0.01 ? $" (~${NodeCompat.ToFixed(usage.Cost.Total, 2)})" : "";
        var label = kind == "compaction" ? "Compaction" : "Branch summary";
        _chatContainer.AddChild(new Spacer(1));
        _chatContainer.AddChild(new Text(Theme.Fg("warning", $"{label}: {FooterComponent.FormatTokens(tokens)} tokens billed{cost}"), 1, 0));
    }

    private void MaybeShowAssistantDiagnostics(AssistantMessage message)
    {
        if (!SettingsManager.ShowCacheMissNotices) return;
        foreach (var diagnostic in message.Diagnostics ?? [])
        {
            if (diagnostic.Type != "anthropic_input_transformations" || diagnostic.Details?["transformations"] is not JsonArray transformations) continue;
            var dropped = new List<string>();
            foreach (var t in transformations.OfType<JsonObject>())
            {
                if (PiJson.GetString(t["type"]) != "thinking_dropped") continue;
                var reason = PiJson.GetString(t["reason"]) ?? "unknown reason";
                var location = PiJson.GetString(t["path"]) is { } p ? $" at {p}" : "";
                dropped.Add(reason + location);
            }
            if (dropped.Count == 0) continue;
            var noun = dropped.Count == 1 ? "thinking block" : $"{dropped.Count} thinking blocks";
            _chatContainer.AddChild(new Spacer(1));
            _chatContainer.AddChild(new Text(Theme.Fg("warning", $"Anthropic dropped {noun}: {string.Join("; ", dropped)}"), 1, 0));
        }
    }

    private void MaybeShowCacheMissNotice(AssistantMessage message)
    {
        if (!SettingsManager.ShowCacheMissNotices) return;
        if (CacheStats.DetectCacheMiss(SessionManager.GetEntries(), message, Session.ModelRuntime) is { } miss) AddCacheMissNotice(miss);
    }

    private void AddCacheMissNotice(CacheMiss miss)
    {
        if (miss.MissedTokens < 20_000 && miss.MissedCost < 0.1) return;
        var cost = miss.MissedCost >= 0.01 ? $" (~${NodeCompat.ToFixed(miss.MissedCost, 2)})" : "";
        var reBilled = $"{FooterComponent.FormatTokens(miss.MissedTokens)} tokens re-billed{cost}";
        var label = miss.ModelChanged ? "Cache miss after model switch" : miss.IdleMs >= CacheStats.CacheTtlMs ? $"Cache miss after {Math.Round(miss.IdleMs / 60_000.0, MidpointRounding.AwayFromZero)}m idle" : "Cache miss";
        _chatContainer.AddChild(new Spacer(1));
        _chatContainer.AddChild(new Text(Theme.Fg("warning", $"{label}: {reBilled}"), 1, 0));
    }

    public void RenderInitialMessages()
    {
        RenderSessionEntries(SessionManager.BuildContextEntries(), updateFooter: true, populateHistory: true);
        RenderProjectTrustWarningIfNeeded();
        var compactionCount = SessionManager.GetEntries().Count(e => e is CompactionEntry);
        if (compactionCount > 0) ShowStatus($"Session compacted {(compactionCount == 1 ? "1 time" : $"{compactionCount} times")}");
    }

    private void RenderProjectTrustWarningIfNeeded()
    {
        if (SettingsManager.IsProjectTrusted || !ProjectTrustStore.HasTrustRequiringProjectResources(SessionManager.Cwd)) return;
        if (_chatContainer.Children.Count > 0) _chatContainer.AddChild(new Spacer(1));
        _chatContainer.AddChild(new Text(Theme.Fg("warning", $"This project is not trusted. Project {AppConfig.ConfigDirName} resources and packages are ignored. Use /trust to save a trust decision, then restart {AppConfig.AppName}."), 1, 0));
    }

    private Task<string> GetUserInputAsync()
    {
        if (_pendingUserInputs.TryDequeue(out var queued)) return Task.FromResult(queued);
        var tcs = new TaskCompletionSource<string>();
        _onInputCallback = text =>
        {
            _onInputCallback = null;
            tcs.TrySetResult(text);
        };
        return tcs.Task;
    }

    private void RebuildChatFromMessages()
    {
        _chatContainer.Clear();
        RenderSessionEntries(SessionManager.BuildContextEntries());
    }

    private void HandleCtrlC()
    {
        var now = NowMs();
        if (now - _lastSigintTime < 500)
        {
            _ = ShutdownAsync();
        }
        else
        {
            ClearEditor();
            _lastSigintTime = now;
        }
    }

    private void HandleCtrlD() => _ = ShutdownAsync();

    private static string QuoteIfNeeded(string value) =>
        value.Length > 0 && !Regex.IsMatch(value, @"[^a-zA-Z0-9_\-./~:@]") ? value : $"'{value.Replace("'", "'\\''")}'";

    private static string? FormatResumeCommand(SessionManager sessionManager)
    {
        if (Console.IsOutputRedirected || !sessionManager.IsPersisted) return null;
        if (sessionManager.SessionFile is not { } file || !File.Exists(file)) return null;
        var args = new List<string> { AppConfig.AppName };
        if (!sessionManager.UsesDefaultSessionDir)
        {
            args.Add("--session-dir");
            args.Add(QuoteIfNeeded(sessionManager.SessionDir));
        }
        args.Add("--session");
        args.Add(sessionManager.SessionId);
        return string.Join(" ", args);
    }

    private async Task ShutdownAsync(bool fromSignal = false)
    {
        if (_isShuttingDown) return;
        _isShuttingDown = true;

        if (fromSignal)
        {
            await _runtimeHost.DisposeAsync();
            _themeController.DisableAutoSync();
            await _ui.Terminal.DrainInputAsync(1000);
            Stop();
            FinishProcess(0);
            return;
        }

        _themeController.DisableAutoSync();
        await _ui.Terminal.DrainInputAsync(1000);
        Stop();
        await _runtimeHost.DisposeAsync();
        if (FormatResumeCommand(SessionManager) is { } resumeCommand)
        {
            Console.Out.Write($"\e[2mTo resume this session:\e[22m {resumeCommand}\n");
            Console.Out.Flush();
        }
        FinishProcess(0);
    }

    private void FinishProcess(int exitCode) => _exit.TrySetResult(exitCode);

    private async Task CheckShutdownRequestedAsync()
    {
        if (_shutdownRequested) await ShutdownAsync();
    }

    private void RegisterSignalHandlers()
    {
        UnregisterSignalHandlers();
        void Register(PosixSignal signal)
        {
            try
            {
                _signalRegistrations.Add(PosixSignalRegistration.Create(signal, context =>
                {
                    context.Cancel = true;
                    ShellUtils.KillTrackedDetachedChildren();
                    _dispatcher.Post(() => _ = ShutdownAsync(fromSignal: true));
                }));
            }
            catch
            {
                // Signal not supported on this platform.
            }
        }
        Register(PosixSignal.SIGTERM);
        if (!OperatingSystem.IsWindows()) Register(PosixSignal.SIGHUP);

        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        _dispatcher.UnhandledException = ex => UncaughtCrash(ex);
    }

    private void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex) UncaughtCrash(ex);
    }

    private void UncaughtCrash(Exception error)
    {
        if (_isShuttingDown) Environment.Exit(1);
        _isShuttingDown = true;
        try
        {
            UnregisterSignalHandlers();
        }
        catch
        {
            // ignored
        }
        try
        {
            ShellUtils.KillTrackedDetachedChildren();
        }
        catch
        {
            // ignored
        }
        try
        {
            _ui.Stop();
        }
        catch
        {
            // ignored
        }
        Console.Error.WriteLine($"{AppConfig.AppName} exiting due to uncaughtException:");
        Console.Error.WriteLine(error);
        Environment.Exit(1);
    }

    private void UnregisterSignalHandlers()
    {
        foreach (var registration in _signalRegistrations) registration.Dispose();
        _signalRegistrations.Clear();
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);

    private void HandleCtrlZ()
    {
        if (OperatingSystem.IsWindows())
        {
            ShowStatus("Suspend to background is not supported on Windows");
            return;
        }
        PosixSignalRegistration? cont = null;
        cont = PosixSignalRegistration.Create(PosixSignal.SIGCONT, _ =>
        {
            _dispatcher.Post(() =>
            {
                cont?.Dispose();
                _ui.Start();
                _ui.RequestRender(true);
            });
        });
        _ui.Stop();
        const int sigtstp = 20;
        if (kill(0, OperatingSystem.IsMacOS() ? 18 : sigtstp) != 0)
        {
            cont.Dispose();
            _ui.Start();
        }
    }
}
