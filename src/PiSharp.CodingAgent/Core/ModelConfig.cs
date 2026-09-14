using System.Text.Json.Nodes;
using PiSharp.Ai.Json;
using PiSharp.Ai.Utils;
using PiSharp.CodingAgent.Utils;

namespace PiSharp.CodingAgent.Core;

/// <summary>One immutable load of models.json (providers kept as raw JSON). Port of core/model-config.ts.</summary>
public sealed class ModelConfig
{
    private readonly Dictionary<string, JsonObject> _providers;

    private ModelConfig(Dictionary<string, JsonObject> providers, string? error = null)
    {
        _providers = providers;
        Error = error;
    }

    public string? Error { get; }

    public static ModelConfig Empty { get; } = new([]);

    private static readonly Lazy<JsonObject> Schema = new(() => (JsonObject)JsonNode.Parse(SchemaJson)!);

    public static async Task<ModelConfig> LoadAsync(string? modelsJsonPath)
    {
        if (string.IsNullOrEmpty(modelsJsonPath)) return new ModelConfig([]);
        var path = PathUtils.NormalizePath(modelsJsonPath);
        string content;
        try
        {
            content = await File.ReadAllTextAsync(path);
        }
        catch (FileNotFoundException)
        {
            return new ModelConfig([]);
        }
        catch (DirectoryNotFoundException)
        {
            return new ModelConfig([]);
        }
        catch (Exception ex)
        {
            return new ModelConfig([], $"Failed to load models.json: {ex.Message}\n\nFile: {path}");
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(TextHelpers.StripJsonComments(TextHelpers.StripBom(content)));
        }
        catch (Exception ex)
        {
            return new ModelConfig([], $"Failed to parse models.json: {ex.Message}\n\nFile: {path}");
        }

        var errors = JsonSchemaValidator.Validate(Schema.Value, parsed);
        if (errors.Count > 0)
        {
            var formatted = string.Join("\n", errors.Select(e =>
            {
                var basePath = e.InstancePath.TrimStart('/').Replace('/', '.');
                var p = e.Keyword == "required" && e.RequiredProperties is { Count: > 0 } req
                    ? (basePath.Length > 0 ? $"{basePath}.{req[0]}" : req[0])
                    : (basePath.Length > 0 ? basePath : "root");
                return $"  - {p}: {e.Message}";
            }));
            return new ModelConfig([], $"Invalid models.json schema:\n{formatted}\n\nFile: {path}");
        }

        var providers = new Dictionary<string, JsonObject>();
        foreach (var (id, provider) in (JsonObject)parsed!["providers"]!)
        {
            providers[id] = (JsonObject)provider!.DeepClone();
        }
        return new ModelConfig(providers);
    }

    public static ModelConfig FromJson(JsonObject root)
    {
        var providers = new Dictionary<string, JsonObject>();
        foreach (var (id, provider) in root["providers"] as JsonObject ?? []) providers[id] = (JsonObject)provider!.DeepClone();
        return new ModelConfig(providers);
    }

    public JsonObject? GetProvider(string providerId) => _providers.GetValueOrDefault(providerId);

    public IReadOnlyList<string> ProviderIds => _providers.Keys.ToList();

    // JSON Schema equivalent of the TypeBox ModelsConfigSchema (compat unions accept any object).
    private const string SchemaJson = """
    {
      "type": "object",
      "required": ["providers"],
      "properties": {
        "providers": {
          "type": "object",
          "additionalProperties": {
            "type": "object",
            "properties": {
              "name": { "type": "string", "minLength": 1 },
              "baseUrl": { "type": "string", "minLength": 1 },
              "apiKey": { "type": "string", "minLength": 1 },
              "api": { "type": "string", "minLength": 1 },
              "oauth": { "const": "radius" },
              "headers": { "type": "object", "additionalProperties": { "type": "string" } },
              "compat": { "type": "object" },
              "authHeader": { "type": "boolean" },
              "models": {
                "type": "array",
                "items": {
                  "type": "object",
                  "required": ["id"],
                  "properties": {
                    "id": { "type": "string", "minLength": 1 },
                    "name": { "type": "string", "minLength": 1 },
                    "api": { "type": "string", "minLength": 1 },
                    "baseUrl": { "type": "string", "minLength": 1 },
                    "reasoning": { "type": "boolean" },
                    "thinkingLevelMap": { "type": "object", "additionalProperties": { "type": ["string", "null"] } },
                    "input": { "type": "array", "items": { "enum": ["text", "image"] } },
                    "cost": {
                      "type": "object",
                      "required": ["input", "output", "cacheRead", "cacheWrite"],
                      "properties": {
                        "input": { "type": "number" }, "output": { "type": "number" },
                        "cacheRead": { "type": "number" }, "cacheWrite": { "type": "number" },
                        "tiers": { "type": "array", "items": { "type": "object", "required": ["inputTokensAbove", "input", "output", "cacheRead", "cacheWrite"] } }
                      }
                    },
                    "contextWindow": { "type": "number" },
                    "maxTokens": { "type": "number" },
                    "samplingParams": { "type": "object" },
                    "headers": { "type": "object", "additionalProperties": { "type": "string" } },
                    "compat": { "type": "object" }
                  }
                }
              },
              "modelOverrides": {
                "type": "object",
                "additionalProperties": {
                  "type": "object",
                  "properties": {
                    "name": { "type": "string", "minLength": 1 },
                    "reasoning": { "type": "boolean" },
                    "thinkingLevelMap": { "type": "object", "additionalProperties": { "type": ["string", "null"] } },
                    "input": { "type": "array", "items": { "enum": ["text", "image"] } },
                    "cost": { "type": "object" },
                    "contextWindow": { "type": "number" },
                    "maxTokens": { "type": "number" },
                    "samplingParams": { "type": "object" },
                    "headers": { "type": "object", "additionalProperties": { "type": "string" } },
                    "compat": { "type": "object" }
                  }
                }
              }
            }
          }
        }
      }
    }
    """;
}
