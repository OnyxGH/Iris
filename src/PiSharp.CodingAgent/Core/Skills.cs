using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.CodingAgent.Config;
using PiSharp.CodingAgent.Utils;

namespace PiSharp.CodingAgent.Core;

public sealed record Skill(string Name, string Description, string FilePath, string BaseDir, SourceInfo SourceInfo, bool DisableModelInvocation);

public sealed record LoadSkillsResult(List<Skill> Skills, List<ResourceDiagnostic> Diagnostics);

/// <summary>Agent Skills discovery and prompt formatting. Port of core/skills.ts.</summary>
public static partial class Skills
{
    private const int MaxNameLength = 64;
    private const int MaxDescriptionLength = 1024;

    [GeneratedRegex("^[a-z0-9-]+$")]
    private static partial Regex ValidName();

    private static List<string> ValidateName(string name)
    {
        var errors = new List<string>();
        if (name.Length > MaxNameLength) errors.Add($"name exceeds {MaxNameLength} characters ({name.Length})");
        if (!ValidName().IsMatch(name)) errors.Add("name contains invalid characters (must be lowercase a-z, 0-9, hyphens only)");
        if (name.StartsWith('-') || name.EndsWith('-')) errors.Add("name must not start or end with a hyphen");
        if (name.Contains("--")) errors.Add("name must not contain consecutive hyphens");
        return errors;
    }

    private static List<string> ValidateDescription(string? description)
    {
        var errors = new List<string>();
        if (description is null || description.Trim().Length == 0) errors.Add("description is required");
        else if (description.Length > MaxDescriptionLength) errors.Add($"description exceeds {MaxDescriptionLength} characters ({description.Length})");
        return errors;
    }

    private static SourceInfo CreateSkillSourceInfo(string filePath, string baseDir, string source) => source switch
    {
        "user" => SourceInfo.Synthetic(filePath, "local", scope: "user", baseDir: baseDir),
        "project" => SourceInfo.Synthetic(filePath, "local", scope: "project", baseDir: baseDir),
        "path" => SourceInfo.Synthetic(filePath, "local", baseDir: baseDir),
        _ => SourceInfo.Synthetic(filePath, source, baseDir: baseDir),
    };

    /// <summary>
    /// Load skills from a directory: a directory containing SKILL.md is a skill root (no further recursion); otherwise
    /// direct .md children of the root are loaded and subdirectories are searched for SKILL.md.
    /// </summary>
    public static LoadSkillsResult LoadSkillsFromDir(string dir, string source) => LoadFromDirInternal(dir, source, true, null, null);

