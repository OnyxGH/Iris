using System.Diagnostics;
using System.Text;
using Iris.Ai;
using Iris.CodingAgent.Core;

namespace Iris.WebAccess.Curator;

/// <summary>How a summary was produced, shown under the summary in the transcript.</summary>
public sealed record SummaryMeta(
    string? Model,
    long DurationMs,
    int TokenEstimate,
    bool FallbackUsed,
    string? FallbackReason = null,
    string? Phase = null,
    bool Edited = false);

public sealed record SummaryDraft(string Summary, SummaryMeta Meta);

/// <summary>
/// Writes the final summary of a set of search results with a small model, falling back to a deterministic summary
/// when no model is available, the model fails, or generation takes too long.
/// </summary>
internal static class SummaryGenerator
{
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    /// <summary>Preferred summary models, in order, when summaryModel is not configured.</summary>
    private static readonly (string Provider, string Id)[] PreferredModels =
    [
        ("anthropic", "claude-haiku-4-5"),
        ("google", "gemini-3.6-flash"),
        ("openai", "gpt-5-mini"),
        ("deepseek", "deepseek-v4-flash"),
    ];

    public static int EstimateTokens(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length == 0 ? 0 : Math.Max(1, (int)Math.Ceiling(trimmed.Length / 4.0));
    }

    /// <summary>The models the curator may use, current model first when it is available.</summary>
    public static List<Model> Candidates(ModelRegistry? registry, Model? currentModel, string? overrideSelector)
    {
        if (registry is null) return [];
        var available = registry.GetAvailable();
        var candidates = new List<Model>();
        void Add(Model? model)
        {
            if (model is null || candidates.Any(c => c.Provider == model.Provider && c.Id == model.Id)) return;
            if (!available.Any(a => a.Provider == model.Provider && a.Id == model.Id)) return;
            candidates.Add(model);
        }

        var configured = overrideSelector ?? WebConfig.GetString("summaryModel");
        if (configured is not null)
        {
            var slash = configured.IndexOf('/');
            if (slash <= 0 || slash >= configured.Length - 1) throw new InvalidOperationException($"Invalid summary model: {configured}. Use provider/model-id.");
            Add(registry.Find(configured[..slash], configured[(slash + 1)..]));
        }
        foreach (var (provider, id) in PreferredModels) Add(registry.Find(provider, id));
        Add(currentModel);
        return candidates;
    }

    public static async Task<SummaryDraft> GenerateAsync(
        IReadOnlyList<QueryResultData> results,
        ModelRegistry? registry,
        Model? currentModel,
        string? modelOverride,
        string? feedback,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Deadline);

        List<Model> candidates;
        try
        {
            candidates = Candidates(registry, currentModel, modelOverride);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fallback(results, $"summary-model-settings-error: {ex.Message}", started.ElapsedMilliseconds);
        }
        if (candidates.Count == 0) return Fallback(results, "no-summary-model-available", started.ElapsedMilliseconds);

        var prompt = BuildPrompt(results, feedback);
        string? lastError = null;
        foreach (var model in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attemptStarted = Stopwatch.StartNew();
            try
            {
                var context = new Context { Messages = [new UserMessage(prompt)] };
                var response = await registry!.CompleteAsync(model, context, new StreamOptions { CancellationToken = deadline.Token, MaxTokens = 2048 });
                if (response.StopReason == StopReason.Aborted) throw new OperationCanceledException();
                var summary = string.Join("\n", response.Content.OfType<TextContent>().Select(t => t.Text).Where(t => t.Trim().Length > 0)).Trim();
                if (summary.Length == 0) throw new InvalidOperationException("Summary model returned empty response");
                return new SummaryDraft(summary, new SummaryMeta(
                    $"{model.Provider}/{model.Id}", attemptStarted.ElapsedMilliseconds, EstimateTokens(summary), false, Phase: "summary-model"));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return Fallback(results, "summary-generation-deadline", started.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                lastError = $"{model.Provider}/{model.Id}: {ex.Message}";
            }
        }
        return Fallback(results, lastError is null ? "summary-model-unavailable" : $"summary-model-error: {lastError}", started.ElapsedMilliseconds);
    }

