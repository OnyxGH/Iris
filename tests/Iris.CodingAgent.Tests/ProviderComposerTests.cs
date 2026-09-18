using System.Text.Json.Nodes;
using Iris.Ai;
using Iris.CodingAgent.Core;

namespace Iris.CodingAgent.Tests;

public class ProviderComposerTests
{
    private static Model Fable() => new()
    {
        Id = "claude-fable-5", Name = "Fable", Api = "anthropic-messages", Provider = "anthropic", BaseUrl = "https://api.anthropic.com",
        Input = ["text"], ContextWindow = 200000, MaxTokens = 64000,
        Compat = JsonNode.Parse("""{"allowedFallbackModels":[{"provider":"anthropic","model":"claude-opus-5","cost":{"input":5,"output":25,"cacheRead":0.5,"cacheWrite":6.25}}]}""")!.AsObject(),
    };

    [Fact]
    public void ModelOverrideReplacesAllowedFallbackModels()
    {
        var over = JsonNode.Parse("""{"compat":{"allowedFallbackModels":[{"provider":"anthropic","model":"claude-opus-4-8","cost":{"input":4,"output":20,"cacheRead":0.4,"cacheWrite":5}}]}}""")!.AsObject();
        var fallbacks = ProviderComposer.ApplyModelOverride(Fable(), over).GetCompat<AnthropicMessagesCompat>().AllowedFallbackModels;
        var fallback = Assert.Single(fallbacks!);
        Assert.Equal("claude-opus-4-8", fallback.Model);
        Assert.Equal(20, fallback.Cost.Output);
    }

    [Fact]
    public void EmptyAllowedFallbackModelsDisablesFallback()
    {
        var over = JsonNode.Parse("""{"compat":{"allowedFallbackModels":[]}}""")!.AsObject();
        Assert.Empty(ProviderComposer.ApplyModelOverride(Fable(), over).GetCompat<AnthropicMessagesCompat>().AllowedFallbackModels!);
    }
}
