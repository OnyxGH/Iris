using System.Text.Json.Nodes;
using Iris.Extensions;
using Iris.WebAccess.Tools;

namespace Iris.WebAccess;

/// <summary>Choosing the web_search workflow and the /curator command that sets its default.</summary>
public sealed partial class WebAccessExtension
{
    /// <summary>Set when the user approves a review with "auto-summarize the rest"; cleared when the run ends.</summary>
    private bool _autoSummarizeRemaining;

    private void RegisterCuratorWorkflow(IExtensionApi iris)
    {
        iris.On<BeforeAgentStartEvent, BeforeAgentStartResult>((_, _) =>
        {
            _autoSummarizeRemaining = false;
            return null;
        });
        iris.On<AgentIdleEvent>((_, _) => _autoSummarizeRemaining = false);

        iris.RegisterCommand("curator", new CommandOptions
        {
            Description = "Toggle or configure the search curator workflow",
            HandlerAsync = (args, ctx) =>
            {
                SetConfiguredWorkflow(iris, args.Trim().ToLowerInvariant(), ctx);
                return Task.CompletedTask;
            },
        });
    }

    internal static SearchWorkflow ParseWorkflow(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "summary-review" => SearchWorkflow.SummaryReview,
        "auto-summary" => SearchWorkflow.AutoSummary,
        _ => SearchWorkflow.None,
    };

    internal static string WorkflowName(SearchWorkflow workflow) => workflow switch
    {
        SearchWorkflow.SummaryReview => "summary-review",
        SearchWorkflow.AutoSummary => "auto-summary",
        _ => "none",
    };

    private static SearchWorkflow ConfiguredWorkflow()
    {
        try
        {
            return ParseWorkflow(WebConfig.GetString("workflow"));
        }
        catch (ConfigParseException)
        {
            return SearchWorkflow.None;
        }
    }

    /// <summary>
    /// The workflow for one call: an explicit workflow wins over the configured default, the curator only opens with a
    /// terminal, and after "auto-summarize the rest" inherited reviews become automatic summaries until the run ends.
    /// </summary>
    internal SearchWorkflow ResolveWorkflow(SearchWorkflow? requested, ExtensionContext ctx)
    {
        var inherited = requested is null;
        var workflow = requested ?? ConfiguredWorkflow();
        if (workflow == SearchWorkflow.AutoSummary) return workflow;
        if (!ctx.HasUI) return SearchWorkflow.None;
        return inherited && _autoSummarizeRemaining && workflow == SearchWorkflow.SummaryReview ? SearchWorkflow.AutoSummary : workflow;
    }

    private static void SetConfiguredWorkflow(IExtensionApi iris, string argument, ExtensionContext ctx)
    {
        SearchWorkflow workflow;
        switch (argument)
        {
            case "":
                workflow = ConfiguredWorkflow() == SearchWorkflow.None ? SearchWorkflow.SummaryReview : SearchWorkflow.None;
                break;
            case "on":
                workflow = SearchWorkflow.SummaryReview;
                break;
            case "off":
                workflow = SearchWorkflow.None;
                break;
            case "none" or "summary-review" or "auto-summary":
                workflow = ParseWorkflow(argument);
                break;
            default:
                ctx.UI.Notify($"Unknown option: {argument}. Use on, off, summary-review, or auto-summary.", NotifyType.Error);
                return;
        }

        try
        {
            WebConfig.Set("workflow", WorkflowName(workflow));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ConfigParseException)
        {
            ctx.UI.Notify($"Failed to save config: {ex.Message}", NotifyType.Error);
            return;
        }

        var label = workflow switch
        {
            SearchWorkflow.None => $"Curator disabled — {WebSearchTool} will return raw results",
            SearchWorkflow.AutoSummary => $"Auto-summary enabled — {WebSearchTool} will generate a summary without opening the curator",
            _ => $"Curator enabled — {WebSearchTool} will open the curator with an auto-generated summary draft",
        };
        iris.SendMessage("curator-config", label, display: true, details: new JsonObject { ["workflow"] = WorkflowName(workflow) },
            options: new SendMessageOptions { TriggerTurn = false, DeliverAs = "followUp" });
    }
}
