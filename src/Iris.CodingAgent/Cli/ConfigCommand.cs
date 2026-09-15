using Iris.CodingAgent.Config;
using Iris.CodingAgent.Core;
using Iris.CodingAgent.Modes.Interactive;
using Iris.CodingAgent.Modes.Interactive.Components;
using Iris.CodingAgent.Utils;
using Iris.Tui;

namespace Iris.CodingAgent.Cli;

/// <summary>`config`: resource configuration TUI for enabling and disabling extensions, skills, prompts and themes.</summary>
public static class ConfigCommand
{
    private static string App => AppConfig.AppName;

    private static string Dir => AppConfig.ConfigDirName;

    private static string Usage => $"{App} config [-l] [--approve|--no-approve]";

    private static void PrintHelp() => Console.WriteLine($"""
        {Chalk.Bold("Usage:")}
          {Usage}

        Open the resource configuration TUI to enable or disable extensions, skills, prompts and themes.
        Without -l, starts in global settings (~/{Dir}/agent/settings.json).
        Press Tab in the TUI to switch between global and project-local modes.

        Options:
          -l, --local       Edit project overrides ({Dir}/settings.json)
          -a, --approve     Trust project-local files for this command with -l
          -na, --no-approve Ignore project-local files for this command with -l
          -h, --help        Show this help
        """.Replace("\r\n", "\n"));

    private static void ReportSettingsErrors(SettingsManager settingsManager)
    {
        foreach (var error in settingsManager.DrainErrors())
        {
            Console.Error.WriteLine(Chalk.Yellow($"Warning (config command, {error.Scope.ToString().ToLowerInvariant()} settings): {error.Error.Message}"));
            if (error.Error.StackTrace is { } stack) Console.Error.WriteLine(Chalk.Dim(stack));
        }
    }

    private static async Task<SettingsManager> CreateSettingsManagerAsync(string cwd, string agentDir, bool? projectTrustOverride)
    {
        var settingsManager = SettingsManager.Create(cwd, agentDir, projectTrusted: false);
        var trustStore = new ProjectTrustStore(agentDir);
        var interactive = !Console.IsInputRedirected && !Console.IsOutputRedirected;
        var projectTrusted = await trustStore.ResolveProjectTrustedAsync(cwd, projectTrustOverride, settingsManager.DefaultProjectTrust,
            interactive ? StartupUi.CreateTrustPrompt(settingsManager) : null);
        settingsManager.SetProjectTrusted(projectTrusted);
        return settingsManager;
    }

    /// <summary>Handle `config`; returns null when the arguments are not the config command, otherwise the exit code.</summary>
    public static async Task<int?> RunAsync(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0] != "config") return null;
        var rest = args.Skip(1).ToList();
        if (rest.Contains("-h") || rest.Contains("--help"))
        {
            PrintHelp();
            return 0;
        }
        var local = false;
        bool? projectTrustOverride = null;
        foreach (var arg in rest)
        {
            switch (arg)
            {
                case "-l" or "--local":
                    local = true;
                    break;
                case "-a" or "--approve":
                    projectTrustOverride = true;
                    break;
                case "-na" or "--no-approve":
                    projectTrustOverride = false;
                    break;
                default:
                    if (arg.StartsWith('-'))
                    {
                        Console.Error.WriteLine(Chalk.Red($"Unknown option {arg} for \"config\"."));
                        Console.Error.WriteLine(Chalk.Dim($"Use \"{App} --help\" or \"{Usage}\"."));
                    }
                    else
                    {
                        Console.Error.WriteLine(Chalk.Red($"Unexpected argument {arg}."));
                        Console.Error.WriteLine(Chalk.Dim($"Usage: {Usage}"));
                    }
                    return 1;
            }
        }

        var cwd = Directory.GetCurrentDirectory();
        var agentDir = AppConfig.AgentDir;
        var settingsManager = await CreateSettingsManagerAsync(cwd, agentDir, projectTrustOverride);
        if (local && !settingsManager.IsProjectTrusted)
        {
            Console.Error.WriteLine(Chalk.Red("Project is not trusted. Use --approve to modify local resource config."));
            return 1;
        }
        ReportSettingsErrors(settingsManager);
        var globalSettingsManager = SettingsManager.Create(cwd, agentDir, projectTrusted: false);
        var globalPaths = await new ResourceResolver(cwd, agentDir, globalSettingsManager).ResolveAsync();
        var projectPaths = settingsManager.IsProjectTrusted ? await new ResourceResolver(cwd, agentDir, settingsManager).ResolveAsync() : globalPaths;

        await StartupUi.RunOnUiThreadAsync(async () =>
        {
            var terminalTheme = ThemeManager.DetectTerminalBackgroundFromEnv().Theme;
            ThemeManager.InitTheme(ThemeManager.ResolveThemeSetting(settingsManager.ThemeSetting, terminalTheme) ?? terminalTheme, true);
            KeybindingsManager.Global = AppKeybindings.Create();
            var ui = new TuiMainScreen(new ProcessTerminal(), UiDispatcher.Current!, settingsManager.ShowHardwareCursor, agentDir);
            ui.SetClearOnShrink(settingsManager.ClearOnShrink);
            var tcs = new TaskCompletionSource<bool>();
            var resolved = false;
            var selector = new ConfigSelectorComponent(globalPaths, projectPaths, settingsManager, cwd, agentDir,
                () =>
                {
                    if (resolved) return;
                    resolved = true;
                    ui.Stop();
                    ThemeManager.StopThemeWatcher();
                    tcs.TrySetResult(true);
                },
                () =>
                {
                    ui.Stop();
                    ThemeManager.StopThemeWatcher();
                    tcs.TrySetResult(true);
                },
                () => ui.RequestRender(),
                ui.Terminal.Rows,
                local ? "project" : "global",
                settingsManager.IsProjectTrusted);
            ui.AddChild(selector);
            ui.SetFocus(selector.GetResourceList());
            ui.Start();
            return await tcs.Task;
        });
        await settingsManager.FlushAsync();
        return 0;
    }
}