    public static SummaryDraft Fallback(IReadOnlyList<QueryResultData> results, string reason, long durationMs)
    {
        var deterministic = Deterministic(results);
        return deterministic with { Meta = deterministic.Meta with { DurationMs = durationMs, FallbackReason = reason } };
    }

    public static SummaryDraft Deterministic(IReadOnlyList<QueryResultData> results)
    {
        var summary = string.Join("\n", DeterministicLines(results)).Trim();
        if (summary.Length == 0) summary = "No search results were selected when the curator session finished.\n\nSources\n- None";
        return new SummaryDraft(summary, new SummaryMeta(null, 0, EstimateTokens(summary), true, "deterministic-submit-fallback", "deterministic-fallback"));
    }

    private static List<string> DeterministicLines(IReadOnlyList<QueryResultData> results)
    {
        if (results.Count == 0) return ["No search results were selected when the curator session finished.", "", "Sources", "- None"];
        var lines = new List<string> { "Summary based on the currently selected search results.", "" };
        var sourceUrls = new List<string>();
        var successful = 0;
        var failed = 0;
        foreach (var result in results)
        {
            if (result.Error is not null)
            {
                failed++;
                lines.Add($"- {result.Query}: failed ({result.Error})");
                continue;
            }
            successful++;
            var preview = AnswerPreview(result.Answer);
            lines.Add(preview.Length > 0
                ? $"- {result.Query}: {preview}"
                : $"- {result.Query}: returned {result.Results.Count} source{(result.Results.Count == 1 ? "" : "s")} without answer text.");
            foreach (var source in result.Results.Where(s => !sourceUrls.Contains(s.Url))) sourceUrls.Add(source.Url);
        }
        lines.AddRange(["", $"Completed queries: {results.Count}", $"Successful: {successful}", $"Failed: {failed}", "", "Sources"]);
        if (sourceUrls.Count == 0) lines.Add("- None");
        else
        {
            lines.AddRange(sourceUrls.Take(12).Select(url => $"- {url}"));
            if (sourceUrls.Count > 12) lines.Add($"- ... and {sourceUrls.Count - 12} more");
        }
        return lines;
    }

    private static string AnswerPreview(string answer)
    {
        var text = System.Text.RegularExpressions.Regex.Replace(answer, @"\s+", " ").Trim();
        if (text.Length == 0) return "";
        var marker = System.Text.RegularExpressions.Regex.Match(text, @"\bSources?\s*:", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (marker.Success) text = text[..marker.Index].Trim();
        return text.Length > 240 ? $"{text[..237]}..." : text;
    }

    public static string BuildPrompt(IReadOnlyList<QueryResultData> results, string? feedback)
    {
        var sections = new List<string>
        {
            "You are writing the final web search summary for a coding assistant.",
            "Write a concise, factual summary using only the provided search results.",
            "Requirements:",
            "- Keep it readable and skimmable.",
            "- Include key findings and caveats.",
            "- Do not invent sources or claims.",
            "- If evidence is weak or conflicting, say so explicitly.",
            "- End with a short \"Sources\" section listing the most relevant URLs.",
        };
        if (feedback is not null) sections.Add("- Incorporate the user feedback provided below into the summary.");
        sections.AddRange(["", "<search_results>"]);
        for (var i = 0; i < results.Count; i++)
        {
            sections.Add($"\n[Result {i + 1}]");
            sections.Add(Describe(results[i]));
        }
        sections.Add("\n</search_results>");
        if (feedback is not null) sections.AddRange(["", "<user_feedback>", feedback, "</user_feedback>"]);
        return string.Join("\n", sections);
    }

    private static string Describe(QueryResultData result)
    {
        if (result.Error is not null) return $"Query: {result.Query}\nStatus: Error\nError: {result.Error}";
        var lines = new StringBuilder($"Query: {result.Query}\nProvider: {result.Provider ?? "unknown"}\nAnswer: {(result.Answer.Length > 0 ? result.Answer : "(no answer text returned)")}");
        if (result.Results.Count == 0) return lines.Append("\nSources: none").ToString();
        lines.Append("\nSources:");
        for (var i = 0; i < result.Results.Count; i++) lines.Append($"\n{i + 1}. {result.Results[i].Title} — {result.Results[i].Url}");
        return lines.ToString();
    }
}
