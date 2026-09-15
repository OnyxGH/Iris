using Iris.Agent;
using Iris.Ai;
using Iris.CodingAgent.Core.Extensions;

namespace Iris.CodingAgent.Core;

public sealed partial class AgentSession
{
    private async Task EmitModelSelectAsync(Model nextModel, Model? previousModel, string source)
    {
        if (ModelUtils.ModelsAreEqual(previousModel, nextModel)) return;
        await _extensionRunner.EmitAsync(RunnerEvent.Of("model_select", ("model", nextModel), ("previousModel", previousModel), ("source", source)));
    }

    /// <summary>Set the model, saving it to the session; persists to global defaults when persist is true.</summary>
    public async Task SetModelAsync(Model model, bool persist = false)
    {
        if (await _modelRuntime.CheckAuthAsync(model.Provider) is null)
        {
            throw new InvalidOperationException($"No API key for {model.Provider}/{model.Id}");
        }

        var previousModel = Model;
        var thinkingLevel = GetThinkingLevelForModelSwitch(model);
        Agent.State.Model = model;
        SessionManager.AppendModelChange(model.Provider, model.Id);
        if (persist)
        {
            SettingsManager.SetDefaultModelAndProvider(model.Provider, model.Id);
            AddPersistedDefaultToNonEmptyScope(model);
        }
        SetThinkingLevel(thinkingLevel);
        await EmitModelSelectAsync(model, previousModel, "set");
    }

    /// <summary>
    /// Iris: replace the active model with the runtime's current catalog entry when its limits changed, e.g. after a
    /// llama.cpp model loads and reports its real context size. Not recorded as a model change.
    /// </summary>
    public bool SyncModelFromCatalog()
    {
        if (Model is not { } current || _modelRuntime.GetModel(current.Provider, current.Id) is not { } latest) return false;
        if (ReferenceEquals(latest, current)
            || (latest.ContextWindow == current.ContextWindow && latest.MaxTokens == current.MaxTokens && latest.Input.SequenceEqual(current.Input)))
        {
            return false;
        }
        Agent.State.Model = latest;
        return true;
    }

    private void AddPersistedDefaultToNonEmptyScope(Model model)
    {
        if (_scopedModels.Count == 0 || _scopedModels.Any(s => ModelUtils.ModelsAreEqual(s.Model, model))) return;
        _scopedModels = [.. _scopedModels, new ScopedModel(model)];

        var enabledModels = SettingsManager.EnabledModels;
        if (enabledModels is not { Count: > 0 }) return;
        var reference = $"{model.Provider}/{model.Id}";
        if (enabledModels.Any(p => string.Equals(p, reference, StringComparison.OrdinalIgnoreCase))) return;
        SettingsManager.SetEnabledModels([.. enabledModels, reference]);
    }

    /// <summary>Cycle through scoped models (--models) when set, otherwise all available models.</summary>
    public Task<ModelCycleResult?> CycleModelAsync(bool forward = true, bool persist = false) =>
        _scopedModels.Count > 0 ? CycleScopedModelAsync(forward, persist) : CycleAvailableModelAsync(forward, persist);

    private async Task<ModelCycleResult?> ApplyCycledModelAsync(Model next, ThinkingLevel? explicitLevel, bool persist, bool isScoped)
    {
        var currentModel = Model;
        var thinkingLevel = GetThinkingLevelForModelSwitch(next, explicitLevel);
        Agent.State.Model = next;
        SessionManager.AppendModelChange(next.Provider, next.Id);
        if (persist)
        {
            SettingsManager.SetDefaultModelAndProvider(next.Provider, next.Id);
            AddPersistedDefaultToNonEmptyScope(next);
        }
        SetThinkingLevel(thinkingLevel);
        await EmitModelSelectAsync(next, currentModel, "cycle");
        return new ModelCycleResult(next, ThinkingLevel, isScoped);
    }

    private static int NextIndex(int current, int length, bool forward) =>
        forward ? (current + 1) % length : (current - 1 + length) % length;

