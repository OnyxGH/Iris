using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using Iris.Agent;
using Iris.Ai;
using Iris.CodingAgent.Core.Tools;

namespace Iris.CodingAgent.Tests;

internal static class TestEnvironment
{
    public static string AgentDir { get; } = Path.Combine(Path.GetTempPath(), "iris-tests-" + Guid.NewGuid().ToString("N")[..8], "agent");

    [ModuleInitializer]
    internal static void Initialize()
    {
        Directory.CreateDirectory(Path.Combine(AgentDir, "bin"));
        Environment.SetEnvironmentVariable("IRIS_CODING_AGENT_DIR", AgentDir);
        Environment.SetEnvironmentVariable("IRIS_OFFLINE", "1");
        // Reuse fd/rg binaries from an existing pi installation when available so find/grep tests can run offline.
        var piBin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pi", "agent", "bin");
        foreach (var name in new[] { "fd.exe", "rg.exe", "fd", "rg" })
        {
            var source = Path.Combine(piBin, name);
            if (File.Exists(source)) File.Copy(source, Path.Combine(AgentDir, "bin", name), overwrite: true);
        }
    }
}

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "iris-tool-" + Guid.NewGuid().ToString("N")[..10]);

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string name, string content)
    {
        var full = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public class ToolFixtureTests
{
    private static JsonNode Fixtures { get; } = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "tools.json")))!;

    [Fact]
    public void EditDiffMatchesPi()
    {
        var index = 0;
        foreach (var c in Fixtures["edits"]!.AsArray())
        {
            var content = c!["content"]!.GetValue<string>();
            var edits = c["edits"]!.AsArray().Select(e => new EditReplacement(e!["oldText"]!.GetValue<string>(), e["newText"]!.GetValue<string>())).ToList();
            var (baseContent, newContent) = EditDiff.ApplyEditsToNormalizedContent(content, edits, "f.txt");
            Assert.True(c["newContent"]!.GetValue<string>() == newContent, $"case {index} newContent");
            var diff = EditDiff.GenerateDiffString(baseContent, newContent);
            Assert.True(c["diff"]!["diff"]!.GetValue<string>() == diff.Diff, $"case {index} diff");
            Assert.Equal(c["diff"]!["firstChangedLine"]?.GetValue<int>(), diff.FirstChangedLine);
            Assert.True(c["patch"]!.GetValue<string>() == EditDiff.GenerateUnifiedPatch("f.txt", baseContent, newContent), $"case {index} patch");
            index++;
        }
    }

    [Fact]
    public void TruncationMatchesPi()
    {
        foreach (var c in Fixtures["truncation"]!.AsArray())
        {
            var content = c!["content"]!.GetValue<string>();
            var maxLines = c["maxLines"]!.GetValue<int>();
            var maxBytes = c["maxBytes"]!.GetValue<int>();
            Assert.Equal(c["head"]!.ToJsonString(), Truncate.TruncateHead(content, maxLines, maxBytes).ToJson().ToJsonString());
            Assert.Equal(c["tail"]!.ToJsonString(), Truncate.TruncateTail(content, maxLines, maxBytes).ToJson().ToJsonString());
        }
        foreach (var pair in Fixtures["sizes"]!.AsArray())
        {
            Assert.Equal(pair![1]!.GetValue<string>(), Truncate.FormatSize(pair[0]!.GetValue<long>()));
        }
    }
}

public class FileToolTests
{
    private static Task<AgentToolResult> Run(ToolDefinition tool, JsonObject args, CancellationToken ct = default)
    {
        if (tool.PrepareArguments is not null) args = tool.PrepareArguments(args);
        return tool.Execute("call-1", args, ct, null, null);
    }

    private static string Text(AgentToolResult result) => string.Join("", result.Content.OfType<TextContent>().Select(t => t.Text));

    [Fact]
    public async Task ReadHonorsOffsetLimitAndNotices()
    {
        using var dir = new TempDir();
        dir.File("a.txt", string.Join("\n", Enumerable.Range(1, 10).Select(i => $"line {i}")));
        var tool = ReadTool.CreateDefinition(dir.Path);

        var full = await Run(tool, new JsonObject { ["path"] = "a.txt" });
        Assert.StartsWith("line 1\nline 2", Text(full));

        var limited = await Run(tool, new JsonObject { ["path"] = "a.txt", ["offset"] = 3, ["limit"] = 2 });
        Assert.Equal("line 3\nline 4\n\n[6 more lines in file. Use offset=5 to continue.]", Text(limited));

        var beyond = await Assert.ThrowsAnyAsync<Exception>(() => Run(tool, new JsonObject { ["path"] = "a.txt", ["offset"] = 50 }));
        Assert.Equal("Offset 50 is beyond end of file (10 lines total)", beyond.Message);

        var missing = await Assert.ThrowsAnyAsync<Exception>(() => Run(tool, new JsonObject { ["path"] = "nope.txt" }));
        Assert.StartsWith("ENOENT: no such file or directory, access '", missing.Message);
    }

