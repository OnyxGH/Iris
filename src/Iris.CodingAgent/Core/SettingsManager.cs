using System.Text.Json.Nodes;
using Iris.Ai;
using Iris.Ai.Json;
using Iris.Ai.Utils;
using Iris.CodingAgent.Config;
using Iris.CodingAgent.Utils;

namespace Iris.CodingAgent.Core;

public enum SettingsScope
{
    Global,
    Project,
}

public sealed record SettingsError(SettingsScope Scope, string? Path, Exception Error);

public interface ISettingsStorage
{
    /// <summary>Read the current content and optionally return new content to write under a lock.</summary>
    void WithLock(SettingsScope scope, Func<string?, string?> fn);
}

public sealed class FileSettingsStorage : ISettingsStorage
{
    private readonly string _globalPath;
    private readonly string _projectPath;

    public FileSettingsStorage(string cwd, string agentDir)
    {
        _globalPath = Path.Combine(PathUtils.ResolvePath(agentDir), "settings.json");
        _projectPath = Path.Combine(PathUtils.ResolvePath(cwd), AppConfig.ConfigDirName, "settings.json");
    }

    public void WithLock(SettingsScope scope, Func<string?, string?> fn)
    {
        var path = scope == SettingsScope.Global ? _globalPath : _projectPath;
        var dir = Path.GetDirectoryName(path)!;
        FileLock? release = null;
        try
        {
            var exists = File.Exists(path);
            if (exists) release = FileLock.AcquireWithRetry(path);
            var current = exists ? File.ReadAllText(path) : null;
            var next = fn(current);
            if (next is not null)
            {
                Directory.CreateDirectory(dir);
                release ??= FileLock.AcquireWithRetry(path);
                File.WriteAllText(path, next);
            }
        }
        finally
        {
            release?.Dispose();
        }
    }
}

public sealed class InMemorySettingsStorage : ISettingsStorage
{
    private string? _global;
    private string? _project;

    public void WithLock(SettingsScope scope, Func<string?, string?> fn)
    {
        lock (this)
        {
            var next = fn(scope == SettingsScope.Global ? _global : _project);
            if (next is null) return;
            if (scope == SettingsScope.Global) _global = next;
            else _project = next;
        }
    }
}

/// <summary>
/// Global (~/.iris/agent/settings.json) and project (.iris/settings.json) settings with deep merge.
/// Settings are kept as JSON so unknown fields round-trip. Port of core/settings-manager.ts.
/// </summary>
public sealed class SettingsManager
{
    private const long DefaultReserveTokens = 16384;
    private const long DefaultKeepRecentTokens = 20000;
    public const long DefaultHttpIdleTimeoutMs = 300_000;

    private readonly ISettingsStorage _storage;
    private readonly Dictionary<SettingsScope, string> _paths;
    private readonly HashSet<string> _modifiedFields = [];
    private readonly Dictionary<string, HashSet<string>> _modifiedNestedFields = [];
    private readonly HashSet<string> _modifiedProjectFields = [];
    private readonly Dictionary<string, HashSet<string>> _modifiedProjectNestedFields = [];
    private readonly List<SettingsError> _errors = [];
    private readonly object _gate = new();
    private JsonObject _globalSettings;
    private JsonObject _projectSettings;
    private JsonObject _settings;
    private Exception? _globalLoadError;
    private Exception? _projectLoadError;
    private Task _writeQueue = Task.CompletedTask;

    private SettingsManager(ISettingsStorage storage, bool projectTrusted, Dictionary<SettingsScope, string>? paths)
    {
        _storage = storage;
        _paths = paths ?? [];
        IsProjectTrusted = projectTrusted;
        var globalLoad = TryLoad(storage, SettingsScope.Global, true);
        var projectLoad = TryLoad(storage, SettingsScope.Project, projectTrusted);
        _globalSettings = globalLoad.Settings;
        _projectSettings = projectLoad.Settings;
        _globalLoadError = globalLoad.Error;
        _projectLoadError = projectLoad.Error;
        if (globalLoad.Error is not null) RecordError(SettingsScope.Global, globalLoad.Error);
        if (projectLoad.Error is not null) RecordError(SettingsScope.Project, projectLoad.Error);
        _settings = DeepMerge(_globalSettings, _projectSettings);
    }

    public bool IsProjectTrusted { get; private set; }

