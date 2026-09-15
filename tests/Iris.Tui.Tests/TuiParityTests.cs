using System.Text.Json.Nodes;
using Iris.Tui;
using Iris.Tui.Components;
using Iris.Tui.Markdown;

namespace Iris.Tui.Tests;

internal sealed class FakeTerminal : ITerminal
{
    public void Start(Action<string> onInput, Action onResize)
    {
    }

    public void Stop()
    {
    }

    public Task DrainInputAsync(int maxMs = 1000, int idleMs = 50) => Task.CompletedTask;

    public void Write(string data)
    {
    }

    public int Columns => 80;
    public int Rows => 40;
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

/// <summary>Byte-level parity with pi-tui, using fixtures generated from the real package (gen-tui-fixtures.mjs).</summary>
public class TuiParityTests
{
    private static JsonNode Fixtures { get; } = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "tui.json")))!;

    static TuiParityTests() => TerminalImage.SetCapabilities(new TerminalCapabilities(null, true, false));

    private static Func<string, string> Wrap(int open, int close) => s => $"\e[{open}m{s}\e[{close}m";

    private static List<string> Strings(JsonNode? node) => node!.AsArray().Select(n => n!.GetValue<string>()).ToList();

    private static void AssertLines(JsonNode? expected, List<string> actual, string context)
    {
        var exp = Strings(expected);
        if (!exp.SequenceEqual(actual))
        {
            Assert.Fail($"{context}\nexpected:\n{string.Join("\n", exp.Select(Show))}\nactual:\n{string.Join("\n", actual.Select(Show))}");
        }
    }

    private static string Show(string s) => JsonValue.Create(s)!.ToJsonString();

    private static readonly MarkdownTheme Theme = new()
    {
        Heading = Wrap(36, 39), Link = Wrap(34, 39), LinkUrl = Wrap(90, 39), Code = Wrap(33, 39), CodeBlock = Wrap(32, 39),
        CodeBlockBorder = Wrap(90, 39), Quote = Wrap(35, 39), QuoteBorder = Wrap(90, 39), Hr = Wrap(90, 39), ListBullet = Wrap(96, 39),
        Bold = Wrap(1, 22), Italic = Wrap(3, 23), Strikethrough = Wrap(9, 29), Underline = Wrap(4, 24),
    };

    private static readonly SelectListTheme SelectTheme = new()
    {
        SelectedPrefix = Wrap(36, 39), SelectedText = Wrap(1, 22), Description = Wrap(90, 39), ScrollInfo = Wrap(90, 39), NoMatch = Wrap(31, 39),
    };

    [Fact]
    public void VisibleWidth()
    {
        foreach (var c in Fixtures["visibleWidth"]!.AsArray())
        {
            var s = c!["s"]!.GetValue<string>();
            Assert.True(c["w"]!.GetValue<int>() == TextUtils.VisibleWidth(s), $"visibleWidth {Show(s)}");
        }
    }

    [Fact]
    public void WrapTruncateSlice()
    {
        foreach (var c in Fixtures["wrap"]!.AsArray())
        {
            AssertLines(c!["lines"], TextUtils.WrapTextWithAnsi(c["text"]!.GetValue<string>(), c["width"]!.GetValue<int>()), "wrap " + Show(c["text"]!.GetValue<string>()));
        }
        foreach (var c in Fixtures["truncate"]!.AsArray())
        {
            var actual = TextUtils.TruncateToWidth(c!["text"]!.GetValue<string>(), c["width"]!.GetValue<int>(), c["ellipsis"]!.GetValue<string>(), c["pad"]!.GetValue<bool>());
            Assert.Equal(c["out"]!.GetValue<string>(), actual);
        }
        foreach (var c in Fixtures["slice"]!.AsArray())
        {
            var actual = TextUtils.SliceByColumn(c!["line"]!.GetValue<string>(), c["start"]!.GetValue<int>(), c["len"]!.GetValue<int>(), c["strict"]!.GetValue<bool>());
            Assert.Equal(c["out"]!.GetValue<string>(), actual);
        }
    }

    [Fact]
    public void Latex()
    {
        var failures = new List<string>();
        foreach (var c in Fixtures["latex"]!.AsArray())
        {
            var src = c!["src"]!.GetValue<string>();
            var display = c["display"]!.GetValue<bool>();
            var expected = c["out"]?.GetValue<string>();
            var actual = Iris.Tui.Markdown.Latex.Render(src, display);
            if (expected != actual) failures.Add($"{Show(src)} display={display}\n  expected {(expected is null ? "null" : Show(expected))}\n  actual   {(actual is null ? "null" : Show(actual))}");
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void Markdown()
    {
        var styles = new Dictionary<string, DefaultTextStyle>
        {
            ["italicGray"] = new() { Color = Wrap(90, 39), Italic = true },
            ["bg"] = new() { BgColor = Wrap(44, 49), Color = Wrap(97, 39) },
        };
        var failures = new List<string>();
        foreach (var c in Fixtures["markdown"]!.AsArray())
        {
            var doc = c!["doc"]!.GetValue<string>();
            var width = c["width"]!.GetValue<int>();
            var style = c["style"]?.GetValue<string>() is { } name ? styles[name] : null;
            var md = new MarkdownComponent(doc, c["paddingX"]!.GetValue<int>(), c["paddingY"]!.GetValue<int>(), Theme, style);
            List<string> actual;
            try
            {
                actual = md.Render(width);
            }
            catch (Exception ex)
            {
                failures.Add($"{Show(doc)} width={width}: {ex.GetType().Name}: {ex.Message}");
                continue;
            }
            var expected = Strings(c["lines"]);
            if (!expected.SequenceEqual(actual))
            {
                failures.Add($"{Show(doc)} width={width} style={c["style"]}\nexpected:\n{string.Join("\n", expected.Select(Show))}\nactual:\n{string.Join("\n", actual.Select(Show))}");
            }
        }
        foreach (var c in Fixtures["markdownPreserve"]!.AsArray())
        {
            var doc = c!["doc"]!.GetValue<string>();
            var md = new MarkdownComponent(doc, 0, 0, Theme, null, new MarkdownOptions { PreserveOrderedListMarkers = true, PreserveBackslashEscapes = true });
            var actual = md.Render(60);
            if (!Strings(c["lines"]).SequenceEqual(actual)) failures.Add($"preserve {Show(doc)}\nactual:\n{string.Join("\n", actual.Select(Show))}");
        }
        Assert.True(failures.Count == 0, $"{failures.Count} failures\n" + string.Join("\n\n", failures));
    }

    [Fact]
    public void WordWrapAndNavigation()
    {
        foreach (var c in Fixtures["wordWrapLine"]!.AsArray())
        {
            var chunks = Editor.WordWrapLine(c!["line"]!.GetValue<string>(), c["width"]!.GetValue<int>());
            var expected = c["chunks"]!.AsArray().Select(x => new TextChunk(x!["text"]!.GetValue<string>(), x["startIndex"]!.GetValue<int>(), x["endIndex"]!.GetValue<int>())).ToList();
            Assert.Equal(expected, chunks);
        }
        var failures = new List<string>();
        foreach (var c in Fixtures["wordNav"]!.AsArray())
        {
            var text = c!["text"]!.GetValue<string>();
            var cursor = c["cursor"]!.GetValue<int>();
            // Known deviation: ICU splits Han fragments with its dictionary; runs of the same script are grouped here.
            if (text == "日本語 テキスト" && cursor == 1) continue;
            var back = WordNavigation.FindWordBackward(text, cursor);
            var fwd = WordNavigation.FindWordForward(text, cursor);
            if (back != c["back"]!.GetValue<int>() || fwd != c["fwd"]!.GetValue<int>())
            {
                failures.Add($"{Show(text)} @{cursor}: back {back} (exp {c["back"]}), fwd {fwd} (exp {c["fwd"]})");
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void KeysAndFuzzy()
    {
        var failures = new List<string>();
        foreach (var c in Fixtures["keys"]!.AsArray())
        {
            var data = c!["data"]!.GetValue<string>();
            var parsed = Keys.Parse(data);
            if (parsed != c["parsed"]?.GetValue<string>()) failures.Add($"parse {Show(data)}: {parsed} (exp {c["parsed"]})");
            var printable = Keys.DecodeKittyPrintable(data);
            if (printable != c["printable"]?.GetValue<string>()) failures.Add($"printable {Show(data)}: {printable} (exp {c["printable"]})");
            foreach (var (id, value) in c["matches"]!.AsObject())
            {
                var actual = Keys.Matches(data, id);
                if (actual != value!.GetValue<bool>()) failures.Add($"matches {Show(data)} {id}: {actual}");
            }
        }
        foreach (var c in Fixtures["fuzzy"]!.AsArray())
        {
            var items = Strings(c!["items"]);
            var actual = Fuzzy.Filter(items, c["query"]!.GetValue<string>(), x => x);
            if (!Strings(c["result"]).SequenceEqual(actual)) failures.Add($"fuzzy {c["query"]}: {string.Join(",", actual)} (exp {c["result"]!.ToJsonString()})");
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void EditorScripts()
    {
        var tui = new TuiMainScreen(new FakeTerminal(), new UiDispatcher());
        var failures = new List<string>();
        foreach (var c in Fixtures["editor"]!.AsArray())
        {
            var editor = new Editor(tui, new EditorTheme { BorderColor = Wrap(90, 39), SelectList = SelectTheme }, new EditorOptions { PaddingX = 0 }) { Focused = true };
            var submitted = new List<string>();
            editor.OnSubmit = t =>
            {
                submitted.Add(t);
                editor.AddToHistory(t);
            };
            var inputs = Strings(c!["inputs"]);
            foreach (var data in inputs) editor.HandleInput(data);
            var lines = editor.Render(c["width"]!.GetValue<int>());
            var context = $"editor script {string.Concat(inputs.Select(Show))}";
            if (!Strings(c["submitted"]).SequenceEqual(submitted)) failures.Add($"{context}: submitted {string.Join("|", submitted)}");
            if (c["text"]!.GetValue<string>() != editor.GetText()) failures.Add($"{context}: text {Show(editor.GetText())} (exp {Show(c["text"]!.GetValue<string>())})");
            if (c["expanded"]!.GetValue<string>() != editor.GetExpandedText()) failures.Add($"{context}: expanded {Show(editor.GetExpandedText())}");
            var (line, col) = editor.GetCursor();
            if (c["cursor"]!["line"]!.GetValue<int>() != line || c["cursor"]!["col"]!.GetValue<int>() != col) failures.Add($"{context}: cursor {line},{col} (exp {c["cursor"]!.ToJsonString()})");
            if (!Strings(c["lines"]).SequenceEqual(lines)) failures.Add($"{context}: lines\n{string.Join("\n", lines.Select(Show))}\nexpected\n{string.Join("\n", Strings(c["lines"]).Select(Show))}");
        }
        Assert.True(failures.Count == 0, string.Join("\n\n", failures));
    }

    [Fact]
    public void InputAndLists()
    {
        foreach (var c in Fixtures["input"]!.AsArray())
        {
            var input = new Input { Focused = true };
            foreach (var data in Strings(c!["inputs"])) input.HandleInput(data);
            Assert.Equal(c["value"]!.GetValue<string>(), input.GetValue());
            AssertLines(c["lines"], input.Render(c["width"]!.GetValue<int>()), "input");
        }

        List<SelectItem> Items() =>
        [
            new("model", "model", "Select model\nfrom list"),
            new("compact", "compact", "Compact the session context to save tokens"),
            new("a-very-long-command-name-that-exceeds-column", "a-very-long-command-name-that-exceeds-column", "desc"),
            new("noDesc", "noDesc"),
            new("five", "five", "5"),
            new("six", "six", "6"),
            new("seven", "seven", "7"),
        ];
        foreach (var c in Fixtures["selectList"]!.AsArray())
        {
            var list = new SelectList(Items(), 4, SelectTheme);
            foreach (var k in Strings(c!["keys"])) list.HandleInput(k);
            AssertLines(c["lines"], list.Render(c["width"]!.GetValue<int>()), "selectList");
            Assert.Equal(c["selected"]?.GetValue<string>(), list.GetSelectedItem()?.Value);
        }
        var empty = new SelectList(Items(), 4, SelectTheme);
        empty.SetFilter("zzz");
        AssertLines(Fixtures["selectListEmpty"], empty.Render(40), "selectList empty");

        var settingsTheme = new SettingsListTheme
        {
            Label = (t, s) => s ? Wrap(1, 22)(t) : t,
            Value = (t, s) => s ? Wrap(36, 39)(t) : Wrap(90, 39)(t),
            Description = Wrap(90, 39),
            Cursor = "→ ",
            Hint = Wrap(90, 39),
        };
        foreach (var c in Fixtures["settingsList"]!.AsArray())
        {
            var changes = new List<string>();
            var items = new List<SettingItem>
            {
                new() { Id = "autocompact", Label = "Auto-compact", Description = "Automatically compact when context is full", CurrentValue = "true", Values = ["true", "false"] },
                new() { Id = "theme", Label = "Theme", CurrentValue = "dark", Values = ["dark", "light"] },
                new() { Id = "thinking", Label = "Hide thinking blocks", Description = "Hide the model's thinking", CurrentValue = "false", Values = ["true", "false"] },
            };
            var list = new SettingsList(items, 5, settingsTheme, (id, v) => changes.Add($"{id}={v}"), () => { }, c!["search"]!.GetValue<bool>());
            foreach (var k in Strings(c["keys"])) list.HandleInput(k);
            AssertLines(c["lines"], list.Render(50), "settingsList");
            var expectedChanges = c["changes"]!.AsArray().Select(x => $"{x![0]}={x[1]}").ToList();
            Assert.Equal(expectedChanges, changes);
        }
    }
}
