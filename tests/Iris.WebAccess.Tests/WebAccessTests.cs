using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Iris.CodingAgent.Core.Extensions;
using Iris.WebAccess.Fetch;
using Iris.WebAccess.Http;
using Iris.WebAccess.Search;

namespace Iris.WebAccess.Tests;

public class ContentFindTests
{
    private const string Text = "Iris is a coding agent.\n\nIt can search the web and fetch pages.\n\nThe Configuration lives in web-search.json.";

    [Fact]
    public void FindsCaseInsensitiveMatchesWithContext()
    {
        var found = ContentFind.Find(Text, ["configuration"], "case-insensitive");
        Assert.Equal(1, found.MatchCount);
        Assert.StartsWith("Text matches (case-insensitive)", found.Text);
        Assert.Contains("\"configuration\" ×1", found.Text);
        Assert.Contains("web-search.json", found.Text);
    }

    [Fact]
    public void ExactModeRespectsCaseAndReportsMissingQueries()
    {
        var found = ContentFind.Find(Text, ["configuration", "Iris"], "exact");
        Assert.Equal(1, found.MatchCount);
        Assert.Contains("No matches: \"configuration\"", found.Text);
    }

    [Fact]
    public void FuzzyModeToleratesTypos()
    {
        var found = ContentFind.Find(Text, ["fetch pagez"], "fuzzy");
        Assert.Equal(1, found.MatchCount);
        Assert.Contains("fetch pages", found.Text);
    }

    [Fact]
    public void BoundsLargeOutputs()
    {
        var large = string.Concat(Enumerable.Repeat("needle " + new string('x', 900) + "\n", 200));
        var found = ContentFind.Find(large, ["needle"], "exact");
        Assert.Equal(200, found.MatchCount);
        Assert.True(found.Text.Length <= 20_000);
        Assert.Equal(200, found.ReturnedMatches);
        Assert.Contains("Queries: Q1 = \"needle\"", found.Text);
    }
}

