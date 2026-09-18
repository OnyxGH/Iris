using Iris.CodingAgent.Core;
using Iris.CodingAgent.Core.Extensions;

namespace Iris.CodingAgent.Tests;

public class ExtensionLoaderTests
{
    private const string GuardSource = """
        using System.ComponentModel;

        public sealed class Guard : IExtension
        {
            public void Register(IExtensionApi iris)
            {
                iris.On<ToolCallEvent, ToolCallResult>((e, ctx) => e.ToolName == "bash" ? ToolCallResult.Blocked("no bash") : null);
                iris.On<ToolResultEvent, ToolResultResult>((e, ctx) =>
                {
                    return null;
                });
                iris.OnAsync<AgentEndedEvent>(async (e, ctx) => await Task.Yield());
                iris.RegisterTool(new Tool<CountParams>
                {
                    Name = "count_words",
                    Label = "Count words",
                    Description = "Count the words in a text",
                    ExecuteAsync = (call, args, ctx) => Task.FromResult(ToolResult.Text($"{args.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length} words")),
                });
                iris.RegisterCommand("guard-status", new CommandOptions { HandlerAsync = (args, ctx) => Task.CompletedTask });
            }
        }

        public sealed record CountParams([property: Description("The text to count")] string Text, int? MinLength = null);
        """;

    [Fact]
    public async Task CompilesAndRegistersSourceExtension()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "guard.cs");
        await File.WriteAllTextAsync(file, GuardSource);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var result = await ExtensionLoader.LoadAsync([new ResolvedResource(file, true, new PathMetadata("local", "user", "top-level", dir.Path))], dir.Path, Path.Combine(dir.Path, "agent"), null, cts.Token);
        Assert.Empty(result.Errors);
        var extension = Assert.Single(result.Extensions);
        var tool = Assert.Single(extension.Tools.Values).Definition;
        Assert.Equal("count_words", tool.Name);
        Assert.Equal("The text to count", tool.Parameters["properties"]!["text"]!["description"]!.GetValue<string>());
        var output = await tool.Execute("call-1", new System.Text.Json.Nodes.JsonObject { ["text"] = "one two three" }, CancellationToken.None, null, null);
        Assert.Equal("3 words", Assert.IsType<Iris.Ai.TextContent>(output.Content[0]).Text);
        Assert.Single(extension.Commands);
        Assert.Equal(3, extension.Handlers.Values.Sum(h => h.Count));
    }

    [Fact]
    public async Task DisposingASubscriptionRemovesTheHandler()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "once.cs");
        await File.WriteAllTextAsync(file, """
            public sealed class Once : IExtension
            {
                public void Register(IExtensionApi iris)
                {
                    var first = iris.On<AgentIdleEvent>((e, ctx) => { });
                    iris.On<AgentIdleEvent>((e, ctx) => { });
                    var only = iris.OnAsync<AgentEndedEvent>(async (e, ctx) => await Task.Yield());
                    first.Dispose();
                    first.Dispose();
                    only.Dispose();
                }
            }
            """);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var result = await ExtensionLoader.LoadAsync([new ResolvedResource(file, true, new PathMetadata("local", "user", "top-level", dir.Path))], dir.Path, Path.Combine(dir.Path, "agent"), null, cts.Token);
        Assert.Empty(result.Errors);
        // The emptied agent-ended list is removed; one idle handler remains.
        var (_, remaining) = Assert.Single(Assert.Single(result.Extensions).Handlers);
        Assert.Single(remaining);
    }

    [Fact]
    public async Task ReportsCompilationErrorsWithLocation()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "broken.cs");
        await File.WriteAllTextAsync(file, "public sealed class Broken : IExtension { public void Register(IExtensionApi iris) { iris.Nope(); } }");
        var result = await ExtensionLoader.LoadAsync([new ResolvedResource(file, true, new PathMetadata("local", "user", "top-level", dir.Path))], dir.Path, Path.Combine(dir.Path, "agent"), null);
        var error = Assert.Single(result.Errors);
        Assert.Contains("broken.cs(1,", error.Error);
        Assert.Contains("CS1061", error.Error);
    }
}
