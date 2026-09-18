using Iris.CodingAgent.Core;
using Iris.CodingAgent.Modes.Interactive;
using Iris.CodingAgent.Modes.Interactive.Components;
using Iris.CodingAgent.Utils;
using Iris.Tui;

namespace Iris.CodingAgent.Tests;

public class MessageComponentTests
{
    private static TuiMouseEvent Click(TuiMouseButton button) => new()
    {
        Type = TuiMouseEventType.Click, Button = button, X = 2, Y = 1, ScreenX = 2, ScreenY = 1, Width = 40, Height = 4, ClickCount = 1,
    };

    private static string Plain(IComponent component) => AnsiUtils.StripAnsi(string.Join("\n", component.Render(40)));

    [Fact]
    public void LeftClickTogglesCompactionSummary()
    {
        ThemeManager.InitTheme("dark");
        var component = new CompactionSummaryMessageComponent(new CompactionSummaryMessage { Summary = "SUMMARY BODY", TokensBefore = 1234 });
        Assert.DoesNotContain("SUMMARY BODY", Plain(component));

        Assert.Null(MouseDispatch.Dispatch(component, Click(TuiMouseButton.Right)));
        Assert.DoesNotContain("SUMMARY BODY", Plain(component));

        Assert.True(MouseDispatch.Dispatch(component, Click(TuiMouseButton.Left))?.Handled);
        Assert.Contains("SUMMARY BODY", Plain(component));

        MouseDispatch.Dispatch(component, Click(TuiMouseButton.Left));
        Assert.DoesNotContain("SUMMARY BODY", Plain(component));
    }
}
