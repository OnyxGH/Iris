using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PiSharp.Ai;

/// <summary>Base for message content blocks. Serialized with a "type" discriminator.</summary>
public abstract class ContentBlock
{
    [JsonPropertyOrder(-100)]
    public abstract string Type { get; }

    public ContentBlock Clone() => CloneCore();

    protected virtual ContentBlock CloneCore() => (ContentBlock)MemberwiseClone();
}

public sealed class TextContent : ContentBlock
{
    public TextContent() { }

    public TextContent(string text) => Text = text;

    public override string Type => "text";

    public string Text { get; set; } = "";

    /// <summary>e.g. OpenAI responses message metadata (legacy id string or TextSignatureV1 JSON).</summary>
    public string? TextSignature { get; set; }

    public new TextContent Clone() => (TextContent)CloneCore();
}

public sealed class ThinkingContent : ContentBlock
{
    public ThinkingContent() { }

    public ThinkingContent(string thinking) => Thinking = thinking;

    public override string Type => "thinking";

    public string Thinking { get; set; } = "";

    /// <summary>Provider-specific opaque or serialized reasoning replay data.</summary>
    public string? ThinkingSignature { get; set; }

    /// <summary>When true, the thinking content was redacted by safety filters.</summary>
    public bool? Redacted { get; set; }

    public new ThinkingContent Clone() => (ThinkingContent)CloneCore();
}

public sealed class ImageContent : ContentBlock
{
    public ImageContent() { }

    public ImageContent(string data, string mimeType)
    {
        Data = data;
        MimeType = mimeType;
    }

    public override string Type => "image";

    /// <summary>Base64 encoded image data.</summary>
    public string Data { get; set; } = "";

    public string MimeType { get; set; } = "";

    public new ImageContent Clone() => (ImageContent)CloneCore();
}

public sealed class ToolCall : ContentBlock
{
    public override string Type => "toolCall";

    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public JsonObject Arguments { get; set; } = new();

    /// <summary>Google-specific: opaque signature for reusing thought context.</summary>
    public string? ThoughtSignature { get; set; }

    /// <summary>OpenAI Responses namespace for calls to dynamically loaded or namespaced tools.</summary>
    public string? Namespace { get; set; }

    public new ToolCall Clone()
    {
        var clone = (ToolCall)CloneCore();
        clone.Arguments = (JsonObject)Arguments.DeepClone();
        return clone;
    }
}

/// <summary>Content block with an unrecognized type, preserved verbatim.</summary>
public sealed class UnknownContent : ContentBlock
{
    public UnknownContent(JsonObject raw) => Raw = raw;

    [JsonIgnore]
    public JsonObject Raw { get; }

    public override string Type => Raw["type"]?.GetValue<string>() ?? "unknown";
}

/// <summary>
/// User message content: either a plain string or a list of text/image blocks.
/// Preserves the original shape across serialization.
/// </summary>
public sealed class UserContent
{
    private UserContent(string? text, List<ContentBlock>? blocks)
    {
        Text = text;
        Blocks = blocks;
    }

    public string? Text { get; }

    public List<ContentBlock>? Blocks { get; }

    public bool IsText => Text is not null;

    public static UserContent FromText(string text) => new(text, null);

    public static UserContent FromBlocks(IEnumerable<ContentBlock> blocks) => new(null, blocks.ToList());

    public static implicit operator UserContent(string text) => FromText(text);

    /// <summary>Content as blocks (a string becomes a single text block).</summary>
    public IReadOnlyList<ContentBlock> AsBlocks() =>
        Blocks ?? (IReadOnlyList<ContentBlock>)[new TextContent(Text ?? "")];

    public UserContent Clone() => Text is not null ? FromText(Text) : FromBlocks(Blocks!.Select(b => b.Clone()));
}
