using System.Text.Json;
using System.Text.Json.Nodes;
using Iris.CodingAgent.Config;
using Iris.CodingAgent.Utils;

namespace Iris.CodingAgent.Core;

/// <summary>A theme JSON file discovered on disk. Parsing into a renderable theme happens in the TUI layer.</summary>
public sealed record ThemeResource(string? Name, string SourcePath, JsonObject Json, SourceInfo? SourceInfo = null);

public sealed record ResourceExtensionPaths(
    IReadOnlyList<(string Path, PathMetadata Metadata)>? SkillPaths = null,
    IReadOnlyList<(string Path, PathMetadata Metadata)>? PromptPaths = null,
    IReadOnlyList<(string Path, PathMetadata Metadata)>? ThemePaths = null);

public interface IResourceLoader
{
    /// <summary>Enabled extension entry points. Loading them is not supported yet (extension design pending).</summary>
    IReadOnlyList<ResolvedResource> ExtensionEntries { get; }

    (IReadOnlyList<Skill> Skills, IReadOnlyList<ResourceDiagnostic> Diagnostics) GetSkills();

    (IReadOnlyList<PromptTemplate> Prompts, IReadOnlyList<ResourceDiagnostic> Diagnostics) GetPrompts();

    (IReadOnlyList<ThemeResource> Themes, IReadOnlyList<ResourceDiagnostic> Diagnostics) GetThemes();

    IReadOnlyList<ContextFile> GetAgentsFiles();

    string? GetSystemPrompt();

    string? GetSystemPromptSource();

    IReadOnlyList<string> GetAppendSystemPrompt();

    IReadOnlyList<string> GetAppendSystemPromptSources();

    void ExtendResources(ResourceExtensionPaths paths);

    Task ReloadAsync(CancellationToken cancellationToken = default);
}

public sealed class DefaultResourceLoaderOptions
{
    public required string Cwd { get; init; }
    public required string AgentDir { get; init; }
    public SettingsManager? SettingsManager { get; init; }
    public IReadOnlyList<string>? AdditionalExtensionPaths { get; init; }
    public IReadOnlyList<string>? AdditionalSkillPaths { get; init; }
    public IReadOnlyList<string>? AdditionalPromptTemplatePaths { get; init; }
    public IReadOnlyList<string>? AdditionalThemePaths { get; init; }
    public bool NoExtensions { get; init; }
    public bool NoSkills { get; init; }
    public bool NoPromptTemplates { get; init; }
    public bool NoThemes { get; init; }
    public bool NoContextFiles { get; init; }
    public string? SystemPrompt { get; init; }
    public IReadOnlyList<string>? AppendSystemPrompt { get; init; }
    public Func<LoadSkillsResult, LoadSkillsResult>? SkillsOverride { get; init; }
    public Func<(List<PromptTemplate> Prompts, List<ResourceDiagnostic> Diagnostics), (List<PromptTemplate> Prompts, List<ResourceDiagnostic> Diagnostics)>? PromptsOverride { get; init; }
    public Func<List<ContextFile>, List<ContextFile>>? AgentsFilesOverride { get; init; }
    public Func<string?, string?>? SystemPromptOverride { get; init; }
    public Func<List<string>, List<string>>? AppendSystemPromptOverride { get; init; }
}

/// <summary>Port of core/resource-loader.ts (without the extension runtime).</summary>
public sealed class DefaultResourceLoader : IResourceLoader
{
    private static readonly string[] ContextFileCandidates = ["AGENTS.override.md", "AGENTS.md", "AGENTS.MD", "CLAUDE.md", "CLAUDE.MD"];

    private readonly DefaultResourceLoaderOptions _options;
    private readonly string _cwd;
    private readonly string _agentDir;
    private readonly SettingsManager _settingsManager;
    private readonly ResourceResolver _resourceResolver;

