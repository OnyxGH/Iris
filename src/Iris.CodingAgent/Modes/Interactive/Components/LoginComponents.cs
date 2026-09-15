using System.Diagnostics;
using System.Text.RegularExpressions;
using Iris.Ai.Auth;
using Iris.Tui;
using Iris.Tui.Components;

namespace Iris.CodingAgent.Modes.Interactive.Components;

/// <summary>Opens URLs in the default browser.</summary>
public static class BrowserLauncher
{
    public static void Open(string url)
    {
        try
        {
            ProcessStartInfo info = OperatingSystem.IsWindows()
                ? new("rundll32", $"url.dll,FileProtocolHandler {url}")
                : OperatingSystem.IsMacOS() ? new("open", url) : new("xdg-open", url);
            info.UseShellExecute = false;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            Process.Start(info);
        }
        catch
        {
            // The URL is shown in the dialog; opening a browser is best effort.
        }
    }
}

/// <summary>Replaces the editor during login flows.</summary>
public sealed class LoginDialogComponent : Container, IInputComponent, IFocusable
{
    private readonly Container _contentContainer = new();
    private readonly Input _input = new();
    private readonly TuiBase _tui;
    private readonly CancellationTokenSource _cts = new();
    private readonly Action<bool, string?> _onComplete;
    private TaskCompletionSource<string>? _pending;
    private bool _focused;

    public LoginDialogComponent(TuiBase tui, string providerId, Action<bool, string?> onComplete, string? providerNameOverride = null, string? titleOverride = null)
    {
        _tui = tui;
        _onComplete = onComplete;
        var theme = ThemeManager.Current;
        var providerName = string.IsNullOrEmpty(providerNameOverride) ? providerId : providerNameOverride;
        var title = titleOverride ?? $"Login to {providerName}";
        AddChild(new DynamicBorder());
        AddChild(new Text(theme.Fg("accent", theme.Bold(title)), 1, 0));
        AddChild(_contentContainer);
        _input.OnSubmit = _ =>
        {
            if (_pending is null) return;
            var value = _input.GetValue();
            ReplaceInputWithSubmittedText(value);
            var pending = _pending;
            _pending = null;
            pending.TrySetResult(value);
        };
        _input.OnEscape = Cancel;
        AddChild(new DynamicBorder());
    }

    public bool Focused
    {
        get => _focused;
        set
        {
            _focused = value;
            _input.Focused = value;
        }
    }

    public CancellationToken CancellationToken => _cts.Token;

    private static string Link(string url, string text) => $"\e]8;;{url}\a{text}\e]8;;\a";

    private void ReplaceInputWithSubmittedText(string value) =>
        _contentContainer.Children = _contentContainer.Children.Select(child => ReferenceEquals(child, _input) ? new Text($"> {value}", 0, 0) : child).ToList();

    private void Cancel()
    {
        _cts.Cancel();
        if (_pending is not null)
        {
            var pending = _pending;
            _pending = null;
            pending.TrySetException(new OperationCanceledException("Login cancelled"));
        }
        _onComplete(false, "Login cancelled");
    }

    public void ShowAuth(string url, string? instructions)
    {
        var theme = ThemeManager.Current;
        _contentContainer.Clear();
        _contentContainer.AddChild(new Spacer(1));
        _contentContainer.AddChild(new Text(theme.Fg("accent", Link(url, url)), 1, 0));
        var clickHint = OperatingSystem.IsMacOS() ? "Cmd+click to open" : "Ctrl+click to open";
        _contentContainer.AddChild(new Text(theme.Fg("dim", Link(url, clickHint)), 1, 0));
        if (!string.IsNullOrEmpty(instructions))
        {
            _contentContainer.AddChild(new Spacer(1));
            _contentContainer.AddChild(new Text(theme.Fg("warning", instructions), 1, 0));
        }
        BrowserLauncher.Open(url);
        _tui.RequestRender();
    }

    public void ShowDeviceCode(AuthDeviceCodeEvent info)
    {
        var theme = ThemeManager.Current;
        _contentContainer.Clear();
        _contentContainer.AddChild(new Spacer(1));
        _contentContainer.AddChild(new Text(theme.Fg("accent", Link(info.VerificationUri, info.VerificationUri)), 1, 0));
        var clickHint = OperatingSystem.IsMacOS() ? "Cmd+click to open" : "Ctrl+click to open";
        _contentContainer.AddChild(new Text(theme.Fg("dim", Link(info.VerificationUri, clickHint)), 1, 0));
        _contentContainer.AddChild(new Spacer(1));
        _contentContainer.AddChild(new Text(theme.Fg("warning", $"Enter code: {info.UserCode}"), 1, 0));
        _tui.RequestRender();
    }

    private Task<string> WaitForInput()
    {
        _pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        return _pending.Task;
    }

