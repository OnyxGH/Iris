using System.Text.Json.Nodes;
using Iris.Ai;
using Iris.Ai.Json;
using Iris.Ai.Providers;
using Iris.Ai.Utils;

namespace Iris.Ai.Tests;

public class JsonTests
{
    [Fact]
    public void Message_round_trips_with_role_and_type_discriminators()
    {
        var message = new AssistantMessage
        {
            Content = [new ThinkingContent("hmm") { ThinkingSignature = "sig" }, new TextContent("hi"), new ToolCall { Id = "t1", Name = "read", Arguments = new JsonObject { ["path"] = "a.txt" } }],
            Api = "openai-completions",
            Provider = "p",
            Model = "m",
            StopReason = StopReason.ToolUse,
            Timestamp = 123,
        };
        var json = IrisJson.Serialize<Message>(message);
        Assert.StartsWith("{\"role\":\"assistant\",\"content\":[{\"type\":\"thinking\"", json);
        Assert.Contains("\"stopReason\":\"toolUse\"", json);
        Assert.DoesNotContain("errorMessage", json);

        var back = Assert.IsType<AssistantMessage>(IrisJson.Deserialize<Message>(json));
        Assert.Equal(3, back.Content.Count);
        Assert.Equal("a.txt", back.Content.OfType<ToolCall>().Single().Arguments["path"]!.GetValue<string>());
        Assert.Equal(json, IrisJson.Serialize<Message>(back));
    }

    [Fact]
    public void User_content_preserves_string_vs_array_shape()
    {
        var text = IrisJson.Serialize<Message>(new UserMessage("hello", 1));
        Assert.Equal("{\"role\":\"user\",\"content\":\"hello\",\"timestamp\":1}", text);
        var blocks = IrisJson.Serialize<Message>(new UserMessage(UserContent.FromBlocks([new TextContent("a"), new ImageContent("AAA", "image/png")]), 2));
        Assert.Equal("{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"a\"},{\"type\":\"image\",\"data\":\"AAA\",\"mimeType\":\"image/png\"}],\"timestamp\":2}", blocks);
    }

    [Fact]
    public void Unknown_roles_are_preserved_verbatim()
    {
        const string json = "{\"role\":\"bashExecution\",\"command\":\"ls\",\"timestamp\":5}";
        var message = IrisJson.Deserialize<Message>(json);
        var unknown = Assert.IsType<UnknownMessage>(message);
        Assert.Equal(5, unknown.Timestamp);
        Assert.Equal(json, IrisJson.Serialize(message));
    }
}

public class JsonParseTests
{
    [Theory]
    [InlineData("{\"a\":\"hel", "{\"a\":\"hel\"}")]
    [InlineData("{\"a\":1,\"b\":[1,2", "{\"a\":1,\"b\":[1,2]}")]
    [InlineData("{\"a\":tr", "{\"a\":true}")]
    [InlineData("{\"path\":\"x\",\"cont", "{\"path\":\"x\"}")]
    [InlineData("", "{}")]
    [InlineData("{\"a\":{\"b\":\"c\"}}", "{\"a\":{\"b\":\"c\"}}")]
    public void ParseStreamingJson_recovers_partial_objects(string input, string expected)
    {
        Assert.Equal(expected, IrisJson.Stringify(JsonParse.ParseStreamingJson(input)));
    }

    [Fact]
    public void RepairJson_escapes_control_characters_and_bad_escapes()
    {
        var repaired = JsonParse.RepairJson("{\"a\":\"line1\nline2 \\d\"}");
        Assert.Equal("{\"a\":\"line1\\nline2 \\\\d\"}", repaired);
        Assert.Equal("line1\nline2 \\d", JsonParse.ParseStreamingJson("{\"a\":\"line1\nline2 \\d\"}")["a"]!.GetValue<string>());
    }
}

public class TextUtilsTests
{
    [Fact]
    public void SanitizeSurrogates_removes_only_unpaired()
    {
        Assert.Equal("Hello 🙈 World", TextUtils.SanitizeSurrogates("Hello 🙈 World"));
        Assert.Equal("Text  here", TextUtils.SanitizeSurrogates($"Text {(char)0xD83D} here"));
    }

    [Fact]
    public void ShortHash_matches_javascript_implementation()
    {
        // Values computed with pi-ai's shortHash in Node.
        Assert.Equal("1h6qa0qrowduu", TextUtils.ShortHash("hello"));
    }

    [Fact]
    public void UuidV7_has_version_and_is_ordered()
    {
        var a = UuidV7.New();
        var b = UuidV7.New();
        Assert.Equal('7', a[14]);
        Assert.True(string.CompareOrdinal(a, b) < 0);
    }
}

