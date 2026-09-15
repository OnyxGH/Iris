using Iris.WebAccess.Curator;

namespace Iris.WebAccess.Tests;

public class SummaryGeneratorTests
{
    private static readonly List<QueryResultData> Results =
    [
        new()
        {
            Query = "c# records",
            Answer = "Records are reference types with value equality.\nSources: https://learn.microsoft.com",
            Provider = "exa",
            Results = [new SearchResult("Records", "https://learn.microsoft.com/records", ""), new SearchResult("Spec", "https://example.com/spec", "")],
        },
        new() { Query = "broken query", Error = "provider unavailable" },
    ];

    [Fact]
    public void PromptDescribesResultsAndFeedback()
    {
        var prompt = SummaryGenerator.BuildPrompt(Results, null);
        Assert.Contains("<search_results>", prompt);
        Assert.Contains("Query: c# records", prompt);
        Assert.Contains("1. Records — https://learn.microsoft.com/records", prompt);
        Assert.Contains("Status: Error\nError: provider unavailable", prompt);
        Assert.DoesNotContain("<user_feedback>", prompt);

        var withFeedback = SummaryGenerator.BuildPrompt(Results, "focus on the designers");
        Assert.Contains("- Incorporate the user feedback provided below into the summary.", withFeedback);
        Assert.Contains("<user_feedback>\nfocus on the designers\n</user_feedback>", withFeedback);
    }

    [Fact]
    public void DeterministicSummaryListsQueriesAndSources()
    {
        var draft = SummaryGenerator.Deterministic(Results);
        Assert.True(draft.Meta.FallbackUsed);
        Assert.Null(draft.Meta.Model);
        Assert.Contains("- c# records: Records are reference types with value equality.", draft.Summary);
        Assert.Contains("- broken query: failed (provider unavailable)", draft.Summary);
        Assert.Contains("Successful: 1", draft.Summary);
        Assert.Contains("- https://example.com/spec", draft.Summary);
        Assert.Equal(SummaryGenerator.EstimateTokens(draft.Summary), draft.Meta.TokenEstimate);
    }

    [Fact]
    public void DeterministicSummaryHandlesEmptySelection()
    {
        var draft = SummaryGenerator.Deterministic([]);
        Assert.Contains("No search results were selected", draft.Summary);
        Assert.Contains("Sources\n- None", draft.Summary);
    }

    [Fact]
    public async Task FallsBackToDeterministicSummaryWithoutAModel()
    {
        var draft = await SummaryGenerator.GenerateAsync(Results, null, null, null, null, CancellationToken.None);
        Assert.True(draft.Meta.FallbackUsed);
        Assert.Equal("no-summary-model-available", draft.Meta.FallbackReason);
    }

    [Fact]
    public async Task RejectsAnInvalidSummaryModelSelector()
    {
        var draft = await SummaryGenerator.GenerateAsync(Results, null, null, "not-a-selector", null, CancellationToken.None);
        Assert.True(draft.Meta.FallbackUsed);
        Assert.Equal("no-summary-model-available", draft.Meta.FallbackReason);
    }
}

public class CuratorStateTests
{
    private static CuratorState NewState() =>
        new(
        [
            new QueryResultData
            {
                Query = "first",
                Answer = "a",
                Provider = "exa",
                Results = [new SearchResult("One", "https://one.example", ""), new SearchResult("Two", "https://two.example", "")],
            },
            new QueryResultData { Query = "second", Answer = "b", Results = [new SearchResult("Three", "https://three.example", "")] },
            new QueryResultData { Query = "failed", Error = "boom" },
        ]);

    [Fact]
    public void SelectsEverythingButFailedQueriesByDefault()
    {
        var state = NewState();
        Assert.Equal(3, state.SelectedSourceCount());
        var selected = state.Selected();
        Assert.Equal(["first", "second"], selected.Select(q => q.Query));
        Assert.Equal(2, selected[0].Results.Count);
    }

    [Fact]
    public void DroppingSourcesAndQueriesNarrowsTheSelection()
    {
        var state = NewState();
        state.Queries[0].Sources[1].Selected = false;
        state.Queries[1].Selected = false;

        Assert.Equal(1, state.SelectedSourceCount());
        var selected = state.Selected();
        var query = Assert.Single(selected);
        Assert.Equal("first", query.Query);
        Assert.Equal("https://one.example", Assert.Single(query.Results).Url);
    }
}
