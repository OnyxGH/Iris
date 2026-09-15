using Iris.Ai.Models;
using Iris.CodingAgent.Config;
using Iris.CodingAgent.Core;
using Iris.CodingAgent.Modes.Interactive;
using Iris.CodingAgent.Modes.Interactive.Components;
using Iris.CodingAgent.Utils;
using Iris.Tui;
using Iris.Tui.Components;

namespace Iris.CodingAgent.Cli;

/// <summary>
/// `install`, `remove`/`uninstall`, `update`, `list` and `config` commands. Port of package-manager-cli.ts.
/// Self-update is not available: Iris is not distributed through npm or pi's managed installer.
/// </summary>
public static class PackageCommands
{
    private sealed class Options
    {
        public required string Command { get; init; }
        public string? Source { get; set; }
        public string UpdateTarget { get; set; } = "self"; // all | self | extensions | models
        public string? UpdateSource { get; set; }
        public bool ShowExtensionsSkippedNote { get; set; }
        public bool Local { get; set; }
        public bool Force { get; set; }
        public bool? ProjectTrustOverride { get; set; }
        public bool Help { get; set; }
        public string? InvalidOption { get; set; }
        public string? InvalidArgument { get; set; }
        public string? MissingOptionValue { get; set; }
        public string? ConflictingOptions { get; set; }
    }

    private static string App => AppConfig.AppName;

    private static string Dir => AppConfig.ConfigDirName;

    private static string Usage(string command) => command switch
    {
        "install" => $"{App} install <source> [-l] [--approve|--no-approve]",
        "remove" => $"{App} remove <source> [-l] [--approve|--no-approve]",
        "update" => $"{App} update [source|self|pi] [--self|--extensions|--models|--all] [--extension <source>] [--approve|--no-approve] [--force]",
        _ => $"{App} list [--approve|--no-approve]",
    };

    private static string ConfigUsage => $"{App} config [-l] [--approve|--no-approve]";

    private static void PrintConfigHelp() => Console.WriteLine($"""
        {Chalk.Bold("Usage:")}
          {ConfigUsage}

        Open the resource configuration TUI to enable or disable package resources.
        Without -l, starts in global settings (~/{Dir}/agent/settings.json).
        Press Tab in the TUI to switch between global and project-local modes.

        Options:
          -l, --local       Edit project overrides ({Dir}/settings.json)
          -a, --approve     Trust project-local files for this command with -l
          -na, --no-approve Ignore project-local files for this command with -l

        """);

    private static void PrintHelp(string command)
    {
        switch (command)
        {
            case "install":
                Console.WriteLine($"""
                    {Chalk.Bold("Usage:")}
                      {Usage("install")}

                    Install a package and add it to settings.

                    Options:
                      -l, --local       Install project-locally ({Dir}/settings.json)
                      -a, --approve     Trust project-local files for this command
                      -na, --no-approve Ignore project-local files for this command

                    Examples:
                      {App} install npm:@foo/bar
                      {App} install git:github.com/user/repo
                      {App} install git:git@github.com:user/repo
                      {App} install https://github.com/user/repo
                      {App} install ssh://git@github.com/user/repo
                      {App} install ./local/path

                    """);
                return;
            case "remove":
                Console.WriteLine($"""
                    {Chalk.Bold("Usage:")}
                      {Usage("remove")}

                    Remove a package and its source from settings.
                    Alias: {App} uninstall <source> [-l]

                    Options:
                      -l, --local       Remove from project settings ({Dir}/settings.json)
                      -a, --approve     Trust project-local files for this command
                      -na, --no-approve Ignore project-local files for this command

                    Examples:
                      {App} remove npm:@foo/bar
                      {App} uninstall npm:@foo/bar

                    """);
                return;
            case "update":
                Console.WriteLine($"""
                    {Chalk.Bold("Usage:")}
                      {Usage("update")}

                    Update pi, installed packages, or model catalogs.

                    Options:
                      --self                  Update pi only (default when no target is given)
                      --extensions            Update installed packages only
                      --models                Refresh model catalogs only
                      --all                   Update pi and installed packages
                      --extension <source>    Update one package only
                      -a, --approve           Trust project-local files for this command
                      -na, --no-approve       Ignore project-local files for this command
                      --force                 Reinstall pi even if the current version is latest

                    Short forms:
                      {App} update                Update pi only
                      {App} update --all          Update pi and all extensions
                      {App} update --models       Refresh model catalogs only
                      {App} update <source>       Update one package
                      {App} update pi             Update pi only (self works as alias to pi)

                    """);
                return;
            default:
                Console.WriteLine($"""
                    {Chalk.Bold("Usage:")}
                      {Usage("list")}

                    List installed packages from user and project settings.

                    Options:
                      -a, --approve      Trust project-local files for this command
                      -na, --no-approve  Ignore project-local files for this command

                    """);
                return;
        }
    }

