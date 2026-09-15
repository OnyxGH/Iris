using System.Collections.Concurrent;
using System.Text;
using Iris.Extensions;
using Iris.WebAccess.Fetch;
using Iris.WebAccess.Search;

namespace Iris.WebAccess;

/// <summary>
/// Web access built into Iris: web_search (SearXNG, Exa, Brave, Tavily, Perplexity, DuckDuckGo), fetch_content (readable
/// pages, PDFs, GitHub repositories) and get_search_content for stored results. Configured in web-search.json in the agent
/// directory; disable with builtinExtensions.web-access = false.
/// </summary>
public sealed partial class WebAccessExtension : IExtension
{
    public const string Id = "web-access";

    internal const string WebSearchTool = "web_search";
    internal const string FetchContentTool = "fetch_content";
    internal const string GetSearchContentTool = "get_search_content";

    private const int DefaultMaxInlineContentChars = 30_000;
    private const int MaxInlineContentChars = 200_000;
    private const int SearchQueryConcurrency = 3;

    private readonly ResultStore _store = new();
    private readonly SearchDispatcher _dispatcher = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pendingFetches = new();
    private IExtensionApi _iris = null!;
    private int _sessionGeneration;

    static WebAccessExtension()
    {
        // Pages declare legacy charsets (windows-1252, shift_jis, ...).
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public void Register(IExtensionApi iris)
    {
        _iris = iris;
        iris.On<SessionStartEvent>((_, ctx) => HandleSessionChange(ctx));
        iris.On<SessionTreeEvent>((_, ctx) => HandleSessionChange(ctx));
        iris.On<SessionShutdownEvent>((_, _) =>
        {
            Interlocked.Increment(ref _sessionGeneration);
            AbortPendingFetches();
            GitHubExtractor.ClearClones();
            _store.Clear();
        });

        iris.RegisterTool(CreateWebSearchTool());
        iris.RegisterTool(CreateFetchContentTool());
        iris.RegisterTool(CreateGetSearchContentTool());
    }

    private void HandleSessionChange(ExtensionContext ctx)
    {
        Interlocked.Increment(ref _sessionGeneration);
        AbortPendingFetches();
        GitHubExtractor.ClearClones();
        _store.RestoreFromSession(ctx.SessionManager);
    }

    private void AbortPendingFetches()
    {
        foreach (var (id, source) in _pendingFetches)
        {
            if (_pendingFetches.TryRemove(id, out _)) source.Cancel();
        }
    }

    internal static int MaxInlineChars()
    {
        try
        {
            return WebConfig.GetInt(WebConfig.Root, "maxInlineContentChars") is { } value && value > 0 ? Math.Min(value, MaxInlineContentChars) : DefaultMaxInlineContentChars;
        }
        catch (ConfigParseException)
        {
            return DefaultMaxInlineContentChars;
        }
    }

    /// <summary>Fetch URLs in the background and tell the model when their content is stored.</summary>
    private string? StartBackgroundFetch(IReadOnlyList<string> urls)
    {
        if (urls.Count == 0) return null;
        var fetchId = ResultStore.GenerateId();
        var source = new CancellationTokenSource();
        _pendingFetches[fetchId] = source;
        var generation = _sessionGeneration;
        _ = Task.Run(async () =>
        {
            try
            {
                var fetched = await ContentExtractor.FetchAllAsync(urls, new ExtractOptions { WebSearchTool = WebSearchTool, FetchContentTool = FetchContentTool }, source.Token);
                if (generation != _sessionGeneration || !_pendingFetches.ContainsKey(fetchId)) return;
                _iris.AppendEntry(ResultStore.EntryType, _store.StoreFetch(fetchId, fetched));
                var ok = fetched.Count(f => f.Error is null);
                var availability = ok == fetched.Count
                    ? "Full page content now available."
                    : ok > 0 ? "Partial page content now available." : "No page content was fetched. Stored fetch diagnostics are available.";
                _iris.SendMessage("web-search-content-ready", $"Content fetched for {ok}/{fetched.Count} URLs [{fetchId}]. {availability}", options: new SendMessageOptions { TriggerTurn = true });
            }
            catch (OperationCanceledException)
            {
                // Aborted with the session.
            }
            catch (Exception ex)
            {
                if (generation != _sessionGeneration || !_pendingFetches.ContainsKey(fetchId)) return;
                _iris.SendMessage("web-search-error", $"Content fetch failed [{fetchId}]: {ex.Message}", options: new SendMessageOptions { TriggerTurn = false });
            }
            finally
            {
                if (_pendingFetches.TryRemove(fetchId, out var removed)) removed.Dispose();
            }
        });
        return fetchId;
    }
}
