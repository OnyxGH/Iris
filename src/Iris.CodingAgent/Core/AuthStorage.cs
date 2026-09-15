using System.Text.Json;
using System.Text.Json.Nodes;
using Iris.Ai.Auth;
using Iris.Ai.Json;
using Iris.Ai.Models;
using Iris.CodingAgent.Config;
using Iris.CodingAgent.Utils;

namespace Iris.CodingAgent.Core;

/// <summary>Locked JSON file access shared by auth.json and models-store.json.</summary>
public sealed class LockedJsonFile
{
    private readonly string _path;
    private readonly SemaphoreSlim _inProcess = new(1, 1);

    public LockedJsonFile(string path) => _path = PathUtils.NormalizePath(path);

    public string Path => _path;

    private void EnsureFile()
    {
        var dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (!File.Exists(_path)) File.WriteAllText(_path, "{}");
    }

    public async Task<T> WithLockAsync<T>(Func<string?, Task<(T Result, string? Next)>> fn, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _inProcess.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureFile();
            using var fileLock = await FileLock.AcquireAsync(_path, retries: 30, minTimeoutMs: 10, maxTimeoutMs: 1000, cancellationToken: cancellationToken).ConfigureAwait(false);
            var current = File.Exists(_path) ? await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false) : null;
            var (result, next) = await fn(current).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (next is not null) await File.WriteAllTextAsync(_path, next, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _inProcess.Release();
        }
    }

    public T WithLock<T>(Func<string?, (T Result, string? Next)> fn)
    {
        _inProcess.Wait();
        try
        {
            EnsureFile();
            using var fileLock = FileLock.AcquireWithRetry(_path);
            var current = File.Exists(_path) ? File.ReadAllText(_path) : null;
            var (result, next) = fn(current);
            if (next is not null) File.WriteAllText(_path, next);
            return result;
        }
        finally
        {
            _inProcess.Release();
        }
    }
}

/// <summary>Credential storage backed by auth.json.</summary>
public sealed class AuthStorage : ICredentialStore
{
    private readonly LockedJsonFile? _file;
    private JsonObject _memory;
    private string? _revision;

    private AuthStorage(LockedJsonFile? file, JsonObject? initial)
    {
        _file = file;
        _memory = initial ?? new JsonObject();
        if (file is not null) Reload();
    }

    public static AuthStorage Create(string? authPath = null) => new(new LockedJsonFile(authPath ?? AppConfig.AuthPath), null);

    public static AuthStorage InMemory(JsonObject? data = null) => new(null, (JsonObject?)data?.DeepClone() ?? new JsonObject());

    private static JsonObject Parse(string? content) =>
        string.IsNullOrEmpty(content) ? new JsonObject() : JsonNode.Parse(TextHelpers.StripBom(content)) as JsonObject ?? new JsonObject();

    public void Reload()
    {
        if (_file is null) return;
        try
        {
            var content = File.Exists(_file.Path) ? File.ReadAllText(_file.Path) : null;
            lock (this)
            {
                _memory = Parse(content);
                _revision = PathUtils.GetFileRevision(_file.Path);
            }
        }
        catch
        {
            // Preserve the last valid snapshot.
        }
    }

    private JsonObject ReadLatest()
    {
        if (_file is not null)
        {
            var revision = PathUtils.GetFileRevision(_file.Path);
            if (revision is null || revision != _revision) Reload();
        }
        lock (this) return _memory;
    }

