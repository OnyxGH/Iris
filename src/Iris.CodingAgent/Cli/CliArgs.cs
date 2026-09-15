using System.Text;
using Iris.Ai;
using Iris.CodingAgent.Config;
using Iris.CodingAgent.Utils;

namespace Iris.CodingAgent.Cli;

public sealed record ArgsDiagnostic(string Type, string Message);

/// <summary>Parsed command line.</summary>
public sealed class CliArgs
{
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public string? ApiKey { get; set; }
    public string? SystemPrompt { get; set; }
    public List<string>? AppendSystemPrompt { get; set; }
    public ThinkingLevel? Thinking { get; set; }
    public bool Continue { get; set; }
    public bool Resume { get; set; }
    public bool Help { get; set; }
    public bool Version { get; set; }

    /// <summary>"text" | "json" | "rpc".</summary>
    public string? Mode { get; set; }

    public string? Name { get; set; }
    public bool NoSession { get; set; }
    public string? Session { get; set; }
    public string? SessionId { get; set; }
    public string? Fork { get; set; }
    public string? SessionDir { get; set; }
    public List<string>? Models { get; set; }
    public List<string>? Tools { get; set; }
    public List<string>? ExcludeTools { get; set; }
    public bool NoTools { get; set; }
    public bool NoBuiltinTools { get; set; }
    public List<string>? Extensions { get; set; }
    public bool NoExtensions { get; set; }
    public bool Print { get; set; }
    public string? Export { get; set; }
    public bool NoSkills { get; set; }
    public List<string>? Skills { get; set; }
    public List<string>? PromptTemplates { get; set; }
    public bool NoPromptTemplates { get; set; }
    public List<string>? Themes { get; set; }
    public string? UseTheme { get; set; }
    public bool NoThemes { get; set; }
    public bool NoContextFiles { get; set; }

    /// <summary>null = not requested; "" = list all; otherwise a search pattern.</summary>
    public string? ListModels { get; set; }

    public bool Offline { get; set; }
    public string? TuiMode { get; set; }
    public bool Verbose { get; set; }
    public bool? ProjectTrustOverride { get; set; }
    public List<string> Messages { get; } = [];
    public List<string> FileArgs { get; } = [];

    /// <summary>Unknown flags (potentially extension flags): name → string value or true.</summary>
    public Dictionary<string, object> UnknownFlags { get; } = [];

    public List<ArgsDiagnostic> Diagnostics { get; } = [];

    private static readonly string[] ValidThinkingLevels = ["off", "minimal", "low", "medium", "high", "xhigh", "max"];

    public static string? NormalizeSessionName(string value)
    {
        var name = value.Trim();
        return name.Length > 0 ? name : null;
    }

    private static List<string> SplitList(string value) => value.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

