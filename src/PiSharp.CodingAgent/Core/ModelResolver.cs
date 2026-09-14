using System.Globalization;
using System.Text.RegularExpressions;
using PiSharp.Ai;
using PiSharp.CodingAgent.Utils;

namespace PiSharp.CodingAgent.Core;

public sealed record ScopedModel(Model Model, ThinkingLevel? ThinkingLevel = null);

public sealed record ParsedModelResult(Model? Model, ThinkingLevel? ThinkingLevel, string? Warning);

public sealed record ModelScopeDiagnostic(string Code, string Message, string Pattern);

public sealed record ResolveCliModelResult(Model? Model, ThinkingLevel? ThinkingLevel, string? Warning, string? Error);

public sealed record InitialModelResult(Model? Model, ThinkingLevel ThinkingLevel, string? FallbackMessage);

/// <summary>Model resolution, scoping and initial selection. Port of core/model-resolver.ts.</summary>
public static partial class ModelResolver
{
    public const ThinkingLevel DefaultThinkingLevel = ThinkingLevel.Medium;

    /// <summary>Default model ids per known provider (insertion order matters for fallback search).</summary>
    public static readonly IReadOnlyList<KeyValuePair<string, string>> DefaultModelPerProvider =
    [
        new("amazon-bedrock", "us.anthropic.claude-opus-4-6-v1"),
        new("ant-ling", "Ring-2.6-1T"),
        new("anthropic", "claude-opus-4-8"),
        new("openai", "gpt-5.5"),
        new("azure-openai-responses", "gpt-5.4"),
        new("openai-codex", "gpt-5.5"),
        new("radius", "balanced"),
        new("nvidia", "nvidia/nemotron-3-super-120b-a12b"),
        new("deepseek", "deepseek-v4-pro"),
        new("google", "gemini-3.1-pro-preview"),
        new("google-vertex", "gemini-3.1-pro-preview"),
        new("github-copilot", "gpt-5.4"),
        new("openrouter", "moonshotai/kimi-k2.6"),
        new("vercel-ai-gateway", "zai/glm-5.1"),
        new("xai", "grok-4.6"),
        new("groq", "openai/gpt-oss-120b"),
        new("cerebras", "gpt-oss-120b"),
        new("zai", "glm-5.3"),
        new("zai-coding-cn", "glm-5.3"),
        new("mistral", "devstral-medium-latest"),
        new("minimax", "MiniMax-M2.7"),
        new("minimax-cn", "MiniMax-M2.7"),
        new("moonshotai", "kimi-k2.6"),
        new("moonshotai-cn", "kimi-k2.6"),
        new("huggingface", "moonshotai/Kimi-K2.6"),
        new("fireworks", "accounts/fireworks/models/kimi-k2p6"),
        new("together", "moonshotai/Kimi-K2.6"),
        new("baseten", "zai-org/GLM-5.2"),
        new("opencode", "kimi-k2.6"),
        new("opencode-go", "kimi-k2.6"),
        new("kimi-coding", "kimi-for-coding"),
        new("cloudflare-workers-ai", "@cf/moonshotai/kimi-k2.6"),
        new("cloudflare-ai-gateway", "workers-ai/@cf/moonshotai/kimi-k2.6"),
        new("qwen-token-plan", "qwen3.7-max"),
        new("qwen-token-plan-cn", "qwen3.7-max"),
        new("qwen-token-plan-individual", "qwen3.8-max"),
        new("xiaomi", "mimo-v2.5-pro"),
        new("xiaomi-token-plan-cn", "mimo-v2.5-pro"),
        new("xiaomi-token-plan-ams", "mimo-v2.5-pro"),
        new("xiaomi-token-plan-sgp", "mimo-v2.5-pro"),
    ];

    public static string? DefaultModelFor(string provider) => DefaultModelPerProvider.FirstOrDefault(kv => kv.Key == provider).Value;

    [GeneratedRegex(@"-\d{8}$")]
    private static partial Regex DatePattern();

    private static bool IsAlias(string id) => id.EndsWith("-latest", StringComparison.Ordinal) || !DatePattern().IsMatch(id);