    public Task<Credential?> ReadAsync(string providerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ReadLatest()[providerId] is not JsonObject obj) return Task.FromResult<Credential?>(null);
        var credential = CredentialJsonConverter.FromJson(obj);
        if (credential is ApiKeyCredential { Key: { } key } api)
        {
            return Task.FromResult<Credential?>(new ApiKeyCredential { Key = ConfigValueResolver.Resolve(key, api.Env), Env = api.Env });
        }
        return Task.FromResult(credential);
    }

    /// <summary>Raw stored credential without resolving configured key values.</summary>
    public Credential? ReadRaw(string providerId) =>
        ReadLatest()[providerId] is JsonObject obj ? CredentialJsonConverter.FromJson(obj) : null;

    public Task<IReadOnlyList<CredentialInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        var entries = ReadLatest()
            .Where(kv => kv.Value is JsonObject)
            .Select(kv => new CredentialInfo(kv.Key, IrisJson.GetString(kv.Value!["type"]) ?? "api_key"))
            .ToList();
        return Task.FromResult<IReadOnlyList<CredentialInfo>>(entries);
    }

    public async Task<Credential?> ModifyAsync(string providerId, Func<Credential?, Task<Credential?>> fn, CancellationToken cancellationToken = default)
    {
        if (_file is null)
        {
            Credential? current;
            lock (this) current = _memory[providerId] is JsonObject o ? CredentialJsonConverter.FromJson(o) : null;
            var next = await fn(current).ConfigureAwait(false);
            if (next is null) return current;
            lock (this) _memory[providerId] = CredentialJsonConverter.ToJson(next);
            return next;
        }

        var result = await _file.WithLockAsync<Credential?>(async content =>
        {
            var data = Parse(content);
            var current = data[providerId] is JsonObject o ? CredentialJsonConverter.FromJson(o) : null;
            var next = await fn(current).ConfigureAwait(false);
            if (next is null) return (current, null);
            data[providerId] = CredentialJsonConverter.ToJson(next);
            return (next, IrisJson.SerializeIndentedTwoSpaces(data));
        }, cancellationToken).ConfigureAwait(false);
        Reload();
        return result;
    }

    public async Task DeleteAsync(string providerId, CancellationToken cancellationToken = default)
    {
        if (_file is null)
        {
            lock (this) _memory.Remove(providerId);
            return;
        }
        await _file.WithLockAsync<bool>(content =>
        {
            var data = Parse(content);
            data.Remove(providerId);
            return Task.FromResult((true, (string?)IrisJson.SerializeIndentedTwoSpaces(data)));
        }, cancellationToken).ConfigureAwait(false);
        Reload();
    }

    /// <summary>One-off read of a stored credential without resolving values.</summary>
    public static Credential? ReadStoredCredential(string providerId, string? authPath = null)
    {
        try
        {
            var data = JsonNode.Parse(TextHelpers.StripBom(File.ReadAllText(PathUtils.NormalizePath(authPath ?? AppConfig.AuthPath)))) as JsonObject;
            return data?[providerId] is JsonObject obj ? CredentialJsonConverter.FromJson(obj) : null;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>Credential store overlay for non-persistent runtime API keys (--api-key).</summary>
public sealed class RuntimeCredentials(ICredentialStore store) : ICredentialStore
{
    private readonly Dictionary<string, string> _overrides = new();

    public void SetRuntimeApiKey(string providerId, string apiKey)
    {
        lock (_overrides) _overrides[providerId] = apiKey;
    }

    public void RemoveRuntimeApiKey(string providerId)
    {
        lock (_overrides) _overrides.Remove(providerId);
    }

    public bool HasRuntimeApiKey(string providerId)
    {
        lock (_overrides) return _overrides.ContainsKey(providerId);
    }

    public Task<Credential?> ReadAsync(string providerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? overrideKey;
        lock (_overrides) _overrides.TryGetValue(providerId, out overrideKey);
        return overrideKey is not null ? Task.FromResult<Credential?>(new ApiKeyCredential { Key = overrideKey }) : store.ReadAsync(providerId, cancellationToken);
    }

    public async Task<IReadOnlyList<CredentialInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        var entries = (await store.ListAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(e => e.ProviderId);
        lock (_overrides)
        {
            foreach (var providerId in _overrides.Keys) entries[providerId] = new CredentialInfo(providerId, AuthTypes.ApiKey);
        }
        return entries.Values.ToList();
    }

    public Task<Credential?> ModifyAsync(string providerId, Func<Credential?, Task<Credential?>> fn, CancellationToken cancellationToken = default) =>
        store.ModifyAsync(providerId, fn, cancellationToken);

    public async Task DeleteAsync(string providerId, CancellationToken cancellationToken = default)
    {
        await store.DeleteAsync(providerId, cancellationToken).ConfigureAwait(false);
        lock (_overrides) _overrides.Remove(providerId);
    }
}

/// <summary>Locked JSON-backed storage for dynamically refreshed provider catalogs (models-store.json).</summary>
public sealed class FileModelsStore : IModelsStore
{
    private readonly LockedJsonFile _file;

    public FileModelsStore(string? path = null) => _file = new LockedJsonFile(path ?? System.IO.Path.Combine(AppConfig.AgentDir, "models-store.json"));

    private JsonObject ReadAll()
    {
        try
        {
            return File.Exists(_file.Path) ? JsonNode.Parse(TextHelpers.StripBom(File.ReadAllText(_file.Path))) as JsonObject ?? new JsonObject() : new JsonObject();
        }
        catch
        {
            return new JsonObject();
        }
    }

    public Task<ModelsStoreEntry?> ReadAsync(string providerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var node = ReadAll()[providerId];
        return Task.FromResult(node is JsonObject ? IrisJson.Deserialize<ModelsStoreEntry>(node) : null);
    }

    public Task WriteAsync(string providerId, ModelsStoreEntry entry, CancellationToken cancellationToken = default) =>
        _file.WithLockAsync<bool>(content =>
        {
            var data = string.IsNullOrEmpty(content) ? new JsonObject() : JsonNode.Parse(TextHelpers.StripBom(content)) as JsonObject ?? new JsonObject();
            data[providerId] = IrisJson.ToNode(entry);
            return Task.FromResult((true, (string?)IrisJson.SerializeIndentedTwoSpaces(data)));
        }, cancellationToken);

    public Task DeleteAsync(string providerId, CancellationToken cancellationToken = default) =>
        _file.WithLockAsync<bool>(content =>
        {
            var data = string.IsNullOrEmpty(content) ? new JsonObject() : JsonNode.Parse(TextHelpers.StripBom(content)) as JsonObject ?? new JsonObject();
            data.Remove(providerId);
            return Task.FromResult((true, (string?)IrisJson.SerializeIndentedTwoSpaces(data)));
        }, cancellationToken);
}