    private List<ResolvedResource> _extensionEntries = [];
    private List<Skill> _skills = [];
    private List<ResourceDiagnostic> _skillDiagnostics = [];
    private List<PromptTemplate> _prompts = [];
    private List<ResourceDiagnostic> _promptDiagnostics = [];
    private List<ThemeResource> _themes = [];
    private List<ResourceDiagnostic> _themeDiagnostics = [];
    private List<ContextFile> _agentsFiles = [];
    private string? _systemPrompt;
    private string? _systemPromptSourcePath;
    private List<string> _appendSystemPrompt = [];
    private List<string> _appendSystemPromptSourcePaths = [];
    private List<string> _lastSkillPaths = [];
    private List<string> _lastPromptPaths = [];
    private List<string> _lastThemePaths = [];
    private Dictionary<string, PathMetadata> _metadataByPath = [];
    private readonly Dictionary<string, SourceInfo> _extensionSkillSourceInfos = [];
    private readonly Dictionary<string, SourceInfo> _extensionPromptSourceInfos = [];
    private readonly Dictionary<string, SourceInfo> _extensionThemeSourceInfos = [];

    public DefaultResourceLoader(DefaultResourceLoaderOptions options)
    {
        _options = options;
        _cwd = PathUtils.ResolvePath(options.Cwd);
        _agentDir = PathUtils.ResolvePath(options.AgentDir);
        _settingsManager = options.SettingsManager ?? SettingsManager.Create(_cwd, _agentDir);
        _resourceResolver = new ResourceResolver(_cwd, _agentDir, _settingsManager);
    }

    public IReadOnlyList<ResolvedResource> ExtensionEntries => _extensionEntries;

    public (IReadOnlyList<Skill> Skills, IReadOnlyList<ResourceDiagnostic> Diagnostics) GetSkills() => (_skills, _skillDiagnostics);

    public (IReadOnlyList<PromptTemplate> Prompts, IReadOnlyList<ResourceDiagnostic> Diagnostics) GetPrompts() => (_prompts, _promptDiagnostics);

    public (IReadOnlyList<ThemeResource> Themes, IReadOnlyList<ResourceDiagnostic> Diagnostics) GetThemes() => (_themes, _themeDiagnostics);

    public IReadOnlyList<ContextFile> GetAgentsFiles() => _agentsFiles;

    public string? GetSystemPrompt() => _systemPrompt;

    public string? GetSystemPromptSource() => _systemPromptSourcePath;

    public IReadOnlyList<string> GetAppendSystemPrompt() => _appendSystemPrompt;

    public IReadOnlyList<string> GetAppendSystemPromptSources() => _appendSystemPromptSourcePaths;

    // ---- context files ----

    private static ContextFile? LoadContextFileFromDir(string dir)
    {
        foreach (var fileName in ContextFileCandidates)
        {
            var filePath = Path.Combine(dir, fileName);
            if (!File.Exists(filePath)) continue;
            try
            {
                return new ContextFile(filePath, TextHelpers.StripBom(File.ReadAllText(filePath)));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(Chalk.Yellow($"Warning: Could not read {filePath}: {ex.Message}"));
            }
        }
        return null;
    }

