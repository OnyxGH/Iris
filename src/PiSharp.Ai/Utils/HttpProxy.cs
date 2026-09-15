using System.Net;
using System.Text.RegularExpressions;

namespace PiSharp.Ai.Utils;

/// <summary>
/// Environment-based proxy with undici EnvHttpProxyAgent semantics (pi's global dispatcher): http_proxy/HTTP_PROXY for
/// http targets, https_proxy/HTTPS_PROXY (falling back to the http proxy) for https targets, and no_proxy/NO_PROXY
/// exclusions re-read on every request. Unlike .NET's default proxy, the OS proxy configuration is never consulted.
/// </summary>
public sealed partial class EnvHttpProxy : IWebProxy
{
    private static readonly Dictionary<string, int> DefaultPorts = new() { ["http"] = 80, ["https"] = 443 };

    private readonly Lazy<(Uri? Http, Uri? Https)> _proxies = new(() =>
    {
        var http = ParseProxy(Environment.GetEnvironmentVariable("http_proxy") ?? Environment.GetEnvironmentVariable("HTTP_PROXY"));
        var https = ParseProxy(Environment.GetEnvironmentVariable("https_proxy") ?? Environment.GetEnvironmentVariable("HTTPS_PROXY")) ?? http;
        return (http, https);
    });

    public static EnvHttpProxy Instance { get; } = new();

    public ICredentials? Credentials { get; set; }

    [GeneratedRegex(@"^(.+):(\d+)$")]
    private static partial Regex HostPort();

    private static Uri? ParseProxy(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) throw new InvalidOperationException($"Invalid proxy URL {System.Text.Json.JsonSerializer.Serialize(value)}");
        return uri;
    }

    private static bool ShouldProxy(string hostname, int port)
    {
        var noProxy = Environment.GetEnvironmentVariable("no_proxy") ?? Environment.GetEnvironmentVariable("NO_PROXY") ?? "";
        var entries = Regex.Split(noProxy, @"[,\s]").Where(e => e.Length > 0).ToList();
        if (entries.Count == 0) return true;
        if (noProxy == "*") return false;
        foreach (var entry in entries)
        {
            var parsed = HostPort().Match(entry);
            var entryHost = Regex.Replace(parsed.Success ? parsed.Groups[1].Value : entry, @"^\*?\.", "").ToLowerInvariant();
            var entryPort = parsed.Success ? int.Parse(parsed.Groups[2].Value) : 0;
            if (entryPort != 0 && entryPort != port) continue;
            if (hostname == entryHost) return false;
            if (hostname.EndsWith("." + entryHost, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    public Uri? GetProxy(Uri destination)
    {
        // Keep IPv6 brackets, like undici's url.host with the port stripped.
        var hostname = Regex.Replace(destination.Authority, @":\d*$", "").ToLowerInvariant();
        var port = destination.IsDefaultPort ? DefaultPorts.GetValueOrDefault(destination.Scheme, 0) : destination.Port;
        if (!ShouldProxy(hostname, port)) return null;
        var (http, https) = _proxies.Value;
        return destination.Scheme == "https" ? https : http;
    }

    public bool IsBypassed(Uri host) => GetProxy(host) is null;

    /// <summary>Apply the httpProxy setting as HTTP_PROXY/HTTPS_PROXY when those are unset. Port of applyHttpProxySettings.</summary>
    public static void ApplyHttpProxySetting(string? httpProxy)
    {
        var proxy = httpProxy?.Trim();
        if (string.IsNullOrEmpty(proxy)) return;
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HTTP_PROXY"))) Environment.SetEnvironmentVariable("HTTP_PROXY", proxy);
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HTTPS_PROXY"))) Environment.SetEnvironmentVariable("HTTPS_PROXY", proxy);
    }

    public const string UnsupportedProxyProtocolMessage = "Unsupported proxy protocol. SOCKS and PAC proxy URLs are not supported; use an HTTP or HTTPS proxy URL.";

    /// <summary>Proxy URL for a target, including all_proxy and scheme-less values. Port of node-http-proxy.ts resolveHttpProxyUrlForTarget.</summary>
    public static Uri? ResolveHttpProxyUrlForTarget(string targetUrl, IReadOnlyDictionary<string, string>? env = null)
    {
        string GetEnv(string key) =>
            (env?.GetValueOrDefault(key.ToLowerInvariant()) is { Length: > 0 } a ? a : null)
            ?? (env?.GetValueOrDefault(key.ToUpperInvariant()) is { Length: > 0 } b ? b : null)
            ?? (Environment.GetEnvironmentVariable(key.ToLowerInvariant()) is { Length: > 0 } c ? c : null)
            ?? (Environment.GetEnvironmentVariable(key.ToUpperInvariant()) is { Length: > 0 } d ? d : null)
            ?? "";

        if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var target) || target.Host.Length == 0) return null;
        var protocol = target.Scheme;
        var hostname = target.Host.Trim('[', ']').ToLowerInvariant();
        var port = target.IsDefaultPort
            ? new Dictionary<string, int> { ["ftp"] = 21, ["gopher"] = 70, ["http"] = 80, ["https"] = 443, ["ws"] = 80, ["wss"] = 443 }.GetValueOrDefault(protocol, 0)
            : target.Port;

        var noProxy = GetEnv("no_proxy").ToLowerInvariant();
        if (noProxy == "*") return null;
        if (noProxy.Length > 0)
        {
            foreach (var raw in Regex.Split(noProxy, @"[,\s]"))
            {
                var trimmed = raw.Trim();
                if (trimmed.Length == 0) continue;
                string host;
                var entryPort = 0;
                if (trimmed.StartsWith('[') && trimmed.IndexOf(']') is var close and > 0)
                {
                    host = trimmed[1..close];
                    var rest = trimmed[(close + 1)..];
                    if (rest.StartsWith(':')) entryPort = int.TryParse(rest[1..], out var p) ? p : 0;
                }
                else if (trimmed.Split(':').Length > 2)
                {
                    host = trimmed;
                }
                else if (trimmed.IndexOf(':') is var colon and >= 0 && int.TryParse(trimmed[(colon + 1)..], out var parsedPort))
                {
                    host = trimmed[..colon];
                    entryPort = parsedPort;
                }
                else
                {
                    host = trimmed;
                }
                if (entryPort != 0 && entryPort != port) continue;
                var domain = host.Trim('[', ']');
                if (domain.StartsWith("*.", StringComparison.Ordinal)) domain = domain[2..];
                else if (domain.StartsWith('.') || domain.StartsWith('*')) domain = domain[1..];
                if (domain.Length == 0) continue;
                if (hostname == domain || hostname.EndsWith("." + domain, StringComparison.Ordinal)) return null;
            }
        }

        var proxy = GetEnv($"{protocol}_proxy");
        if (proxy.Length == 0) proxy = GetEnv("all_proxy");
        if (proxy.Length == 0) return null;
        if (!proxy.Contains("://", StringComparison.Ordinal)) proxy = $"{protocol}://{proxy}";
        if (!Uri.TryCreate(proxy, UriKind.Absolute, out var proxyUrl)) throw new InvalidOperationException($"Invalid proxy URL {System.Text.Json.JsonSerializer.Serialize(proxy)}: Invalid URL");
        if (proxyUrl.Scheme is not ("http" or "https")) throw new InvalidOperationException($"{UnsupportedProxyProtocolMessage} Got {proxyUrl.Scheme}:");
        return proxyUrl;
    }
}