public class OverflowTests
{
    [Theory]
    [InlineData("400: {\"code\":400,\"message\":\"the request exceeds the available context size, try increasing it\",\"type\":\"exceed_context_size_error\"}")]
    [InlineData("prompt is too long: 213462 tokens > 200000 maximum")]
    public void Detects_overflow_errors(string error)
    {
        var message = new AssistantMessage { StopReason = StopReason.Error, ErrorMessage = error };
        Assert.True(Overflow.IsContextOverflow(message));
    }

    [Theory]
    [InlineData("400 status code (no body)")]
    [InlineData("413 status code (no body)")]
    public void Bodyless_errors_are_overflow_only_for_cerebras(string error)
    {
        Assert.True(Overflow.IsContextOverflow(new AssistantMessage { Provider = "cerebras", StopReason = StopReason.Error, ErrorMessage = error }));
        Assert.False(Overflow.IsContextOverflow(new AssistantMessage { Provider = "openai", StopReason = StopReason.Error, ErrorMessage = error }));
    }

    [Theory]
    [InlineData("520 status code (no body)")]
    [InlineData("The model is currently experiencing high demand. Please try again later.")]
    public void Transient_provider_errors_are_retryable(string error) =>
        Assert.True(AssistantRetry.IsRetryableAssistantError(new AssistantMessage { StopReason = StopReason.Error, ErrorMessage = error }));

    [Fact]
    public void Rate_limits_are_not_overflow()
    {
        var message = new AssistantMessage { StopReason = StopReason.Error, ErrorMessage = "ThrottlingException: Too many tokens, rate limit" };
        Assert.False(Overflow.IsContextOverflow(message));
        Assert.True(AssistantRetry.IsRetryableAssistantError(message));
    }
}

public class ValidationTests
{
    private static (Tool, ToolCall) Echo(string schema, JsonNode? value)
    {
        var tool = new Tool
        {
            Name = "echo",
            Description = "Echo tool",
            Parameters = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["value"] = JsonNode.Parse(schema) },
                ["required"] = new JsonArray("value"),
            },
        };
        return (tool, new ToolCall { Id = "tool-1", Name = "echo", Arguments = new JsonObject { ["value"] = value } });
    }

    [Theory]
    [InlineData("{\"type\":\"number\"}", "\"42\"", "42")]
    [InlineData("{\"type\":\"number\"}", "true", "1")]
    [InlineData("{\"type\":\"number\"}", "null", "0")]
    [InlineData("{\"type\":\"integer\"}", "\"42\"", "42")]
    [InlineData("{\"type\":\"boolean\"}", "\"true\"", "true")]
    [InlineData("{\"type\":\"boolean\"}", "0", "false")]
    [InlineData("{\"type\":\"string\"}", "null", "\"\"")]
    [InlineData("{\"type\":\"string\"}", "true", "\"true\"")]
    [InlineData("{\"type\":\"null\"}", "\"\"", "null")]
    [InlineData("{\"type\":[\"number\",\"string\"]}", "\"1\"", "\"1\"")]
    [InlineData("{\"type\":[\"boolean\",\"number\"]}", "\"1\"", "1")]
    public void Coerces_primitives(string schema, string input, string expected)
    {
        var (tool, call) = Echo(schema, JsonNode.Parse(input));
        var result = ToolValidation.ValidateToolArguments(tool, call);
        Assert.Equal($"{{\"value\":{expected}}}", IrisJson.Stringify(result));
    }

    [Fact]
    public void Treats_null_as_omission_for_optional_properties()
    {
        var tool = new Tool
        {
            Name = "echo",
            Parameters = JsonNode.Parse("""
                {"type":"object","properties":{
                  "path":{"type":"string"},
                  "offset":{"type":"number"},
                  "nullable":{"anyOf":[{"type":"string"},{"type":"null"}]},
                  "metadata":{"type":"object","properties":{"enabled":{"type":"boolean"}}}
                },"required":["path","metadata"]}
                """)!.AsObject(),
        };
        var call = new ToolCall { Name = "echo", Arguments = JsonNode.Parse("{\"path\":\"file.txt\",\"offset\":null,\"nullable\":null,\"metadata\":{\"enabled\":null}}")!.AsObject() };
        Assert.Equal("{\"path\":\"file.txt\",\"nullable\":null,\"metadata\":{}}", IrisJson.Stringify(ToolValidation.ValidateToolArguments(tool, call)));
    }

    [Fact]
    public void Reports_missing_required_properties()
    {
        var tool = new Tool { Name = "read", Parameters = JsonNode.Parse("{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}},\"required\":[\"path\"]}")!.AsObject() };
        var ex = Assert.Throws<InvalidOperationException>(() => ToolValidation.ValidateToolArguments(tool, new ToolCall { Name = "read" }));
        Assert.StartsWith("Validation failed for tool \"read\":\n  - path: must have required properties path", ex.Message);
    }
}

