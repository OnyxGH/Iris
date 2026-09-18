using System.Diagnostics;
using Iris.CodingAgent.Config;
using Iris.CodingAgent.Utils;

namespace Iris.CodingAgent.Cli;

/// <summary>`update`: update Iris through `dotnet tool update` when it runs as an installed .NET tool.</summary>
public static class UpdateCommand
{
    private static string App => AppConfig.AppName;

    /// <summary>How this copy of Iris was installed as a .NET tool: global, or with --tool-path.</summary>
    public sealed record ToolInstall(bool Global, string ToolPath);

    /// <summary>
    /// Detect a .NET tool install from the application directory, which looks like
    /// &lt;tool path&gt;/.store/&lt;package id&gt;/&lt;version&gt;/&lt;package id&gt;/&lt;version&gt;/tools/&lt;tfm&gt;/any/.
    /// </summary>
    public static ToolInstall? DetectToolInstall(string baseDirectory, string? homeDir = null)
    {
        var parts = Path.GetFullPath(baseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var storeIndex = Array.FindLastIndex(parts, p => p == ".store");
        if (storeIndex < 1 || storeIndex + 1 >= parts.Length || !parts[storeIndex + 1].Equals(AppConfig.PackageId, StringComparison.OrdinalIgnoreCase)) return null;
        var toolPath = string.Join(Path.DirectorySeparatorChar, parts[..storeIndex]);
        if (toolPath.Length == 0 || toolPath.EndsWith(':')) toolPath += Path.DirectorySeparatorChar;
        var globalPath = Path.Combine(homeDir ?? AppConfig.HomeDir, ".dotnet", "tools");
        var global = string.Equals(Path.GetFullPath(toolPath).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(globalPath).TrimEnd(Path.DirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        return new ToolInstall(global, toolPath);
    }

    public static string UpdateCommandLine(ToolInstall? install) => install is { Global: false }
        ? $"dotnet tool update {AppConfig.PackageId} --tool-path \"{install.ToolPath}\""
        : $"dotnet tool update -g {AppConfig.PackageId}";

    /// <summary>PowerShell script that waits for Iris to exit, runs `dotnet toolArgs` and writes its output to logPath.</summary>
    public static string WindowsUpdateScript(int processId, IEnumerable<string> toolArgs, string logPath)
    {
        static string Quote(string value) => $"'{value.Replace("'", "''")}'";
        return $"Wait-Process -Id {processId} -ErrorAction SilentlyContinue; "
            + $"& dotnet {string.Join(" ", toolArgs.Select(Quote))} 2>&1 | ForEach-Object {{ \"$_\" }} | Set-Content -Encoding UTF8 -LiteralPath {Quote(logPath)}; "
            + "exit $LASTEXITCODE";
    }

    private static void PrintHelp() => Console.WriteLine($"""
        {Chalk.Bold("Usage:")}
          {App} update [--check]

        Update Iris to the latest version published on NuGet ({AppConfig.PackageId}).

        Options:
          --check     Only report whether a newer version is available
          -h, --help  Show this help
        """.Replace("\r\n", "\n"));

    /// <summary>Handle `update`; returns null when the arguments are not the update command, otherwise the exit code.</summary>
    public static async Task<int?> RunAsync(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0] != "update") return null;
        var checkOnly = false;
        foreach (var arg in args.Skip(1))
        {
            switch (arg)
            {
                case "-h" or "--help":
                    PrintHelp();
                    return 0;
                case "--check":
                    checkOnly = true;
                    break;
                default:
                    Console.Error.WriteLine(Chalk.Red($"Unknown argument {arg} for \"update\"."));
                    Console.Error.WriteLine(Chalk.Dim($"Usage: {App} update [--check]"));
                    return 1;
            }
        }

        var current = AppConfig.Version;
        var install = DetectToolInstall(AppContext.BaseDirectory);
        LatestRelease? latest;
        try
        {
            latest = await VersionCheck.GetLatestReleaseAsync(current, retry: true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(Chalk.Red($"Could not check for updates: {ex.Message}"));
            return 1;
        }
        if (latest is null || !VersionCheck.IsNewerPackageVersion(latest.Version, current))
        {
            Console.WriteLine($"{App} {current} is up to date.");
            return 0;
        }
        Console.WriteLine($"New version {latest.Version} is available (current: {current}).");
        if (checkOnly) return 0;

        var command = UpdateCommandLine(install);
        if (install is null)
        {
            Console.WriteLine($"This copy of {App} was not installed as a .NET tool. To install it as one, run:");
            Console.WriteLine($"  dotnet tool install -g {AppConfig.PackageId}");
            return 1;
        }

        Console.WriteLine(Chalk.Dim(command));
        List<string> toolArgs = install.Global
            ? ["tool", "update", "-g", AppConfig.PackageId]
            : ["tool", "update", AppConfig.PackageId, "--tool-path", install.ToolPath];

        if (OperatingSystem.IsWindows())
        {
            // The running iris.exe locks its version folder, so the update has to run after this process exits. The
            // helper gets its own hidden console: sharing ours would print dotnet's output over the shell's next prompt.
            var logPath = Path.Combine(AppConfig.AgentDir, "update.log");
            var helper = new ProcessStartInfo("powershell") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command", WindowsUpdateScript(Environment.ProcessId, toolArgs, logPath) }) helper.ArgumentList.Add(arg);
            try
            {
                Directory.CreateDirectory(AppConfig.AgentDir);
                Process.Start(helper);
                Console.WriteLine($"{App} will finish updating in the background once it exits. Run `{App} --version` to confirm.");
                Console.WriteLine(Chalk.Dim($"Output is written to {logPath}"));
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(Chalk.Red($"Could not start the updater: {ex.Message}"));
                Console.Error.WriteLine($"Run: {command}");
                return 1;
            }
        }

        var startInfo = new ProcessStartInfo("dotnet") { UseShellExecute = false };
        foreach (var arg in toolArgs) startInfo.ArgumentList.Add(arg);
        try
        {
            using var process = Process.Start(startInfo)!;
            await process.WaitForExitAsync();
            if (process.ExitCode == 0) return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(Chalk.Red($"Could not run dotnet: {ex.Message}"));
        }
        Console.Error.WriteLine($"Update failed. Close {App} and run: {command}");
        return 1;
    }
}
