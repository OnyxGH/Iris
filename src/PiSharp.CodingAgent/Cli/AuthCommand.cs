using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.Ai;
using PiSharp.Ai.Auth;
using PiSharp.Ai.Json;
using PiSharp.Ai.Models;
using PiSharp.CodingAgent.Config;
using PiSharp.CodingAgent.Core;
using PiSharp.CodingAgent.Utils;

namespace PiSharp.CodingAgent.Cli;

public sealed class AuthCommandException(string message) : Exception(message);

/// <summary>auth.json reader that never writes or runs command-backed keys. Port of ReadOnlyAuthStorage.</summary>
public sealed class ReadOnlyAuthStorage(string? authPath = null) : ICredentialStore
{
    private readonly string _authPath = PathUtils.NormalizePath(authPath ?? AppConfig.AuthPath);
    private JsonObject? _data;

    private JsonObject Load()
    {
        if (_data is not null) return _data;
        JsonNode? parsed;
        try
        {
            if (!File.Exists(_authPath)) return _data = new JsonObject();
            parsed = JsonNode.Parse(TextHelpers.StripBom(File.ReadAllText(_authPath)));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to read auth.json: {ex.Message}");
        }
        if (parsed is not JsonObject obj) throw new InvalidOperationException("Invalid auth.json: expected an object");
        foreach (var (providerId, credential) in obj)
        {
            if (credential is JsonObject value)
            {
                var type = PiJson.GetString(value["type"]);
                if (type == "api_key")
                {
                    var validKey = !value.ContainsKey("key") || PiJson.GetString(value["key"]) is not null;
                    var validEnv = !value.ContainsKey("env") || (value["env"] is JsonObject env && env.All(kv => PiJson.GetString(kv.Value) is not null));
                    if (validKey && validEnv) continue;
                }
                else if (type == "oauth" && PiJson.GetString(value["access"]) is not null && PiJson.GetString(value["refresh"]) is not null
                    && PiJson.GetNumber(value["expires"]) is { } expires && double.IsFinite(expires))
                {
                    continue;
                }
            }
            throw new InvalidOperationException($"Invalid auth.json credential for provider \"{providerId}\"");
        }
        return _data = obj;
    }