    /// <summary>
    /// The main repo's context file shadowed by a nested linked worktree's own copy (both cover the same repository
    /// scope, so loading both would apply that context twice). Returned canonicalized.
    /// </summary>
    private static string? FindShadowedContextFile(string cwd)
    {
        var gitPaths = GitPaths.Find(cwd);
        if (gitPaths is null) return null;
        var commonGitDir = PathUtils.CanonicalizePath(gitPaths.CommonGitDir);
        var worktreeRoot = PathUtils.CanonicalizePath(gitPaths.RepoDir);
        var mainRepoRoot = Path.GetDirectoryName(commonGitDir);
        if (mainRepoRoot is null || !worktreeRoot.StartsWith(mainRepoRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return null;
        if (PathUtils.CanonicalizePath(Path.Combine(mainRepoRoot, ".git")) != commonGitDir) return null;
        var worktreeContextFile = LoadContextFileFromDir(worktreeRoot);
        return worktreeContextFile is null ? null : Path.Combine(mainRepoRoot, Path.GetFileName(worktreeContextFile.Path));
    }

    /// <summary>Global AGENTS.md from the agent dir, then ancestor context files from the filesystem root down to cwd.</summary>
    public static List<ContextFile> LoadProjectContextFiles(string cwd, string agentDir)
    {
        var resolvedCwd = PathUtils.ResolvePath(cwd);
        var resolvedAgentDir = PathUtils.ResolvePath(agentDir);
        var contextFiles = new List<ContextFile>();
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);

        if (LoadContextFileFromDir(resolvedAgentDir) is { } globalContext)
        {
            contextFiles.Add(globalContext);
            seenPaths.Add(globalContext.Path);
        }

        var ancestors = new List<ContextFile>();
        var shadowed = FindShadowedContextFile(resolvedCwd);
        var currentDir = resolvedCwd;
        while (true)
        {
            var contextFile = LoadContextFileFromDir(currentDir);
            var isShadowed = shadowed is not null && PathUtils.CanonicalizePath(contextFile?.Path ?? "") == shadowed;
            if (contextFile is not null && !isShadowed && seenPaths.Add(contextFile.Path)) ancestors.Insert(0, contextFile);

            var parentDir = Path.GetDirectoryName(currentDir);
            if (parentDir is null || parentDir == currentDir) break;
            currentDir = parentDir;
        }
        contextFiles.AddRange(ancestors);
        return contextFiles;
    }

    // ---- reload ----

    private string ResolveResourcePath(string p) => PathUtils.ResolvePath(p, _cwd, new PathInputOptions { Trim = true });

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private List<string> MergePaths(IEnumerable<string> primary, IEnumerable<string> additional)
    {
        var merged = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in primary.Concat(additional))
        {
            var resolved = ResolveResourcePath(p);
            if (!seen.Add(PathUtils.CanonicalizePath(resolved))) continue;
            merged.Add(resolved);
        }
        return merged;
    }

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        await _settingsManager.ReloadAsync();
        var resolvedPaths = await _resourceResolver.ResolveAsync();
        var cliPaths = await _resourceResolver.ResolveExtensionSourcesAsync(_options.AdditionalExtensionPaths ?? []);

        _metadataByPath = [];
        _extensionSkillSourceInfos.Clear();
        _extensionPromptSourceInfos.Clear();
        _extensionThemeSourceInfos.Clear();
        var metadataByPath = _metadataByPath;

        List<ResolvedResource> Enabled(IEnumerable<ResolvedResource> resources)
        {
            var list = resources.ToList();
            foreach (var r in list) metadataByPath.TryAdd(r.Path, r.Metadata);
            return list.Where(r => r.Enabled).ToList();
        }

        var enabledExtensions = Enabled(resolvedPaths.Extensions);
        var enabledSkills = Enabled(resolvedPaths.Skills).Select(r => MapSkillPath(r, metadataByPath)).ToList();
        var enabledPrompts = Enabled(resolvedPaths.Prompts).Select(r => r.Path).ToList();
        var enabledThemes = Enabled(resolvedPaths.Themes).Select(r => r.Path).ToList();

        foreach (var r in cliPaths.Extensions.Concat(cliPaths.Skills)) metadataByPath.TryAdd(r.Path, new PathMetadata("cli", "temporary", "top-level"));
        var cliExtensions = Enabled(cliPaths.Extensions);
        var cliSkills = Enabled(cliPaths.Skills).Select(r => r.Path).ToList();
        var cliPrompts = Enabled(cliPaths.Prompts).Select(r => r.Path).ToList();
        var cliThemes = Enabled(cliPaths.Themes).Select(r => r.Path).ToList();

        _extensionEntries = _options.NoExtensions ? cliExtensions : [.. cliExtensions, .. enabledExtensions];

        var additionalSkills = _options.AdditionalSkillPaths ?? [];
        var skillPaths = _options.NoSkills ? MergePaths(cliSkills, additionalSkills) : MergePaths([.. cliSkills, .. enabledSkills], additionalSkills);
        _lastSkillPaths = skillPaths;
        UpdateSkillsFromPaths(skillPaths);
        foreach (var p in additionalSkills)
        {
            if (!PathUtils.IsLocalPath(p)) continue;
            var resolved = ResolveResourcePath(p);
            if (!Exists(resolved) && !_skillDiagnostics.Any(d => d.Path == resolved))
            {
                _skillDiagnostics.Add(new ResourceDiagnostic("error", "Skill path does not exist", resolved));
            }
        }

