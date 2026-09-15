using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Iris.Ai.Json;

namespace Iris.Ai.Auth;

/// <summary>Request auth for a single model request.</summary>
public sealed class ModelAuth
{
    public string? ApiKey { get; set; }
    public Dictionary<string, string?>? Headers { get; set; }
    public string? BaseUrl { get; set; }

    public ModelAuth Clone() => new()
    {
        ApiKey = ApiKey,
        Headers = Headers is null ? null : new Dictionary<string, string?>(Headers),
        BaseUrl = BaseUrl,
    };
}

/// <summary>One type-tagged credential per provider — the shape of auth.json entries.</summary>
[JsonConverter(typeof(CredentialJsonConverter))]
public abstract class Credential
{
    public abstract string Type { get; }
}

public sealed class ApiKeyCredential : Credential
{
    public override string Type => "api_key";

    public string? Key { get; set; }

    /// <summary>Provider-scoped environment/config values such as Cloudflare account ids.</summary>
    public Dictionary<string, string>? Env { get; set; }
}

public sealed class OAuthCredential : Credential
{
    public override string Type => "oauth";

    public string Refresh { get; set; } = "";

    public string Access { get; set; } = "";

    /// <summary>Unix ms expiry.</summary>
    public long Expires { get; set; }

    /// <summary>Additional provider-specific fields (e.g. enterpriseUrl, accountId).</summary>
    public JsonObject Extra { get; set; } = new();

    public OAuthCredential Clone() => new()
    {
        Refresh = Refresh,
        Access = Access,
        Expires = Expires,
        Extra = (JsonObject)Extra.DeepClone(),
    };
}

public sealed class CredentialJsonConverter : JsonConverter<Credential>
{
    public override Credential? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        var obj = JsonNode.Parse(ref reader) as JsonObject ?? throw new JsonException("Credential must be an object");
        return FromJson(obj);
    }

    public static Credential? FromJson(JsonObject obj)
    {
        var type = obj["type"] is JsonValue tv && tv.TryGetValue<string>(out var t) ? t : null;
        switch (type)
        {
            case "api_key":
            {
                var credential = new ApiKeyCredential
                {
                    Key = obj["key"] is JsonValue kv && kv.TryGetValue<string>(out var k) ? k : null,
                };
                if (obj["env"] is JsonObject env)
                {
                    credential.Env = env
                        .Where(p => p.Value is JsonValue v && v.TryGetValue<string>(out _))
                        .ToDictionary(p => p.Key, p => p.Value!.GetValue<string>());
                }
                return credential;
            }
            case "oauth":
            {
                var extra = (JsonObject)obj.DeepClone();
                string Take(string name)
                {
                    var value = extra[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
                    extra.Remove(name);
                    return value;
                }
                extra.Remove("type");
                var refresh = Take("refresh");
                var access = Take("access");
                long expires = extra["expires"] is JsonValue ev && IrisJson.TryGetNumber(ev, out var e) ? (long)e : 0;
                extra.Remove("expires");
                return new OAuthCredential { Refresh = refresh, Access = access, Expires = expires, Extra = extra };
            }
            default:
                return null;
        }
    }

    public static JsonObject ToJson(Credential credential)
    {
        switch (credential)
        {
            case ApiKeyCredential api:
            {
                var obj = new JsonObject { ["type"] = "api_key" };
                if (api.Key is not null) obj["key"] = api.Key;
                if (api.Env is not null)
                {
                    var env = new JsonObject();
                    foreach (var (k, v) in api.Env) env[k] = v;
                    obj["env"] = env;
                }
                return obj;
            }
            case OAuthCredential oauth:
            {
                var obj = new JsonObject
                {
                    ["type"] = "oauth",
                    ["refresh"] = oauth.Refresh,
                    ["access"] = oauth.Access,
                    ["expires"] = oauth.Expires,
                };
                foreach (var (k, v) in oauth.Extra) obj[k] = v?.DeepClone();
                return obj;
            }
            default:
                throw new JsonException($"Unknown credential type {credential.Type}");
        }
    }

    public override void Write(Utf8JsonWriter writer, Credential value, JsonSerializerOptions options) =>
        ToJson(value).WriteTo(writer, options);
}

public sealed record CredentialInfo(string ProviderId, string Type);

/// <summary>
/// App-owned credential storage keyed by provider id. <see cref="ModifyAsync"/> is the only write path and is
/// serialized per provider.
/// </summary>
public interface ICredentialStore
{
    Task<Credential?> ReadAsync(string providerId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CredentialInfo>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Serialized read-modify-write. Return null from <paramref name="fn"/> to leave the entry unchanged.</summary>
    Task<Credential?> ModifyAsync(string providerId, Func<Credential?, Task<Credential?>> fn, CancellationToken cancellationToken = default);

    Task DeleteAsync(string providerId, CancellationToken cancellationToken = default);
}

/// <summary>Environment access for auth resolution.</summary>
public interface IAuthContext
{
    Task<string?> EnvAsync(string name);

