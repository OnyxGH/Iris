using System.Text.Json.Nodes;
using Iris.Agent;
using Iris.Ai;
using Iris.CodingAgent.Core;
using Iris.CodingAgent.Core.Compaction;

namespace Iris.CodingAgent.Tests;

/// <summary>Compaction and branch summarization compared with pi on the same synthetic session (Fixtures/compaction.json).</summary>
public class CompactionFixtureTests
{
    private sealed record Call(string SystemPrompt, string Prompt, int? MaxTokens, ThinkingLevel? Reasoning, CacheRetention? CacheRetention, bool HasSessionId);

    private static readonly JsonNode Fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "compaction.json")))!;

    private static string? Str(JsonNode? node) => node?.GetValue<string>();

    // The fake model numbers its replies; pi generated the fixture with one counter across all scenarios.
    private static string NormalizeReplies(string? text) => System.Text.RegularExpressions.Regex.Replace(text ?? "", "SUMMARY [0-9]+", "SUMMARY n");

    private static SessionManager OpenSession()
    {
        CodingAgentMessages.Register();
        var dir = Path.Combine(Path.GetTempPath(), "iris-comp-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "session.jsonl");
        File.WriteAllText(path, Str(Fixture["sessionJsonl"]));
        return SessionManager.Open(path, dir);
    }

    private static Model TestModel() => new()
    {
        Id = "m", Name = "m", Api = "openai-completions", Provider = "p", BaseUrl = "http://x",
        Reasoning = true, Input = ["text"], ContextWindow = 32000, MaxTokens = 3000,
    };

    private static (StreamFn Fn, List<Call> Calls) FakeStream()
    {
        var calls = new List<Call>();
        var reply = 0;
        StreamFn fn = (model, context, options) =>
        {
            var prompt = ((TextContent)((UserMessage)context.Messages[0]).Content.Blocks![0]).Text;
            calls.Add(new Call(context.SystemPrompt ?? "", prompt, options?.MaxTokens, options?.Reasoning, options?.CacheRetention, options?.SessionId is not null));
            var n = Interlocked.Increment(ref reply);
            return Task.FromResult(AssistantMessageEventStream.Run(model, stream =>
            {
                var message = AssistantMessage.CreateEmpty(model, StopReason.Stop);
                message.Content = [new TextContent($"SUMMARY {n}")];
                message.Usage = new Usage { Input = 400, Output = 100, TotalTokens = 500 };
                stream.Push(new DoneEvent(StopReason.Stop, message));
                stream.End(message, hasResult: true);
                return Task.CompletedTask;
            }));
        };
        return (fn, calls);
    }

    private static void AssertCalls(JsonArray expected, List<Call> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            var e = expected[i]!;
            Assert.Equal(Str(e["systemPrompt"]), actual[i].SystemPrompt);
            Assert.Equal(Str(e["prompt"]), actual[i].Prompt);
            Assert.Equal(e["maxTokens"]!.GetValue<int>(), actual[i].MaxTokens);
            Assert.Equal(Str(e["reasoning"]), actual[i].Reasoning is { } r ? ThinkingLevels.ToWire(r) : null);
            Assert.Equal(CacheRetention.None, actual[i].CacheRetention);
            Assert.True(actual[i].HasSessionId);
        }
    }

    [Fact]
    public void EstimatesAndSerializationMatchPi()
    {
        var session = OpenSession();
        var messages = session.BuildSessionContext().Messages;
        Assert.Equal(Fixture["estimates"]!.AsArray().Select(e => e!.GetValue<long>()), messages.Select(Compactor.EstimateTokens));

        var estimate = Compactor.EstimateContextTokens(messages);
        var expected = Fixture["contextEstimate"]!;
        Assert.Equal(expected["tokens"]!.GetValue<long>(), estimate.Tokens);
        Assert.Equal(expected["usageTokens"]!.GetValue<long>(), estimate.UsageTokens);
        Assert.Equal(expected["trailingTokens"]!.GetValue<long>(), estimate.TrailingTokens);
        Assert.Equal(expected["lastUsageIndex"]?.GetValue<int>(), estimate.LastUsageIndex);

        var llm = messages.Where(m => m.Role is "user" or "assistant" or "toolResult");
        Assert.Equal(Str(Fixture["serialized"]), CompactionUtils.SerializeConversation(llm));
    }

    [Fact]
    public async Task CompactionMatchesPi()
    {
        var session = OpenSession();
        var branch = session.GetBranch();
        foreach (var (keep, expected) in Fixture["compactions"]!.AsObject())
        {
            var settings = new CompactionSettings { ReserveTokens = 4000, KeepRecentTokens = long.Parse(keep) };
            var preparation = Compactor.PrepareCompaction(branch, settings);
            if (expected is null)
            {
                Assert.Null(preparation);
                continue;
            }
            Assert.NotNull(preparation);
            Assert.Equal(Str(expected["firstKeptEntryId"]), preparation.FirstKeptEntryId);
            Assert.Equal(expected["isSplitTurn"]!.GetValue<bool>(), preparation.IsSplitTurn);
            Assert.Equal(expected["tokensBefore"]!.GetValue<long>(), preparation.TokensBefore);
            Assert.Equal(Str(expected["previousSummary"]), preparation.PreviousSummary);
            Assert.Equal(expected["messagesToSummarize"]!.GetValue<int>(), preparation.MessagesToSummarize.Count);
            Assert.Equal(expected["turnPrefixMessages"]!.GetValue<int>(), preparation.TurnPrefixMessages.Count);

            var (fn, calls) = FakeStream();
            var request = new SummarizationRequest { Model = TestModel(), ApiKey = "key", ThinkingLevel = ThinkingLevel.High, StreamFn = fn };
            var result = await Compactor.CompactAsync(preparation, request, keep == "6000" ? "focus on tests" : null);

            AssertCalls(expected["calls"]!.AsArray(), calls);
            var r = expected["result"]!;
            Assert.Equal(NormalizeReplies(Str(r["summary"])), NormalizeReplies(result.Summary));
            Assert.Equal(Str(r["firstKeptEntryId"]), result.FirstKeptEntryId);
            Assert.Equal(r["details"]!.ToJsonString(), result.Details!.ToJsonString());
        }
    }

    [Fact]
    public async Task BranchSummaryMatchesPi()
    {
        var session = OpenSession();
        var expected = Fixture["branch"]!;
        var collected = BranchSummarization.CollectEntriesForBranchSummary(session, session.LeafId, "e0020");
        Assert.Equal(Str(expected["commonAncestorId"]), collected.CommonAncestorId);
        Assert.Equal(expected["entryIds"]!.AsArray().Select(Str), collected.Entries.Select(e => e.Id));

        var prepared = BranchSummarization.PrepareBranchEntries(collected.Entries, 5000);
        Assert.Equal(expected["prepared"]!["count"]!.GetValue<int>(), prepared.Messages.Count);
        Assert.Equal(expected["prepared"]!["totalTokens"]!.GetValue<long>(), prepared.TotalTokens);

        var (fn, calls) = FakeStream();
        var result = await BranchSummarization.GenerateBranchSummaryAsync(collected.Entries,
            new SummarizationRequest { Model = TestModel(), ApiKey = "k", StreamFn = fn }, reserveTokens: 24000);

        var e = expected["calls"]!.AsArray();
        Assert.Single(calls);
        Assert.Equal(Str(e[0]!["prompt"]), calls[0].Prompt);
        Assert.Equal(e[0]!["maxTokens"]!.GetValue<int>(), calls[0].MaxTokens);
        Assert.Equal(NormalizeReplies(Str(expected["result"]!["summary"])), NormalizeReplies(result.Summary));
        Assert.Equal(expected["result"]!["readFiles"]!.AsArray().Select(Str), result.ReadFiles!);
        Assert.Equal(expected["result"]!["modifiedFiles"]!.AsArray().Select(Str), result.ModifiedFiles!);
    }
}