    public static SettingsManager Create(string cwd, string? agentDir = null, bool projectTrusted = true)
    {
        var resolvedCwd = PathUtils.ResolvePath(cwd);
        var resolvedAgentDir = PathUtils.ResolvePath(agentDir ?? AppConfig.AgentDir);
        return new SettingsManager(new FileSettingsStorage(resolvedCwd, resolvedAgentDir), projectTrusted, new Dictionary<SettingsScope, string>
        {
            [SettingsScope.Global] = Path.Combine(resolvedAgentDir, "settings.json"),
            [SettingsScope.Project] = Path.Combine(resolvedCwd, AppConfig.ConfigDirName, "settings.json"),
        });
    }

    public static SettingsManager FromStorage(ISettingsStorage storage, bool projectTrusted = true) => new(storage, projectTrusted, null);

    public static SettingsManager InMemory(JsonObject? settings = null, bool projectTrusted = true)
    {
        var storage = new InMemorySettingsStorage();
        var initial = MigrateSettings((JsonObject)(settings?.DeepClone() ?? new JsonObject()));
        storage.WithLock(SettingsScope.Global, _ => PiJson.SerializeIndentedTwoSpaces(initial));
        return FromStorage(storage, projectTrusted);
    }

    private static (JsonObject Settings, Exception? Error) TryLoad(ISettingsStorage storage, SettingsScope scope, bool projectTrusted)
    {
        try
        {
            if (scope == SettingsScope.Project && !projectTrusted) return (new JsonObject(), null);
            string? content = null;
            storage.WithLock(scope, current =>
            {
                content = current;
                return null;
            });
            if (string.IsNullOrEmpty(content)) return (new JsonObject(), null);
            var parsed = JsonNode.Parse(TextHelpers.StripBom(content)) as JsonObject ?? throw new FormatException("Settings must be a JSON object");
            return (MigrateSettings(parsed), null);
        }
        catch (Exception ex)
        {
            return (new JsonObject(), ex);
        }
    }

    private static JsonObject MigrateSettings(JsonObject settings)
    {
        if (settings.ContainsKey("queueMode") && !settings.ContainsKey("steeringMode"))
        {
            settings["steeringMode"] = settings["queueMode"]?.DeepClone();
            settings.Remove("queueMode");
        }
        if (!settings.ContainsKey("transport") && PiJson.GetBool(settings["websockets"]) is { } websockets)
        {
            settings["transport"] = websockets ? "websocket" : "sse";
            settings.Remove("websockets");
        }
        if (settings["skills"] is JsonObject skills)
        {
            if (PiJson.GetBool(skills["enableSkillCommands"]) is { } enable && !settings.ContainsKey("enableSkillCommands"))
                settings["enableSkillCommands"] = enable;
            if (skills["customDirectories"] is JsonArray { Count: > 0 } dirs) settings["skills"] = dirs.DeepClone();
            else settings.Remove("skills");
        }
        if (settings["retry"] is JsonObject retry)
        {
            var provider = retry["provider"] as JsonObject;
            if (PiJson.GetNumber(retry["maxDelayMs"]) is { } maxDelay && provider?["maxRetryDelayMs"] is null)
            {
                var next = (JsonObject?)provider?.DeepClone() ?? new JsonObject();
                next["maxRetryDelayMs"] = maxDelay;
                retry["provider"] = next;
            }
            retry.Remove("maxDelayMs");
        }
        return settings;
    }

    private static JsonObject DeepMerge(JsonObject baseObj, JsonObject overrides)
    {
        var result = (JsonObject)baseObj.DeepClone();
        foreach (var (key, value) in overrides)
        {
            if (result[key] is JsonObject b && value is JsonObject o) result[key] = DeepMerge(b, o);
            else result[key] = value?.DeepClone();
        }
        return result;
    }

    public JsonObject GetGlobalSettings()
    {
        lock (_gate) return (JsonObject)_globalSettings.DeepClone();
    }

    public JsonObject GetProjectSettings()
    {
        lock (_gate) return (JsonObject)_projectSettings.DeepClone();
    }

    /// <summary>Merged effective settings (read-only snapshot).</summary>
    public JsonObject Effective
    {
        get
        {
            lock (_gate) return _settings;
        }
    }

    public void SetProjectTrusted(bool trusted)
    {
        lock (_gate)
        {
            if (IsProjectTrusted == trusted) return;
            IsProjectTrusted = trusted;
            _modifiedProjectFields.Clear();
            _modifiedProjectNestedFields.Clear();
            if (!trusted)
            {
                _projectSettings = new JsonObject();
                _projectLoadError = null;
            }
            else
            {
                var load = TryLoad(_storage, SettingsScope.Project, true);
                _projectSettings = load.Settings;
                _projectLoadError = load.Error;
                if (load.Error is not null) RecordError(SettingsScope.Project, load.Error);
            }
            _settings = DeepMerge(_globalSettings, _projectSettings);
        }
    }