    /// <summary>Check whether a file exists. Supports a leading "~".</summary>
    Task<bool> FileExistsAsync(string path);
}

public sealed class DefaultAuthContext : IAuthContext
{
    public static readonly DefaultAuthContext Instance = new();

    public Task<string?> EnvAsync(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return Task.FromResult(string.IsNullOrWhiteSpace(value) ? null : value);
    }

    public Task<bool> FileExistsAsync(string path)
    {
        var resolved = path.StartsWith('~') ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..] : path;
        return Task.FromResult(File.Exists(resolved) || Directory.Exists(resolved));
    }
}

public sealed class OverlayEnvAuthContext(IAuthContext inner, IReadOnlyDictionary<string, string> env) : IAuthContext
{
    public async Task<string?> EnvAsync(string name) =>
        env.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value) ? value : await inner.EnvAsync(name);

    public Task<bool> FileExistsAsync(string path) => inner.FileExistsAsync(path);
}

public sealed class AuthResult
{
    public ModelAuth Auth { get; set; } = new();

    /// <summary>Provider-scoped environment/config values resolved from credentials and ambient context.</summary>
    public Dictionary<string, string>? Env { get; set; }

    /// <summary>Human-readable label for status UI: "ANTHROPIC_API_KEY", "OAuth", ...</summary>
    public string? Source { get; set; }
}

public sealed record AuthCheck(string Type, string? Source = null);

public static class AuthTypes
{
    public const string ApiKey = "api_key";
    public const string OAuth = "oauth";
}

public sealed record AuthPromptOption(string Id, string Label, string? Description = null);

/// <summary>Prompt shown to the user during login. Type is text, secret, select or manual_code.</summary>
public sealed class AuthPrompt
{
    public string Type { get; init; } = "text";
    public string Message { get; init; } = "";
    public string? Placeholder { get; init; }
    public IReadOnlyList<AuthPromptOption>? Options { get; init; }
    public CancellationToken CancellationToken { get; init; }
}

public sealed record AuthInfoLink(string Url, string? Label = null);

public abstract record AuthEvent(string Type);

public sealed record AuthInfoEvent(string Message, IReadOnlyList<AuthInfoLink>? Links = null) : AuthEvent("info");

public sealed record AuthUrlEvent(string Url, string? Instructions = null) : AuthEvent("auth_url");

public sealed record AuthDeviceCodeEvent(string UserCode, string VerificationUri, int? IntervalSeconds = null, int? ExpiresInSeconds = null) : AuthEvent("device_code");

public sealed record AuthProgressEvent(string Message) : AuthEvent("progress");

/// <summary>Login interaction callbacks serving both api-key and OAuth flows.</summary>
public interface IAuthInteraction
{
    CancellationToken CancellationToken { get; }

    /// <summary>Returns the entered/selected string (select returns the option id). Throws on cancel.</summary>
    Task<string> PromptAsync(AuthPrompt prompt);

    void Notify(AuthEvent authEvent);
}

public sealed record ApiKeyAuthInput(IAuthContext Ctx, ApiKeyCredential? Credential, CancellationToken CancellationToken);

/// <summary>Api-key auth: stored key/provider env plus ambient sources. Ambient-only providers omit Login.</summary>
public sealed class ApiKeyAuth
{
    public required string Name { get; init; }

    public Func<IAuthInteraction, Task<ApiKeyCredential>>? Login { get; init; }

    /// <summary>Optional side-effect-free availability check.</summary>
    public Func<ApiKeyAuthInput, Task<AuthCheck?>>? Check { get; init; }

    public required Func<ApiKeyAuthInput, Task<AuthResult?>> Resolve { get; init; }
}

/// <summary>OAuth auth.</summary>
public sealed class OAuthAuth
{
    public required string Name { get; init; }

    public bool IsSubscription { get; init; }

    public string? LoginLabel { get; init; }

    public required Func<IAuthInteraction, Task<OAuthCredential>> Login { get; init; }

    public required Func<OAuthCredential, CancellationToken, Task<OAuthCredential>> Refresh { get; init; }

    public required Func<OAuthCredential, Task<ModelAuth>> ToAuth { get; init; }
}

public sealed class ProviderAuth
{
    public ApiKeyAuth? ApiKey { get; init; }
    public OAuthAuth? OAuth { get; init; }
}

public enum ModelsErrorCode
{
    ModelSource,
    ModelValidation,
    Provider,
    Stream,
    Auth,
    OAuth,
}

public sealed class ModelsException : Exception
{
    public ModelsException(ModelsErrorCode code, string message, Exception? cause = null)
        : base(WithCauseDetail(message, cause), cause)
    {
        Code = code;
    }

    public ModelsErrorCode Code { get; }

    private static string WithCauseDetail(string message, Exception? cause)
    {
        if (cause is null) return message;
        var detail = (string.IsNullOrEmpty(cause.Message) ? cause.GetType().Name : cause.Message).Trim();
        if (detail.Length == 0 || message.Contains(detail, StringComparison.Ordinal)) return message;
        return $"{message}: {detail}";
    }
}
