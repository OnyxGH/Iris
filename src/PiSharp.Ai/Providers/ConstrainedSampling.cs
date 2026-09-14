using System.Text.Json.Nodes;
using PiSharp.Ai.Json;

namespace PiSharp.Ai.Providers;

public sealed class UnsupportedStrictJsonSchemaException(string message) : Exception(message);

public sealed record GrammarConstrainedSampling(string Format, string Definition, string InputProperty);

public sealed class GrammarToolInputJsonBuffer
{
    public string Input { get; set; } = "";
    public bool Started { get; set; }
    public bool Closed { get; set; }
}

/// <summary>Strict JSON schema and grammar tool helpers. Port of api/constrained-sampling.ts.</summary>
public static class ConstrainedSampling
{
    private static readonly string[] UnsupportedStrictSchemaKeys =
    [
        "$ref", "$defs", "definitions", "allOf", "oneOf", "patternProperties", "dependentSchemas", "dependencies",
        "unevaluatedProperties", "propertyNames", "contains", "prefixItems", "not", "if", "then", "else",
    ];

    private static bool IsStructuredSchema(JsonNode? schema)
    {
        if (schema is not JsonObject obj) return false;
        var types = TypesOf(obj);
        return types.Contains("object") || types.Contains("array") || obj.ContainsKey("properties") || obj.ContainsKey("items");
    }

