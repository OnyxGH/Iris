using System.Text.Json.Nodes;
using PiSharp.CodingAgent.Config;
using PiSharp.CodingAgent.Core;

namespace PiSharp.CodingAgent.Tests;

/// <summary>Runs DefaultResourceLoader on the same tree as pi's loader (Fixtures/loader.json) and compares results.</summary>
public class ResourceLoaderFixtureTests
{
    private static readonly SemaphoreSlim HomeLock = new(1, 1);

    private static string? Str(JsonNode? node) => node?.GetValue<string>();

    [Fact]
    public async Task LoaderMatchesPi()
    {
        var raw = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "loader.json"));
        var piConfigDir = JsonNode.Parse(raw)!["configDirName"]!.GetValue<string>();
        var fixture = JsonNode.Parse(raw.Replace($"/{piConfigDir}/", $"/{AppConfig.ConfigDirName}/").Replace($"/{piConfigDir}\"", $"/{AppConfig.ConfigDirName}\""))!;

        var tree = Path.Combine(Path.GetTempPath(), "pisharp-loader-" + Guid.NewGuid().ToString("N")[..8]);
        foreach (var (relative, content) in fixture["files"]!.AsObject())
        {
            var full = Path.Combine(tree, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content!.GetValue<string>());
        }

        string Abs(string? rel) => Path.Combine(tree, rel!.Replace('/', Path.DirectorySeparatorChar));
        string? Rel(string? path) => path is null ? null : path.StartsWith(tree, StringComparison.Ordinal) ? Path.GetRelativePath(tree, path).Replace('\\', '/') : path;

        await HomeLock.WaitAsync();
        var previousHome = Environment.GetEnvironmentVariable("HOME");
        Environment.SetEnvironmentVariable("HOME", Path.Combine(tree, "home"));
        try
        {
            foreach (var testCase in fixture["cases"]!.AsArray())
            {
                var name = Str(testCase!["name"]);
                var o = testCase["options"]!;
                var cwd = Path.Combine(tree, "repo", "work");
                var agentDir = Path.Combine(tree, "agent");
                var loader = new DefaultResourceLoader(new DefaultResourceLoaderOptions
                {
                    Cwd = cwd,
                    AgentDir = agentDir,
                    SettingsManager = SettingsManager.Create(cwd, agentDir),
                    NoSkills = o["noSkills"]?.GetValue<bool>() ?? false,
                    NoContextFiles = o["noContextFiles"]?.GetValue<bool>() ?? false,
                    AdditionalSkillPaths = o["additionalSkillPaths"]?.AsArray().Select(p => Abs(Str(p))).ToList(),
                    SystemPrompt = Str(o["systemPrompt"]),
                    AppendSystemPrompt = o["appendSystemPrompt"]?.AsArray().Select(p => Abs(Str(p))).ToList(),
                });
                await loader.ReloadAsync();
                var expected = testCase["result"]!;

                var (skills, skillDiagnostics) = loader.GetSkills();
                var expectedSkills = expected["skills"]!.AsArray();
                Assert.True(expectedSkills.Select(s => Str(s!["name"])).SequenceEqual(skills.Select(s => s.Name)),
                    $"{name}: skills {string.Join(",", skills.Select(s => s.Name))}");
                for (var i = 0; i < expectedSkills.Count; i++)
                {
                    var e = expectedSkills[i]!;
                    Assert.Equal(Str(e["filePath"]), Rel(skills[i].FilePath));
                    AssertSourceInfo(e["sourceInfo"]!, skills[i].SourceInfo, Rel, $"{name}: skill {skills[i].Name}");
                }
                AssertDiagnostics(expected["skillDiagnostics"]!.AsArray(), skillDiagnostics, Rel, $"{name}: skill diagnostics");

                var (prompts, promptDiagnostics) = loader.GetPrompts();
                var expectedPrompts = expected["prompts"]!.AsArray();
                Assert.True(expectedPrompts.Select(p => Str(p!["name"])).SequenceEqual(prompts.Select(p => p.Name)),
                    $"{name}: prompts {string.Join(",", prompts.Select(p => p.Name))}");
                for (var i = 0; i < expectedPrompts.Count; i++)
                {
                    var e = expectedPrompts[i]!;
                    Assert.Equal(Str(e["filePath"]), Rel(prompts[i].FilePath));
                    Assert.Equal(Str(e["content"]), prompts[i].Content);
                    AssertSourceInfo(e["sourceInfo"]!, prompts[i].SourceInfo, Rel, $"{name}: prompt {prompts[i].Name}");
                }
                AssertDiagnostics(expected["promptDiagnostics"]!.AsArray(), promptDiagnostics, Rel, $"{name}: prompt diagnostics");

                // Themes are not compared: pi validates theme JSON in its TUI layer, which is not ported yet.

                var agentsFiles = loader.GetAgentsFiles().Where(f => f.Path.StartsWith(tree, StringComparison.Ordinal)).ToList();
                Assert.Equal(expected["agentsFiles"]!.AsArray().Select(f => $"{Str(f!["path"])}={Str(f["content"])}"),
                    agentsFiles.Select(f => $"{Rel(f.Path)}={f.Content}"));

                Assert.Equal(Str(expected["systemPrompt"]), loader.GetSystemPrompt());
                Assert.Equal(Str(expected["systemPromptSource"]), Rel(loader.GetSystemPromptSource()));
                Assert.Equal(expected["appendSystemPrompt"]!.AsArray().Select(Str), loader.GetAppendSystemPrompt());
                Assert.Equal(expected["appendSources"]!.AsArray().Select(Str), loader.GetAppendSystemPromptSources().Select(Rel));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", previousHome);
            HomeLock.Release();
            try
            {
                Directory.Delete(tree, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static void AssertSourceInfo(JsonNode expected, SourceInfo actual, Func<string?, string?> rel, string context)
    {
        Assert.True(Str(expected["source"]) == actual.Source, $"{context}: source {actual.Source}");
        Assert.True(Str(expected["scope"]) == actual.Scope, $"{context}: scope {actual.Scope}");
        Assert.True(Str(expected["origin"]) == actual.Origin, $"{context}: origin {actual.Origin}");
        Assert.True(Str(expected["baseDir"]) == rel(actual.BaseDir), $"{context}: baseDir {rel(actual.BaseDir)}");
    }

    private static void AssertDiagnostics(JsonArray expected, IReadOnlyList<ResourceDiagnostic> actual, Func<string?, string?> rel, string context)
    {
        Assert.True(expected.Count == actual.Count, $"{context}: {string.Join(" | ", actual.Select(d => $"{d.Type} {d.Message} {rel(d.Path)}"))}");
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(Str(expected[i]!["type"]), actual[i].Type);
            Assert.Equal(Str(expected[i]!["message"]), actual[i].Message);
            Assert.Equal(Str(expected[i]!["path"]), rel(actual[i].Path));
        }
    }
}
