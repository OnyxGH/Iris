using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Iris.Ai.Json;

/// <summary>
/// Shared JSON settings. Mirrors JavaScript JSON.stringify output as closely as practical:
/// camelCase names, omitted undefined (null) properties, no HTML escaping.
/// </summary>
public static class PiJson
{
    public static readonly JsonSerializerOptions Options = CreateOptions(indented: false);
    public static readonly JsonSerializerOptions Indented = CreateOptions(indented: true);

    private static JsonSerializerOptions CreateOptions(bool indented)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = null,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = indented,
            IndentCharacter = '\t',
            IndentSize = 1,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
        };
        options.Converters.Add(new MessageJsonConverter());
        options.Converters.Add(new ContentBlockJsonConverter());
        options.Converters.Add(new UserContentJsonConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static string SerializeIndented<T>(T value) => JsonSerializer.Serialize(value, Indented);

    private static readonly JsonSerializerOptions TwoSpaces = new(Options) { WriteIndented = true, IndentCharacter = ' ', IndentSize = 2 };

    /// <summary>Equivalent of JSON.stringify(value, null, 2).</summary>
    public static string SerializeIndentedTwoSpaces(JsonNode? node) => node is null ? "null" : node.ToJsonString(TwoSpaces);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    public static T? Deserialize<T>(JsonNode? node) => node is null ? default : node.Deserialize<T>(Options);

    public static JsonNode? ToNode<T>(T value) => JsonSerializer.SerializeToNode(value, Options);

    /// <summary>JSON.stringify for an arbitrary node (compact, JS-compatible escaping).</summary>
    public static string Stringify(JsonNode? node) =>
        node is null ? "null" : node.ToJsonString(Options);

    /// <summary>Equivalent of JSON.stringify for a plain string.</summary>
    public static string Quote(string value) => JsonSerializer.Serialize(value, Options);

    public static JsonNode? CloneNode(JsonNode? node) => node?.DeepClone();

    /// <summary>Read a numeric JSON value regardless of the CLR type backing the node.</summary>
    public static bool TryGetNumber(JsonNode? node, out double value)
    {
        value = 0;
        if (node is not JsonValue v) return false;
        if (v.TryGetValue<double>(out value)) return true;
        if (v.TryGetValue<long>(out var l)) { value = l; return true; }
        if (v.TryGetValue<int>(out var i)) { value = i; return true; }
        if (v.TryGetValue<decimal>(out var m)) { value = (double)m; return true; }
        if (v.TryGetValue<float>(out var f)) { value = f; return true; }
        if (v.TryGetValue<ulong>(out var ul)) { value = ul; return true; }
        if (v.TryGetValue<uint>(out var ui)) { value = ui; return true; }
        if (v.TryGetValue<short>(out var s)) { value = s; return true; }
        if (v.TryGetValue<byte>(out var b)) { value = b; return true; }
        if (v.TryGetValue<JsonElement>(out var e) && e.ValueKind == JsonValueKind.Number) { value = e.GetDouble(); return true; }
        return false;
    }

    public static double? GetNumber(JsonNode? node) => TryGetNumber(node, out var d) ? d : null;

    public static long? GetLong(JsonNode? node) => TryGetNumber(node, out var d) ? (long)d : null;

    public static string? GetString(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static bool? GetBool(JsonNode? node) => node is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    /// <summary>JavaScript truthiness of a JSON value.</summary>
    public static bool IsTruthy(JsonNode? node)
    {
        if (node is null) return false;
        if (node is not JsonValue value) return true;
        return value.GetValueKind() switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.False => false,
            JsonValueKind.String => value.GetValue<string>().Length > 0,
            JsonValueKind.Number => value.TryGetValue<double>(out var d) && d != 0 && !double.IsNaN(d),
            _ => true,
        };
    }
}