    public async Task ReloadAsync()
    {
        await FlushAsync();
        lock (_gate)
        {
            var globalLoad = TryLoad(_storage, SettingsScope.Global, true);
            if (globalLoad.Error is null)
            {
                _globalSettings = globalLoad.Settings;
                _globalLoadError = null;
            }
            else
            {
                _globalLoadError = globalLoad.Error;
                RecordError(SettingsScope.Global, globalLoad.Error);
            }
            _modifiedFields.Clear();
            _modifiedNestedFields.Clear();
            _modifiedProjectFields.Clear();
            _modifiedProjectNestedFields.Clear();
            var projectLoad = TryLoad(_storage, SettingsScope.Project, IsProjectTrusted);
            if (projectLoad.Error is null)
            {
                _projectSettings = projectLoad.Settings;
                _projectLoadError = null;
            }
            else
            {
                _projectLoadError = projectLoad.Error;
                RecordError(SettingsScope.Project, projectLoad.Error);
            }
            _settings = DeepMerge(_globalSettings, _projectSettings);
        }
    }

    /// <summary>Apply additional overrides on top of current settings (not persisted).</summary>
    public void ApplyOverrides(JsonObject overrides)
    {
        lock (_gate) _settings = DeepMerge(_settings, overrides);
    }

    private void RecordError(SettingsScope scope, Exception error) => _errors.Add(new SettingsError(scope, _paths.GetValueOrDefault(scope), error));

    private void MarkModified(string field, string? nestedKey = null)
    {
        _modifiedFields.Add(field);
        if (nestedKey is null) return;
        if (!_modifiedNestedFields.TryGetValue(field, out var set)) _modifiedNestedFields[field] = set = [];
        set.Add(nestedKey);
    }

    private void EnqueueWrite(SettingsScope scope, Action task)
    {
        _writeQueue = _writeQueue.ContinueWith(_ =>
        {
            try
            {
                if (scope == SettingsScope.Project && !IsProjectTrusted) throw new InvalidOperationException("Project is not trusted; refusing to write project settings");
                task();
                lock (_gate)
                {
                    if (scope == SettingsScope.Global)
                    {
                        _modifiedFields.Clear();
                        _modifiedNestedFields.Clear();
                    }
                    else
                    {
                        _modifiedProjectFields.Clear();
                        _modifiedProjectNestedFields.Clear();
                    }
                }
            }
            catch (Exception ex)
            {
                lock (_gate) RecordError(scope, ex);
            }
        }, TaskScheduler.Default);
    }

    private void PersistScoped(SettingsScope scope, JsonObject snapshot, HashSet<string> modifiedFields, Dictionary<string, HashSet<string>> modifiedNested)
    {
        _storage.WithLock(scope, current =>
        {
            var currentFile = current is not null ? MigrateSettings(JsonNode.Parse(TextHelpers.StripBom(current)) as JsonObject ?? new JsonObject()) : new JsonObject();
            var merged = (JsonObject)currentFile.DeepClone();
            foreach (var field in modifiedFields)
            {
                var value = snapshot[field];
                if (modifiedNested.TryGetValue(field, out var nestedKeys) && value is JsonObject inMemoryNested)
                {
                    var mergedNested = currentFile[field] is JsonObject baseNested ? (JsonObject)baseNested.DeepClone() : new JsonObject();
                    foreach (var key in nestedKeys)
                    {
                        if (inMemoryNested.TryGetPropertyValue(key, out var nestedValue)) mergedNested[key] = nestedValue?.DeepClone();
                        else mergedNested.Remove(key);
                    }
                    merged[field] = mergedNested;
                }
                else if (snapshot.ContainsKey(field))
                {
                    merged[field] = value?.DeepClone();
                }
                else
                {
                    merged.Remove(field);
                }
            }
            return PiJson.SerializeIndentedTwoSpaces(merged);
        });
    }

    private void Save()
    {
        _settings = DeepMerge(_globalSettings, _projectSettings);
        if (_globalLoadError is not null) return;
        var snapshot = (JsonObject)_globalSettings.DeepClone();
        var fields = new HashSet<string>(_modifiedFields);
        var nested = _modifiedNestedFields.ToDictionary(kv => kv.Key, kv => new HashSet<string>(kv.Value));
        EnqueueWrite(SettingsScope.Global, () => PersistScoped(SettingsScope.Global, snapshot, fields, nested));
    }

