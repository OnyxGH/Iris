using Iris.CodingAgent.Config;
using Iris.CodingAgent.Core;
using Iris.CodingAgent.Modes.Interactive;
using Iris.CodingAgent.Modes.Interactive.Components;
using Iris.Tui;

namespace Iris.CodingAgent.Cli;

/// <summary>Short-lived TUIs shown before interactive mode starts.</summary>
public static class StartupUi
{
    /// <summary>Run a function on a dedicated UI dispatcher thread and return its result.</summary>
    public static Task<T> RunOnUiThreadAsync<T>(Func<Task<T>> main)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                tcs.TrySetResult(UiDispatcher.Run(main));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        })
        {
            Name = "Iris UI",
            IsBackground = true,
        };
        thread.Start();
        return tcs.Task;
    }

    private static TuiMainScreen CreateStartupTui(SettingsManager settingsManager)
    {
        var overrides = settingsManager.TerminalCapabilityOverrides;
        TerminalImage.SetCapabilityOverrides(
            Ai.Json.IrisJson.GetBool(overrides["trueColor"]),
            Ai.Json.IrisJson.GetBool(overrides["hyperlinks"]),
            overrides.ContainsKey("images"),
            Ai.Json.IrisJson.GetString(overrides["images"]));
        var terminalTheme = ThemeManager.DetectTerminalBackgroundFromEnv().Theme;
        ThemeManager.InitTheme(ThemeManager.ResolveThemeSetting(settingsManager.ThemeSetting, terminalTheme) ?? terminalTheme);
        KeybindingsManager.Global = AppKeybindings.Create();
        var ui = new TuiMainScreen(new ProcessTerminal(), UiDispatcher.Current!, settingsManager.ShowHardwareCursor, AppConfig.AgentDir);
        ui.SetClearOnShrink(settingsManager.ClearOnShrink);
        return ui;
    }

    private static void StartStartupTui(TuiBase ui, SettingsManager settingsManager)
    {
        ui.Start();
        _ = ApplyDetectedStartupThemeAsync(ui, settingsManager);
    }

    private static async Task ApplyDetectedStartupThemeAsync(TuiBase ui, SettingsManager settingsManager)
    {
        var themeSetting = settingsManager.ThemeSetting;
        if (themeSetting is not null && ThemeManager.ParseAutoThemeSetting(themeSetting) is null) return;
        var terminalTheme = await ThemeManager.DetectTerminalThemeForAutoAsync(ui, 100);
        ThemeManager.SetTheme(ThemeManager.ResolveThemeSetting(themeSetting, terminalTheme) ?? terminalTheme);
        ui.Invalidate();
        ui.RequestRender();
    }

    private static async Task ClearStartupTuiAsync(TuiBase ui)
    {
        ui.Clear();
        ui.RequestRender();
        await Task.Delay(25);
    }

    public static Task<T?> ShowSelectorAsync<T>(SettingsManager settingsManager, string title, List<(string Label, T Value)> options) where T : class =>
        RunOnUiThreadAsync(() =>
        {
            var ui = CreateStartupTui(settingsManager);
            var tcs = new TaskCompletionSource<T?>();
            var settled = false;
            async Task Finish(T? result)
            {
                if (settled) return;
                settled = true;
                await ClearStartupTuiAsync(ui);
                ui.Stop();
                ThemeManager.StopThemeWatcher();
                tcs.TrySetResult(result);
            }
            ExtensionSelectorComponent? selector = null;
            selector = new ExtensionSelectorComponent(title, options.Select(o => o.Label).ToList(),
                option => _ = Finish(options.FirstOrDefault(o => o.Label == option).Value),
                () => _ = Finish(null),
                ui);
            ui.AddChild(selector);
            ui.SetFocus(selector);
            StartStartupTui(ui, settingsManager);
            return tcs.Task;
        });

    /// <summary>Interactive project trust prompt for <see cref="ProjectTrustStore.ResolveProjectTrustedAsync"/>.</summary>
    public static Func<string, List<ProjectTrustOption>, Task<ProjectTrustOption?>> CreateTrustPrompt(SettingsManager settingsManager) =>
        (title, options) => ShowSelectorAsync(settingsManager, title, options.Select(o => (o.Label, o)).ToList());

    /// <summary>TUI session selector for --resume. Returns the selected session path, or null when cancelled.</summary>
    public static Task<string?> SelectSessionAsync(SessionSelectorComponent.SessionsLoader currentSessionsLoader, SessionSelectorComponent.SessionsLoader allSessionsLoader, SettingsManager settingsManager) =>
        RunOnUiThreadAsync(() =>
        {
            var ui = CreateStartupTui(settingsManager);
            var tcs = new TaskCompletionSource<string?>();
            var resolved = false;
            var selector = new SessionSelectorComponent(currentSessionsLoader, allSessionsLoader,
                path =>
                {
                    if (resolved) return;
                    resolved = true;
                    ui.Stop();
                    tcs.TrySetResult(path);
                },
                () =>
                {
                    if (resolved) return;
                    resolved = true;
                    ui.Stop();
                    tcs.TrySetResult(null);
                },
                () =>
                {
                    ui.Stop();
                    Environment.Exit(0);
                },
                () => ui.RequestRender(),
                showRenameHint: false,
                keybindings: KeybindingsManager.Global);
            ui.AddChild(selector);
            ui.SetFocus(selector.GetSessionList());
            StartStartupTui(ui, settingsManager);
            return tcs.Task;
        });
}