        var additionalPrompts = _options.AdditionalPromptTemplatePaths ?? [];
        var promptPaths = _options.NoPromptTemplates ? MergePaths(cliPrompts, additionalPrompts) : MergePaths([.. cliPrompts, .. enabledPrompts], additionalPrompts);
        _lastPromptPaths = promptPaths;
        UpdatePromptsFromPaths(promptPaths);
        foreach (var p in additionalPrompts)
        {
            if (!PathUtils.IsLocalPath(p)) continue;
            var resolved = ResolveResourcePath(p);
            if (!Exists(resolved) && !_promptDiagnostics.Any(d => d.Path == resolved))
            {
                _promptDiagnostics.Add(new ResourceDiagnostic("error", "Prompt template path does not exist", resolved));
            }
        }

        var additionalThemes = _options.AdditionalThemePaths ?? [];
        var themePaths = _options.NoThemes ? MergePaths(cliThemes, additionalThemes) : MergePaths([.. cliThemes, .. enabledThemes], additionalThemes);
        _lastThemePaths = themePaths;
        UpdateThemesFromPaths(themePaths);
        foreach (var p in additionalThemes)
        {
            var resolved = ResolveResourcePath(p);
            if (!Exists(resolved) && !_themeDiagnostics.Any(d => d.Path == resolved))
            {
                _themeDiagnostics.Add(new ResourceDiagnostic("error", "Theme path does not exist", resolved));
            }
        }

        var agentsFiles = _options.NoContextFiles ? [] : LoadProjectContextFiles(_cwd, _agentDir);
        _agentsFiles = _options.AgentsFilesOverride?.Invoke(agentsFiles) ?? agentsFiles;

        var systemPromptSource = _options.SystemPrompt ?? DiscoverFile("SYSTEM.md");
        var baseSystemPrompt = ResolvePromptInput(systemPromptSource, "system prompt");
        _systemPrompt = _options.SystemPromptOverride is null ? baseSystemPrompt : _options.SystemPromptOverride(baseSystemPrompt);
        _systemPromptSourcePath = systemPromptSource is not null && Exists(systemPromptSource) ? PathUtils.ResolvePath(systemPromptSource) : null;

