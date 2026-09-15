using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Iris.Ai.Json;
using Iris.CodingAgent.Config;
using Iris.CodingAgent.Utils;
using Semver;

namespace Iris.CodingAgent.Core;

public sealed record ResolvedResource(string Path, bool Enabled, PathMetadata Metadata);

public sealed record ResolvedPaths(
    List<ResolvedResource> Extensions,
    List<ResolvedResource> Skills,
    List<ResolvedResource> Prompts,
    List<ResolvedResource> Themes);

/// <summary>"install" | "skip" | "error".</summary>
public enum MissingSourceAction
{
    Install,
    Skip,
    Error,
}

/// <summary>Type is start | progress | complete | error; Action is install | remove | update | clone | pull.</summary>
public sealed record PackageProgressEvent(string Type, string Action, string Source, string? Message = null);

/// <summary>Type is npm | git; Scope is user | project.</summary>
public sealed record PackageUpdate(string Source, string DisplayName, string Type, string Scope);

/// <summary>Scope is user | project.</summary>
public sealed record ConfiguredPackage(string Source, string Scope, bool Filtered, string? InstalledPath);

/// <summary>npm-style semver helpers (node-semver valid/validRange/satisfies/gt/maxSatisfying).</summary>
internal static class NpmSemver
{
    public static SemVersion? Valid(string? version) =>
        SemVersion.TryParse((version ?? "").Trim(), SemVersionStyles.AllowLowerV, out var parsed) ? parsed : null;

    public static SemVersionRange? ValidRange(string? range) =>
        range is not null && SemVersionRange.TryParseNpm(range, out var parsed) ? parsed : null;

    public static bool Satisfies(string version, SemVersionRange range) => Valid(version) is { } v && range.Contains(v);

    public static bool Gt(string left, string right) =>
        Valid(left) is { } l && Valid(right) is { } r ? SemVersion.ComparePrecedence(l, r) > 0 : throw new FormatException($"Invalid Version: {left}");
}

/// <summary>
/// Package sources (npm, git, local) and resource path resolution from settings and auto-discovery.
/// Port of core/package-manager.ts.
/// </summary>
public sealed partial class PackageManager(string cwd, string agentDir, SettingsManager settingsManager)
{
    private const int NetworkTimeoutMs = 10000;
    private const int UpdateCheckConcurrency = 4;
    private const int GitUpdateConcurrency = 4;
    private static readonly string[] ResourceTypes = ["extensions", "skills", "prompts", "themes"];

    private readonly string _cwd = PathUtils.ResolvePath(cwd);
    private readonly string _agentDir = PathUtils.ResolvePath(agentDir);
    private string? _globalNpmRoot;
    private string? _globalNpmRootCommandKey;
    private Action<PackageProgressEvent>? _progressCallback;

    private abstract record ParsedSource;

    private sealed record NpmSource(string Spec, string Name, string? Version, SemVersionRange? Range, bool Pinned) : ParsedSource;

    private sealed record GitParsedSource(GitSource Git) : ParsedSource
    {
        public bool Pinned => Git.Pinned;
    }

    private sealed record LocalSource(string Path) : ParsedSource;

    private sealed record PackageEntry(JsonNode Pkg, string Scope);

    private sealed class Accumulator
    {
        public Dictionary<string, (PathMetadata Metadata, bool Enabled)> Extensions { get; } = [];
        public Dictionary<string, (PathMetadata Metadata, bool Enabled)> Skills { get; } = [];
        public Dictionary<string, (PathMetadata Metadata, bool Enabled)> Prompts { get; } = [];
        public Dictionary<string, (PathMetadata Metadata, bool Enabled)> Themes { get; } = [];

        // Insertion order matters for precedence ties, so track it alongside the dictionaries.
        public Dictionary<string, List<string>> Order { get; } = new() { ["extensions"] = [], ["skills"] = [], ["prompts"] = [], ["themes"] = [] };

        public Dictionary<string, (PathMetadata Metadata, bool Enabled)> Target(string type) => type switch
        {
            "extensions" => Extensions,
            "skills" => Skills,
            "prompts" => Prompts,
            "themes" => Themes,
            _ => throw new ArgumentException($"Unknown resource type: {type}"),
        };

        public void Add(string type, string path, PathMetadata metadata, bool enabled)
        {
            if (string.IsNullOrEmpty(path)) return;
            var target = Target(type);
            if (target.ContainsKey(path)) return;
            target[path] = (metadata, enabled);
            Order[type].Add(path);
        }
    }

