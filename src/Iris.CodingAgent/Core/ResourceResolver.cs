using System.Text.Json;
using System.Text.Json.Nodes;
using Iris.CodingAgent.Config;
using Iris.CodingAgent.Utils;

namespace Iris.CodingAgent.Core;

public sealed record ResolvedResource(string Path, bool Enabled, PathMetadata Metadata);

public sealed record ResolvedPaths(
    List<ResolvedResource> Extensions,
    List<ResolvedResource> Skills,
    List<ResolvedResource> Prompts,
    List<ResolvedResource> Themes);

/// <summary>
/// Resolves extensions, skills, prompts and themes from settings entries (with include/exclude patterns) and the
/// auto-discovered user and project resource directories.
/// </summary>
public sealed class ResourceResolver(string cwd, string agentDir, SettingsManager settingsManager)
{
    private static readonly string[] ResourceTypes = ["extensions", "skills", "prompts", "themes"];

    private readonly string _cwd = PathUtils.ResolvePath(cwd);
    private readonly string _agentDir = PathUtils.ResolvePath(agentDir);

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

    private static string ToPosix(string path) => IgnoreMatcher.ToPosixPath(path);

    private static bool IsPattern(string s) => s.StartsWith('!') || s.StartsWith('+') || s.StartsWith('-') || s.Contains('*') || s.Contains('?');

    private static List<string> StringEntries(JsonObject settings, string key) =>
        settings[key] is JsonArray arr
            ? arr.OfType<JsonValue>().Where(v => v.GetValueKind() == JsonValueKind.String).Select(v => v.GetValue<string>()).ToList()
            : [];
    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    public Task<ResolvedPaths> ResolveAsync()
    {
        var accumulator = new Accumulator();
        var globalSettings = settingsManager.GetGlobalSettings();
        var projectSettings = settingsManager.GetProjectSettings();
        var globalBaseDir = _agentDir;
        var projectBaseDir = Path.Combine(_cwd, AppConfig.ConfigDirName);
        foreach (var type in ResourceTypes)
        {
            ResolveLocalEntries(StringEntries(projectSettings, type), type, accumulator, new PathMetadata("local", "project", "top-level"), projectBaseDir);
            ResolveLocalEntries(StringEntries(globalSettings, type), type, accumulator, new PathMetadata("local", "user", "top-level"), globalBaseDir);
        }

        AddAutoDiscoveredResources(accumulator, globalSettings, projectSettings, globalBaseDir, projectBaseDir);
        return Task.FromResult(ToResolvedPaths(accumulator));
    }

    /// <summary>Resolve extension paths passed on the command line (files, or directories of resources).</summary>
    public Task<ResolvedPaths> ResolveExtensionSourcesAsync(IEnumerable<string> sources)
    {
        var accumulator = new Accumulator();
        var metadata = new PathMetadata("cli", "temporary", "top-level");
        foreach (var source in sources) ResolveLocalExtensionSource(source, accumulator, metadata, _cwd);
        return Task.FromResult(ToResolvedPaths(accumulator));
    }

    private static void ResolveLocalExtensionSource(string source, Accumulator accumulator, PathMetadata metadata, string baseDir)
    {
        var resolved = ResolvePathFromBase(source, baseDir);
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
                if (!CollectResourceDirectories(resolved, accumulator, withBase)) accumulator.Add("extensions", resolved, withBase, true);
            }
        }
        catch
        {
            // Ignore unreadable local sources.
        }
    }

    /// <summary>Collect resources from extensions/, skills/, prompts/ and themes/ subdirectories; false when there are none.</summary>
    private static bool CollectResourceDirectories(string root, Accumulator accumulator, PathMetadata metadata)
    {
        var hasAnyDir = false;
        foreach (var type in ResourceTypes)
        {
            var dir = Path.Combine(root, type);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in CollectResourceFiles(dir, type)) accumulator.Add(type, f, metadata, true);
            hasAnyDir = true;
        }
        return hasAnyDir;
    }

    private static string ResolvePathFromBase(string input, string baseDir) =>
        PathUtils.ResolvePath(input, baseDir, new PathInputOptions { HomeDir = HomeDir, Trim = true });



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
            AddResources("skills", CollectSkillEntries(Path.Combine(projectBaseDir, "skills"), "root"), projectMetadata, StringEntries(projectSettings, "skills"), projectBaseDir);
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
        AddResources("skills", CollectSkillEntries(Path.Combine(globalBaseDir, "skills"), "root"), userMetadata, StringEntries(globalSettings, "skills"), globalBaseDir);

        var userAgentsBaseDir = Path.GetDirectoryName(userAgentsSkillsDir)!;
        AddResources("skills", CollectSkillEntries(userAgentsSkillsDir, "agents"), userMetadata with { BaseDir = userAgentsBaseDir }, StringEntries(globalSettings, "skills"), userAgentsBaseDir);

        AddResources("prompts", CollectFlatEntries(Path.Combine(globalBaseDir, "prompts"), ".md"), userMetadata, StringEntries(globalSettings, "prompts"), globalBaseDir);
        AddResources("themes", CollectFlatEntries(Path.Combine(globalBaseDir, "themes"), ".json"), userMetadata, StringEntries(globalSettings, "themes"), globalBaseDir);
    }

    private static int PrecedenceRank(PathMetadata m)
    {
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
        "skills" => CollectSkillEntries(dir, "root"),
        "extensions" => ResolveExtensionEntries(dir) ?? CollectAutoExtensionEntries(dir),
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

    /// <summary>mode "root": root-level .md files count as skills; mode "agents": only nested .md files do.</summary>
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
                    && ((mode == "root" && dir == root) || (mode == "agents" && dir != root));
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

    /// <summary>A folder is one extension when it has a DLL named after it or contains C# sources.</summary>
    private static List<string>? ResolveExtensionEntries(string dir)
    {
        try
        {
            if (File.Exists(Path.Combine(dir, Path.GetFileName(dir) + ".dll"))) return [dir];
            return Directory.EnumerateFiles(dir, "*.cs", SearchOption.TopDirectoryOnly).Any() ? [dir] : null;
        }
        catch
        {
            return null;
        }
    }

    private static List<string> CollectAutoExtensionEntries(string dir)
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
                var relPath = ToPosix(Path.GetRelativePath(dir, entry.FullName));
                if (ig.Ignores(stat.IsDirectory ? relPath + "/" : relPath)) continue;
                if (stat.IsFile && (entry.Name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
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

}
