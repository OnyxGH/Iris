using System.Text.Json.Nodes;
using Iris.Ai.Json;
using Iris.Ai.Utils;
using Iris.CodingAgent.Core;

namespace Iris.CodingAgent.Utils;

public sealed record LatestRelease(string Version, string? PackageName = null, string? Note = null);

/// <summary>
/// New-version checks. Iris has no release endpoint yet, so the URL comes from IRIS_LATEST_VERSION_URL (JSON shape:
/// {"version": "...", "packageName"?: "...", "note"?: "..."}) and checks are skipped when it is unset.
/// </summary>
public static class VersionCheck
{
    public const string LatestVersionUrlEnv = "IRIS_LATEST_VERSION_URL";
    private const int DefaultTimeoutMs = 10000;

    public static string? LatestVersionUrl => Environment.GetEnvironmentVariable(LatestVersionUrlEnv) is { Length: > 0 } url ? url.Trim() : null;

    public static int? ComparePackageVersions(string left, string right) =>
        ParseVersion(left) is { } l && ParseVersion(right) is { } r ? Semver.SemVersion.ComparePrecedence(l, r) : null;

    private static Semver.SemVersion? ParseVersion(string version) =>
        Semver.SemVersion.TryParse(version.Trim(), Semver.SemVersionStyles.AllowLowerV, out var parsed) ? parsed : null;

    public static bool IsNewerPackageVersion(string candidate, string current) =>
        ComparePackageVersions(candidate, current) is { } comparison ? comparison > 0 : candidate.Trim() != current.Trim();

    public static async Task<LatestRelease?> GetLatestReleaseAsync(string currentVersion, int? timeoutMs = null, bool retry = false, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("IRIS_OFFLINE"))) return null;
        if (LatestVersionUrl is not { } url) return null;
        using var response = await ManagementHttp.FetchWithRetryAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", IrisUserAgent.Get());
            request.Headers.TryAddWithoutValidation("accept", "application/json");
            return request;
        }, ct, maxRetries: retry ? 2 : 0, timeoutMs: timeoutMs ?? DefaultTimeoutMs);
        if (!response.IsSuccessStatusCode) return null;
        if (JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) is not JsonObject data) return null;
        if (IrisJson.GetString(data["version"])?.Trim() is not { Length: > 0 } version) return null;
        var packageName = IrisJson.GetString(data["packageName"])?.Trim() is { Length: > 0 } name ? name : null;
        var note = IrisJson.GetString(data["note"])?.Trim() is { Length: > 0 } n ? n : null;
        return new LatestRelease(version, packageName, note);
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
