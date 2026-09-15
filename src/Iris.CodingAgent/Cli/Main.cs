using System.Text;
using Iris.Ai;
using Iris.Ai.Utils;
using Iris.CodingAgent.Config;
using Iris.CodingAgent.Core;
using Iris.CodingAgent.Modes.Interactive;
using Iris.CodingAgent.Modes;
using Iris.CodingAgent.Utils;

namespace Iris.CodingAgent.Cli;

/// <summary>
/// CLI entry point. Port of main.ts. Supported so far: interactive, print (-p), json and rpc modes, session selection
/// flags (including the --resume picker), model/tool options, --list-models, --help, --version. --export and
/// package/auth/config subcommands are not ported yet.
/// </summary>
public static class Main
{
    private const string ExtensionLoadFailureHint = "Hint: Start without extensions using \"{0} -ne\".";

    private static bool IsTruthyEnvFlag(string? value) =>
        !string.IsNullOrEmpty(value) && (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("yes", StringComparison.OrdinalIgnoreCase));

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            return await RunCoreAsync(args);
        }
        catch (CliExitException exit)
        {
            if (!string.IsNullOrEmpty(exit.Message)) (exit.ExitCode == 0 ? Console.Out : Console.Error).WriteLine(exit.Message);
            return exit.ExitCode;
        }
    }

    private static void ReportDiagnostics(IEnumerable<AgentSessionRuntimeDiagnostic> diagnostics)
    {
        foreach (var d in diagnostics)
        {
            var line = d.Type switch
            {
                "error" => Chalk.Red($"Error: {d.Message}"),
                "warning" => Chalk.Yellow($"Warning: {d.Message}"),
                _ => Chalk.Dim(d.Message),
            };
            Console.Error.WriteLine(line);
        }
    }

    /// <summary>"interactive" | "print" | "json" | "rpc".</summary>
    private static string ResolveAppMode(CliArgs parsed, bool stdinIsTty, bool stdoutIsTty)
    {
        if (parsed.Mode == "rpc") return "rpc";
        if (parsed.Mode == "json") return "json";
        if (parsed.Print || !stdinIsTty || !stdoutIsTty) return "print";
        return "interactive";
    }

    private static async Task<string?> ReadPipedStdinAsync()
    {
        if (!Console.IsInputRedirected) return null;
        using var reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var data = await reader.ReadToEndAsync();
        var trimmed = data.Trim();
        return trimmed.Length > 0 ? trimmed : null;
    }

    private sealed record ResolvedSession(string Type, string? Path = null, string? Cwd = null, string? Arg = null);

    private static async Task<ResolvedSession> ResolveSessionPathAsync(string sessionArg, string cwd, string? sessionDir)
    {
        if (sessionArg.Contains('/') || sessionArg.Contains('\\') || sessionArg.EndsWith(".jsonl", StringComparison.Ordinal))
        {
            return new ResolvedSession("path", PathUtils.ResolvePath(sessionArg, cwd));
        }

        var local = await SessionManager.ListAsync(cwd, sessionDir);
        var localMatch = local.FirstOrDefault(s => s.Id == sessionArg) ?? local.FirstOrDefault(s => s.Id.StartsWith(sessionArg, StringComparison.Ordinal));
        if (localMatch is not null) return new ResolvedSession("local", localMatch.Path);

        var all = await SessionManager.ListAllAsync(sessionDir);
        var globalMatch = all.FirstOrDefault(s => s.Id == sessionArg) ?? all.FirstOrDefault(s => s.Id.StartsWith(sessionArg, StringComparison.Ordinal));
        if (globalMatch is not null) return new ResolvedSession("global", globalMatch.Path, globalMatch.Cwd);

        return new ResolvedSession("not_found", Arg: sessionArg);
    }

    private static bool PromptConfirm(string message)
    {
        if (Console.IsInputRedirected) return false;
        Console.Write($"{message} [y/N] ");
        var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
        return answer is "y" or "yes";
    }

    private static void ValidateForkFlags(CliArgs parsed)
    {
        if (parsed.Fork is null) return;
        var conflicts = new[]
        {
            parsed.Session is not null ? "--session" : null,
            parsed.Continue ? "--continue" : null,
            parsed.Resume ? "--resume" : null,
            parsed.NoSession ? "--no-session" : null,
        }.OfType<string>().ToList();
        if (conflicts.Count > 0) throw new CliExitException(1, Chalk.Red($"Error: --fork cannot be combined with {string.Join(", ", conflicts)}"));
    }

    private static void ValidateSessionIdFlags(CliArgs parsed)
    {
        if (parsed.SessionId is null) return;
        var conflicts = new[]
        {
            parsed.Session is not null ? "--session" : null,
            parsed.Continue ? "--continue" : null,
            parsed.Resume ? "--resume" : null,
        }.OfType<string>().ToList();
        if (conflicts.Count > 0) throw new CliExitException(1, Chalk.Red($"Error: --session-id cannot be combined with {string.Join(", ", conflicts)}"));
        try
        {
            SessionManager.AssertValidSessionId(parsed.SessionId);
        }
        catch (Exception ex)
        {
            throw new CliExitException(1, Chalk.Red($"Error: {ex.Message}"));
        }
    }

    private static SessionManager OrExit(Func<SessionManager> create)
    {
        try
        {
            return create();
        }
        catch (Exception ex) when (ex is not CliExitException)
        {
            throw new CliExitException(1, Chalk.Red($"Error: {ex.Message}"));
        }
    }

    public static async Task<SessionManager> CreateSessionManagerAsync(CliArgs parsed, string cwd, string? sessionDir, SettingsManager? settingsManager = null)
    {
        if (parsed.NoSession || parsed.Help || parsed.ListModels is not null)
        {
            return SessionManager.InMemory(cwd, parsed.SessionId is not null ? new NewSessionOptions { Id = parsed.SessionId } : null);
        }

        if (parsed.Fork is not null)
        {
            if (parsed.SessionId is not null && (await SessionManager.ListAsync(cwd, sessionDir)).Any(s => s.Id == parsed.SessionId))
            {
                throw new CliExitException(1, Chalk.Red($"Session already exists with id '{parsed.SessionId}'"));
            }
            var resolved = await ResolveSessionPathAsync(parsed.Fork, cwd, sessionDir);
            if (resolved.Type == "not_found") throw new CliExitException(1, Chalk.Red($"No session found matching '{resolved.Arg}'"));
            return OrExit(() => SessionManager.ForkFrom(resolved.Path!, cwd, sessionDir, new NewSessionOptions { Id = parsed.SessionId }));
        }

        if (parsed.Session is not null)
        {
            var resolved = await ResolveSessionPathAsync(parsed.Session, cwd, sessionDir);
            switch (resolved.Type)
            {
                case "path" or "local":
                    return OrExit(() => SessionManager.Open(resolved.Path!, sessionDir));
                case "global":
                    Console.WriteLine(Chalk.Yellow($"Session found in different project: {resolved.Cwd}"));
                    if (!PromptConfirm("Fork this session into current directory?")) throw new CliExitException(0, Chalk.Dim("Aborted."));
                    return OrExit(() => SessionManager.ForkFrom(resolved.Path!, cwd, sessionDir));
                default:
                    throw new CliExitException(1, Chalk.Red($"No session found matching '{resolved.Arg}'"));
            }
        }

        if (parsed.Resume)
        {
            var selectedPath = await StartupUi.SelectSessionAsync(
                onProgress => SessionManager.ListAsync(cwd, sessionDir, onProgress),
                onProgress => SessionManager.ListAllAsync(sessionDir, onProgress),
                settingsManager ?? SettingsManager.Create(cwd, AppConfig.AgentDir));
            if (selectedPath is null) throw new CliExitException(0, Chalk.Dim("No session selected"));
            return SessionManager.Open(selectedPath, sessionDir);
        }

        if (parsed.Continue) return SessionManager.ContinueRecent(cwd, sessionDir);

        if (parsed.SessionId is not null)
        {
            var existing = (await SessionManager.ListAsync(cwd, sessionDir)).FirstOrDefault(s => s.Id == parsed.SessionId);
            if (existing is not null) return SessionManager.Open(existing.Path, sessionDir);
            Console.Error.WriteLine(Chalk.Yellow($"Warning: No project session found with id '{parsed.SessionId}'; creating a new session with that id."));
        }

        return SessionManager.Create(cwd, sessionDir, new NewSessionOptions { Id = parsed.SessionId });
    }

    private sealed record SessionOptions(Model? Model, ThinkingLevel? ThinkingLevel, List<ScopedModel>? ScopedModels, string? NoTools, List<string>? Tools, List<string>? ExcludeTools);

    private static (SessionOptions Options, bool CliThinkingFromModel, List<AgentSessionRuntimeDiagnostic> Diagnostics) BuildSessionOptions(
        CliArgs parsed, List<ScopedModel> scopedModels, bool hasExistingSession, ModelRuntime modelRuntime, SettingsManager settingsManager)
    {
        var diagnostics = new List<AgentSessionRuntimeDiagnostic>();
        Model? model = null;
        ThinkingLevel? thinkingLevel = null;
        var cliThinkingFromModel = false;

        if (parsed.Model is not null)
        {
            var resolved = ModelResolver.ResolveCliModel(parsed.Provider, parsed.Model, parsed.Thinking, modelRuntime);
            if (resolved.Warning is not null) diagnostics.Add(new("warning", resolved.Warning));
            if (resolved.Error is not null) diagnostics.Add(new("error", resolved.Error));
            if (resolved.Model is not null)
            {
                model = resolved.Model;
                // "--model <pattern>:<thinking>" shorthand; explicit --thinking still wins below.
                if (parsed.Thinking is null && resolved.ThinkingLevel is { } level)
                {
                    thinkingLevel = level;
                    cliThinkingFromModel = true;
                }
            }
        }

        if (model is null && scopedModels.Count > 0 && !hasExistingSession)
        {
            var savedModel = settingsManager.DefaultProvider is { } p && settingsManager.DefaultModel is { } m ? modelRuntime.GetModel(p, m) : null;
            var savedInScope = savedModel is null ? null : scopedModels.FirstOrDefault(s => ModelUtils.ModelsAreEqual(s.Model, savedModel));
            var chosen = savedInScope ?? scopedModels[0];
            model = chosen.Model;
            if (parsed.Thinking is null && chosen.ThinkingLevel is { } scopedLevel) thinkingLevel = scopedLevel;
        }

        if (parsed.Thinking is { } explicitLevel) thinkingLevel = explicitLevel;

        var noTools = parsed.NoTools ? "all" : parsed.NoBuiltinTools ? "builtin" : null;
        return (new SessionOptions(model, thinkingLevel, scopedModels.Count > 0 ? scopedModels : null, noTools, parsed.Tools, parsed.ExcludeTools), cliThinkingFromModel, diagnostics);
    }

    private static List<string>? ResolveCliPaths(string cwd, List<string>? paths) =>
        paths?.Select(value => PathUtils.IsLocalPath(value) ? PathUtils.ResolvePath(value, cwd) : value).ToList();

    private static async Task<int> RunCoreAsync(string[] args)
    {
        CodingAgentMessages.Register();

        var offlineMode = args.Contains("--offline") || IsTruthyEnvFlag(Environment.GetEnvironmentVariable("PI_OFFLINE"));
        if (offlineMode)
        {
            Environment.SetEnvironmentVariable("PI_OFFLINE", "1");
            Environment.SetEnvironmentVariable("PI_SKIP_VERSION_CHECK", "1");
        }

        if (await AuthCommand.RunAsync(args) is { } authExitCode) return authExitCode;

        var cwd = Directory.GetCurrentDirectory();
        var agentDir = AppConfig.AgentDir;
        var bootstrapSettingsManager = SettingsManager.Create(cwd, agentDir, projectTrusted: false);
        EnvHttpProxy.ApplyHttpProxySetting(Iris.Ai.Json.PiJson.GetString(bootstrapSettingsManager.GetGlobalSettings()["httpProxy"]));

        if (await ConfigCommand.RunAsync(args) is { } configExitCode) return configExitCode;

        var parsed = CliArgs.Parse(args);
        foreach (var d in parsed.Diagnostics)
        {
            Console.Error.WriteLine(d.Type == "error" ? Chalk.Red($"Error: {d.Message}") : Chalk.Yellow($"Warning: {d.Message}"));
        }
        if (parsed.Diagnostics.Any(d => d.Type == "error")) return 1;

        if (parsed.Version)
        {
            Console.WriteLine(AppConfig.Version);
            return 0;
        }

        if (parsed.Export is not null)
        {
            Console.Error.WriteLine(Chalk.Red("Error: --export is not available in Iris yet"));
            return 1;
        }

        var appMode = ResolveAppMode(parsed, !Console.IsInputRedirected, !Console.IsOutputRedirected);
        if (parsed.Mode == "rpc" && parsed.FileArgs.Count > 0)
        {
            Console.Error.WriteLine(Chalk.Red("Error: @file arguments are not supported in RPC mode"));
            return 1;
        }

        ValidateForkFlags(parsed);
        ValidateSessionIdFlags(parsed);

        var startupSettingsManager = SettingsManager.Create(cwd, agentDir);

        var envSessionDir = Environment.GetEnvironmentVariable(AppConfig.EnvSessionDir);
        var sessionDir = (parsed.SessionDir is not null ? PathUtils.NormalizePath(parsed.SessionDir) : null)
            ?? (!string.IsNullOrEmpty(envSessionDir) ? AppConfig.ExpandTildePath(envSessionDir) : null)
            ?? startupSettingsManager.SessionDir;

        var sessionManager = await CreateSessionManagerAsync(parsed, cwd, sessionDir, startupSettingsManager);
        if (SessionCwdIssue.Find(sessionManager, cwd) is { } missingCwd)
        {
            Console.Error.WriteLine(Chalk.Red(missingCwd.ErrorMessage));
            return 1;
        }

        if (parsed.Name is not null)
        {
            var name = CliArgs.NormalizeSessionName(parsed.Name);
            if (name is null)
            {
                Console.Error.WriteLine(Chalk.Red("Error: --name requires a non-empty value"));
                return 1;
            }
            sessionManager.AppendSessionInfo(name);
        }

        var trustStore = new ProjectTrustStore(agentDir);
        var sessionCwd = sessionManager.Cwd;
        var autoTrustOnReloadCwd = parsed.ProjectTrustOverride is null && !ProjectTrustStore.HasTrustRequiringProjectResources(sessionCwd) ? sessionCwd : null;
        // Interactive startup prompts with a standalone TUI; once interactive mode runs, it prompts inside its own UI.
        Func<string, List<ProjectTrustOption>, Task<ProjectTrustOption?>>? trustPrompt =
            appMode == "interactive" && !parsed.Help && parsed.ListModels is null ? StartupUi.CreateTrustPrompt(startupSettingsManager) : null;
        var projectTrustByCwd = new Dictionary<string, bool>();
        var resolvedExtensionPaths = ResolveCliPaths(cwd, parsed.Extensions);
        var resolvedSkillPaths = ResolveCliPaths(cwd, parsed.Skills);
        var resolvedPromptTemplatePaths = ResolveCliPaths(cwd, parsed.PromptTemplates);
        var resolvedThemePaths = ResolveCliPaths(cwd, parsed.Themes);

        async Task<CreateAgentSessionRuntimeResult> CreateRuntime(RuntimeTarget target)
        {
            if (!projectTrustByCwd.TryGetValue(target.Cwd, out var projectTrusted))
            {
                // Without an interactive UI, untrusted projects with trust-requiring resources stay untrusted.
                projectTrusted = await trustStore.ResolveProjectTrustedAsync(target.Cwd, parsed.ProjectTrustOverride, startupSettingsManager.DefaultProjectTrust, trustPrompt);
                projectTrustByCwd[target.Cwd] = projectTrusted;
            }

            var runtimeSettingsManager = SettingsManager.Create(target.Cwd, target.AgentDir, projectTrusted);
            var services = await AgentSessionServicesFactory.CreateAsync(new AgentSessionServicesFactory.Options
            {
                Cwd = target.Cwd,
                AgentDir = target.AgentDir,
                SettingsManager = runtimeSettingsManager,
                ExtensionFlagValues = parsed.UnknownFlags,
                ResourceLoaderOptions = (loaderCwd, loaderAgentDir, settings) => new DefaultResourceLoaderOptions
                {
                    Cwd = loaderCwd,
                    AgentDir = loaderAgentDir,
                    SettingsManager = settings,
                    AdditionalExtensionPaths = resolvedExtensionPaths,
                    AdditionalSkillPaths = resolvedSkillPaths,
                    AdditionalPromptTemplatePaths = resolvedPromptTemplatePaths,
                    AdditionalThemePaths = resolvedThemePaths,
                    NoExtensions = parsed.NoExtensions,
                    NoSkills = parsed.NoSkills,
                    NoPromptTemplates = parsed.NoPromptTemplates,
                    NoThemes = parsed.NoThemes,
                    NoContextFiles = parsed.NoContextFiles,
                    SystemPrompt = parsed.SystemPrompt,
                    AppendSystemPrompt = parsed.AppendSystemPrompt,
                },
            });

            var diagnostics = new List<AgentSessionRuntimeDiagnostic>(services.Diagnostics);
            var modelPatterns = parsed.Models ?? services.SettingsManager.EnabledModels;
            using var scopeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var scopedModels = modelPatterns is { Count: > 0 }
                ? await ModelResolver.ResolveModelScopeAsync(modelPatterns, services.ModelRuntime, scopeTimeout.Token)
                : [];

            var (options, cliThinkingFromModel, optionDiagnostics) = BuildSessionOptions(parsed, scopedModels,
                target.SessionManager.BuildSessionContext().Messages.Count > 0, services.ModelRuntime, services.SettingsManager);
            diagnostics.AddRange(optionDiagnostics);

            if (parsed.ApiKey is not null)
            {
                if (options.Model is null) diagnostics.Add(new("error", "--api-key requires a model to be specified via --model, --provider/--model, or --models"));
                else await services.ModelRuntime.SetRuntimeApiKeyAsync(options.Model.Provider, parsed.ApiKey);
            }

            var created = await Sdk.CreateAgentSessionAsync(new CreateAgentSessionOptions
            {
                Cwd = services.Cwd,
                AgentDir = services.AgentDir,
                ModelRuntime = services.ModelRuntime,
                SettingsManager = services.SettingsManager,
                ResourceLoader = services.ResourceLoader,
                SessionManager = target.SessionManager,
                Model = options.Model,
                ThinkingLevel = options.ThinkingLevel,
                ScopedModels = options.ScopedModels,
                Tools = options.Tools,
                ExcludeTools = options.ExcludeTools,
                NoTools = options.NoTools,
                SessionStartReason = target.SessionStartReason ?? "startup",
            });

            if (created.Session.Model is not null && (parsed.Thinking is not null || cliThinkingFromModel))
            {
                created.Session.SetThinkingLevel(created.Session.ThinkingLevel);
            }
            return new CreateAgentSessionRuntimeResult(created.Session, services, diagnostics, created.ModelFallbackMessage);
        }

        var runtime = await AgentSessionRuntime.CreateAsync(CreateRuntime, new RuntimeTarget(sessionManager.Cwd, agentDir, sessionManager));
        var session = runtime.Session;
        var services = runtime.Services;

        if (parsed.Help)
        {
            Console.WriteLine(CliArgs.HelpText());
            return 0;
        }

        if (parsed.ListModels is not null)
        {
            using var listTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await ModelLister.ListAsync(services.ModelRuntime, parsed.ListModels.Length > 0 ? parsed.ListModels : null, Console.Out, listTimeout.Token);
            return 0;
        }

        string? stdinContent = null;
        if (appMode != "rpc")
        {
            stdinContent = await ReadPipedStdinAsync();
            if (stdinContent is not null && appMode == "interactive") appMode = "print";
        }

        var (fileText, fileImages) = parsed.FileArgs.Count > 0
            ? await InitialMessageBuilder.ProcessFileArgumentsAsync(parsed.FileArgs, services.SettingsManager.ImageAutoResize)
            : ("", []);
        var (initialMessage, initialImages) = InitialMessageBuilder.Build(parsed, fileText, fileImages, stdinContent);

        var hasRuntimeErrors = runtime.Diagnostics.Any(d => d.Type == "error");
        if (appMode != "interactive" || hasRuntimeErrors) ReportDiagnostics(runtime.Diagnostics);
        if (hasRuntimeErrors)
        {
            if (runtime.Diagnostics.Any(d => d.Message.Contains("Failed to load extension"))) Console.Error.WriteLine(Chalk.Yellow(string.Format(ExtensionLoadFailureHint, AppConfig.AppName)));
            return 1;
        }

        if (appMode == "interactive")
        {
            return await StartupUi.RunOnUiThreadAsync(() =>
            {
                var interactiveMode = new InteractiveMode(runtime, new InteractiveModeOptions
                {
                    StartupDiagnostics = runtime.Diagnostics,
                    ModelFallbackMessage = runtime.ModelFallbackMessage,
                    AutoTrustOnReloadCwd = autoTrustOnReloadCwd,
                    InitialMessage = initialMessage,
                    InitialImages = initialImages,
                    InitialMessages = parsed.Messages,
                    Verbose = parsed.Verbose,
                    InitialThemeSetting = parsed.UseTheme,
                    TuiMode = parsed.TuiMode,
                });
                trustPrompt = interactiveMode.PromptProjectTrustAsync;
                return interactiveMode.RunAsync();
            });
        }
        if (appMode == "rpc")
        {
            var rpcStdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = false };
            var rpcStdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            if (!offlineMode)
            {
                _ = Task.Run(async () =>
                {
                    using var refreshTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    try
                    {
                        await services.ModelRuntime.RefreshAsync(new Iris.Ai.Models.ModelsRefreshOptions { CancellationToken = refreshTimeout.Token });
                    }
                    catch
                    {
                        // Background catalog refresh is best-effort.
                    }
                });
            }
            return await RpcMode.RunAsync(runtime, rpcStdin, rpcStdout);
        }

        if (session.Model is null)
        {
            Console.Error.WriteLine(Chalk.Red(AuthGuidance.FormatNoModelsAvailableMessage()));
            return 1;
        }

        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = false };
        return await PrintMode.RunAsync(runtime, appMode == "json" ? "json" : "text", parsed.Messages, initialMessage, initialImages, stdout);
    }
}