    public static bool IsValidThinkingLevel(string value) => ThinkingLevels.TryParse(value, out _);

    private static int LocaleCompare(string a, string b) => string.Compare(a, b, CultureInfo.InvariantCulture, CompareOptions.None);

    public static Model? FindExactModelReferenceMatch(string reference, IReadOnlyList<Model> models)
    {
        var trimmed = reference.Trim();
        if (trimmed.Length == 0) return null;
        var normalized = trimmed.ToLowerInvariant();

        var canonical = models.Where(m => $"{m.Provider}/{m.Id}".ToLowerInvariant() == normalized).ToList();
        if (canonical.Count == 1) return canonical[0];
        if (canonical.Count > 1) return null;

        var slash = trimmed.IndexOf('/');
        if (slash != -1)
        {
            var provider = trimmed[..slash].Trim();
            var modelId = trimmed[(slash + 1)..].Trim();
            if (provider.Length > 0 && modelId.Length > 0)
            {
                var providerMatches = models.Where(m => string.Equals(m.Provider, provider, StringComparison.OrdinalIgnoreCase) && string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase)).ToList();
                if (providerMatches.Count == 1) return providerMatches[0];
                if (providerMatches.Count > 1) return null;
            }
        }

        var idMatches = models.Where(m => m.Id.ToLowerInvariant() == normalized).ToList();
        return idMatches.Count == 1 ? idMatches[0] : null;
    }

    private static Model? TryMatchModel(string pattern, IReadOnlyList<Model> models)
    {
        var exact = FindExactModelReferenceMatch(pattern, models);
        if (exact is not null) return exact;
        var lower = pattern.ToLowerInvariant();
        var matches = models.Where(m => m.Id.ToLowerInvariant().Contains(lower) || (m.Name?.ToLowerInvariant().Contains(lower) ?? false)).ToList();
        if (matches.Count == 0) return null;
        var aliases = matches.Where(m => IsAlias(m.Id)).ToList();
        var dated = matches.Where(m => !IsAlias(m.Id)).ToList();
        if (aliases.Count > 0)
        {
            aliases.Sort((a, b) => LocaleCompare(b.Id, a.Id));
            return aliases[0];
        }
        dated.Sort((a, b) => LocaleCompare(b.Id, a.Id));
        return dated[0];
    }

    private static Model? BuildFallbackModel(string provider, string modelId, IReadOnlyList<Model> models)
    {
        var providerModels = models.Where(m => m.Provider == provider).ToList();
        if (providerModels.Count == 0) return null;
        var defaultId = DefaultModelFor(provider);
        var baseModel = defaultId is not null ? providerModels.FirstOrDefault(m => m.Id == defaultId) ?? providerModels[0] : providerModels[0];
        var fallback = baseModel.Clone();
        fallback.Id = modelId;
        fallback.Name = modelId;
        return fallback;
    }

    public static ParsedModelResult ParseModelPattern(string pattern, IReadOnlyList<Model> models, bool allowInvalidThinkingLevelFallback = true)
    {
        var exact = TryMatchModel(pattern, models);
        if (exact is not null) return new ParsedModelResult(exact, null, null);

        var lastColon = pattern.LastIndexOf(':');
        if (lastColon == -1) return new ParsedModelResult(null, null, null);

        var prefix = pattern[..lastColon];
        var suffix = pattern[(lastColon + 1)..];
        if (ThinkingLevels.TryParse(suffix, out var level))
        {
            var result = ParseModelPattern(prefix, models, allowInvalidThinkingLevelFallback);
            if (result.Model is not null) return result with { ThinkingLevel = result.Warning is not null ? null : level };
            return result;
        }

        if (!allowInvalidThinkingLevelFallback) return new ParsedModelResult(null, null, null);
        var inner = ParseModelPattern(prefix, models, allowInvalidThinkingLevelFallback);
        if (inner.Model is not null)
        {
            return new ParsedModelResult(inner.Model, null, $"Invalid thinking level \"{suffix}\" in pattern \"{pattern}\". Using default instead.");
        }
        return inner;
    }

    public static (List<ScopedModel> ScopedModels, List<ModelScopeDiagnostic> Diagnostics) ResolveModelScopeFromModels(IEnumerable<string> patterns, IReadOnlyList<Model> models)
    {
        var scoped = new List<ScopedModel>();
        var diagnostics = new List<ModelScopeDiagnostic>();

        void Add(Model model, ThinkingLevel? level)
        {
            if (!scoped.Any(s => ModelUtils.ModelsAreEqual(s.Model, model))) scoped.Add(new ScopedModel(model, level));
        }

        foreach (var pattern in patterns)
        {
            if (pattern.Contains('*') || pattern.Contains('?') || pattern.Contains('['))
            {
                var colon = pattern.LastIndexOf(':');
                var globPattern = pattern;
                ThinkingLevel? level = null;
                if (colon != -1 && ThinkingLevels.TryParse(pattern[(colon + 1)..], out var parsed))
                {
                    level = parsed;
                    globPattern = pattern[..colon];
                }
                var exact = FindExactModelReferenceMatch(globPattern, models);
                if (exact is not null)
                {
                    Add(exact, level);
                    continue;
                }
                var matching = models.Where(m => Glob.IsMatch($"{m.Provider}/{m.Id}", globPattern, noCase: true) || Glob.IsMatch(m.Id, globPattern, noCase: true)).ToList();
                if (matching.Count == 0)
                {
                    diagnostics.Add(new ModelScopeDiagnostic("no-match", $"No models match pattern \"{pattern}\"", pattern));
                    continue;
                }
                foreach (var model in matching) Add(model, level);
                continue;
            }

            var (m, thinking, warning) = ParseModelPattern(pattern, models);
            if (warning is not null) diagnostics.Add(new ModelScopeDiagnostic("invalid-thinking-level", warning, pattern));
            if (m is null)
            {
                diagnostics.Add(new ModelScopeDiagnostic("no-match", $"No models match pattern \"{pattern}\"", pattern));
                continue;
            }
            Add(m, thinking);
        }
        return (scoped, diagnostics);
    }

    public static async Task<List<ScopedModel>> ResolveModelScopeAsync(IEnumerable<string> patterns, ModelRuntime runtime, CancellationToken ct = default)
    {
        var (scoped, diagnostics) = ResolveModelScopeFromModels(patterns, await runtime.GetAvailableAsync(null, ct));
        foreach (var diagnostic in diagnostics) Console.Error.WriteLine(Chalk.Yellow($"Warning: {diagnostic.Message}"));
        return scoped;
    }

    public static ResolveCliModelResult ResolveCliModel(string? cliProvider, string? cliModel, ThinkingLevel? cliThinking, ModelRuntime runtime)
    {
        if (string.IsNullOrEmpty(cliModel)) return new ResolveCliModelResult(null, null, null, null);
        var models = runtime.GetModels();
        if (models.Count == 0) return new ResolveCliModelResult(null, null, null, "No models available. Check your installation or add models to models.json.");

        var providerMap = new Dictionary<string, string>();
        foreach (var m in models) providerMap[m.Provider.ToLowerInvariant()] = m.Provider;

        string? provider = cliProvider is not null ? providerMap.GetValueOrDefault(cliProvider.ToLowerInvariant()) : null;
        if (cliProvider is not null && provider is null)
            return new ResolveCliModelResult(null, null, null, $"Unknown provider \"{cliProvider}\". Use --list-models to see available providers/models.");

        var pattern = cliModel;
        var inferredProvider = false;
        if (provider is null)
        {
            var slash = cliModel.IndexOf('/');
            if (slash != -1 && providerMap.TryGetValue(cliModel[..slash].ToLowerInvariant(), out var canonical))
            {
                provider = canonical;
                pattern = cliModel[(slash + 1)..];
                inferredProvider = true;
            }
        }

        if (provider is null)
        {
            var lower = cliModel.ToLowerInvariant();
            var exactMatches = models.Where(m => m.Id.ToLowerInvariant() == lower || $"{m.Provider}/{m.Id}".ToLowerInvariant() == lower).ToList();
            if (exactMatches.Count == 1) return new ResolveCliModelResult(exactMatches[0], null, null, null);
            if (exactMatches.Count > 1)
            {
                var authenticated = exactMatches.Where(m => runtime.HasConfiguredAuth(m.Provider)).ToList();
                if (authenticated.Count == 1) return new ResolveCliModelResult(authenticated[0], null, null, null);
                var list = string.Join(", ", exactMatches.Select(m => $"{m.Provider}/{m.Id}").OrderBy(s => s, StringComparer.InvariantCulture));
                var hint = authenticated.Count == 0 ? "No matching provider is authenticated." : "More than one matching provider is authenticated.";
                return new ResolveCliModelResult(null, null, null, $"Model \"{cliModel}\" is ambiguous across providers: {list}. {hint} Use --provider or provider/model.");
            }
        }

        if (cliProvider is not null && provider is not null && cliModel.StartsWith($"{provider}/", StringComparison.OrdinalIgnoreCase))
        {
            pattern = cliModel[(provider.Length + 1)..];
        }

        var candidates = provider is not null ? models.Where(m => m.Provider == provider).ToList() : models.ToList();
        var parsed = ParseModelPattern(pattern, candidates, allowInvalidThinkingLevelFallback: false);
        if (parsed.Model is not null)
        {
            if (inferredProvider)
            {
                var rawExact = models.Where(m => string.Equals(m.Id, cliModel, StringComparison.OrdinalIgnoreCase) && !ModelUtils.ModelsAreEqual(m, parsed.Model)).ToList();
                if (rawExact.Count > 0 && !runtime.HasConfiguredAuth(parsed.Model.Provider))
                {
                    var authenticatedRaw = rawExact.Where(m => runtime.HasConfiguredAuth(m.Provider)).ToList();
                    if (authenticatedRaw.Count == 1) return new ResolveCliModelResult(authenticatedRaw[0], null, null, null);
                }
            }
            return new ResolveCliModelResult(parsed.Model, parsed.ThinkingLevel, parsed.Warning, null);
        }

        if (inferredProvider)
        {
            var lower = cliModel.ToLowerInvariant();
            var exact = models.FirstOrDefault(m => m.Id.ToLowerInvariant() == lower || $"{m.Provider}/{m.Id}".ToLowerInvariant() == lower);
            if (exact is not null) return new ResolveCliModelResult(exact, null, null, null);
            var fallback = ParseModelPattern(cliModel, models, allowInvalidThinkingLevelFallback: false);
            if (fallback.Model is not null) return new ResolveCliModelResult(fallback.Model, fallback.ThinkingLevel, fallback.Warning, null);
        }

        if (provider is not null)
        {
            var fallbackPattern = pattern;
            ThinkingLevel? fallbackThinking = null;
            if (cliThinking is null)
            {
                var lastColon = pattern.LastIndexOf(':');
                if (lastColon != -1 && ThinkingLevels.TryParse(pattern[(lastColon + 1)..], out var suffixLevel))
                {
                    fallbackPattern = pattern[..lastColon];
                    fallbackThinking = suffixLevel;
                }
            }
            var fallbackModel = BuildFallbackModel(provider, fallbackPattern, models);
            if (fallbackModel is not null)
            {
                var requested = cliThinking ?? fallbackThinking;
                if (requested is not null and not ThinkingLevel.Off) fallbackModel.Reasoning = true;
                var warning = parsed.Warning is not null
                    ? $"{parsed.Warning} Model \"{fallbackPattern}\" not found for provider \"{provider}\". Using custom model id."
                    : $"Model \"{fallbackPattern}\" not found for provider \"{provider}\". Using custom model id.";
                return new ResolveCliModelResult(fallbackModel, fallbackThinking, warning, null);
            }
        }

        var display = provider is not null ? $"{provider}/{pattern}" : cliModel;
        return new ResolveCliModelResult(null, null, parsed.Warning, $"Model \"{display}\" not found. Use --list-models to see available models.");
    }

    private static Model? FindDefaultAvailable(IReadOnlyList<Model> available)
    {
        foreach (var (provider, defaultId) in DefaultModelPerProvider)
        {
            var match = available.FirstOrDefault(m => m.Provider == provider && m.Id == defaultId);
            if (match is not null) return match;
        }
        return null;
    }

    public static InitialModelResult FindInitialModel(
        string? cliProvider,
        string? cliModel,
        IReadOnlyList<ScopedModel> scopedModels,
        bool isContinuing,
        string? defaultProvider,
        string? defaultModelId,
        ThinkingLevel? defaultThinkingLevel,
        IReadOnlyDictionary<string, ThinkingLevel>? modelThinkingLevels,
        ModelRuntime runtime)
    {
        if (cliProvider is not null && cliModel is not null)
        {
            var resolved = ResolveCliModel(cliProvider, cliModel, null, runtime);
            if (resolved.Error is not null)
            {
                Console.Error.WriteLine(Chalk.Red(resolved.Error));
                Environment.Exit(1);
            }
            if (resolved.Model is not null) return new InitialModelResult(resolved.Model, DefaultThinkingLevel, null);
        }

        if (scopedModels.Count > 0 && !isContinuing)
        {
            var scoped = scopedModels[0];
            ThinkingLevel? perModel = modelThinkingLevels is not null && modelThinkingLevels.TryGetValue($"{scoped.Model.Provider}/{scoped.Model.Id}", out var pm) ? pm : null;
            return new InitialModelResult(scoped.Model, scoped.ThinkingLevel ?? perModel ?? defaultThinkingLevel ?? DefaultThinkingLevel, null);
        }

        if (defaultProvider is not null && defaultModelId is not null)
        {
            var found = runtime.GetModel(defaultProvider, defaultModelId);
            if (found is not null && runtime.HasConfiguredAuth(found.Provider))
            {
                var level = DefaultThinkingLevel;
                if (modelThinkingLevels is not null && modelThinkingLevels.TryGetValue($"{defaultProvider}/{defaultModelId}", out var perModel)) level = perModel;
                else if (defaultThinkingLevel is not null) level = defaultThinkingLevel.Value;
                return new InitialModelResult(found, level, null);
            }
        }

        var available = runtime.AvailableSnapshot;
        if (available.Count > 0) return new InitialModelResult(FindDefaultAvailable(available) ?? available[0], DefaultThinkingLevel, null);
        return new InitialModelResult(null, DefaultThinkingLevel, null);
    }

    public static (Model? Model, string? FallbackMessage) RestoreModelFromSession(string savedProvider, string savedModelId, Model? currentModel, bool shouldPrintMessages, ModelRuntime runtime)
    {
        var restored = runtime.GetModel(savedProvider, savedModelId);
        var hasAuth = restored is not null && runtime.HasConfiguredAuth(restored.Provider);
        if (restored is not null && hasAuth)
        {
            if (shouldPrintMessages) Console.WriteLine(Chalk.Dim($"Restored model: {savedProvider}/{savedModelId}"));
            return (restored, null);
        }
        var reason = restored is null ? "model no longer exists" : "no auth configured";
        if (shouldPrintMessages) Console.Error.WriteLine(Chalk.Yellow($"Warning: Could not restore model {savedProvider}/{savedModelId} ({reason})."));
        if (currentModel is not null)
        {
            if (shouldPrintMessages) Console.WriteLine(Chalk.Dim($"Falling back to: {currentModel.Provider}/{currentModel.Id}"));
            return (currentModel, $"Could not restore model {savedProvider}/{savedModelId} ({reason}). Using {currentModel.Provider}/{currentModel.Id}.");
        }
        var available = runtime.AvailableSnapshot;
        if (available.Count > 0)
        {
            var fallback = FindDefaultAvailable(available) ?? available[0];
            if (shouldPrintMessages) Console.WriteLine(Chalk.Dim($"Falling back to: {fallback.Provider}/{fallback.Id}"));
            return (fallback, $"Could not restore model {savedProvider}/{savedModelId} ({reason}). Using {fallback.Provider}/{fallback.Id}.");
        }
        return (null, null);
    }
}