    public static CliArgs Parse(IReadOnlyList<string> args)
    {
        var result = new CliArgs();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            bool HasNext() => i + 1 < args.Count;

            switch (arg)
            {
                case "--":
                    foreach (var positional in args.Skip(i + 1))
                    {
                        if (positional.StartsWith('@')) result.FileArgs.Add(positional[1..]);
                        else result.Messages.Add(positional);
                    }
                    return result;
                case "--help" or "-h":
                    result.Help = true;
                    break;
                case "--version" or "-v":
                    result.Version = true;
                    break;
                case "--mode" when HasNext():
                {
                    var mode = args[++i];
                    if (mode is "text" or "json" or "rpc") result.Mode = mode;
                    break;
                }
                case "--continue" or "-c":
                    result.Continue = true;
                    break;
                case "--resume" or "-r":
                    result.Resume = true;
                    break;
                case "--provider" when HasNext():
                    result.Provider = args[++i];
                    break;
                case "--model" when HasNext():
                    result.Model = args[++i];
                    break;
                case "--api-key" when HasNext():
                    result.ApiKey = args[++i];
                    break;
                case "--system-prompt" when HasNext():
                    result.SystemPrompt = args[++i];
                    break;
                case "--append-system-prompt" when HasNext():
                    (result.AppendSystemPrompt ??= []).Add(args[++i]);
                    break;
                case "--name" or "-n":
                    if (HasNext()) result.Name = args[++i];
                    else result.Diagnostics.Add(new("error", "--name requires a value"));
                    break;
                case "--no-session":
                    result.NoSession = true;
                    break;
                case "--session" when HasNext():
                    result.Session = args[++i];
                    break;
                case "--session-id" when HasNext():
                    result.SessionId = args[++i];
                    break;
                case "--fork" when HasNext():
                    result.Fork = args[++i];
                    break;
                case "--session-dir" when HasNext():
                    result.SessionDir = args[++i];
                    break;
                case "--models" when HasNext():
                    result.Models = args[++i].Split(',').Select(s => s.Trim()).ToList();
                    break;
                case "--no-tools" or "-nt":
                    result.NoTools = true;
                    break;
                case "--no-builtin-tools" or "-nbt":
                    result.NoBuiltinTools = true;
                    break;
                case "--tools" or "-t" when HasNext():
                    result.Tools = SplitList(args[++i]);
                    break;
                case "--exclude-tools" or "-xt" when HasNext():
                    result.ExcludeTools = SplitList(args[++i]);
                    break;
                case "--thinking" when HasNext():
                {
                    var level = args[++i];
                    if (ThinkingLevels.TryParse(level, out var parsed) && ValidThinkingLevels.Contains(level)) result.Thinking = parsed;
                    else result.Diagnostics.Add(new("warning", $"Invalid thinking level \"{level}\". Valid values: {string.Join(", ", ValidThinkingLevels)}"));
                    break;
                }
                case "--print" or "-p":
                {
                    result.Print = true;
                    var next = HasNext() ? args[i + 1] : null;
                    if (next is not null && !next.StartsWith('@') && (!next.StartsWith('-') || next.StartsWith("---", StringComparison.Ordinal)))
                    {
                        result.Messages.Add(next);
                        i++;
                    }
                    break;
                }
                case "--export" when HasNext():
                    result.Export = args[++i];
                    break;
                case "--extension" or "-e" when HasNext():
                    (result.Extensions ??= []).Add(args[++i]);
                    break;
                case "--no-extensions" or "-ne":
                    result.NoExtensions = true;
                    break;
                case "--skill" when HasNext():
                    (result.Skills ??= []).Add(args[++i]);
                    break;
                case "--prompt-template" when HasNext():
                    (result.PromptTemplates ??= []).Add(args[++i]);
                    break;
                case "--theme" when HasNext():
                    (result.Themes ??= []).Add(args[++i]);
                    break;
                case "--use-theme":
                {
                    var themeName = HasNext() ? args[i + 1] : null;
                    if (themeName is null || themeName.StartsWith('-'))
                    {
                        result.Diagnostics.Add(new("error", "--use-theme requires a theme name"));
                    }
                    else
                    {
                        result.UseTheme = themeName;
                        i++;
                    }
                    break;
                }
                case "--no-skills" or "-ns":
                    result.NoSkills = true;
                    break;
                case "--no-prompt-templates" or "-np":
                    result.NoPromptTemplates = true;
                    break;
                case "--no-themes":
                    result.NoThemes = true;
                    break;
                case "--no-context-files" or "-nc":
                    result.NoContextFiles = true;
                    break;
                case "--list-models":
                    if (HasNext() && !args[i + 1].StartsWith('-') && !args[i + 1].StartsWith('@')) result.ListModels = args[++i];
                    else result.ListModels = "";
                    break;
                case "--tui-mode":
                {
                    var mode = HasNext() ? args[i + 1] : null;
                    if (mode is "regular" or "fullscreen")
                    {
                        result.TuiMode = mode;
                        i++;
                    }
                    else if (mode is null || mode.StartsWith('-'))
                    {
                        result.Diagnostics.Add(new("error", "--tui-mode requires regular or fullscreen"));
                    }
                    else
                    {
                        i++;
                        result.Diagnostics.Add(new("error", $"Invalid TUI mode \"{mode}\". Valid values: regular, fullscreen"));
                    }
                    break;
                }
                case "--verbose":
                    result.Verbose = true;
                    break;
                case "--approve" or "-a":
                    result.ProjectTrustOverride = true;
                    break;
                case "--no-approve" or "-na":
                    result.ProjectTrustOverride = false;
                    break;
                case "--offline":
                    result.Offline = true;
                    break;
                default:
                    if (arg.StartsWith('@'))
                    {
                        result.FileArgs.Add(arg[1..]);
                    }
                    else if (arg.StartsWith("--", StringComparison.Ordinal))
                    {
                        var eqIndex = arg.IndexOf('=');
                        if (eqIndex != -1)
                        {
                            result.UnknownFlags[arg[2..eqIndex]] = arg[(eqIndex + 1)..];
                        }
                        else
                        {
                            var next = HasNext() ? args[i + 1] : null;
                            if (next is not null && !next.StartsWith('-') && !next.StartsWith('@'))
                            {
                                result.UnknownFlags[arg[2..]] = next;
                                i++;
                            }
                            else
                            {
                                result.UnknownFlags[arg[2..]] = true;
                            }
                        }
                    }
                    else if (arg.StartsWith('-'))
                    {
                        result.Diagnostics.Add(new("error", $"Unknown option: {arg}"));
                    }
                    else
                    {
                        result.Messages.Add(arg);
                    }
                    break;
            }
        }
        return result;
    }

    public static string HelpText()
    {
        var app = AppConfig.AppName;
        var sb = new StringBuilder();
        sb.AppendLine($"{Chalk.Bold(app)} - AI coding assistant with read, bash, edit, write tools");
        sb.AppendLine();
        sb.AppendLine(Chalk.Bold("Usage:"));
        sb.AppendLine($"  {app} [options] [--] [@files...] [messages...]");
        sb.AppendLine();
        sb.AppendLine(Chalk.Bold("Commands:"));
        sb.AppendLine($"  {app} config [-l]               Open TUI to enable/disable resources (Tab switches scope)");
        sb.AppendLine($"  {app} auth <command>            Print credentials or check provider readiness");
        sb.AppendLine($"  {app} <command> --help          Show help for config/auth");
        sb.AppendLine();
        sb.AppendLine(Chalk.Bold("Options:"));
        sb.AppendLine("""
              --provider <name>              Provider name (default: google)
              --model <pattern>              Model pattern or ID (supports "provider/id" and optional ":<thinking>")
              --api-key <key>                API key (defaults to env vars)
              --system-prompt <text>         System prompt (default: coding assistant prompt)
              --append-system-prompt <text>  Append text or file contents to the system prompt (can be used multiple times)
              --mode <mode>                  Output mode: text (default), json, or rpc
              --print, -p                    Non-interactive mode: process prompt and exit
              --continue, -c                 Continue previous session
              --resume, -r                   Select a session to resume
              --session <path|id>            Use specific session file or partial UUID
              --session-id <id>              Use exact project session ID, creating it if missing
              --fork <path|id>               Fork specific session file or partial UUID into a new session
              --session-dir <dir>            Directory for session storage and lookup
              --no-session                   Don't save session (ephemeral)
              --name, -n <name>              Set session display name
              --models <patterns>            Comma-separated model patterns for Ctrl+P cycling
                                             Supports globs (anthropic/*, *sonnet*) and fuzzy matching
              --no-tools, -nt                Disable all tools by default (built-in and extension)
              --no-builtin-tools, -nbt       Disable built-in tools by default but keep extension/custom tools enabled
              --tools, -t <tools>            Comma-separated allowlist of tool names to enable
                                             Applies to built-in, extension, and custom tools
              --exclude-tools, -xt <tools>   Comma-separated denylist of tool names to disable
                                             Applies to built-in, extension, and custom tools
              --thinking <level>             Set thinking level: off, minimal, low, medium, high, xhigh, max
              --extension, -e <path>         Load an extension file (can be used multiple times)
              --no-extensions, -ne           Disable extension discovery (explicit -e paths still work)
              --skill <path>                 Load a skill file or directory (can be used multiple times)
              --no-skills, -ns               Disable skills discovery and loading
              --prompt-template <path>       Load a prompt template file or directory (can be used multiple times)
              --no-prompt-templates, -np     Disable prompt template discovery and loading
              --theme <path>                 Load a theme file or directory (can be used multiple times)
              --use-theme <name[/name]>      Set the initial interactive theme for this run
              --no-themes                    Disable theme discovery and loading
              --no-context-files, -nc        Disable AGENTS.md and CLAUDE.md discovery and loading
              --export <file>                Export session file to HTML and exit
              --list-models [search]         List available models (with optional fuzzy search)
              --verbose                      Force verbose startup (overrides quietStartup setting)
              --tui-mode <mode>              TUI mode: fullscreen (default) or regular
              --approve, -a                  Trust project-local files for this run
              --no-approve, -na              Ignore project-local files for this run
              --offline                      Disable startup network operations (same as IRIS_OFFLINE=1)
              --                             End option parsing; treat remaining arguments as messages/files
              --help, -h                     Show this help
              --version, -v                  Show version number
            """.Replace("\r\n", "\n"));
        sb.AppendLine();
        sb.AppendLine(Chalk.Bold("Examples:"));
        sb.AppendLine($"  # Interactive mode\n  {app}\n");
        sb.AppendLine($"  # Include files in initial message\n  {app} @prompt.md @image.png \"What color is the sky?\"\n");
        sb.AppendLine($"  # Non-interactive mode (process and exit)\n  {app} -p \"List all .ts files in src/\"\n");
        sb.AppendLine($"  # Continue previous session\n  {app} --continue \"What did we discuss?\"\n");
        sb.AppendLine($"  # Use model with provider prefix and thinking level\n  {app} --model openai/gpt-4o:high \"Help me refactor this code\"\n");
        sb.AppendLine($"  # Read-only mode (no file modifications possible)\n  {app} --tools read,grep,find,ls -p \"Review the code in src/\"");
        sb.AppendLine();
        sb.AppendLine(Chalk.Bold("Environment Variables:"));
        sb.AppendLine($"  {AppConfig.EnvAgentDir.PadRight(32)} - Config directory (default: ~/{AppConfig.ConfigDirName}/agent)");
        sb.AppendLine($"  {AppConfig.EnvSessionDir.PadRight(32)} - Session storage directory (overridden by --session-dir)");
        sb.AppendLine("  IRIS_OFFLINE                       - Disable startup network operations when set to 1/true/yes");
        sb.AppendLine("  IRIS_TELEMETRY                     - Override install telemetry when set to 1/true/yes or 0/false/no");
        sb.AppendLine("  Provider API keys use the standard variables (ANTHROPIC_API_KEY, OPENAI_API_KEY, GEMINI_API_KEY, ...)");
        sb.AppendLine();
        sb.AppendLine(Chalk.Bold("Built-in Tool Names:"));
        sb.AppendLine("""
              read       - Read file contents
              bash       - Execute bash commands
              powershell - Execute PowerShell commands on Windows
              edit       - Edit files with find/replace
              write      - Write files (creates/overwrites)
              grep       - Search file contents (read-only, off by default)
              find       - Find files by glob pattern (read-only, off by default)
              ls         - List directory contents (read-only, off by default)
            """.Replace("\r\n", "\n"));
        return sb.ToString();
    }
}
