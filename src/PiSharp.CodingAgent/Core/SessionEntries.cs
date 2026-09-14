using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PiSharp.Ai;
using PiSharp.Ai.Json;

namespace PiSharp.CodingAgent.Core;

/// <summary>A line in a session JSONL file: the header or a tree entry.</summary>
[JsonConverter(typeof(FileEntryJsonConverter))]
public abstract class FileEntry
{
    [JsonPropertyOrder(-100)]
    public abstract string Type { get; }
}

public sealed class SessionHeader : FileEntry
{
    public override string Type => "session";

    /// <summary>v1 sessions don't have this.</summary>
    public int? Version { get; set; }

    public string Id { get; set; } = "";

    public string Timestamp { get; set; } = "";

    public string? Cwd { get; set; }

    public string? ParentSession { get; set; }
}

public abstract class SessionEntry : FileEntry
{
    [JsonPropertyOrder(-90)]
    public string Id { get; set; } = "";

    [JsonPropertyOrder(-80)]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ParentId { get; set; }

    [JsonPropertyOrder(-70)]
    public string Timestamp { get; set; } = "";

    public SessionEntry ShallowCopy() => (SessionEntry)MemberwiseClone();
}

public sealed class SessionMessageEntry : SessionEntry
{
    public override string Type => "message";

    public Message Message { get; set; } = null!;
}

public sealed class ThinkingLevelChangeEntry : SessionEntry
{
    public override string Type => "thinking_level_change";

    public string ThinkingLevel { get; set; } = "off";
}

public sealed class ModelChangeEntry : SessionEntry
{
    public override string Type => "model_change";

    public string Provider { get; set; } = "";

    public string ModelId { get; set; } = "";
}

public sealed class CompactionEntry : SessionEntry
{
    public override string Type => "compaction";

    public string Summary { get; set; } = "";

    public string FirstKeptEntryId { get; set; } = "";

    public long TokensBefore { get; set; }

    public JsonNode? Details { get; set; }

    public Usage? Usage { get; set; }

    public bool? FromHook { get; set; }
}

public sealed class BranchSummaryEntry : SessionEntry
{
    public override string Type => "branch_summary";

    public string FromId { get; set; } = "";

    public string Summary { get; set; } = "";

    public JsonNode? Details { get; set; }

    public Usage? Usage { get; set; }

    public bool? FromHook { get; set; }
}

/// <summary>Extension state entry; does not participate in LLM context.</summary>
public sealed class CustomEntry : SessionEntry
{
    public override string Type => "custom";

    public string CustomType { get; set; } = "";

    public JsonNode? Data { get; set; }
}

public sealed class LabelEntry : SessionEntry
{
    public override string Type => "label";

    public string TargetId { get; set; } = "";

    public string? Label { get; set; }
}

public sealed class SessionInfoEntry : SessionEntry
{
    public override string Type => "session_info";

    public string? Name { get; set; }
}

/// <summary>Extension message entry that participates in LLM context.</summary>
public sealed class CustomMessageEntry : SessionEntry
{
    public override string Type => "custom_message";

    public string CustomType { get; set; } = "";

    public UserContent Content { get; set; } = UserContent.FromBlocks([]);

    public JsonNode? Details { get; set; }

    public bool Display { get; set; }
}

/// <summary>Entry with an unknown type, preserved verbatim.</summary>
public sealed class UnknownSessionEntry : SessionEntry
{
    public UnknownSessionEntry(JsonObject raw)
    {
        Raw = raw;
        Id = PiJson.GetString(raw["id"]) ?? "";
        ParentId = PiJson.GetString(raw["parentId"]);
        Timestamp = PiJson.GetString(raw["timestamp"]) ?? "";
    }

    [JsonIgnore]
    public JsonObject Raw { get; }

    public override string Type => PiJson.GetString(Raw["type"]) ?? "unknown";
}

public sealed class FileEntryJsonConverter : JsonConverter<FileEntry>
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert == typeof(FileEntry) || typeToConvert == typeof(SessionEntry);

    public override FileEntry? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        var obj = JsonNode.Parse(ref reader) as JsonObject ?? throw new JsonException("Session entry must be an object");
        return FromObject(obj, options);
    }

    public static FileEntry FromObject(JsonObject obj, JsonSerializerOptions? options = null)
    {
        options ??= PiJson.Options;
        var type = PiJson.GetString(obj["type"]);
        Type? target = type switch
        {
            "session" => typeof(SessionHeader),
            "message" => typeof(SessionMessageEntry),
            "thinking_level_change" => typeof(ThinkingLevelChangeEntry),
            "model_change" => typeof(ModelChangeEntry),
            "compaction" => typeof(CompactionEntry),
            "branch_summary" => typeof(BranchSummaryEntry),
            "custom" => typeof(CustomEntry),
            "label" => typeof(LabelEntry),
            "session_info" => typeof(SessionInfoEntry),
            "custom_message" => typeof(CustomMessageEntry),
            _ => null,
        };
        if (target is null) return new UnknownSessionEntry(obj);
        return (FileEntry)obj.Deserialize(target, options)!;
    }

    public override void Write(Utf8JsonWriter writer, FileEntry value, JsonSerializerOptions options)
    {
        if (value is UnknownSessionEntry unknown)
        {
            unknown.Raw.WriteTo(writer, options);
            return;
        }
        JsonSerializer.Serialize(writer, value, value.GetType(), options);
    }
}
