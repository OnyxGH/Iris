using System.Text.Json.Nodes;
using Iris.Ai;
using Iris.CodingAgent.Modes.Interactive;

namespace Iris.CodingAgent.Tests;

public class ThinkingDropNoticeTests
{
    [Fact]
    public void CountsDroppedThinkingBlocksAcrossDiagnostics()
    {
        static AssistantMessageDiagnostic Diagnostic(string json) => new()
        {
            Type = "anthropic_input_transformations",
            Details = JsonNode.Parse(json)!.AsObject(),
        };
        var message = new AssistantMessage
        {
            Diagnostics =
            [
                Diagnostic("""{"transformations":[{"type":"thinking_dropped"},{"type":"other"}]}"""),
                Diagnostic("""{"transformations":[{"type":"thinking_dropped","reason":"x"}]}"""),
                new AssistantMessageDiagnostic { Type = "unrelated", Details = JsonNode.Parse("""{"transformations":[{"type":"thinking_dropped"}]}""")!.AsObject() },
            ],
        };
        Assert.Equal(2, InteractiveMode.CountDroppedThinkingBlocks(message));
        Assert.Equal(0, InteractiveMode.CountDroppedThinkingBlocks(new AssistantMessage()));
    }
}
