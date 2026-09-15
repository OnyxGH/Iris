using System.Text.Json.Nodes;
using PiSharp.CodingAgent.Utils;

namespace PiSharp.CodingAgent.Tests;

/// <summary>Git package source parsing compared with pi's parseGitUrl + hosted-git-info (fixtures generated from the installed pi).</summary>
public class GitUrlTests
{
    [Fact]
    public void ParseMatchesPi()
    {
        var cases = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "git-urls.json")))!.AsArray();
        var failures = new List<string>();
        foreach (var testCase in cases)
        {
            var input = testCase!["input"]!.GetValue<string>();
            var expected = testCase["result"] as JsonObject;
            var actual = GitUrlParser.Parse(input);
            var expectedText = expected is null ? "null" : $"{expected["repo"]}|{expected["host"]}|{expected["path"]}|{expected["ref"]}|{expected["pinned"]}";
            var actualText = actual is null ? "null" : $"{actual.Repo}|{actual.Host}|{actual.Path}|{actual.Ref}|{actual.Pinned.ToString().ToLowerInvariant()}";
            if (expectedText != actualText) failures.Add($"{input}\n  expected {expectedText}\n  actual   {actualText}");
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}