    public Task<string> ShowManualInput(string prompt)
    {
        _input.SetValue("");
        _contentContainer.AddChild(new Spacer(1));
        _contentContainer.AddChild(new Text(ThemeManager.Current.Fg("dim", prompt), 1, 0));
        _contentContainer.AddChild(_input);
        _contentContainer.AddChild(new Text($"({KeyHints.KeyHint("tui.select.cancel", "to cancel")})", 1, 0));
        _tui.RequestRender();
        return WaitForInput();
    }

    public Task<string> ShowPrompt(string message, string? placeholder)
    {
        var theme = ThemeManager.Current;
        _contentContainer.AddChild(new Spacer(1));
        _contentContainer.AddChild(new Text(theme.Fg("text", message), 1, 0));
        if (!string.IsNullOrEmpty(placeholder)) _contentContainer.AddChild(new Text(theme.Fg("dim", $"e.g., {placeholder}"), 1, 0));
        _contentContainer.AddChild(_input);
        _contentContainer.AddChild(new Text($"({KeyHints.KeyHint("tui.select.cancel", "to cancel,")} {KeyHints.KeyHint("tui.select.confirm", "to submit")})", 1, 0));
        _input.SetValue("");
        _tui.RequestRender();
        return WaitForInput();
    }

    public void ShowDetails(IEnumerable<string> lines)
    {
        _contentContainer.Clear();
        _contentContainer.AddChild(new Spacer(1));
        foreach (var line in lines) _contentContainer.AddChild(new Text(line, 1, 0));
        _tui.RequestRender();
    }

    public void ShowInfo(string message, IReadOnlyList<AuthInfoLink>? links = null, bool showCloseHint = false)
    {
        var theme = ThemeManager.Current;
        _contentContainer.AddChild(new Spacer(1));
        _contentContainer.AddChild(new Text(theme.Fg("text", message), 1, 0));
        foreach (var link in links ?? [])
        {
            var text = link.Label is { Length: > 0 } label ? $"{label}: {link.Url}" : link.Url;
            _contentContainer.AddChild(new Text(theme.Fg("accent", Link(link.Url, text)), 1, 0));
        }
        if (showCloseHint)
        {
            _contentContainer.AddChild(new Spacer(1));
            _contentContainer.AddChild(new Text($"({KeyHints.KeyHint("tui.select.cancel", "to close")})", 1, 0));
        }
        _tui.RequestRender();
    }

    public void ShowWaiting(string message)
    {
        _contentContainer.AddChild(new Spacer(1));
        _contentContainer.AddChild(new Text(ThemeManager.Current.Fg("dim", message), 1, 0));
        _contentContainer.AddChild(new Text($"({KeyHints.KeyHint("tui.select.cancel", "to cancel")})", 1, 0));
        _tui.RequestRender();
    }

    public void ShowProgress(string message)
    {
        _contentContainer.AddChild(new Text(ThemeManager.Current.Fg("dim", message), 1, 0));
        _tui.RequestRender();
    }

    public void HandleInput(string data)
    {
        if (KeybindingsManager.Global.Matches(data, "tui.select.cancel"))
        {
            Cancel();
            return;
        }
        _input.HandleInput(data);
    }
}

/// <summary>A provider/auth-method pair offered by /login and /logout.</summary>
public sealed record AuthSelectorProvider(string Id, string Name, string AuthType, string? MethodName = null, bool HasLogin = false, string? LoginLabel = null, AuthCheck? Status = null)
{
    public static string FormatType(string authType) => authType == AuthTypes.OAuth ? "subscription" : "API key";
}

/// <summary>Provider selector for /login and /logout.</summary>
public sealed partial class OAuthSelectorComponent : Container, IInputComponent, IFocusable
{
    private readonly Input _searchInput = new();
    private readonly Container _listContainer = new();
    private readonly List<AuthSelectorProvider> _allProviders;
    private List<AuthSelectorProvider> _filteredProviders;
    private int _selectedIndex;
    private readonly string _mode;
    private readonly Action<string, string> _onSelect;
    private readonly Action _onCancel;
    private readonly bool _showAuthTypeLabels;
    private bool _focused;

    [GeneratedRegex("^[A-Z][A-Z0-9_]*(?:, [A-Z][A-Z0-9_]*)*$")]
    private static partial Regex EnvVarList();

