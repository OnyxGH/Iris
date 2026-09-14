using System.Reflection;
using PiSharp.CodingAgent.Utils;

namespace PiSharp.CodingAgent.Config;

/// <summary>
/// Application identity and user config paths. Port of coding-agent config.ts. PiSharp uses its own config directory
/// (~/.pisharp/agent) with the same file formats as pi.
/// </summary>
public static class AppConfig
{
    public const string PackageName = "@earendil-works/pi-coding-agent";

    /// <summary>Application name (pi's piConfig.name).</summary>
    public const string AppName = "pisharp";

    /// <summary>Display title shown in the UI.</summary>
    public const string AppTitle = "π#";

    /// <summary>Config directory name used in the home directory and projects.</summary>
    public const string ConfigDirName = ".pisharp";

    public static string Version { get; } =
        typeof(AppConfig).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(AppConfig).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";

    public static string EnvAgentDir => $"{AppName.ToUpperInvariant()}_CODING_AGENT_DIR";

    public static string EnvSessionDir => $"{AppName.ToUpperInvariant()}_CODING_AGENT_SESSION_DIR";

    public static string HomeDir => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string ExpandTildePath(string path) => PathUtils.NormalizePath(path);

    /// <summary>Directory containing the executable and bundled assets.</summary>
    public static string PackageDir
    {
        get
        {
            var envDir = Environment.GetEnvironmentVariable("PI_PACKAGE_DIR");
            return !string.IsNullOrEmpty(envDir) ? PathUtils.NormalizePath(envDir) : AppContext.BaseDirectory;
        }
    }

    public static string ThemesDir => Path.Combine(PackageDir, "theme");

    public static string ExportTemplateDir => Path.Combine(PackageDir, "export-html");

    public static string ReadmePath => Path.GetFullPath(Path.Combine(PackageDir, "README.md"));

    public static string DocsPath => Path.GetFullPath(Path.Combine(PackageDir, "docs"));

    public static string ExamplesPath => Path.GetFullPath(Path.Combine(PackageDir, "examples"));

    public static string ChangelogPath => Path.GetFullPath(Path.Combine(PackageDir, "CHANGELOG.md"));

    public static string InteractiveAssetsDir => Path.Combine(PackageDir, "assets");

    public static string ShareViewerUrl(string gistId)
    {
        var baseUrl = Environment.GetEnvironmentVariable("PI_SHARE_VIEWER_URL");
        return $"{(string.IsNullOrEmpty(baseUrl) ? "https://pi.dev/session/" : baseUrl)}#{gistId}";
    }

    /// <summary>The agent config directory (e.g. ~/.pisharp/agent).</summary>
    public static string AgentDir
    {
        get
        {
            var envDir = Environment.GetEnvironmentVariable(EnvAgentDir);
            return !string.IsNullOrEmpty(envDir) ? ExpandTildePath(envDir) : Path.Combine(HomeDir, ConfigDirName, "agent");
        }
    }

    public static string CustomThemesDir => Path.Combine(AgentDir, "themes");

    public static string ModelsPath => Path.Combine(AgentDir, "models.json");

    public static string AuthPath => Path.Combine(AgentDir, "auth.json");

    public static string SettingsPath => Path.Combine(AgentDir, "settings.json");

    public static string ToolsDir => Path.Combine(AgentDir, "tools");

    public static string BinDir => Path.Combine(AgentDir, "bin");

    public static string PromptsDir => Path.Combine(AgentDir, "prompts");

    public static string SessionsDir => Path.Combine(AgentDir, "sessions");

    public static string DebugLogPath => Path.Combine(AgentDir, $"{AppName}-debug.log");
}
