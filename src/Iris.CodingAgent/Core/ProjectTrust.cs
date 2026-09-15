using System.Text.Json;
using System.Text.Json.Nodes;
using Iris.CodingAgent.Config;
using Iris.CodingAgent.Utils;

namespace Iris.CodingAgent.Core;

public sealed record ProjectTrustUpdate(string Path, bool? Decision);

public sealed record ProjectTrustOption(string Label, bool Trusted, List<ProjectTrustUpdate> Updates, string? SavedPath = null);

/// <summary>Persistent per-directory trust decisions in agentDir/trust.json.</summary>
public sealed class ProjectTrustStore(string agentDir)
{
    private static readonly string[] TrustRequiringProjectConfigResources = ["settings.json", "extensions", "skills", "prompts", "themes", "SYSTEM.md", "APPEND_SYSTEM.md"];

    private readonly string _trustPath = Path.Combine(PathUtils.ResolvePath(agentDir), "trust.json");

    private static string NormalizeCwd(string cwd) => PathUtils.CanonicalizePath(PathUtils.ResolvePath(cwd));

    private static string HomeDir => Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } home ? home : AppConfig.HomeDir;

    /// <summary>
    /// True when cwd has trust-requiring entries under cwd/.iris or a .agents/skills directory in cwd or an ancestor
    /// (the user's ~/.agents/skills is always trusted).
    /// </summary>
    public static bool HasTrustRequiringProjectResources(string cwd)
    {
        var homeDir = PathUtils.CanonicalizePath(PathUtils.ResolvePath(HomeDir));
        var userAgentsSkillsDir = Path.Combine(homeDir, ".agents", "skills");
        var currentDir = NormalizeCwd(cwd);

        var configDir = Path.Combine(currentDir, AppConfig.ConfigDirName);
        if (TrustRequiringProjectConfigResources.Any(entry => File.Exists(Path.Combine(configDir, entry)) || Directory.Exists(Path.Combine(configDir, entry)))) return true;

        while (true)
        {
            var agentsSkillsDir = Path.Combine(currentDir, ".agents", "skills");
            if (agentsSkillsDir != userAgentsSkillsDir && Directory.Exists(agentsSkillsDir)) return true;
            var parent = Path.GetDirectoryName(currentDir);
            if (parent is null || parent == currentDir) return false;
            currentDir = parent;
        }
    }

    public static string? GetParentPath(string cwd)
    {
        var trustPath = NormalizeCwd(cwd);
        var parent = Path.GetDirectoryName(trustPath);
        return parent is null || parent == trustPath ? null : parent;
    }

    public static List<ProjectTrustOption> GetOptions(string cwd, bool includeSessionOnly = false)
    {
        var trustPath = NormalizeCwd(cwd);
        var options = new List<ProjectTrustOption> { new("Trust", true, [new(trustPath, true)], trustPath) };
        if (GetParentPath(cwd) is { } parentPath)
        {
            options.Add(new($"Trust parent folder ({parentPath})", true, [new(parentPath, true), new(trustPath, null)], parentPath));
        }
        if (includeSessionOnly) options.Add(new("Trust (this session only)", true, []));
        options.Add(new("Do not trust", false, [new(trustPath, false)], trustPath));
        if (includeSessionOnly) options.Add(new("Do not trust (this session only)", false, []));
        return options;
    }

    private Dictionary<string, bool?> ReadTrustFile()
    {
        if (!File.Exists(_trustPath)) return [];
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(TextHelpers.StripBom(File.ReadAllText(_trustPath)));
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"Failed to read trust store {_trustPath}: {ex.Message}");
        }
        if (parsed is not JsonObject obj) throw new InvalidDataException($"Invalid trust store {_trustPath}: expected an object");

        var data = new Dictionary<string, bool?>(StringComparer.Ordinal);
        foreach (var (key, value) in obj)
        {
            data[key] = value?.GetValueKind() switch
            {
                null or JsonValueKind.Null => null,
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new InvalidDataException($"Invalid trust store {_trustPath}: value for {JsonSerializer.Serialize(key)} must be true, false, or null"),
            };
        }
        return data;
    }

    private void WriteTrustFile(Dictionary<string, bool?> data)
    {
        var sorted = new JsonObject();
        foreach (var key in data.Keys.Order(StringComparer.Ordinal)) sorted[key] = data[key];
        Directory.CreateDirectory(Path.GetDirectoryName(_trustPath)!);
        File.WriteAllText(_trustPath, Iris.Ai.Json.IrisJson.SerializeIndentedTwoSpaces(sorted) + "\n");
    }

    private T WithLock<T>(Func<T> fn)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_trustPath)!);
        using var _ = FileLock.AcquireWithRetry(_trustPath);
        return fn();
    }

    /// <summary>The nearest saved decision for cwd or an ancestor, or null when undecided.</summary>
    public bool? Get(string cwd) => WithLock(() =>
    {
        var data = ReadTrustFile();
        var currentDir = NormalizeCwd(cwd);
        while (true)
        {
            if (data.TryGetValue(currentDir, out var value) && value is not null) return value;
            var parent = Path.GetDirectoryName(currentDir);
            if (parent is null || parent == currentDir) return (bool?)null;
            currentDir = parent;
        }
    });

    /// <summary>The nearest saved decision with the path it was saved for, or null when undecided.</summary>
    public (string Path, bool Decision)? GetEntry(string cwd) => WithLock<(string Path, bool Decision)?>(() =>
    {
        var data = ReadTrustFile();
        var currentDir = NormalizeCwd(cwd);
        while (true)
        {
            if (data.TryGetValue(currentDir, out var value) && value is { } decision) return (currentDir, decision);
            var parent = Path.GetDirectoryName(currentDir);
            if (parent is null || parent == currentDir) return null;
            currentDir = parent;
        }
    });

    public void Set(string cwd, bool? decision) => SetMany([new ProjectTrustUpdate(cwd, decision)]);

    public void SetMany(IEnumerable<ProjectTrustUpdate> updates) => WithLock(() =>
    {
        var data = ReadTrustFile();
        foreach (var update in updates)
        {
            var key = NormalizeCwd(update.Path);
            if (update.Decision is null) data.Remove(key);
            else data[key] = update.Decision;
        }
        WriteTrustFile(data);
        return 0;
    });

    /// <summary>
    /// Resolve whether project-local resources may load. Interactive prompting is supplied by
    /// the caller; non-interactive modes pass null and untrusted projects stay untrusted.
    /// </summary>
    public async Task<bool> ResolveProjectTrustedAsync(string cwd, bool? trustOverride, string defaultProjectTrust, Func<string, List<ProjectTrustOption>, Task<ProjectTrustOption?>>? prompt)
    {
        if (trustOverride is { } forced) return forced;
        if (!HasTrustRequiringProjectResources(cwd)) return true;
        if (Get(cwd) is { } decision) return decision;

        switch (defaultProjectTrust)
        {
            case "always":
                return true;
            case "never":
                return false;
        }

        if (prompt is null) return false;
        var options = GetOptions(cwd, includeSessionOnly: true);
        var selected = await prompt($"Trust project folder?\n{cwd}\n\nThis allows {AppConfig.AppName} to load {AppConfig.ConfigDirName} settings and resources, install missing project packages, and execute project extensions.", options);
        if (selected is null) return false;
        if (selected.Updates.Count > 0) SetMany(selected.Updates);
        return selected.Trusted;
    }
}