    public OAuthSelectorComponent(string mode, List<AuthSelectorProvider> providers, Action<string, string> onSelect, Action onCancel, string? initialSearchInput = null)
    {
        var theme = ThemeManager.Current;
        _mode = mode;
        _allProviders = providers;
        _filteredProviders = providers;
        _showAuthTypeLabels = providers.Select(p => p.AuthType).Distinct().Count() > 1;
        _onSelect = onSelect;
        _onCancel = onCancel;

        AddChild(new DynamicBorder());
        AddChild(new Spacer(1));
        var title = mode == "login" ? "Select provider to configure:" : "Select provider to logout:";
        AddChild(new TruncatedText(theme.Fg("accent", theme.Bold(title)), 1, 0));
        AddChild(new Spacer(1));
        if (!string.IsNullOrEmpty(initialSearchInput)) _searchInput.SetValue(initialSearchInput);
        _searchInput.OnSubmit = _ => SelectCurrent();
        AddChild(_searchInput);
        AddChild(new Spacer(1));
        AddChild(_listContainer);
        AddChild(new Spacer(1));
        AddChild(new DynamicBorder());
        FilterProviders(initialSearchInput ?? "");
    }

    public bool Focused
    {
        get => _focused;
        set
        {
            _focused = value;
            _searchInput.Focused = value;
        }
    }

    private void SelectCurrent()
    {
        if (_selectedIndex < _filteredProviders.Count)
        {
            var provider = _filteredProviders[_selectedIndex];
            _onSelect(provider.Id, provider.AuthType);
        }
    }

    private void FilterProviders(string query)
    {
        _filteredProviders = query.Length > 0
            ? Fuzzy.Filter(_allProviders, query, p => $"{p.Name} {p.Id} {p.AuthType} {p.MethodName ?? ""}")
            : _allProviders;
        _selectedIndex = Math.Max(0, Math.Min(_selectedIndex, Math.Max(0, _filteredProviders.Count - 1)));
        UpdateList();
    }

    private void UpdateList()
    {
        var theme = ThemeManager.Current;
        _listContainer.Clear();
        const int maxVisible = 8;
        var startIndex = Math.Max(0, Math.Min(_selectedIndex - maxVisible / 2, _filteredProviders.Count - maxVisible));
        var endIndex = Math.Min(startIndex + maxVisible, _filteredProviders.Count);
        for (var i = startIndex; i < endIndex; i++)
        {
            var provider = _filteredProviders[i];
            var statusIndicator = FormatStatusIndicator(provider);
            var authTypeLabel = _showAuthTypeLabels ? theme.Fg("muted", $" [{AuthSelectorProvider.FormatType(provider.AuthType)}]") : "";
            var line = i == _selectedIndex
                ? theme.Fg("accent", "→ ") + theme.Fg("accent", provider.Name) + authTypeLabel + statusIndicator
                : $"  {theme.Fg("text", provider.Name)}" + authTypeLabel + statusIndicator;
            _listContainer.AddChild(new TruncatedText(line, 1, 0));
        }
        if (startIndex > 0 || endIndex < _filteredProviders.Count)
        {
            _listContainer.AddChild(new TruncatedText(theme.Fg("muted", $"  ({_selectedIndex + 1}/{_filteredProviders.Count})"), 1, 0));
        }
        if (_filteredProviders.Count == 0)
        {
            var message = _allProviders.Count == 0
                ? _mode == "login" ? "No providers available" : "No providers logged in. Use /login first."
                : "No matching providers";
            _listContainer.AddChild(new TruncatedText(theme.Fg("muted", $"  {message}"), 1, 0));
        }
    }

    private static string FormatStatusIndicator(AuthSelectorProvider provider)
    {
        var theme = ThemeManager.Current;
        if (provider.Status is null) return theme.Fg("muted", " • unconfigured");
        if (provider.Status.Type != provider.AuthType)
        {
            var label = provider.Status.Type == AuthTypes.OAuth ? "subscription configured" : "API key configured";
            return theme.Fg("muted", " • ") + theme.Fg("warning", label);
        }
        if (string.IsNullOrEmpty(provider.Status.Source) || provider.Status.Source is "OAuth" or "stored credential")
        {
            return theme.Fg("success", " ✓ configured");
        }
        var source = EnvVarList().IsMatch(provider.Status.Source) ? $"env: {provider.Status.Source}" : provider.Status.Source;
        return theme.Fg("success", $" ✓ {source}");
    }

    public void HandleInput(string keyData)
    {
        var kb = KeybindingsManager.Global;
        if (kb.Matches(keyData, "tui.select.up"))
        {
            if (_filteredProviders.Count == 0) return;
            _selectedIndex = Math.Max(0, _selectedIndex - 1);
            UpdateList();
        }
        else if (kb.Matches(keyData, "tui.select.down"))
        {
            if (_filteredProviders.Count == 0) return;
            _selectedIndex = Math.Min(_filteredProviders.Count - 1, _selectedIndex + 1);
            UpdateList();
        }
        else if (kb.Matches(keyData, "tui.select.confirm"))
        {
            SelectCurrent();
        }
        else if (kb.Matches(keyData, "tui.select.cancel"))
        {
            _onCancel();
        }
        else
        {
            _searchInput.HandleInput(keyData);
            FilterProviders(_searchInput.GetValue());
        }
    }
}
