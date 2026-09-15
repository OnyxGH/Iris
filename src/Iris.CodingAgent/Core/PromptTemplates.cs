using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Iris.CodingAgent.Config;
using Iris.CodingAgent.Utils;

namespace Iris.CodingAgent.Core;

public sealed record PromptTemplate(string Name, string Description, string? ArgumentHint, string Content, SourceInfo SourceInfo, string FilePath);

/// <summary>Markdown prompt templates invoked as /name.</summary>
public static partial class PromptTemplates
{
    [GeneratedRegex(@"\$\{([0-9]+|ARGUMENTS|@):-([^}]*)\}|\$\{@:([0-9]+)(?::([0-9]+))?\}|\$(ARGUMENTS|@|[0-9]+)")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"^/([^\s]+)(?:\s+([\s\S]*))?$")]
    private static partial Regex TemplateInvocation();

    /// <summary>Parse command arguments respecting single/double quoted strings (bash-style).</summary>
    public static List<string> ParseCommandArgs(string argsString)
    {
        var args = new List<string>();
        var current = new System.Text.StringBuilder();
        char? inQuote = null;
        foreach (var c in argsString)
        {
            if (inQuote is not null)
            {
                if (c == inQuote) inQuote = null;
                else current.Append(c);
            }
            else if (c is '"' or '\'')
            {
                inQuote = c;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    args.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }
        if (current.Length > 0) args.Add(current.ToString());
        return args;
    }

    private static int ParseIntLoose(string digits) => int.TryParse(digits, out var value) ? value : int.MaxValue;

    /// <summary>
    /// Substitute $1, $@, $ARGUMENTS, ${N:-default}, ${@:-default}, ${@:N} and ${@:N:L}. Substitution is not recursive.
    /// </summary>
    public static string SubstituteArgs(string content, IReadOnlyList<string> args)
    {
        var allArgs = string.Join(" ", args);
        return Placeholder().Replace(content, m =>
        {
            if (m.Groups[1].Success)
            {
                var target = m.Groups[1].Value;
                string? value;
                if (target is "@" or "ARGUMENTS")
                {
                    value = allArgs;
                }
                else
                {
                    var index = ParseIntLoose(target) - 1;
                    value = index >= 0 && index < args.Count ? args[index] : null;
                }
                return string.IsNullOrEmpty(value) ? m.Groups[2].Value : value;
            }

            if (m.Groups[3].Success)
            {
                var start = Math.Max(0, ParseIntLoose(m.Groups[3].Value) - 1);
                var skipped = args.Skip(start);
                if (m.Groups[4].Success) skipped = skipped.Take(ParseIntLoose(m.Groups[4].Value));
                return string.Join(" ", skipped);
            }

            var simple = m.Groups[5].Value;
            if (simple is "ARGUMENTS" or "@") return allArgs;
            var i = ParseIntLoose(simple) - 1;
            return i >= 0 && i < args.Count ? args[i] : "";
        });
    }

    private static PromptTemplate? LoadTemplateFromFile(string filePath, SourceInfo sourceInfo)
    {
        try
        {
            var parsed = Frontmatter.Parse(File.ReadAllText(filePath));
            var name = Path.GetFileName(filePath);
            if (name.EndsWith(".md", StringComparison.Ordinal)) name = name[..^3];

            var description = StringField(parsed.Frontmatter, "description") ?? "";
            if (description.Length == 0)
            {
                var firstLine = parsed.Body.Split('\n').FirstOrDefault(line => line.Trim().Length > 0);
                if (firstLine is not null)
                {
                    description = firstLine.Length > 60 ? firstLine[..60] + "..." : firstLine;
                }
            }

            var argumentHint = StringField(parsed.Frontmatter, "argument-hint");
            return new PromptTemplate(name, description, string.IsNullOrEmpty(argumentHint) ? null : argumentHint, parsed.Body, sourceInfo, filePath);
        }
        catch
        {
            return null;
        }
    }

    private static string? StringField(JsonObject obj, string key) => obj[key] switch
    {
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
        JsonValue v when v.GetValueKind() is JsonValueKind.Number or JsonValueKind.True => v.ToJsonString(),
        _ => null,
    };

    private static List<PromptTemplate> LoadTemplatesFromDir(string dir, Func<string, SourceInfo> getSourceInfo)
    {
        var templates = new List<PromptTemplate>();
        if (!Directory.Exists(dir)) return templates;
        try
        {
            foreach (var entry in ResourcePaths.Entries(dir))
            {
                var stat = ResourcePaths.Stat(entry);
                if (stat is null) continue;
                if (stat.Value.IsFile && entry.Name.EndsWith(".md", StringComparison.Ordinal))
                {
                    var template = LoadTemplateFromFile(entry.FullName, getSourceInfo(entry.FullName));
                    if (template is not null) templates.Add(template);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return templates;
    }

    /// <summary>Load templates from agentDir/prompts, cwd/.iris/prompts (when includeDefaults) and explicit paths.</summary>
    public static List<PromptTemplate> LoadPromptTemplates(string cwd, string agentDir, IEnumerable<string> promptPaths, bool includeDefaults)
    {
        var resolvedCwd = PathUtils.ResolvePath(cwd);
        var resolvedAgentDir = PathUtils.ResolvePath(agentDir);
        var templates = new List<PromptTemplate>();
        var globalPromptsDir = Path.Combine(resolvedAgentDir, "prompts");
        var projectPromptsDir = Path.GetFullPath(Path.Combine(resolvedCwd, AppConfig.ConfigDirName, "prompts"));

        SourceInfo GetSourceInfo(string resolvedPath)
        {
            if (ResourcePaths.IsUnderPath(resolvedPath, globalPromptsDir))
            {
                return SourceInfo.Synthetic(resolvedPath, "local", scope: "user", baseDir: globalPromptsDir);
            }
            if (ResourcePaths.IsUnderPath(resolvedPath, projectPromptsDir))
            {
                return SourceInfo.Synthetic(resolvedPath, "local", scope: "project", baseDir: projectPromptsDir);
            }
            return SourceInfo.Synthetic(resolvedPath, "local", baseDir: Directory.Exists(resolvedPath) ? resolvedPath : Path.GetDirectoryName(resolvedPath));
        }

        if (includeDefaults)
        {
            templates.AddRange(LoadTemplatesFromDir(globalPromptsDir, GetSourceInfo));
            templates.AddRange(LoadTemplatesFromDir(projectPromptsDir, GetSourceInfo));
        }

        foreach (var rawPath in promptPaths)
        {
            var resolvedPath = PathUtils.ResolvePath(rawPath, resolvedCwd, new PathInputOptions { Trim = true });
            try
            {
                if (Directory.Exists(resolvedPath))
                {
                    templates.AddRange(LoadTemplatesFromDir(resolvedPath, GetSourceInfo));
                }
                else if (File.Exists(resolvedPath) && resolvedPath.EndsWith(".md", StringComparison.Ordinal))
                {
                    var template = LoadTemplateFromFile(resolvedPath, GetSourceInfo(resolvedPath));
                    if (template is not null) templates.Add(template);
                }
            }
            catch
            {
                // Ignore read failures.
            }
        }
        return templates;
    }

    /// <summary>Expand "/name args" when name matches a template; otherwise return the text unchanged.</summary>
    public static string ExpandPromptTemplate(string text, IReadOnlyList<PromptTemplate> templates)
    {
        if (!text.StartsWith('/')) return text;
        var match = TemplateInvocation().Match(text);
        if (!match.Success) return text;
        var template = templates.FirstOrDefault(t => t.Name == match.Groups[1].Value);
        if (template is null) return text;
        return SubstituteArgs(template.Content, ParseCommandArgs(match.Groups[2].Success ? match.Groups[2].Value : ""));
    }
}
