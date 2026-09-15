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
}
