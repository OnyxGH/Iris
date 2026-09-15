using System.Text.Json.Nodes;
using Iris.Ai.Json;
using Iris.Ai.Utils;
using Iris.CodingAgent.Config;
using Iris.CodingAgent.Core;

namespace Iris.CodingAgent.Utils;

public sealed record LatestRelease(string Version);

/// <summary>New-version checks against the published versions of the Iris .NET tool package on NuGet.</summary>
public static class VersionCheck
{
    private const int DefaultTimeoutMs = 10000;

    /// <summary>NuGet flat-container version index for the tool package; IRIS_VERSION_INDEX_URL overrides it (mirrors, testing).</summary>
    public static string VersionIndexUrl =>
        Environment.GetEnvironmentVariable("IRIS_VERSION_INDEX_URL") is { Length: > 0 } url ? url.Trim() : $"https://api.nuget.org/v3-flatcontainer/{AppConfig.PackageId.ToLowerInvariant()}/index.json";

    public static int? ComparePackageVersions(string left, string right) =>
        ParseVersion(left) is { } l && ParseVersion(right) is { } r ? Semver.SemVersion.ComparePrecedence(l, r) : null;

    private static Semver.SemVersion? ParseVersion(string version) =>
        Semver.SemVersion.TryParse(version.Trim(), Semver.SemVersionStyles.AllowLowerV, out var parsed) ? parsed : null;

    public static bool IsNewerPackageVersion(string candidate, string current) =>
        ComparePackageVersions(candidate, current) is { } comparison ? comparison > 0 : candidate.Trim() != current.Trim();

    /// <summary>Highest published version; prereleases only count when the current version is a prerelease.</summary>
    public static string? SelectLatestVersion(IEnumerable<string> versions, string currentVersion)
    {
        var includePrerelease = ParseVersion(currentVersion)?.IsPrerelease == true;
        return versions
            .Select(v => (Text: v, Parsed: ParseVersion(v)))
            .Where(v => v.Parsed is not null && (includePrerelease || !v.Parsed.IsPrerelease))
            .OrderByDescending(v => v.Parsed!, Semver.SemVersion.PrecedenceComparer)
            .Select(v => v.Text)
            .FirstOrDefault();
    }

    public static async Task<LatestRelease?> GetLatestReleaseAsync(string currentVersion, int? timeoutMs = null, bool retry = false, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("IRIS_OFFLINE"))) return null;
        using var response = await ManagementHttp.FetchWithRetryAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, VersionIndexUrl);
            request.Headers.TryAddWithoutValidation("User-Agent", IrisUserAgent.Get());
            request.Headers.TryAddWithoutValidation("accept", "application/json");
            return request;
        }, ct, maxRetries: retry ? 2 : 0, timeoutMs: timeoutMs ?? DefaultTimeoutMs);
        if (!response.IsSuccessStatusCode) return null;
        if (JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) is not JsonObject data || data["versions"] is not JsonArray versions) return null;
        return SelectLatestVersion(versions.Select(IrisJson.GetString).OfType<string>(), currentVersion) is { } latest ? new LatestRelease(latest) : null;
    }

    public static async Task<LatestRelease?> CheckForNewVersionAsync(string currentVersion)
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("IRIS_SKIP_VERSION_CHECK"))) return null;
        try
        {
            var latest = await GetLatestReleaseAsync(currentVersion);
            return latest is not null && IsNewerPackageVersion(latest.Version, currentVersion) ? latest : null;
        }
        catch
        {
            return null;
        }
    }
}