    [Fact]
    public async Task ReadTruncatesByLines()
    {
        using var dir = new TempDir();
        dir.File("big.txt", string.Join("\n", Enumerable.Range(1, 2500).Select(i => $"{i}")));
        var result = await Run(ReadTool.CreateDefinition(dir.Path), new JsonObject { ["path"] = "big.txt" });
        Assert.EndsWith("\n\n[Showing lines 1-2000 of 2500. Use offset=2001 to continue.]", Text(result));
        Assert.Equal("lines", result.Details!["truncation"]!["truncatedBy"]!.GetValue<string>());
    }

    [Fact]
    public async Task ReadReturnsImages()
    {
        using var dir = new TempDir();
        using var bitmap = new SkiaSharp.SKBitmap(2, 2);
        bitmap.Erase(SkiaSharp.SKColors.Red);
        using var encoded = SkiaSharp.SKImage.FromBitmap(bitmap).Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        var png = encoded.ToArray();
        File.WriteAllBytes(Path.Combine(dir.Path, "img.png"), png);
        var result = await Run(ReadTool.CreateDefinition(dir.Path), new JsonObject { ["path"] = "img.png" });
        Assert.Equal("Read image file [image/png]", Text(result));
        var image = Assert.IsType<ImageContent>(result.Content[1]);
        Assert.Equal(Convert.ToBase64String(png), image.Data);
    }

    [Fact]
    public async Task WriteCreatesDirectories()
    {
        using var dir = new TempDir();
        var result = await Run(WriteTool.CreateDefinition(dir.Path), new JsonObject { ["path"] = "sub/dir/x.txt", ["content"] = "héllo" });
        Assert.Equal("Successfully wrote to sub/dir/x.txt", Text(result));
        Assert.Equal(Encoding.UTF8.GetBytes("héllo"), File.ReadAllBytes(Path.Combine(dir.Path, "sub", "dir", "x.txt")));
    }

