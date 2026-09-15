namespace Iris.Ai.Providers;

public static class GithubCopilotHeaders
{
    /// <summary>Copilot expects X-Initiator to indicate whether the request is user- or agent-initiated.</summary>
    public static string InferInitiator(IReadOnlyList<Message> messages) =>
        messages.Count > 0 && messages[^1] is not UserMessage ? "agent" : "user";

    public static bool HasVisionInput(IEnumerable<Message> messages) => messages.Any(msg => msg switch
    {
        UserMessage { Content.Blocks: { } blocks } => blocks.Any(c => c is ImageContent),
        ToolResultMessage tr => tr.Content.Any(c => c is ImageContent),
        _ => false,
    });

    public static Dictionary<string, string> BuildDynamicHeaders(IReadOnlyList<Message> messages, bool hasImages)
    {
        var headers = new Dictionary<string, string>
        {
            ["X-Initiator"] = InferInitiator(messages),
            ["Openai-Intent"] = "conversation-edits",
        };
        if (hasImages) headers["Copilot-Vision-Request"] = "true";
        return headers;
    }
}
