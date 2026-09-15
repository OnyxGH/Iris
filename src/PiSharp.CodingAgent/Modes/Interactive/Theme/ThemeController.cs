using PiSharp.CodingAgent.Core;
using PiSharp.Tui;

namespace PiSharp.CodingAgent.Modes.Interactive;

/// <summary>Applies theme settings, including automatic light/dark sync. Port of theme-controller.ts.</summary>
public sealed class InteractiveThemeController
{
    private readonly TuiBase _ui;
    private readonly Func<SettingsManager> _getSettingsManager;
    private readonly Action<string> _showError;
    private readonly Action _onChanged;
    private string? _currentThemeSetting;
    private string _terminalTheme = ThemeManager.DetectTerminalBackgroundFromEnv().Theme;
    private string? _activeThemeName;
    private bool _autoSyncEnabled;
    private Action? _unsubscribeColorScheme;

    public InteractiveThemeController(TuiBase ui, Func<SettingsManager> getSettingsManager, Action<string> showError, Action onChanged, string? initialThemeSetting = null)
    {
        _ui = ui;
        _getSettingsManager = getSettingsManager;
        _showError = showError;
        _onChanged = onChanged;
        _currentThemeSetting = initialThemeSetting;
        _activeThemeName = ThemeManager.ResolveThemeSetting(_currentThemeSetting ?? getSettingsManager().ThemeSetting, _terminalTheme);
        ThemeManager.InitTheme(_activeThemeName, true);
        BindColorSchemeListener();
    }

    public void RebindTui()
    {
        _unsubscribeColorScheme?.Invoke();
        BindColorSchemeListener();
        _ui.SetTerminalColorSchemeNotifications(_autoSyncEnabled);
    }

    public async Task ApplyFromSettingsAsync()
    {
        var settings = _getSettingsManager();
        var themeSetting = _currentThemeSetting ?? settings.ThemeSetting;
        if (ThemeManager.ParseAutoThemeSetting(themeSetting) is { } auto)
        {
            _terminalTheme = await ThemeManager.DetectTerminalThemeForAutoAsync(_ui, 100);
            SetAutoSync(true);
            ApplyThemeName(_terminalTheme == "light" ? auto.LightTheme : auto.DarkTheme, true);
            return;
        }

        SetAutoSync(false);
        if (themeSetting is not null)
        {
            ApplyThemeName(themeSetting, true);
            return;
        }

        var detection = await ThemeManager.DetectTerminalBackgroundThemeAsync(_ui, 100);
        _terminalTheme = detection.Theme;
        if (!ApplyThemeName(detection.Theme).Success) return;
        if (detection.Confidence == "high")
        {
            settings.SetTheme(detection.Theme);
            await settings.FlushAsync();
        }
    }

    public string? GetThemeSelection() => _currentThemeSetting ?? _getSettingsManager().ThemeSetting ?? _activeThemeName;

    public (bool Success, string? Error) SetThemeName(string themeName, bool showError = false)
    {
        SetAutoSync(false);
        var result = ApplyThemeName(themeName, showError);
        if (result.Success) _currentThemeSetting = themeName;
        return result;
    }

    public async Task SetThemeSettingAsync(string themeSetting)
    {
        _currentThemeSetting = themeSetting;
        await ApplyFromSettingsAsync();
    }

    public void SetThemeInstance(Theme theme)
    {
        SetAutoSync(false);
        ThemeManager.SetThemeInstance(theme);
        _activeThemeName = "<in-memory>";
        NotifyChanged();
    }

    public void Preview(string themeSettingOrName)
    {
        var themeName = ThemeManager.ResolveThemeSetting(themeSettingOrName, _terminalTheme) ?? _activeThemeName;
        if (themeName is null) return;
        if (ThemeManager.SetTheme(themeName, true).Success)
        {
            _ui.Invalidate();
            _ui.RequestRender();
        }
    }

    public void DisableAutoSync() => SetAutoSync(false);

    public void Dispose()
    {
        SetAutoSync(false);
        _unsubscribeColorScheme?.Invoke();
        _unsubscribeColorScheme = null;
    }

    public string GetTerminalTheme() => _terminalTheme;

    private (bool Success, string? Error) ApplyThemeName(string themeName, bool showError = false)
    {
        var result = ThemeManager.SetTheme(themeName, true);
        _activeThemeName = result.Success ? themeName : "dark";
        NotifyChanged();
        if (!result.Success && showError) _showError($"Failed to load theme \"{themeName}\": {result.Error}\nFell back to dark theme.");
        return result;
    }

    private void NotifyChanged()
    {
        _ui.Invalidate();
        _onChanged();
    }

    private void SetAutoSync(bool enabled)
    {
        if (_autoSyncEnabled == enabled) return;
        _autoSyncEnabled = enabled;
        _ui.SetTerminalColorSchemeNotifications(enabled);
    }

    private void BindColorSchemeListener() => _unsubscribeColorScheme = _ui.OnTerminalColorSchemeChange(ApplyTerminalTheme);

    private void ApplyTerminalTheme(string terminalTheme)
    {
        if (!_autoSyncEnabled) return;
        _terminalTheme = terminalTheme;
        if (ThemeManager.ParseAutoThemeSetting(_currentThemeSetting ?? _getSettingsManager().ThemeSetting) is not { } auto)
        {
            SetAutoSync(false);
            return;
        }
        var themeName = terminalTheme == "light" ? auto.LightTheme : auto.DarkTheme;
        if (themeName != _activeThemeName) ApplyThemeName(themeName);
    }
}
