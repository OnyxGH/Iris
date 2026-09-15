using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Iris.Ai.Json;

namespace Iris.Ai.Utils;

public sealed record SchemaValidationError(string Keyword, string InstancePath, string Message, IReadOnlyList<string>? RequiredProperties = null);

/// <summary>
/// Lightweight JSON Schema validator covering the keywords used by tool schemas, with AJV/TypeBox style messages.
/// </summary>
public static class JsonSchemaValidator
{
    public static bool Check(JsonObject schema, JsonNode? value) => Validate(schema, value).Count == 0;

    public static List<SchemaValidationError> Validate(JsonNode? schema, JsonNode? value)
    {
        var errors = new List<SchemaValidationError>();
        ValidateNode(schema, value, "", errors);
        return errors;
    }

    private static string? Str(JsonObject obj, string name) => obj[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static double? Num(JsonObject obj, string name) => obj[name] is JsonValue v && IrisJson.TryGetNumber(v, out var d) ? d : null;

    private static List<string> TypesOf(JsonObject schema) => schema["type"] switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => [s],
        JsonArray a => a.OfType<JsonValue>().Select(x => x.TryGetValue<string>(out var t) ? t : "").Where(t => t.Length > 0).ToList(),
        _ => [],
    };

    public static bool MatchesType(JsonNode? value, string type)
    {
        switch (type)
        {
            case "null":
                return value is null || (value is JsonValue nv && nv.GetValueKind() == JsonValueKind.Null);
            case "object":
                return value is JsonObject;
            case "array":
                return value is JsonArray;
            case "string":
                return value is JsonValue sv && sv.GetValueKind() == JsonValueKind.String;
            case "boolean":
                return value is JsonValue bv && bv.GetValueKind() is JsonValueKind.True or JsonValueKind.False;
            case "number":
                return value is JsonValue n && n.GetValueKind() == JsonValueKind.Number;
            case "integer":
                return value is JsonValue i && i.GetValueKind() == JsonValueKind.Number && IrisJson.TryGetNumber(i, out var d) && Math.Floor(d) == d && !double.IsInfinity(d);
            default:
                return false;
        }
    }

    private static void ValidateNode(JsonNode? schemaNode, JsonNode? value, string path, List<SchemaValidationError> errors)
    {
        if (schemaNode is JsonValue boolSchema && boolSchema.TryGetValue<bool>(out var allowed))
        {
            if (!allowed) errors.Add(new("false schema", path, "boolean schema is false"));
            return;
        }
        if (schemaNode is not JsonObject schema) return;

        var types = TypesOf(schema);
        if (types.Count > 0 && !types.Any(t => MatchesType(value, t)))
        {
            errors.Add(new("type", path, $"must be {string.Join(",", types)}"));
            return;
        }

        if (schema.TryGetPropertyValue("const", out var constValue) && !JsonNode.DeepEquals(constValue, value))
        {
            errors.Add(new("const", path, "must be equal to constant"));
        }

        if (schema["enum"] is JsonArray enumValues && !enumValues.Any(e => JsonNode.DeepEquals(e, value)))
        {
            errors.Add(new("enum", path, "must be equal to one of the allowed values"));
        }

        if (schema["anyOf"] is JsonArray anyOf && !anyOf.Any(s => Validate(s, value).Count == 0))
        {
            errors.Add(new("anyOf", path, "must match a schema in anyOf"));
        }

        if (schema["oneOf"] is JsonArray oneOf)
        {
            var matches = oneOf.Count(s => Validate(s, value).Count == 0);
            if (matches != 1) errors.Add(new("oneOf", path, "must match exactly one schema in oneOf"));
        }

        if (schema["allOf"] is JsonArray allOf)
        {
            foreach (var sub in allOf) ValidateNode(sub, value, path, errors);
        }

        if (schema["not"] is { } notSchema && Validate(notSchema, value).Count == 0)
        {
            errors.Add(new("not", path, "must NOT be valid"));
        }

        switch (value)
        {
            case JsonObject obj:
                ValidateObject(schema, obj, path, errors);
                break;
            case JsonArray arr:
                ValidateArray(schema, arr, path, errors);
                break;
            case JsonValue v when v.GetValueKind() == JsonValueKind.String:
                ValidateString(schema, v.GetValue<string>(), path, errors);
                break;
            case JsonValue v when v.GetValueKind() == JsonValueKind.Number:
                ValidateNumber(schema, IrisJson.GetNumber(v)!.Value, path, errors);
                break;
        }
    }