public class SsrfTests
{
    [Theory]
    [InlineData("10.1.2.3", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("172.20.0.1", true)]
    [InlineData("192.168.1.1", true)]
    [InlineData("169.254.169.254", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("198.18.0.1", true)]
    [InlineData("224.0.0.1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("172.32.0.1", false)]
    public void ClassifiesIPv4Addresses(string address, bool blocked) => Assert.Equal(blocked, SafeHttp.IsBlockedIPv4(IPAddress.Parse(address)));

    [Theory]
    [InlineData("::1", true)]
    [InlineData("fd00::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("::ffff:10.0.0.1", true)]
    [InlineData("2606:4700::1111", false)]
    public void ClassifiesIPv6Addresses(string address, bool blocked) => Assert.Equal(blocked, SafeHttp.IsBlockedIPv6(IPAddress.Parse(address)));

    [Theory]
    [InlineData("198.18.0.0/15", true)]
    [InlineData("1.2.3.4", true)]
    [InlineData("fd00::/8", true)]
    [InlineData("198.18.0.0/", false)]
    [InlineData("0.0.0.0/0", false)]
    [InlineData("10.0.0.0/33", false)]
    [InlineData("example.com", false)]
    public void ParsesAllowRangesStrictly(string cidr, bool valid) => Assert.Equal(valid, SsrfSettings.ParseCidr(cidr) is not null);

    [Theory]
    [InlineData("http://localhost:8080/")]
    [InlineData("http://app.localhost/")]
    [InlineData("http://10.0.0.1/")]
    [InlineData("http://[::1]/")]
    [InlineData("ftp://example.com/")]
    public async Task BlocksInternalAndNonHttpTargets(string url)
    {
        var policy = new RemoteFetchPolicy(SsrfSettings.Default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => SafeHttp.ValidateRemoteUrlAsync(new Uri(url), policy, CancellationToken.None));
    }

    [Fact]
    public async Task AllowRangesExemptAddresses()
    {
        var policy = new RemoteFetchPolicy(new SsrfSettings([SsrfSettings.ParseCidr("10.0.0.0/8")!.Value], false));
        Assert.NotNull(await SafeHttp.ValidateRemoteUrlAsync(new Uri("http://10.0.0.1/"), policy, CancellationToken.None));
    }

    [Fact]
    public void DomainPolicyDeniesAndAllows()
    {
        var policy = new DomainPolicy(["example.com"], ["private.example.com"]);
        policy.Assert("docs.example.com");
        Assert.Throws<InvalidOperationException>(() => policy.Assert("private.example.com"));
        Assert.Throws<InvalidOperationException>(() => policy.Assert("other.org"));
    }
}

public class ParsingTests
{
    [Fact]
    public void ParsesGitHubUrls()
    {
        var root = GitHubExtractor.ParseUrl("https://github.com/OnyxGH/Iris");
        Assert.Equal(("OnyxGH", "Iris", "root"), (root!.Owner, root.Repo, root.Type));

        var blob = GitHubExtractor.ParseUrl("https://github.com/owner/repo.git/blob/main/src/a%20b.cs");
        Assert.Equal(("repo", "main", "src/a b.cs", "blob", false), (blob!.Repo, blob.Ref, blob.Path, blob.Type, blob.RefIsFullSha));

        Assert.True(GitHubExtractor.ParseUrl($"https://github.com/owner/repo/tree/{new string('a', 40)}")!.RefIsFullSha);
        Assert.Null(GitHubExtractor.ParseUrl("https://github.com/owner/repo/issues/1"));
        Assert.Null(GitHubExtractor.ParseUrl("https://gitlab.com/owner/repo"));
        Assert.Null(GitHubExtractor.ParseUrl("https://github.com/owner"));
    }

    [Fact]
    public void ExpandsJsonArrayQueryStrings()
    {
        Assert.Equal(["a", "b"], WebAccessExtension.ExpandQueryString("[\"a\", \" b \"]"));
        Assert.Equal(["[1, \"a\"]"], WebAccessExtension.ExpandQueryString("[1, \"a\"]"));
        Assert.Equal(["plain query"], WebAccessExtension.ExpandQueryString("plain query"));
    }

    [Fact]
    public void SlicesContentAtLineBreaksNearTheLimit()
    {
        var content = new string('a', 90) + "\n" + new string('b', 50);
        var slice = WebAccessExtension.InitialSlice(content, 100);
        Assert.Equal(91, slice.EndOffset);
        Assert.Equal(2, slice.TotalLines);
        Assert.Equal(2, slice.ShownLines);
    }

    [Fact]
    public void NormalizesDomainFilters()
    {
        var filters = DomainFilters.From(["https://Docs.Example.com/path", "-ads.example.com", "not a domain"]);
        Assert.Equal(["docs.example.com"], filters.Allowed);
        Assert.Equal(["ads.example.com"], filters.Blocked);
        Assert.True(filters.Matches("https://sub.docs.example.com/x"));
        Assert.False(filters.Matches("https://ads.example.com/x"));
        Assert.Equal("q site:docs.example.com -site:ads.example.com", filters.ApplyToQuery("q", "-site:"));
    }

    [Fact]
    public void ParsesProviderSelections()
    {
        Assert.Equal("auto", SearchDispatcher.Parse(null, "provider").Mode);
        Assert.Equal(["brave"], SearchDispatcher.Parse(JsonValue.Create("Brave"), "provider").Providers);
        Assert.Equal("list", SearchDispatcher.Parse(new JsonArray("exa", "tavily"), "provider").Mode);
        Assert.Throws<InvalidOperationException>(() => SearchDispatcher.Parse(JsonValue.Create("gemini"), "provider"));
    }

    [Fact]
    public async Task ExtractsReadableArticleAsMarkdown()
    {
        var paragraph = string.Concat(Enumerable.Repeat("Iris reads pages and keeps the article text while dropping navigation and ads. ", 12));
        var html = $"""
            <html><head><title>Article title</title></head><body>
            <nav><a href="/">Home</a><a href="/about">About</a></nav>
            <article><h1>Readable heading</h1><p>{paragraph}</p><p>See <a href="https://example.com/docs">the docs</a> for details. {paragraph}</p></article>
            <footer>Footer links</footer></body></html>
            """;
        var result = await ContentExtractor.ExtractHtmlAsync("https://example.com/post", "https://example.com/post", html, CancellationToken.None);
        Assert.Null(result.Error);
        Assert.Contains("[the docs](https://example.com/docs)", result.Content);
        Assert.DoesNotContain("Footer links", result.Content);
    }
}

public class ToolSchemaTests
{
    [Fact]
    public async Task RegistersToolsWithGeneratedSchemas()
    {
        using var dir = new TempDirectory();
        BuiltinExtensions.Register(WebAccessExtension.Id, () => new WebAccessExtension());
        var loaded = await ExtensionLoader.LoadAsync([], dir.Path, dir.Path, null);
        Assert.Empty(loaded.Errors);
        var tools = loaded.Extensions.SelectMany(e => e.Tools.Values).ToDictionary(t => t.Definition.Name, t => t.Definition.Parameters);
        Assert.Contains("fetch_content", tools.Keys);

        var search = tools["web_search"]["properties"]!;
        Assert.Equal("string", search["recencyFilter"]!["type"]!.GetValue<string>());
        Assert.Equal(["day", "week", "month", "year"], search["recencyFilter"]!["enum"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.Equal("integer", search["numResults"]!["type"]!.GetValue<string>());
        Assert.NotNull(search["provider"]!["description"]);

        var get = tools["get_search_content"];
        Assert.Equal(["responseId"], get["required"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.Contains("case-insensitive", get["properties"]!["findMode"]!["enum"]!.AsArray().Select(v => v!.GetValue<string>()));
    }
}

/// <summary>Tests that read web-search.json from a temporary agent directory.</summary>
[Collection("agent-dir")]
public sealed class FetchTests : IDisposable
{
    private readonly TempDirectory _agentDir = new();
    private readonly string? _previousAgentDir = Environment.GetEnvironmentVariable("IRIS_CODING_AGENT_DIR");

    public FetchTests() => Environment.SetEnvironmentVariable("IRIS_CODING_AGENT_DIR", _agentDir.Path);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("IRIS_CODING_AGENT_DIR", _previousAgentDir);
        _agentDir.Dispose();
    }

    [Fact]
    public async Task FetchesLocalPagesOnlyWhenAllowed()
    {
        using var server = LocalServer.Start("text/plain; charset=utf-8", "# Notes\n\nplain text body");
        var blocked = await ContentExtractor.ExtractAsync(server.Url, new ExtractOptions(), CancellationToken.None);
        Assert.StartsWith("Blocked internal address", blocked.Error);

        File.WriteAllText(Path.Combine(_agentDir.Path, "web-search.json"), """{ "ssrf": { "allowRanges": ["127.0.0.1"] } }""");
        var allowed = await ContentExtractor.ExtractAsync(server.Url, new ExtractOptions(), CancellationToken.None);
        Assert.Null(allowed.Error);
        Assert.Equal("Notes", allowed.Title);
        Assert.Contains("plain text body", allowed.Content);

        var store = new ResultStore();
        var entry = store.StoreFetch("abc123", [allowed]);
        Assert.Null(entry["urls"]);
        Assert.Equal(allowed.Content.Length, entry["urlMetadata"]![0]!["contentLength"]!.GetValue<int>());
        Assert.Equal(allowed.Content, store.Get("abc123")!.Urls![0].Content);
    }

    [Fact]
    public async Task ReportsNotFoundWithSearchGuidance()
    {
        File.WriteAllText(Path.Combine(_agentDir.Path, "web-search.json"), """{ "ssrf": { "allowRanges": ["127.0.0.1"] } }""");
        using var server = LocalServer.Start("text/html", "gone", HttpStatusCode.NotFound);
        var result = await ContentExtractor.ExtractAsync(server.Url, new ExtractOptions { WebSearchTool = "web_search", FetchContentTool = "fetch_content" }, CancellationToken.None);
        Assert.Contains("HTTP 404", result.Error);
        Assert.Contains("Use web_search to find the current URL", result.Error);
    }
}

internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("iris-web-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }
}

internal sealed class LocalServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();

    private LocalServer(TcpListener listener) => _listener = listener;

    public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/page";

    public static LocalServer Start(string contentType, string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = new LocalServer(listener);
        _ = Task.Run(async () =>
        {
            while (!server._stop.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(server._stop.Token);
                var stream = client.GetStream();
                var buffer = new byte[8192];
                _ = await stream.ReadAsync(buffer);
                var payload = Encoding.UTF8.GetBytes(body);
                var header = $"HTTP/1.1 {(int)status} {status}\r\nContent-Type: {contentType}\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
                await stream.WriteAsync(payload);
            }
        });
        return server;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }
}
