using System.Text.Json.Nodes;
using Iris.CodingAgent.Core;

namespace Iris.CodingAgent.Tests;

/// <summary>Theme JSON validation messages compared with pi's typebox validator (fixtures generated from the installed pi).</summary>
public class ThemeValidationTests
{
    [Fact]
    public void ValidationMessagesMatchPi()
    {
        var cases = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "theme-validation.json")))!.AsArray();
        Assert.NotEmpty(cases);
        foreach (var testCase in cases)
        {
            var name = testCase!["name"]!.GetValue<string>();
            var expected = testCase["error"]?.GetValue<string>();
            string? actual = null;
            try
            {
                ThemeJsonValidator.Validate(name, testCase["json"]?.DeepClone());
            }
            catch (InvalidDataException ex)
            {
                actual = ex.Message;
            }
            Assert.True(expected == actual, $"case {name}\nexpected:\n{expected}\nactual:\n{actual}");
        }
    }
}
