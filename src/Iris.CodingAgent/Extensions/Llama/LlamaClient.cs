using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Iris.Ai.Json;
using Iris.Ai.Utils;

namespace Iris.CodingAgent.Extensions.Llama;

public sealed class LlamaModelStatus
{
    /// <summary>"unloaded" | "loading" | "loaded" | "downloading" | "sleeping".</summary>
    public string Value { get; init; } = "unloaded";
    public List<string>? Args { get; init; }
    public bool Failed { get; init; }
    public int? ExitCode { get; init; }
    public JsonObject? Progress { get; init; }
}

public sealed class LlamaModelInfo
{
    public required string Id { get; init; }
    public List<string>? Aliases { get; init; }
    public required LlamaModelStatus Status { get; init; }
    public List<string>? InputModalities { get; init; }
    public string? Source { get; init; }
    public long? ContextSize { get; init; }
    public long? TrainContextSize { get; init; }

    public bool IsLoaded => Status.Value is "loaded" or "sleeping";

    public static LlamaModelInfo? FromJson(JsonNode? node)
    {
        if (node is not JsonObject obj || IrisJson.GetString(obj["id"]) is not { } id) return null;
        if (obj["status"] is not JsonObject status || IrisJson.GetString(status["value"]) is not { } value) return null;
        var meta = obj["meta"] as JsonObject;
        return new LlamaModelInfo
        {
            Id = id,
            Aliases = (obj["aliases"] as JsonArray)?.Select(IrisJson.GetString).OfType<string>().ToList(),
            Status = new LlamaModelStatus
            {
                Value = value,
                Args = (status["args"] as JsonArray)?.Select(IrisJson.GetString).OfType<string>().ToList(),
                Failed = IrisJson.GetBool(status["failed"]) == true,
                ExitCode = IrisJson.GetNumber(status["exit_code"]) is { } code ? (int)code : null,
                Progress = status["progress"] as JsonObject,
            },
            InputModalities = ((obj["architecture"] as JsonObject)?["input_modalities"] as JsonArray)?.Select(IrisJson.GetString).OfType<string>().ToList(),
            Source = IrisJson.GetString(obj["source"]),
            ContextSize = IrisJson.GetNumber(meta?["n_ctx"]) is { } nCtx ? (long)nCtx : null,
            TrainContextSize = IrisJson.GetNumber(meta?["n_ctx_train"]) is { } nTrain ? (long)nTrain : null,
        };
    }
}

/// <summary>Fields Iris reads from llama-server's /props.</summary>
public sealed record LlamaServerProps(bool? ModelsAutoload, string? ChatTemplate);

public sealed record LlamaProgress(string Message, double? Ratio = null, string? Detail = null);

/// <summary>llama.cpp router-mode HTTP client.</summary>
public sealed class LlamaClient
{
    private const int RequestTimeoutMs = 15_000;
    private readonly string? _apiKey;

    public LlamaClient(string serverUrl, string? apiKey = null)
    {
        ServerUrl = NormalizeServerUrl(serverUrl);
        _apiKey = string.IsNullOrEmpty(apiKey) ? null : apiKey;
    }

    public string ServerUrl { get; }