    private static string HomeDir => Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } home ? home : AppConfig.HomeDir;

    private static bool IsOfflineModeEnabled() =>
        Environment.GetEnvironmentVariable("PI_OFFLINE") is { Length: > 0 } value && (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("yes", StringComparison.OrdinalIgnoreCase));

    private static string ToPosix(string path) => IgnoreMatcher.ToPosixPath(path);

    private static bool IsPattern(string s) => s.StartsWith('!') || s.StartsWith('+') || s.StartsWith('-') || s.Contains('*') || s.Contains('?');

    private static bool IsOverride(string s) => s.StartsWith('!') || s.StartsWith('+') || s.StartsWith('-');

    private static bool HasGlobPattern(string s) => s.Contains('*') || s.Contains('?');

    private static List<string> StringEntries(JsonObject settings, string key) =>
        settings[key] is JsonArray arr
            ? arr.OfType<JsonValue>().Where(v => v.GetValueKind() == JsonValueKind.String).Select(v => v.GetValue<string>()).ToList()
            : [];

    private static List<JsonNode> PackagesOf(JsonObject settings) =>
        settings["packages"] is JsonArray arr ? arr.Where(n => n is JsonValue { } v && v.GetValueKind() == JsonValueKind.String || n is JsonObject).Select(n => n!).ToList() : [];

    private static string SourceOf(JsonNode pkg) => pkg is JsonObject obj ? PiJson.GetString(obj["source"]) ?? "" : pkg.GetValue<string>();

    public static string GetExtensionTempFolder(string agentDir)
    {
        var tempFolder = Path.Combine(agentDir, "tmp", "extensions");
        Directory.CreateDirectory(tempFolder);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tempFolder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return tempFolder;
    }

    public void SetProgressCallback(Action<PackageProgressEvent>? callback) => _progressCallback = callback;

    // ----- settings -----

    public bool AddSourceToSettings(string source, bool local = false)
    {
        var scope = local ? "project" : "user";
        var currentPackages = PackagesOf(scope == "project" ? settingsManager.GetProjectSettings() : settingsManager.GetGlobalSettings());
        var normalizedSource = NormalizePackageSourceForSettings(source, scope);
        var matchIndex = currentPackages.FindIndex(existing => PackageSourcesMatch(existing, source, scope));
        List<JsonNode> nextPackages;
        if (matchIndex != -1)
        {
            var existing = currentPackages[matchIndex];
            if (SourceOf(existing) == normalizedSource) return false;
            nextPackages = currentPackages.Select(p => p.DeepClone()).ToList();
            if (existing is JsonObject obj)
            {
                var updated = (JsonObject)obj.DeepClone();
                updated["source"] = normalizedSource;
                nextPackages[matchIndex] = updated;
            }
            else
            {
                nextPackages[matchIndex] = JsonValue.Create(normalizedSource);
            }
        }
        else
        {
            nextPackages = [.. currentPackages.Select(p => p.DeepClone()), JsonValue.Create(normalizedSource)];
        }
        if (scope == "project") settingsManager.SetProjectPackages(nextPackages);
        else settingsManager.SetPackages(nextPackages);
        return true;
    }

    public bool RemoveSourceFromSettings(string source, bool local = false)
    {
        var scope = local ? "project" : "user";
        var currentPackages = PackagesOf(scope == "project" ? settingsManager.GetProjectSettings() : settingsManager.GetGlobalSettings());
        var nextPackages = currentPackages.Where(existing => !PackageSourcesMatch(existing, source, scope)).Select(p => p.DeepClone()).ToList();
        if (nextPackages.Count == currentPackages.Count) return false;
        if (scope == "project") settingsManager.SetProjectPackages(nextPackages);
        else settingsManager.SetPackages(nextPackages);
        return true;
    }

    public string? GetInstalledPath(string source, string scope)
    {
        switch (ParseSource(source))
        {
            case NpmSource npm:
            {
                var path = GetNpmInstallPath(npm, scope);
                return Exists(path) ? path : null;
            }
            case GitParsedSource git:
            {
                var path = GetGitInstallPath(git.Git, scope);
                return Exists(path) ? path : null;
            }
            case LocalSource local:
            {
                var path = ResolvePathFromBase(local.Path, GetBaseDirForScope(scope));
                return Exists(path) ? path : null;
            }
        }
        return null;
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private void EmitProgress(PackageProgressEvent evt) => _progressCallback?.Invoke(evt);

    private async Task WithProgressAsync(string action, string source, string message, Func<Task> operation)
    {
        EmitProgress(new PackageProgressEvent("start", action, source, message));
        try
        {
            await operation();
            EmitProgress(new PackageProgressEvent("complete", action, source));
        }
        catch (Exception ex)
        {
            EmitProgress(new PackageProgressEvent("error", action, source, ex.Message));
            throw;
        }
    }

    // ----- resolve -----

    public async Task<ResolvedPaths> ResolveAsync(Func<string, Task<MissingSourceAction>>? onMissing = null)
    {
        var accumulator = new Accumulator();
        var globalSettings = settingsManager.GetGlobalSettings();
        var projectSettings = settingsManager.GetProjectSettings();

        var allPackages = new List<PackageEntry>();
        allPackages.AddRange(PackagesOf(projectSettings).Select(p => new PackageEntry(p, "project")));
        allPackages.AddRange(PackagesOf(globalSettings).Select(p => new PackageEntry(p, "user")));
        await ResolvePackageSourcesAsync(DedupePackages(allPackages), accumulator, onMissing);

        var globalBaseDir = _agentDir;
        var projectBaseDir = Path.Combine(_cwd, AppConfig.ConfigDirName);
        foreach (var type in ResourceTypes)
        {
            ResolveLocalEntries(StringEntries(projectSettings, type), type, accumulator, new PathMetadata("local", "project", "top-level"), projectBaseDir);
            ResolveLocalEntries(StringEntries(globalSettings, type), type, accumulator, new PathMetadata("local", "user", "top-level"), globalBaseDir);
        }

        AddAutoDiscoveredResources(accumulator, globalSettings, projectSettings, globalBaseDir, projectBaseDir);
        return ToResolvedPaths(accumulator);
    }

    public async Task<ResolvedPaths> ResolveExtensionSourcesAsync(IEnumerable<string> sources, bool local = false, bool temporary = false)
    {
        var accumulator = new Accumulator();
        var scope = temporary ? "temporary" : local ? "project" : "user";
        await ResolvePackageSourcesAsync(sources.Select(s => new PackageEntry(JsonValue.Create(s), scope)).ToList(), accumulator);
        return ToResolvedPaths(accumulator);
    }

    public List<ConfiguredPackage> ListConfiguredPackages()
    {
        var result = new List<ConfiguredPackage>();
        foreach (var pkg in PackagesOf(settingsManager.GetGlobalSettings()))
        {
            var source = SourceOf(pkg);
            result.Add(new ConfiguredPackage(source, "user", pkg is JsonObject, GetInstalledPath(source, "user")));
        }
        foreach (var pkg in PackagesOf(settingsManager.GetProjectSettings()))
        {
            var source = SourceOf(pkg);
            result.Add(new ConfiguredPackage(source, "project", pkg is JsonObject, GetInstalledPath(source, "project")));
        }
        return result;
    }

    // ----- install / remove / update -----

    public async Task InstallAsync(string source, bool local = false)
    {
        var parsed = ParseSource(source);
        var scope = local ? "project" : "user";
        AssertProjectTrustedForScope(scope);
        await WithProgressAsync("install", source, $"Installing {source}...", async () =>
        {
            switch (parsed)
            {
                case NpmSource npm:
                    await InstallNpmAsync(npm, scope, false);
                    return;
                case GitParsedSource git:
                    await InstallGitAsync(git.Git, scope);
                    return;
                case LocalSource localSource:
                {
                    var resolved = ResolvePath(localSource.Path);
                    if (!Exists(resolved)) throw new InvalidOperationException($"Path does not exist: {resolved}");
                    return;
                }
                default:
                    throw new InvalidOperationException($"Unsupported install source: {source}");
            }
        });
    }

    public async Task InstallAndPersistAsync(string source, bool local = false)
    {
        await InstallAsync(source, local);
        AddSourceToSettings(source, local);
    }

    public async Task RemoveAsync(string source, bool local = false)
    {
        var parsed = ParseSource(source);
        var scope = local ? "project" : "user";
        AssertProjectTrustedForScope(scope);
        await WithProgressAsync("remove", source, $"Removing {source}...", async () =>
        {
            switch (parsed)
            {
                case NpmSource npm:
                    await UninstallNpmAsync(npm, scope);
                    return;
                case GitParsedSource git:
                    RemoveGit(git.Git, scope);
                    return;
                case LocalSource:
                    return;
                default:
                    throw new InvalidOperationException($"Unsupported remove source: {source}");
            }
        });
    }

    public async Task<bool> RemoveAndPersistAsync(string source, bool local = false)
    {
        await RemoveAsync(source, local);
        return RemoveSourceFromSettings(source, local);
    }

    public async Task UpdateAsync(string? source = null)
    {
        var globalSettings = settingsManager.GetGlobalSettings();
        var projectSettings = settingsManager.GetProjectSettings();
        var identity = source is not null ? GetPackageIdentity(source) : null;
        var matched = false;
        var updateSources = new List<(string Source, string Scope)>();
        foreach (var pkg in PackagesOf(globalSettings))
        {
            var sourceStr = SourceOf(pkg);
            if (identity is not null && GetPackageIdentity(sourceStr, "user") != identity) continue;
            matched = true;
            updateSources.Add((sourceStr, "user"));
        }
        foreach (var pkg in PackagesOf(projectSettings))
        {
            var sourceStr = SourceOf(pkg);
            if (identity is not null && GetPackageIdentity(sourceStr, "project") != identity) continue;
            matched = true;
            updateSources.Add((sourceStr, "project"));
        }
        if (source is not null && !matched)
        {
            throw new InvalidOperationException(BuildNoMatchingPackageMessage(source, [.. PackagesOf(globalSettings), .. PackagesOf(projectSettings)]));
        }
        await UpdateConfiguredSourcesAsync(updateSources);
    }

    private async Task UpdateConfiguredSourcesAsync(List<(string Source, string Scope)> sources)
    {
        if (IsOfflineModeEnabled() || sources.Count == 0) return;

        var npmCandidates = new List<(string Source, string Scope, NpmSource Parsed)>();
        var gitCandidates = new List<(string Source, string Scope, GitSource Parsed)>();
        foreach (var (source, scope) in sources)
        {
            switch (ParseSource(source))
            {
                // Pinned npm versions are fixed; pinned git refs are reconciled when the configured ref changes.
                case NpmSource { Pinned: false } npm:
                    npmCandidates.Add((source, scope, npm));
                    break;
                case GitParsedSource git:
                    gitCandidates.Add((source, scope, git.Git));
                    break;
            }
        }

        var npmResults = await RunWithConcurrencyAsync(npmCandidates.Select(entry => (Func<Task<(string Source, string Scope, NpmSource Parsed, bool ShouldUpdate)>>)(async () =>
            (entry.Source, entry.Scope, entry.Parsed, await ShouldUpdateNpmSourceAsync(entry.Parsed, entry.Scope)))).ToList(), UpdateCheckConcurrency);
        var userNpmUpdates = npmResults.Where(r => r.ShouldUpdate && r.Scope == "user").Select(r => (r.Source, r.Parsed)).ToList();
        var projectNpmUpdates = npmResults.Where(r => r.ShouldUpdate && r.Scope != "user").Select(r => (r.Source, r.Parsed)).ToList();

        var tasks = new List<Task>();
        if (userNpmUpdates.Count > 0) tasks.Add(UpdateNpmBatchAsync(userNpmUpdates, "user"));
        if (projectNpmUpdates.Count > 0) tasks.Add(UpdateNpmBatchAsync(projectNpmUpdates, "project"));
        if (gitCandidates.Count > 0)
        {
            var gitTasks = gitCandidates.Select(entry => (Func<Task<bool>>)(async () =>
            {
                await WithProgressAsync("update", entry.Source, $"Updating {entry.Source}...", () => UpdateGitAsync(entry.Parsed, entry.Scope));
                return true;
            })).ToList();
            tasks.Add(RunWithConcurrencyAsync(gitTasks, GitUpdateConcurrency));
        }
        await Task.WhenAll(tasks);
    }

    private async Task<bool> ShouldUpdateNpmSourceAsync(NpmSource source, string scope)
    {
        var installedPath = GetManagedNpmInstallPath(source, scope);
        var installedVersion = Exists(installedPath) ? GetInstalledNpmVersion(installedPath) : null;
        if (installedVersion is null) return true;
        try
        {
            var targetVersion = await GetLatestNpmVersionAsync(source.Version is not null ? source.Spec : source.Name, source.Range);
            return NpmSemver.Gt(targetVersion, installedVersion);
        }
        catch
        {
            // Preserve existing update behavior when version lookup fails.
            return true;
        }
    }

    private async Task UpdateNpmBatchAsync(List<(string Source, NpmSource Parsed)> sources, string scope)
    {
        if (sources.Count == 0) return;
        var sourceLabel = sources.Count == 1 ? sources[0].Source : $"{scope} npm packages";
        var message = sources.Count == 1 ? $"Updating {sources[0].Source}..." : $"Updating {scope} npm packages...";
        var specs = sources.Select(entry => entry.Parsed.Version is not null ? entry.Parsed.Spec : $"{entry.Parsed.Name}@latest").ToList();
        await WithProgressAsync("update", sourceLabel, message, async () =>
        {
            var installRoot = GetNpmInstallRoot(scope, false);
            EnsureNpmProject(installRoot);
            await RunNpmCommandAsync(GetNpmInstallArgs(specs, installRoot));
        });
    }

    public async Task<List<PackageUpdate>> CheckForAvailableUpdatesAsync()
    {
        if (IsOfflineModeEnabled()) return [];
        var allPackages = new List<PackageEntry>();
        allPackages.AddRange(PackagesOf(settingsManager.GetProjectSettings()).Select(p => new PackageEntry(p, "project")));
        allPackages.AddRange(PackagesOf(settingsManager.GetGlobalSettings()).Select(p => new PackageEntry(p, "user")));

        var checks = DedupePackages(allPackages).Where(e => e.Scope != "temporary").Select(entry => (Func<Task<PackageUpdate?>>)(async () =>
        {
            var source = SourceOf(entry.Pkg);
            switch (ParseSource(source))
            {
                case NpmSource { Pinned: false } npm:
                {
                    var installedPath = GetNpmInstallPath(npm, entry.Scope);
                    if (!Exists(installedPath) || !await NpmHasAvailableUpdateAsync(npm, installedPath)) return null;
                    return new PackageUpdate(source, npm.Name, "npm", entry.Scope);
                }
                case GitParsedSource { Pinned: false } git:
                {
                    var installedPath = GetGitInstallPath(git.Git, entry.Scope);
                    if (!Exists(installedPath) || !await GitHasAvailableUpdateAsync(installedPath)) return null;
                    return new PackageUpdate(source, $"{git.Git.Host}/{git.Git.Path}", "git", entry.Scope);
                }
                default:
                    return null;
            }
        })).ToList();
        return (await RunWithConcurrencyAsync(checks, UpdateCheckConcurrency)).OfType<PackageUpdate>().ToList();
    }

    private async Task ResolvePackageSourcesAsync(List<PackageEntry> sources, Accumulator accumulator, Func<string, Task<MissingSourceAction>>? onMissing = null)
    {
        foreach (var (pkg, scope) in sources)
        {
            var sourceStr = SourceOf(pkg);
            var filter = pkg as JsonObject;
            var deltaBase = FindAutoloadDeltaBase(pkg, scope, sources);
            var resolvedSource = deltaBase?.Source ?? sourceStr;
            var resolvedScope = deltaBase?.Scope ?? scope;
            var parsed = ParseSource(resolvedSource);
            var metadata = new PathMetadata(sourceStr, scope, "package");

            if (parsed is LocalSource local)
            {
                ResolveLocalExtensionSource(local, accumulator, filter, metadata, GetBaseDirForScope(resolvedScope));
                continue;
            }

            async Task<bool> InstallMissingAsync()
            {
                if (IsOfflineModeEnabled()) return false;
                if (onMissing is not null)
                {
                    var action = await onMissing(resolvedSource);
                    if (action == MissingSourceAction.Skip) return false;
                    if (action == MissingSourceAction.Error) throw new InvalidOperationException($"Missing source: {resolvedSource}");
                }
                await InstallParsedSourceAsync(parsed, resolvedScope);
                return true;
            }

            if (parsed is NpmSource npm)
            {
                var installedPath = GetNpmInstallPath(npm, resolvedScope);
                if (!Exists(installedPath) || !InstalledNpmMatchesConfiguredVersion(npm, installedPath))
                {
                    if (!await InstallMissingAsync()) continue;
                    installedPath = GetNpmInstallPath(npm, resolvedScope);
                }
                CollectPackageResources(installedPath, accumulator, filter, metadata with { BaseDir = installedPath });
                continue;
            }

            if (parsed is GitParsedSource git)
            {
                var installedPath = GetGitInstallPath(git.Git, resolvedScope);
                if (!Exists(installedPath))
                {
                    if (!await InstallMissingAsync()) continue;
                }
                else if (resolvedScope == "temporary" && !git.Pinned && !IsOfflineModeEnabled())
                {
                    await RefreshTemporaryGitSourceAsync(git.Git, resolvedSource);
                }
                CollectPackageResources(installedPath, accumulator, filter, metadata with { BaseDir = installedPath });
            }
        }
    }

    private (string Source, string Scope)? FindAutoloadDeltaBase(JsonNode pkg, string scope, List<PackageEntry> sources)
    {
        if (scope != "project" || pkg is not JsonObject obj || PiJson.GetBool(obj["autoload"]) != false) return null;
        var identity = GetPackageIdentity(SourceOf(pkg), scope);
        var userEntry = sources.FirstOrDefault(e => e.Scope == "user" && GetPackageIdentity(SourceOf(e.Pkg), "user") == identity);
        return userEntry is null ? null : (SourceOf(userEntry.Pkg), "user");
    }

    private void ResolveLocalExtensionSource(LocalSource source, Accumulator accumulator, JsonObject? filter, PathMetadata metadata, string baseDir)
    {
        var resolved = ResolvePathFromBase(source.Path, baseDir);
        try
        {
            if (File.Exists(resolved))
            {
                accumulator.Add("extensions", resolved, metadata with { BaseDir = Path.GetDirectoryName(resolved) }, true);
                return;
            }
            if (Directory.Exists(resolved))
            {
                var withBase = metadata with { BaseDir = resolved };
                if (!CollectPackageResources(resolved, accumulator, filter, withBase)) accumulator.Add("extensions", resolved, withBase, true);
            }
        }
        catch
        {
            // Ignore unreadable local sources.
        }
    }

    private async Task InstallParsedSourceAsync(ParsedSource parsed, string scope)
    {
        if (parsed is NpmSource npm) await InstallNpmAsync(npm, scope, scope == "temporary");
        else if (parsed is GitParsedSource git) await InstallGitAsync(git.Git, scope);
    }

    // ----- source matching -----

    private string GetSourceMatchKeyForInput(string source) => ParseSource(source) switch
    {
        NpmSource npm => $"npm:{npm.Name}",
        GitParsedSource git => $"git:{git.Git.Host}/{git.Git.Path}",
        LocalSource local => $"local:{ResolvePath(local.Path)}",
        _ => source,
    };

    private string GetSourceMatchKeyForSettings(string source, string scope) => ParseSource(source) switch
    {
        NpmSource npm => $"npm:{npm.Name}",
        GitParsedSource git => $"git:{git.Git.Host}/{git.Git.Path}",
        LocalSource local => $"local:{ResolvePathFromBase(local.Path, GetBaseDirForScope(scope))}",
        _ => source,
    };

    private string BuildNoMatchingPackageMessage(string source, List<JsonNode> configuredPackages)
    {
        var suggestion = FindSuggestedConfiguredSource(source, configuredPackages);
        return suggestion is null ? $"No matching package found for {source}" : $"No matching package found for {source}. Did you mean {suggestion}?";
    }

    private string? FindSuggestedConfiguredSource(string source, List<JsonNode> configuredPackages)
    {
        var trimmed = source.Trim();
        foreach (var pkg in configuredPackages)
        {
            var sourceStr = SourceOf(pkg);
            switch (ParseSource(sourceStr))
            {
                case NpmSource npm when trimmed == npm.Name || trimmed == npm.Spec:
                    return sourceStr;
                case GitParsedSource git:
                {
                    var shorthand = $"{git.Git.Host}/{git.Git.Path}";
                    if (trimmed == shorthand || (git.Git.Ref is not null && trimmed == $"{shorthand}@{git.Git.Ref}")) return sourceStr;
                    break;
                }
            }
        }
        return null;
    }

    private bool PackageSourcesMatch(JsonNode existing, string inputSource, string scope) =>
        GetSourceMatchKeyForSettings(SourceOf(existing), scope) == GetSourceMatchKeyForInput(inputSource);

    private string NormalizePackageSourceForSettings(string source, string scope)
    {
        if (ParseSource(source) is not LocalSource local) return source;
        var baseDir = GetBaseDirForScope(scope);
        var rel = Path.GetRelativePath(baseDir, ResolvePath(local.Path));
        return rel is "" or "." ? "." : rel;
    }

    [GeneratedRegex(@"^(@?[^@]+(?:/[^@]+)?)(?:@(.+))?$", RegexOptions.Singleline)]
    private static partial Regex NpmSpec();

    private ParsedSource ParseSource(string source)
    {
        if (source.StartsWith("npm:", StringComparison.Ordinal))
        {
            var spec = source["npm:".Length..].Trim();
            var match = NpmSpec().Match(spec);
            var name = match.Success ? match.Groups[1].Value : spec;
            var version = match.Success && match.Groups[2].Success ? match.Groups[2].Value : null;
            return new NpmSource(spec, name, version, version is null ? null : NpmSemver.ValidRange(version), NpmSemver.Valid(version) is not null && version is not null);
        }
        if (PathUtils.IsLocalPath(source)) return new LocalSource(source);
        if (GitUrlParser.Parse(source) is { } git) return new GitParsedSource(git);
        return new LocalSource(source);
    }

    private bool InstalledNpmMatchesConfiguredVersion(NpmSource source, string installedPath)
    {
        var installedVersion = GetInstalledNpmVersion(installedPath);
        if (installedVersion is null) return false;
        return source.Range is null || NpmSemver.Satisfies(installedVersion, source.Range);
    }

    private async Task<bool> NpmHasAvailableUpdateAsync(NpmSource source, string installedPath)
    {
        if (IsOfflineModeEnabled()) return false;
        var installedVersion = GetInstalledNpmVersion(installedPath);
        if (installedVersion is null) return false;
        try
        {
            var targetVersion = await GetLatestNpmVersionAsync(source.Version is not null ? source.Spec : source.Name, source.Range);
            return NpmSemver.Gt(targetVersion, installedVersion);
        }
        catch
        {
            return false;
        }
    }

    private static string? GetInstalledNpmVersion(string installedPath)
    {
        var packageJsonPath = Path.Combine(installedPath, "package.json");
        if (!File.Exists(packageJsonPath)) return null;
        try
        {
            return PiJson.GetString((JsonNode.Parse(TextHelpers.StripBom(File.ReadAllText(packageJsonPath))) as JsonObject)?["version"]);
        }
        catch
        {
            return null;
        }
    }

    private async Task<string> GetLatestNpmVersionAsync(string packageSpec, SemVersionRange? range)
    {
        var (command, args) = GetNpmCommand();
        var raw = (await ProcessRunner.RunCaptureAsync(command, [.. args, "view", packageSpec, "version", "--json"], _cwd, NetworkTimeoutMs)).Trim();
        if (raw.Length == 0) throw new InvalidOperationException("Empty response from npm view");
        var parsed = JsonNode.Parse(raw);
        if (PiJson.GetString(parsed) is { } single) return single;
        if (parsed is JsonArray array)
        {
            var versions = array.Select(PiJson.GetString).Where(v => !string.IsNullOrEmpty(v)).Select(v => (Text: v!, Version: NpmSemver.Valid(v))).Where(v => v.Version is not null).ToList();
            var candidates = range is null ? versions : versions.Where(v => range.Contains(v.Version!)).ToList();
            var latest = candidates.OrderByDescending(v => v.Version!, SemVersion.PrecedenceComparer).FirstOrDefault();
            if (latest.Text is not null) return latest.Text;
        }
        throw new InvalidOperationException("Unexpected response from npm view");
    }

    // ----- git -----

    private static readonly IReadOnlyDictionary<string, string> NoTerminalPrompt = new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0" };

    private static Task<string> GitCaptureAsync(string cwd, params string[] args) => ProcessRunner.RunCaptureAsync("git", args, cwd, NetworkTimeoutMs);

    private static async Task<bool> GitHasAvailableUpdateAsync(string installedPath)
    {
        if (IsOfflineModeEnabled()) return false;
        try
        {
            var localHead = await GitCaptureAsync(installedPath, "rev-parse", "HEAD");
            var remoteHead = await GetRemoteGitHeadAsync(installedPath);
            return localHead.Trim() != remoteHead.Trim();
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string> GetRemoteGitHeadAsync(string installedPath)
    {
        if (await GetGitUpstreamRefAsync(installedPath) is { } upstreamRef)
        {
            var remote = await ProcessRunner.RunCaptureAsync("git", ["ls-remote", "origin", upstreamRef], installedPath, NetworkTimeoutMs, NoTerminalPrompt);
            if (Regex.Match(remote, @"^([0-9a-f]{40})\s+", RegexOptions.Multiline) is { Success: true } m) return m.Groups[1].Value;
        }
        var head = await ProcessRunner.RunCaptureAsync("git", ["ls-remote", "origin", "HEAD"], installedPath, NetworkTimeoutMs, NoTerminalPrompt);
        var match = Regex.Match(head, @"^([0-9a-f]{40})\s+HEAD\r?$", RegexOptions.Multiline);
        return match.Success ? match.Groups[1].Value : throw new InvalidOperationException("Failed to determine remote HEAD");
    }

    private static async Task<(string Ref, string Head, string[] FetchArgs)> GetLocalGitUpdateTargetAsync(string installedPath)
    {
        try
        {
            var upstream = (await GitCaptureAsync(installedPath, "rev-parse", "--abbrev-ref", "@{upstream}")).Trim();
            if (!upstream.StartsWith("origin/", StringComparison.Ordinal)) throw new InvalidOperationException($"Unsupported upstream remote: {upstream}");
            var branch = upstream["origin/".Length..];
            if (branch.Length == 0) throw new InvalidOperationException("Missing upstream branch name");
            var head = await GitCaptureAsync(installedPath, "rev-parse", "@{upstream}");
            return ("@{upstream}", head, ["fetch", "--prune", "--no-tags", "origin", $"+refs/heads/{branch}:refs/remotes/origin/{branch}"]);
        }
        catch
        {
            try
            {
                await ProcessRunner.RunAsync("git", ["remote", "set-head", "origin", "-a"], installedPath);
            }
            catch
            {
                // Best effort.
            }
            var head = await GitCaptureAsync(installedPath, "rev-parse", "origin/HEAD");
            string originHeadRef;
            try
            {
                originHeadRef = await GitCaptureAsync(installedPath, "symbolic-ref", "refs/remotes/origin/HEAD");
            }
            catch
            {
                originHeadRef = "";
            }
            var branch = Regex.Replace(originHeadRef.Trim(), "^refs/remotes/origin/", "");
            return branch.Length > 0
                ? ("origin/HEAD", head, ["fetch", "--prune", "--no-tags", "origin", $"+refs/heads/{branch}:refs/remotes/origin/{branch}"])
                : ("origin/HEAD", head, ["fetch", "--prune", "--no-tags", "origin", "+HEAD:refs/remotes/origin/HEAD"]);
        }
    }

    private static async Task<string?> GetGitUpstreamRefAsync(string installedPath)
    {
        try
        {
            var upstream = (await GitCaptureAsync(installedPath, "rev-parse", "--abbrev-ref", "@{upstream}")).Trim();
            if (!upstream.StartsWith("origin/", StringComparison.Ordinal)) return null;
            var branch = upstream["origin/".Length..];
            return branch.Length > 0 ? $"refs/heads/{branch}" : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<List<T>> RunWithConcurrencyAsync<T>(List<Func<Task<T>>> tasks, int limit)
    {
        if (tasks.Count == 0) return [];
        var results = new T[tasks.Count];
        var nextIndex = 0;
        async Task Worker()
        {
            while (true)
            {
                var index = nextIndex++;
                if (index >= tasks.Count) return;
                results[index] = await tasks[index]();
            }
        }
        await Task.WhenAll(Enumerable.Range(0, Math.Max(1, Math.Min(limit, tasks.Count))).Select(_ => Worker()));
        return [.. results];
    }

    /// <summary>Identity ignoring version/ref, so SSH and HTTPS URLs of one repository and project/global duplicates match.</summary>
    private string GetPackageIdentity(string source, string? scope = null) => ParseSource(source) switch
    {
        NpmSource npm => $"npm:{npm.Name}",
        GitParsedSource git => $"git:{git.Git.Host}/{git.Git.Path}",
        LocalSource local when scope is not null => $"local:{ResolvePathFromBase(local.Path, GetBaseDirForScope(scope))}",
        LocalSource local => $"local:{ResolvePath(local.Path)}",
        _ => source,
    };

    /// <summary>Project scope wins for the same identity; a project entry with autoload=false is a delta, so both are kept.</summary>
    private List<PackageEntry> DedupePackages(List<PackageEntry> packages)
    {
        var result = new List<PackageEntry>();
        var seen = new Dictionary<string, int>();
        foreach (var entry in packages)
        {
            var identity = GetPackageIdentity(SourceOf(entry.Pkg), entry.Scope);
            if (!seen.TryGetValue(identity, out var index))
            {
                seen[identity] = result.Count;
                result.Add(entry);
                continue;
            }
            var existing = result[index];
            if (existing.Scope == "project" && entry.Scope == "user")
            {
                if (existing.Pkg is JsonObject obj && PiJson.GetBool(obj["autoload"]) == false) result.Add(entry);
            }
            else if (entry.Scope == "project")
            {
                result[index] = entry;
            }
        }
        return result;
    }

    private void AssertProjectTrustedForScope(string scope)
    {
        if (scope == "project" && !settingsManager.IsProjectTrusted) throw new InvalidOperationException("Project is not trusted; refusing to access project package storage");
    }

    private (string Command, List<string> Args) GetNpmCommand()
    {
        var configured = settingsManager.NpmCommand;
        if (configured is not { Count: > 0 }) return ("npm", []);
        if (string.IsNullOrEmpty(configured[0])) throw new InvalidOperationException("Invalid npmCommand: first array entry must be a non-empty command");
        return (configured[0], configured.Skip(1).ToList());
    }

    private string GetPackageManagerName()
    {
        var (command, args) = GetNpmCommand();
        var parts = new List<string> { command };
        parts.AddRange(args);
        var separatorIndex = parts.LastIndexOf("--");
        var packageManagerCommand = separatorIndex >= 0 ? parts.ElementAtOrDefault(separatorIndex + 1) : command;
        return string.IsNullOrEmpty(packageManagerCommand) ? "" : Regex.Replace(Path.GetFileName(packageManagerCommand), @"\.(cmd|exe)$", "", RegexOptions.IgnoreCase);
    }

    private Task RunNpmCommandAsync(IEnumerable<string> args, string? cwd = null)
    {
        var (command, baseArgs) = GetNpmCommand();
        return ProcessRunner.RunAsync(command, [.. baseArgs, .. args], cwd);
    }

    private string RunNpmCommandSync(IEnumerable<string> args)
    {
        var (command, baseArgs) = GetNpmCommand();
        return ProcessRunner.RunSync(command, [.. baseArgs, .. args]);
    }

    private List<string> GetGitDependencyInstallArgs() => settingsManager.NpmCommand is { Count: > 0 } ? ["install"] : ["install", "--omit=dev"];

    private List<string> GetNpmInstallArgs(List<string> specs, string installRoot)
    {
        // Managed installs skip peer dependency resolution so host-provided pi packages are never installed as peers.
        return GetPackageManagerName() switch
        {
            "bun" => ["install", .. specs, "--cwd", installRoot, "--omit=peer"],
            "pnpm" => ["install", .. specs, "--prefix", installRoot, "--config.auto-install-peers=false", "--config.strict-peer-dependencies=false", "--config.strict-dep-builds=false"],
            _ => ["install", .. specs, "--prefix", installRoot, "--legacy-peer-deps"],
        };
    }

    private async Task InstallNpmAsync(NpmSource source, string scope, bool temporary)
    {
        var installRoot = GetNpmInstallRoot(scope, temporary);
        EnsureNpmProject(installRoot);
        await RunNpmCommandAsync(GetNpmInstallArgs([source.Spec], installRoot));
    }

    private async Task UninstallNpmAsync(NpmSource source, string scope)
    {
        var installRoot = GetNpmInstallRoot(scope, false);
        if (!Directory.Exists(installRoot)) return;
        var packageManagerName = GetPackageManagerName();
        if (packageManagerName == "bun")
        {
            await RunNpmCommandAsync(["uninstall", source.Name, "--cwd", installRoot]);
            return;
        }
        var args = new List<string> { "uninstall", source.Name, "--prefix", installRoot };
        if (packageManagerName != "pnpm") args.Add("--legacy-peer-deps");
        await RunNpmCommandAsync(args);
    }

    private async Task InstallGitAsync(GitSource source, string scope)
    {
        var targetDir = GetGitInstallPath(source, scope);
        if (Directory.Exists(targetDir))
        {
            if (source.Ref is not null)
            {
                await EnsureGitRefAsync(targetDir, ["fetch", "origin", source.Ref], "FETCH_HEAD");
                return;
            }
            var target = await GetLocalGitUpdateTargetAsync(targetDir);
            await EnsureGitRefAsync(targetDir, target.FetchArgs, target.Ref);
            return;
        }
        var gitRoot = GetGitInstallRoot(scope);
        if (gitRoot is not null) EnsureGitIgnore(gitRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(targetDir)!);
        DeleteFile(GetGitUpdateMarkerPath(targetDir));
        try
        {
            await ProcessRunner.RunAsync("git", ["clone", source.Repo, targetDir]);
            if (source.Ref is not null) await ProcessRunner.RunAsync("git", ["checkout", source.Ref], targetDir);
            if (File.Exists(Path.Combine(targetDir, "package.json"))) await RunNpmCommandAsync(GetGitDependencyInstallArgs(), targetDir);
        }
        catch
        {
            DeleteDirectory(targetDir);
            PruneEmptyGitParents(targetDir, gitRoot);
            throw;
        }
    }

    private async Task UpdateGitAsync(GitSource source, string scope)
    {
        var targetDir = GetGitInstallPath(source, scope);
        if (!Directory.Exists(targetDir))
        {
            await InstallGitAsync(source, scope);
            return;
        }
        if (source.Ref is not null)
        {
            await EnsureGitRefAsync(targetDir, ["fetch", "origin", source.Ref], "FETCH_HEAD");
            return;
        }
        var target = await GetLocalGitUpdateTargetAsync(targetDir);
        await EnsureGitRefAsync(targetDir, target.FetchArgs, target.Ref);
    }

    private static bool HasMissingGitDependencies(string targetDir)
    {
        var packageJsonPath = Path.Combine(targetDir, "package.json");
        if (!File.Exists(packageJsonPath)) return false;
        try
        {
            if ((JsonNode.Parse(TextHelpers.StripBom(File.ReadAllText(packageJsonPath))) as JsonObject)?["dependencies"] is not JsonObject dependencies) return false;
            var nodeModulesDir = Path.GetFullPath(Path.Combine(targetDir, "node_modules"));
            return dependencies.Select(kv => kv.Key).Any(name =>
            {
                var dependencyPath = Path.GetFullPath(Path.Combine(nodeModulesDir, name));
                if (!dependencyPath.StartsWith(nodeModulesDir + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return false;
                return !Exists(dependencyPath);
            });
        }
        catch
        {
            return false;
        }
    }

    private async Task RepairMissingGitDependenciesAsync(string targetDir)
    {
        if (!HasMissingGitDependencies(targetDir)) return;
        await RunNpmCommandAsync(GetGitDependencyInstallArgs(), targetDir);
    }

    private static string GetGitUpdateMarkerPath(string targetDir) =>
        Path.Combine(Path.GetDirectoryName(targetDir)!, $".{Path.GetFileName(targetDir)}.pi-update-incomplete");

    private async Task CleanAndInstallGitDependenciesAsync(string targetDir, string markerPath)
    {
        try
        {
            await ProcessRunner.RunAsync("git", ["clean", "-fdx"], targetDir);
        }
        catch
        {
            try
            {
                await RepairMissingGitDependenciesAsync(targetDir);
            }
            catch
            {
                // Preserve the clean error.
            }
            throw;
        }
        if (File.Exists(Path.Combine(targetDir, "package.json"))) await RunNpmCommandAsync(GetGitDependencyInstallArgs(), targetDir);
        DeleteFile(markerPath);
    }

    private async Task EnsureGitRefAsync(string targetDir, string[] fetchArgs, string @ref)
    {
        // Fetch only the ref we will reset to, avoiding unrelated branch/tag noise.
        await ProcessRunner.RunAsync("git", fetchArgs, targetDir);
        var localHead = await GitCaptureAsync(targetDir, "rev-parse", "HEAD");
        var commitRef = $"{@ref}^{{commit}}";
        var targetHead = await GitCaptureAsync(targetDir, "rev-parse", commitRef);
        var markerPath = GetGitUpdateMarkerPath(targetDir);
        if (localHead.Trim() == targetHead.Trim())
        {
            if (File.Exists(markerPath)) await CleanAndInstallGitDependenciesAsync(targetDir, markerPath);
            else await RepairMissingGitDependenciesAsync(targetDir);
            return;
        }
        await File.WriteAllTextAsync(markerPath, "");
        await ProcessRunner.RunAsync("git", ["reset", "--hard", commitRef], targetDir);
        await CleanAndInstallGitDependenciesAsync(targetDir, markerPath);
    }

    private async Task RefreshTemporaryGitSourceAsync(GitSource source, string sourceStr)
    {
        if (IsOfflineModeEnabled()) return;
        try
        {
            await WithProgressAsync("pull", sourceStr, $"Refreshing {sourceStr}...", () => UpdateGitAsync(source, "temporary"));
        }
        catch
        {
            // Keep the cached temporary checkout if refresh fails.
        }
    }

    private void RemoveGit(GitSource source, string scope)
    {
        var targetDir = GetGitInstallPath(source, scope);
        DeleteDirectory(targetDir);
        DeleteFile(GetGitUpdateMarkerPath(targetDir));
        PruneEmptyGitParents(targetDir, GetGitInstallRoot(scope));
    }

    private static void DeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Like rmSync(force).
        }
    }

    private static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        // git objects are read-only on Windows; clear attributes so the tree can be removed.
        foreach (var file in Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
        {
            try
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            catch
            {
                // Best effort.
            }
        }
        Directory.Delete(path, recursive: true);
    }

    private static void PruneEmptyGitParents(string targetDir, string? installRoot)
    {
        if (installRoot is null) return;
        var resolvedRoot = Path.GetFullPath(installRoot);
        var current = Path.GetDirectoryName(targetDir);
        while (current is not null && current.StartsWith(resolvedRoot, StringComparison.Ordinal) && current != resolvedRoot)
        {
            if (!Directory.Exists(current))
            {
                current = Path.GetDirectoryName(current);
                continue;
            }
            if (Directory.EnumerateFileSystemEntries(current).Any()) break;
            try
            {
                Directory.Delete(current, recursive: true);
            }
            catch
            {
                break;
            }
            current = Path.GetDirectoryName(current);
        }
    }

    private static void MarkPathIgnoredByCloudSync(string path)
    {
        string[] attrs = OperatingSystem.IsMacOS() ? ["com.dropbox.ignored", "com.apple.fileprovider.ignore#P"] : OperatingSystem.IsLinux() ? ["user.com.dropbox.ignored"] : [];
        foreach (var attr in attrs)
        {
            try
            {
                if (OperatingSystem.IsMacOS()) ProcessRunner.RunSync("xattr", ["-w", attr, "1", path]);
                else ProcessRunner.RunSync("setfattr", ["-n", attr, "-v", "1", path]);
            }
            catch
            {
                // Attribute tools are optional.
            }
        }
    }

    private static void EnsureNpmProject(string installRoot)
    {
        Directory.CreateDirectory(installRoot);
        MarkPathIgnoredByCloudSync(installRoot);
        EnsureGitIgnore(installRoot);
        var packageJsonPath = Path.Combine(installRoot, "package.json");
        if (!File.Exists(packageJsonPath)) File.WriteAllText(packageJsonPath, "{\n  \"name\": \"pi-extensions\",\n  \"private\": true\n}");
    }

    private static void EnsureGitIgnore(string dir)
    {
        Directory.CreateDirectory(dir);
        var ignorePath = Path.Combine(dir, ".gitignore");
        if (!File.Exists(ignorePath)) File.WriteAllText(ignorePath, "*\n!.gitignore\n");
    }

    private string GetNpmInstallRoot(string scope, bool temporary)
    {
        if (temporary) return GetTemporaryDir("npm");
        if (scope == "project")
        {
            AssertProjectTrustedForScope(scope);
            return Path.Combine(_cwd, AppConfig.ConfigDirName, "npm");
        }
        return Path.Combine(_agentDir, "npm");
    }

    private string GetGlobalNpmRoot()
    {
        var (command, args) = GetNpmCommand();
        var commandKey = string.Join("\0", new[] { command }.Concat(args));
        if (_globalNpmRoot is not null && _globalNpmRootCommandKey == commandKey) return _globalNpmRoot;
        if (GetPackageManagerName() == "bun")
        {
            var binDir = RunNpmCommandSync(["pm", "bin", "-g"]).Trim();
            _globalNpmRoot = Path.Combine(Path.GetDirectoryName(binDir)!, "install", "global", "node_modules");
        }
        else
        {
            _globalNpmRoot = RunNpmCommandSync(["root", "-g"]).Trim();
        }
        _globalNpmRootCommandKey = commandKey;
        return _globalNpmRoot;
    }

    private string? GetPnpmGlobalPackagePath(string packageName)
    {
        if (GetPackageManagerName() != "pnpm") return null;
        if (JsonNode.Parse(RunNpmCommandSync(["list", "-g", "--depth", "0", "--json"])) is not JsonArray entries) return null;
        foreach (var entry in entries.OfType<JsonObject>())
        {
            if (PiJson.GetString((entry["dependencies"] as JsonObject)?[packageName]?["path"]) is { Length: > 0 } path) return path;
        }
        return null;
    }

    private string GetManagedNpmInstallPath(NpmSource source, string scope)
    {
        if (scope == "temporary") return Path.Combine(GetTemporaryDir("npm"), "node_modules", source.Name);
        if (scope == "project")
        {
            AssertProjectTrustedForScope(scope);
            return Path.Combine(_cwd, AppConfig.ConfigDirName, "npm", "node_modules", source.Name);
        }
        return Path.Combine(_agentDir, "npm", "node_modules", source.Name);
    }

    private string? GetLegacyGlobalNpmInstallPath(NpmSource source)
    {
        try
        {
            return GetPnpmGlobalPackagePath(source.Name) ?? Path.Combine(GetGlobalNpmRoot(), source.Name);
        }
        catch
        {
            return null;
        }
    }

    private string GetNpmInstallPath(NpmSource source, string scope)
    {
        var managedPath = GetManagedNpmInstallPath(source, scope);
        if (scope != "user" || Exists(managedPath)) return managedPath;
        var legacyPath = GetLegacyGlobalNpmInstallPath(source);
        return legacyPath is not null && Exists(legacyPath) ? legacyPath : managedPath;
    }

    private string GetGitInstallPath(GitSource source, string scope)
    {
        if (scope == "temporary") return GetTemporaryDir($"git-{source.Host}", source.Path);
        var installRoot = GetGitInstallRoot(scope) ?? throw new InvalidOperationException("Missing git install root");
        return ResolveManagedPath(installRoot, source.Host, source.Path);
    }

    private string? GetGitInstallRoot(string scope)
    {
        if (scope == "temporary") return null;
        if (scope == "project")
        {
            AssertProjectTrustedForScope(scope);
            return Path.Combine(_cwd, AppConfig.ConfigDirName, "git");
        }
        return Path.Combine(_agentDir, "git");
    }

    private string GetTemporaryDir(string prefix, string? suffix = null)
    {
        var root = ResolveManagedPath(GetExtensionTempFolder(_agentDir), prefix);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{prefix}-{suffix ?? ""}")))[..8];
        return ResolveManagedPath(root, hash, suffix ?? "");
    }

    private static string ResolveManagedPath(string root, params string[] parts)
    {
        var resolvedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var resolvedPath = Path.GetFullPath(Path.Combine([resolvedRoot, .. parts.Select(p => p.Replace('/', Path.DirectorySeparatorChar))])).TrimEnd(Path.DirectorySeparatorChar);
        if (resolvedPath != resolvedRoot && !resolvedPath.StartsWith(resolvedRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Refusing to use path outside package install root: {resolvedPath}");
        }
        return resolvedPath;
    }

    private string GetBaseDirForScope(string scope)
    {
        if (scope == "project")
        {
            AssertProjectTrustedForScope(scope);
            return Path.Combine(_cwd, AppConfig.ConfigDirName);
        }
        return scope == "user" ? _agentDir : _cwd;
    }

    private string ResolvePath(string input) => ResolvePathFromBase(input, _cwd);

    private static string ResolvePathFromBase(string input, string baseDir) =>
        PathUtils.ResolvePath(input, baseDir, new PathInputOptions { HomeDir = HomeDir, Trim = true });

    // ----- package resources -----

    private static List<string>? FilterPatterns(JsonObject filter, string type) =>
        filter.TryGetPropertyValue(type, out var node) && node is JsonArray arr
            ? arr.Select(PiJson.GetString).OfType<string>().ToList()
            : null;

    private bool CollectPackageResources(string packageRoot, Accumulator accumulator, JsonObject? filter, PathMetadata metadata)
    {
        if (filter is not null)
        {
            foreach (var type in ResourceTypes)
            {
                var patterns = FilterPatterns(filter, type);
                if (PiJson.GetBool(filter["autoload"]) == false) ApplyPackageDeltaFilter(packageRoot, patterns ?? [], type, accumulator, metadata);
                else if (patterns is not null) ApplyPackageFilter(packageRoot, patterns, type, accumulator, metadata);
                else CollectDefaultResources(packageRoot, type, accumulator, metadata);
            }
            return true;
        }

        if (ReadPiManifest(Path.Combine(packageRoot, "package.json")) is { } manifest)
        {
            foreach (var type in ResourceTypes) AddManifestEntries(manifest.GetValueOrDefault(type), packageRoot, type, accumulator, metadata);
            return true;
        }

        var hasAnyDir = false;
        foreach (var type in ResourceTypes)
        {
            var dir = Path.Combine(packageRoot, type);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in CollectResourceFiles(dir, type)) accumulator.Add(type, f, metadata, true);
            hasAnyDir = true;
        }
        return hasAnyDir;
    }

    private void CollectDefaultResources(string packageRoot, string type, Accumulator accumulator, PathMetadata metadata)
    {
        if (ReadPiManifest(Path.Combine(packageRoot, "package.json"))?.GetValueOrDefault(type) is { } entries)
        {
            AddManifestEntries(entries, packageRoot, type, accumulator, metadata);
            return;
        }
        var dir = Path.Combine(packageRoot, type);
        if (!Directory.Exists(dir)) return;
        foreach (var f in CollectResourceFiles(dir, type)) accumulator.Add(type, f, metadata, true);
    }

    private void ApplyPackageFilter(string packageRoot, List<string> userPatterns, string type, Accumulator accumulator, PathMetadata metadata)
    {
        var allFiles = CollectManifestFiles(packageRoot, type);
        if (userPatterns.Count == 0)
        {
            // An empty array explicitly disables all resources of this type.
            foreach (var f in allFiles) accumulator.Add(type, f, metadata, false);
            return;
        }
        var enabledByUser = ApplyPatterns(allFiles, userPatterns, packageRoot);
        foreach (var f in allFiles) accumulator.Add(type, f, metadata, enabledByUser.Contains(f));
    }

    private void ApplyPackageDeltaFilter(string packageRoot, List<string> userPatterns, string type, Accumulator accumulator, PathMetadata metadata)
    {
        if (userPatterns.Count == 0) return;
        var allFiles = CollectManifestFiles(packageRoot, type);
        foreach (var (filePath, enabled) in ApplyAutoloadDisabledPatterns(allFiles, userPatterns, packageRoot)) accumulator.Add(type, filePath, metadata, enabled);
    }

    private List<string> CollectManifestFiles(string packageRoot, string type)
    {
        if (ReadPiManifest(Path.Combine(packageRoot, "package.json"))?.GetValueOrDefault(type) is { Count: > 0 } entries)
        {
            var allFiles = CollectFilesFromManifestEntries(entries, packageRoot, type);
            var manifestPatterns = entries.Where(IsOverride).ToList();
            return manifestPatterns.Count > 0 ? allFiles.Where(ApplyPatterns(allFiles, manifestPatterns, packageRoot).Contains).ToList() : allFiles;
        }
        var conventionDir = Path.Combine(packageRoot, type);
        return Directory.Exists(conventionDir) ? CollectResourceFiles(conventionDir, type) : [];
    }

    private void AddManifestEntries(List<string>? entries, string root, string type, Accumulator accumulator, PathMetadata metadata)
    {
        if (entries is null) return;
        var allFiles = CollectFilesFromManifestEntries(entries, root, type);
        var enabledPaths = ApplyPatterns(allFiles, entries.Where(IsOverride).ToList(), root);
        foreach (var f in allFiles)
        {
            if (enabledPaths.Contains(f)) accumulator.Add(type, f, metadata, true);
        }
    }

    private static List<string> CollectFilesFromManifestEntries(List<string> entries, string root, string type)
    {
        var resolved = entries.Where(e => !IsOverride(e)).SelectMany(entry => HasGlobPattern(entry) ? ExpandPackageGlob(entry, root) : [Path.GetFullPath(Path.Combine(root, entry))]);
        return CollectFilesFromPaths(resolved, type);
    }

    /// <summary>Glob entries discover visible paths (no dot segments); exact entries can target dot paths or symlinked trees.</summary>
    private static List<string> ExpandPackageGlob(string pattern, string root)
    {
        var normalizedPattern = ToPosix(pattern.StartsWith("./", StringComparison.Ordinal) ? pattern[2..] : pattern);
        var matches = new List<string>();
        void Walk(string dir)
        {
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(dir);
            }
            catch
            {
                return;
            }
            foreach (var entry in entries)
            {
                var name = Path.GetFileName(entry);
                if (name.StartsWith('.')) continue;
                var rel = ToPosix(Path.GetRelativePath(root, entry));
                if (Glob.IsMatch(rel, normalizedPattern)) matches.Add(Path.GetFullPath(entry));
                if (Directory.Exists(entry)) Walk(entry);
            }
        }
        Walk(root);
        matches.Sort(StringComparer.Ordinal);
        return matches;
    }

    public static Dictionary<string, List<string>>? ReadPiManifest(string packageJsonPath)
    {
        try
        {
            if (!File.Exists(packageJsonPath)) return null;
            if (JsonNode.Parse(TextHelpers.StripBom(File.ReadAllText(packageJsonPath))) is not JsonObject pkg || pkg["pi"] is not JsonObject pi) return null;
            var manifest = new Dictionary<string, List<string>>();
            foreach (var field in ResourceTypes)
            {
                if (pi[field] is JsonArray arr && arr.All(e => e is JsonValue v && v.GetValueKind() == JsonValueKind.String))
                {
                    manifest[field] = arr.Select(e => e!.GetValue<string>()).ToList();
                }
            }
            return manifest;
        }
        catch
        {
            return null;
        }
    }

    private static void ResolveLocalEntries(List<string> entries, string type, Accumulator accumulator, PathMetadata metadata, string baseDir)
    {
        if (entries.Count == 0) return;
        var plain = entries.Where(e => !IsPattern(e)).Select(p => ResolvePathFromBase(p, baseDir));
        var patterns = entries.Where(IsPattern).ToList();
        var allFiles = CollectFilesFromPaths(plain, type);
        var enabled = ApplyPatterns(allFiles, patterns, baseDir);
        foreach (var f in allFiles) accumulator.Add(type, f, metadata, enabled.Contains(f));
    }

    private void AddAutoDiscoveredResources(Accumulator accumulator, JsonObject globalSettings, JsonObject projectSettings, string globalBaseDir, string projectBaseDir)
    {
        var userMetadata = new PathMetadata("auto", "user", "top-level", globalBaseDir);
        var projectMetadata = new PathMetadata("auto", "project", "top-level", projectBaseDir);

        var userAgentsSkillsDir = Path.Combine(HomeDir, ".agents", "skills");
        var projectTrusted = settingsManager.IsProjectTrusted;
        var projectAgentsSkillDirs = projectTrusted
            ? CollectAncestorAgentsSkillDirs(_cwd).Where(d => Path.GetFullPath(d) != Path.GetFullPath(userAgentsSkillsDir)).ToList()
            : [];

        void AddResources(string type, List<string> paths, PathMetadata metadata, List<string> overrides, string baseDir)
        {
            foreach (var path in paths) accumulator.Add(type, path, metadata, IsEnabledByOverrides(path, overrides, baseDir));
        }

        if (projectTrusted)
        {
            AddResources("extensions", CollectAutoExtensionEntries(Path.Combine(projectBaseDir, "extensions")), projectMetadata, StringEntries(projectSettings, "extensions"), projectBaseDir);
            AddResources("skills", CollectSkillEntries(Path.Combine(projectBaseDir, "skills"), "pi"), projectMetadata, StringEntries(projectSettings, "skills"), projectBaseDir);
        }

        foreach (var agentsSkillsDir in projectAgentsSkillDirs)
        {
            var agentsBaseDir = Path.GetDirectoryName(agentsSkillsDir)!;
            AddResources("skills", CollectSkillEntries(agentsSkillsDir, "agents"), projectMetadata with { BaseDir = agentsBaseDir }, StringEntries(projectSettings, "skills"), agentsBaseDir);
        }

        if (projectTrusted)
        {
            AddResources("prompts", CollectFlatEntries(Path.Combine(projectBaseDir, "prompts"), ".md"), projectMetadata, StringEntries(projectSettings, "prompts"), projectBaseDir);
            AddResources("themes", CollectFlatEntries(Path.Combine(projectBaseDir, "themes"), ".json"), projectMetadata, StringEntries(projectSettings, "themes"), projectBaseDir);
        }

        AddResources("extensions", CollectAutoExtensionEntries(Path.Combine(globalBaseDir, "extensions")), userMetadata, StringEntries(globalSettings, "extensions"), globalBaseDir);
        AddResources("skills", CollectSkillEntries(Path.Combine(globalBaseDir, "skills"), "pi"), userMetadata, StringEntries(globalSettings, "skills"), globalBaseDir);

        var userAgentsBaseDir = Path.GetDirectoryName(userAgentsSkillsDir)!;
        AddResources("skills", CollectSkillEntries(userAgentsSkillsDir, "agents"), userMetadata with { BaseDir = userAgentsBaseDir }, StringEntries(globalSettings, "skills"), userAgentsBaseDir);

        AddResources("prompts", CollectFlatEntries(Path.Combine(globalBaseDir, "prompts"), ".md"), userMetadata, StringEntries(globalSettings, "prompts"), globalBaseDir);
        AddResources("themes", CollectFlatEntries(Path.Combine(globalBaseDir, "themes"), ".json"), userMetadata, StringEntries(globalSettings, "themes"), globalBaseDir);
    }

    private static int PrecedenceRank(PathMetadata m)
    {
        if (m.Origin == "package") return 4;
        var scopeBase = m.Scope == "project" ? 0 : 2;
        return scopeBase + (m.Source == "local" ? 0 : 1);
    }

    private static ResolvedPaths ToResolvedPaths(Accumulator accumulator)
    {
        List<ResolvedResource> Map(string type)
        {
            var target = accumulator.Target(type);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            return accumulator.Order[type]
                .Select(path => new ResolvedResource(path, target[path].Enabled, target[path].Metadata))
                .OrderBy(r => PrecedenceRank(r.Metadata)) // stable, like Array.prototype.sort
                .Where(r => seen.Add(PathUtils.CanonicalizePath(r.Path)))
                .ToList();
        }
        return new ResolvedPaths(Map("extensions"), Map("skills"), Map("prompts"), Map("themes"));
    }

    // ---- discovery helpers ----

    private static List<string> CollectFilesFromPaths(IEnumerable<string> paths, string type)
    {
        var files = new List<string>();
        foreach (var p in paths)
        {
            try
            {
                if (File.Exists(p)) files.Add(p);
                else if (Directory.Exists(p)) files.AddRange(CollectResourceFiles(p, type));
            }
            catch
            {
                // Ignore errors.
            }
        }
        return files;
    }

    private static List<string> CollectResourceFiles(string dir, string type) => type switch
    {
        "skills" => CollectSkillEntries(dir, "pi"),
        "extensions" => CollectAutoExtensionEntries(dir),
        "themes" => CollectFiles(dir, ".json", null, null),
        _ => CollectFiles(dir, ".md", null, null),
    };

    private static List<string> CollectFiles(string dir, string extension, IgnoreMatcher? ignoreMatcher, string? rootDir)
    {
        var files = new List<string>();
        if (!Directory.Exists(dir)) return files;
        var root = rootDir ?? dir;
        var ig = ignoreMatcher ?? new IgnoreMatcher();
        ig.AddIgnoreFiles(dir, root);
        try
        {
            foreach (var entry in ResourcePaths.Entries(dir))
            {
                if (entry.Name.StartsWith('.') || entry.Name == "node_modules") continue;
                if (ResourcePaths.Stat(entry) is not { } stat) continue;
                var relPath = ToPosix(Path.GetRelativePath(root, entry.FullName));
                if (ig.Ignores(stat.IsDirectory ? relPath + "/" : relPath)) continue;
                if (stat.IsDirectory) files.AddRange(CollectFiles(entry.FullName, extension, ig, root));
                else if (stat.IsFile && entry.Name.EndsWith(extension, StringComparison.Ordinal)) files.Add(entry.FullName);
            }
        }
        catch
        {
            // Ignore errors.
        }
        return files;
    }

    /// <summary>mode "pi": root-level .md files count as skills; mode "agents": only nested .md files do.</summary>
    private static List<string> CollectSkillEntries(string dir, string mode, IgnoreMatcher? ignoreMatcher = null, string? rootDir = null)
    {
        var entries = new List<string>();
        if (!Directory.Exists(dir)) return entries;
        var root = rootDir ?? dir;
        var ig = ignoreMatcher ?? new IgnoreMatcher();
        ig.AddIgnoreFiles(dir, root);
        try
        {
            var dirEntries = ResourcePaths.Entries(dir).ToList();
            foreach (var entry in dirEntries)
            {
                if (entry.Name != "SKILL.md") continue;
                if (ResourcePaths.Stat(entry) is not { } stat) continue;
                var relPath = ToPosix(Path.GetRelativePath(root, entry.FullName));
                if (stat.IsFile && !ig.Ignores(relPath))
                {
                    entries.Add(entry.FullName);
                    return entries;
                }
            }

            foreach (var entry in dirEntries)
            {
                if (entry.Name.StartsWith('.') || entry.Name == "node_modules") continue;
                if (ResourcePaths.Stat(entry) is not { } stat) continue;
                var relPath = ToPosix(Path.GetRelativePath(root, entry.FullName));
                var includeMarkdown = stat.IsFile && entry.Name.EndsWith(".md", StringComparison.Ordinal) && !ig.Ignores(relPath)
                    && ((mode == "pi" && dir == root) || (mode == "agents" && dir != root));
                if (includeMarkdown)
                {
                    entries.Add(entry.FullName);
                    continue;
                }
                if (!stat.IsDirectory || ig.Ignores(relPath + "/")) continue;
                entries.AddRange(CollectSkillEntries(entry.FullName, mode, ig, root));
            }
        }
        catch
        {
            // Ignore errors.
        }
        return entries;
    }

    private static List<string> CollectAncestorAgentsSkillDirs(string startDir)
    {
        var dirs = new List<string>();
        var dir = Path.GetFullPath(startDir);
        string? gitRoot = null;
        for (var probe = dir; probe is not null; probe = Path.GetDirectoryName(probe))
        {
            if (Exists(Path.Combine(probe, ".git")))
            {
                gitRoot = probe;
                break;
            }
        }
        while (true)
        {
            dirs.Add(Path.Combine(dir, ".agents", "skills"));
            if (gitRoot is not null && dir == gitRoot) break;
            var parent = Path.GetDirectoryName(dir);
            if (parent is null || parent == dir) break;
            dir = parent;
        }
        return dirs;
    }

    /// <summary>Non-recursive discovery of prompt (.md) or theme (.json) files.</summary>
    private static List<string> CollectFlatEntries(string dir, string extension)
    {
        var entries = new List<string>();
        if (!Directory.Exists(dir)) return entries;
        var ig = new IgnoreMatcher();
        ig.AddIgnoreFiles(dir, dir);
        try
        {
            foreach (var entry in ResourcePaths.Entries(dir))
            {
                if (entry.Name.StartsWith('.') || entry.Name == "node_modules") continue;
                if (ResourcePaths.Stat(entry) is not { } stat) continue;
                if (ig.Ignores(ToPosix(Path.GetRelativePath(dir, entry.FullName)))) continue;
                if (stat.IsFile && entry.Name.EndsWith(extension, StringComparison.Ordinal)) entries.Add(entry.FullName);
            }
        }
        catch
        {
            // Ignore errors.
        }
        return entries;
    }

    private static List<string>? ResolveExtensionEntries(string dir)
    {
        if (ReadPiManifest(Path.Combine(dir, "package.json"))?.GetValueOrDefault("extensions") is { Count: > 0 } extensions)
        {
            var entries = extensions.Select(e => Path.GetFullPath(Path.Combine(dir, e))).Where(Exists).ToList();
            if (entries.Count > 0) return entries;
        }
        var indexTs = Path.Combine(dir, "index.ts");
        if (File.Exists(indexTs)) return [indexTs];
        var indexJs = Path.Combine(dir, "index.js");
        if (File.Exists(indexJs)) return [indexJs];
        return null;
    }

    private static List<string> CollectAutoExtensionEntries(string dir)
    {
        var entries = new List<string>();
        if (!Directory.Exists(dir)) return entries;
        if (ResolveExtensionEntries(dir) is { } rootEntries) return rootEntries;

        var ig = new IgnoreMatcher();
        ig.AddIgnoreFiles(dir, dir);
        try
        {
            foreach (var entry in ResourcePaths.Entries(dir))
            {
                if (entry.Name.StartsWith('.') || entry.Name == "node_modules") continue;
                if (ResourcePaths.Stat(entry) is not { } stat) continue;
                var relPath = ToPosix(Path.GetRelativePath(dir, entry.FullName));
                if (ig.Ignores(stat.IsDirectory ? relPath + "/" : relPath)) continue;
                if (stat.IsFile && (entry.Name.EndsWith(".ts", StringComparison.Ordinal) || entry.Name.EndsWith(".js", StringComparison.Ordinal)))
                {
                    entries.Add(entry.FullName);
                }
                else if (stat.IsDirectory && ResolveExtensionEntries(entry.FullName) is { } resolved)
                {
                    entries.AddRange(resolved);
                }
            }
        }
        catch
        {
            // Ignore errors.
        }
        return entries;
    }

    // ---- pattern filtering ----

    private static bool MatchesAnyPattern(string filePath, IEnumerable<string> patterns, string baseDir)
    {
        var rel = ToPosix(Path.GetRelativePath(baseDir, filePath));
        var name = Path.GetFileName(filePath);
        var filePathPosix = ToPosix(filePath);
        var isSkillFile = name == "SKILL.md";
        var parentDir = isSkillFile ? Path.GetDirectoryName(filePath)! : null;

        return patterns.Any(pattern =>
        {
            var p = ToPosix(pattern);
            if (Glob.IsMatch(rel, p) || Glob.IsMatch(name, p) || Glob.IsMatch(filePathPosix, p)) return true;
            if (!isSkillFile) return false;
            return Glob.IsMatch(ToPosix(Path.GetRelativePath(baseDir, parentDir!)), p)
                || Glob.IsMatch(Path.GetFileName(parentDir!), p)
                || Glob.IsMatch(ToPosix(parentDir!), p);
        });
    }

    private static bool MatchesAnyExactPattern(string filePath, IEnumerable<string> patterns, string baseDir)
    {
        var rel = ToPosix(Path.GetRelativePath(baseDir, filePath));
        var filePathPosix = ToPosix(filePath);
        var isSkillFile = Path.GetFileName(filePath) == "SKILL.md";
        var parentDir = isSkillFile ? Path.GetDirectoryName(filePath)! : null;

        return patterns.Any(pattern =>
        {
            var normalized = ToPosix(pattern.StartsWith("./", StringComparison.Ordinal) || pattern.StartsWith(".\\", StringComparison.Ordinal) ? pattern[2..] : pattern);
            if (normalized == rel || normalized == filePathPosix) return true;
            if (!isSkillFile) return false;
            return normalized == ToPosix(Path.GetRelativePath(baseDir, parentDir!)) || normalized == ToPosix(parentDir!);
        });
    }

    private static bool IsEnabledByOverrides(string filePath, List<string> patterns, string baseDir)
    {
        var excludes = patterns.Where(p => p.StartsWith('!')).Select(p => p[1..]).ToList();
        var forceIncludes = patterns.Where(p => p.StartsWith('+')).Select(p => p[1..]).ToList();
        var forceExcludes = patterns.Where(p => p.StartsWith('-')).Select(p => p[1..]).ToList();

        var enabled = true;
        if (excludes.Count > 0 && MatchesAnyPattern(filePath, excludes, baseDir)) enabled = false;
        if (forceIncludes.Count > 0 && MatchesAnyExactPattern(filePath, forceIncludes, baseDir)) enabled = true;
        if (forceExcludes.Count > 0 && MatchesAnyExactPattern(filePath, forceExcludes, baseDir)) enabled = false;
        return enabled;
    }

    /// <summary>Plain patterns include, "!" excludes, "+path" force-includes, "-path" force-excludes.</summary>
    private static HashSet<string> ApplyPatterns(List<string> allPaths, List<string> patterns, string baseDir)
    {
        var includes = patterns.Where(p => !p.StartsWith('+') && !p.StartsWith('-') && !p.StartsWith('!')).ToList();
        var excludes = patterns.Where(p => p.StartsWith('!')).Select(p => p[1..]).ToList();
        var forceIncludes = patterns.Where(p => p.StartsWith('+')).Select(p => p[1..]).ToList();
        var forceExcludes = patterns.Where(p => p.StartsWith('-')).Select(p => p[1..]).ToList();

        var result = includes.Count == 0 ? [.. allPaths] : allPaths.Where(f => MatchesAnyPattern(f, includes, baseDir)).ToList();
        if (excludes.Count > 0) result = result.Where(f => !MatchesAnyPattern(f, excludes, baseDir)).ToList();
        if (forceIncludes.Count > 0)
        {
            foreach (var f in allPaths)
            {
                if (!result.Contains(f) && MatchesAnyExactPattern(f, forceIncludes, baseDir)) result.Add(f);
            }
        }
        if (forceExcludes.Count > 0) result = result.Where(f => !MatchesAnyExactPattern(f, forceExcludes, baseDir)).ToList();
        return [.. result];
    }

    /// <summary>autoload=false delta filters: each pattern sets the enabled state of its matches, in order.</summary>
    private static List<(string Path, bool Enabled)> ApplyAutoloadDisabledPatterns(List<string> allPaths, List<string> patterns, string baseDir)
    {
        var result = new Dictionary<string, bool>();
        var order = new List<string>();
        foreach (var pattern in patterns)
        {
            var target = pattern.StartsWith('+') || pattern.StartsWith('-') || pattern.StartsWith('!') ? pattern[1..] : pattern;
            var enabled = !pattern.StartsWith('-') && !pattern.StartsWith('!');
            var exact = pattern.StartsWith('+') || pattern.StartsWith('-');
            foreach (var filePath in allPaths)
            {
                if (!(exact ? MatchesAnyExactPattern(filePath, [target], baseDir) : MatchesAnyPattern(filePath, [target], baseDir))) continue;
                if (!result.ContainsKey(filePath)) order.Add(filePath);
                result[filePath] = enabled;
            }
        }
        return order.Select(p => (p, result[p])).ToList();
    }
}
