using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Iris.WebAccess.Http;

/// <summary>Hostname allow/deny lists for fetch_content targets (fetchContent.domainPolicy in web-search.json).</summary>
internal sealed record DomainPolicy(IReadOnlyList<string> Allow, IReadOnlyList<string> Deny)
{
    public static DomainPolicy None { get; } = new([], []);

    public static DomainPolicy Load()
    {
        if (WebConfig.Get("fetchContent") is not { } fetchContent) return None;
        if (fetchContent is not JsonObject obj) throw new InvalidOperationException($"fetchContent in {WebConfig.Path} must be an object");
        if (obj["domainPolicy"] is not { } policy) return None;
        if (policy is not JsonObject policyObj) throw new InvalidOperationException($"fetchContent.domainPolicy in {WebConfig.Path} must be an object");
        return new DomainPolicy(ParseEntries(policyObj["allow"], "allow"), ParseEntries(policyObj["deny"], "deny"));
    }

    private static List<string> ParseEntries(JsonNode? value, string field)
    {
        if (value is null) return [];
        if (value is not JsonArray array) throw new InvalidOperationException($"fetchContent.domainPolicy.{field} in {WebConfig.Path} must be an array of hostnames");
        return [.. array.Select((entry, index) =>
        {
            if (entry is not JsonValue text || !text.TryGetValue<string>(out var hostname))
            {
                throw new InvalidOperationException($"fetchContent.domainPolicy.{field} in {WebConfig.Path} must contain only hostnames; entry {index + 1} is not a string");
            }
            var normalized = SafeHttp.NormalizeHostname(hostname.Trim());
            if (normalized.Length == 0 || normalized.IndexOfAny([' ', '\\', '/', '?', ':', '#', '@']) >= 0 && !IPAddress.TryParse(normalized, out _))
            {
                throw new InvalidOperationException($"fetchContent.domainPolicy.{field} in {WebConfig.Path} contains an invalid hostname: \"{hostname}\"");
            }
            return normalized;
        })];
    }

    public void Assert(string hostname)
    {
        if (Deny.Any(entry => Matches(hostname, entry))) throw new InvalidOperationException($"Blocked hostname by fetch_content domain policy: {hostname}");
        if (Allow.Count > 0 && !Allow.Any(entry => Matches(hostname, entry))) throw new InvalidOperationException($"Hostname not allowed by fetch_content domain policy: {hostname}");
    }

    private static bool Matches(string hostname, string entry) => hostname == entry || hostname.EndsWith($".{entry}", StringComparison.Ordinal);
}

/// <summary>SSRF guard settings (ssrf in web-search.json).</summary>
internal sealed record SsrfSettings(IReadOnlyList<IPNetwork> AllowRanges, bool TrustEnvProxy)
{
    public static SsrfSettings Default { get; } = new([], false);

    public static SsrfSettings Load()
    {
        if (WebConfig.Get("ssrf") is not { } ssrf) return Default;
        if (ssrf is not JsonObject obj) throw new InvalidOperationException($"ssrf in {WebConfig.Path} must be an object");
        var trustEnvProxy = false;
        if (obj["trustEnvProxy"] is { } trust)
        {
            if (trust is not JsonValue value || !value.TryGetValue<bool>(out trustEnvProxy)) throw new InvalidOperationException($"ssrf.trustEnvProxy in {WebConfig.Path} must be a boolean");
        }
        var ranges = new List<IPNetwork>();
        if (obj["allowRanges"] is { } allowRanges)
        {
            if (allowRanges is not JsonArray array) throw new InvalidOperationException($"ssrf.allowRanges in {WebConfig.Path} must be an array of CIDR strings");
            foreach (var entry in array)
            {
                if (entry is not JsonValue text || !text.TryGetValue<string>(out var cidr)) throw new InvalidOperationException($"ssrf.allowRanges in {WebConfig.Path} must contain only CIDR strings");
                if (cidr.Trim().Length == 0) continue;
                ranges.Add(ParseCidr(cidr.Trim()) ?? throw new InvalidOperationException($"Invalid CIDR notation in ssrf.allowRanges: \"{cidr}\""));
            }
        }
        return new SsrfSettings(ranges, trustEnvProxy);
    }