    private void UpdateProjectSettings(string field, Action<JsonObject> update)
    {
        if (!IsProjectTrusted) throw new InvalidOperationException("Project is not trusted; refusing to write project settings");
        var project = (JsonObject)_projectSettings.DeepClone();
        update(project);
        _modifiedProjectFields.Add(field);
        _projectSettings = project;
        _settings = DeepMerge(_globalSettings, _projectSettings);
        if (_projectLoadError is not null) return;
        var snapshot = (JsonObject)_projectSettings.DeepClone();
        var fields = new HashSet<string>(_modifiedProjectFields);
        var nested = _modifiedProjectNestedFields.ToDictionary(kv => kv.Key, kv => new HashSet<string>(kv.Value));
        EnqueueWrite(SettingsScope.Project, () => PersistScoped(SettingsScope.Project, snapshot, fields, nested));
    }

    public Task FlushAsync()
    {
        lock (_gate) return _writeQueue;
    }

    public List<SettingsError> DrainErrors()
    {
        lock (_gate)
        {
            var drained = _errors.ToList();
            _errors.Clear();
            return drained;
        }
    }

    // ---- generic helpers ----

    private JsonNode? Get(string field)
    {
        lock (_gate) return _settings[field];
    }

    private JsonNode? GetNested(string field, string key)
    {
        lock (_gate) return (_settings[field] as JsonObject)?[key];
    }

    private void SetGlobal(string field, JsonNode? value)
    {
        lock (_gate)
        {
            if (value is null) _globalSettings.Remove(field);
            else _globalSettings[field] = value;
            MarkModified(field);
            Save();
        }
    }

    private void SetGlobalNested(string field, string key, JsonNode? value)
    {
        lock (_gate)
        {
            if (_globalSettings[field] is not JsonObject obj)
            {
                obj = new JsonObject();
                _globalSettings[field] = obj;
            }
            obj[key] = value;
            MarkModified(field, key);
            Save();
        }
    }

    private string? GetString(string field) => PiJson.GetString(Get(field));

    private bool? GetBool(string field) => PiJson.GetBool(Get(field));

    private static List<string>? StringList(JsonNode? node) =>
        node is JsonArray arr ? arr.Select(x => PiJson.GetString(x)).Where(x => x is not null).Select(x => x!).ToList() : null;

    private static JsonArray ToArray(IEnumerable<string> values) => new(values.Select(v => (JsonNode)JsonValue.Create(v)).ToArray());

    // ---- typed accessors ----

    public string? LastChangelogVersion => GetString("lastChangelogVersion");

    public void SetLastChangelogVersion(string version) => SetGlobal("lastChangelogVersion", version);

    public string? SessionDir => GetString("sessionDir") is { Length: > 0 } dir ? PathUtils.NormalizePath(dir) : null;

    public string? DefaultProvider => GetString("defaultProvider");

    public string? DefaultModel => GetString("defaultModel");

    public void SetDefaultProvider(string provider) => SetGlobal("defaultProvider", provider);

    public void SetDefaultModel(string modelId) => SetGlobal("defaultModel", modelId);

    public void SetDefaultModelAndProvider(string provider, string modelId)
    {
        lock (_gate)
        {
            _globalSettings["defaultProvider"] = provider;
            _globalSettings["defaultModel"] = modelId;
            MarkModified("defaultProvider");
            MarkModified("defaultModel");
            Save();
        }
    }

    public string SteeringMode => GetString("steeringMode") is { Length: > 0 } mode ? mode : "one-at-a-time";

    public void SetSteeringMode(string mode) => SetGlobal("steeringMode", mode);

    public string FollowUpMode => GetString("followUpMode") is { Length: > 0 } mode ? mode : "one-at-a-time";

    public void SetFollowUpMode(string mode) => SetGlobal("followUpMode", mode);

    public string? ThemeSetting => GetString("theme");

    public string? Theme => ThemeSetting is { } theme && !theme.Contains('/') ? theme : null;

    public void SetTheme(string theme) => SetGlobal("theme", theme);

    public ThinkingLevel? DefaultThinkingLevel => ThinkingLevels.Parse(GetString("defaultThinkingLevel"));

