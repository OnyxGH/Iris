using System.Text.Json.Nodes;
using PiSharp.Agent;
using PiSharp.Ai;
using PiSharp.Ai.Providers;

namespace PiSharp.Agent.Tests;

public class AgentTests
{
    private static (Agent Agent, FauxProvider Faux, List<AgentEvent> Events) Create(params AgentTool[] tools)
    {
        var faux = new FauxProvider();
        var agent = new Agent(new AgentOptions
        {
            Model = faux.GetModel(),
            Tools = [.. tools],
            StreamFn = DefaultStreamFn.FromSync(faux.StreamSimple),
        });
        var events = new List<AgentEvent>();
        agent.Subscribe(e =>
        {
            lock (events) events.Add(e);
        });
        return (agent, faux, events);
    }

    private static AgentTool EchoTool(List<string>? calls = null, int delayMs = 0) => new()
    {
        Name = "echo",
        Label = "Echo",
        Description = "Echo text",
        Parameters = JsonNode.Parse("{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\"}},\"required\":[\"text\"]}")!.AsObject(),
        Execute = async (id, args, ct, onUpdate) =>
        {
            if (delayMs > 0) await Task.Delay(delayMs, ct);
            var text = args["text"]!.GetValue<string>();
            lock (calls ?? []) calls?.Add(text);
            onUpdate?.Invoke(AgentToolResult.Text($"partial {text}"));
            return AgentToolResult.Text($"echo: {text}");
        },
    };

    [Fact]
    public async Task Prompt_produces_standard_event_sequence()
    {
        var (agent, faux, events) = Create();
        faux.SetResponses([FauxProvider.AssistantMessage("hello")]);
        await agent.PromptAsync("hi");

        var types = events.Select(e => e.Type).ToList();
        Assert.Equal(["agent_start", "turn_start", "message_start", "message_end", "message_start"], types.Take(5));
        Assert.Equal("agent_end", types[^1]);
        Assert.Equal(2, agent.State.Messages.Count);
        Assert.False(agent.State.IsStreaming);
        Assert.Equal("hello", ((TextContent)((AssistantMessage)agent.State.Messages[1]).Content[0]).Text);
    }

    [Fact]
    public async Task Executes_tools_and_continues_until_stop()
    {
        var calls = new List<string>();
        var (agent, faux, events) = Create(EchoTool(calls));
        faux.SetResponses(
        [
            FauxProvider.AssistantMessage([FauxProvider.ToolCall("echo", new JsonObject { ["text"] = "a" }, "c1")], StopReason.ToolUse),
            FauxProvider.AssistantMessage("done"),
        ]);
        await agent.PromptAsync("go");

        Assert.Equal(["a"], calls);
        var roles = agent.State.Messages.Select(m => m.Role).ToList();
        Assert.Equal(["user", "assistant", "toolResult", "assistant"], roles);
        var result = (ToolResultMessage)agent.State.Messages[2];
        Assert.Equal("echo: a", ((TextContent)result.Content[0]).Text);
        Assert.Contains(events, e => e is ToolExecutionUpdateEvent);
        Assert.Equal(2, events.Count(e => e is TurnStartEvent));
    }

    [Fact]
    public async Task Parallel_tool_results_are_emitted_in_source_order()
    {
        var calls = new List<string>();
        var (agent, faux, events) = Create(EchoTool(calls, delayMs: 30));
        faux.SetResponses(
        [
            FauxProvider.AssistantMessage([
                FauxProvider.ToolCall("echo", new JsonObject { ["text"] = "first" }, "c1"),
                FauxProvider.ToolCall("echo", new JsonObject { ["text"] = "second" }, "c2"),
            ], StopReason.ToolUse),
            FauxProvider.AssistantMessage("done"),
        ]);
        await agent.PromptAsync("go");
        var toolResults = agent.State.Messages.OfType<ToolResultMessage>().Select(m => m.ToolCallId).ToList();
        Assert.Equal(["c1", "c2"], toolResults);
        Assert.Equal(2, calls.Count);
    }