    private static void ValidateObject(JsonObject schema, JsonObject obj, string path, List<SchemaValidationError> errors)
    {
        if (schema["required"] is JsonArray required)
        {
            foreach (var r in required.OfType<JsonValue>())
            {
                if (!r.TryGetValue<string>(out var name)) continue;
                if (!obj.ContainsKey(name)) errors.Add(new("required", path, $"must have required properties {name}", [name]));
            }
        }

        var properties = schema["properties"] as JsonObject;
        if (properties is not null)
        {
            foreach (var (name, propertySchema) in properties)
            {
                if (obj.TryGetPropertyValue(name, out var propertyValue)) ValidateNode(propertySchema, propertyValue, $"{path}/{name}", errors);
            }
        }

        if (schema.TryGetPropertyValue("additionalProperties", out var additional) && additional is not null)
        {
            foreach (var (name, propertyValue) in obj)
            {
                if (properties?.ContainsKey(name) == true) continue;
                if (additional is JsonValue av && av.TryGetValue<bool>(out var allowAdditional))
                {
                    if (!allowAdditional) errors.Add(new("additionalProperties", path, "must NOT have additional properties"));
                }
                else
                {
                    ValidateNode(additional, propertyValue, $"{path}/{name}", errors);
                }
            }
        }

        if (Num(schema, "minProperties") is { } minProps && obj.Count < minProps)
            errors.Add(new("minProperties", path, $"must NOT have fewer than {minProps} properties"));
        if (Num(schema, "maxProperties") is { } maxProps && obj.Count > maxProps)
            errors.Add(new("maxProperties", path, $"must NOT have more than {maxProps} properties"));
    }

    private static void ValidateArray(JsonObject schema, JsonArray arr, string path, List<SchemaValidationError> errors)
    {
        if (schema["items"] is JsonArray tuple)
        {
            for (var i = 0; i < arr.Count && i < tuple.Count; i++) ValidateNode(tuple[i], arr[i], $"{path}/{i}", errors);
        }
        else if (schema["items"] is { } itemSchema)
        {
            for (var i = 0; i < arr.Count; i++) ValidateNode(itemSchema, arr[i], $"{path}/{i}", errors);
        }
        if (Num(schema, "minItems") is { } minItems && arr.Count < minItems)
            errors.Add(new("minItems", path, $"must NOT have fewer than {minItems} items"));
        if (Num(schema, "maxItems") is { } maxItems && arr.Count > maxItems)
            errors.Add(new("maxItems", path, $"must NOT have more than {maxItems} items"));
        if (schema["uniqueItems"] is JsonValue uv && uv.TryGetValue<bool>(out var unique) && unique)
        {
            for (var i = 0; i < arr.Count; i++)
            {
                for (var j = i + 1; j < arr.Count; j++)
                {
                    if (JsonNode.DeepEquals(arr[i], arr[j]))
                    {
                        errors.Add(new("uniqueItems", path, $"must NOT have duplicate items (items ## {j} and {i} are identical)"));
                        return;
                    }
                }
            }
        }
    }

    private static void ValidateString(JsonObject schema, string s, string path, List<SchemaValidationError> errors)
    {
        var length = s.EnumerateRunes().Count();
        if (Num(schema, "minLength") is { } minLength && length < minLength)
            errors.Add(new("minLength", path, $"must NOT have fewer than {minLength} characters"));
        if (Num(schema, "maxLength") is { } maxLength && length > maxLength)
            errors.Add(new("maxLength", path, $"must NOT have more than {maxLength} characters"));
        if (Str(schema, "pattern") is { } pattern)
        {
            try
            {
                if (!Regex.IsMatch(s, pattern, RegexOptions.None, TimeSpan.FromSeconds(1)))
                    errors.Add(new("pattern", path, $"must match pattern \"{pattern}\""));
            }
            catch (ArgumentException)
            {
            }
        }
    }

    private static void ValidateNumber(JsonObject schema, double n, string path, List<SchemaValidationError> errors)
    {
        static string F(double d) => d.ToString(CultureInfo.InvariantCulture);
        if (Num(schema, "minimum") is { } min && n < min) errors.Add(new("minimum", path, $"must be >= {F(min)}"));
        if (Num(schema, "maximum") is { } max && n > max) errors.Add(new("maximum", path, $"must be <= {F(max)}"));
        if (Num(schema, "exclusiveMinimum") is { } xmin && n <= xmin) errors.Add(new("exclusiveMinimum", path, $"must be > {F(xmin)}"));
        if (Num(schema, "exclusiveMaximum") is { } xmax && n >= xmax) errors.Add(new("exclusiveMaximum", path, $"must be < {F(xmax)}"));
        if (Num(schema, "multipleOf") is { } multiple && multiple != 0 && Math.Abs(n / multiple - Math.Round(n / multiple)) > 1e-9)
            errors.Add(new("multipleOf", path, $"must be multiple of {F(multiple)}"));
    }
}