public class TransformMessagesTests
{
    private static Model ClaudeModel() => new()
    {
        Id = "claude-sonnet-4.6", Api = "anthropic-messages", Provider = "github-copilot", Reasoning = true, Input = ["text", "image"],
    };

    [Fact]
    public void Converts_foreign_thinking_to_text_and_normalizes_ids()
    {
        var model = ClaudeModel();
        var messages = new List<Message>
        {
            new UserMessage("hello"),
            new AssistantMessage
            {
                Api = "openai-responses", Provider = "github-copilot", Model = "gpt-5", StopReason = StopReason.ToolUse,
                Content = [new ThinkingContent("Let me think") { ThinkingSignature = "reasoning_content" }, new ToolCall { Id = "call_1|fc_+/=", Name = "read" }],
            },
            new ToolResultMessage { ToolCallId = "call_1|fc_+/=", ToolName = "read", Content = [new TextContent("ok")] },
        };
        var result = MessageTransformer.Transform(messages, model, (id, _, _) => AnthropicMessagesApi.NormalizeToolCallId(id));
        var assistant = Assert.IsType<AssistantMessage>(result[1]);
        Assert.IsType<TextContent>(assistant.Content[0]);
        Assert.Equal("call_1_fc____", assistant.Content.OfType<ToolCall>().Single().Id);
        Assert.Equal("call_1_fc____", Assert.IsType<ToolResultMessage>(result[2]).ToolCallId);
    }

    [Fact]
    public void Inserts_synthetic_results_for_orphaned_tool_calls_and_drops_errored_turns()
    {
        var model = ClaudeModel();
        var messages = new List<Message>
        {
            new AssistantMessage { Api = model.Api, Provider = model.Provider, Model = model.Id, StopReason = StopReason.ToolUse, Content = [new ToolCall { Id = "a", Name = "bash" }] },
            new UserMessage("next"),
            new AssistantMessage { Api = model.Api, Provider = model.Provider, Model = model.Id, StopReason = StopReason.Error, Content = [new TextContent("partial")] },
        };
        var result = MessageTransformer.Transform(messages, model);
        Assert.Equal(3, result.Count);
        var synthetic = Assert.IsType<ToolResultMessage>(result[1]);
        Assert.True(synthetic.IsError);
        Assert.Equal("No result provided", ((TextContent)synthetic.Content[0]).Text);
    }
}

public class OpenAICompletionsPayloadTests
{
    private static Model LlamaModel(string? compat = null) => new()
    {
        Id = "Iris", Api = "openai-completions", Provider = "llama.cpp", BaseUrl = "http://localhost:8689/v1", Reasoning = true,
        Input = ["text"], ContextWindow = 262144, MaxTokens = 8192, Compat = compat is null ? null : JsonNode.Parse(compat)!.AsObject(),
    };

    [Fact]
    public void Builds_openai_style_reasoning_payload()
    {
        var model = LlamaModel();
        var p = OpenAICompletionsApi.BuildParams(model, new Context { SystemPrompt = "sys", Messages = [new UserMessage("hi")] },
            new OpenAICompletionsOptions { MaxTokens = 100, ReasoningEffort = ThinkingLevel.High });
        Assert.Equal("developer", p["messages"]![0]!["role"]!.GetValue<string>());
        Assert.Equal("high", p["reasoning_effort"]!.GetValue<string>());
        Assert.Equal(100, p["max_completion_tokens"]!.GetValue<int>());
        Assert.False(p["store"]!.GetValue<bool>());
        Assert.True(p["stream_options"]!["include_usage"]!.GetValue<bool>());
    }

    [Fact]
    public void Chat_template_kwargs_resolve_variables()
    {
        var model = LlamaModel("{\"thinkingFormat\":\"chat-template\",\"chatTemplateKwargs\":{\"enable_thinking\":{\"$var\":\"thinking.enabled\"},\"reasoning_effort\":{\"$var\":\"thinking.effort\",\"omitWhenOff\":true},\"fixed\":1}}");
        var on = OpenAICompletionsApi.BuildParams(model, new Context { Messages = [new UserMessage("hi")] }, new OpenAICompletionsOptions { ReasoningEffort = ThinkingLevel.Low });
        Assert.Equal("{\"enable_thinking\":true,\"reasoning_effort\":\"low\",\"fixed\":1}", IrisJson.Stringify(on["chat_template_kwargs"]));
        var off = OpenAICompletionsApi.BuildParams(model, new Context { Messages = [new UserMessage("hi")] }, new OpenAICompletionsOptions());
        Assert.Equal("{\"enable_thinking\":false,\"fixed\":1}", IrisJson.Stringify(off["chat_template_kwargs"]));
    }

