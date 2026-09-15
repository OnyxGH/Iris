using PiSharp.Ai;
using PiSharp.Ai.Auth;
using PiSharp.Ai.Models;
using PiSharp.CodingAgent.Config;
using PiSharp.CodingAgent.Core;
using PiSharp.CodingAgent.Extensions.Llama;
using PiSharp.CodingAgent.Modes.Interactive.Components;
using PiSharp.Tui;
using PiSharp.Tui.Components;

namespace PiSharp.CodingAgent.Modes.Interactive;

public sealed partial class InteractiveMode
{
    private const string LoginCancelledMessage = "Login cancelled";

    private sealed class DialogAuthInteraction(InteractiveMode mode, LoginDialogComponent dialog) : IAuthInteraction
    {
        public CancellationToken CancellationToken => dialog.CancellationToken;

        public Task<string> PromptAsync(AuthPrompt prompt) => mode.ShowAuthPromptAsync(dialog, prompt);

        public void Notify(AuthEvent authEvent) => mode._dispatcher.Invoke(() => mode.NotifyAuthDialog(dialog, authEvent));
    }

    private static bool IsLoginCancelled(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is OperationCanceledException || current.Message == LoginCancelledMessage) return true;
        }
        return false;
    }

    // ----- Provider options -----

    private List<AuthSelectorProvider> GetLoginProviderOptions(string? authType = null)
    {
        var runtime = Session.ModelRuntime;
        var options = new List<AuthSelectorProvider>();
        foreach (var provider in runtime.GetProviders())
        {
            var authStatus = runtime.GetProviderAuthStatus(provider.Id);
            var status = authStatus.Configured
                ? new AuthCheck(runtime.IsUsingOAuth(provider.Id) ? AuthTypes.OAuth : AuthTypes.ApiKey, authStatus.Label ?? authStatus.Source)
                : null;
            if (authType is null or AuthTypes.OAuth && provider.Auth.OAuth is { } oauth)
            {
                options.Add(new AuthSelectorProvider(provider.Id, provider.Name, AuthTypes.OAuth, oauth.Name, true, oauth.LoginLabel, status));
            }
            if (authType is null or AuthTypes.ApiKey && provider.Auth.ApiKey is { } apiKey)
            {
                options.Add(new AuthSelectorProvider(provider.Id, provider.Name, AuthTypes.ApiKey, apiKey.Name, apiKey.Login is not null, null, status));
            }
        }
        options.Sort((a, b) => NodeCompare.LocaleCompare(a.Name, b.Name));
        return options;
    }

    private async Task<List<AuthSelectorProvider>> GetLogoutProviderOptionsAsync()
    {
        using var cts = new CancellationTokenSource(15_000);
        var credentials = await Session.ModelRuntime.ListCredentialsAsync(cts.Token);
        var options = credentials
            .Select(c => new AuthSelectorProvider(c.ProviderId, Session.ModelRuntime.GetProvider(c.ProviderId)?.Name ?? c.ProviderId, c.Type, Status: new AuthCheck(c.Type, "stored credential")))
            .ToList();
        options.Sort((a, b) => NodeCompare.LocaleCompare(a.Name, b.Name));
        return options;
    }

    private List<AuthSelectorProvider> FindLoginProviderOptions(string providerRef)
    {
        var normalized = providerRef.Trim().ToLowerInvariant();
        if (normalized.Length == 0) return [];
        return GetLoginProviderOptions().Where(p => p.Id.ToLowerInvariant() == normalized || p.Name.ToLowerInvariant() == normalized).ToList();
    }

    private static readonly Dictionary<string, int> AuthTypeOrder = new() { [AuthTypes.OAuth] = 0, [AuthTypes.ApiKey] = 1 };

    private List<AutocompleteItem>? GetLoginArgumentCompletions(string prefix)
    {
        var byId = new Dictionary<string, (string Id, string Name, List<string> AuthTypes)>();
        foreach (var provider in GetLoginProviderOptions())
        {
            if (byId.TryGetValue(provider.Id, out var existing))
            {
                if (!existing.AuthTypes.Contains(provider.AuthType))
                {
                    existing.AuthTypes.Add(provider.AuthType);
                    existing.AuthTypes.Sort((a, b) => AuthTypeOrder[a] - AuthTypeOrder[b]);
                }
                continue;
            }
            byId[provider.Id] = (provider.Id, provider.Name, [provider.AuthType]);
        }
        var providers = byId.Values.ToList();
        providers.Sort((a, b) => NodeCompare.LocaleCompare(a.Name, b.Name));
        return FuzzyItems(providers, prefix,
            p => $"{p.Id} {p.Name} {string.Join(" ", p.AuthTypes.Select(t => $"{t} {AuthSelectorProvider.FormatType(t)}"))}",
            p =>
            {
                var types = string.Join("/", p.AuthTypes.Select(AuthSelectorProvider.FormatType));
                return new AutocompleteItem(p.Id, p.Id, p.Name == p.Id ? types : $"{p.Name} · {types}");
            });
    }

    // ----- /login -----

    private async Task HandleLoginCommandAsync(string? providerRef)
    {
        if (string.IsNullOrEmpty(providerRef))
        {
            ShowLoginAuthTypeSelector();
            return;
        }
        var providerOptions = FindLoginProviderOptions(providerRef);
        if (providerOptions.Count == 1)
        {
            await StartProviderLoginAsync(providerOptions[0]);
            return;
        }
        if (providerOptions.Count > 1 && providerOptions.Select(p => p.Id).Distinct().Count() == 1)
        {
            ShowLoginAuthTypeSelector(providerOptions);
            return;
        }
        ShowLoginProviderSelector(null, providerRef);
    }

    private async Task StartProviderLoginAsync(AuthSelectorProvider provider)
    {
        if (provider.AuthType == AuthTypes.OAuth) await ShowLoginDialogAsync(provider.Id, provider.Name, AuthTypes.OAuth);
        else if (provider.HasLogin) await ShowLoginDialogAsync(provider.Id, provider.Name, AuthTypes.ApiKey);
        else ShowAmbientAuthDialog(provider);
    }

    private void ShowLoginAuthTypeSelector(List<AuthSelectorProvider>? providerOptions = null)
    {
        var oauthProvider = providerOptions?.FirstOrDefault(p => p.AuthType == AuthTypes.OAuth);
        var subscriptionLabel = oauthProvider?.LoginLabel ?? "Sign in with an account";
        const string apiKeyLabel = "Sign in with an API key";
        var available = providerOptions is not null ? providerOptions.Select(p => p.AuthType).ToHashSet() : [AuthTypes.OAuth, AuthTypes.ApiKey];
        var options = new List<string>();
        if (available.Contains(AuthTypes.OAuth)) options.Add(subscriptionLabel);
        if (available.Contains(AuthTypes.ApiKey)) options.Add(apiKeyLabel);
        if (options.Count == 0)
        {
            ShowStatus("No login methods available.");
            return;
        }
        if (providerOptions is not null && options.Count == 1)
        {
            if (providerOptions.Count > 0) _ = StartProviderLoginAsync(providerOptions[0]);
            return;
        }
        var title = providerOptions is { Count: > 0 } ? $"Select authentication method for {providerOptions[0].Name}:" : "Select authentication method:";
        ShowSelector(done =>
        {
            var selector = new ExtensionSelectorComponent(title, options,
                option =>
                {
                    done();
                    var authType = option == subscriptionLabel ? AuthTypes.OAuth : AuthTypes.ApiKey;
                    if (providerOptions is not null)
                    {
                        if (providerOptions.FirstOrDefault(p => p.AuthType == authType) is { } match) _ = StartProviderLoginAsync(match);
                        return;
                    }
                    ShowLoginProviderSelector(authType);
                },
                () =>
                {
                    done();
                    _ui.RequestRender();
                });
            return (selector, selector, null);
        });
    }

    private void ShowLoginProviderSelector(string? authType = null, string? initialSearchInput = null)
    {
        var providerOptions = GetLoginProviderOptions(authType);
        if (providerOptions.Count == 0)
        {
            ShowStatus(authType switch
            {
                AuthTypes.OAuth => "No subscription providers available.",
                AuthTypes.ApiKey => "No API key providers available.",
                _ => "No login providers available.",
            });
            return;
        }
        ShowSelector(done =>
        {
            var selector = new OAuthSelectorComponent("login", providerOptions,
                (providerId, selectedAuthType) =>
                {
                    done();
                    if (providerOptions.FirstOrDefault(p => p.Id == providerId && p.AuthType == selectedAuthType) is { } option) _ = StartProviderLoginAsync(option);
                },
                () =>
                {
                    done();
                    if (authType is not null) ShowLoginAuthTypeSelector();
                    else _ui.RequestRender();
                },
                initialSearchInput);
            return (selector, selector, null);
        });
    }

    private async Task ShowLogoutSelectorAsync()
    {
        List<AuthSelectorProvider> providerOptions;
        try
        {
            providerOptions = await GetLogoutProviderOptionsAsync();
        }
        catch (Exception ex)
        {
            ShowError($"Could not read stored credentials: {ex.Message}");
            return;
        }
        if (providerOptions.Count == 0)
        {
            ShowStatus("No stored credentials to remove. /logout only removes credentials saved by /login; environment variables and models.json config are unchanged.");
            return;
        }
        ShowSelector(done =>
        {
            var selector = new OAuthSelectorComponent("logout", providerOptions,
                (providerId, _selectedType) =>
                {
                    done();
                    if (providerOptions.FirstOrDefault(p => p.Id == providerId) is { } option) _ = LogoutAsync(option);
                },
                () =>
                {
                    done();
                    _ui.RequestRender();
                });
            return (selector, selector, null);
        });

        async Task LogoutAsync(AuthSelectorProvider option)
        {
            try
            {
                using var cts = new CancellationTokenSource(15_000);
                await Session.ModelRuntime.LogoutAsync(option.Id, cts.Token);
                UpdateAvailableProviderCount();
                ShowStatus(option.AuthType == AuthTypes.OAuth
                    ? $"Logged out of {option.Name}"
                    : $"Removed stored API key for {option.Name}. Environment variables and models.json config are unchanged.");
            }
            catch (Exception ex)
            {
                ShowError($"Logout failed: {ex.Message}");
            }
        }
    }

    private static string LlamaCppPostLoginGuidance(string actionLabel, int loadedModelCount) =>
        loadedModelCount == 0
            ? $"{actionLabel}. No llama.cpp models are loaded. Use /llama to load a model, then /model to select it."
            : $"{actionLabel}. Use /model to select a loaded llama.cpp model, or /llama to manage models.";

    private async Task CompleteProviderAuthenticationAsync(string providerId, string providerName, string authType, Model? previousModel)
    {
        var actionLabel = authType == AuthTypes.OAuth ? $"Logged in to {providerName}" : $"Saved API key for {providerName}";
        var authPath = Path.Combine(_runtimeHost.Services.AgentDir, "auth.json");
        var session = Session;
        var defaultModelId = ModelResolver.DefaultModelPerProvider.FirstOrDefault(kv => kv.Key == providerId).Value;
        var unknownPrevious = previousModel is null;
        // Deviation from pi: llama.cpp catalogs are always empty before the first refresh after login, so pi reports
        // "no models are loaded" even when the server has loaded models. Wait for the refresh before giving guidance.
        var deferSelection = unknownPrevious
            && (providerId == LlamaProvider.ProviderId
                || (defaultModelId is not null && !session.ModelRuntime.AvailableSnapshot.Any(m => m.Provider == providerId && m.Id == defaultModelId)));
        string? guidance = null;

        async Task FinishAuthenticationAsync()
        {
            Model? selectedModel = null;
            string? selectionError = null;
            if (unknownPrevious)
            {
                var providerModels = Session.ModelRuntime.AvailableSnapshot.Where(m => m.Provider == providerId).ToList();
                if (providerId == LlamaProvider.ProviderId)
                {
                    // With loaded models this is guidance, not a failure.
                    if (providerModels.Count == 0) selectionError = LlamaCppPostLoginGuidance(actionLabel, 0);
                    else guidance = LlamaCppPostLoginGuidance(actionLabel, providerModels.Count);
                }
                else if (defaultModelId is null)
                {
                    selectionError = $"{actionLabel}, but no default model is configured for provider \"{providerId}\". Use /model to select a model.";
                }
                else if (providerModels.Count == 0)
                {
                    selectionError = $"{actionLabel}, but no models are available for that provider. Use /model to select a model.";
                }
                else
                {
                    selectedModel = providerModels.FirstOrDefault(m => m.Id == defaultModelId) ?? (providerId == "radius" ? providerModels[0] : null);
                    if (selectedModel is null)
                    {
                        selectionError = $"{actionLabel}, but its default model \"{defaultModelId}\" is not available. Use /model to select a model.";
                    }
                    else
                    {
                        try
                        {
                            await Session.SetModelAsync(selectedModel, persist: true);
                        }
                        catch (Exception ex)
                        {
                            selectedModel = null;
                            selectionError = $"{actionLabel}, but selecting its default model failed: {ex.Message}. Use /model to select a model.";
                        }
                    }
                }
            }

            UpdateAvailableProviderCount();
            _footer.Invalidate();
            UpdateEditorBorderColor();
            if (selectedModel is not null)
            {
                ShowStatus($"{actionLabel}. Selected {selectedModel.Id}. Credentials saved to {authPath}");
                _ = MaybeWarnAboutAnthropicSubscriptionAuthAsync(selectedModel);
            }
            else
            {
                ShowStatus(guidance is not null ? $"{guidance} Credentials saved to {authPath}" : $"{actionLabel}. Credentials saved to {authPath}");
                if (selectionError is not null) ShowError(selectionError);
                else _ = MaybeWarnAboutAnthropicSubscriptionAuthAsync();
            }
        }

        if (deferSelection) ShowStatus($"{actionLabel}. Credentials saved to {authPath}. Refreshing model catalog…");
        else await FinishAuthenticationAsync();

        using var cts = new CancellationTokenSource(15_000);
        try
        {
            var result = await session.ModelRuntime.RefreshAsync(new ModelsRefreshOptions { Providers = [providerId], CancellationToken = cts.Token });
            if (result.Aborted) ShowWarning($"{actionLabel}, but its model catalog refresh timed out; using cached models.");
            else if (result.Errors.Count > 0) ShowWarning($"{actionLabel}, but its model catalog could not be refreshed; using cached models.");
            // Do not replace a model or session selected while the refresh was running.
            if (deferSelection && ReferenceEquals(Session, session) && ReferenceEquals(session.Model, previousModel)) await FinishAuthenticationAsync();
            UpdateAvailableProviderCount();
            _footer.Invalidate();
            _ui.RequestRender();
        }
        catch (Exception ex)
        {
            ShowWarning($"{actionLabel}, but its model catalog could not be refreshed: {ex.Message}");
            if (deferSelection && ReferenceEquals(Session, session) && ReferenceEquals(session.Model, previousModel)) await FinishAuthenticationAsync();
        }
    }

    private void RestoreEditorAfterDialog()
    {
        _editorContainer.Clear();
        _editorContainer.AddChild(_editor);
        _ui.SetFocus(_editor);
        _ui.RequestRender();
    }

    private void ShowAmbientAuthDialog(AuthSelectorProvider provider)
    {
        var dialog = new LoginDialogComponent(_ui, provider.Id, (_, _) => RestoreEditorAfterDialog(), provider.Name, $"{provider.Name} setup");
        dialog.ShowInfo($"{provider.MethodName ?? "Authentication"} is configured outside {AppConfig.AppName}.", [], true);
        DisposeActiveSelector();
        _editorContainer.Clear();
        _editorContainer.AddChild(dialog);
        _ui.SetFocus(dialog);
        _ui.RequestRender();
    }

    private async Task ShowLoginDialogAsync(string providerId, string providerName, string authType)
    {
        var previousModel = Session.Model;
        var dialog = new LoginDialogComponent(_ui, providerId, (_, _) => { }, providerName);
        if (authType == AuthTypes.ApiKey && providerId == "amazon-bedrock")
        {
            dialog.ShowDetails([
                Theme.Fg("text", "You can also use an AWS profile, IAM keys, or role-based credentials."),
                Theme.Fg("muted", "See:"),
                Theme.Fg("accent", "  docs/providers.md"),
            ]);
        }
        DisposeActiveSelector();
        _editorContainer.Clear();
        _editorContainer.AddChild(dialog);
        _ui.SetFocus(dialog);
        _ui.RequestRender();

        try
        {
            await Session.ModelRuntime.LoginAsync(providerId, authType, new DialogAuthInteraction(this, dialog));
            RestoreEditorAfterDialog();
            await CompleteProviderAuthenticationAsync(providerId, providerName, authType, previousModel);
        }
        catch (Exception ex)
        {
            RestoreEditorAfterDialog();
            if (IsLoginCancelled(ex)) return;
            ShowError(authType == AuthTypes.OAuth ? $"Failed to login to {providerName}: {ex.Message}" : $"Failed to save API key for {providerName}: {ex.Message}");
        }
    }

    private Task<string> ShowAuthSelectAsync(LoginDialogComponent dialog, AuthPrompt prompt)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void RestoreDialog()
        {
            _editorContainer.Clear();
            _editorContainer.AddChild(dialog);
            _ui.SetFocus(dialog);
            _ui.RequestRender();
        }
        var options = prompt.Options ?? [];
        var selector = new ExtensionSelectorComponent(prompt.Message, options.Select(o => o.Label).ToList(),
            label =>
            {
                RestoreDialog();
                if (options.FirstOrDefault(o => o.Label == label)?.Id is { } id) tcs.TrySetResult(id);
                else tcs.TrySetException(new OperationCanceledException(LoginCancelledMessage));
            },
            () =>
            {
                RestoreDialog();
                tcs.TrySetException(new OperationCanceledException(LoginCancelledMessage));
            });
        _editorContainer.Clear();
        _editorContainer.AddChild(selector);
        _ui.SetFocus(selector);
        _ui.RequestRender();
        return tcs.Task;
    }

    private async Task<string> ShowAuthPromptAsync(LoginDialogComponent dialog, AuthPrompt prompt)
    {
        var response = new TaskCompletionSource<Task<string>>();
        _dispatcher.Invoke(() => response.TrySetResult(prompt.Type switch
        {
            "select" => ShowAuthSelectAsync(dialog, prompt),
            "manual_code" => dialog.ShowManualInput(prompt.Message),
            _ => dialog.ShowPrompt(prompt.Message, prompt.Placeholder),
        }));
        var responseTask = await response.Task;
        var tokens = new[] { prompt.CancellationToken, dialog.CancellationToken }.Where(t => t.CanBeCanceled).ToArray();
        if (tokens.Any(t => t.IsCancellationRequested)) throw new OperationCanceledException(LoginCancelledMessage);
        if (tokens.Length == 0) return await responseTask;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(tokens);
        var cancelled = Task.Delay(Timeout.Infinite, linked.Token);
        if (await Task.WhenAny(responseTask, cancelled) != responseTask) throw new OperationCanceledException(LoginCancelledMessage);
        return await responseTask;
    }

    private void NotifyAuthDialog(LoginDialogComponent dialog, AuthEvent authEvent)
    {
        switch (authEvent)
        {
            case AuthUrlEvent url:
                dialog.ShowAuth(url.Url, url.Instructions);
                break;
            case AuthDeviceCodeEvent device:
                dialog.ShowDeviceCode(device);
                dialog.ShowWaiting("Waiting for authentication...");
                break;
            case AuthInfoEvent info:
                dialog.ShowInfo(info.Message, info.Links);
                break;
            case AuthProgressEvent progress:
                dialog.ShowProgress(progress.Message);
                break;
        }
    }

    // ----- /llama -----

    private void Notify(string message, string type = "info")
    {
        if (type == "error") ShowError(message);
        else if (type == "warning") ShowWarning(message);
        else ShowStatus(message);
    }

    private static bool IsLlamaConnectionError(Exception error) =>
        error is HttpRequestException { StatusCode: null } or TimeoutException or TaskCanceledException
        || $"{error.GetType().Name} {error.Message}".ToLowerInvariant() is var text && (text.Contains("timeout") || text.Contains("network") || text.Contains("timed out"));

    private static string LlamaConnectionErrorMessage(Exception error) => IsLlamaConnectionError(error) ? "Could not connect to the server." : error.Message;

    private async Task<LlamaClient?> ConfiguredLlamaClientAsync()
    {
        var result = await Session.ModelRuntime.GetAuthAsync(LlamaProvider.ProviderId);
        if (result is null)
        {
            Notify($"Configure llama.cpp with /login {LlamaProvider.ProviderId}", "warning");
            return null;
        }
        var configuredUrl = result.Env?.GetValueOrDefault("LLAMA_BASE_URL");
        return new LlamaClient(!string.IsNullOrEmpty(configuredUrl) ? configuredUrl : result.Auth.BaseUrl ?? "", result.Auth.ApiKey);
    }

    private async Task<List<LlamaModelInfo>> SyncLlamaCatalogAsync(LlamaClient client, List<LlamaModelInfo>? catalog = null)
    {
        using var cts = new CancellationTokenSource(15_000);
        var current = catalog ?? await client.ListAsync(ct: cts.Token);
        LlamaProvider.EnsureRegistered(Session.ModelRuntime).SetCatalog(current, client.ServerUrl);
        // /llama already contacted the configured llama.cpp server, so keep this refresh live even in PI_OFFLINE.
        var result = await Session.ModelRuntime.RefreshAsync(new ModelsRefreshOptions { Providers = [LlamaProvider.ProviderId], AllowNetwork = true, CancellationToken = cts.Token });
        if (result.Aborted) throw new TimeoutException("Model catalog refresh timed out.");
        if (result.Errors.TryGetValue(LlamaProvider.ProviderId, out var refreshError)) throw refreshError;
        UpdateAvailableProviderCount();
        return current;
    }

    private async Task HandleLlamaCommandAsync()
    {
        LlamaClient? client;
        try
        {
            client = await ConfiguredLlamaClientAsync();
        }
        catch (Exception ex)
        {
            Notify(ex.Message, "error");
            return;
        }
        if (client is null) return;

        var savedText = _editor.GetText();
        var view = new LlamaView(_ui);
        DisposeActiveSelector();
        _editorContainer.Clear();
        _editorContainer.AddChild(view);
        _ui.SetFocus(view);
        _ui.RequestRender();
        try
        {
            await RunLlamaManagerAsync(view, client);
        }
        catch (Exception ex)
        {
            Notify(ex.Message, "error");
        }
        finally
        {
            _editorContainer.Clear();
            _editorContainer.AddChild(_editor);
            _editor.SetText(savedText);
            _ui.SetFocus(_editor);
            _ui.RequestRender();
        }
    }

    private async Task RunLlamaManagerAsync(LlamaView ui, LlamaClient client)
    {
        async Task<List<LlamaModelInfo>?> ReadCatalogAsync()
        {
            while (true)
            {
                try
                {
                    return await SyncLlamaCatalogAsync(client);
                }
                catch (Exception ex)
                {
                    if (!await ui.ConnectionErrorShouldRetryAsync(client.ServerUrl, LlamaConnectionErrorMessage(ex))) return null;
                }
            }
        }

        var catalog = await ReadCatalogAsync();
        if (catalog is null) return;
        while (true)
        {
            var action = await ui.ShowModelsAsync(client.ServerUrl, catalog);
            if (action is LlamaManagerAction.Close) return;
            Exception? actionError = null;
            try
            {
                switch (action)
                {
                    case LlamaManagerAction.SelectModel { Model: var model } when model.IsLoaded:
                        await UnloadLlamaModelAsync(ui, client, model);
                        break;
                    case LlamaManagerAction.SelectModel { Model: var model } when model.Status.Value == "unloaded":
                        await LoadLlamaModelAsync(ui, client, catalog, model);
                        break;
                    case LlamaManagerAction.SelectModel { Model: var model }:
                        Notify($"{model.Id} is {model.Status.Value}", "warning");
                        break;
                }
            }
            catch (Exception ex)
            {
                actionError = ex;
            }
            var refreshed = await ReadCatalogAsync();
            if (refreshed is null) return;
            catalog = refreshed;
            if (actionError is not null && !IsLlamaConnectionError(actionError)) Notify(actionError.Message, "error");
        }
    }

    private async Task LoadLlamaModelAsync(LlamaView ui, LlamaClient client, List<LlamaModelInfo> catalog, LlamaModelInfo target)
    {
        var loaded = catalog.Where(m => m.Id != target.Id && m.IsLoaded).ToList();
        var replace = false;
        if (loaded.Count > 0)
        {
            var choice = await ui.SelectAsync($"{loaded.Count} model{(loaded.Count == 1 ? " is" : "s are")} loaded", ["Unload all and load", "Keep loaded and load", "Cancel"]);
            if (choice is null or "Cancel") return;
            replace = choice == "Unload all and load";
        }

        async Task RestoreLoadedAsync()
        {
            Notify("Restoring previously loaded models");
            foreach (var model in loaded) await client.LoadAndWaitAsync(model.Id, _ => { });
            await SyncLlamaCatalogAsync(client);
        }

        if (replace)
        {
            foreach (var model in loaded) await client.UnloadAndWaitAsync(model.Id);
        }

        try
        {
            var (cancelled, _) = await ui.RunWithProgressAsync("Loading model", target.Id, "Starting…", "Stop loading?", target.Id,
                (ct, update) => client.LoadAndWaitAsync(target.Id, update, ct),
                () => client.UnloadAsync(target.Id));
            if (cancelled)
            {
                if (replace) await RestoreLoadedAsync();
                return;
            }
            var refreshed = await SyncLlamaCatalogAsync(client);
            var loadedModel = refreshed.FirstOrDefault(m => m.Id == target.Id);
            Notify(loadedModel?.Status.Value == "loaded" ? $"Loaded {target.Id}" : $"Load started for {target.Id}");
        }
        catch
        {
            if (replace)
            {
                try
                {
                    await RestoreLoadedAsync();
                }
                catch
                {
                    // Preserve the original load error.
                }
            }
            throw;
        }
    }

    private async Task UnloadLlamaModelAsync(LlamaView ui, LlamaClient client, LlamaModelInfo model)
    {
        if (!await ui.ConfirmAsync("Unload model?", model.Id)) return;
        await client.UnloadAndWaitAsync(model.Id);
        await SyncLlamaCatalogAsync(client);
        Notify($"Unloaded {model.Id}");
    }
}
