using System.Text.RegularExpressions;
using Iris.Tui.Components;

namespace Iris.Tui.Tests;

internal sealed class ScreenTerminal(int columns, int rows) : ITerminal
{
    private readonly string[] _screen = Enumerable.Repeat("", rows).ToArray();

    public Action<string>? Input { get; private set; }

    public string[] Screen => _screen;

    public void Start(Action<string> onInput, Action onResize) => Input = onInput;

    public void Stop()
    {
    }

    public Task DrainInputAsync(int maxMs = 1000, int idleMs = 50) => Task.CompletedTask;

    public void Write(string data)
    {
        foreach (Match match in Regex.Matches(data, @"\e\[(\d+);1H\e\[2K(.*?)(?=\e\[\d+;1H|\e\[\?25[hl]|\e\[\?2026l|$)", RegexOptions.Singleline))
        {
            _screen[int.Parse(match.Groups[1].Value) - 1] = TextUtils.StripTerminalSequences(match.Groups[2].Value).TrimEnd();
        }
    }

    public int Columns => columns;
    public int Rows => rows;
    public bool KittyProtocolActive => false;

    public void MoveBy(int lines)
    {
    }

    public void HideCursor()
    {
    }

    public void ShowCursor()
    {
    }

    public void ClearLine()
    {
    }

    public void ClearFromCursor()
    {
    }

    public void ClearScreen()
    {
    }

    public void SetTitle(string title)
    {
    }

    public void SetProgress(bool active)
    {
    }
}

public class AltScreenTests
{
    private static (TuiAltScreen Tui, ScreenTerminal Terminal, List<string> Copied) Create(string? copyError = null)
    {
        var terminal = new ScreenTerminal(40, 10);
        var copied = new List<string>();
        var tui = new TuiAltScreen(terminal, new UiDispatcher(), options: new TuiAltScreenOptions
        {
            CopyOnSelect = copyError is null,
            CopySelection = text =>
            {
                copied.Add(text);
                return Task.FromResult(copyError);
            },
        });
        var transcript = new Container();
        for (var i = 0; i < 30; i++) transcript.AddChild(new Text($"line {i}", 0, 0));
        var editor = new Text("editor text here", 0, 0);
        var footer = new Text("footer stats 42tok/s", 0, 0);
        tui.SetLayoutRoot(new VStack(
        [
            (new ScrollView(transcript, new ScrollViewOptions { FollowEnd = true, Primary = true }), new StackEntryOptions { Basis = 0, Grow = 1, Shrink = 1, MinSize = 1 }),
            (new VStack([(editor, null), (footer, null)]), new StackEntryOptions { Grow = 0, Shrink = 1, MinSize = 1 }),
        ]));
        tui.Start();
        tui.RenderNow();
        return (tui, terminal, copied);
    }

    private static string Sgr(int button, int x, int y, bool release = false) => $"\e[<{button};{x + 1};{y + 1}{(release ? 'm' : 'M')}";

    [Fact]
    public void DockStaysAtBottomWhileTranscriptScrolls()
    {
        var (tui, terminal, _) = Create();
        Assert.Equal("line 29", terminal.Screen[7]);
        Assert.Equal("editor text here", terminal.Screen[8]);
        Assert.Equal("footer stats 42tok/s", terminal.Screen[9]);

        terminal.Input!(Sgr(64, 5, 3)); // wheel up
        tui.RenderNow();
        Assert.Equal("line 28", terminal.Screen[7]);
        Assert.Equal("editor text here", terminal.Screen[8]);
        Assert.Equal("footer stats 42tok/s", terminal.Screen[9]);
    }

    [Fact]
    public void TranscriptSelectionCopiesContentText()
    {
        var (tui, terminal, copied) = Create();
        terminal.Input!(Sgr(0, 0, 6));
        terminal.Input!(Sgr(32, 3, 7));
        terminal.Input!(Sgr(0, 3, 7, release: true));
        tui.RenderNow();
        Assert.Equal(["line 28\nline"], copied);
    }

    [Fact]
    public void SelectionOutsideScrollViewStaysInItsSection()
    {
        var (tui, terminal, copied) = Create();
        // Start on the footer and drag up into the editor and transcript: only footer text is selected.
        terminal.Input!(Sgr(0, 7, 9));
        terminal.Input!(Sgr(32, 2, 4));
        terminal.Input!(Sgr(0, 2, 4, release: true));
        tui.RenderNow();
        Assert.Equal(["footer s"], copied);
    }

    [Fact]
    public async Task CopyFailureFlashesTheBackendError()
    {
        var (tui, terminal, copied) = Create(copyError: "Clipboard unavailable: install xclip");
        terminal.Input!(Sgr(0, 0, 6));
        terminal.Input!(Sgr(32, 3, 7));
        terminal.Input!(Sgr(0, 3, 7, release: true));
        Assert.Empty(copied);

        Assert.False(await tui.CopyActiveSelectionToClipboardAsync());
        tui.RenderNow();
        Assert.Contains(terminal.Screen, line => line.Contains("Clipboard unavailable: install xclip"));
        Assert.DoesNotContain(terminal.Screen, line => line.Contains("Copy failed"));
    }
}