    public Task<Credential?> ReadAsync(string providerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Load()[providerId] is not JsonObject obj) return Task.FromResult<Credential?>(null);
        var credential = CredentialJsonConverter.FromJson((JsonObject)obj.DeepClone());
        if (credential is ApiKeyCredential { Key: { Length: > 0 } key } api && !ConfigValueResolver.IsCommandConfigValue(key))
        {
            return Task.FromResult<Credential?>(new ApiKeyCredential { Key = ConfigValueResolver.Resolve(key, api.Env), Env = api.Env });
        }
        return Task.FromResult(credential);
    }

    public Task<IReadOnlyList<CredentialInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<CredentialInfo> list = Load().Select(kv => new CredentialInfo(kv.Key, PiJson.GetString(kv.Value?["type"]) ?? "")).ToList();
        return Task.FromResult(list);
    }

    public Task<Credential?> ModifyAsync(string providerId, Func<Credential?, Task<Credential?>> fn, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Read-only credential storage cannot modify auth.json");

    public Task DeleteAsync(string providerId, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Read-only credential storage cannot modify auth.json");
}

/// <summary>`pisharp auth check | print-api-key | print-bearer-token`. Port of cli/auth-command.ts, auth-check.ts and credential-print.ts.</summary>
public static partial class AuthCommand
{
    private const long DefaultBearerTokenMinExpiryMs = 30 * 60_000;

    private sealed record Command(string Kind, List<string> Args, bool Json, bool Credentials, bool NoRefresh, long? MinExpiryMs);

    private sealed record CheckResult(string Status, string Provider, string? Reason = null, string? AuthType = null);

    private static string CommandName(string kind) => kind switch
    {
        "check" => "auth check",
        "api_key" => "auth print-api-key",
        _ => "auth print-bearer-token",
    };

    private static string CommandUsage(string kind) => kind switch
    {
        "check" => $"{AppConfig.AppName} auth check --provider <provider> [--json] [--credentials] [--no-refresh]",
        "api_key" => $"{AppConfig.AppName} auth print-api-key --provider <provider> [--model <model>]",
        _ => $"{AppConfig.AppName} auth print-bearer-token --provider <provider> [--model <model>] [--min-expiry <duration>]",
    };

    private static bool IsHelp(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0] == "auth" && (args.Count == 1 || args[1] == "help" || args.Contains("--help") || args.Contains("-h"));

    private static void PrintHelp() => Console.WriteLine($"""
        Usage:
          {AppConfig.AppName} auth print-api-key [--provider <provider>] [--model <model>]
          {AppConfig.AppName} auth print-bearer-token [--provider <provider>] [--model <model>] [--min-expiry <duration>]
          {AppConfig.AppName} auth check [--provider <provider>] [--model <model>] [--json] [--credentials] [--no-refresh]

        Auth commands require at least one of --provider or --model. Checks refresh expired OAuth credentials by default; --no-refresh prevents this. --credentials emits the credential, or includes it in JSON output.
        """);

    [GeneratedRegex(@"^(\d+)(ms|s|m|h)$", RegexOptions.IgnoreCase)]
    private static partial Regex Duration();

    private static Command? Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0] != "auth") return null;
        var kind = args[1] switch
        {
            "check" => "check",
            "print-api-key" => "api_key",
            "print-bearer-token" => "bearer_token",
            _ => null,
        } ?? throw new AuthCommandException($"Unknown auth command \"{args[1]}\". Use \"{AppConfig.AppName} auth print-api-key\", \"{AppConfig.AppName} auth print-bearer-token\", or \"{AppConfig.AppName} auth check\".");

        var commandArgs = new List<string>();
        bool json = false, credentials = false, noRefresh = false;
        long? minExpiryMs = null;
        for (var index = 2; index < args.Count; index++)
        {
            var arg = args[index];
            if (arg == "--min-expiry")
            {
                if (kind != "bearer_token") throw new AuthCommandException("--min-expiry is only supported by print-bearer-token");
                var value = index + 1 < args.Count ? args[++index] : null;
                var match = value is null ? null : Duration().Match(value);
                if (match is not { Success: true }) throw new AuthCommandException("--min-expiry must use a duration such as 30m or 1h");
                var amount = long.Parse(match.Groups[1].Value);
                minExpiryMs = amount * match.Groups[2].Value.ToLowerInvariant() switch { "ms" => 1, "s" => 1_000, "m" => 60_000, _ => 3_600_000 };
                continue;
            }
            if (arg is "--json" or "--credentials" or "--no-refresh")
            {
                if (kind != "check") throw new AuthCommandException($"{arg} is only supported by auth check");
                if (arg == "--json") json = true;
                else if (arg == "--credentials") credentials = true;
                else noRefresh = true;
                continue;
            }
            commandArgs.Add(arg);
        }
        return new Command(kind, commandArgs, json, credentials, noRefresh, minExpiryMs);
    }

    private static (string? Provider, string? Model) ValidateArgs(CliArgs args, string kind)
    {
        var provider = string.IsNullOrWhiteSpace(args.Provider) ? null : args.Provider.Trim();
        var model = string.IsNullOrWhiteSpace(args.Model) ? null : args.Model.Trim();
        if (args.UnknownFlags.Count > 0) throw new AuthCommandException($"Unknown option --{args.UnknownFlags.Keys.First()} for \"{CommandName(kind)}\".");
        if (args.ApiKey is not null || args.Messages.Count > 0 || args.FileArgs.Count > 0) throw new AuthCommandException("Auth commands only accept --provider and --model");
        if (provider is null && model is null)
        {
            throw new AuthCommandException(kind == "check" ? "Auth checks require --provider <provider> or --model <model>" : "Credential printing requires --provider <provider> or --model <model>");
        }
        return (provider, model);
    }

    private static string? GetAuthCredential(AuthResult? auth)
    {
        if (!string.IsNullOrEmpty(auth?.Auth.ApiKey)) return auth.Auth.ApiKey;
        var authorization = auth?.Auth.Headers?.FirstOrDefault(kv => kv.Key.Equals("authorization", StringComparison.OrdinalIgnoreCase)).Value;
        return authorization is null ? null : Regex.Match(authorization, @"^Bearer\s+(.+)$", RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value : null;
    }

    /// <summary>Handle `auth ...` arguments. Returns null when the arguments are not an auth command, otherwise the exit code.</summary>
    public static async Task<int?> RunAsync(IReadOnlyList<string> args)
    {
        if (IsHelp(args))
        {
            PrintHelp();
            return 0;
        }
        Command? command;
        try
        {
            command = Parse(args);
        }
        catch (AuthCommandException ex)
        {
            Console.Error.WriteLine(Chalk.Red($"Error: {ex.Message}"));
            return 1;
        }
        if (command is null) return null;

        var parsed = CliArgs.Parse(command.Args);
        if (parsed.UnknownFlags.Count > 0)
        {
            Console.Error.WriteLine(Chalk.Red($"Unknown option --{parsed.UnknownFlags.Keys.First()} for \"{CommandName(command.Kind)}\"."));
            Console.Error.WriteLine(Chalk.Dim($"Use \"{AppConfig.AppName} --help\" or \"{CommandUsage(command.Kind)}\"."));
            return 1;
        }
        try
        {
            if (parsed.Diagnostics.Count > 0) throw new AuthCommandException(string.Join("\n", parsed.Diagnostics.Select(d => d.Message)));
            if (command.Kind != "check")
            {
                using var cts = new CancellationTokenSource(15_000);
                var runtime = await ModelRuntime.CreateAsync(new CreateModelRuntimeOptions { AllowModelNetwork = false, CancellationToken = cts.Token });
                var credential = await ResolveCredentialForPrintAsync(parsed, runtime, command.Kind, command.MinExpiryMs, cts.Token);
                Console.Out.Write($"{credential}\n");
                return 0;
            }

            var requested = ValidateArgs(parsed, command.Kind);
            CheckResult result;
            string? printed = null;
            try
            {
                ICredentialStore credentials = command.NoRefresh ? new ReadOnlyAuthStorage() : AuthStorage.Create();
                var runtime = await ModelRuntime.CreateAsync(new CreateModelRuntimeOptions
                {
                    Credentials = credentials,
                    ModelsStore = new InMemoryModelsStore(),
                    AllowModelNetwork = false,
                    RefreshOnCreate = false,
                });
                result = await CheckProviderAuthAsync(parsed, runtime, !command.NoRefresh);
                if (command.Credentials && result.Status == "ready")
                {
                    var stored = await credentials.ReadAsync(result.Provider);
                    printed = !command.NoRefresh || stored is not OAuthCredential oauth ? GetAuthCredential(await runtime.GetAuthAsync(result.Provider)) : oauth.Access;
                    if (string.IsNullOrEmpty(printed))
                    {
                        printed = null;
                        result = new CheckResult("not_ready", result.Provider, "credential_not_available");
                    }
                }
            }
            catch
            {
                result = new CheckResult("invalid", requested.Provider ?? requested.Model!, "invalid_state");
            }
            string output;
            if (command.Json)
            {
                var obj = new JsonObject { ["status"] = result.Status, ["provider"] = result.Provider };
                if (result.Reason is not null) obj["reason"] = result.Reason;
                if (result.AuthType is not null) obj["authType"] = result.AuthType;
                if (printed is not null) obj["credentials"] = printed;
                output = PiJson.Stringify(obj);
            }
            else
            {
                output = printed ?? result.Status;
            }
            Console.Out.Write($"{output}\n");
            return result.Status switch { "ready" => 0, "not_ready" => 1, _ => 2 };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(Chalk.Red($"Error: {(ex is AuthCommandException ? ex.Message : "Failed to resolve credential")}"));
            return command.Kind == "check" ? 2 : 1;
        }
    }

    private static async Task<CheckResult> CheckProviderAuthAsync(CliArgs args, ModelRuntime runtime, bool refresh)
    {
        var (cliProvider, cliModel) = ValidateArgs(args, "check");
        var provider = cliProvider;
        if (cliModel is not null)
        {
            var resolved = ModelResolver.ResolveCliModel(cliProvider, cliModel, null, runtime);
            if (resolved.Error is not null || resolved.Model is null) throw new AuthCommandException(resolved.Error ?? $"Unable to resolve model \"{cliModel}\"");
            provider = resolved.Model.Provider;
        }
        if (provider is null) throw new AuthCommandException("Unable to resolve an auth provider");
        if (runtime.GetError() is not null) return new CheckResult("invalid", provider, "invalid_state");
        if (runtime.GetProvider(provider) is null) return new CheckResult("not_ready", provider, "provider_not_found");
        try
        {
            var auth = await runtime.CheckAuthAsync(provider);
            if (auth is null) return new CheckResult("not_ready", provider, "credentials_not_configured");
            if (refresh && await runtime.GetAuthAsync(provider) is null) return new CheckResult("not_ready", provider, "credentials_not_configured");
            return new CheckResult("ready", provider, AuthType: auth.Type);
        }
        catch
        {
            return new CheckResult("invalid", provider, "invalid_state");
        }
    }

    private static async Task<string> ResolveCredentialForPrintAsync(CliArgs args, ModelRuntime runtime, string kind, long? minExpiryMs, CancellationToken ct)
    {
        var (cliProvider, cliModel) = ValidateArgs(args, kind);
        var credentialTypes = (await runtime.ListCredentialsAsync(ct)).GroupBy(c => c.ProviderId).ToDictionary(g => g.Key, g => g.Last().Type);
        var providers = new List<(string Id, Model? Model)>();
        if (cliProvider is not null)
        {
            var provider = runtime.GetProvider(cliProvider) ?? throw new AuthCommandException($"Unknown provider \"{cliProvider}\". Use --list-models to see available providers.");
            if (cliModel is not null)
            {
                var resolved = ModelResolver.ResolveCliModel(provider.Id, cliModel, null, runtime);
                if (resolved.Error is not null || resolved.Model is null) throw new AuthCommandException(resolved.Error ?? "Unable to resolve the requested provider/model");
                providers.Add((provider.Id, resolved.Model));
            }
            else
            {
                providers.Add((provider.Id, null));
            }
        }
        else
        {
            foreach (var provider in runtime.GetProviders())
            {
                if (!credentialTypes.ContainsKey(provider.Id)) continue;
                var resolved = ModelResolver.ResolveCliModel(provider.Id, cliModel!, null, runtime);
                if (resolved.Model is not null && resolved.Error is null && resolved.Warning?.Contains("Using custom model id") != true) providers.Add((provider.Id, resolved.Model));
            }
            if (providers.Count == 0) throw new AuthCommandException($"Model \"{cliModel}\" not found. Use --list-models to see available models.");
        }

        var credentials = new List<(string ProviderId, string Value)>();
        foreach (var (id, model) in providers)
        {
            var type = credentialTypes.GetValueOrDefault(id);
            if (kind == "api_key" && type == AuthTypes.OAuth) continue;
            if (kind == "bearer_token" && type != AuthTypes.OAuth) continue;
            var overrides = new AuthResolutionOverrides
            {
                MinOAuthValidityMs = kind == "bearer_token" ? minExpiryMs ?? DefaultBearerTokenMinExpiryMs : null,
                CancellationToken = ct,
            };
            var auth = model is not null ? await runtime.GetAuthAsync(model, overrides) : await runtime.GetAuthAsync(id, overrides);
            if (GetAuthCredential(auth) is { Length: > 0 } value) credentials.Add((id, value));
        }

        if (credentials.Count == 1) return credentials[0].Value;
        if (credentials.Count == 0)
        {
            var providerId = providers.Count > 0 ? providers[0].Id : null;
            var type = providerId is not null ? credentialTypes.GetValueOrDefault(providerId) : null;
            if (cliProvider is not null && kind == "api_key" && type == AuthTypes.OAuth) throw new AuthCommandException($"Provider \"{providerId}\" is configured with OAuth, not an API key");
            if (cliProvider is not null && kind == "bearer_token" && type != AuthTypes.OAuth) throw new AuthCommandException($"Provider \"{providerId}\" is not configured with an OAuth bearer token");
            throw new AuthCommandException($"No usable {(kind == "api_key" ? "API key" : "OAuth bearer token")} is configured");
        }
        throw new AuthCommandException($"Multiple configured providers matched ({string.Join(", ", credentials.Select(c => c.ProviderId))}). Specify --provider.");
    }
}