    /// <summary>"198.18.0.0/15", "fd00::/8" or a bare address. The prefix must be at least 1 so a typo cannot allow everything.</summary>
    public static IPNetwork? ParseCidr(string raw)
    {
        var slash = raw.LastIndexOf('/');
        var addressPart = slash >= 0 ? raw[..slash] : raw;
        if (!IPAddress.TryParse(addressPart, out var address)) return null;
        var maxPrefix = address.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
        var prefix = maxPrefix;
        if (slash >= 0)
        {
            var prefixPart = raw[(slash + 1)..];
            if (prefixPart.Length == 0 || !prefixPart.All(char.IsAsciiDigit) || !int.TryParse(prefixPart, out prefix)) return null;
        }
        if (prefix < 1 || prefix > maxPrefix) return null;
        return new IPNetwork(MaskAddress(address, prefix), prefix);
    }

    private static IPAddress MaskAddress(IPAddress address, int prefix)
    {
        var bytes = address.GetAddressBytes();
        for (var i = 0; i < bytes.Length; i++)
        {
            var bits = Math.Clamp(prefix - i * 8, 0, 8);
            bytes[i] &= (byte)(0xff << (8 - bits));
        }
        return new IPAddress(bytes);
    }
}

/// <summary>How a remote request is validated before and while connecting.</summary>
internal sealed record RemoteFetchPolicy(SsrfSettings Ssrf, DomainPolicy? Domains = null, bool AllowLoopback = false);

/// <summary>
/// HTTP for web access. Remote targets (fetched pages, self-hosted endpoints) are checked against private and reserved
/// address ranges before the request and again on every connection, so DNS rebinding cannot reach internal hosts.
/// Redirects are followed manually and each hop is validated.
/// </summary>
internal static class SafeHttp
{
    public const string UserAgent = "Mozilla/5.0 (compatible; iris-web-access/1.0)";
    private const int MaxRedirects = 5;

    private static readonly HttpRequestOptionsKey<RemoteFetchPolicy> PolicyKey = new("iris.webaccess.policy");
    private static readonly HashSet<HttpStatusCode> RedirectStatuses =
        [HttpStatusCode.MovedPermanently, HttpStatusCode.Found, HttpStatusCode.SeeOther, HttpStatusCode.TemporaryRedirect, HttpStatusCode.PermanentRedirect];

    private static readonly Lazy<HttpClient> SharedClient = new(() => new HttpClient(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(30),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectCallback = ConnectAsync,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    });

    public static HttpClient Client => SharedClient.Value;