    public void SetDefaultThinkingLevel(ThinkingLevel level) => SetGlobal("defaultThinkingLevel", level.ToWire());

    public ThinkingLevel? GetModelThinkingLevel(string provider, string modelId) =>
        ThinkingLevels.Parse(PiJson.GetString(GetNested("modelThinkingLevels", $"{provider}/{modelId}")));

    public Dictionary<string, ThinkingLevel> GetAllModelThinkingLevels()
    {
        var result = new Dictionary<string, ThinkingLevel>();
        if (Get("modelThinkingLevels") is JsonObject obj)
        {
            foreach (var (key, value) in obj)
            {
                if (ThinkingLevels.Parse(PiJson.GetString(value)) is { } level) result[key] = level;
            }
        }
        return result;
    }

    public void SetModelThinkingLevel(string provider, string modelId, ThinkingLevel level)
    {
        lock (_gate)
        {
            if (_globalSettings["modelThinkingLevels"] is not JsonObject obj)
            {
                obj = new JsonObject();
                _globalSettings["modelThinkingLevels"] = obj;
            }
            obj[$"{provider}/{modelId}"] = level.ToWire();
            MarkModified("modelThinkingLevels");
            Save();
        }
    }

    public void RemoveModelThinkingLevel(string provider, string modelId)
    {
        lock (_gate)
        {
            if (_globalSettings["modelThinkingLevels"] is not JsonObject obj) return;
            obj.Remove($"{provider}/{modelId}");
            if (obj.Count == 0) _globalSettings.Remove("modelThinkingLevels");
            MarkModified("modelThinkingLevels");
            Save();
        }
    }

    public string TransportSetting => GetString("transport") ?? "auto";

    public Transport Transport => TransportSetting switch
    {
        "sse" => Transport.Sse,
        "websocket" => Transport.WebSocket,
        "websocket-cached" => Transport.WebSocketCached,
        _ => Transport.Auto,
    };

    public void SetTransport(string transport) => SetGlobal("transport", transport);

    public bool CompactionEnabled => PiJson.GetBool(GetNested("compaction", "enabled")) ?? true;

    public void SetCompactionEnabled(bool enabled) => SetGlobalNested("compaction", "enabled", enabled);

    private long GetCompactionTokenSetting(string field, Model? model)
    {
        var compaction = Get("compaction") as JsonObject;
        var ordinaryNode = compaction?[field];
        long? ordinary = null;
        if (ordinaryNode is not null)
        {
            if (!PiJson.TryGetNumber(ordinaryNode, out var d) || d < 0 || Math.Floor(d) != d || d > 9007199254740991)
                throw new InvalidOperationException($"Invalid compaction.{field} setting: {ordinaryNode.ToJsonString()}. Expected a non-negative safe integer.");
            ordinary = (long)d;
        }
        var modelKey = model is null ? null : $"{model.Provider}/{model.Id}";
        long? overrideValue = null;
        if (modelKey is not null && (compaction?["modelOverrides"] as JsonObject)?[modelKey] is { } entry)
        {
            if (entry is not JsonObject entryObj)
                throw new InvalidOperationException($"Invalid compaction.modelOverrides[\"{modelKey}\"] setting: {entry.ToJsonString()}. Expected an object.");
            if (entryObj[field] is { } overrideNode)
            {
                if (!PiJson.TryGetNumber(overrideNode, out var d) || d < 0 || Math.Floor(d) != d || d > 9007199254740991)
                    throw new InvalidOperationException($"Invalid compaction.modelOverrides[\"{modelKey}\"].{field} setting: {overrideNode.ToJsonString()}. Expected a non-negative safe integer.");
                overrideValue = (long)d;
            }
        }
        return overrideValue ?? ordinary ?? (field == "reserveTokens" ? DefaultReserveTokens : DefaultKeepRecentTokens);
    }

    public long GetCompactionReserveTokens(Model? model = null) => GetCompactionTokenSetting("reserveTokens", model);

    public long GetCompactionKeepRecentTokens(Model? model = null) => GetCompactionTokenSetting("keepRecentTokens", model);

    public (bool Enabled, long ReserveTokens, long KeepRecentTokens) GetCompactionSettings(Model? model = null) =>
        (CompactionEnabled, GetCompactionReserveTokens(model), GetCompactionKeepRecentTokens(model));

    public (long ReserveTokens, bool SkipPrompt) BranchSummarySettings =>
        (PiJson.GetLong(GetNested("branchSummary", "reserveTokens")) ?? 16384, PiJson.GetBool(GetNested("branchSummary", "skipPrompt")) ?? false);

