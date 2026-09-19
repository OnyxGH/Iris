using System.Text.Json.Nodes;
using Iris.Ai;
using Iris.CodingAgent.Extensions.Llama;

namespace Iris.CodingAgent.Tests;

public class LlamaThinkingTests
{
    // The relevant part of a Qwen-style template with its own reasoning effort setting.
    private const string EffortTemplate = """
        {%- if enable_thinking is undefined or enable_thinking is true %}
            {%- set resolved_reasoning_effort = reasoning_effort|default('xhigh') %}
            {%- if resolved_reasoning_effort not in ('xhigh', 'medium', 'low') %}
                {{- raise_exception('Unexpected reasoning effort ' ~ reasoning_effort ~ '. Supported types are xhigh (default), medium, and low.') }}
            {%- endif %}
        {%- endif %}
        {%- if enable_thinking is defined and enable_thinking is false %}{{- '<think>\n\n</think>\n\n' }}{%- endif %}
        """;

    private const string SwitchOnlyTemplate = "{%- if enable_thinking is defined and enable_thinking is false %}<think></think>{%- endif %}";

    private sealed class PayloadCaptured : Exception;

    private static Model LlamaModel(LlamaThinking thinking)
    {
        var compat = new JsonObject { ["supportsDeveloperRole"] = false, ["supportsStore"] = false, ["maxTokensField"] = "max_tokens" };
        foreach (var (key, value) in thinking.Compat) compat[key] = value?.DeepClone();
        return new Model
        {
            Id = "local", Name = "local", Api = "openai-completions", Provider = "llama.cpp", BaseUrl = "http://127.0.0.1:9/v1",
            Reasoning = true, Input = ["text"], ContextWindow = 262144, MaxTokens = 262144,
            ThinkingLevelMap = thinking.ThinkingLevelMap, Compat = compat,
        };
    }

    private static async Task<JsonObject> Payload(Model model, ThinkingLevel level, ThinkingBudgets? budgets = null)
    {
        JsonObject? captured = null;
        await IrisAi.StreamSimple(model, new Context { Messages = [new UserMessage("hi", 1)] }, new SimpleStreamOptions
        {
            ApiKey = "none",
            Reasoning = level,
            ThinkingBudgets = budgets,
            OnPayload = (payload, _) =>
            {
                captured = (JsonObject)payload.DeepClone();
                throw new PayloadCaptured();
            },
        }).Result();
        return captured!;
    }

    [Fact]
    public void Offers_exactly_the_efforts_the_template_accepts()
    {
        Assert.Equal(["xhigh", "medium", "low"], LlamaThinking.AcceptedEfforts(EffortTemplate).OrderByDescending(x => x == "xhigh").ThenByDescending(x => x == "medium"));
        var thinking = LlamaThinking.Detect(new LlamaServerProps(null, EffortTemplate, SupportsReasoningEffort: true))!;
        var levels = ModelUtils.GetSupportedThinkingLevels(LlamaModel(thinking));
        Assert.Equal([ThinkingLevel.Off, ThinkingLevel.Low, ThinkingLevel.Medium, ThinkingLevel.XHigh], levels);
        Assert.Null(thinking.Compat["thinkingTokenBudgetField"]);
    }

    [Theory]
    [InlineData(ThinkingLevel.Low, "low")]
    [InlineData(ThinkingLevel.Medium, "medium")]
    [InlineData(ThinkingLevel.XHigh, "xhigh")]
    [InlineData(ThinkingLevel.High, "xhigh")]
    public async Task Sends_the_chosen_effort_to_the_template(ThinkingLevel level, string expected)
    {
        var model = LlamaModel(LlamaThinking.Detect(new LlamaServerProps(null, EffortTemplate, SupportsReasoningEffort: true))!);
        var payload = await Payload(model, ModelUtils.ClampThinkingLevel(model, level));
        var kwargs = payload["chat_template_kwargs"]!;
        Assert.True(kwargs["enable_thinking"]!.GetValue<bool>());
        Assert.Equal(expected, kwargs["reasoning_effort"]!.GetValue<string>());
        Assert.Null(payload["thinking_budget_tokens"]);
    }

    [Fact]
    public async Task Off_disables_thinking_without_an_effort()
    {
        var model = LlamaModel(LlamaThinking.Detect(new LlamaServerProps(null, EffortTemplate, SupportsReasoningEffort: true))!);
        var kwargs = (await Payload(model, ThinkingLevel.Off))["chat_template_kwargs"]!;
        Assert.False(kwargs["enable_thinking"]!.GetValue<bool>());
        Assert.Null(kwargs["reasoning_effort"]);
    }

    [Theory]
    [InlineData(ThinkingLevel.Low, 512)]
    [InlineData(ThinkingLevel.Medium, 2048)]
    [InlineData(ThinkingLevel.High, 8192)]
    [InlineData(ThinkingLevel.XHigh, null)]
    public async Task Caps_thinking_with_budgets_when_the_template_only_switches_it(ThinkingLevel level, int? expected)
    {
        var thinking = LlamaThinking.Detect(new LlamaServerProps(null, SwitchOnlyTemplate))!;
        var model = LlamaModel(thinking);
        Assert.Equal([ThinkingLevel.Off, ThinkingLevel.Low, ThinkingLevel.Medium, ThinkingLevel.High, ThinkingLevel.XHigh], ModelUtils.GetSupportedThinkingLevels(model));
        var payload = await Payload(model, level);
        Assert.True(payload["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
        Assert.Null(payload["chat_template_kwargs"]!["reasoning_effort"]);
        Assert.Equal((long?)expected, payload["thinking_budget_tokens"]?.GetValue<long>());
    }

    [Fact]
    public async Task User_budgets_override_the_defaults()
    {
        var model = LlamaModel(LlamaThinking.Detect(new LlamaServerProps(null, SwitchOnlyTemplate))!);
        var payload = await Payload(model, ThinkingLevel.Medium, new ThinkingBudgets { Medium = 6000 });
        Assert.Equal(6000L, payload["thinking_budget_tokens"]!.GetValue<long>());
    }

    [Fact]
    public void Templates_without_thinking_controls_are_not_reasoning_models()
    {
        Assert.Null(LlamaThinking.Detect(new LlamaServerProps(null, "{{ messages }}")));
        Assert.Null(LlamaThinking.Detect(new LlamaServerProps(null, null)));
    }

    [Fact]
    public void Effort_templates_without_a_list_get_low_medium_and_high()
    {
        var thinking = LlamaThinking.Detect(new LlamaServerProps(null, "Reasoning: {{ reasoning_effort }}", SupportsReasoningEffort: true))!;
        Assert.Equal([ThinkingLevel.Off, ThinkingLevel.Low, ThinkingLevel.Medium, ThinkingLevel.High], ModelUtils.GetSupportedThinkingLevels(LlamaModel(thinking)));
    }
}