public class CompactionCutPointTests
{
    private static readonly Model TestModel = new()
    {
        Id = "m", Name = "m", Api = "openai-completions", Provider = "p", BaseUrl = "http://x", Input = ["text"], ContextWindow = 32000, MaxTokens = 3000,
    };

    private static AssistantMessage Assistant(ContentBlock block, StopReason reason = StopReason.Stop)
    {
        var message = AssistantMessage.CreateEmpty(TestModel, reason);
        message.Content = [block];
        return message;
    }

    [Fact]
    public void KeepsToolCallBeforeOversizedTrailingToolResults()
    {
        CodingAgentMessages.Register();
        var session = SessionManager.InMemory();
        session.AppendMessage(new UserMessage(UserContent.FromText("old history")));
        session.AppendMessage(Assistant(new TextContent("old answer")));
        session.AppendMessage(new UserMessage(UserContent.FromText("read the large file")));
        var toolCallId = session.AppendMessage(Assistant(new ToolCall { Id = "call-1", Name = "read", Arguments = new JsonObject { ["path"] = "big.txt" } }, StopReason.ToolUse));
        session.AppendMessage(new ToolResultMessage { ToolCallId = "call-1", ToolName = "read", Content = [new TextContent(new string('x', 8000))] });
        var entries = session.GetBranch();

        Assert.Equal(new CutPointResult(3, 2, true), Compactor.FindCutPoint(entries, 0, entries.Count, 1000));

        var preparation = Compactor.PrepareCompaction(entries, new CompactionSettings { KeepRecentTokens = 1000 });
        Assert.NotNull(preparation);
        Assert.Equal(toolCallId, preparation.FirstKeptEntryId);
        Assert.Equal(2, preparation.MessagesToSummarize.Count);
        Assert.Equal("read the large file", Assert.IsType<UserMessage>(Assert.Single(preparation.TurnPrefixMessages)).Content.ToString());
    }
}