    public static string NormalizeServerUrl(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)) throw new FormatException("Invalid URL");
        if (uri.Scheme is not ("http" or "https")) throw new FormatException("Server URL must use http or https");
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/v1", StringComparison.Ordinal)) path = path[..^3];
        var builder = new UriBuilder(uri) { Query = "", Fragment = "", Path = path.Length == 0 ? "/" : path };
        var text = builder.Uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped);
        return text.TrimEnd('/');
    }

    public static string InferenceUrl(string serverUrl) => $"{NormalizeServerUrl(serverUrl)}/v1";

    public static string FormatBytes(double bytes)
    {
        if (bytes < 1024) return $"{bytes.ToString(CultureInfo.InvariantCulture)} B";
        string[] units = ["KiB", "MiB", "GiB", "TiB"];
        var value = bytes / 1024;
        var unit = units[0];
        for (var index = 1; index < units.Length && value >= 1024; index++)
        {
            value /= 1024;
            unit = units[index];
        }
        return $"{(value >= 10 ? Core.Tools.NodeCompat.ToFixed(value, 1) : Core.Tools.NodeCompat.ToFixed(value, 2))} {unit}";
    }

    private static string ErrorMessage(JsonNode? payload, string fallback) =>
        IrisJson.GetString((payload as JsonObject)?["error"]?["message"]) is { Length: > 0 } message ? message : fallback;

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, JsonNode? body = null)
    {
        var request = new HttpRequestMessage(method, $"{ServerUrl}{path}");
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        if (_apiKey is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        return request;
    }

    private async Task<JsonNode?> RequestAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeoutMs);
        using var request = CreateRequest(method, path, body);
        HttpResponseMessage response;
        try
        {
            response = await ProviderHttp.Client.SendAsync(request, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("The operation timed out.");
        }
        using (response)
        {
            JsonNode? payload = null;
            try
            {
                payload = JsonNode.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Non-JSON body.
            }
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException(ErrorMessage(payload, $"llama.cpp returned HTTP {(int)response.StatusCode}"));
            return payload;
        }
    }

    public async Task<List<LlamaModelInfo>> ListAsync(bool reload = false, CancellationToken ct = default)
    {
        var payload = await RequestAsync(HttpMethod.Get, reload ? "/models?reload=1" : "/models", null, ct);
        if ((payload as JsonObject)?["data"] is not JsonArray data) throw new InvalidOperationException("llama.cpp returned an invalid model catalog");
        var models = data.Select(LlamaModelInfo.FromJson).ToList();
        if (models.Any(m => m is null)) throw new InvalidOperationException("Server is not running in llama.cpp router mode");
        return models!;
    }

    public async Task<bool?> GetModelsAutoloadAsync(CancellationToken ct = default) => (await GetPropsAsync(ct: ct)).ModelsAutoload;

    /// <summary>Read /props, for one model when given (without autoloading it).</summary>
    public async Task<LlamaServerProps> GetPropsAsync(string? model = null, CancellationToken ct = default)
    {
        var query = model is null ? "" : $"?model={Uri.EscapeDataString(model)}&autoload=false";
        var payload = await RequestAsync(HttpMethod.Get, "/props" + query, null, ct) as JsonObject;
        return new LlamaServerProps(IrisJson.GetBool(payload?["models_autoload"]), IrisJson.GetString(payload?["chat_template"]));
    }

    public Task LoadAsync(string model, CancellationToken ct = default) => RequestAsync(HttpMethod.Post, "/models/load", new JsonObject { ["model"] = model }, ct);

    public Task UnloadAsync(string model, CancellationToken ct = default) => RequestAsync(HttpMethod.Post, "/models/unload", new JsonObject { ["model"] = model }, ct);

    public async Task UnloadAndWaitAsync(string model, CancellationToken ct = default)
    {
        await UnloadAsync(model, ct);
        while (true)
        {
            var entry = (await ListAsync(ct: ct)).FirstOrDefault(m => m.Id == model);
            if (entry is null || entry.Status.Value == "unloaded") return;
            await Task.Delay(100, ct);
        }
    }

    /// <summary>Stream model events from /models/sse until cancelled.</summary>
    public async Task WatchAsync(Action<string, string, JsonNode?> onEvent, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Get, "/models/sse");
        using var response = await ProviderHttp.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"llama.cpp SSE returned HTTP {(int)response.StatusCode}");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    try
                    {
                        if (JsonNode.Parse(data.ToString()) is JsonObject evt && IrisJson.GetString(evt["model"]) is { } model && IrisJson.GetString(evt["event"]) is { } name)
                        {
                            onEvent(model, name, evt["data"]);
                        }
                    }
                    catch
                    {
                        // Ignore malformed events; catalog polling remains authoritative.
                    }
                    data.Clear();
                }
                continue;
            }
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line[5..].TrimStart());
            }
        }
    }

    private static LlamaProgress? ParseLoadProgress(JsonNode? data)
    {
        if ((data as JsonObject)?["progress"] is not JsonObject progress) return null;
        var stage = IrisJson.GetString(progress["current"]) ?? IrisJson.GetString(progress["stage"]);
        var stages = (progress["stages"] as JsonArray)?.Select(IrisJson.GetString).OfType<string>().ToList() ?? [];
        double? stageRatio = IrisJson.GetNumber(progress["value"]) is { } v ? Math.Clamp(v, 0, 1) : null;
        var ratio = stageRatio;
        if (stage is not null && stages.Count > 0)
        {
            var index = stages.IndexOf(stage);
            if (index >= 0) ratio = (index + (stageRatio ?? 0)) / stages.Count;
        }
        return new LlamaProgress(stage is not null ? $"Loading {stage.Replace('_', ' ')}" : "Loading model", ratio);
    }

    public async Task<LlamaModelInfo> LoadAndWaitAsync(string model, Action<LlamaProgress> onProgress, CancellationToken ct = default)
    {
        using var watcher = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var eventLoaded = false;
        string? eventError = null;
        _ = WatchAsync((eventModel, name, data) =>
        {
            if (eventModel != model || name is not ("model_status" or "status_change")) return;
            var status = IrisJson.GetString((data as JsonObject)?["status"]);
            if (status == "loaded") eventLoaded = true;
            if (status == "unloaded") eventError = "Model failed to load";
            if (ParseLoadProgress(data) is { } progress) onProgress(progress);
        }, watcher.Token).ContinueWith(_ => { }, TaskScheduler.Default);
        try
        {
            await LoadAsync(model, ct);
            onProgress(new LlamaProgress("Loading model"));
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var entry = (await ListAsync(ct: ct)).FirstOrDefault(m => m.Id == model);
                if (entry?.Status.Value == "loaded") return entry;
                if (eventLoaded && entry is null) return new LlamaModelInfo { Id = model, Status = new LlamaModelStatus { Value = "loaded" } };
                if (entry?.Status.Failed == true || eventError is not null)
                {
                    throw new InvalidOperationException(entry?.Status.ExitCode is { } exitCode ? $"Model exited with code {exitCode}" : eventError ?? "Model failed to load");
                }
                await Task.Delay(250, ct);
            }
        }
        finally
        {
            watcher.Cancel();
        }
    }
}
