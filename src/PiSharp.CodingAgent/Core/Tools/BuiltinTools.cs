using PiSharp.Agent;

namespace PiSharp.CodingAgent.Core.Tools;

public sealed class ToolsOptions
{
    public ReadToolOptions? Read { get; init; }
    public BashToolOptions? Bash { get; init; }
    public BashToolOptions? PowerShell { get; init; }
    public WriteOperations? Write { get; init; }
    public EditOperations? Edit { get; init; }
    public GrepOperations? Grep { get; init; }
    public FindOperations? Find { get; init; }
    public LsOperations? Ls { get; init; }
}

/// <summary>Factory for the built-in tools. Port of core/tools/index.ts.</summary>
public static class BuiltinTools
{
    public static readonly IReadOnlyList<string> AllToolNames = ["read", "bash", "powershell", "edit", "write", "grep", "find", "ls"];

    public static ToolDefinition CreateToolDefinition(string toolName, string cwd, ToolsOptions? options = null) => toolName switch
    {
        "read" => ReadTool.CreateDefinition(cwd, options?.Read),
        "bash" => ShellTool.CreateBashDefinition(cwd, options?.Bash),
        "powershell" => ShellTool.CreatePowerShellDefinition(cwd, options?.PowerShell),
        "edit" => EditTool.CreateDefinition(cwd, options?.Edit),
        "write" => WriteTool.CreateDefinition(cwd, options?.Write),
        "grep" => GrepTool.CreateDefinition(cwd, options?.Grep),
        "find" => FindTool.CreateDefinition(cwd, options?.Find),
        "ls" => LsTool.CreateDefinition(cwd, options?.Ls),
        _ => throw new ArgumentException($"Unknown tool name: {toolName}"),
    };

    public static AgentTool CreateTool(string toolName, string cwd, ToolsOptions? options = null) =>
        CreateToolDefinition(toolName, cwd, options).ToAgentTool();

    public static List<ToolDefinition> CreateCodingToolDefinitions(string cwd, ToolsOptions? options = null) =>
        new[] { "read", "bash", "edit", "write" }.Select(name => CreateToolDefinition(name, cwd, options)).ToList();

    public static List<ToolDefinition> CreateReadOnlyToolDefinitions(string cwd, ToolsOptions? options = null) =>
        new[] { "read", "grep", "find", "ls" }.Select(name => CreateToolDefinition(name, cwd, options)).ToList();

    public static Dictionary<string, ToolDefinition> CreateAllToolDefinitions(string cwd, ToolsOptions? options = null) =>
        AllToolNames.ToDictionary(name => name, name => CreateToolDefinition(name, cwd, options));

    public static List<AgentTool> CreateCodingTools(string cwd, ToolsOptions? options = null) =>
        CreateCodingToolDefinitions(cwd, options).Select(d => d.ToAgentTool()).ToList();

    public static List<AgentTool> CreateReadOnlyTools(string cwd, ToolsOptions? options = null) =>
        CreateReadOnlyToolDefinitions(cwd, options).Select(d => d.ToAgentTool()).ToList();

    public static Dictionary<string, AgentTool> CreateAllTools(string cwd, ToolsOptions? options = null) =>
        AllToolNames.ToDictionary(name => name, name => CreateTool(name, cwd, options));
}
