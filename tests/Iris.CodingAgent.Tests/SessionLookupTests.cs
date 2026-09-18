using Iris.CodingAgent.Core;

namespace Iris.CodingAgent.Tests;

public class SessionLookupTests
{
    private static string WriteSession(string dir, string id, string cwd)
    {
        var path = Path.Combine(dir, $"2026-09-18T00-00-00-000Z_{id}.jsonl");
        var header = new System.Text.Json.Nodes.JsonObject
        {
            ["type"] = "session", ["version"] = SessionManager.CurrentSessionVersion, ["id"] = id, ["timestamp"] = "2026-09-18T00:00:00.000Z", ["cwd"] = cwd,
        };
        File.WriteAllText(path, header.ToJsonString() + "\n");
        return path;
    }

    [Fact]
    public void FindsExactSessionIdsInCustomSessionDirForTheCurrentProject()
    {
        using var sessions = new TempDir();
        using var project = new TempDir();
        using var otherProject = new TempDir();
        var match = WriteSession(sessions.Path, "abc-123", project.Path);
        WriteSession(sessions.Path, "abc-1234", project.Path);
        WriteSession(sessions.Path, "other-project", otherProject.Path);
        File.WriteAllText(Path.Combine(sessions.Path, "broken.jsonl"), "not json\n");

        Assert.Equal(match, SessionManager.FindById(project.Path, "abc-123", sessions.Path));
        Assert.Null(SessionManager.FindById(project.Path, "abc", sessions.Path));
        Assert.Null(SessionManager.FindById(project.Path, "other-project", sessions.Path));
        Assert.Null(SessionManager.FindById(project.Path, "abc-123", Path.Combine(sessions.Path, "missing")));
    }
}