    public bool RetryEnabled => PiJson.GetBool(GetNested("retry", "enabled")) ?? true;

    public void SetRetryEnabled(bool enabled) => SetGlobalNested("retry", "enabled", enabled);

    public RetryPolicy RetrySettings => new()
    {
        Enabled = RetryEnabled,
        MaxRetries = (int)(PiJson.GetLong(GetNested("retry", "maxRetries")) ?? 3),
        BaseDelayMs = PiJson.GetLong(GetNested("retry", "baseDelayMs")) ?? 2000,
        MaxAgentDelayMs = PiJson.GetLong(GetNested("retry", "maxAgentDelayMs")) ?? AssistantRetry.DefaultMaxAgentRetryDelayMs,
    };

    public long HttpIdleTimeoutMs
    {
        get
        {
            var node = Get("httpIdleTimeoutMs");
            if (node is null) return DefaultHttpIdleTimeoutMs;
            if (PiJson.TryGetNumber(node, out var d) && d >= 0) return (long)d;
            throw new InvalidOperationException($"Invalid httpIdleTimeoutMs setting: {node.ToJsonString()}");
        }
    }

    public void SetHttpIdleTimeoutMs(long timeoutMs)
    {
        if (timeoutMs < 0) throw new ArgumentOutOfRangeException(nameof(timeoutMs), $"Invalid httpIdleTimeoutMs setting: {timeoutMs}");
        SetGlobal("httpIdleTimeoutMs", timeoutMs);
    }

    public (int? TimeoutMs, int? MaxRetries, int MaxRetryDelayMs) ProviderRetrySettings
    {
        get
        {
            var provider = (Get("retry") as JsonObject)?["provider"] as JsonObject;
            return ((int?)PiJson.GetLong(provider?["timeoutMs"]), (int?)PiJson.GetLong(provider?["maxRetries"]), (int)(PiJson.GetLong(provider?["maxRetryDelayMs"]) ?? 60000));
        }
    }

    public int? WebSocketConnectTimeoutMs => (int?)PiJson.GetLong(Get("websocketConnectTimeoutMs"));

    public bool HideThinkingBlock => GetBool("hideThinkingBlock") ?? false;

    public void SetHideThinkingBlock(bool hide) => SetGlobal("hideThinkingBlock", hide);

    public bool ShowCacheMissNotices => GetBool("showCacheMissNotices") ?? false;

    public void SetShowCacheMissNotices(bool show) => SetGlobal("showCacheMissNotices", show);

    public string ExternalEditorCommand
    {
        get
        {
            if (GetString("externalEditor") is { } configured && configured.Trim().Length > 0) return configured;
            var env = Environment.GetEnvironmentVariable("VISUAL");
            if (string.IsNullOrEmpty(env)) env = Environment.GetEnvironmentVariable("EDITOR");
            if (!string.IsNullOrEmpty(env)) return env;
            return OperatingSystem.IsWindows() ? "notepad" : "nano";
        }
    }

    public string? ShellPath => GetString("shellPath") is { Length: > 0 } p ? PathUtils.NormalizePath(p) : null;

    public void SetShellPath(string? path) => SetGlobal("shellPath", path);

    public bool QuietStartup => GetBool("quietStartup") ?? false;

    public void SetQuietStartup(bool quiet) => SetGlobal("quietStartup", quiet);

    public string DefaultProjectTrust
    {
        get
        {
            string? value;
            lock (_gate) value = PiJson.GetString(_globalSettings["defaultProjectTrust"]);
            return value is "always" or "never" ? value : "ask";
        }
    }

    public void SetDefaultProjectTrust(string value) => SetGlobal("defaultProjectTrust", value);

    public string? ShellCommandPrefix => GetString("shellCommandPrefix");

    public void SetShellCommandPrefix(string? prefix) => SetGlobal("shellCommandPrefix", prefix);

    public List<string>? NpmCommand => StringList(Get("npmCommand"));

    public void SetNpmCommand(IEnumerable<string>? command) => SetGlobal("npmCommand", command is null ? null : ToArray(command));

    public bool CollapseChangelog => GetBool("collapseChangelog") ?? false;

    public void SetCollapseChangelog(bool collapse) => SetGlobal("collapseChangelog", collapse);

    public bool EnableInstallTelemetry => GetBool("enableInstallTelemetry") ?? true;

    public void SetEnableInstallTelemetry(bool enabled) => SetGlobal("enableInstallTelemetry", enabled);

