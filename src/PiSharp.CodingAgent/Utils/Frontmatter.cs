using System.Text.Json.Nodes;
using YamlDotNet.RepresentationModel;

namespace PiSharp.CodingAgent.Utils;

public sealed record ParsedFrontmatter(JsonObject Frontmatter, string Body);

/// <summary>YAML frontmatter parsing. Port of utils/frontmatter.ts.</summary>
public static class Frontmatter
{
    private static string NormalizeNewlines(string value) => value.Replace("\r\n", "\n").Replace('\r', '\n');

    private static (string? Yaml, string Body) Extract(string content)
    {
        var normalized = NormalizeNewlines(TextHelpers.StripBom(content));
        if (!normalized.StartsWith("---", StringComparison.Ordinal)) return (null, normalized);
        var endIndex = normalized.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (endIndex == -1) return (null, normalized);
        return (normalized.Substring(Math.Min(4, normalized.Length), Math.Max(0, endIndex - 4)), normalized[(endIndex + 4)..].Trim());
    }

    public static ParsedFrontmatter Parse(string content)
    {
        var (yaml, body) = Extract(content);
        if (string.IsNullOrEmpty(yaml)) return new ParsedFrontmatter(new JsonObject(), body);
        return new ParsedFrontmatter(ParseYaml(yaml) as JsonObject ?? new JsonObject(), body);
    }

    public static string Strip(string content) => Parse(content).Body;

    /// <summary>Parse a YAML document into a JSON node (scalars typed like the JS yaml package's core schema).</summary>
    public static JsonNode? ParseYaml(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        if (stream.Documents.Count == 0) return null;
        return Convert(stream.Documents[0].RootNode);
    }

    private static JsonNode? Convert(YamlNode node)
    {
        switch (node)
        {
            case YamlMappingNode mapping:
            {
                var obj = new JsonObject();
                foreach (var (key, value) in mapping.Children)
                {
                    var name = key is YamlScalarNode ks ? ks.Value ?? "" : key.ToString();
                    obj[name] = Convert(value);
                }
                return obj;
            }
            case YamlSequenceNode sequence:
            {
                var arr = new JsonArray();
                foreach (var child in sequence.Children) arr.Add(Convert(child));
                return arr;
            }
            case YamlScalarNode scalar:
                return ConvertScalar(scalar);
            default:
                return null;
        }
    }

    private static JsonNode? ConvertScalar(YamlScalarNode scalar)
    {
        var value = scalar.Value ?? "";
        if (scalar.Style is YamlDotNet.Core.ScalarStyle.SingleQuoted or YamlDotNet.Core.ScalarStyle.DoubleQuoted
            or YamlDotNet.Core.ScalarStyle.Literal or YamlDotNet.Core.ScalarStyle.Folded)
        {
            return JsonValue.Create(value);
        }
        switch (value)
        {
            case "" or "~" or "null" or "Null" or "NULL":
                return null;
            case "true" or "True" or "TRUE":
                return JsonValue.Create(true);
            case "false" or "False" or "FALSE":
                return JsonValue.Create(false);
        }
        if (System.Text.RegularExpressions.Regex.IsMatch(value, @"^[-+]?[0-9]+$") && long.TryParse(value, out var l)) return JsonValue.Create(l);
        if (System.Text.RegularExpressions.Regex.IsMatch(value, @"^[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?$")
            && double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
        {
            return JsonValue.Create(d);
        }
        return JsonValue.Create(value);
    }
}
