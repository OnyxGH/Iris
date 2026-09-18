using Iris.Ai.Providers;

namespace Iris.Ai.Tests;

public class GoogleTests
{
    private static Model Gemini(string id, Dictionary<string, string?>? map) => new()
    {
        Id = id, Name = id, Api = "google-generative-ai", Provider = "google", BaseUrl = "https://example.com",
        Reasoning = true, Input = ["text"], ContextWindow = 1_000_000, MaxTokens = 65536, ThinkingLevelMap = map,
    };

    [Theory]
    [InlineData("gemini-3.1-pro-preview", true)]
    [InlineData("gemini-3.8-flash", true)]
    [InlineData("gemini-flash-lite-latest", true)]
    [InlineData("gemma4-31b-it", true)]
    [InlineData("gemini-2.5-pro", false)]
    public void Selects_thinking_level_wire_format(string id, bool expected) =>
        Assert.Equal(expected, GoogleShared.UsesThinkingLevel(Gemini(id, null)));

    [Fact]
    public void Disabled_thinking_uses_lowest_supported_level()
    {
        var pro = Gemini("gemini-3.1-pro-preview", new() { ["off"] = null, ["minimal"] = null, ["low"] = "LOW", ["medium"] = null, ["high"] = "HIGH" });
        Assert.Equal("LOW", GoogleShared.DisabledThinkingConfig(pro)["thinkingLevel"]!.GetValue<string>());

        var flash = Gemini("gemini-3.5-flash", new() { ["off"] = null });
        Assert.Equal("MINIMAL", GoogleShared.DisabledThinkingConfig(flash)["thinkingLevel"]!.GetValue<string>());

        // A model without MINIMAL must not be sent it, even though its ID looks like a Flash model.
        var noMinimal = Gemini("gemini-3.9-flash", new() { ["off"] = null, ["minimal"] = null });
        Assert.Equal("LOW", GoogleShared.DisabledThinkingConfig(noMinimal)["thinkingLevel"]!.GetValue<string>());

        Assert.Equal(0, GoogleShared.DisabledThinkingConfig(Gemini("gemini-2.5-flash", null))["thinkingBudget"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(ThinkingLevel.Minimal, "LOW")]
    [InlineData(ThinkingLevel.Medium, "HIGH")]
    [InlineData(ThinkingLevel.High, "HIGH")]
    public void Enabled_levels_follow_the_model_map(ThinkingLevel requested, string expected)
    {
        var pro = Gemini("gemini-3.1-pro-preview", new() { ["off"] = null, ["minimal"] = null, ["low"] = "LOW", ["medium"] = null, ["high"] = "HIGH" });
        var clamped = ModelUtils.ClampThinkingLevel(pro, requested);
        Assert.Equal(expected, GoogleShared.ToThinkingLevel(GoogleShared.ResolveThinkingLevel(pro, clamped)));
    }
}