    [Fact]
    public void Replays_reasoning_content_and_tool_calls()
    {
        var model = LlamaModel();
        var context = new Context
        {
            Messages =
            [
                new UserMessage("q"),
                new AssistantMessage
                {
                    Api = model.Api, Provider = model.Provider, Model = model.Id, StopReason = StopReason.ToolUse,
                    Content = [new ThinkingContent("thought") { ThinkingSignature = "reasoning_content" }, new ToolCall { Id = "x", Name = "calc", Arguments = new JsonObject { ["expr"] = "1+1" } }],
                },
                new ToolResultMessage { ToolCallId = "x", ToolName = "calc", Content = [new TextContent("2")] },
            ],
        };
        var p = OpenAICompletionsApi.BuildParams(model, context, new OpenAICompletionsOptions());
        var assistant = p["messages"]![1]!;
        Assert.Equal("thought", assistant["reasoning_content"]!.GetValue<string>());
        Assert.Equal("{\"expr\":\"1+1\"}", assistant["tool_calls"]![0]!["function"]!["arguments"]!.GetValue<string>());
        Assert.Equal("tool", p["messages"]![2]!["role"]!.GetValue<string>());
        Assert.Equal("[]", IrisJson.Stringify(p["tools"]));
    }
}

public class FauxProviderTests
{
    [Fact]
    public async Task Streams_scripted_responses_with_deltas()
    {
        var faux = new FauxProvider();
        faux.SetResponses([FauxProvider.AssistantMessage([FauxProvider.Thinking("plan"), FauxProvider.Text("hello world"), FauxProvider.ToolCall("read", new JsonObject { ["path"] = "a" }, "t1")], StopReason.ToolUse)]);
        var stream = faux.StreamSimple(faux.GetModel(), new Context { Messages = [new UserMessage("hi")] });
        var types = new List<string>();
        await foreach (var e in stream) types.Add(e.Type);
        var result = await stream.Result();
        Assert.Equal("start", types[0]);
        Assert.Equal("done", types[^1]);
        Assert.Contains("toolcall_end", types);
        Assert.Equal(StopReason.ToolUse, result.StopReason);
        Assert.Equal(faux.ApiId, result.Api);
        Assert.True(result.Usage.Input > 0);
    }

    [Fact]
    public async Task Errors_when_no_responses_are_queued()
    {
        var faux = new FauxProvider();
        var result = await faux.StreamSimple(faux.GetModel(), new Context()).Result();
        Assert.Equal(StopReason.Error, result.StopReason);
        Assert.Equal("No more faux responses queued", result.ErrorMessage);
    }

    [Fact]
    public async Task Works_through_models_collection()
    {
        var faux = new FauxProvider();
        var models = new Iris.Ai.Models.ModelsCollection();
        models.SetProvider(faux.Provider);
        faux.SetResponses([FauxProvider.AssistantMessage("hi")]);
        var result = await models.CompleteSimpleAsync(faux.GetModel(), new Context { Messages = [new UserMessage("x")] });
        Assert.Equal("hi", ((TextContent)result.Content[0]).Text);
    }
}

public class CatalogTests
{
    [Fact]
    public void Loads_builtin_catalog()
    {
        Assert.Contains("anthropic", BuiltinCatalog.GetProviders());
        var model = BuiltinCatalog.GetModels("anthropic").FirstOrDefault();
        Assert.NotNull(model);
        Assert.Equal("anthropic-messages", model!.Api);
        Assert.True(BuiltinCatalog.GeneratedAt > 0);
    }

    [Fact]
    public void Thinking_levels_respect_model_map()
    {
        var model = new Model { Reasoning = true, ThinkingLevelMap = new() { ["off"] = null, ["xhigh"] = "xhigh" } };
        Assert.Equal([ThinkingLevel.Minimal, ThinkingLevel.Low, ThinkingLevel.Medium, ThinkingLevel.High, ThinkingLevel.XHigh], ModelUtils.GetSupportedThinkingLevels(model));
        Assert.Equal(ThinkingLevel.Minimal, ModelUtils.ClampThinkingLevel(model, ThinkingLevel.Off));
        Assert.Equal(ThinkingLevel.XHigh, ModelUtils.ClampThinkingLevel(model, ThinkingLevel.Max));
    }
}
