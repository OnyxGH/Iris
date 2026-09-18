using Iris.CodingAgent.Cli;
using Iris.CodingAgent.Utils;

namespace Iris.CodingAgent.Tests;

public class UpdateTests
{
    [Fact]
    public void SelectsHighestStableVersion()
    {
        string[] versions = ["0.85.0", "0.86.0-beta.1", "0.85.2", "not-a-version", "0.85.10"];
        Assert.Equal("0.85.10", VersionCheck.SelectLatestVersion(versions, "0.85.1"));
        Assert.Equal("0.86.0-beta.1", VersionCheck.SelectLatestVersion(versions, "0.86.0-alpha.1"));
        Assert.Null(VersionCheck.SelectLatestVersion([], "0.85.1"));
    }

    [Fact]
    public void DetectsGlobalAndToolPathInstalls()
    {
        var home = Path.Combine(Path.GetTempPath(), "iris-home");
        var global = Path.Combine(home, ".dotnet", "tools", ".store", "iris-agent", "1.0.0", "iris-agent.win-x64", "1.0.0", "tools", "net10.0", "win-x64");
        var detected = UpdateCommand.DetectToolInstall(global, home);
        Assert.NotNull(detected);
        Assert.True(detected.Global);

        var custom = Path.Combine(Path.GetTempPath(), "custom-tools");
        var local = UpdateCommand.DetectToolInstall(Path.Combine(custom, ".store", "iris-agent", "1.0.0", "iris-agent", "1.0.0", "tools", "net10.0", "any"), home);
        Assert.NotNull(local);
        Assert.False(local.Global);
        Assert.Equal(Path.GetFullPath(custom), Path.GetFullPath(local.ToolPath));

        Assert.Null(UpdateCommand.DetectToolInstall(Path.Combine(custom, "bin", "Debug", "net10.0"), home));
        Assert.Null(UpdateCommand.DetectToolInstall(Path.Combine(custom, ".store", "other-tool", "1.0.0"), home));
    }

    [Theory]
    [InlineData("--version", 0)]
    [InlineData("no-such-dotnet-command", 1)]
    public void WindowsUpdateScriptLogsOutputAndExitCode(string dotnetArg, int expectedExit)
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir = Path.Combine(Path.GetTempPath(), "iris-update-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "it's update.log");
        try
        {
            // An already exited process id: Wait-Process returns immediately.
            using var exited = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd", "/c exit") { CreateNoWindow = true })!;
            exited.WaitForExit();
            var start = new System.Diagnostics.ProcessStartInfo("powershell") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command", UpdateCommand.WindowsUpdateScript(exited.Id, [dotnetArg], log) }) start.ArgumentList.Add(arg);
            using var helper = System.Diagnostics.Process.Start(start)!;
            var console = helper.StandardOutput.ReadToEnd();
            helper.WaitForExit();

            Assert.Equal("", console);
            Assert.Equal(expectedExit, Math.Min(helper.ExitCode, 1));
            Assert.NotEmpty(File.ReadAllText(log).Trim());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