    private static Options? Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0) return null;
        var command = args[0] switch
        {
            "uninstall" => "remove",
            "install" or "remove" or "update" or "list" => args[0],
            _ => null,
        };
        if (command is null) return null;
        var options = new Options { Command = command };
        bool selfFlag = false, extensionsFlag = false, modelsFlag = false, allFlag = false;
        string? extensionFlagSource = null;
        for (var index = 1; index < args.Count; index++)
        {
            var arg = args[index];
            switch (arg)
            {
                case "-h" or "--help":
                    options.Help = true;
                    continue;
                case "-l" or "--local":
                    if (command is "install" or "remove") options.Local = true;
                    else options.InvalidOption ??= arg;
                    continue;
                case "--self":
                    if (command == "update") selfFlag = true;
                    else options.InvalidOption ??= arg;
                    continue;
                case "--extensions":
                    if (command == "update") extensionsFlag = true;
                    else options.InvalidOption ??= arg;
                    continue;
                case "--models":
                    if (command == "update") modelsFlag = true;
                    else options.InvalidOption ??= arg;
                    continue;
                case "--all":
                    if (command == "update") allFlag = true;
                    else options.InvalidOption ??= arg;
                    continue;
                case "--approve" or "-a":
                    options.ProjectTrustOverride = true;
                    continue;
                case "--no-approve" or "-na":
                    options.ProjectTrustOverride = false;
                    continue;
                case "--force":
                    if (command == "update") options.Force = true;
                    else options.InvalidOption ??= arg;
                    continue;
                case "--extension":
                {
                    if (command != "update")
                    {
                        options.InvalidOption ??= arg;
                        continue;
                    }
                    var value = index + 1 < args.Count ? args[index + 1] : null;
                    if (string.IsNullOrEmpty(value) || value.StartsWith('-'))
                    {
                        options.MissingOptionValue ??= arg;
                    }
                    else if (extensionFlagSource is not null)
                    {
                        options.ConflictingOptions ??= "--extension can only be provided once";
                        index++;
                    }
                    else
                    {
                        extensionFlagSource = value;
                        index++;
                    }
                    continue;
                }
            }
            if (arg.StartsWith('-'))
            {
                options.InvalidOption ??= arg;
                continue;
            }
            if (options.Source is null) options.Source = arg;
            else options.InvalidArgument ??= arg;
        }

        if (command == "update")
        {
            var source = options.Source;
            if (allFlag && (selfFlag || extensionsFlag || modelsFlag || extensionFlagSource is not null))
            {
                options.ConflictingOptions ??= "--all cannot be combined with --self, --extensions, --models, or --extension";
            }
            if (allFlag && source is not null) options.ConflictingOptions ??= "--all cannot be combined with a positional source";
            if (modelsFlag)
            {
                if (selfFlag || extensionsFlag || allFlag || extensionFlagSource is not null)
                {
                    options.ConflictingOptions ??= "--models cannot be combined with --self, --extensions, --all, or --extension";
                }
                if (source is not null) options.ConflictingOptions ??= "--models cannot be combined with a positional source";
                options.UpdateTarget = "models";
            }
            else if (extensionFlagSource is not null)
            {
                if (selfFlag || extensionsFlag || allFlag) options.ConflictingOptions ??= "--extension cannot be combined with --self, --extensions, or --all";
                if (source is not null) options.ConflictingOptions ??= "--extension cannot be combined with a positional source";
                options.UpdateTarget = "extensions";
                options.UpdateSource = extensionFlagSource;
            }
            else if (source is not null)
            {
                if (source is "self" or "pi")
                {
                    options.UpdateTarget = extensionsFlag ? "all" : "self";
                }
                else
                {
                    if (extensionsFlag || selfFlag || allFlag) options.ConflictingOptions ??= "positional update targets cannot be combined with --self, --extensions, or --all";
                    options.UpdateTarget = "extensions";
                    options.UpdateSource = source;
                }
            }
            else if (allFlag || (selfFlag && extensionsFlag))
            {
                options.UpdateTarget = "all";
            }
            else if (selfFlag)
            {
                options.UpdateTarget = "self";
            }
            else if (extensionsFlag)
            {
                options.UpdateTarget = "extensions";
            }
            else
            {
                options.UpdateTarget = "self";
                options.ShowExtensionsSkippedNote = true;
            }
        }
        return options;
    }

    private static void ReportSettingsErrors(SettingsManager settingsManager, string context)
    {
        foreach (var error in settingsManager.DrainErrors())
        {
            Console.Error.WriteLine(Chalk.Yellow($"Warning ({context}, {error.Scope.ToString().ToLowerInvariant()} settings): {error.Error.Message}"));
            if (error.Error.StackTrace is { } stack) Console.Error.WriteLine(Chalk.Dim(stack));
        }
    }

    private static async Task<SettingsManager> CreateCommandSettingsManagerAsync(string cwd, string agentDir, bool? projectTrustOverride, bool useSavedProjectTrustOnly = false)
    {
        var settingsManager = SettingsManager.Create(cwd, agentDir, projectTrusted: false);
        var trustStore = new ProjectTrustStore(agentDir);
        if (useSavedProjectTrustOnly)
        {
            settingsManager.SetProjectTrusted(projectTrustOverride ?? trustStore.Get(cwd) == true);
            return settingsManager;
        }
        var interactive = !Console.IsInputRedirected && !Console.IsOutputRedirected;
        var projectTrusted = await trustStore.ResolveProjectTrustedAsync(cwd, projectTrustOverride, settingsManager.DefaultProjectTrust,
            interactive ? StartupUi.CreateTrustPrompt(settingsManager) : null);
        settingsManager.SetProjectTrusted(projectTrusted);
        return settingsManager;
    }

    /// <summary>Handle `config`; returns null when the arguments are not the config command, otherwise the exit code.</summary>
    public static async Task<int?> RunConfigAsync(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0] != "config") return null;
        var rest = args.Skip(1).ToList();
        if (rest.Contains("-h") || rest.Contains("--help"))
        {
            PrintConfigHelp();
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
                        Console.Error.WriteLine(Chalk.Dim($"Use \"{App} --help\" or \"{ConfigUsage}\"."));
                    }
                    else
                    {
                        Console.Error.WriteLine(Chalk.Red($"Unexpected argument {arg}."));
                        Console.Error.WriteLine(Chalk.Dim($"Usage: {ConfigUsage}"));
                    }
                    return 1;
            }
        }

        var cwd = Directory.GetCurrentDirectory();
        var agentDir = AppConfig.AgentDir;
        var settingsManager = await CreateCommandSettingsManagerAsync(cwd, agentDir, projectTrustOverride);
        if (local && !settingsManager.IsProjectTrusted)
        {
            Console.Error.WriteLine(Chalk.Red("Project is not trusted. Use --approve to modify local resource config."));
            return 1;
        }
        ReportSettingsErrors(settingsManager, "config command");
        var globalSettingsManager = SettingsManager.Create(cwd, agentDir, projectTrusted: false);
        var globalPaths = await new PackageManager(cwd, agentDir, globalSettingsManager).ResolveAsync();
        var projectPaths = settingsManager.IsProjectTrusted ? await new PackageManager(cwd, agentDir, settingsManager).ResolveAsync() : globalPaths;

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

    private static async Task RefreshModelCatalogsAsync(string agentDir)
    {
        using var cts = new CancellationTokenSource(15_000);
        var runtime = await ModelRuntime.CreateAsync(new CreateModelRuntimeOptions
        {
            AuthPath = Path.Combine(agentDir, "auth.json"),
            ModelsPath = Path.Combine(agentDir, "models.json"),
            AllowModelNetwork = false,
            CancellationToken = cts.Token,
        });
        var result = await runtime.RefreshAsync(new ModelsRefreshOptions { AllowNetwork = true, Force = true, CancellationToken = cts.Token });
        if (result.Aborted) throw new TimeoutException("Model catalog refresh timed out.");
        if (result.Errors.Count > 0)
        {
            throw new InvalidOperationException($"Could not refresh model catalogs: {string.Join("; ", result.Errors.Select(kv => $"{kv.Key}: {kv.Value.Message}"))}");
        }
        Console.WriteLine(Chalk.Green("Model catalogs refreshed"));
    }

    private static void PrintSelfUpdateUnavailable()
    {
        Console.Error.WriteLine($"error: {App} cannot self-update this installation.");
        Console.Error.WriteLine($"Update {App} by rebuilding it from its source checkout or installing a newer release.");
        if (Environment.ProcessPath is { } entrypoint)
        {
            Console.Error.WriteLine("");
            Console.Error.WriteLine($"Location of {App} executable: {entrypoint}");
        }
    }

    private static void PrintSelfUpdateNote(string note)
    {
        var trimmed = note.Trim();
        if (trimmed.Length == 0) return;
        Console.WriteLine();
        Console.WriteLine(Chalk.Bold(Chalk.Yellow("Update note")));
        try
        {
            var width = Math.Max(20, Console.IsOutputRedirected ? 80 : Console.WindowWidth);
            var theme = new MarkdownTheme
            {
                Heading = text => Chalk.Bold(Chalk.Yellow(text)),
                Link = Chalk.Cyan,
                LinkUrl = Chalk.Dim,
                Code = Chalk.Yellow,
                CodeBlock = Chalk.Dim,
                CodeBlockBorder = Chalk.Dim,
                Quote = Chalk.Dim,
                QuoteBorder = Chalk.Dim,
                Hr = Chalk.Dim,
                ListBullet = Chalk.Yellow,
                Bold = Chalk.Bold,
                Italic = AnsiStyle.Italic,
                Strikethrough = AnsiStyle.Strikethrough,
                Underline = AnsiStyle.Underline,
            };
            Console.WriteLine(string.Join("\n", new MarkdownComponent(trimmed, 0, 0, theme).Render(width).Select(line => line.TrimEnd())));
        }
        catch
        {
            Console.WriteLine(trimmed);
        }
        Console.WriteLine();
    }

    /// <summary>Handle package commands; returns null when the arguments are not a package command, otherwise the exit code.</summary>
    public static async Task<int?> RunAsync(IReadOnlyList<string> args)
    {
        if (Parse(args) is not { } options) return null;
        if (options.Help)
        {
            PrintHelp(options.Command);
            return 0;
        }
        if (options.InvalidOption is not null)
        {
            Console.Error.WriteLine(Chalk.Red($"Unknown option {options.InvalidOption} for \"{options.Command}\"."));
            Console.Error.WriteLine(Chalk.Dim($"Use \"{App} --help\" or \"{Usage(options.Command)}\"."));
            return 1;
        }
        if (options.MissingOptionValue is not null)
        {
            Console.Error.WriteLine(Chalk.Red($"Missing value for {options.MissingOptionValue}."));
            Console.Error.WriteLine(Chalk.Dim($"Usage: {Usage(options.Command)}"));
            return 1;
        }
        if (options.InvalidArgument is not null)
        {
            Console.Error.WriteLine(Chalk.Red($"Unexpected argument {options.InvalidArgument}."));
            Console.Error.WriteLine(Chalk.Dim($"Usage: {Usage(options.Command)}"));
            return 1;
        }
        if (options.ConflictingOptions is not null)
        {
            Console.Error.WriteLine(Chalk.Red(options.ConflictingOptions));
            Console.Error.WriteLine(Chalk.Dim($"Usage: {Usage(options.Command)}"));
            return 1;
        }
        var source = options.Source;
        if (options.Command is "install" or "remove" && source is null)
        {
            Console.Error.WriteLine(Chalk.Red($"Missing {options.Command} source."));
            Console.Error.WriteLine(Chalk.Dim($"Usage: {Usage(options.Command)}"));
            return 1;
        }
        if (options.Command == "update" && options.UpdateTarget == "models")
        {
            try
            {
                await RefreshModelCatalogsAsync(AppConfig.AgentDir);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(Chalk.Red($"Error: {ex.Message}"));
                return 1;
            }
        }

        var cwd = Directory.GetCurrentDirectory();
        var agentDir = AppConfig.AgentDir;
        var writesProjectPackageConfig = options.Command is "install" or "remove" && options.Local;
        var settingsManager = await CreateCommandSettingsManagerAsync(cwd, agentDir, options.ProjectTrustOverride, useSavedProjectTrustOnly: options.Command == "update");
        if (!settingsManager.IsProjectTrusted && writesProjectPackageConfig)
        {
            Console.Error.WriteLine(Chalk.Red("Project is not trusted. Use --approve to modify local package config."));
            return 1;
        }
        ReportSettingsErrors(settingsManager, "package command");

        var packageManager = new PackageManager(cwd, agentDir, settingsManager);
        packageManager.SetProgressCallback(evt =>
        {
            if (evt.Type == "start") Console.Out.Write(Chalk.Dim($"{evt.Message}\n"));
        });

        try
        {
            switch (options.Command)
            {
                case "install":
                    await packageManager.InstallAndPersistAsync(source!, options.Local);
                    await settingsManager.FlushAsync();
                    Console.WriteLine(Chalk.Green($"Installed {source}"));
                    return 0;

                case "remove":
                {
                    var removed = await packageManager.RemoveAndPersistAsync(source!, options.Local);
                    await settingsManager.FlushAsync();
                    if (!removed)
                    {
                        Console.Error.WriteLine(Chalk.Red($"No matching package found for {source}"));
                        return 1;
                    }
                    Console.WriteLine(Chalk.Green($"Removed {source}"));
                    return 0;
                }

                case "list":
                {
                    var configured = packageManager.ListConfiguredPackages();
                    var userPackages = configured.Where(p => p.Scope == "user").ToList();
                    var projectPackages = configured.Where(p => p.Scope == "project").ToList();
                    if (configured.Count == 0)
                    {
                        Console.WriteLine(Chalk.Dim("No packages installed."));
                        return 0;
                    }
                    void Format(ConfiguredPackage pkg)
                    {
                        Console.WriteLine($"  {(pkg.Filtered ? $"{pkg.Source} (filtered)" : pkg.Source)}");
                        if (pkg.InstalledPath is not null) Console.WriteLine(Chalk.Dim($"    {pkg.InstalledPath}"));
                    }
                    if (userPackages.Count > 0)
                    {
                        Console.WriteLine(Chalk.Bold("User packages:"));
                        foreach (var pkg in userPackages) Format(pkg);
                    }
                    if (projectPackages.Count > 0)
                    {
                        if (userPackages.Count > 0) Console.WriteLine();
                        Console.WriteLine(Chalk.Bold("Project packages:"));
                        foreach (var pkg in projectPackages) Format(pkg);
                    }
                    return 0;
                }

                default:
                {
                    if (options.ShowExtensionsSkippedNote) Console.WriteLine(Chalk.Dim($"Extensions are skipped. Run {App} update --extensions to update extensions."));
                    if (options.UpdateTarget is "all" or "extensions")
                    {
                        await packageManager.UpdateAsync(options.UpdateSource);
                        Console.WriteLine(Chalk.Green(options.UpdateSource is not null ? $"Updated {options.UpdateSource}" : "Updated packages"));
                    }
                    if (options.UpdateTarget is "all" or "self")
                    {
                        if (VersionCheck.LatestVersionUrl is null)
                        {
                            PrintSelfUpdateUnavailable();
                            return 1;
                        }
                        LatestRelease? latest;
                        try
                        {
                            latest = await VersionCheck.GetLatestReleaseAsync(AppConfig.Version, retry: true);
                        }
                        catch (Exception ex)
                        {
                            throw new InvalidOperationException($"Could not determine latest {App} version: {ex.Message}", ex);
                        }
                        if (latest is null) throw new InvalidOperationException($"Could not determine latest {App} version.");
                        if (!options.Force && !VersionCheck.IsNewerPackageVersion(latest.Version, AppConfig.Version))
                        {
                            Console.WriteLine(Chalk.Green($"{App} is already up to date (v{AppConfig.Version})"));
                            return 0;
                        }
                        if (latest.Note is not null) PrintSelfUpdateNote(latest.Note);
                        PrintSelfUpdateUnavailable();
                        return 1;
                    }
                    return 0;
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(Chalk.Red($"Error: {ex.Message}"));
            return 1;
        }
    }
}