    private Task<ModelCycleResult?> CycleScopedModelAsync(bool forward, bool persist)
    {
        var available = _modelRuntime.AvailableSnapshot.Select(m => $"{m.Provider}\0{m.Id}").ToHashSet();
        var scoped = _scopedModels.Where(s => available.Contains($"{s.Model.Provider}\0{s.Model.Id}")).ToList();
        if (scoped.Count <= 1) return Task.FromResult<ModelCycleResult?>(null);

        var currentIndex = scoped.FindIndex(s => ModelUtils.ModelsAreEqual(s.Model, Model));
        if (currentIndex == -1) currentIndex = 0;
        var next = scoped[NextIndex(currentIndex, scoped.Count, forward)];
        return ApplyCycledModelAsync(next.Model, next.ThinkingLevel, persist, isScoped: true);
    }

    private Task<ModelCycleResult?> CycleAvailableModelAsync(bool forward, bool persist)
    {
        var available = _modelRuntime.AvailableSnapshot;
        if (available.Count <= 1) return Task.FromResult<ModelCycleResult?>(null);

        var currentIndex = -1;
        for (var i = 0; i < available.Count; i++)
        {
            if (ModelUtils.ModelsAreEqual(available[i], Model))
            {
                currentIndex = i;
                break;
            }
        }
        if (currentIndex == -1) currentIndex = 0;
        return ApplyCycledModelAsync(available[NextIndex(currentIndex, available.Count, forward)], null, persist, isScoped: false);
    }

    /// <summary>Set the thinking level, clamped to the model; saved to the session only when it changes.</summary>
    public void SetThinkingLevel(ThinkingLevel level, bool persist = false)
    {
        var available = GetAvailableThinkingLevels();
        var effective = available.Contains(level) ? level : Model is { } model ? ModelUtils.ClampThinkingLevel(model, level) : ThinkingLevel.Off;
        var previous = Agent.State.ThinkingLevel;
        var isChanging = effective != previous;
        Agent.State.ThinkingLevel = effective;

        if (persist) SettingsManager.SetDefaultThinkingLevel(level);

        if (isChanging)
        {
            SessionManager.AppendThinkingLevelChange(ThinkingLevels.ToWire(effective));
            Emit(new ThinkingLevelChangedEvent(effective));
            _ = _extensionRunner.EmitAsync(RunnerEvent.Of("thinking_level_select", ("level", effective), ("previousLevel", previous)));
        }
    }

    /// <summary>Cycle to the next thinking level, or null when the model does not support thinking.</summary>
    public ThinkingLevel? CycleThinkingLevel(bool persist = false)
    {
        if (!SupportsThinking()) return null;
        var levels = GetAvailableThinkingLevels();
        var index = levels.ToList().IndexOf(ThinkingLevel);
        var next = levels[(index + 1) % levels.Count];
        SetThinkingLevel(next, persist);
        return next;
    }

    public IReadOnlyList<ThinkingLevel> GetAvailableThinkingLevels() =>
        Model is { } model ? ModelUtils.GetSupportedThinkingLevels(model) : ThinkingLevels.All;

    public bool SupportsThinking() => Model?.Reasoning == true;

    private ThinkingLevel GetThinkingLevelForModelSwitch(Model? targetModel = null, ThinkingLevel? explicitLevel = null)
    {
        if (explicitLevel is { } level) return level;
        if (targetModel is not null && SettingsManager.GetModelThinkingLevel(targetModel.Provider, targetModel.Id) is { } perModel) return perModel;
        return SettingsManager.DefaultThinkingLevel ?? ThinkingLevel;
    }

    private void SyncQueueModesFromSettings()
    {
        Agent.SteeringMode = QueueModes.Parse(SettingsManager.SteeringMode);
        Agent.FollowUpMode = QueueModes.Parse(SettingsManager.FollowUpMode);
    }

    public void SetSteeringMode(QueueMode mode)
    {
        Agent.SteeringMode = mode;
        SettingsManager.SetSteeringMode(mode.ToWire());
    }

    public void SetFollowUpMode(QueueMode mode)
    {
        Agent.FollowUpMode = mode;
        SettingsManager.SetFollowUpMode(mode.ToWire());
    }
}