/// <summary>Tool argument validation with lenient coercion.</summary>
public static class ToolValidation
{
    public static JsonObject ValidateToolCall(IReadOnlyList<Tool> tools, ToolCall toolCall)
    {
        var tool = tools.FirstOrDefault(t => t.Name == toolCall.Name) ?? throw new InvalidOperationException($"Tool \"{toolCall.Name}\" not found");
        return ValidateToolArguments(tool, toolCall);
    }

    public static JsonObject ValidateToolArguments(Tool tool, ToolCall toolCall)
    {
        var args = (JsonObject)toolCall.Arguments.DeepClone();
        NormalizeOptionalNulls(args, tool.Parameters);
        var coerced = CoerceWithJsonSchema(args, tool.Parameters);
        if (coerced is JsonObject coercedObj) args = coercedObj;

        var errors = JsonSchemaValidator.Validate(tool.Parameters, args);
        if (errors.Count == 0) return args;

        var formatted = string.Join("\n", errors.Select(e => $"  - {FormatValidationPath(e)}: {e.Message}"));
        if (formatted.Length == 0) formatted = "Unknown validation error";
        var received = IrisJson.SerializeIndentedTwoSpaces(toolCall.Arguments);
        throw new InvalidOperationException($"Validation failed for tool \"{toolCall.Name}\":\n{formatted}\n\nReceived arguments:\n{received}");
    }

    private static string FormatValidationPath(SchemaValidationError error)
    {
        var basePath = error.InstancePath.TrimStart('/').Replace('/', '.');
        if (error.Keyword == "required" && error.RequiredProperties is { Count: > 0 } req)
        {
            return basePath.Length > 0 ? $"{basePath}.{req[0]}" : req[0];
        }
        return basePath.Length > 0 ? basePath : "root";
    }

