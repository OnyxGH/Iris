using System.Globalization;
using System.Text;
using System.Text.Json;
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

public sealed partial class InteractiveMode
{
    private void SetupEditorSubmitHandler() => _defaultEditor.OnSubmit = text => _ = HandleSubmitAsync(text);

    private async Task HandleSubmitAsync(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;

        static bool Is(string text, string command) => text == command || text.StartsWith(command + " ", StringComparison.Ordinal);

        try
        {
            switch (text)
            {
                case "/settings":
                    ShowSettingsSelector();
                    _editor.SetText("");
                    return;
                case "/scoped-models":
                    _editor.SetText("");
                    ShowModelsSelector();
                    return;
                case "/share":
                    HandleShareCommand();
                    _editor.SetText("");
                    return;
                case "/copy":
                    await HandleCopyCommandAsync();
                    _editor.SetText("");
                    return;
                case "/session":
                    HandleSessionCommand();
                    _editor.SetText("");
                    return;
                case "/changelog":
                    HandleChangelogCommand();
                    _editor.SetText("");
                    return;
                case "/hotkeys":
                    HandleHotkeysCommand();
                    _editor.SetText("");
                    return;
                case "/fork":
                    ShowUserMessageSelector();
                    _editor.SetText("");
                    return;
                case "/clone":
                    _editor.SetText("");
                    await HandleCloneCommandAsync();
                    return;
                case "/tree":
                    ShowTreeSelector();
                    _editor.SetText("");
                    return;
                case "/trust":
                    ShowTrustSelector();
                    _editor.SetText("");
                    return;
                case "/logout":
                    _ = ShowLogoutSelectorAsync();
                    _editor.SetText("");
                    return;
                case "/llama":
                    _editor.AddToHistory(text);
                    _editor.SetText("");
                    await HandleLlamaCommandAsync();
                    return;
                case "/new":
                    _editor.SetText("");
                    await HandleClearCommandAsync();
                    return;
                case "/reload":
                    _editor.SetText("");
                    await HandleReloadCommandAsync();
                    return;
                case "/debug":
                    HandleDebugCommand();
                    _editor.SetText("");
                    return;
                case "/resume":
                    ShowSessionSelector();
                    _editor.SetText("");
                    return;
                case "/quit":
                    _editor.SetText("");
                    await ShutdownAsync();
                    return;
            }

            if (Is(text, "/model"))
            {
                var searchTerm = text.StartsWith("/model ", StringComparison.Ordinal) ? text[7..].Trim() : null;
                _editor.SetText("");
                await HandleModelCommandAsync(searchTerm);
                return;
            }
            if (Is(text, "/thinking"))
            {
                var searchTerm = text.StartsWith("/thinking ", StringComparison.Ordinal) ? text[10..].Trim() : null;
                _editor.SetText("");
                HandleThinkingCommand(searchTerm);
                return;
            }
            if (Is(text, "/export"))
            {
                HandleExportCommand(text);
                _editor.SetText("");
                return;
            }
            if (Is(text, "/import"))
            {
                await HandleImportCommandAsync(text);
                _editor.SetText("");
                return;
            }
            if (Is(text, "/name"))
            {
                HandleNameCommand(text);
                _editor.SetText("");
                return;
            }
            if (Is(text, "/login"))
            {
                var providerRef = text.StartsWith("/login ", StringComparison.Ordinal) ? text[7..].Trim() : null;
                _editor.SetText("");
                await HandleLoginCommandAsync(providerRef);
                return;
            }
            if (Is(text, "/compact"))
            {
                var customInstructions = text.StartsWith("/compact ", StringComparison.Ordinal) ? text[9..].Trim() : null;
                _editor.SetText("");
                await HandleCompactCommandAsync(customInstructions);
                return;
            }

            if (text.StartsWith('!'))
            {
                var isExcluded = text.StartsWith("!!", StringComparison.Ordinal);
                var command = isExcluded ? text[2..].Trim() : text[1..].Trim();
                if (command.Length > 0)
                {
                    if (Session.IsBashRunning)
                    {
                        ShowWarning("A bash command is already running. Press Esc to cancel it first.");
                        _editor.SetText(text);
                        return;
                    }
                    _editor.AddToHistory(text);
                    await HandleBashCommandAsync(command, isExcluded);
                    _isBashMode = false;
                    UpdateEditorBorderColor();
                    return;
                }
            }

            if (Session.IsCompacting)
            {
                if (IsExtensionCommand(text))
                {
                    _editor.AddToHistory(text);
                    _editor.SetText("");
                    await Session.PromptAsync(text);
                }
                else
                {
                    QueueCompactionMessage(text, "steer");
                }
                return;
            }

            if (Session.IsStreaming)
            {
                _editor.AddToHistory(text);
                _editor.SetText("");
                await Session.PromptAsync(text, new PromptOptions { StreamingBehavior = "steer" });
                UpdatePendingMessagesDisplay();
                _ui.RequestRender();
                return;
            }

            FlushPendingBashComponents();
            if (_onInputCallback is not null) _onInputCallback(text);
            else _pendingUserInputs.Enqueue(text);
            _editor.AddToHistory(text);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private async Task HandleFollowUpAsync()
    {
        var text = _editor.GetExpandedText().Trim();
        if (text.Length == 0) return;
        try
        {
            if (Session.IsCompacting)
            {
                if (IsExtensionCommand(text))
                {
                    _editor.AddToHistory(text);
                    _editor.SetText("");
                    await Session.PromptAsync(text);
                }
                else
                {
                    QueueCompactionMessage(text, "followUp");
                }
                return;
            }
            if (Session.IsStreaming)
            {
                _editor.AddToHistory(text);
                _editor.SetText("");
                await Session.PromptAsync(text, new PromptOptions { StreamingBehavior = "followUp" });
                UpdatePendingMessagesDisplay();
                _ui.RequestRender();
            }
            else if (_editor.OnSubmit is { } onSubmit)
            {
                _editor.SetText("");
                onSubmit(text);
            }
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void HandleDequeue()
    {
        var restored = RestoreQueuedMessagesToEditor();
        ShowStatus(restored == 0 ? "No queued messages to restore" : $"Restored {restored} queued message{(restored > 1 ? "s" : "")} to editor");
    }

    private void UpdateEditorBorderColor()
    {
        if (_editor is Editor editor)
        {
            editor.BorderColor = _isBashMode ? Theme.GetBashModeBorderColor() : Theme.GetThinkingBorderColor(Session.ThinkingLevel);
        }
        _activeStatusIndicator?.Invalidate();
        _ui.RequestRender();
    }

    private void CycleThinkingLevel()
    {
        var newLevel = Session.CycleThinkingLevel();
        if (newLevel is null)
        {
            ShowStatus("Current model does not support thinking");
            return;
        }
        _footer.Invalidate();
        UpdateEditorBorderColor();
        ShowStatus($"Thinking level: {newLevel.Value.ToWire()}");
    }

    private async Task CycleModelAsync(bool forward)
    {
        try
        {
            var result = await Session.CycleModelAsync(forward);
            if (result is null)
            {
                ShowStatus(Session.ScopedModels.Count > 0 ? "Only one model in scope" : "Only one model available");
                return;
            }
            _footer.Invalidate();
            UpdateEditorBorderColor();
            var thinkingStr = result.Model.Reasoning && result.ThinkingLevel != ThinkingLevel.Off ? $" (thinking: {result.ThinkingLevel.ToWire()})" : "";
            ShowStatus($"Switched to {(string.IsNullOrEmpty(result.Model.Name) ? result.Model.Id : result.Model.Name)}{thinkingStr}");
            _ = MaybeWarnAboutAnthropicSubscriptionAuthAsync(result.Model);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void ToggleToolOutputExpansion() => SetToolsExpanded(!_toolOutputExpanded);

    private void SetToolsExpanded(bool expanded)
    {
        if (expanded == _toolOutputExpanded) return;
        _toolOutputExpanded = expanded;
        if (_builtInHeader is IExpandable header) header.SetExpanded(expanded);
        foreach (var container in new[] { _loadedResourcesContainer, _chatContainer })
        {
            foreach (var child in container.Children.OfType<IExpandable>()) child.SetExpanded(expanded);
        }
        ShowStatus($"Tool output: {(expanded ? "expanded" : "collapsed")}");
    }

    private void UpdateThinkingBlockVisibility()
    {
        foreach (var child in _chatContainer.Children.OfType<AssistantMessageComponent>()) child.SetHideThinkingBlock(_hideThinkingBlock);
        _ui.RequestRender();
    }

    private void ToggleThinkingBlockVisibility()
    {
        _hideThinkingBlock = !_hideThinkingBlock;
        SettingsManager.SetHideThinkingBlock(_hideThinkingBlock);
        UpdateThinkingBlockVisibility();
        ShowStatus($"Thinking blocks: {(_hideThinkingBlock ? "hidden" : "visible")}");
    }

    private async Task HandleOpenExternalEditorAsync()
    {
        var editorCmd = SettingsManager.ExternalEditorCommand;
        var content = _editor.GetExpandedText();
        _ui.Stop();
        try
        {
            if (await ExternalEditor.EditAsync(editorCmd, content) is { } result) _editor.SetText(result);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            _ui.Start();
            _ui.RequestRender(true);
        }
    }

    // ----- UI helpers -----

    public void ClearEditor()
    {
        _editor.SetText("");
        _ui.RequestRender();
    }

    public void ShowError(string errorMessage)
    {
        _chatContainer.AddChild(new Spacer(1));
        _chatContainer.AddChild(new Text(Theme.Fg("error", $"Error: {errorMessage}"), _outputPad, 0));
        _ui.RequestRender();
    }

    public void ShowWarning(string warningMessage)
    {
        _chatContainer.AddChild(new Spacer(1));
        _chatContainer.AddChild(new Text(Theme.Fg("warning", $"Warning: {warningMessage}"), 1, 0));
        _ui.RequestRender();
    }

    // ----- Queues -----

    private (List<string> Steering, List<string> FollowUp) GetAllQueuedMessages() => (
        [.. Session.GetSteeringMessages(), .. _compactionQueuedMessages.Where(m => m.Mode == "steer").Select(m => m.Text)],
        [.. Session.GetFollowUpMessages(), .. _compactionQueuedMessages.Where(m => m.Mode == "followUp").Select(m => m.Text)]);

    private (List<string> Steering, List<string> FollowUp) ClearAllQueues()
    {
        var (steering, followUp) = Session.ClearQueue();
        var compactionSteering = _compactionQueuedMessages.Where(m => m.Mode == "steer").Select(m => m.Text);
        var compactionFollowUp = _compactionQueuedMessages.Where(m => m.Mode == "followUp").Select(m => m.Text);
        var result = (Steering: steering.Concat(compactionSteering).ToList(), FollowUp: followUp.Concat(compactionFollowUp).ToList());
        _compactionQueuedMessages = [];
        return result;
    }

    private void UpdatePendingMessagesDisplay()
    {
        _pendingMessagesContainer.Clear();
        var (steering, followUp) = GetAllQueuedMessages();
        if (steering.Count == 0 && followUp.Count == 0) return;
        _pendingMessagesContainer.AddChild(new Spacer(1));
        foreach (var message in steering) _pendingMessagesContainer.AddChild(new TruncatedText(Theme.Fg("dim", $"Steering: {message}"), 1, 0));
        foreach (var message in followUp) _pendingMessagesContainer.AddChild(new TruncatedText(Theme.Fg("dim", $"Follow-up: {message}"), 1, 0));
        var dequeueHint = KeyHints.KeyDisplayText("app.message.dequeue");
        _pendingMessagesContainer.AddChild(new TruncatedText(Theme.Fg("dim", $"↳ {dequeueHint} to edit all queued messages"), 1, 0));
    }

    private int RestoreQueuedMessagesToEditor(bool abort = false, string? currentText = null)
    {
        var (steering, followUp) = ClearAllQueues();
        var allQueued = steering.Concat(followUp).ToList();
        if (allQueued.Count == 0)
        {
            UpdatePendingMessagesDisplay();
            if (abort) Agent.Abort();
            return 0;
        }
        var queuedText = string.Join("\n\n", allQueued);
        var combined = string.Join("\n\n", new[] { queuedText, currentText ?? _editor.GetText() }.Where(t => t.Trim().Length > 0));
        _editor.SetText(combined);
        UpdatePendingMessagesDisplay();
        if (abort) Agent.Abort();
        return allQueued.Count;
    }

    private void QueueCompactionMessage(string text, string mode)
    {
        _compactionQueuedMessages.Add(new CompactionQueuedMessage(text, mode));
        _editor.AddToHistory(text);
        _editor.SetText("");
        UpdatePendingMessagesDisplay();
        ShowStatus("Queued message for after compaction");
    }

    private bool IsExtensionCommand(string text)
    {
        if (!text.StartsWith('/')) return false;
        var spaceIndex = text.IndexOf(' ');
        var commandName = spaceIndex == -1 ? text[1..] : text[1..spaceIndex];
        return Session.ExtensionRunner.GetCommand(commandName) is not null;
    }

    private async Task FlushCompactionQueueAsync(bool willRetry = false)
    {
        if (_compactionQueuedMessages.Count == 0) return;
        var queuedMessages = _compactionQueuedMessages.ToList();
        _compactionQueuedMessages = [];
        UpdatePendingMessagesDisplay();

        void RestoreQueue(Exception error)
        {
            Session.ClearQueue();
            _compactionQueuedMessages = queuedMessages;
            UpdatePendingMessagesDisplay();
            ShowError($"Failed to send queued message{(queuedMessages.Count > 1 ? "s" : "")}: {error.Message}");
        }

        async Task SendQueued(CompactionQueuedMessage message)
        {
            if (IsExtensionCommand(message.Text)) await Session.PromptAsync(message.Text);
            else if (message.Mode == "followUp") await Session.FollowUpAsync(message.Text);
            else await Session.SteerAsync(message.Text);
        }

        try
        {
            if (willRetry)
            {
                foreach (var message in queuedMessages) await SendQueued(message);
                UpdatePendingMessagesDisplay();
                return;
            }

            var firstPromptIndex = queuedMessages.FindIndex(m => !IsExtensionCommand(m.Text));
            if (firstPromptIndex == -1)
            {
                foreach (var message in queuedMessages) await Session.PromptAsync(message.Text);
                return;
            }

            foreach (var message in queuedMessages.Take(firstPromptIndex)) await Session.PromptAsync(message.Text);
            var firstPrompt = queuedMessages[firstPromptIndex];
            var promptTask = Session.PromptAsync(firstPrompt.Text, new PromptOptions { StreamingBehavior = firstPrompt.Mode });
            _ = promptTask.ContinueWith(t => _dispatcher.Post(() => RestoreQueue(t.Exception!.GetBaseException())), TaskContinuationOptions.OnlyOnFaulted);
            foreach (var message in queuedMessages.Skip(firstPromptIndex + 1)) await SendQueued(message);
            UpdatePendingMessagesDisplay();
        }
        catch (Exception ex)
        {
            RestoreQueue(ex);
        }
    }

    private void FlushPendingBashComponents()
    {
        foreach (var component in _pendingBashComponents)
        {
            _pendingMessagesContainer.RemoveChild(component);
            _chatContainer.AddChild(component);
        }
        _pendingBashComponents.Clear();
    }

    // ----- Selectors -----

    private void DisposeActiveSelector()
    {
        var dispose = _activeSelectorDispose;
        _activeSelectorToken = null;
        _activeSelectorDispose = null;
        dispose?.Invoke();
    }

    private void ShowSelector(Func<Action, (IComponent Component, IComponent Focus, Action? Dispose)> create)
    {
        var token = new object();
        Action? dispose = null;
        void Done()
        {
            dispose?.Invoke();
            if (!ReferenceEquals(_activeSelectorToken, token)) return;
            _activeSelectorToken = null;
            _activeSelectorDispose = null;
            _editorContainer.Clear();
            _editorContainer.AddChild(_editor);
            _ui.SetFocus(_editor);
        }
        var created = create(Done);
        dispose = created.Dispose;
        DisposeActiveSelector();
        _activeSelectorToken = token;
        _activeSelectorDispose = dispose;
        _editorContainer.Clear();
        _editorContainer.AddChild(created.Component);
        _ui.SetFocus(created.Focus);
        _ui.RequestRender();
    }

    private static string QueueModeToWire(QueueMode mode) => mode == QueueMode.All ? "all" : "one-at-a-time";

    private static QueueMode ParseQueueMode(string mode) => mode == "all" ? QueueMode.All : QueueMode.OneAtATime;

    private void ShowSettingsSelector()
    {
        ShowSelector(done =>
        {
            SettingsSelectorComponent? selector = null;
            var defaultProvider = SettingsManager.DefaultProvider;
            var defaultModelId = SettingsManager.DefaultModel;
            var config = new SettingsConfig
            {
                AutoCompact = Session.AutoCompactionEnabled,
                DefaultModel = defaultProvider is not null && defaultModelId is not null ? $"{defaultProvider}/{defaultModelId}" : "not set",
                CurrentModel = Session.Model,
                AvailableDefaultModels = Session.ModelRuntime.AvailableSnapshot,
                ShowImages = SettingsManager.ShowImages,
                ImageWidthCells = SettingsManager.ImageWidthCells,
                AutoResizeImages = SettingsManager.ImageAutoResize,
                BlockImages = SettingsManager.BlockImages,
                EnableSkillCommands = SettingsManager.EnableSkillCommands,
                SteeringMode = QueueModeToWire(Session.SteeringMode),
                FollowUpMode = QueueModeToWire(Session.FollowUpMode),
                Transport = SettingsManager.TransportSetting,
                HttpIdleTimeoutMs = SettingsManager.HttpIdleTimeoutMs,
                ThinkingLevel = SettingsManager.DefaultThinkingLevel ?? ModelResolver.DefaultThinkingLevel,
                ModelThinkingLevels = SettingsManager.GetAllModelThinkingLevels(),
                CurrentTheme = _themeController.GetThemeSelection() ?? "dark",
                TerminalTheme = _themeController.GetTerminalTheme(),
                AvailableThemes = ThemeManager.GetAvailableThemes(),
                HideThinkingBlock = _hideThinkingBlock,
                MermaidRenderingMode = SettingsManager.MermaidRenderingMode,
                CollapseChangelog = SettingsManager.CollapseChangelog,
                EnableInstallTelemetry = SettingsManager.EnableInstallTelemetry,
                DoubleEscapeAction = SettingsManager.DoubleEscapeAction,
                TreeFilterMode = SettingsManager.TreeFilterMode,
                ShowHardwareCursor = SettingsManager.ShowHardwareCursor,
                ShowCacheMissNotices = SettingsManager.ShowCacheMissNotices,
                DefaultProjectTrust = SettingsManager.DefaultProjectTrust,
                EditorPaddingX = SettingsManager.EditorPaddingX,
                OutputPad = SettingsManager.OutputPad,
                AutocompleteMaxVisible = SettingsManager.AutocompleteMaxVisible,
                QuietStartup = SettingsManager.QuietStartup,
                ClearOnShrink = SettingsManager.ClearOnShrink,
                ShowTerminalProgress = SettingsManager.ShowTerminalProgress,
                TuiMode = _ui.Mode,
                FullscreenExitOutput = SettingsManager.FullscreenExitOutput,
                FullscreenScrollbar = SettingsManager.FullscreenScrollbar,
                FullscreenCopyOnSelect = SettingsManager.FullscreenCopyOnSelect,
                Warnings = SettingsManager.Warnings,
            };
            var callbacks = new SettingsCallbacks
            {
                OnAutoCompactChange = enabled =>
                {
                    Session.SetAutoCompactionEnabled(enabled);
                    _footer.SetAutoCompactEnabled(enabled);
                },
                OnShowImagesChange = enabled =>
                {
                    SettingsManager.SetShowImages(enabled);
                    foreach (var child in _chatContainer.Children.OfType<ToolExecutionComponent>()) child.SetShowImages(enabled);
                },
                OnImageWidthCellsChange = width =>
                {
                    SettingsManager.SetImageWidthCells(width);
                    foreach (var child in _chatContainer.Children.OfType<ToolExecutionComponent>()) child.SetImageWidthCells(width);
                },
                OnAutoResizeImagesChange = SettingsManager.SetImageAutoResize,
                OnBlockImagesChange = SettingsManager.SetBlockImages,
                OnEnableSkillCommandsChange = enabled =>
                {
                    SettingsManager.SetEnableSkillCommands(enabled);
                    SetupAutocompleteProvider();
                },
                OnSteeringModeChange = mode => Session.SetSteeringMode(ParseQueueMode(mode)),
                OnFollowUpModeChange = mode => Session.SetFollowUpMode(ParseQueueMode(mode)),
                OnTransportChange = transport =>
                {
                    SettingsManager.SetTransport(transport);
                    Agent.Transport = SettingsManager.Transport;
                },
                OnHttpIdleTimeoutMsChange = timeoutMs =>
                {
                    SettingsManager.SetHttpIdleTimeoutMs(timeoutMs);
                    ShowStatus($"HTTP idle timeout: {SettingsSelectorComponent.FormatHttpIdleTimeoutMs(timeoutMs)}");
                },
                OnModelThinkingLevelChange = (provider, modelId, level) =>
                {
                    SettingsManager.SetModelThinkingLevel(provider, modelId, level);
                    if (Session.Model is { } current && current.Provider == provider && current.Id == modelId)
                    {
                        Session.SetThinkingLevel(level);
                        _footer.Invalidate();
                        UpdateEditorBorderColor();
                    }
                },
                OnModelThinkingLevelRemove = (provider, modelId) =>
                {
                    SettingsManager.RemoveModelThinkingLevel(provider, modelId);
                    if (Session.Model is { } current && current.Provider == provider && current.Id == modelId)
                    {
                        Session.SetThinkingLevel(SettingsManager.DefaultThinkingLevel ?? ModelResolver.DefaultThinkingLevel);
                        _footer.Invalidate();
                        UpdateEditorBorderColor();
                    }
                },
                OnThemeChange = themeSetting =>
                {
                    SettingsManager.SetTheme(themeSetting);
                    _ = _themeController.SetThemeSettingAsync(themeSetting);
                },
                OnThemePreview = _themeController.Preview,
                OnHideThinkingBlockChange = hidden =>
                {
                    _hideThinkingBlock = hidden;
                    SettingsManager.SetHideThinkingBlock(hidden);
                    UpdateThinkingBlockVisibility();
                },
                OnMermaidRenderingModeChange = mode =>
                {
                    SettingsManager.SetMermaidRenderingMode(mode);
                    _chatContainer.Invalidate();
                    _ui.RequestRender();
                },
                OnShowCacheMissNoticesChange = shown =>
                {
                    SettingsManager.SetShowCacheMissNotices(shown);
                    RebuildChatFromMessages();
                },
                OnCollapseChangelogChange = SettingsManager.SetCollapseChangelog,
                OnEnableInstallTelemetryChange = SettingsManager.SetEnableInstallTelemetry,
                OnQuietStartupChange = SettingsManager.SetQuietStartup,
                OnDefaultProjectTrustChange = SettingsManager.SetDefaultProjectTrust,
                OnDoubleEscapeActionChange = SettingsManager.SetDoubleEscapeAction,
                OnTreeFilterModeChange = SettingsManager.SetTreeFilterMode,
                OnShowHardwareCursorChange = enabled =>
                {
                    SettingsManager.SetShowHardwareCursor(enabled);
                    _ui.SetShowHardwareCursor(enabled);
                },
                OnEditorPaddingXChange = padding =>
                {
                    SettingsManager.SetEditorPaddingX(padding);
                    _defaultEditor.SetPaddingX(padding);
                    if (!ReferenceEquals(_editor, _defaultEditor)) _editor.SetPaddingX(padding);
                },
                OnOutputPadChange = padding =>
                {
                    SettingsManager.SetOutputPad(padding);
                    _outputPad = padding;
                    if (_streamingComponent is not null || Session.IsStreaming)
                    {
                        foreach (var child in _chatContainer.Children)
                        {
                            switch (child)
                            {
                                case AssistantMessageComponent a:
                                    a.SetOutputPad(padding);
                                    break;
                                case CustomMessageComponent c:
                                    c.SetOutputPad(padding);
                                    break;
                                case UserMessageComponent u:
                                    u.SetOutputPad(padding);
                                    break;
                            }
                        }
                        _streamingComponent?.SetOutputPad(padding);
                        _ui.RequestRender();
                        return;
                    }
                    RebuildChatFromMessages();
                },
                OnAutocompleteMaxVisibleChange = maxVisible =>
                {
                    SettingsManager.SetAutocompleteMaxVisible(maxVisible);
                    _defaultEditor.SetAutocompleteMaxVisible(maxVisible);
                    if (!ReferenceEquals(_editor, _defaultEditor)) _editor.SetAutocompleteMaxVisible(maxVisible);
                },
                OnClearOnShrinkChange = enabled =>
                {
                    SettingsManager.SetClearOnShrink(enabled);
                    _ui.SetClearOnShrink(enabled);
                    if (!enabled && _activeStatusIndicator is null) _statusContainer.Clear();
                },
                OnShowTerminalProgressChange = SettingsManager.SetShowTerminalProgress,
                OnTuiModeChange = mode =>
                {
                    // Iris: renderers are not swapped live; the new mode applies on the next start.
                    SettingsManager.SetTuiMode(mode);
                    ShowStatus(mode == _ui.Mode ? $"TUI mode: {mode}" : $"TUI mode set to {mode}; restart Iris to apply");
                },
                OnFullscreenExitOutputChange = SettingsManager.SetFullscreenExitOutput,
                OnFullscreenScrollbarChange = mode =>
                {
                    SettingsManager.SetFullscreenScrollbar(mode);
                    _transcriptScrollView?.SetScrollbar(ParseScrollbarSetting(mode));
                },
                OnFullscreenCopyOnSelectChange = enabled =>
                {
                    SettingsManager.SetFullscreenCopyOnSelect(enabled);
                    if (_ui is TuiAltScreen altScreen) altScreen.CopyOnSelect = enabled;
                },
                OnWarningsChange = SettingsManager.SetWarnings,
                OnCancel = () =>
                {
                    done();
                    _ui.RequestRender();
                },
            };
            selector = new SettingsSelectorComponent(config, callbacks);
            return (selector, selector.GetSettingsList(), null);
        });
    }

    private void HandleThinkingCommand(string? searchTerm)
    {
        var availableLevels = Session.GetAvailableThinkingLevels();
        if (string.IsNullOrEmpty(searchTerm))
        {
            ShowThinkingSelector();
            return;
        }
        var normalized = searchTerm.Trim().ToLowerInvariant();
        var match = availableLevels.Where(l => l.ToWire() == normalized).Select(l => (ThinkingLevel?)l).FirstOrDefault();
        if (match is not { } level)
        {
            ShowError($"Unknown thinking level \"{searchTerm}\". Available levels: {string.Join(", ", availableLevels.Select(l => l.ToWire()))}.");
            return;
        }
        SelectThinkingLevel(level, false);
    }

    private void SelectThinkingLevel(ThinkingLevel level, bool persist)
    {
        try
        {
            Session.SetThinkingLevel(level, persist);
            _footer.Invalidate();
            UpdateEditorBorderColor();
            ShowStatus(persist ? $"Default thinking level: {level.ToWire()}" : $"Thinking level: {level.ToWire()}");
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void ShowThinkingSelector()
    {
        ShowSelector(done =>
        {
            void Select(ThinkingLevel level, bool persist)
            {
                SelectThinkingLevel(level, persist);
                done();
            }
            var selector = new ThinkingSelectorComponent(
                Session.ThinkingLevel,
                Session.GetAvailableThinkingLevels(),
                level => Select(level, false),
                () =>
                {
                    done();
                    _ui.RequestRender();
                },
                level => Select(level, true),
                SettingsManager.DefaultThinkingLevel ?? ModelResolver.DefaultThinkingLevel);
            return (selector, selector, null);
        });
    }

    private async Task HandleModelCommandAsync(string? searchTerm)
    {
        if (string.IsNullOrEmpty(searchTerm))
        {
            ShowModelSelector();
            return;
        }
        if (await FindExactModelMatchAsync(searchTerm) is { } model)
        {
            try
            {
                await Session.SetModelAsync(model, persist: false);
                _footer.Invalidate();
                UpdateEditorBorderColor();
                ShowStatus($"Model: {model.Id}");
                _ = MaybeWarnAboutAnthropicSubscriptionAuthAsync(model);
            }
            catch (Exception ex)
            {
                ShowError(ex.Message);
            }
            return;
        }
        ShowModelSelector(searchTerm);
    }

    private async Task<Model?> FindExactModelMatchAsync(string searchTerm)
    {
        var cachedModels = Session.ScopedModels.Count > 0 ? Session.ScopedModels.Select(s => s.Model).ToList() : Session.ModelRuntime.AvailableSnapshot.ToList();
        var cachedMatch = ModelResolver.FindExactModelReferenceMatch(searchTerm, cachedModels);
        if (cachedMatch is not null || Session.ScopedModels.Count > 0) return cachedMatch;

        ShowStatus("Refreshing model catalogs…");
        using var cts = new CancellationTokenSource(15_000);
        try
        {
            var result = await ModelCatalogRefresh.RefreshAsync(Session.ModelRuntime, cts.Token);
            if (result.Aborted && cts.IsCancellationRequested) ShowWarning("Model refresh timed out; searching cached models.");
            else if (result.Errors.Count > 0) ShowWarning($"Could not refresh {string.Join(", ", result.Errors.Keys)}; searching cached models.");
        }
        catch (Exception ex)
        {
            ShowWarning(cts.IsCancellationRequested ? "Model refresh timed out; searching cached models." : $"Could not refresh model catalogs: {ex.Message}");
        }
        return ModelResolver.FindExactModelReferenceMatch(searchTerm, Session.ModelRuntime.AvailableSnapshot.ToList());
    }

    private void UpdateAvailableProviderCount()
    {
        var models = Session.ScopedModels.Count > 0 ? Session.ScopedModels.Select(s => s.Model) : Session.ModelRuntime.AvailableSnapshot;
        _footerDataProvider.SetAvailableProviderCount(models.Select(m => m.Provider).Distinct().Count());
    }

    private async Task MaybeWarnAboutAnthropicSubscriptionAuthAsync(Model? model = null)
    {
        model ??= Session.Model;
        if (IrisJson.GetBool(SettingsManager.Warnings["anthropicExtraUsage"]) == false) return;
        if (_anthropicSubscriptionWarningShown || model is null || model.Provider != "anthropic") return;
        try
        {
            if ((await Session.ModelRuntime.CheckAuthAsync("anthropic"))?.Type == "oauth")
            {
                _anthropicSubscriptionWarningShown = true;
                ShowWarning(AnthropicSubscriptionAuthWarning);
                return;
            }
            var apiKey = (await Session.ModelRuntime.GetAuthAsync(model.Provider))?.Auth.ApiKey;
            if (apiKey is null || !apiKey.Contains("sk-ant-oat", StringComparison.Ordinal)) return;
            _anthropicSubscriptionWarningShown = true;
            ShowWarning(AnthropicSubscriptionAuthWarning);
        }
        catch
        {
            // Warning-only check.
        }
    }

    private bool MaybeSaveImplicitProjectTrustAfterReload()
    {
        var cwd = SessionManager.Cwd;
        if (_autoTrustOnReloadCwd != cwd) return false;
        if (!SettingsManager.IsProjectTrusted || !ProjectTrustStore.HasTrustRequiringProjectResources(cwd)) return false;
        var trustStore = new ProjectTrustStore(_runtimeHost.Services.AgentDir);
        try
        {
            if (trustStore.Get(cwd) is not null)
            {
                _autoTrustOnReloadCwd = null;
                return false;
            }
            trustStore.Set(cwd, true);
            _autoTrustOnReloadCwd = null;
            return true;
        }
        catch (Exception ex)
        {
            ShowWarning($"Could not save project trust after reload: {ex.Message}");
            return false;
        }
    }

    private void ShowTrustSelector()
    {
        var cwd = SessionManager.Cwd;
        var trustStore = new ProjectTrustStore(_runtimeHost.Services.AgentDir);
        var savedDecision = trustStore.GetEntry(cwd);
        ShowSelector(done =>
        {
            var selector = new TrustSelectorComponent(cwd, savedDecision, SettingsManager.IsProjectTrusted,
                selection =>
                {
                    trustStore.SetMany(selection.Updates);
                    done();
                    ShowStatus($"Saved trust decision: {(selection.Trusted ? "trusted" : "untrusted")}. Restart {AppConfig.AppName} for this to take effect.");
                },
                () =>
                {
                    done();
                    _ui.RequestRender();
                });
            return (selector, selector, null);
        });
    }

    private void ShowModelSelector(string? initialSearchInput = null)
    {
        ShowSelector(done =>
        {
            async Task SelectModel(Model model, bool persist)
            {
                try
                {
                    await Session.SetModelAsync(model, persist);
                    UpdateAvailableProviderCount();
                    _footer.Invalidate();
                    UpdateEditorBorderColor();
                    done();
                    ShowStatus(persist ? $"Default model: {model.Provider}/{model.Id}" : $"Model: {model.Id}");
                    _ = MaybeWarnAboutAnthropicSubscriptionAuthAsync(model);
                }
                catch (Exception ex)
                {
                    done();
                    ShowError(ex.Message);
                }
            }
            var defaultProvider = SettingsManager.DefaultProvider;
            var defaultModel = SettingsManager.DefaultModel;
            var selector = new ModelSelectorComponent(_ui, Session.Model, Session.ModelRuntime, Session.ScopedModels,
                model => _ = SelectModel(model, false),
                () =>
                {
                    done();
                    _ui.RequestRender();
                },
                initialSearchInput,
                model => _ = SelectModel(model, true),
                defaultProvider is not null && defaultModel is not null ? (defaultProvider, defaultModel) : null);
            return (selector, selector, selector.Dispose);
        });
    }

    private void ShowModelsSelector()
    {
        var availableModels = Session.ModelRuntime.AvailableSnapshot.ToList();
        var availableModelIds = availableModels.Select(m => $"{m.Provider}/{m.Id}").ToHashSet();
        var configuredPatterns = SettingsManager.EnabledModels;
        var sessionScopedModels = Session.ScopedModels.ToList();

        List<string>? ConfiguredEnabledIds(IReadOnlyList<Model> models)
        {
            if (configuredPatterns is not { Count: > 0 }) return null;
            var (scoped, diagnostics) = ModelResolver.ResolveModelScopeFromModels(configuredPatterns, models);
            var ids = scoped.Select(s => $"{s.Model.Provider}/{s.Model.Id}").ToList();
            foreach (var diagnostic in diagnostics)
            {
                if (diagnostic.Code == "no-match" && diagnostic.Pattern is { } pattern && !ids.Contains(pattern)) ids.Add(pattern);
            }
            return ids;
        }

        var currentEnabledIds = sessionScopedModels.Count > 0 ? sessionScopedModels.Select(s => $"{s.Model.Provider}/{s.Model.Id}").ToList() : ConfiguredEnabledIds(availableModels);
        var selectionChanged = false;

        void UpdateSessionModels(List<string>? enabledIds)
        {
            currentEnabledIds = enabledIds?.ToList();
            var hasEnabledAvailableModel = enabledIds?.Any(availableModelIds.Contains) ?? false;
            var allAvailableModelsEnabled = enabledIds is not null && availableModelIds.All(enabledIds.Contains);
            if (enabledIds is not null && hasEnabledAvailableModel && !allAvailableModelsEnabled)
            {
                Session.SetScopedModels(ModelResolver.ResolveModelScopeFromModels(enabledIds, availableModels).ScopedModels);
            }
            else
            {
                Session.SetScopedModels([]);
            }
            UpdateAvailableProviderCount();
            _ui.RequestRender();
        }

        ShowSelector(done =>
        {
            var disposed = false;
            var cts = new CancellationTokenSource(15_000);
            ScopedModelsSelectorComponent selector = null!;
            selector = new ScopedModelsSelectorComponent(availableModels, currentEnabledIds, "Refreshing model catalogs…",
                enabledIds =>
                {
                    selectionChanged = true;
                    UpdateSessionModels(enabledIds);
                },
                enabledIds =>
                {
                    var allEnabled = enabledIds is not null && enabledIds.Count == availableModels.Count && enabledIds.All(availableModelIds.Contains);
                    SettingsManager.SetEnabledModels(enabledIds is null || allEnabled ? null : enabledIds.ToList());
                    ShowStatus("Model selection saved to settings");
                },
                () =>
                {
                    done();
                    _ui.RequestRender();
                });

            _ = RefreshAsync();

            async Task RefreshAsync()
            {
                try
                {
                    var result = await ModelCatalogRefresh.RefreshAsync(Session.ModelRuntime, cts.Token);
                    if (disposed) return;
                    availableModels = Session.ModelRuntime.AvailableSnapshot.ToList();
                    availableModelIds = availableModels.Select(m => $"{m.Provider}/{m.Id}").ToHashSet();
                    if (!selectionChanged && sessionScopedModels.Count == 0)
                    {
                        currentEnabledIds = ConfiguredEnabledIds(availableModels);
                        selector.UpdateModels(availableModels, currentEnabledIds, updateEnabled: true);
                    }
                    else
                    {
                        selector.UpdateModels(availableModels);
                    }
                    if (currentEnabledIds is not null) UpdateSessionModels(currentEnabledIds);
                    if (result.Aborted && cts.IsCancellationRequested) selector.SetRefreshStatus("Model refresh timed out; showing cached models.", "warning");
                    else if (result.Errors.Count > 0) selector.SetRefreshStatus($"Could not refresh {string.Join(", ", result.Errors.Keys)}; showing cached models.", "warning");
                    else selector.SetRefreshStatus("Model catalogs refreshed.", "success");
                    _ui.RequestRender();
                }
                catch (Exception ex)
                {
                    if (disposed) return;
                    selector.SetRefreshStatus(cts.IsCancellationRequested ? "Model refresh timed out; showing cached models." : $"Could not refresh model catalogs: {ex.Message}", "warning");
                    _ui.RequestRender();
                }
            }

            return (selector, selector, () =>
            {
                disposed = true;
                cts.Cancel();
            });
        });
    }

    private void ShowUserMessageSelector()
    {
        var userMessages = Session.GetUserMessagesForForking();
        if (userMessages.Count == 0)
        {
            ShowStatus("No messages to fork from");
            return;
        }
        var initialSelectedId = userMessages[^1].EntryId;
        ShowSelector(done =>
        {
            var selector = new UserMessageSelectorComponent(
                userMessages.Select(m => (m.EntryId, m.Text)).ToList(),
                entryId => _ = ForkAsync(entryId),
                () =>
                {
                    done();
                    _ui.RequestRender();
                },
                initialSelectedId);

            async Task ForkAsync(string entryId)
            {
                done();
                try
                {
                    var (cancelled, selectedText) = await _runtimeHost.ForkAsync(entryId);
                    if (cancelled)
                    {
                        _ui.RequestRender();
                        return;
                    }
                    _editor.SetText(selectedText ?? "");
                    ShowStatus("Forked to new session");
                }
                catch (Exception ex)
                {
                    ShowError(ex.Message);
                }
            }

            return (selector, selector.GetMessageList(), null);
        });
    }

    private async Task HandleCloneCommandAsync()
    {
        if (SessionManager.LeafId is not { } leafId)
        {
            ShowStatus("Nothing to clone yet");
            return;
        }
        try
        {
            var (cancelled, _) = await _runtimeHost.ForkAsync(leafId, "at");
            if (cancelled)
            {
                _ui.RequestRender();
                return;
            }
            _editor.SetText("");
            ShowStatus("Cloned to new session");
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void ShowTreeSelector(string? initialSelectedId = null)
    {
        var tree = SessionManager.GetTree();
        var realLeafId = SessionManager.LeafId;
        var initialFilterMode = SettingsManager.TreeFilterMode;
        if (tree.Count == 0)
        {
            ShowStatus("No entries in session");
            return;
        }

        ShowSelector(done =>
        {
            async Task OnSelectAsync(string entryId)
            {
                if (entryId == SessionManager.LeafId)
                {
                    done();
                    ShowStatus("Already at this point");
                    return;
                }
                done();

                var wantsSummary = false;
                string? customInstructions = null;
                if (!SettingsManager.BranchSummarySettings.SkipPrompt)
                {
                    while (true)
                    {
                        var summaryChoice = await ShowExtensionSelectorAsync("Summarize branch?", ["No summary", "Summarize", "Summarize with custom prompt"]);
                        if (summaryChoice is null)
                        {
                            ShowTreeSelector(entryId);
                            return;
                        }
                        wantsSummary = summaryChoice != "No summary";
                        if (summaryChoice == "Summarize with custom prompt")
                        {
                            customInstructions = await ShowExtensionEditorAsync("Custom summarization instructions");
                            if (customInstructions is null) continue;
                        }
                        break;
                    }
                }

                if (Session.IsStreaming)
                {
                    RestoreQueuedMessagesToEditor();
                    await Session.AbortAsync();
                }
                if (Session.IsCompacting)
                {
                    ShowError("Wait for the current compaction or tree navigation to finish before navigating the session tree.");
                    return;
                }

                var showingSummaryIndicator = false;
                var originalOnEscape = _defaultEditor.OnEscape;
                if (wantsSummary)
                {
                    _defaultEditor.OnEscape = () => Session.AbortBranchSummary();
                    _chatContainer.AddChild(new Spacer(1));
                    ShowStatusIndicator(new BranchSummaryStatusIndicator(_ui));
                    showingSummaryIndicator = true;
                    _ui.RequestRender();
                }

                try
                {
                    var result = await Session.NavigateTreeAsync(entryId, wantsSummary, customInstructions);
                    if (result.Aborted)
                    {
                        ShowStatus("Branch summarization cancelled");
                        ShowTreeSelector(entryId);
                        return;
                    }
                    if (result.Cancelled)
                    {
                        ShowStatus("Navigation cancelled");
                        return;
                    }
                    _chatContainer.Clear();
                    RenderInitialMessages();
                    if (!string.IsNullOrEmpty(result.EditorText) && _editor.GetText().Trim().Length == 0) _editor.SetText(result.EditorText);
                    ShowStatus("Navigated to selected point");
                    _ = FlushCompactionQueueAsync(false);
                }
                catch (Exception ex)
                {
                    ShowError(ex.Message);
                }
                finally
                {
                    if (showingSummaryIndicator) ClearStatusIndicator("branchSummary");
                    _defaultEditor.OnEscape = originalOnEscape;
                }
            }

            var selector = new TreeSelectorComponent(tree, realLeafId, _ui.Terminal.Rows,
                entryId => _ = OnSelectAsync(entryId),
                () =>
                {
                    done();
                    _ui.RequestRender();
                },
                (entryId, label) =>
                {
                    SessionManager.AppendLabelChange(entryId, label);
                    _ui.RequestRender();
                },
                initialSelectedId,
                initialFilterMode);
            selector.OnCopy = text => _ = CopyAsync(text);

            async Task CopyAsync(string? text)
            {
                if (string.IsNullOrEmpty(text))
                {
                    ShowError("Selected entry has no text to copy");
                    return;
                }
                try
                {
                    await Clipboard.CopyAsync(text);
                    ShowStatus("Copied selected message to clipboard");
                }
                catch (Exception ex)
                {
                    ShowError(ex.Message);
                }
            }

            return (selector, selector, null);
        });
    }

    private void ShowSessionSelector()
    {
        ShowSelector(done =>
        {
            var selector = new SessionSelectorComponent(
                onProgress => SessionManager.ListAsync(SessionManager.Cwd, SessionManager.SessionDir, onProgress),
                onProgress => SessionManager.UsesDefaultSessionDir ? SessionManager.ListAllAsync(null, onProgress) : SessionManager.ListAllAsync(SessionManager.SessionDir, onProgress),
                sessionPath =>
                {
                    done();
                    _ = HandleResumeSessionAsync(sessionPath);
                },
                () =>
                {
                    done();
                    _ui.RequestRender();
                },
                () => _ = ShutdownAsync(),
                () => _ui.RequestRender(),
                (sessionFilePath, nextName) =>
                {
                    var next = (nextName ?? "").Trim();
                    if (next.Length == 0) return Task.CompletedTask;
                    Core.SessionManager.Open(sessionFilePath).AppendSessionInfo(next);
                    return Task.CompletedTask;
                },
                true,
                _keybindings,
                SessionManager.SessionFile);
            return (selector, selector, null);
        });
    }

    private async Task HandleResumeSessionAsync(string sessionPath)
    {
        ClearStatusIndicator();
        try
        {
            if (!await _runtimeHost.SwitchSessionAsync(sessionPath)) return;
            ShowStatus("Resumed session");
        }
        catch (MissingSessionCwdException error)
        {
            if (await PromptForMissingSessionCwdAsync(error) is not { } selectedCwd)
            {
                ShowStatus("Resume cancelled");
                return;
            }
            try
            {
                if (!await _runtimeHost.SwitchSessionAsync(sessionPath, selectedCwd)) return;
                ShowStatus("Resumed session in current cwd");
            }
            catch (Exception ex)
            {
                HandleFatalRuntimeError("Failed to resume session", ex);
            }
        }
        catch (Exception ex)
        {
            HandleFatalRuntimeError("Failed to resume session", ex);
        }
    }

    // ----- Commands -----

    private async Task HandleReloadCommandAsync()
    {
        if (Session.IsStreaming)
        {
            ShowWarning("Wait for the current response to finish before reloading.");
            return;
        }
        if (Session.IsCompacting)
        {
            ShowWarning("Wait for compaction to finish before reloading.");
            return;
        }

        ResetExtensionUI();
        var reloadBox = new Container();
        string BorderColor(string s) => Theme.Fg("border", s);
        reloadBox.AddChild(new DynamicBorder(BorderColor));
        reloadBox.AddChild(new Spacer(1));
        reloadBox.AddChild(new Text(Theme.Fg("muted", "Reloading keybindings, extensions, skills, prompts, themes, and context files..."), 1, 0));
        reloadBox.AddChild(new Spacer(1));
        reloadBox.AddChild(new DynamicBorder(BorderColor));

        var previousEditor = _editor;
        _editorContainer.Clear();
        _editorContainer.AddChild(reloadBox);
        _ui.SetFocus(reloadBox);
        _ui.RequestRender(true);
        await Task.Yield();

        void DismissReloadBox(IComponent editor)
        {
            _editorContainer.Clear();
            _editorContainer.AddChild(editor);
            _ui.SetFocus(editor);
            _ui.RequestRender();
        }

        var dismissed = false;
        var chatRestored = false;
        void RestoreChat()
        {
            if (chatRestored) return;
            chatRestored = true;
            _hideThinkingBlock = SettingsManager.HideThinkingBlock;
            _outputPad = SettingsManager.OutputPad;
            RebuildChatFromMessages();
        }

        try
        {
            await Session.ReloadAsync(() =>
            {
                RestoreChat();
                return Task.CompletedTask;
            });
            RestoreChat();
            AppKeybindings.Reload(_keybindings, _runtimeHost.Services.AgentDir);
            if (_builtInHeader is IExpandable header) header.SetExpanded(_toolOutputExpanded);
            ThemeManager.SetRegisteredThemes(Session.ResourceLoader.GetThemes().Themes.Select(ThemeManager.CreateThemeFromResource));
            ApplyRuntimeSettings();
            await _themeController.ApplyFromSettingsAsync();
            SetupAutocompleteProvider();
            SetupExtensionShortcuts();
            ShowLoadedResources(false, true);
            var savedImplicitProjectTrust = MaybeSaveImplicitProjectTrustAfterReload();
            if (Session.ModelRuntime.GetError() is { Length: > 0 } modelsJsonError) ShowError($"models.json error: {modelsJsonError}");
            ShowStatus(savedImplicitProjectTrust
                ? "Reloaded keybindings, extensions, skills, prompts, themes, and context files; saved project trust"
                : "Reloaded keybindings, extensions, skills, prompts, themes, and context files");
            DismissReloadBox(_editor);
            dismissed = true;
        }
        catch (Exception ex)
        {
            if (!dismissed) DismissReloadBox(previousEditor);
            ShowError($"Reload failed: {ex.Message}");
        }
    }

    private void HandleExportCommand(string text)
    {
        var outputPath = GetPathCommandArgument(text, "/export");
        try
        {
            if (outputPath is not null && outputPath.EndsWith(".jsonl", StringComparison.Ordinal))
            {
                var filePath = SessionExport.ExportToJsonl(SessionManager, outputPath);
                ShowStatus($"Session exported to: {filePath}");
            }
            else
            {
                ShowError("Failed to export session: HTML export is not available in Iris yet. Use /export <file.jsonl>.");
            }
        }
        catch (Exception ex)
        {
            ShowError($"Failed to export session: {ex.Message}");
        }
    }

    private static string? GetPathCommandArgument(string text, string command)
    {
        if (text == command || !text.StartsWith(command + " ", StringComparison.Ordinal)) return null;
        var argsString = text[(command.Length + 1)..].TrimStart();
        if (argsString.Length == 0) return null;
        var firstChar = argsString[0];
        if (firstChar is '"' or '\'')
        {
            var closing = argsString.IndexOf(firstChar, 1);
            return closing < 0 ? null : argsString[1..closing];
        }
        var ws = argsString.IndexOfAny([' ', '\t', '\n', '\r', '\f', '\v']);
        return ws < 0 ? argsString : argsString[..ws];
    }

    private async Task HandleImportCommandAsync(string text)
    {
        var inputPath = GetPathCommandArgument(text, "/import");
        if (inputPath is null)
        {
            ShowError("Usage: /import <path.jsonl>");
            return;
        }
        if (!await ShowExtensionConfirmAsync("Import session", $"Replace current session with {inputPath}?"))
        {
            ShowStatus("Import cancelled");
            return;
        }
        try
        {
            ClearStatusIndicator();
            if (!await _runtimeHost.ImportFromJsonlAsync(inputPath))
            {
                ShowStatus("Import cancelled");
                return;
            }
            ShowStatus($"Session imported from: {inputPath}");
        }
        catch (MissingSessionCwdException error)
        {
            if (await PromptForMissingSessionCwdAsync(error) is not { } selectedCwd)
            {
                ShowStatus("Import cancelled");
                return;
            }
            try
            {
                if (!await _runtimeHost.ImportFromJsonlAsync(inputPath, selectedCwd))
                {
                    ShowStatus("Import cancelled");
                    return;
                }
                ShowStatus($"Session imported from: {inputPath}");
            }
            catch (Exception ex)
            {
                HandleFatalRuntimeError("Failed to import session", ex);
            }
        }
        catch (SessionImportFileNotFoundException error)
        {
            ShowError($"Failed to import session: {error.Message}");
        }
        catch (Exception ex)
        {
            HandleFatalRuntimeError("Failed to import session", ex);
        }
    }

    private void HandleShareCommand() => ShowError("/share is not available in Iris yet.");

    private async Task HandleCopyCommandAsync(bool flashConfirmation = false, bool preferSelection = false)
    {
        if (preferSelection && _ui is TuiAltScreen { CopyOnSelect: false } selectionUi && selectionUi.HasActiveSelection())
        {
            await selectionUi.CopyActiveSelectionToClipboardAsync();
            return;
        }
        var text = Session.GetLastAssistantText();
        if (string.IsNullOrEmpty(text))
        {
            ShowError("No agent messages to copy yet.");
            return;
        }
        try
        {
            await Clipboard.CopyAsync(text);
            if (flashConfirmation && _ui is TuiAltScreen flashUi) flashUi.Flash("Copied!");
            else ShowStatus("Copied last agent message to clipboard");
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void HandleNameCommand(string text)
    {
        var name = System.Text.RegularExpressions.Regex.Replace(text, @"^/name\s*", "").Trim();
        if (name.Length == 0)
        {
            if (SessionManager.SessionName is { } currentName)
            {
                _chatContainer.AddChild(new Spacer(1));
                _chatContainer.AddChild(new Text(Theme.Fg("dim", $"Session name: {currentName}"), 1, 0));
            }
            else
            {
                ShowWarning("Usage: /name <name>");
            }
            _ui.RequestRender();
            return;
        }
        Session.SetSessionName(name);
        var sessionName = SessionManager.SessionName;
        if (sessionName != name) ShowWarning($"Session name was normalized from {JsonSerializer.Serialize(name)} to {(sessionName is null ? "undefined" : JsonSerializer.Serialize(sessionName))}");
        _chatContainer.AddChild(new Spacer(1));
        _chatContainer.AddChild(new Text(Theme.Fg("dim", $"Session name set: {sessionName ?? name}"), 1, 0));
        _ui.RequestRender();
    }

    private static string LocaleNumber(long value) => value.ToString("#,0", CultureInfo.InvariantCulture);

    private static List<(string Key, double Cost, long Tokens)> GetUsageCostBreakdown(IEnumerable<SessionEntry> entries)
    {
        var totals = new Dictionary<string, (double Cost, long Tokens)>();
        var order = new List<string>();
        foreach (var entry in entries)
        {
            string? key = null;
            Usage? usage = null;
            if (entry is SessionMessageEntry { Message: AssistantMessage assistant })
            {
                key = $"{assistant.Provider}/{assistant.ResponseModel ?? assistant.Model}";
                usage = assistant.Usage;
            }
            else if (entry is SessionMessageEntry { Message: ToolResultMessage { Usage: { } toolUsage } })
            {
                key = "Tools/summaries";
                usage = toolUsage;
            }
            else if (entry is BranchSummaryEntry { Usage: { } branchUsage })
            {
                key = "Tools/summaries";
                usage = branchUsage;
            }
            else if (entry is CompactionEntry { Usage: { } compactionUsage })
            {
                key = "Tools/summaries";
                usage = compactionUsage;
            }
            if (key is null || usage is null) continue;
            if (!totals.TryGetValue(key, out var current)) order.Add(key);
            totals[key] = (current.Cost + usage.Cost.Total, current.Tokens + usage.Input + usage.Output + usage.CacheRead + usage.CacheWrite);
        }
        return order.Select(k => (k, totals[k].Cost, totals[k].Tokens)).Where(e => e.Cost > 0 || e.Tokens > 0).OrderByDescending(e => e.Cost).ToList();
    }

    private void HandleSessionCommand()
    {
        var stats = Session.GetSessionStats();
        var sessionName = SessionManager.SessionName;
        var entries = SessionManager.GetEntries();
        var cacheWaste = CacheStats.ComputeCacheWaste(entries, Session.ModelRuntime);
        var usageBreakdown = GetUsageCostBreakdown(entries);

        var info = new StringBuilder($"{Theme.Bold("Session Info")}\n\n");
        if (!string.IsNullOrEmpty(sessionName)) info.Append($"{Theme.Fg("dim", "Name:")} {sessionName}\n");
        info.Append($"{Theme.Fg("dim", "File:")} {stats.SessionFile ?? "In-memory"}\n");
        info.Append($"{Theme.Fg("dim", "ID:")} {stats.SessionId}\n\n");
        info.Append($"{Theme.Bold("Messages")}\n");
        info.Append($"{Theme.Fg("dim", "Total:")} {stats.TotalMessages}\n");
        info.Append($"{Theme.Fg("dim", "User:")} {stats.UserMessages}\n");
        info.Append($"{Theme.Fg("dim", "Assistant:")} {stats.AssistantMessages}\n");
        info.Append($"{Theme.Fg("dim", "Tools:")} {stats.ToolCalls} calls, {stats.ToolResults} results\n\n");
        info.Append($"{Theme.Bold("Tokens")}\n");
        var (input, cacheRead, cacheWrite) = (stats.Tokens.Input, stats.Tokens.CacheRead, stats.Tokens.CacheWrite);
        var promptTokens = input + cacheRead + cacheWrite;
        info.Append($"{Theme.Fg("dim", "Input:")} {LocaleNumber(promptTokens)}\n");
        if (promptTokens > 0 && (cacheRead > 0 || cacheWrite > 0))
        {
            var hitRate = Theme.Fg("dim", $"({NodeCompat.ToFixed((double)cacheRead / promptTokens * 100, 1)}%)");
            info.Append($"  {Theme.Fg("dim", "Cached:")} {LocaleNumber(cacheRead)} {hitRate}\n");
            var written = cacheWrite > 0 ? $" {Theme.Fg("dim", $"({LocaleNumber(cacheWrite)} written to cache)")}" : "";
            info.Append($"  {Theme.Fg("dim", "Uncached:")} {LocaleNumber(input + cacheWrite)}{written}\n");
        }
        info.Append($"{Theme.Fg("dim", "Output:")} {LocaleNumber(stats.Tokens.Output)}\n");
        info.Append($"{Theme.Fg("dim", "Total:")} {LocaleNumber(stats.Tokens.Total)}\n");

        if (stats.Cost > 0 || cacheWaste.MissedTokens > 0)
        {
            info.Append($"\n{Theme.Bold("Cost")}\n");
            info.Append($"{Theme.Fg("dim", "Total:")} ${NodeCompat.ToFixed(stats.Cost, 3)}");
            if (usageBreakdown.Count > 1)
            {
                foreach (var entry in usageBreakdown)
                {
                    info.Append($"\n  {Theme.Fg("dim", $"{entry.Key}:")} ${NodeCompat.ToFixed(entry.Cost, 3)} {Theme.Fg("dim", $"({FooterComponent.FormatTokens(entry.Tokens)} tokens)")}");
                }
            }
            if (cacheWaste.MissedTokens > 0)
            {
                var missLabel = cacheWaste.MissCount == 1 ? "1 miss" : $"{cacheWaste.MissCount} misses";
                var detail = $"{LocaleNumber(cacheWaste.MissedTokens)} tokens, {missLabel}";
                info.Append(cacheWaste.MissedCost >= 0.0001
                    ? $"\n{Theme.Fg("dim", "Cache Re-billed:")} ${NodeCompat.ToFixed(cacheWaste.MissedCost, 3)} {Theme.Fg("dim", $"({detail})")}"
                    : $"\n{Theme.Fg("dim", "Cache Re-billed:")} {detail}");
            }
        }

        _chatContainer.AddChild(new Spacer(1));
        _chatContainer.AddChild(new Text(info.ToString(), 1, 0));
        _ui.RequestRender();
    }

    private void HandleChangelogCommand()
    {
        var allEntries = Changelog.Parse(AppConfig.ChangelogPath);
        var markdown = allEntries.Count > 0
            ? string.Join("\n\n", Enumerable.Reverse(allEntries).Select(e => e.Content))
            : "No changelog entries found.";
        _chatContainer.AddChild(new Spacer(1));
        _chatContainer.AddChild(new DynamicBorder());
        _chatContainer.AddChild(new Text(Theme.Bold(Theme.Fg("accent", "What's New")), 1, 0));
        _chatContainer.AddChild(new Spacer(1));
        _chatContainer.AddChild(new MarkdownComponent(markdown, 1, 1, GetMarkdownThemeWithSettings()));
        _chatContainer.AddChild(new DynamicBorder());
        _ui.RequestRender();
    }

    private void HandleHotkeysCommand()
    {
        static string K(string action) => KeyHints.KeyDisplayText(action);
        var newLineNote = OperatingSystem.IsWindows() ? " (Ctrl+Enter on Windows Terminal)" : "";
        var hotkeys = $"""

**Navigation**
| Key | Action |
|-----|--------|
| `{K("tui.editor.cursorUp")}` / `{K("tui.editor.cursorDown")}` / `{K("tui.editor.cursorLeft")}` / `{K("tui.editor.cursorRight")}` | Move cursor / browse history |
| `{K("tui.editor.cursorWordLeft")}` / `{K("tui.editor.cursorWordRight")}` | Move by word |
| `{K("tui.editor.cursorLineStart")}` | Start of line |
| `{K("tui.editor.cursorLineEnd")}` | End of line |
| `{K("tui.editor.jumpForward")}` | Jump forward to character |
| `{K("tui.editor.jumpBackward")}` | Jump backward to character |
| `{K("tui.editor.pageUp")}` / `{K("tui.editor.pageDown")}` | Scroll by page |

**Editing**
| Key | Action |
|-----|--------|
| `{K("tui.input.submit")}` | Send message |
| `{K("tui.input.newLine")}` | New line{newLineNote} |
| `{K("tui.editor.deleteWordBackward")}` | Delete word backwards |
| `{K("tui.editor.deleteWordForward")}` | Delete word forwards |
| `{K("tui.editor.deleteToLineStart")}` | Delete to start of line |
| `{K("tui.editor.deleteToLineEnd")}` | Delete to end of line |
| `{K("tui.editor.yank")}` | Paste the most-recently-deleted text |
| `{K("tui.editor.yankPop")}` | Cycle through the deleted text after pasting |
| `{K("tui.editor.undo")}` | Undo |

**Other**
| Key | Action |
|-----|--------|
| `{K("tui.input.tab")}` | Path completion / accept autocomplete |
| `{K("app.interrupt")}` | Cancel autocomplete / abort streaming |
| `{K("app.clear")}` | Clear editor (first) / exit (second) |
| `{K("app.exit")}` | Exit (when editor is empty) |
| `{K("app.suspend")}` | Suspend to background |
| `{K("app.thinking.cycle")}` | Cycle thinking level |
| `{K("app.model.cycleForward")}` / `{K("app.model.cycleBackward")}` | Cycle models |
| `{K("app.model.select")}` | Open model selector |
| `{K("app.tools.expand")}` | Toggle tool output expansion |
| `{K("app.thinking.toggle")}` | Toggle thinking block visibility |
| `{K("app.editor.external")}` | Edit message in external editor |
| `{K("app.message.copy")}` | Copy last assistant message |
| `{K("app.message.followUp")}` | Queue follow-up message |
| `{K("app.message.dequeue")}` | Restore queued messages |
| `{K("app.clipboard.pasteImage")}` | Paste image or text from clipboard |
| `/` | Slash commands |
| `!` | Run bash command |
| `!!` | Run bash command (excluded from context) |

""";
        _chatContainer.AddChild(new Spacer(1));
        _chatContainer.AddChild(new DynamicBorder());
        _chatContainer.AddChild(new Text(Theme.Bold(Theme.Fg("accent", "Keyboard Shortcuts")), 1, 0));
        _chatContainer.AddChild(new Spacer(1));
        _chatContainer.AddChild(new MarkdownComponent(hotkeys.Trim(), 1, 1, GetMarkdownThemeWithSettings()));
        _chatContainer.AddChild(new DynamicBorder());
        _ui.RequestRender();
    }

    private async Task HandleClearCommandAsync()
    {
        ClearStatusIndicator();
        try
        {
            if (!await _runtimeHost.NewSessionAsync()) return;
            _chatContainer.AddChild(new Spacer(1));
            _chatContainer.AddChild(new Text(Theme.Fg("accent", "✓ New session started"), 1, 1));
            _ui.RequestRender();
        }
        catch (Exception ex)
        {
            HandleFatalRuntimeError("Failed to create session", ex);
        }
    }

    private void HandleDebugCommand()
    {
        var width = _ui.Terminal.Columns;
        var height = _ui.Terminal.Rows;
        var allLines = _ui.Render(width);
        var debugLogPath = AppConfig.DebugLogPath;
        var lines = new List<string>
        {
            $"Debug output at {DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ}",
            $"Terminal: {width}x{height}",
            $"Total lines: {allLines.Count}",
            "",
            "=== All rendered lines with visible widths ===",
        };
        lines.AddRange(allLines.Select((line, idx) => $"[{idx}] (w={TextUtils.VisibleWidth(line)}) {IrisJson.Stringify(JsonValueOf(line))}"));
        lines.Add("");
        lines.Add("=== Agent messages (JSONL) ===");
        lines.AddRange(Session.Messages.Select(m => IrisJson.Stringify(IrisJson.ToNode<Message>(m))));
        lines.Add("");
        Directory.CreateDirectory(Path.GetDirectoryName(debugLogPath)!);
        File.WriteAllText(debugLogPath, string.Join("\n", lines));
        _chatContainer.AddChild(new Spacer(1));
        _chatContainer.AddChild(new Text($"{Theme.Fg("accent", "✓ Debug log written")}\n{Theme.Fg("muted", debugLogPath)}", 1, 1));
        _ui.RequestRender();
    }

    private static System.Text.Json.Nodes.JsonNode JsonValueOf(string value) => System.Text.Json.Nodes.JsonValue.Create(value);

    private async Task HandleBashCommandAsync(string command, bool excludeFromContext = false)
    {
        var isDeferred = Session.IsStreaming;
        var component = new BashExecutionComponent(command, _ui, excludeFromContext);
        _bashComponent = component;
        if (isDeferred)
        {
            _pendingMessagesContainer.AddChild(component);
            _pendingBashComponents.Add(component);
        }
        else
        {
            _chatContainer.AddChild(component);
        }
        _ui.RequestRender();

        try
        {
            var result = await Session.ExecuteBashAsync(command, chunk => _dispatcher.Invoke(() =>
            {
                if (_bashComponent is null) return;
                _bashComponent.AppendOutput(chunk);
                _ui.RequestRender();
            }), excludeFromContext);
            // Let queued output chunks render before completing the component.
            await Task.Yield();
            _bashComponent?.SetComplete(result.ExitCode, result.Cancelled, result.Truncated ? new TruncationResult { Truncated = true, Content = result.Output } : null, result.FullOutputPath);
        }
        catch (Exception ex)
        {
            _bashComponent?.SetComplete(null, false);
            ShowError($"Bash command failed: {ex.Message}");
        }
        _bashComponent = null;
        _ui.RequestRender();
    }

    private async Task HandleCompactCommandAsync(string? customInstructions)
    {
        ClearStatusIndicator();
        try
        {
            await Session.CompactAsync(customInstructions);
        }
        catch
        {
            // Reported through compaction events.
        }
    }

    public void Stop()
    {
        DisposeActiveSelector();
        if (SettingsManager.ShowTerminalProgress) _ui.Terminal.SetProgress(false);
        ClearStatusIndicator();
        _themeController.DisableAutoSync();
        _footerDataProvider.Dispose();
        _unsubscribe?.Dispose();
        _unsubscribe = null;
        if (_isInitialized)
        {
            // Fullscreen: "transcript" prints the conversation to the main screen on exit; "resume-hint" leaves it clean.
            _ui.Stop(preserveScreen: _ui is TuiAltScreen && SettingsManager.FullscreenExitOutput != "transcript");
            _isInitialized = false;
        }
        UnregisterSignalHandlers();
    }
}