    public bool EnableAnalytics => GetBool("enableAnalytics") ?? false;

    public string? TrackingId => GetString("trackingId");

    public void SetEnableAnalytics(bool enabled)
    {
        lock (_gate)
        {
            _globalSettings["enableAnalytics"] = enabled;
            MarkModified("enableAnalytics");
            if (enabled && PiJson.GetString(_globalSettings["trackingId"]) is null)
            {
                _globalSettings["trackingId"] = Guid.NewGuid().ToString();
                MarkModified("trackingId");
            }
            Save();
        }
    }

    /// <summary>Package sources (strings or objects) as raw JSON.</summary>
    public List<JsonNode> Packages => (Get("packages") as JsonArray)?.Where(n => n is not null).Select(n => n!.DeepClone()).ToList() ?? [];

    public void SetPackages(IEnumerable<JsonNode> packages) => SetGlobal("packages", new JsonArray(packages.Select(p => p.DeepClone()).ToArray()));

    public void SetProjectPackages(IEnumerable<JsonNode> packages) =>
        UpdateProjectSettings("packages", s => s["packages"] = new JsonArray(packages.Select(p => p.DeepClone()).ToArray()));

    public List<string> ExtensionPaths => StringList(Get("extensions")) ?? [];

    public void SetExtensionPaths(IEnumerable<string> paths) => SetGlobal("extensions", ToArray(paths));

    public void SetProjectExtensionPaths(IEnumerable<string> paths) => UpdateProjectSettings("extensions", s => s["extensions"] = ToArray(paths));

    public List<string> SkillPaths => StringList(Get("skills")) ?? [];

    public void SetSkillPaths(IEnumerable<string> paths) => SetGlobal("skills", ToArray(paths));

    public void SetProjectSkillPaths(IEnumerable<string> paths) => UpdateProjectSettings("skills", s => s["skills"] = ToArray(paths));

    public List<string> PromptTemplatePaths => StringList(Get("prompts")) ?? [];

    public void SetPromptTemplatePaths(IEnumerable<string> paths) => SetGlobal("prompts", ToArray(paths));

    public void SetProjectPromptTemplatePaths(IEnumerable<string> paths) => UpdateProjectSettings("prompts", s => s["prompts"] = ToArray(paths));

    public List<string> ThemePaths => StringList(Get("themes")) ?? [];

    public void SetThemePaths(IEnumerable<string> paths) => SetGlobal("themes", ToArray(paths));

    public void SetProjectThemePaths(IEnumerable<string> paths) => UpdateProjectSettings("themes", s => s["themes"] = ToArray(paths));

    public bool EnableSkillCommands => GetBool("enableSkillCommands") ?? true;

    public void SetEnableSkillCommands(bool enabled) => SetGlobal("enableSkillCommands", enabled);

    public ThinkingBudgets? ThinkingBudgets => Get("thinkingBudgets") is JsonObject obj ? PiJson.Deserialize<ThinkingBudgets>(obj) : null;

    /// <summary>images: "kitty" | "iterm2" | false; trueColor/hyperlinks booleans.</summary>
    public JsonObject TerminalCapabilityOverrides
    {
        get
        {
            var terminal = Get("terminal") as JsonObject;
            var result = new JsonObject();
            var images = terminal?["images"];
            if (PiJson.GetString(images) is "kitty" or "iterm2") result["images"] = images!.DeepClone();
            else if (PiJson.GetBool(images) == false) result["images"] = null;
            if (PiJson.GetBool(terminal?["trueColor"]) is { } trueColor) result["trueColor"] = trueColor;
            if (PiJson.GetBool(terminal?["hyperlinks"]) is { } hyperlinks) result["hyperlinks"] = hyperlinks;
            return result;
        }
    }

    public bool ShowImages => PiJson.GetBool(GetNested("terminal", "showImages")) ?? true;

    public void SetShowImages(bool show) => SetGlobalNested("terminal", "showImages", show);

    public int ImageWidthCells => PiJson.GetNumber(GetNested("terminal", "imageWidthCells")) is { } width && double.IsFinite(width) ? Math.Max(1, (int)Math.Floor(width)) : 60;

    public void SetImageWidthCells(int width) => SetGlobalNested("terminal", "imageWidthCells", Math.Max(1, width));

    public bool ClearOnShrink => PiJson.GetBool(GetNested("terminal", "clearOnShrink")) ?? Environment.GetEnvironmentVariable("PI_CLEAR_ON_SHRINK") == "1";