    private static LoadSkillsResult LoadFromDirInternal(string dir, string source, bool includeRootFiles, IgnoreMatcher? ignoreMatcher, string? rootDir)
    {
        var skills = new List<Skill>();
        var diagnostics = new List<ResourceDiagnostic>();
        if (!Directory.Exists(dir)) return new LoadSkillsResult(skills, diagnostics);

        var root = rootDir ?? dir;
        var ig = ignoreMatcher ?? new IgnoreMatcher();
        ig.AddIgnoreFiles(dir, root);

        try
        {
            var entries = ResourcePaths.Entries(dir).ToList();
            foreach (var entry in entries)
            {
                if (entry.Name != "SKILL.md") continue;
                var stat = ResourcePaths.Stat(entry);
                if (stat is null) continue;
                var relPath = IgnoreMatcher.ToPosixPath(Path.GetRelativePath(root, entry.FullName));
                if (!stat.Value.IsFile || ig.Ignores(relPath)) continue;

                var result = LoadSkillFromFile(entry.FullName, source);
                if (result.Skill is not null) skills.Add(result.Skill);
                diagnostics.AddRange(result.Diagnostics);
                return new LoadSkillsResult(skills, diagnostics);
            }

            foreach (var entry in entries)
            {
                if (entry.Name.StartsWith('.') || entry.Name == "node_modules") continue;
                var stat = ResourcePaths.Stat(entry);
                if (stat is null) continue;
                var (isFile, isDirectory) = stat.Value;

                var relPath = IgnoreMatcher.ToPosixPath(Path.GetRelativePath(root, entry.FullName));
                if (ig.Ignores(isDirectory ? relPath + "/" : relPath)) continue;

                if (isDirectory)
                {
                    var sub = LoadFromDirInternal(entry.FullName, source, false, ig, root);
                    skills.AddRange(sub.Skills);
                    diagnostics.AddRange(sub.Diagnostics);
                    continue;
                }
                if (!isFile || !includeRootFiles || !entry.Name.EndsWith(".md", StringComparison.Ordinal)) continue;

                var result = LoadSkillFromFile(entry.FullName, source);
                if (result.Skill is not null) skills.Add(result.Skill);
                diagnostics.AddRange(result.Diagnostics);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return new LoadSkillsResult(skills, diagnostics);
    }

    private static (Skill? Skill, List<ResourceDiagnostic> Diagnostics) LoadSkillFromFile(string filePath, string source)
    {
        var diagnostics = new List<ResourceDiagnostic>();
        var isDeclaredSkill = Path.GetFileName(filePath) == "SKILL.md";

        string rawContent;
        try
        {
            rawContent = File.ReadAllText(filePath);
        }
        catch (Exception ex)
        {
            diagnostics.Add(new ResourceDiagnostic("warning", ex.Message, filePath));
            return (null, diagnostics);
        }

        JsonObject frontmatter;
        try
        {
            frontmatter = Frontmatter.Parse(rawContent).Frontmatter;
        }
        catch (Exception ex)
        {
            if (isDeclaredSkill) diagnostics.Add(new ResourceDiagnostic("warning", ex.Message, filePath));
            return (null, diagnostics);
        }

        var description = frontmatter["description"] is JsonValue d && d.GetValueKind() == JsonValueKind.String ? d.GetValue<string>() : null;
        var hasDescription = description is not null && description.Trim().Length > 0;
        if (!isDeclaredSkill && !hasDescription) return (null, diagnostics);

        var skillDir = Path.GetDirectoryName(filePath)!;
        var parentDirName = Path.GetFileName(skillDir);

        foreach (var error in ValidateDescription(description)) diagnostics.Add(new ResourceDiagnostic("warning", error, filePath));

        var frontmatterName = frontmatter["name"] is JsonValue n && n.GetValueKind() == JsonValueKind.String ? n.GetValue<string>() : null;
        var name = string.IsNullOrEmpty(frontmatterName) ? parentDirName : frontmatterName;
        foreach (var error in ValidateName(name)) diagnostics.Add(new ResourceDiagnostic("warning", error, filePath));

        if (!hasDescription) return (null, diagnostics);

        var disable = frontmatter["disable-model-invocation"] is JsonValue dm && dm.GetValueKind() == JsonValueKind.True;
        return (new Skill(name, description!, filePath, skillDir, CreateSkillSourceInfo(filePath, skillDir, source), disable), diagnostics);
    }

    /// <summary>Format skills for the system prompt (Agent Skills XML format). Skills with disableModelInvocation are omitted.</summary>
    public static string FormatSkillsForPrompt(IEnumerable<Skill> skills, string fileReadTool = "read")
    {
        var visible = skills.Where(s => !s.DisableModelInvocation).ToList();
        if (visible.Count == 0) return "";

        var lines = new List<string>
        {
            "\n\nThe following skills provide specialized instructions for specific tasks.",
            fileReadTool == "read"
                ? "Use the read tool to load a skill's file when the task matches its description."
                : "Use bash to load a skill's file when the task matches its description.",
            "When a skill file references a relative path, resolve it against the skill directory (parent of SKILL.md / dirname of the path) and use that absolute path in tool commands.",
            "",
            "<available_skills>",
        };
        foreach (var skill in visible)
        {
            lines.Add("  <skill>");
            lines.Add($"    <name>{EscapeXml(skill.Name)}</name>");
            lines.Add($"    <description>{EscapeXml(skill.Description)}</description>");
            lines.Add($"    <location>{EscapeXml(skill.FilePath)}</location>");
            lines.Add("  </skill>");
        }
        lines.Add("</available_skills>");
        return string.Join("\n", lines);
    }

    private static string EscapeXml(string str) =>
        str.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&apos;");

    /// <summary>Load skills from all configured locations, reporting validation and collision diagnostics.</summary>
    public static LoadSkillsResult LoadSkills(string cwd, string? agentDir, IEnumerable<string> skillPaths, bool includeDefaults)
    {
        var resolvedCwd = PathUtils.ResolvePath(cwd);
        var resolvedAgentDir = PathUtils.ResolvePath(agentDir ?? AppConfig.AgentDir);

        var skillMap = new Dictionary<string, Skill>(StringComparer.Ordinal);
        var ordered = new List<Skill>();
        var realPaths = new HashSet<string>(StringComparer.Ordinal);
        var allDiagnostics = new List<ResourceDiagnostic>();
        var collisionDiagnostics = new List<ResourceDiagnostic>();

        void AddSkills(LoadSkillsResult result)
        {
            allDiagnostics.AddRange(result.Diagnostics);
            foreach (var skill in result.Skills)
            {
                var realPath = PathUtils.CanonicalizePath(skill.FilePath);
                if (realPaths.Contains(realPath)) continue;
                if (skillMap.TryGetValue(skill.Name, out var existing))
                {
                    collisionDiagnostics.Add(new ResourceDiagnostic("collision", $"name \"{skill.Name}\" collision", skill.FilePath,
                        new ResourceCollision("skill", skill.Name, existing.FilePath, skill.FilePath)));
                }
                else
                {
                    skillMap[skill.Name] = skill;
                    ordered.Add(skill);
                    realPaths.Add(realPath);
                }
            }
        }

        var userSkillsDir = Path.Combine(resolvedAgentDir, "skills");
        var projectSkillsDir = Path.GetFullPath(Path.Combine(resolvedCwd, AppConfig.ConfigDirName, "skills"));

        if (includeDefaults)
        {
            AddSkills(LoadFromDirInternal(userSkillsDir, "user", true, null, null));
            AddSkills(LoadFromDirInternal(projectSkillsDir, "project", true, null, null));
        }

        string GetSource(string resolvedPath)
        {
            if (!includeDefaults)
            {
                if (ResourcePaths.IsUnderPath(resolvedPath, userSkillsDir)) return "user";
                if (ResourcePaths.IsUnderPath(resolvedPath, projectSkillsDir)) return "project";
            }
            return "path";
        }

        foreach (var rawPath in skillPaths)
        {
            var resolvedPath = PathUtils.ResolvePath(rawPath, resolvedCwd, new PathInputOptions { Trim = true });
            if (!File.Exists(resolvedPath) && !Directory.Exists(resolvedPath))
            {
                allDiagnostics.Add(new ResourceDiagnostic("warning", "skill path does not exist", resolvedPath));
                continue;
            }
            try
            {
                var source = GetSource(resolvedPath);
                if (Directory.Exists(resolvedPath))
                {
                    AddSkills(LoadFromDirInternal(resolvedPath, source, true, null, null));
                }
                else if (resolvedPath.EndsWith(".md", StringComparison.Ordinal))
                {
                    var result = LoadSkillFromFile(resolvedPath, source);
                    if (result.Skill is not null) AddSkills(new LoadSkillsResult([result.Skill], result.Diagnostics));
                    else allDiagnostics.AddRange(result.Diagnostics);
                }
                else
                {
                    allDiagnostics.Add(new ResourceDiagnostic("warning", "skill path is not a markdown file", resolvedPath));
                }
            }
            catch (Exception ex)
            {
                allDiagnostics.Add(new ResourceDiagnostic("warning", ex.Message, resolvedPath));
            }
        }

        return new LoadSkillsResult(ordered, [.. allDiagnostics, .. collisionDiagnostics]);
    }
}
