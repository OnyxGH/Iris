using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PiSharp.Ai.Json;

/// <summary>Maps message "role" values to concrete CLR types. Extensible by higher layers.</summary>
public static class MessageTypeRegistry
{
    private static readonly ConcurrentDictionary<string, Type> Roles = new()
    {
        ["user"] = typeof(UserMessage),
        ["assistant"] = typeof(AssistantMessage),
        ["toolResult"] = typeof(ToolResultMessage),
    };

    public static void Register<T>(string role) where T : Message => Roles[role] = typeof(T);

    public static bool TryGetType(string role, out Type type) => Roles.TryGetValue(role, out type!);
}

public sealed class MessageJsonConverter : JsonConverter<Message>
{
    public override Message? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        var node = JsonNode.Parse(ref reader) as JsonObject
            ?? throw new JsonException("Message must be a JSON object");
        return FromObject(node, options);
    }

    public static Message FromObject(JsonObject node, JsonSerializerOptions options)
    {
        var role = node["role"] is JsonValue rv && rv.TryGetValue<string>(out var r) ? r : null;
        if (role is not null && MessageTypeRegistry.TryGetType(role, out var type))
        {
            return (Message)node.Deserialize(type, options)!;
        }
        return new UnknownMessage(node);
    }

    public override void Write(Utf8JsonWriter writer, Message value, JsonSerializerOptions options)
    {
        if (value is UnknownMessage unknown)
        {
            unknown.Raw.WriteTo(writer, options);
            return;
        }
        JsonSerializer.Serialize(writer, value, value.GetType(), options);
    }
}

public sealed class ContentBlockJsonConverter : JsonConverter<ContentBlock>
{
    public override ContentBlock? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        var node = JsonNode.Parse(ref reader) as JsonObject
            ?? throw new JsonException("Content block must be a JSON object");
        var type = node["type"] is JsonValue tv && tv.TryGetValue<string>(out var t) ? t : null;
        return type switch
        {
            "text" => node.Deserialize<TextContent>(options),
            "thinking" => node.Deserialize<ThinkingContent>(options),
            "image" => node.Deserialize<ImageContent>(options),
            "toolCall" => node.Deserialize<ToolCall>(options),
            _ => new UnknownContent(node),
        };
    }

    public override void Write(Utf8JsonWriter writer, ContentBlock value, JsonSerializerOptions options)
    {
        if (value is UnknownContent unknown)
        {
            unknown.Raw.WriteTo(writer, options);
            return;
        }
        JsonSerializer.Serialize(writer, value, value.GetType(), options);
    }
}

public sealed class UserContentJsonConverter : JsonConverter<UserContent>
{
    public override UserContent? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return UserContent.FromBlocks([]);
            case JsonTokenType.String:
                return UserContent.FromText(reader.GetString()!);
            case JsonTokenType.StartArray:
                var blocks = JsonSerializer.Deserialize<List<ContentBlock>>(ref reader, options) ?? [];
                return UserContent.FromBlocks(blocks.Where(b => b is not null));
            default:
                throw new JsonException("User content must be a string or an array");
        }
    }

    public override void Write(Utf8JsonWriter writer, UserContent value, JsonSerializerOptions options)
    {
        if (value.Text is not null) writer.WriteStringValue(value.Text);
        else JsonSerializer.Serialize(writer, value.Blocks, options);
    }
}
