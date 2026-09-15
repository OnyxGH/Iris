using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Iris.CodingAgent.Config;
using Iris.CodingAgent.Core;

namespace Iris.WebAccess;

public sealed class FetchCacheRef
{
    public int Version { get; init; } = ResultStore.FetchCacheVersion;

    public required string Key { get; init; }

    public long StoredAt { get; init; }
}

public sealed class StoredFetchUrlMetadata
{
    public required string Url { get; init; }

    public string Title { get; init; } = "";

    public string? Error { get; init; }

    public int ContentLength { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MimeType { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Status { get; init; }
}

/// <summary>A stored web_search or fetch_content result, referenced by its responseId.</summary>
public sealed class StoredSearchData
{
    public required string Id { get; init; }

    /// <summary>"search" | "fetch".</summary>
    public required string Type { get; init; }

    public long Timestamp { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<QueryResultData>? Queries { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ExtractedContent>? Urls { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FetchCacheRef? FetchCache { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<StoredFetchUrlMetadata>? UrlMetadata { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FetchCacheError { get; init; }
}

/// <summary>
/// Search and fetch results for get_search_content. Search results are stored in the session; fetched page content is
/// kept in a size-bounded file cache next to the session (web-search-cache) and the session only records its metadata.
/// Results expire after an hour.
/// </summary>
public sealed partial class ResultStore
{
    public const string EntryType = "web-search-results";
    internal const int FetchCacheVersion = 1;

    private static readonly TimeSpan Ttl = TimeSpan.FromHours(1);
    private const int MaxMetadataText = 8192;
    private const int MaxCacheEntries = 128;
    private const long MaxCacheBytes = 128L * 1024 * 1024;

    private readonly Dictionary<string, StoredSearchData> _results = [];
    private readonly Lock _gate = new();

    [GeneratedRegex(@"^[A-Za-z0-9_-]+$")]
    private static partial Regex CacheIdPattern();

    public static string CacheDir => Path.Combine(AppConfig.AgentDir, "web-search-cache");

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public static string GenerateId()
    {
        const string alphabet = "0123456789abcdefghijklmnopqrstuvwxyz";
        var time = Now;
        var builder = new StringBuilder();
        do
        {
            builder.Insert(0, alphabet[(int)(time % 36)]);
            time /= 36;
        } while (time > 0);
        for (var i = 0; i < 6; i++) builder.Append(alphabet[RandomNumberGenerator.GetInt32(36)]);
        return builder.ToString();
    }

    /// <summary>Store a search result; returns the session entry data.</summary>
    public JsonNode StoreSearch(string id, List<QueryResultData> queries)
    {
        var data = new StoredSearchData { Id = id, Type = "search", Timestamp = Now, Queries = queries };
        lock (_gate) _results[id] = data;
        return JsonSerializer.SerializeToNode(data, WebJson.Options)!;
    }

    /// <summary>Store fetched content in the file cache; returns the session entry data (metadata only).</summary>
    public JsonNode StoreFetch(string id, List<ExtractedContent> urls)
    {
        var data = new StoredSearchData { Id = id, Type = "fetch", Timestamp = Now, Urls = urls };
        FetchCacheRef? cacheRef = null;
        string? cacheError = null;
        try
        {
            cacheRef = WriteCache(data);
        }
        catch (Exception ex)
        {
            cacheError = Truncate($"Fetched content cache write failed: {ex.Message}");
        }
        var metadata = MetadataFor(urls);
        lock (_gate)
        {
            PruneExpired();
            _results[id] = cacheRef is not null
                ? new StoredSearchData { Id = id, Type = "fetch", Timestamp = data.Timestamp, Urls = urls, FetchCache = cacheRef, UrlMetadata = metadata }
                : new StoredSearchData { Id = id, Type = "fetch", Timestamp = data.Timestamp, Urls = urls, FetchCacheError = cacheError };
        }
        var sessionData = new StoredSearchData { Id = id, Type = "fetch", Timestamp = data.Timestamp, UrlMetadata = metadata, FetchCache = cacheRef, FetchCacheError = cacheError };
        return JsonSerializer.SerializeToNode(sessionData, WebJson.Options)!;
    }

    public StoredSearchData? Get(string id)
    {
        StoredSearchData? data;
        lock (_gate) data = _results.GetValueOrDefault(id);
        if (data is null) return null;
        var loaded = LoadFetchContent(data);
        if (!ReferenceEquals(loaded, data)) lock (_gate) _results[id] = loaded;
        return loaded;
    }

    public void Clear()
    {
        lock (_gate) _results.Clear();
    }

    /// <summary>Rebuild the store from the current session branch.</summary>
    public void RestoreFromSession(SessionManager? sessionManager)
    {
        lock (_gate) _results.Clear();
        if (sessionManager is null) return;
        PruneCacheFiles(null, 0);
        var now = Now;
        foreach (var entry in sessionManager.GetBranch())
        {
            if (entry is not CustomEntry { CustomType: EntryType, Data: { } node }) continue;
            StoredSearchData? data;
            try
            {
                data = node.Deserialize<StoredSearchData>(WebJson.Options);
            }
            catch (JsonException)
            {
                continue;
            }
            if (data is null || data.Type is not ("search" or "fetch") || now - data.Timestamp >= Ttl.TotalMilliseconds) continue;
            lock (_gate) _results[data.Id] = data;
        }
    }

    private void PruneExpired()
    {
        var now = Now;
        foreach (var (id, data) in _results.ToList())
        {
            if (data.Type == "fetch" && now - data.Timestamp >= Ttl.TotalMilliseconds) _results.Remove(id);
        }
    }

    private static List<StoredFetchUrlMetadata> MetadataFor(List<ExtractedContent> urls) =>
    [
        .. urls.Select(url => new StoredFetchUrlMetadata
        {
            Url = Truncate(url.Url),
            Title = Truncate(url.Title),
            Error = url.Error is null ? null : Truncate(url.Error),
            ContentLength = url.Content.Length,
            MimeType = url.MimeType is null ? null : Truncate(url.MimeType),
            Status = url.Status,
        }),
    ];

    private static string Truncate(string value) => value.Length > MaxMetadataText ? $"{value[..MaxMetadataText]}..." : value;

    private static StoredSearchData LoadFetchContent(StoredSearchData data)
    {
        if (data.Type != "fetch") return data;
        if (Now - data.Timestamp >= Ttl.TotalMilliseconds) return Unavailable(data, "Cached fetched content is missing or expired");
        if (data.Urls is not null) return data;
        if (data.FetchCache is null) return Unavailable(data, data.FetchCacheError ?? "Cached fetched content is unavailable");
        if (data.FetchCache.Version != FetchCacheVersion || !CacheIdPattern().IsMatch(Path.GetFileNameWithoutExtension(data.FetchCache.Key)))
        {
            return Unavailable(data, "Cached fetched content is invalid");
        }
        var path = Path.Combine(CacheDir, data.FetchCache.Key);
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint)) return Unavailable(data, "Cached fetched content is missing or expired");
            var cached = JsonSerializer.Deserialize<StoredSearchData>(File.ReadAllText(path), WebJson.Options);
            if (cached is null || cached.Type != "fetch" || cached.Id != data.Id || cached.Urls is null) return Unavailable(data, "Cached fetched content is invalid");
            return new StoredSearchData { Id = data.Id, Type = "fetch", Timestamp = data.Timestamp, Urls = cached.Urls, FetchCache = data.FetchCache, UrlMetadata = data.UrlMetadata };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Unavailable(data, $"Cached fetched content could not be read: {ex.Message}");
        }
    }

    private static StoredSearchData Unavailable(StoredSearchData data, string reason) => new()
    {
        Id = data.Id,
        Type = data.Type,
        Timestamp = data.Timestamp,
        FetchCache = data.FetchCache,
        UrlMetadata = data.UrlMetadata,
        Urls =
        [
            .. (data.UrlMetadata ?? []).Select(meta => new ExtractedContent { Url = meta.Url, Title = meta.Title, Error = reason, MimeType = meta.MimeType, Status = meta.Status }),
        ],
    };

    private static FetchCacheRef WriteCache(StoredSearchData data)
    {
        if (!CacheIdPattern().IsMatch(data.Id)) throw new InvalidOperationException($"Invalid fetched content cache id: {data.Id}");
        var serialized = JsonSerializer.SerializeToUtf8Bytes(data, WebJson.Options);
        if (serialized.Length > MaxCacheBytes) throw new InvalidOperationException($"Fetched content cache entry exceeds {MaxCacheBytes} bytes");
        var dir = CacheDir;
        Directory.CreateDirectory(dir);
        if (new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidOperationException("Fetched content cache directory must not be a link");
        var key = $"{data.Id}.json";
        PruneCacheFiles(key, serialized.Length);
        var tmp = Path.Combine(dir, $"{key}.{Environment.ProcessId}.{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16))}.tmp");
        using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(serialized);
            stream.Flush(flushToDisk: true);
        }
        File.Move(tmp, Path.Combine(dir, key), overwrite: true);
        return new FetchCacheRef { Key = key, StoredAt = Now };
    }

    /// <summary>Delete expired and leftover temporary files, then the oldest entries beyond the count and size limits.</summary>
    private static void PruneCacheFiles(string? reserveKey, long reserveBytes)
    {
        var dir = CacheDir;
        if (!Directory.Exists(dir)) return;
        var now = DateTime.UtcNow;
        var entries = new List<FileInfo>();
        foreach (var file in new DirectoryInfo(dir).EnumerateFiles())
        {
            try
            {
                if (file.Name.EndsWith(".tmp", StringComparison.Ordinal))
                {
                    if (now - file.LastWriteTimeUtc > TimeSpan.FromMinutes(10)) file.Delete();
                    continue;
                }
                if (!file.Name.EndsWith(".json", StringComparison.Ordinal) || file.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                if (now - file.LastWriteTimeUtc >= Ttl)
                {
                    file.Delete();
                    continue;
                }
                if (file.Name != reserveKey) entries.Add(file);
            }
            catch (IOException)
            {
                // In use by another Iris process.
            }
        }
        var totalBytes = entries.Sum(e => e.Length) + reserveBytes;
        var count = entries.Count + (reserveKey is null ? 0 : 1);
        foreach (var file in entries.OrderBy(e => e.LastWriteTimeUtc))
        {
            if (count <= MaxCacheEntries && totalBytes <= MaxCacheBytes) break;
            try
            {
                file.Delete();
                count--;
                totalBytes -= file.Length;
            }
            catch (IOException)
            {
                // In use by another Iris process.
            }
        }
    }
}
