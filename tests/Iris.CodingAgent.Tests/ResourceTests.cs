using System.Text.Json.Nodes;
using Iris.CodingAgent.Config;
using Iris.CodingAgent.Core;

namespace Iris.CodingAgent.Tests;

/// <summary>Compares skills, prompt templates and system prompt output with fixtures generated from the installed pi.</summary>
public class ResourceFixtureTests : IDisposable
{
    private readonly JsonNode _fixture;
    private readonly string _tree = Path.Combine(Path.GetTempPath(), "iris-res-" + Guid.NewGuid().ToString("N")[..8]);

    public ResourceFixtureTests()
    {
        var piConfigDir = JsonNode.Parse(File.ReadAllText(FixturePath))!["configDirName"]!.GetValue<string>();
        // Fixture paths use pi's ".pi" project dir; Iris uses ".iris".
        var text = File.ReadAllText(FixturePath).Replace($"project/{piConfigDir}/", $"project/{AppConfig.ConfigDirName}/");
        _fixture = JsonNode.Parse(text)!;
        foreach (var (relative, content) in _fixture["files"]!.AsObject())
        {
            var full = Path.Combine(_tree, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content!.GetValue<string>());
        }
    }

    private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "resources.json");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tree, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Rel(string? path) => path is null ? "" : Path.GetRelativePath(_tree, path).Replace('\\', '/');

    private static string? Str(JsonNode? node) => node?.GetValue<string>();

    private LoadSkillsResult LoadSkills() =>
        Skills.LoadSkills(Path.Combine(_tree, "project"), Path.Combine(_tree, "agent"), [Path.Combine(_tree, "extra"), Path.Combine(_tree, "missing")], includeDefaults: true);

    [Fact]
    public void SkillsMatchPi()
    {
        var result = LoadSkills();
        var expectedSkills = _fixture["skills"]!["skills"]!.AsArray();
        Assert.Equal(expectedSkills.Select(s => Str(s!["name"])), result.Skills.Select(s => s.Name));
        for (var i = 0; i < expectedSkills.Count; i++)
        {
            var expected = expectedSkills[i]!;
            var actual = result.Skills[i];
            Assert.Equal(Str(expected["description"]), actual.Description);
            Assert.Equal(Str(expected["filePath"]), Rel(actual.FilePath));
            Assert.Equal(Str(expected["baseDir"]), Rel(actual.BaseDir));
            Assert.Equal(expected["disableModelInvocation"]!.GetValue<bool>(), actual.DisableModelInvocation);
            var info = expected["sourceInfo"]!;
            Assert.Equal(Str(info["source"]), actual.SourceInfo.Source);
            Assert.Equal(Str(info["scope"]), actual.SourceInfo.Scope);
            Assert.Equal(Str(info["origin"]), actual.SourceInfo.Origin);
            Assert.Equal(Str(info["baseDir"]), Rel(actual.SourceInfo.BaseDir));
        }

        var expectedDiagnostics = _fixture["skills"]!["diagnostics"]!.AsArray();
        Assert.Equal(expectedDiagnostics.Count, result.Diagnostics.Count);
        for (var i = 0; i < expectedDiagnostics.Count; i++)
        {
            var expected = expectedDiagnostics[i]!;
            var actual = result.Diagnostics[i];
            Assert.Equal(Str(expected["type"]), actual.Type);
            Assert.Equal(Str(expected["path"]), Rel(actual.Path));
            // YAML parser error text differs between the JS yaml package and YamlDotNet.
            if (!Rel(actual.Path).EndsWith("broken/SKILL.md")) Assert.Equal(Str(expected["message"]), actual.Message);
            if (expected["collision"] is { } collision)
            {
                Assert.Equal(Str(collision["winnerPath"]), Rel(actual.Collision!.WinnerPath));
                Assert.Equal(Str(collision["loserPath"]), Rel(actual.Collision.LoserPath));
            }
        }
    }

    [Fact]
    public void PromptTemplatesMatchPi()
    {
        var templates = PromptTemplates.LoadPromptTemplates(Path.Combine(_tree, "project"), Path.Combine(_tree, "agent"), [], includeDefaults: true);
        var expected = _fixture["templates"]!.AsArray();
        Assert.Equal(expected.Count, templates.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(Str(expected[i]!["name"]), templates[i].Name);
            Assert.Equal(Str(expected[i]!["description"]), templates[i].Description);
            Assert.Equal(Str(expected[i]!["argumentHint"]), templates[i].ArgumentHint);
            Assert.Equal(Str(expected[i]!["content"]), templates[i].Content);
            Assert.Equal(Str(expected[i]!["sourceInfo"]!["scope"]), templates[i].SourceInfo.Scope);
            Assert.Equal(Str(expected[i]!["sourceInfo"]!["baseDir"]), Rel(templates[i].SourceInfo.BaseDir));
        }

        foreach (var pair in _fixture["expand"]!.AsArray())
        {
            Assert.Equal(Str(pair![1]), PromptTemplates.ExpandPromptTemplate(Str(pair[0])!, templates));
        }
        foreach (var c in _fixture["substitute"]!.AsArray())
        {
            var args = c![1]!.AsArray().Select(a => a!.GetValue<string>()).ToList();
            Assert.Equal(Str(c[2]), PromptTemplates.SubstituteArgs(Str(c[0])!, args));
        }
        foreach (var c in _fixture["parseArgs"]!.AsArray())
        {
            Assert.Equal(c![1]!.AsArray().Select(a => a!.GetValue<string>()), PromptTemplates.ParseCommandArgs(Str(c[0])!));
        }
    }

    [Fact]
    public void SystemPromptMatchesPi()
    {
        var skills = LoadSkills().Skills;
        foreach (var c in _fixture["systemPrompts"]!.AsArray())
        {
            var o = c!["options"]!;
            var options = new BuildSystemPromptOptions
            {
                Cwd = Str(o["cwd"])!,
                CustomPrompt = Str(o["customPrompt"]),
                AppendSystemPrompt = Str(o["appendSystemPrompt"]),
                SelectedTools = o["selectedTools"]?.AsArray().Select(t => t!.GetValue<string>()).ToList(),
                ToolSnippets = o["toolSnippets"]?.AsObject().ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>()),
                PromptGuidelines = o["promptGuidelines"]?.AsArray().Select(t => t!.GetValue<string>()).ToList(),
                ContextFiles = o["contextFiles"]?.AsArray().Select(f => new ContextFile(Str(f!["path"])!, Str(f["content"])!)).ToList(),
                Skills = o["skills"] is null ? null : skills,
            };
            var actual = SystemPrompt.Build(options)
                .Replace(AppConfig.ReadmePath, "<README>")
                .Replace(AppConfig.DocsPath, "<DOCS>")
                .Replace(AppConfig.ExamplesPath, "<EXAMPLES>")
                .Replace(_tree, "<TREE>");
            Assert.Equal(Str(c["prompt"]), actual);
        }
    }
}