    public void SetClearOnShrink(bool enabled) => SetGlobalNested("terminal", "clearOnShrink", enabled);

    public bool ShowTerminalProgress => PiJson.GetBool(GetNested("terminal", "showTerminalProgress")) ?? false;

    public void SetShowTerminalProgress(bool enabled) => SetGlobalNested("terminal", "showTerminalProgress", enabled);

    /// <summary>Iris: fullscreen (fixed input dock) is the default; pi defaults to regular.</summary>
    public string TuiMode => GetString("tuiMode") == "regular" ? "regular" : "fullscreen";

    public void SetTuiMode(string mode) => SetGlobal("tuiMode", mode);

    public void SetFullscreenExitOutput(string output) => SetGlobal("fullscreenExitOutput", output);

    public void SetFullscreenScrollbar(string mode) => SetGlobal("fullscreenScrollbar", mode);

    public void SetFullscreenCopyOnSelect(bool enabled) => SetGlobal("fullscreenCopyOnSelect", enabled);

    public string FullscreenExitOutput => GetString("fullscreenExitOutput") == "resume-hint" ? "resume-hint" : "transcript";

    public string FullscreenScrollbar => GetString("fullscreenScrollbar") is "always" or "hidden" ? GetString("fullscreenScrollbar")! : "auto";

    public bool FullscreenCopyOnSelect => GetBool("fullscreenCopyOnSelect") ?? true;

    public bool ImageAutoResize => PiJson.GetBool(GetNested("images", "autoResize")) ?? true;

    public void SetImageAutoResize(bool enabled) => SetGlobalNested("images", "autoResize", enabled);

    public bool BlockImages => PiJson.GetBool(GetNested("images", "blockImages")) ?? false;

    public void SetBlockImages(bool blocked) => SetGlobalNested("images", "blockImages", blocked);

    public List<string>? EnabledModels => StringList(Get("enabledModels"));

    public List<string>? DefaultTools => StringList(Get("defaultTools"));

    public void SetEnabledModels(IEnumerable<string>? patterns) => SetGlobal("enabledModels", patterns is null ? null : ToArray(patterns));

    public string DoubleEscapeAction => GetString("doubleEscapeAction") is "fork" or "none" ? GetString("doubleEscapeAction")! : "tree";

    public void SetDoubleEscapeAction(string action) => SetGlobal("doubleEscapeAction", action);

    public string TreeFilterMode => GetString("treeFilterMode") is "default" or "no-tools" or "user-only" or "labeled-only" or "all" ? GetString("treeFilterMode")! : "default";

    public void SetTreeFilterMode(string mode) => SetGlobal("treeFilterMode", mode);

    public bool ShowHardwareCursor => GetBool("showHardwareCursor") ?? Environment.GetEnvironmentVariable("PI_HARDWARE_CURSOR") == "1";

    public void SetShowHardwareCursor(bool enabled) => SetGlobal("showHardwareCursor", enabled);

    public int EditorPaddingX => (int)(PiJson.GetLong(Get("editorPaddingX")) ?? 0);

    public void SetEditorPaddingX(int padding) => SetGlobal("editorPaddingX", Math.Max(0, Math.Min(3, padding)));

    public int OutputPad => PiJson.GetLong(Get("outputPad")) == 0 ? 0 : 1;

    public void SetOutputPad(int padding) => SetGlobal("outputPad", padding == 0 ? 0 : 1);

    public int AutocompleteMaxVisible => (int)(PiJson.GetLong(Get("autocompleteMaxVisible")) ?? 5);

    public void SetAutocompleteMaxVisible(int maxVisible) => SetGlobal("autocompleteMaxVisible", Math.Max(3, Math.Min(20, maxVisible)));

    public string CodeBlockIndent => PiJson.GetString(GetNested("markdown", "codeBlockIndent")) ?? "  ";

    public string MermaidRenderingMode => PiJson.GetString(GetNested("markdown", "mermaid")) is "off" or "final" ? PiJson.GetString(GetNested("markdown", "mermaid"))! : "streaming";

    public void SetMermaidRenderingMode(string mode) => SetGlobalNested("markdown", "mermaid", mode);

    public JsonObject Warnings => (JsonObject?)(Get("warnings") as JsonObject)?.DeepClone() ?? new JsonObject();

    public void SetWarnings(JsonObject warnings) => SetGlobal("warnings", warnings.DeepClone());

    public string? HttpProxy => GetString("httpProxy");
}