        var appendSources = _options.AppendSystemPrompt?.ToList() ?? (DiscoverFile("APPEND_SYSTEM.md") is { } discovered ? [discovered] : []);
        var baseAppend = appendSources.Select(s => ResolvePromptInput(s, "append system prompt")).OfType<string>().ToList();
        _appendSystemPrompt = _options.AppendSystemPromptOverride?.Invoke(baseAppend) ?? baseAppend;
        _appendSystemPromptSourcePaths = appendSources.Where(Exists).Select(s => PathUtils.ResolvePath(s)).ToList();
    }

    private static string? ResolvePromptInput(string? input, string description)
    {
        if (string.IsNullOrEmpty(input)) return null;
        if (File.Exists(input))
        {
            try
            {
                return TextHelpers.StripBom(File.ReadAllText(input));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(Chalk.Yellow($"Warning: Could not read {description} file {input}: {ex.Message}"));
                return input;
            }
        }
        return input;
    }

    private string? DiscoverFile(string fileName)
    {
        var projectPath = Path.Combine(_cwd, AppConfig.ConfigDirName, fileName);
        if (_settingsManager.IsProjectTrusted && File.Exists(projectPath)) return projectPath;
        var globalPath = Path.Combine(_agentDir, fileName);
        return File.Exists(globalPath) ? globalPath : null;
    }

    private static string MapSkillPath(ResolvedResource resource, Dictionary<string, PathMetadata> metadataByPath)
    {
        if (resource.Metadata.Source != "auto" && resource.Metadata.Origin != "package") return resource.Path;
        if (!Directory.Exists(resource.Path)) return resource.Path;
        var skillFile = Path.Combine(resource.Path, "SKILL.md");
        if (!File.Exists(skillFile)) return resource.Path;
        metadataByPath.TryAdd(skillFile, resource.Metadata);
        return skillFile;
    }

    public void ExtendResources(ResourceExtensionPaths paths)
    {
        List<(string Path, PathMetadata Metadata)> Normalize(IReadOnlyList<(string Path, PathMetadata Metadata)>? entries) =>
            (entries ?? []).Select(e => (ResolveResourcePath(e.Path),
                e.Metadata.BaseDir is null ? e.Metadata : e.Metadata with { BaseDir = ResolveResourcePath(e.Metadata.BaseDir) })).ToList();

        var skillPaths = Normalize(paths.SkillPaths);
        var promptPaths = Normalize(paths.PromptPaths);
        var themePaths = Normalize(paths.ThemePaths);
        foreach (var e in skillPaths) _extensionSkillSourceInfos[e.Path] = SourceInfo.FromMetadata(e.Path, e.Metadata);
        foreach (var e in promptPaths) _extensionPromptSourceInfos[e.Path] = SourceInfo.FromMetadata(e.Path, e.Metadata);
        foreach (var e in themePaths) _extensionThemeSourceInfos[e.Path] = SourceInfo.FromMetadata(e.Path, e.Metadata);

        if (skillPaths.Count > 0)
        {
            _lastSkillPaths = MergePaths(_lastSkillPaths, skillPaths.Select(e => e.Path));
            UpdateSkillsFromPaths(_lastSkillPaths);
        }
        if (promptPaths.Count > 0)
        {
            _lastPromptPaths = MergePaths(_lastPromptPaths, promptPaths.Select(e => e.Path));
            UpdatePromptsFromPaths(_lastPromptPaths);
        }
        if (themePaths.Count > 0)
        {
            _lastThemePaths = MergePaths(_lastThemePaths, themePaths.Select(e => e.Path));
            UpdateThemesFromPaths(_lastThemePaths);
        }
    }

    private void UpdateSkillsFromPaths(List<string> skillPaths)
    {
        var result = _options.NoSkills && skillPaths.Count == 0
            ? new LoadSkillsResult([], [])
            : Skills.LoadSkills(_cwd, _agentDir, skillPaths, includeDefaults: false);
        if (_options.SkillsOverride is not null) result = _options.SkillsOverride(result);
        _skills = result.Skills
            .Select(s => s with { SourceInfo = FindSourceInfoForPath(s.FilePath, _extensionSkillSourceInfos) ?? s.SourceInfo })
            .ToList();
        _skillDiagnostics = result.Diagnostics;
    }

    private void UpdatePromptsFromPaths(List<string> promptPaths)
    {
        (List<PromptTemplate> Prompts, List<ResourceDiagnostic> Diagnostics) result;
        if (_options.NoPromptTemplates && promptPaths.Count == 0)
        {
            result = ([], []);
        }
        else
        {
            var all = PromptTemplates.LoadPromptTemplates(_cwd, _agentDir, promptPaths, includeDefaults: false);
            var seen = new Dictionary<string, PromptTemplate>(StringComparer.Ordinal);
            var prompts = new List<PromptTemplate>();
            var diagnostics = new List<ResourceDiagnostic>();
            foreach (var prompt in all)
            {
                if (seen.TryGetValue(prompt.Name, out var existing))
                {
                    diagnostics.Add(new ResourceDiagnostic("collision", $"name \"/{prompt.Name}\" collision", prompt.FilePath,
                        new ResourceCollision("prompt", prompt.Name, existing.FilePath, prompt.FilePath)));
                }
                else
                {
                    seen[prompt.Name] = prompt;
                    prompts.Add(prompt);
                }
            }
            result = (prompts, diagnostics);
        }
        if (_options.PromptsOverride is not null) result = _options.PromptsOverride(result);
        _prompts = result.Prompts
            .Select(p => p with { SourceInfo = FindSourceInfoForPath(p.FilePath, _extensionPromptSourceInfos) ?? p.SourceInfo })
            .ToList();
        _promptDiagnostics = result.Diagnostics;
    }

    private void UpdateThemesFromPaths(List<string> themePaths)
    {
        var themes = new List<ThemeResource>();
        var diagnostics = new List<ResourceDiagnostic>();
        if (!(_options.NoThemes && themePaths.Count == 0))
        {
            foreach (var p in themePaths)
            {
                var resolved = ResolveResourcePath(p);
                if (!Exists(resolved))
                {
                    diagnostics.Add(new ResourceDiagnostic("warning", "theme path does not exist", resolved));
                    continue;
                }
                if (Directory.Exists(resolved))
                {
                    try
                    {
                        foreach (var entry in ResourcePaths.Entries(resolved))
                        {
                            if (ResourcePaths.Stat(entry) is { IsFile: true } && entry.Name.EndsWith(".json", StringComparison.Ordinal))
                            {
                                LoadThemeFromFile(entry.FullName, themes, diagnostics);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        diagnostics.Add(new ResourceDiagnostic("warning", ex.Message, resolved));
                    }
                }
                else if (resolved.EndsWith(".json", StringComparison.Ordinal))
                {
                    LoadThemeFromFile(resolved, themes, diagnostics);
                }
                else
                {
                    diagnostics.Add(new ResourceDiagnostic("warning", "theme path is not a json file", resolved));
                }
            }
        }

        var seen = new Dictionary<string, ThemeResource>(StringComparer.Ordinal);
        var deduped = new List<ThemeResource>();
        foreach (var theme in themes)
        {
            var name = theme.Name ?? "unnamed";
            if (seen.TryGetValue(name, out var existing))
            {
                diagnostics.Add(new ResourceDiagnostic("collision", $"name \"{name}\" collision", theme.SourcePath,
                    new ResourceCollision("theme", name, existing.SourcePath, theme.SourcePath)));
                continue;
            }
            seen[name] = theme;
            deduped.Add(theme with { SourceInfo = FindSourceInfoForPath(theme.SourcePath, _extensionThemeSourceInfos) ?? DefaultSourceInfoForPath(theme.SourcePath) });
        }
        _themes = deduped;
        _themeDiagnostics = diagnostics;
    }

    private static void LoadThemeFromFile(string filePath, List<ThemeResource> themes, List<ResourceDiagnostic> diagnostics)
    {
        try
        {
            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(TextHelpers.StripBom(File.ReadAllText(filePath)));
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"Failed to parse theme {filePath}: {ex.Message}");
            }
            var json = ThemeJsonValidator.Validate(filePath, parsed);
            var name = json["name"] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;
            themes.Add(new ThemeResource(name, filePath, json));
        }
        catch (Exception ex)
        {
            diagnostics.Add(new ResourceDiagnostic("warning", ex.Message, filePath));
        }
    }

    private SourceInfo? FindSourceInfoForPath(string resourcePath, Dictionary<string, SourceInfo>? extraSourceInfos)
    {
        if (string.IsNullOrEmpty(resourcePath)) return null;
        if (resourcePath.StartsWith('<')) return DefaultSourceInfoForPath(resourcePath);

        var normalized = Path.GetFullPath(resourcePath);
        static bool Under(string path, string root) => path == root || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);

        if (extraSourceInfos is not null)
        {
            foreach (var (sourcePath, info) in extraSourceInfos)
            {
                if (Under(normalized, Path.GetFullPath(sourcePath))) return info with { Path = resourcePath };
            }
        }

        if (_metadataByPath.TryGetValue(normalized, out var exact) || _metadataByPath.TryGetValue(resourcePath, out exact))
        {
            return SourceInfo.FromMetadata(resourcePath, exact);
        }
        foreach (var (sourcePath, metadata) in _metadataByPath)
        {
            if (Under(normalized, Path.GetFullPath(sourcePath))) return SourceInfo.FromMetadata(resourcePath, metadata);
        }
        return null;
    }

    private SourceInfo DefaultSourceInfoForPath(string filePath)
    {
        if (filePath.StartsWith('<') && filePath.EndsWith('>'))
        {
            var source = filePath[1..^1].Split(':')[0];
            return new SourceInfo(filePath, source.Length > 0 ? source : "temporary", "temporary", "top-level");
        }

        var normalized = Path.GetFullPath(filePath);
        foreach (var kind in new[] { "skills", "prompts", "themes", "extensions" })
        {
            var root = Path.Combine(_agentDir, kind);
            if (ResourcePaths.IsUnderPath(normalized, root)) return new SourceInfo(filePath, "local", "user", "top-level", root);
        }
        foreach (var kind in new[] { "skills", "prompts", "themes", "extensions" })
        {
            var root = Path.Combine(_cwd, AppConfig.ConfigDirName, kind);
            if (ResourcePaths.IsUnderPath(normalized, root)) return new SourceInfo(filePath, "local", "project", "top-level", root);
        }
        return new SourceInfo(filePath, "local", "temporary", "top-level", Directory.Exists(normalized) ? normalized : Path.GetDirectoryName(normalized));
    }
}