    [Fact]
    public async Task EditPreservesBomAndCrlf()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "crlf.txt");
        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("one\r\ntwo\r\nthree\r\n")]);
        var tool = EditTool.CreateDefinition(dir.Path);
        var result = await Run(tool, new JsonObject { ["path"] = "crlf.txt", ["edits"] = new JsonArray(new JsonObject { ["oldText"] = "two", ["newText"] = "TWO\nextra" }) });
        Assert.Equal("Successfully replaced 1 block(s) in crlf.txt.", Text(result));
        Assert.Equal([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("one\r\nTWO\r\nextra\r\nthree\r\n")], File.ReadAllBytes(path));
        Assert.Equal(2, result.Details!["firstChangedLine"]!.GetValue<int>());
    }

    [Fact]
    public async Task EditAcceptsLegacyAndStringifiedArguments()
    {
        using var dir = new TempDir();
        dir.File("a.txt", "hello world\n");
        var tool = EditTool.CreateDefinition(dir.Path);
        await Run(tool, new JsonObject { ["path"] = "a.txt", ["oldText"] = "hello", ["newText"] = "goodbye" });
        await Run(tool, new JsonObject { ["path"] = "a.txt", ["edits"] = "[{\"oldText\":\"world\",\"newText\":\"moon\"}]" });
        Assert.Equal("goodbye moon\n", File.ReadAllText(Path.Combine(dir.Path, "a.txt")));

        var notFound = await Assert.ThrowsAnyAsync<Exception>(() => Run(tool, new JsonObject { ["path"] = "a.txt", ["edits"] = new JsonArray(new JsonObject { ["oldText"] = "zzz", ["newText"] = "y" }) }));
        Assert.Equal("Could not find the exact text in a.txt. The old text must match exactly including all whitespace and newlines.", notFound.Message);

        var missing = await Assert.ThrowsAnyAsync<Exception>(() => Run(tool, new JsonObject { ["path"] = "b.txt", ["edits"] = new JsonArray(new JsonObject { ["oldText"] = "x", ["newText"] = "y" }) }));
        Assert.Equal("Could not edit file: b.txt. Error code: ENOENT.", missing.Message);
    }

    [Fact]
    public async Task EditDetectsDuplicatesAndOverlaps()
    {
        using var dir = new TempDir();
        dir.File("a.txt", "abc abc\nxyz\n");
        var tool = EditTool.CreateDefinition(dir.Path);
        var dup = await Assert.ThrowsAnyAsync<Exception>(() => Run(tool, new JsonObject { ["path"] = "a.txt", ["edits"] = new JsonArray(new JsonObject { ["oldText"] = "abc", ["newText"] = "q" }) }));
        Assert.Equal("Found 2 occurrences of the text in a.txt. The text must be unique. Please provide more context to make it unique.", dup.Message);

        var overlap = await Assert.ThrowsAnyAsync<Exception>(() => Run(tool, new JsonObject
        {
            ["path"] = "a.txt",
            ["edits"] = new JsonArray(new JsonObject { ["oldText"] = "abc\nxy", ["newText"] = "1" }, new JsonObject { ["oldText"] = "xyz", ["newText"] = "2" }),
        }));
        Assert.Equal("edits[0] and edits[1] overlap in a.txt. Merge them into one edit or target disjoint regions.", overlap.Message);
    }

    [Fact]
    public async Task EditFuzzyMatchesSmartQuotes()
    {
        using var dir = new TempDir();
        dir.File("q.txt", "keep “this”   \nsay “hello”\n");
        var result = await Run(EditTool.CreateDefinition(dir.Path), new JsonObject { ["path"] = "q.txt", ["edits"] = new JsonArray(new JsonObject { ["oldText"] = "say \"hello\"", ["newText"] = "say \"bye\"" }) });
        Assert.Equal("Successfully replaced 1 block(s) in q.txt.", Text(result));
        Assert.Equal("keep “this”   \nsay \"bye\"\n", File.ReadAllText(Path.Combine(dir.Path, "q.txt")));
    }

    [Fact]
    public async Task LsListsSortedEntries()
    {
        using var dir = new TempDir();
        dir.File("b.txt", "");
        dir.File("A.txt", "");
        dir.File(".hidden", "");
        Directory.CreateDirectory(Path.Combine(dir.Path, "sub"));
        var tool = LsTool.CreateDefinition(dir.Path);
        Assert.Equal(".hidden\nA.txt\nb.txt\nsub/", Text(await Run(tool, [])));
        Assert.Equal(".hidden\nA.txt\n\n[2 entries limit reached. Use limit=4 for more]", Text(await Run(tool, new JsonObject { ["limit"] = 2 })));
        var notDir = await Assert.ThrowsAnyAsync<Exception>(() => Run(tool, new JsonObject { ["path"] = "b.txt" }));
        Assert.StartsWith("Not a directory: ", notDir.Message);
    }
}

public class ShellToolTests
{
    private static string Text(AgentToolResult result) => string.Join("", result.Content.OfType<TextContent>().Select(t => t.Text));

    private static Task<AgentToolResult> Bash(string cwd, string command, double? timeout = null, CancellationToken ct = default, AgentToolUpdateCallback? onUpdate = null)
    {
        var args = new JsonObject { ["command"] = command };
        if (timeout is not null) args["timeout"] = timeout;
        return ShellTool.CreateBashDefinition(cwd).Execute("id", args, ct, onUpdate, null);
    }

    [Fact]
    public async Task RunsCommandsAndReportsExitCodes()
    {
        using var dir = new TempDir();
        Assert.Equal("hi\nthere\n", Text(await Bash(dir.Path, "echo hi; echo there >&2")));
        Assert.Equal("(no output)", Text(await Bash(dir.Path, "true")));

        var failed = await Assert.ThrowsAnyAsync<Exception>(() => Bash(dir.Path, "echo oops; exit 3"));
        Assert.Equal("oops\n\n\nCommand exited with code 3", failed.Message);
    }

    [Theory]
    [InlineData(1234, "1.2s")]
    [InlineData(59_949, "59.9s")]
    [InlineData(65_400, "1m 5s")]
    [InlineData(3_725_000, "1h 2m 5s")]
    public void FormatsDurations(long ms, string expected) =>
        Assert.Equal(expected, Iris.CodingAgent.Modes.Interactive.Components.BuiltInToolRenderers.FormatDuration(ms));