    private static List<string> TypesOf(JsonObject obj) => obj["type"] switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => [s],
        JsonArray a => a.Select(x => x is JsonValue xv && xv.TryGetValue<string>(out var xs) ? xs : "").ToList(),
        _ => [],
    };

    private static bool SchemaAllowsNull(JsonNode? schema)
    {
        if (schema is not JsonObject obj) return false;
        if (TypesOf(obj).Contains("null")) return true;
        if (obj.TryGetPropertyValue("const", out var c) && c is null) return true;
        if (obj["enum"] is JsonArray e && e.Any(x => x is null)) return true;
        return obj["anyOf"] is JsonArray anyOf && anyOf.Any(SchemaAllowsNull);
    }

    private static void MakeNodeStrict(JsonNode? schema)
    {
        if (schema is not JsonObject obj) throw new UnsupportedStrictJsonSchemaException("boolean schemas are unsupported");
        foreach (var key in UnsupportedStrictSchemaKeys)
        {
            if (obj.ContainsKey(key)) throw new UnsupportedStrictJsonSchemaException($"{key} schemas are unsupported");
        }

        if (obj.ContainsKey("anyOf"))
        {
            if (obj["anyOf"] is not JsonArray anyOf || anyOf.Count == 0)
                throw new UnsupportedStrictJsonSchemaException("anyOf must contain at least one schema");
            foreach (var variant in anyOf)
            {
                if (IsStructuredSchema(variant)) throw new UnsupportedStrictJsonSchemaException("object and array unions are unsupported");
                MakeNodeStrict(variant);
            }
        }

        if (obj.ContainsKey("items"))
        {
            if (obj["items"] is JsonArray) throw new UnsupportedStrictJsonSchemaException("tuple schemas are unsupported");
            MakeNodeStrict(obj["items"]);
        }

        var isObjectSchema = obj["type"] is JsonValue tv && tv.TryGetValue<string>(out var t) && t == "object";
        if (obj.ContainsKey("properties") && !isObjectSchema)
            throw new UnsupportedStrictJsonSchemaException("properties require type object");
        if (!isObjectSchema) return;
        if (obj.TryGetPropertyValue("additionalProperties", out var ap) && !(ap is JsonValue apv && apv.TryGetValue<bool>(out var apb) && !apb))
            throw new UnsupportedStrictJsonSchemaException("schema-valued or true additionalProperties is unsupported");
        if (obj.ContainsKey("properties") && obj["properties"] is not JsonObject)
            throw new UnsupportedStrictJsonSchemaException("object properties must be a schema map");
        if (obj.ContainsKey("required") && (obj["required"] is not JsonArray req || req.Any(k => k is not JsonValue kv || !kv.TryGetValue<string>(out _))))
            throw new UnsupportedStrictJsonSchemaException("object required must be a string array");

        var properties = obj["properties"] as JsonObject ?? new JsonObject();
        var propertyNames = properties.Select(p => p.Key).ToList();
        var required = (obj["required"] as JsonArray)?.Select(x => x!.GetValue<string>()).ToHashSet() ?? [];
        if (required.Any(k => !propertyNames.Contains(k)))
            throw new UnsupportedStrictJsonSchemaException("required contains an unknown property");

        foreach (var key in propertyNames)
        {
            var property = properties[key];
            MakeNodeStrict(property);
            if (!required.Contains(key) && !SchemaAllowsNull(property))
            {
                properties[key] = new JsonObject
                {
                    ["anyOf"] = new JsonArray(property!.DeepClone(), new JsonObject { ["type"] = "null" }),
                };
            }
        }
        obj["required"] = new JsonArray(propertyNames.Select(n => (JsonNode)JsonValue.Create(n)).ToArray());
        obj["additionalProperties"] = false;
    }

    public static JsonObject MakeStrictJsonSchema(JsonObject schema)
    {
        var cloned = (JsonObject)schema.DeepClone();
        MakeNodeStrict(cloned);
        if (!(cloned["type"] is JsonValue tv && tv.TryGetValue<string>(out var t) && t == "object"))
            throw new UnsupportedStrictJsonSchemaException("root schema must have type object");
        return cloned;
    }

    public static JsonObject GetJsonSchemaToolParameters(Tool tool, bool? strict) =>
        strict == true ? MakeStrictJsonSchema(tool.Parameters) : tool.Parameters;

    public static string GetGrammarToolInput(string toolName, JsonObject arguments, string inputProperty)
    {
        if (arguments[inputProperty] is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        throw new InvalidOperationException($"Grammar tool call \"{toolName}\" requires argument \"{inputProperty}\" to be a string.");
    }

    public static string? AppendGrammarToolInputJsonDelta(GrammarToolInputJsonBuffer buffer, string inputProperty, string nextInput, bool close)
    {
        if (buffer.Closed)
        {
            if (close && nextInput == buffer.Input) return null;
            throw new InvalidOperationException($"grammar tool input for property \"{inputProperty}\" changed after it was closed");
        }
        if (!nextInput.StartsWith(buffer.Input, StringComparison.Ordinal))
            throw new InvalidOperationException($"grammar tool input for property \"{inputProperty}\" changed non-monotonically");

        var inputDelta = nextInput[buffer.Input.Length..];
        if (!close && inputDelta.Length == 0) return null;

        var delta = "";
        if (!buffer.Started)
        {
            delta += "{" + PiJson.Quote(inputProperty) + ":\"";
            buffer.Started = true;
        }
        var quoted = PiJson.Quote(inputDelta);
        delta += quoted[1..^1];
        buffer.Input = nextInput;
        if (close)
        {
            delta += "\"}";
            buffer.Closed = true;
        }
        return delta;
    }

    private static string InferGrammarInputProperty(Tool tool)
    {
        var schema = tool.Parameters;
        if (!(schema["type"] is JsonValue tv && tv.TryGetValue<string>(out var t) && t == "object"))
            throw new InvalidOperationException("grammar constrained sampling requires an object parameter schema");
        if (schema["required"] is not JsonArray req || req.Count != 1 || req[0] is not JsonValue rv || !rv.TryGetValue<string>(out var inputProperty))
            throw new InvalidOperationException("grammar constrained sampling requires exactly one required string property");
        if (schema["properties"] is not JsonObject props || props[inputProperty] is not JsonObject prop)
            throw new InvalidOperationException($"grammar constrained sampling requires a properties entry for {inputProperty}");
        if (!(prop["type"] is JsonValue ptv && ptv.TryGetValue<string>(out var pt) && pt == "string"))
            throw new InvalidOperationException($"grammar constrained sampling property {inputProperty} must have type string");
        return inputProperty;
    }

    public static bool? ResolveJsonSchemaStrictSampling(Tool tool, bool supportsStrictMode)
    {
        var config = tool.ConstrainedSampling;
        if (config is null || config.Type != "json_schema") return null;
        if (supportsStrictMode)
        {
            try
            {
                MakeStrictJsonSchema(tool.Parameters);
                return true;
            }
            catch (UnsupportedStrictJsonSchemaException ex)
            {
                if (config.Strict != "require") return null;
                throw new InvalidOperationException($"Tool \"{tool.Name}\" requires JSON-schema constrained sampling, but {ex.Message}.");
            }
        }
        if (config.Strict == "require")
            throw new InvalidOperationException($"Tool \"{tool.Name}\" requires JSON-schema constrained sampling, but strict tools are unsupported.");
        return null;
    }

    public static GrammarConstrainedSampling? ResolveGrammarConstrainedSampling(Tool tool, bool supportsOpenAIGrammarTools)
    {
        var config = tool.ConstrainedSampling;
        if (config is null || config.Type != "grammar") return null;
        if (!supportsOpenAIGrammarTools) return null;

        string? lark = null, regex = null;
        config.Variants?.TryGetValue("openai_lark", out lark);
        config.Variants?.TryGetValue("openai_regex", out regex);
        var hasLark = !string.IsNullOrWhiteSpace(lark);
        var hasRegex = !string.IsNullOrWhiteSpace(regex);
        if (!hasLark && !hasRegex)
            throw new InvalidOperationException($"Tool \"{tool.Name}\" cannot use grammar constrained sampling: no supported grammar variant was provided.");
        try
        {
            return new GrammarConstrainedSampling(hasLark ? "lark" : "regex", hasLark ? lark! : regex!, InferGrammarInputProperty(tool));
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException($"Tool \"{tool.Name}\" cannot use grammar constrained sampling: {ex.Message}.");
        }
    }

    public static IReadOnlyDictionary<string, string> CreateGrammarToolInputProperties(IEnumerable<Tool>? tools, bool supportsOpenAIGrammarTools)
    {
        var properties = new Dictionary<string, string>();
        foreach (var tool in tools ?? [])
        {
            var grammar = ResolveGrammarConstrainedSampling(tool, supportsOpenAIGrammarTools);
            if (grammar is not null) properties[tool.Name] = grammar.InputProperty;
        }
        return properties;
    }
}
