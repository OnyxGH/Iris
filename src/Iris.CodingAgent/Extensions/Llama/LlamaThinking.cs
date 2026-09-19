using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Iris.CodingAgent.Extensions.Llama;

/// <summary>
/// How a llama.cpp model's thinking is controlled, derived from its chat template. Templates with a reasoning_effort
/// variable get that variable set to the chosen level (offering only the values the template accepts); templates that
/// can only switch thinking on and off get a per-level thinking_budget_tokens cap, as llama.cpp's web UI does.
/// </summary>
public sealed record LlamaThinking(Dictionary<string, string?> ThinkingLevelMap, JsonObject Compat)
{
    /// <summary>Budgets used by llama.cpp's web UI for low, medium and high; xhigh leaves thinking unlimited.</summary>
    public static readonly IReadOnlyDictionary<string, int?> DefaultBudgets = new Dictionary<string, int?>
    {
        ["minimal"] = 256,
        ["low"] = 512,
        ["medium"] = 2048,
        ["high"] = 8192,
        ["xhigh"] = null,
    };

    private static readonly string[] EffortNames = ["none", "minimal", "low", "medium", "high", "xhigh", "max"];

    private static readonly Regex QuotedWord = new("""['"]([a-z]+)['"]""", RegexOptions.Compiled);

    /// <summary>The thinking configuration for a loaded model, or null when its template has no thinking switch.</summary>
    public static LlamaThinking? Detect(LlamaServerProps? props)
    {
        var template = props?.ChatTemplate;
        if (template is null) return null;
        var hasSwitch = template.Contains("enable_thinking", StringComparison.Ordinal);
        var hasEffort = props!.SupportsReasoningEffort || template.Contains("reasoning_effort", StringComparison.Ordinal);
        if (!hasSwitch && !hasEffort) return null;

        var kwargs = new JsonObject
        {
            ["enable_thinking"] = new JsonObject { ["$var"] = "thinking.enabled" },
            ["preserve_thinking"] = true,
        };
        var compat = new JsonObject { ["thinkingFormat"] = "chat-template", ["chatTemplateKwargs"] = kwargs };

        if (hasEffort && EffortMap(AcceptedEfforts(template)) is { } effortMap)
        {
            kwargs["reasoning_effort"] = new JsonObject { ["$var"] = "thinking.effort", ["omitWhenOff"] = true };
            return new LlamaThinking(effortMap, compat);
        }

        compat["thinkingTokenBudgetField"] = "thinking_budget_tokens";
        var budgets = new JsonObject();
        foreach (var (level, budget) in DefaultBudgets) budgets[level] = budget;
        compat["thinkingTokenBudgets"] = budgets;
        return new LlamaThinking(
            new Dictionary<string, string?> { ["off"] = "off", ["minimal"] = null, ["low"] = "low", ["medium"] = "medium", ["high"] = "high", ["xhigh"] = "xhigh" },
            compat);
    }

    /// <summary>
    /// The reasoning_effort values a template accepts, read from the quoted words on lines that mention the variable
    /// (a validation tuple, comparisons, a default). Empty when the template does not list any.
    /// </summary>
    public static HashSet<string> AcceptedEfforts(string template)
    {
        var accepted = new HashSet<string>();
        foreach (var line in template.Split('\n'))
        {
            if (!line.Contains("reasoning_effort", StringComparison.Ordinal)) continue;
            foreach (Match match in QuotedWord.Matches(line))
            {
                if (EffortNames.Contains(match.Groups[1].Value)) accepted.Add(match.Groups[1].Value);
            }
        }
        return accepted;
    }

    /// <summary>Map Iris levels to accepted effort values; templates that list none get the OpenAI-style low/medium/high.</summary>
    private static Dictionary<string, string?>? EffortMap(HashSet<string> accepted)
    {
        if (accepted.Count == 0) accepted = ["low", "medium", "high"];
        string? Pick(params string[] names) => names.FirstOrDefault(accepted.Contains);
        var map = new Dictionary<string, string?>
        {
            ["off"] = "off",
            ["minimal"] = Pick("minimal"),
            ["low"] = Pick("low"),
            ["medium"] = Pick("medium"),
            ["high"] = Pick("high"),
            ["xhigh"] = Pick("xhigh", "max"),
        };
        return map.Count(kv => kv.Key != "off" && kv.Value is not null) > 0 ? map : null;
    }
}
