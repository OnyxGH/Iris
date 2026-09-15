using System.Text.Json.Nodes;
using Iris.Ai.Auth;
using Iris.Ai.Json;
using Iris.Ai.Providers;

namespace Iris.Ai.Models;

public sealed class ModelsStoreEntry
{
    public List<Model> Models { get; set; } = [];

    /// <summary>Unix timestamp from the remote catalog's Last-Modified header.</summary>
    public long? LastModified { get; set; }

    /// <summary>Unix timestamp of the last completed remote check.</summary>
    public long? CheckedAt { get; set; }

    /// <summary>Opaque validator from the remote catalog's ETag header, stored verbatim.</summary>
    public string? Etag { get; set; }

    public ModelsStoreEntry Clone() => IrisJson.Deserialize<ModelsStoreEntry>(IrisJson.Serialize(this))!;
}

/// <summary>Persistent model catalogs keyed by provider id.</summary>
public interface IModelsStore
{
    Task<ModelsStoreEntry?> ReadAsync(string providerId, CancellationToken cancellationToken = default);
    Task WriteAsync(string providerId, ModelsStoreEntry entry, CancellationToken cancellationToken = default);
    Task DeleteAsync(string providerId, CancellationToken cancellationToken = default);
}

public sealed class InMemoryModelsStore : IModelsStore
{
    private readonly Dictionary<string, ModelsStoreEntry> _entries = new();

    public Task<ModelsStoreEntry?> ReadAsync(string providerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_entries) return Task.FromResult(_entries.TryGetValue(providerId, out var e) ? e.Clone() : null);
    }

    public Task WriteAsync(string providerId, ModelsStoreEntry entry, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_entries) _entries[providerId] = entry.Clone();
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string providerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_entries) _entries.Remove(providerId);
        return Task.CompletedTask;
    }
}

public sealed class ModelsPublication
{
    /// <summary>True when <see cref="Persist"/> should be applied (null Persist deletes).</summary>
    public bool HasPersist { get; init; }

    public ModelsStoreEntry? Persist { get; init; }

    /// <summary>Synchronous update of provider-private in-memory catalog state.</summary>
    public Action? Update { get; init; }
}

public sealed class RefreshModelsContext
{
    public Credential? Credential { get; init; }
    public ModelsStoreEntry? Stored { get; init; }
    public required Func<ModelsPublication, Task<bool>> Publish { get; init; }
    public bool AllowNetwork { get; init; }
    public bool? Force { get; init; }
    public CancellationToken CancellationToken { get; init; }
}

/// <summary>
/// A provider is the concrete runtime unit. It owns id/name/base metadata, auth methods, model listing and
/// stream behavior.
/// </summary>
public interface IProvider
{
    string Id { get; }
    string Name { get; }
    string? BaseUrl { get; }
    IReadOnlyDictionary<string, string?>? Headers { get; }
    ProviderAuth Auth { get; }

    /// <summary>Current known models. Must not throw.</summary>
    IReadOnlyList<Model> GetModels();

    /// <summary>Dynamic providers only; null for static providers.</summary>
    Func<RefreshModelsContext, Task>? RefreshModels { get; }

    Func<IReadOnlyList<Model>, Credential?, IReadOnlyList<Model>>? FilterModels { get; }

    AssistantMessageEventStream Stream(Model model, Context context, StreamOptions? options = null);

    AssistantMessageEventStream StreamSimple(Model model, Context context, SimpleStreamOptions? options = null);
}

public sealed class CreateProviderOptions
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    public string? BaseUrl { get; init; }
    public IReadOnlyDictionary<string, string?>? Headers { get; init; }
    public required ProviderAuth Auth { get; init; }
    public IReadOnlyList<Model> Models { get; init; } = [];
    public Func<RefreshModelsContext, Task<IReadOnlyList<Model>>>? FetchModels { get; init; }
    public Func<IReadOnlyList<Model>, Credential?, IReadOnlyList<Model>>? FilterModels { get; init; }

    /// <summary>Single implementation for all models.</summary>
    public IApiStreams? Api { get; init; }

    /// <summary>Implementations keyed by model.Api for mixed-API providers.</summary>
    public IReadOnlyDictionary<string, IApiStreams>? ApiByName { get; init; }
}

/// <summary>Provider built from parts.</summary>
public sealed class ComposedProvider : IProvider
{
    private readonly CreateProviderOptions _input;
    private IReadOnlyList<Model> _dynamicModels = [];

    public ComposedProvider(CreateProviderOptions input)
    {
        _input = input;
        if (input.FetchModels is { } fetch)
        {
            RefreshModels = async context =>
            {
                if (context.Stored is not null)
                {
                    var restored = context.Stored.Models.Where(m => m.Provider == input.Id).ToList();
                    if (!await context.Publish(new ModelsPublication { Update = () => _dynamicModels = restored })) return;
                }
                if (!context.AllowNetwork || context.CancellationToken.IsCancellationRequested) return;
                var refreshed = await fetch(context);
                if (context.CancellationToken.IsCancellationRequested) return;
                await context.Publish(new ModelsPublication
                {
                    HasPersist = true,
                    Persist = new ModelsStoreEntry { Models = refreshed.ToList(), CheckedAt = TimeUtil.NowMs() },
                    Update = () => _dynamicModels = refreshed,
                });
            };
        }
    }

    public string Id => _input.Id;
    public string Name => _input.Name ?? _input.Id;
    public string? BaseUrl => _input.BaseUrl;
    public IReadOnlyDictionary<string, string?>? Headers => _input.Headers;
    public ProviderAuth Auth => _input.Auth;
    public Func<RefreshModelsContext, Task>? RefreshModels { get; }
    public Func<IReadOnlyList<Model>, Credential?, IReadOnlyList<Model>>? FilterModels => _input.FilterModels;

    public IReadOnlyList<Model> GetModels()
    {
        var merged = _input.Models.ToList();
        foreach (var model in _dynamicModels)
        {
            var index = merged.FindIndex(m => m.Id == model.Id);
            if (index >= 0) merged[index] = model;
            else merged.Add(model);
        }
        return merged;
    }

    private IApiStreams? ApiFor(Model model) =>
        _input.Api ?? (_input.ApiByName is not null && _input.ApiByName.TryGetValue(model.Api, out var api) ? api : null);

    public AssistantMessageEventStream Stream(Model model, Context context, StreamOptions? options = null)
    {
        var api = ApiFor(model);
        return api is null
            ? AssistantMessageEventStream.Lazy(model, () => throw new ModelsException(ModelsErrorCode.Stream, $"Provider {Id} has no API implementation for \"{model.Api}\""))
            : api.Stream(model, context, options);
    }

    public AssistantMessageEventStream StreamSimple(Model model, Context context, SimpleStreamOptions? options = null)
    {
        var api = ApiFor(model);
        return api is null
            ? AssistantMessageEventStream.Lazy(model, () => throw new ModelsException(ModelsErrorCode.Stream, $"Provider {Id} has no API implementation for \"{model.Api}\""))
            : api.StreamSimple(model, context, options);
    }
}

public static class ProviderFactory
{
    public static IProvider Create(CreateProviderOptions options) => new ComposedProvider(options);
}