    [Fact]
    public async Task Invalid_arguments_become_error_results()
    {
        var (agent, faux, _) = Create(EchoTool());
        faux.SetResponses(
        [
            FauxProvider.AssistantMessage([FauxProvider.ToolCall("echo", new JsonObject(), "c1"), FauxProvider.ToolCall("missing", new JsonObject(), "c2")], StopReason.ToolUse),
            FauxProvider.AssistantMessage("done"),
        ]);
        await agent.PromptAsync("go");
        var results = agent.State.Messages.OfType<ToolResultMessage>().ToList();
        Assert.True(results[0].IsError);
        Assert.StartsWith("Validation failed for tool \"echo\"", ((TextContent)results[0].Content[0]).Text);
        Assert.Equal("Tool missing not found", ((TextContent)results[1].Content[0]).Text);
    }

    [Fact]
    public async Task Length_stop_fails_tool_calls_without_executing()
    {
        var calls = new List<string>();
        var (agent, faux, _) = Create(EchoTool(calls));
        faux.SetResponses(
        [
            FauxProvider.AssistantMessage([FauxProvider.ToolCall("echo", new JsonObject { ["text"] = "x" }, "c1")], StopReason.Length),
            FauxProvider.AssistantMessage("done"),
        ]);
        await agent.PromptAsync("go");
        Assert.Empty(calls);
        Assert.True(agent.State.Messages.OfType<ToolResultMessage>().Single().IsError);
    }

    [Fact]
    public async Task Steering_messages_are_injected_after_tool_batch()
    {
        var (agent, faux, _) = Create();
        var steered = false;
        var tool = new AgentTool
        {
            Name = "wait",
            Label = "Wait",
            Parameters = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            Execute = (_, _, _, _) =>
            {
                if (!steered)
                {
                    agent.Steer(new UserMessage("steer me"));
                    steered = true;
                }
                return Task.FromResult(AgentToolResult.Text("ok"));
            },
        };
        agent.State.Tools = [tool];
        faux.SetResponses(
        [
            FauxProvider.AssistantMessage([FauxProvider.ToolCall("wait", new JsonObject(), "c1")], StopReason.ToolUse),
            FauxProvider.AssistantMessage("after steer"),
        ]);
        await agent.PromptAsync("go");
        var roles = agent.State.Messages.Select(m => m.Role).ToList();
        Assert.Equal(["user", "assistant", "toolResult", "user", "assistant"], roles);
    }

    [Fact]
    public async Task Follow_up_runs_after_agent_would_stop()
    {
        var (agent, faux, _) = Create();
        faux.SetResponses([FauxProvider.AssistantMessage("first"), FauxProvider.AssistantMessage("second")]);
        agent.FollowUp(new UserMessage("later"));
        await agent.PromptAsync("now");
        Assert.Equal(["user", "assistant", "user", "assistant"], agent.State.Messages.Select(m => m.Role));
    }

    [Fact]
    public async Task Abort_produces_aborted_message()
    {
        var faux = new FauxProvider(new FauxProviderOptions { TokensPerSecond = 20 });
        var agent = new Agent(new AgentOptions { Model = faux.GetModel(), StreamFn = DefaultStreamFn.FromSync(faux.StreamSimple) });
        faux.SetResponses([FauxProvider.AssistantMessage(new string('x', 400))]);
        var run = agent.PromptAsync("hi");
        await Task.Delay(100);
        agent.Abort();
        await run;
        var last = Assert.IsType<AssistantMessage>(agent.State.Messages[^1]);
        Assert.Equal(StopReason.Aborted, last.StopReason);
        Assert.Equal("Request was aborted", agent.State.ErrorMessage);
    }

    [Fact]
    public async Task Continue_from_assistant_throws_without_queue()
    {
        var (agent, faux, _) = Create();
        faux.SetResponses([FauxProvider.AssistantMessage("x")]);
        await agent.PromptAsync("hi");
        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.ContinueAsync());
    }

    [Fact]
    public async Task Before_tool_call_can_block()
    {
        var calls = new List<string>();
        var (agent, faux, _) = Create(EchoTool(calls));
        agent.BeforeToolCall = (_, _) => Task.FromResult<BeforeToolCallResult?>(new BeforeToolCallResult { Block = true, Reason = "nope" });
        faux.SetResponses(
        [
            FauxProvider.AssistantMessage([FauxProvider.ToolCall("echo", new JsonObject { ["text"] = "x" }, "c1")], StopReason.ToolUse),
            FauxProvider.AssistantMessage("done"),
        ]);
        await agent.PromptAsync("go");
        Assert.Empty(calls);
        Assert.Equal("nope", ((TextContent)agent.State.Messages.OfType<ToolResultMessage>().Single().Content[0]).Text);
    }
}