    /// <summary>Send a request to a remote target, validating the URL and every redirect hop against the policy.</summary>
    public static async Task<HttpResponseMessage> SendRemoteAsync(Uri url, Func<Uri, HttpRequestMessage> createRequest, RemoteFetchPolicy policy, CancellationToken cancellationToken)
    {
        var current = await ValidateRemoteUrlAsync(url, policy, cancellationToken);
        var configuredOrigin = Origin(current);
        var method = (HttpMethod?)null;
        for (var redirects = 0; ; redirects++)
        {
            var hopPolicy = Origin(current) == configuredOrigin ? policy : policy with { AllowLoopback = false };
            using var request = createRequest(current);
            if (method is not null)
            {
                request.Method = method;
                request.Content = null;
            }
            request.Options.Set(PolicyKey, hopPolicy);
            var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!RedirectStatuses.Contains(response.StatusCode) || response.Headers.Location is not { } location) return response;
            response.Dispose();
            if (redirects == MaxRedirects) throw new InvalidOperationException($"Too many redirects fetching {current}");
            var next = location.IsAbsoluteUri ? location : new Uri(current, location);
            current = await ValidateRemoteUrlAsync(next, Origin(next) == configuredOrigin ? policy : policy with { AllowLoopback = false }, cancellationToken);
            if (response.StatusCode == HttpStatusCode.SeeOther || request.Method == HttpMethod.Post && response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found)
            {
                method = HttpMethod.Get;
            }
        }
    }

    /// <summary>
    /// Send a request to a provider API. Redirects are followed manually; credential headers are dropped when a redirect
    /// leaves the original origin.
    /// </summary>
    public static async Task<HttpResponseMessage> SendApiAsync(Func<Uri, HttpRequestMessage> createRequest, Uri url, IReadOnlyCollection<string> credentialHeaders, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var origin = Origin(url);
        var current = url;
        HttpMethod? method = null;
        try
        {
            for (var redirects = 0; ; redirects++)
            {
                using var request = createRequest(current);
                if (method is not null)
                {
                    request.Method = method;
                    request.Content = null;
                }
                if (Origin(current) != origin)
                {
                    foreach (var header in credentialHeaders) request.Headers.Remove(header);
                }
                var response = await Client.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutSource.Token);
                if (!RedirectStatuses.Contains(response.StatusCode) || response.Headers.Location is not { } location) return response;
                response.Dispose();
                if (redirects == MaxRedirects) throw new InvalidOperationException($"Too many redirects fetching {current}");
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                if (current.Scheme != Uri.UriSchemeHttps && current.Scheme != Uri.UriSchemeHttp) throw new InvalidOperationException("Redirect to a non-HTTP URL was blocked");
                if (response.StatusCode == HttpStatusCode.SeeOther || request.Method == HttpMethod.Post && response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found)
                {
                    method = HttpMethod.Get;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Request to {url.Host} timed out after {(int)timeout.TotalSeconds}s");
        }
    }

    public static async Task<Uri> ValidateRemoteUrlAsync(Uri url, RemoteFetchPolicy policy, CancellationToken cancellationToken)
    {
        if (!url.IsAbsoluteUri || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)) throw new InvalidOperationException("Only HTTP and HTTPS URLs can be fetched remotely");
        var hostname = NormalizeHostname(url.IdnHost);
        if (hostname.Length == 0) throw new InvalidOperationException("URL must include a hostname");
        if (hostname == "localhost")
        {
            if (policy.AllowLoopback) return url;
            throw new InvalidOperationException($"Blocked internal hostname: {hostname}");
        }
        if (hostname.EndsWith(".localhost", StringComparison.Ordinal)) throw new InvalidOperationException($"Blocked internal hostname: {hostname}");
        policy.Domains?.Assert(hostname);
        if (IPAddress.TryParse(hostname, out var literal))
        {
            AssertPublicAddress(literal, hostname, policy);
            return url;
        }
        if (ShouldTrustEnvProxy(url, policy.Ssrf.TrustEnvProxy)) return url;

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(hostname, cancellationToken);
        }
        catch (SocketException ex)
        {
            throw new InvalidOperationException($"Failed to resolve {hostname}: {ex.Message}");
        }
        if (addresses.Length == 0) throw new InvalidOperationException($"Failed to resolve {hostname}: no addresses returned");
        foreach (var address in addresses) AssertPublicAddress(address, hostname, policy);
        return url;
    }

    public static string NormalizeHostname(string hostname) => hostname.ToLowerInvariant().Trim('[', ']').TrimEnd('.');

    /// <summary>Read a response body, failing once it exceeds maxBytes.</summary>
    public static async Task<byte[]> ReadBodyAsync(HttpResponseMessage response, long maxBytes, Func<Exception> tooLarge, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes) throw tooLarge();
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>Decode text using the response charset, falling back to UTF-8.</summary>
    public static string DecodeText(byte[] body, HttpContentHeaders headers)
    {
        var charset = headers.ContentType?.CharSet?.Trim('"', '\'');
        if (!string.IsNullOrEmpty(charset))
        {
            try
            {
                return (Encoding.GetEncoding(charset)).GetString(body);
            }
            catch (ArgumentException)
            {
                // Unknown charset.
            }
        }
        return Encoding.UTF8.GetString(body);
    }

    private static string Origin(Uri uri) => uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();

    private static void AssertPublicAddress(IPAddress address, string hostname, RemoteFetchPolicy policy)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (policy.Ssrf.AllowRanges.Any(range => range.BaseAddress.AddressFamily == address.AddressFamily && range.Contains(address))) return;
        if (policy.AllowLoopback && IPAddress.IsLoopback(address)) return;
        if (address.AddressFamily == AddressFamily.InterNetwork && IsBlockedIPv4(address))
        {
            var hint = IsFakeIpProxyAddress(address)
                ? ". This address is in 198.18.0.0/15, commonly used by TUN/fake-IP proxies. If that matches your setup, configure ssrf.allowRanges with [\"198.18.0.0/15\"] in web-search.json."
                : "";
            throw new InvalidOperationException($"Blocked internal address for {hostname}: {address}{hint}");
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && IsBlockedIPv6(address)) throw new InvalidOperationException($"Blocked internal address for {hostname}: {address}");
        if (address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)) throw new InvalidOperationException($"Resolved non-IP address for {hostname}: {address}");
    }

    private static bool IsFakeIpProxyAddress(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 198 && bytes[1] is 18 or 19;
    }

    internal static bool IsBlockedIPv4(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b[0] == 0 || b[0] == 10 || b[0] == 127
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
            || (b[0] == 169 && b[1] == 254)
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || IsFakeIpProxyAddress(address)
            || b[0] >= 224;
    }

    internal static bool IsBlockedIPv6(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) return IsBlockedIPv4(address.MapToIPv4());
        var b = address.GetAddressBytes();
        if (b.All(x => x == 0)) return true;
        if (b.Take(15).All(x => x == 0) && b[15] == 1) return true;
        if ((b[0] & 0xfe) == 0xfc) return true;
        if (b[0] == 0xfe && (b[1] & 0xc0) == 0x80) return true;
        return false;
    }

    private static bool ShouldTrustEnvProxy(Uri url, bool enabled)
    {
        if (!enabled) return false;
        var candidates = url.Scheme == Uri.UriSchemeHttp
            ? new[] { "HTTP_PROXY", "http_proxy", "ALL_PROXY", "all_proxy" }
            : ["HTTPS_PROXY", "https_proxy", "HTTP_PROXY", "http_proxy", "ALL_PROXY", "all_proxy"];
        var hasProxy = candidates.Select(Environment.GetEnvironmentVariable).Any(value =>
            !string.IsNullOrWhiteSpace(value) && Uri.TryCreate(value.Trim(), UriKind.Absolute, out var proxy) && proxy.Scheme is "http" or "https" && proxy.Host.Length > 0);
        if (!hasProxy) return false;
        var hostname = NormalizeHostname(url.IdnHost);
        var port = url.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var noProxy = Environment.GetEnvironmentVariable("NO_PROXY") ?? Environment.GetEnvironmentVariable("no_proxy") ?? "";
        return !noProxy.Split(',').Any(entry => NoProxyMatches(hostname, port, entry));
    }

    private static bool NoProxyMatches(string hostname, string port, string entry)
    {
        var trimmed = entry.Trim();
        if (trimmed.Length == 0) return false;
        if (trimmed == "*") return true;
        var hostEntry = trimmed;
        string? entryPort = null;
        if (hostEntry.StartsWith('['))
        {
            var closing = hostEntry.IndexOf(']');
            if (closing >= 0)
            {
                var suffix = hostEntry[(closing + 1)..];
                if (suffix.Length > 1 && suffix[0] == ':' && suffix[1..].All(char.IsAsciiDigit)) entryPort = suffix[1..];
                hostEntry = hostEntry[..(closing + 1)];
            }
        }
        else
        {
            var colon = hostEntry.LastIndexOf(':');
            if (colon > -1 && hostEntry[(colon + 1)..] is { Length: > 0 } digits && digits.All(char.IsAsciiDigit))
            {
                entryPort = digits;
                hostEntry = hostEntry[..colon];
            }
        }
        if (entryPort is not null && entryPort != port) return false;
        var normalized = NormalizeHostname(hostEntry);
        if (normalized.Length == 0) return false;
        if (normalized == hostname) return true;
        var suffixMatch = normalized.StartsWith("*.", StringComparison.Ordinal) ? normalized[1..] : normalized.StartsWith('.') ? normalized : $".{normalized}";
        return hostname.EndsWith(suffixMatch, StringComparison.Ordinal);
    }

    /// <summary>Connect, re-checking the resolved addresses of guarded targets (not of proxies).</summary>
    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var endpoint = context.DnsEndPoint;
        context.InitialRequestMessage.Options.TryGetValue(PolicyKey, out var policy);
        var requestHost = context.InitialRequestMessage.RequestUri is { } uri ? NormalizeHostname(uri.IdnHost) : null;
        var guarded = policy is not null && requestHost == NormalizeHostname(endpoint.Host);

        var addresses = IPAddress.TryParse(endpoint.Host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(endpoint.Host, cancellationToken);
        if (guarded)
        {
            var allowed = new List<IPAddress>();
            Exception? blocked = null;
            foreach (var address in addresses)
            {
                try
                {
                    AssertPublicAddress(address, endpoint.Host, policy!);
                    allowed.Add(address);
                }
                catch (InvalidOperationException ex)
                {
                    blocked ??= ex;
                }
            }
            if (allowed.Count == 0) throw blocked ?? new InvalidOperationException($"Failed to resolve {endpoint.Host}: no addresses returned");
            addresses = [.. allowed];
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, endpoint.Port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public static HttpContent JsonBody(JsonNode body) => new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
}