    private sealed class NoExitCodeOperations : IBashOperations
    {
        public Task<int?> ExecAsync(string command, string cwd, ShellExecOptions options)
        {
            options.OnData("partial\n"u8.ToArray());
            return Task.FromResult<int?>(null);
        }
    }

    [Fact]
    public async Task FailsCommandsWithoutAnExitCode()
    {
        using var dir = new TempDir();
        var tool = ShellTool.CreateBashDefinition(dir.Path, new BashToolOptions { Operations = new NoExitCodeOperations() });
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => tool.Execute("id", new JsonObject { ["command"] = "anything" }, default, null, null));
        Assert.Equal("partial\n\n\nCommand terminated without an exit code", ex.Message);
    }

    [Fact]
    public async Task ExposesSessionEnvironmentAndCwd()
    {
        using var dir = new TempDir();
        var output = Text(await Bash(dir.Path, "echo \"${IRIS_MODEL:-none}\"; pwd -W 2>/dev/null || pwd"));
        var lines = output.Split('\n');
        Assert.Equal("none", lines[0]);
        Assert.Equal(Path.GetFullPath(dir.Path).TrimEnd('\\', '/').Replace('\\', '/'), lines[1].TrimEnd('/'), ignoreCase: true);
    }

    [Fact]
    public async Task TimesOut()
    {
        using var dir = new TempDir();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => Bash(dir.Path, "echo start; sleep 10", timeout: 0.5));
        Assert.Equal("start\n\n\nCommand timed out after 0.5 seconds", ex.Message);
    }

    [Fact]
    public async Task AbortsAndStreamsUpdates()
    {
        using var dir = new TempDir();
        using var cts = new CancellationTokenSource();
        var updates = 0;
        var task = Bash(dir.Path, "echo ready; sleep 10", ct: cts.Token, onUpdate: partial =>
        {
            Interlocked.Increment(ref updates);
            if (Text(partial).Contains("ready")) cts.Cancel();
        });
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => task);
        Assert.Equal("ready\n\n\nCommand aborted", ex.Message);
        Assert.True(updates >= 2);
    }

    [Fact]
    public async Task TruncatesLongOutputToTempFile()
    {
        using var dir = new TempDir();
        var result = await Bash(dir.Path, "seq 1 3000");
        var text = Text(result);
        var fullOutputPath = result.Details!["fullOutputPath"]!.GetValue<string>();
        Assert.EndsWith($"\n\n[Showing lines 1001-3000 of 3000. Full output: {fullOutputPath}]", text);
        Assert.StartsWith("1001\n", text);
        Assert.Equal(3000, File.ReadAllText(fullOutputPath).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        File.Delete(fullOutputPath);
    }
}

public class SearchToolTests
{
    private static string Text(AgentToolResult result) => string.Join("", result.Content.OfType<TextContent>().Select(t => t.Text));

    [Fact]
    public async Task GrepFindsMatchesWithContext()
    {
        if (Utils.ToolsManager.GetToolPath("rg") is null) return;
        using var dir = new TempDir();
        dir.File("src/a.ts", "one\nneedle here\nthree\n");
        dir.File("b.md", "no match\n");
        var tool = GrepTool.CreateDefinition(dir.Path);
        Assert.Equal("src/a.ts:2: needle here", Text(await tool.Execute("id", new JsonObject { ["pattern"] = "needle" }, default, null, null)));
        Assert.Equal("src/a.ts-1- one\nsrc/a.ts:2: needle here\nsrc/a.ts-3- three",
            Text(await tool.Execute("id", new JsonObject { ["pattern"] = "NEEDLE", ["ignoreCase"] = true, ["context"] = 1 }, default, null, null)));
        Assert.Equal("No matches found", Text(await tool.Execute("id", new JsonObject { ["pattern"] = "zzz" }, default, null, null)));
    }

    [Fact]
    public async Task FindListsFilesByGlob()
    {
        if (Utils.ToolsManager.GetToolPath("fd") is null) return;
        using var dir = new TempDir();
        dir.File("src/a.spec.ts", "");
        dir.File("src/b.ts", "");
        dir.File("c.ts", "");
        var tool = FindTool.CreateDefinition(dir.Path);
        var all = Text(await tool.Execute("id", new JsonObject { ["pattern"] = "*.ts" }, default, null, null)).Split('\n').Order().ToArray();
        Assert.Equal(["c.ts", "src/a.spec.ts", "src/b.ts"], all);
        Assert.Equal("src/a.spec.ts", Text(await tool.Execute("id", new JsonObject { ["pattern"] = "src/*.spec.ts" }, default, null, null)));
    }
}
