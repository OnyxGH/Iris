using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Iris.CodingAgent.Core;

/// <summary>
/// Validates user-authored theme JSON, reporting up to 8 errors.
/// </summary>
public static class ThemeJsonValidator
{
    private const int MaxErrors = 8;

    private static readonly (string Name, bool Optional)[] ColorProperties =
    [
        ("accent", false), ("border", false), ("borderAccent", false), ("borderMuted", false), ("success", false), ("error", false),
        ("warning", false), ("muted", false), ("dim", false), ("text", false), ("thinkingText", false),
        ("scrollbarTrack", true), ("scrollbarThumb", true),
        ("selectedBg", false), ("searchMatchBg", true), ("searchMatchText", true), ("userMessageBg", false), ("userMessageText", false),
        ("customMessageBg", false), ("customMessageText", false), ("customMessageLabel", false), ("toolPendingBg", false),
        ("toolSuccessBg", false), ("toolErrorBg", false), ("toolTitle", false), ("toolOutput", false),
        ("mdHeading", false), ("mdLink", false), ("mdLinkUrl", false), ("mdCode", false), ("mdCodeBlock", false), ("mdCodeBlockBorder", false),
        ("mdQuote", false), ("mdQuoteBorder", false), ("mdHr", false), ("mdListBullet", false),
        ("toolDiffAdded", false), ("toolDiffRemoved", false), ("toolDiffContext", false),
        ("syntaxComment", false), ("syntaxKeyword", false), ("syntaxFunction", false), ("syntaxVariable", false), ("syntaxString", false),
        ("syntaxNumber", false), ("syntaxType", false), ("syntaxOperator", false), ("syntaxPunctuation", false),
        ("thinkingOff", false), ("thinkingMinimal", false), ("thinkingLow", false), ("thinkingMedium", false), ("thinkingHigh", false),
        ("thinkingXhigh", false), ("thinkingMax", true),
        ("bashMode", false),
    ];

    private sealed record ValidationError(string Keyword, string InstancePath, string Message, IReadOnlyList<string>? RequiredProperties = null);

    private sealed class Collector
    {
        public List<ValidationError> Errors { get; } = [];
        public bool Full => Errors.Count >= MaxErrors;

        public void Add(ValidationError error)
        {
            if (!Full) Errors.Add(error);
        }
    }

    private static string Pointer(string parent, string key) => $"{parent}/{key.Replace("~", "~0").Replace("/", "~1")}";

    private static bool IsObject(JsonNode? node) => node is JsonObject;

    private static bool IsString(JsonNode? node) => node is JsonValue v && v.GetValueKind() == JsonValueKind.String;

    private static void CheckString(JsonNode? node, string path, Collector errors)
    {
        if (!IsString(node)) errors.Add(new ValidationError("type", path, "must be string"));
    }

    private static void CheckColor(JsonNode? node, string path, Collector errors)
    {
        if (IsString(node)) return;
        errors.Add(new ValidationError("type", path, "must be string"));
        if (node is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue<double>(out var number))
        {
            if (Math.Floor(number) != number || double.IsInfinity(number)) errors.Add(new ValidationError("type", path, "must be integer"));
            else if (number < 0) errors.Add(new ValidationError("minimum", path, "must be >= 0"));
            else if (number > 255) errors.Add(new ValidationError("maximum", path, "must be <= 255"));
            else return;
        }
        else
        {
            errors.Add(new ValidationError("type", path, "must be integer"));
        }
        errors.Add(new ValidationError("anyOf", path, "must match a schema in anyOf"));
    }

    private static void CheckRequired(JsonObject obj, string path, IEnumerable<string> required, Collector errors)
    {
        var missing = required.Where(name => !obj.ContainsKey(name)).ToList();
        if (missing.Count > 0) errors.Add(new ValidationError("required", path, $"must have required properties {string.Join(", ", missing)}", missing));
    }

    private static List<ValidationError> Check(JsonNode? json)
    {
        var errors = new Collector();
        if (json is not JsonObject root)
        {
            errors.Add(new ValidationError("type", "", "must be object"));
            return errors.Errors;
        }
        CheckRequired(root, "", ["name", "colors"], errors);
        if (root.TryGetPropertyValue("$schema", out var schema)) CheckString(schema, "/$schema", errors);
        if (root.TryGetPropertyValue("name", out var name)) CheckString(name, "/name", errors);
        if (root.TryGetPropertyValue("vars", out var vars))
        {
            if (vars is not JsonObject varsObj) errors.Add(new ValidationError("type", "/vars", "must be object"));
            else foreach (var (key, value) in varsObj) CheckColor(value, Pointer("/vars", key), errors);
        }
        if (root.TryGetPropertyValue("colors", out var colors))
        {
            if (colors is not JsonObject colorsObj)
            {
                errors.Add(new ValidationError("type", "/colors", "must be object"));
            }
            else
            {
                CheckRequired(colorsObj, "/colors", ColorProperties.Where(p => !p.Optional).Select(p => p.Name), errors);
                foreach (var (property, _) in ColorProperties)
                {
                    if (colorsObj.TryGetPropertyValue(property, out var color)) CheckColor(color, Pointer("/colors", property), errors);
                }
            }
        }
        if (root.TryGetPropertyValue("export", out var export))
        {
            if (!IsObject(export))
            {
                errors.Add(new ValidationError("type", "/export", "must be object"));
            }
            else
            {
                foreach (var property in new[] { "pageBg", "cardBg", "infoBg" })
                {
                    if (((JsonObject)export!).TryGetPropertyValue(property, out var value)) CheckColor(value, Pointer("/export", property), errors);
                }
            }
        }
        return errors.Errors;
    }

    /// <summary>Validate one theme document, throwing a message that names the offending tokens.</summary>
    public static JsonObject Validate(string label, JsonNode? json)
    {
        var errors = Check(json);
        if (errors.Count > 0)
        {
            var missingColors = new SortedSet<string>(StringComparer.Ordinal);
            var otherErrors = new List<string>();
            foreach (var error in errors)
            {
                if (error.Keyword == "required" && error.InstancePath == "/colors")
                {
                    foreach (var property in error.RequiredProperties ?? []) missingColors.Add(property);
                    continue;
                }
                otherErrors.Add($"  - {(error.InstancePath.Length > 0 ? error.InstancePath : "/")}: {error.Message}");
            }
            var message = new StringBuilder($"Invalid theme \"{label}\":\n");
            if (missingColors.Count > 0)
            {
                message.Append("\nMissing required color tokens:\n");
                message.Append(string.Join("\n", missingColors.Select(c => $"  - {c}")));
                message.Append("\n\nPlease add these colors to your theme's \"colors\" object.");
                message.Append("\nSee the built-in themes (dark.json, light.json) for reference values.");
            }
            if (otherErrors.Count > 0) message.Append($"\n\nOther errors:\n{string.Join("\n", otherErrors)}");
            throw new InvalidDataException(message.ToString());
        }
        var theme = (JsonObject)json!;
        var themeName = theme["name"]!.GetValue<string>();
        if (themeName.Contains('/'))
        {
            throw new InvalidDataException($"Invalid theme name \"{themeName}\": theme names cannot contain \"/\" because it is reserved for automatic light/dark theme settings.");
        }
        return theme;
    }
}