    private static List<string> TypesOf(JsonObject schema) => schema["type"] switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => [s],
        JsonArray a => a.OfType<JsonValue>().Select(x => x.TryGetValue<string>(out var t) ? t : "").Where(t => t.Length > 0).ToList(),
        _ => [],
    };

    private static bool IsNull(JsonNode? n) => n is null || (n is JsonValue v && v.GetValueKind() == JsonValueKind.Null);

    private static JsonNode? CoercePrimitiveByType(JsonNode? value, string type, out bool changed)
    {
        changed = false;
        JsonValue? v = value as JsonValue;
        var kind = v?.GetValueKind() ?? JsonValueKind.Null;
        switch (type)
        {
            case "number":
                if (IsNull(value)) { changed = true; return JsonValue.Create(0); }
                if (kind == JsonValueKind.String)
                {
                    var s = v!.GetValue<string>();
                    if (s.Trim().Length > 0 && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d))
                    {
                        changed = true;
                        return Math.Floor(d) == d && Math.Abs(d) < 9007199254740992 ? JsonValue.Create((long)d) : JsonValue.Create(d);
                    }
                }
                if (kind is JsonValueKind.True or JsonValueKind.False) { changed = true; return JsonValue.Create(kind == JsonValueKind.True ? 1 : 0); }
                return value;
            case "integer":
                if (IsNull(value)) { changed = true; return JsonValue.Create(0); }
                if (kind == JsonValueKind.String)
                {
                    var s = v!.GetValue<string>();
                    if (s.Trim().Length > 0 && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && Math.Floor(d) == d && double.IsFinite(d))
                    {
                        changed = true;
                        return JsonValue.Create((long)d);
                    }
                }
                if (kind is JsonValueKind.True or JsonValueKind.False) { changed = true; return JsonValue.Create(kind == JsonValueKind.True ? 1 : 0); }
                return value;
            case "boolean":
                if (IsNull(value)) { changed = true; return JsonValue.Create(false); }
                if (kind == JsonValueKind.String)
                {
                    var s = v!.GetValue<string>();
                    if (s == "true") { changed = true; return JsonValue.Create(true); }
                    if (s == "false") { changed = true; return JsonValue.Create(false); }
                }
                if (kind == JsonValueKind.Number)
                {
                    var d = IrisJson.GetNumber(v)!.Value;
                    if (d == 1) { changed = true; return JsonValue.Create(true); }
                    if (d == 0) { changed = true; return JsonValue.Create(false); }
                }
                return value;
            case "string":
                if (IsNull(value)) { changed = true; return JsonValue.Create(""); }
                if (kind == JsonValueKind.Number) { changed = true; return JsonValue.Create(v!.ToJsonString()); }
                if (kind is JsonValueKind.True or JsonValueKind.False) { changed = true; return JsonValue.Create(kind == JsonValueKind.True ? "true" : "false"); }
                return value;
            case "null":
                if (kind == JsonValueKind.String && v!.GetValue<string>() == "") { changed = true; return null; }
                if (kind == JsonValueKind.Number && IrisJson.GetNumber(v)!.Value == 0) { changed = true; return null; }
                if (kind == JsonValueKind.False) { changed = true; return null; }
                return value;
            default:
                return value;
        }
    }

    private static JsonNode? CoerceWithUnionSchema(JsonNode? value, JsonArray schemas)
    {
        foreach (var schema in schemas)
        {
            if (JsonSchemaValidator.Validate(schema, value).Count == 0) return value;
        }
        foreach (var schema in schemas.OfType<JsonObject>())
        {
            var candidate = value?.DeepClone();
            var coerced = CoerceWithJsonSchema(candidate, schema);
            if (JsonSchemaValidator.Validate(schema, coerced).Count == 0) return coerced;
        }
        return value;
    }

    public static JsonNode? CoerceWithJsonSchema(JsonNode? value, JsonObject schema)
    {
        var next = value;
        if (schema["allOf"] is JsonArray allOf)
        {
            foreach (var nested in allOf.OfType<JsonObject>()) next = CoerceWithJsonSchema(next, nested);
        }
        if (schema["anyOf"] is JsonArray anyOf) next = CoerceWithUnionSchema(next, anyOf);
        if (schema["oneOf"] is JsonArray oneOf) next = CoerceWithUnionSchema(next, oneOf);

        var types = TypesOf(schema);
        var matchesUnionMember = types.Count > 1 && types.Any(t => JsonSchemaValidator.MatchesType(next, t));
        if (types.Count > 0 && !matchesUnionMember)
        {
            foreach (var type in types)
            {
                var candidate = CoercePrimitiveByType(next, type, out var changed);
                if (changed)
                {
                    next = candidate;
                    break;
                }
            }
        }

        if (types.Contains("object") && next is JsonObject obj)
        {
            var properties = schema["properties"] as JsonObject;
            if (properties is not null)
            {
                foreach (var (key, propertySchema) in properties)
                {
                    if (!obj.TryGetPropertyValue(key, out var current) || propertySchema is not JsonObject ps) continue;
                    var coerced = CoerceWithJsonSchema(current, ps);
                    if (!ReferenceEquals(coerced, current))
                    {
                        obj[key] = coerced?.Parent is null ? coerced : coerced.DeepClone();
                    }
                }
            }
            if (schema["additionalProperties"] is JsonObject additional)
            {
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                {
                    if (properties?.ContainsKey(key) == true) continue;
                    var current = obj[key];
                    var coerced = CoerceWithJsonSchema(current, additional);
                    if (!ReferenceEquals(coerced, current)) obj[key] = coerced?.Parent is null ? coerced : coerced.DeepClone();
                }
            }
        }

        if (types.Contains("array") && next is JsonArray arr)
        {
            if (schema["items"] is JsonArray tuple)
            {
                for (var i = 0; i < arr.Count && i < tuple.Count; i++)
                {
                    if (tuple[i] is not JsonObject itemSchema) continue;
                    var current = arr[i];
                    var coerced = CoerceWithJsonSchema(current, itemSchema);
                    if (!ReferenceEquals(coerced, current)) arr[i] = coerced?.Parent is null ? coerced : coerced.DeepClone();
                }
            }
            else if (schema["items"] is JsonObject itemSchema)
            {
                for (var i = 0; i < arr.Count; i++)
                {
                    var current = arr[i];
                    var coerced = CoerceWithJsonSchema(current, itemSchema);
                    if (!ReferenceEquals(coerced, current)) arr[i] = coerced?.Parent is null ? coerced : coerced.DeepClone();
                }
            }
        }

        return next;
    }

    private static void NormalizeOptionalNulls(JsonNode? value, JsonObject schema)
    {
        if (value is JsonArray arr)
        {
            if (schema["items"] is JsonArray tuple)
            {
                for (var i = 0; i < arr.Count && i < tuple.Count; i++)
                {
                    if (tuple[i] is JsonObject s) NormalizeOptionalNulls(arr[i], s);
                }
            }
            else if (schema["items"] is JsonObject itemSchema)
            {
                foreach (var item in arr) NormalizeOptionalNulls(item, itemSchema);
            }
            return;
        }
        if (value is not JsonObject obj || schema["properties"] is not JsonObject properties) return;
        var required = (schema["required"] as JsonArray)?.OfType<JsonValue>().Select(r => r.TryGetValue<string>(out var s) ? s : "").ToHashSet() ?? [];
        foreach (var (key, propertySchema) in properties)
        {
            if (!obj.TryGetPropertyValue(key, out var current)) continue;
            if (propertySchema is not JsonObject ps) continue;
            if (IsNull(current) && !required.Contains(key) && ps["$ref"] is not JsonValue && JsonSchemaValidator.Validate(ps, null).Count > 0)
            {
                obj.Remove(key);
            }
            else
            {
                NormalizeOptionalNulls(current, ps);
            }
        }
    }
}
