using System.Text;
using Iris.CodingAgent.Config;

namespace Iris.CodingAgent.Core;

public sealed class BuildSystemPromptOptions
{
    /// <summary>Custom system prompt (replaces the default).</summary>
    public string? CustomPrompt { get; init; }

    /// <summary>Tools to include in the prompt. Default: read, bash, edit, write.</summary>
    public IReadOnlyList<string>? SelectedTools { get; init; }

    /// <summary>One-line tool snippets keyed by tool name.</summary>
    public IReadOnlyDictionary<string, string>? ToolSnippets { get; init; }

    /// <summary>Additional guideline bullets appended to the default guidelines.</summary>
    public IReadOnlyList<string>? PromptGuidelines { get; init; }

    public string? AppendSystemPrompt { get; init; }

    public required string Cwd { get; init; }

    public IReadOnlyList<ContextFile>? ContextFiles { get; init; }

    public IReadOnlyList<Skill>? Skills { get; init; }
}

public sealed record BuiltinSlashCommand(string Name, string Description, string? ArgumentHint = null);

/// <summary>System prompt construction. Port of core/system-prompt.ts (text kept verbatim).</summary>
public static class SystemPrompt
{
    private static void AppendContextFiles(StringBuilder prompt, IReadOnlyList<ContextFile> contextFiles)
    {
        if (contextFiles.Count == 0) return;
        prompt.Append("\n\n<project_context>\n\n");
        prompt.Append("Project-specific instructions and guidelines:\n\n");
        foreach (var file in contextFiles)
        {
            prompt.Append($"<project_instructions path=\"{file.Path}\">\n{file.Content}\n</project_instructions>\n\n");
        }
        prompt.Append("</project_context>\n");
    }

    public static string Build(BuildSystemPromptOptions options)
    {
        var promptCwd = options.Cwd.Replace('\\', '/');
        var appendSection = string.IsNullOrEmpty(options.AppendSystemPrompt) ? "" : $"\n\n{options.AppendSystemPrompt}";
        var contextFiles = options.ContextFiles ?? [];
        var skills = options.Skills ?? [];
        IReadOnlyList<string> tools = options.SelectedTools is { } selected ? selected : ["read", "bash", "edit", "write"];
        var skillFileReadTool = new[] { "read", "bash" }.FirstOrDefault(tools.Contains);

        if (!string.IsNullOrEmpty(options.CustomPrompt))
        {
            var custom = new StringBuilder(options.CustomPrompt);
            custom.Append(appendSection);
            AppendContextFiles(custom, contextFiles);
            if (skillFileReadTool is not null && skills.Count > 0) custom.Append(Skills.FormatSkillsForPrompt(skills, skillFileReadTool));
            custom.Append($"\nCurrent working directory: {promptCwd}\n");
            return custom.ToString();
        }

        var visibleTools = tools.Where(name => options.ToolSnippets?.GetValueOrDefault(name) is { Length: > 0 }).ToList();
        var toolsList = visibleTools.Count > 0
            ? string.Join("\n", visibleTools.Select(name => $"- {name}: {options.ToolSnippets![name]}"))
            : "(none)";

        var guidelinesList = new List<string>();
        void AddGuideline(string guideline)
        {
            if (!guidelinesList.Contains(guideline)) guidelinesList.Add(guideline);
        }

        var hasBash = tools.Contains("bash");
        var hasPowerShell = tools.Contains("powershell");
        var hasGrep = tools.Contains("grep");
        var hasFind = tools.Contains("find");
        var hasLs = tools.Contains("ls");

        if ((hasBash || hasPowerShell) && !hasGrep && !hasFind && !hasLs)
        {
            if (hasBash && hasPowerShell) AddGuideline("Use bash or PowerShell for file operations like listing, searching, and finding files");
            else if (hasPowerShell) AddGuideline("Use PowerShell for file operations like listing, searching, and finding files");
            else AddGuideline("Use bash for file operations like ls, rg, find");
        }

        foreach (var guideline in options.PromptGuidelines ?? [])
        {
            var normalized = guideline.Trim();
            if (normalized.Length > 0) AddGuideline(normalized);
        }

        AddGuideline("Be concise in your responses");
        AddGuideline("Show file paths clearly when working with files");
        var guidelines = string.Join("\n", guidelinesList.Select(g => $"- {g}"));

        var prompt = new StringBuilder();
        prompt.Append($"""
            You are an expert coding assistant operating inside Iris, a coding agent harness. You help users by reading files, executing commands, editing code, and writing new files.

            Available tools:
            {toolsList}

            In addition to the tools above, you may have access to other custom tools depending on the project.

            Guidelines:
            {guidelines}

            Iris documentation (read only when the user asks about Iris itself, its SDK, extensions, themes, skills, or TUI):
            - Main documentation: {AppConfig.ReadmePath}
            - Additional docs: {AppConfig.DocsPath}
            - Examples: {AppConfig.ExamplesPath} (extensions, custom tools, SDK)
            - When reading Iris docs or examples, resolve docs/... under Additional docs and examples/... under Examples, not the current working directory
            - When asked about: extensions (docs/extensions.md, examples/extensions/), themes (docs/themes.md), skills (docs/skills.md), prompt templates (docs/prompt-templates.md), TUI components (docs/tui.md), keybindings (docs/keybindings.md), SDK integrations (docs/sdk.md), custom providers (docs/custom-provider.md), adding models (docs/models.md), Iris packages (docs/packages.md), environment variables (docs/environment-variables.md)
            - When working on Iris topics, read the docs and examples, and follow .md cross-references before implementing
            - Always read Iris .md files completely and follow links to related docs (e.g., tui.md for TUI API details)
            """.Replace("\r\n", "\n"));

        prompt.Append(appendSection);
        AppendContextFiles(prompt, contextFiles);
        if (skillFileReadTool is not null && skills.Count > 0) prompt.Append(Skills.FormatSkillsForPrompt(skills, skillFileReadTool));
        prompt.Append($"\nCurrent working directory: {promptCwd}");
        return prompt.ToString();
    }

    /// <summary>Port of BUILTIN_SLASH_COMMANDS from core/slash-commands.ts.</summary>
    public static readonly IReadOnlyList<BuiltinSlashCommand> BuiltinSlashCommands =
    [
        new("settings", "Open settings menu"),
        new("model", "Select model (opens selector UI)", "<provider/model>"),
        new("tree", "Navigate session tree (switch branches)"),
        new("thinking", "Set thinking level", "<level>"),
        new("scoped-models", "Enable/disable models for Ctrl+P cycling"),
        new("export", "Export session (HTML default, or specify path: .html/.jsonl)"),
        new("import", "Import and resume a session from a JSONL file"),
        new("share", "Share session as a secret GitHub gist"),
        new("copy", "Copy last agent message to clipboard"),
        new("name", "Set session display name"),
        new("session", "Show session info and stats"),
        new("changelog", "Show changelog entries"),
        new("hotkeys", "Show all keyboard shortcuts"),
        new("fork", "Create a new fork from a previous user message"),
        new("clone", "Duplicate the current session at the current position"),
        new("trust", "Save project trust decision for future sessions"),
        new("login", "Configure provider authentication", "<provider>"),
        new("logout", "Remove provider authentication"),
        new("new", "Start a new session"),
        new("compact", "Manually compact the session context"),
        new("resume", "Resume a different session"),
        new("reload", "Reload keybindings, extensions, skills, prompts, themes, and context files"),
        new("quit", $"Quit {AppConfig.AppName}"),
    ];
}
