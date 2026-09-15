using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.Ai;
using PiSharp.Ai.Json;
using PiSharp.CodingAgent.Core;

namespace PiSharp.CodingAgent.Utils;

public sealed record ChangelogEntry(int Major, int Minor, int Patch, string Content);

/// <summary>CHANGELOG.md parsing. Port of utils/changelog.ts.</summary>
public static partial class Changelog
{
    private const string GithubRepo = "earendil-works/pi";
    private const string LinkBasePath = "packages/coding-agent";

    [GeneratedRegex(@"^https://github\.com/(?:badlogic|earendil-works)/pi-mono(?=/|$)")]
    private static partial Regex LegacyRepo();

    [GeneratedRegex("^[a-z][a-z0-9+.-]*:", RegexOptions.IgnoreCase)]
    private static partial Regex UrlScheme();

    [GeneratedRegex(@"(!?\[[^\]\n]+\]\()([^\s)]+)((?:\s+[^)]*)?\))")]
    private static partial Regex InlineMarkdownLink();

    [GeneratedRegex(@"##\s+\[?(\d+)\.(\d+)\.(\d+)\]?")]
    private static partial Regex VersionHeader();

    public static List<ChangelogEntry> Parse(string changelogPath)
    {
        if (!File.Exists(changelogPath)) return [];
        try
        {
            var entries = new List<ChangelogEntry>();
            var currentLines = new List<string>();
            (int, int, int)? current = null;
            foreach (var line in File.ReadAllText(changelogPath).Split('\n'))
            {
                if (line.StartsWith("## ", StringComparison.Ordinal))
                {
                    if (current is { } c && currentLines.Count > 0) entries.Add(new ChangelogEntry(c.Item1, c.Item2, c.Item3, string.Join("\n", currentLines).Trim()));
                    var m = VersionHeader().Match(line);
                    if (m.Success)
                    {
                        current = (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
                        currentLines = [line];
                    }
                    else
                    {
                        current = null;
                        currentLines = [];
                    }
                }
                else if (current is not null)
                {
                    currentLines.Add(line);
                }
            }
            if (current is { } last && currentLines.Count > 0) entries.Add(new ChangelogEntry(last.Item1, last.Item2, last.Item3, string.Join("\n", currentLines).Trim()));
            return entries;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: Could not parse changelog: {ex.Message}");
            return [];
        }
    }

    private static int Compare(ChangelogEntry a, ChangelogEntry b) =>
        a.Major != b.Major ? a.Major - b.Major : a.Minor != b.Minor ? a.Minor - b.Minor : a.Patch - b.Patch;

    public static List<ChangelogEntry> GetNewEntries(List<ChangelogEntry> entries, string lastVersion)
    {
        var parts = lastVersion.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
        var last = new ChangelogEntry(parts.ElementAtOrDefault(0), parts.ElementAtOrDefault(1), parts.ElementAtOrDefault(2), "");
        return entries.Where(e => Compare(e, last) > 0).ToList();
    }

    public static string NormalizeLinks(string markdown, ChangelogEntry entry)
    {
        var tag = $"v{entry.Major}.{entry.Minor}.{entry.Patch}";
        return InlineMarkdownLink().Replace(markdown, m => m.Groups[1].Value + NormalizeTarget(m.Groups[2].Value, tag) + m.Groups[3].Value);
    }

    private static string NormalizeTarget(string target, string tag)
    {
        var repoUrl = $"https://github.com/{GithubRepo}";
        var canonical = LegacyRepo().Replace(target, repoUrl);
        foreach (var route in new[] { "blob", "tree" })
        {
            foreach (var branch in new[] { "main", "master" })
            {
                var prefix = $"{repoUrl}/{route}/{branch}/";
                if (canonical.StartsWith(prefix, StringComparison.Ordinal)) canonical = $"{repoUrl}/{route}/{tag}/{canonical[prefix.Length..]}";
            }
        }
        if (canonical.StartsWith('#') || canonical.StartsWith("//", StringComparison.Ordinal) || UrlScheme().IsMatch(canonical)) return canonical;

        var hash = canonical.IndexOf('#');
        var beforeHash = hash == -1 ? canonical : canonical[..hash];
        var fragment = hash == -1 ? "" : canonical[hash..];
        var q = beforeHash.IndexOf('?');
        var pathPart = q == -1 ? beforeHash : beforeHash[..q];
        var query = q == -1 ? "" : beforeHash[q..];
        if (pathPart.Length == 0) return canonical;

        var normalized = pathPart.Replace('\\', '/');
        var joined = PosixNormalize(normalized.StartsWith('/') ? normalized.TrimStart('/') : $"{LinkBasePath}/{normalized}");
        if (joined is "." or ".." || joined.StartsWith("../", StringComparison.Ordinal)) return canonical;
        var baseName = joined[(joined.LastIndexOf('/') + 1)..];
        var routeName = pathPart.EndsWith('/') || !baseName.Contains('.') ? "tree" : "blob";
        return $"https://github.com/{GithubRepo}/{routeName}/{tag}/{Uri.EscapeUriString(joined)}{query}{fragment}";
    }

    private static string PosixNormalize(string path)
    {
        var trailing = path.EndsWith('/');
        var segments = new List<string>();
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment == ".") continue;
            if (segment == ".." && segments.Count > 0 && segments[^1] != "..") segments.RemoveAt(segments.Count - 1);
            else segments.Add(segment);
        }
        var result = string.Join("/", segments);
        if (result.Length == 0) return ".";
        return trailing ? result + "/" : result;
    }
}

/// <summary>System clipboard access. Port of utils/clipboard.ts (native module replaced with platform commands).</summary>
public static class Clipboard
{
    private static async Task<byte[]?> RunAsync(string command, IEnumerable<string> args, byte[]? input = null, int timeoutMs = 5000)
    {
        try
        {
            var psi = new ProcessStartInfo(command) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = input is not null, CreateNoWindow = true };
            foreach (var arg in args) psi.ArgumentList.Add(arg);
            using var process = Process.Start(psi);
            if (process is null) return null;
            if (input is not null)
            {
                await process.StandardInput.BaseStream.WriteAsync(input);
                process.StandardInput.Close();
            }
            using var output = new MemoryStream();
            var copy = process.StandardOutput.BaseStream.CopyToAsync(output);
            _ = process.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(timeoutMs);
            try
            {
                await process.WaitForExitAsync(cts.Token);
                await copy;
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(true);
                }
                catch
                {
                    // ignored
                }
                return null;
            }
            return process.ExitCode == 0 ? output.ToArray() : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsRemote => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SSH_CONNECTION")) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SSH_CLIENT")) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MOSH_CONNECTION"));

    public static async Task<string?> ReadTextAsync()
    {
        IEnumerable<(string, string[])> commands;
        if (OperatingSystem.IsWindows()) commands = [("powershell", ["-NoProfile", "-NonInteractive", "-Command", "[Console]::OutputEncoding=[Text.Encoding]::UTF8; Get-Clipboard -Raw"])];
        else if (OperatingSystem.IsMacOS()) commands = [("pbpaste", [])];
        else
        {
            var list = new List<(string, string[])>();
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TERMUX_VERSION"))) list.Add(("termux-clipboard-get", []));
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))) list.Add(("wl-paste", ["--no-newline", "--type", "text"]));
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
            {
                list.Add(("xclip", ["-selection", "clipboard", "-out"]));
                list.Add(("xsel", ["--clipboard", "--output"]));
            }
            commands = list;
        }
        foreach (var (command, args) in commands)
        {
            if (await RunAsync(command, args) is { } bytes)
            {
                var text = Encoding.UTF8.GetString(bytes);
                if (OperatingSystem.IsWindows() && text.EndsWith("\r\n", StringComparison.Ordinal)) text = text[..^2];
                return text.Length > 0 ? text : null;
            }
        }
        return null;
    }

    public static async Task CopyAsync(string text)
    {
        var copied = false;
        IEnumerable<(string, string[], byte[])> commands;
        if (OperatingSystem.IsWindows())
        {
            commands = [("powershell", ["-NoProfile", "-NonInteractive", "-Command", "$input_text = [Console]::In.ReadToEnd(); Set-Clipboard -Value $input_text"], new UTF8Encoding(false).GetBytes(text))];
        }
        else if (OperatingSystem.IsMacOS())
        {
            commands = [("pbcopy", [], Encoding.UTF8.GetBytes(text))];
        }
        else
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            var list = new List<(string, string[], byte[])>();
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TERMUX_VERSION"))) list.Add(("termux-clipboard-set", [], bytes));
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))) list.Add(("wl-copy", [], bytes));
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
            {
                list.Add(("xclip", ["-selection", "clipboard"], bytes));
                list.Add(("xsel", ["--clipboard", "--input"], bytes));
            }
            commands = list;
        }
        foreach (var (command, args, input) in commands)
        {
            if (await RunAsync(command, args, input) is not null)
            {
                copied = true;
                break;
            }
        }
        if (IsRemote || !copied)
        {
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
            if (encoded.Length <= 100_000)
            {
                Console.Out.Write($"\e]52;c;{encoded}\a");
                Console.Out.Flush();
                copied = true;
            }
        }
        if (!copied) throw new InvalidOperationException("Failed to copy to clipboard");
    }

    /// <summary>Read an image from the clipboard as PNG bytes (Windows/macOS/Linux command-line tools).</summary>
    public static async Task<(byte[] Bytes, string MimeType)?> ReadImageAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            var tempFile = Path.Combine(Path.GetTempPath(), $"pi-clip-{Guid.NewGuid():N}.png");
            const string script = "Add-Type -AssemblyName System.Windows.Forms; Add-Type -AssemblyName System.Drawing; $img = [System.Windows.Forms.Clipboard]::GetImage(); if ($img -eq $null) { exit 1 }; $img.Save($env:PI_CLIP_OUT, [System.Drawing.Imaging.ImageFormat]::Png)";
            try
            {
                var psi = new ProcessStartInfo("powershell") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
                foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-STA", "-Command", script }) psi.ArgumentList.Add(arg);
                psi.Environment["PI_CLIP_OUT"] = tempFile;
                using var process = Process.Start(psi);
                if (process is null) return null;
                await process.WaitForExitAsync();
                if (process.ExitCode != 0 || !File.Exists(tempFile)) return null;
                return (await File.ReadAllBytesAsync(tempFile), "image/png");
            }
            catch
            {
                return null;
            }
            finally
            {
                try
                {
                    File.Delete(tempFile);
                }
                catch
                {
                    // ignored
                }
            }
        }
        if (OperatingSystem.IsLinux())
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")) && await RunAsync("wl-paste", ["--type", "image/png"]) is { Length: > 0 } wl) return (wl, "image/png");
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) && await RunAsync("xclip", ["-selection", "clipboard", "-t", "image/png", "-o"]) is { Length: > 0 } x) return (x, "image/png");
        }
        return null;
    }

    public static string? ExtensionForImageMimeType(string mimeType) => mimeType switch
    {
        "image/png" => "png",
        "image/jpeg" => "jpg",
        "image/gif" => "gif",
        "image/webp" => "webp",
        _ => null,
    };
}

public sealed record CacheMiss(long MissedTokens, double MissedCost, long IdleMs, bool ModelChanged);

public sealed record CacheWasteTotals(long MissedTokens, double MissedCost, int MissCount);

/// <summary>Prompt cache miss detection. Port of core/cache-stats.ts.</summary>
public static class CacheStats
{
    public const long CacheTtlMs = 5 * 60 * 1000;
    private const long NoiseFloorTokens = 1024;

    private sealed record PreviousRequest(long PromptTokens, string ModelKey, long Timestamp, bool ReportedCache);

    private static CacheMiss? DetectMiss(PreviousRequest? prev, AssistantMessage message, ModelRuntime models)
    {
        var usage = message.Usage;
        var promptTokens = usage.Input + usage.CacheRead + usage.CacheWrite;
        if (prev is null || promptTokens <= 0 || (usage.CacheRead + usage.CacheWrite == 0 && !prev.ReportedCache)) return null;
        var missed = Math.Min(prev.PromptTokens, promptTokens) - usage.CacheRead;
        if (missed <= NoiseFloorTokens) return null;
        var paidTokens = usage.Input + usage.CacheWrite;
        var paidPerToken = paidTokens > 0 ? (usage.Cost.Input + usage.Cost.CacheWrite) / paidTokens : 0;
        var readPerToken = usage.CacheRead > 0 ? usage.Cost.CacheRead / usage.CacheRead : (models.GetModel(message.Provider, message.Model)?.Cost.CacheRead ?? 0) / 1_000_000;
        return new CacheMiss(missed, missed * Math.Max(0, paidPerToken - readPerToken), Math.Max(0, message.Timestamp - prev.Timestamp), $"{message.Provider}/{message.Model}" != prev.ModelKey);
    }

    private static PreviousRequest? AsPrevious(AssistantMessage message, bool reportedCache)
    {
        var usage = message.Usage;
        var promptTokens = usage.Input + usage.CacheRead + usage.CacheWrite;
        return promptTokens <= 0 ? null : new PreviousRequest(promptTokens, $"{message.Provider}/{message.Model}", message.Timestamp, reportedCache || usage.CacheRead + usage.CacheWrite > 0);
    }

    private static (PreviousRequest? Prev, CacheWasteTotals Totals, Dictionary<AssistantMessage, CacheMiss> Misses) Scan(IEnumerable<SessionEntry> entries, ModelRuntime models)
    {
        PreviousRequest? prev = null;
        long missedTokens = 0;
        double missedCost = 0;
        var count = 0;
        var misses = new Dictionary<AssistantMessage, CacheMiss>(ReferenceEqualityComparer.Instance);
        foreach (var entry in entries)
        {
            if (entry is CompactionEntry or BranchSummaryEntry)
            {
                prev = null;
                continue;
            }
            if (entry is SessionMessageEntry { Message: AssistantMessage assistant })
            {
                if (DetectMiss(prev, assistant, models) is { } miss)
                {
                    missedTokens += miss.MissedTokens;
                    missedCost += miss.MissedCost;
                    count++;
                    misses[assistant] = miss;
                }
                prev = AsPrevious(assistant, prev?.ReportedCache ?? false) ?? prev;
            }
        }
        return (prev, new CacheWasteTotals(missedTokens, missedCost, count), misses);
    }

    public static CacheWasteTotals ComputeCacheWaste(IEnumerable<SessionEntry> entries, ModelRuntime models) => Scan(entries, models).Totals;

    public static Dictionary<AssistantMessage, CacheMiss> CollectCacheMisses(IEnumerable<SessionEntry> entries, ModelRuntime models) => Scan(entries, models).Misses;

    public static CacheMiss? DetectCacheMiss(IEnumerable<SessionEntry> entries, AssistantMessage message, ModelRuntime models) => DetectMiss(Scan(entries, models).Prev, message, models);
}

/// <summary>Session JSONL export. Port of core/session-export.ts.</summary>
public static class SessionExport
{
    public static string ExportToJsonl(SessionManager sessionManager, string? outputPath = null)
    {
        var fileName = outputPath ?? $"session-{DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture).Replace(':', '-').Replace('.', '-')}.jsonl";
        var filePath = PathUtils.ResolvePath(fileName, Environment.CurrentDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        var header = new SessionHeader
        {
            Version = SessionManager.CurrentSessionVersion,
            Id = sessionManager.SessionId,
            Timestamp = SessionManager.NowIso(),
            Cwd = sessionManager.Cwd,
        };
        var lines = new List<string> { SessionManager.SerializeEntry(header) };
        string? parentId = null;
        foreach (var entry in sessionManager.GetBranch())
        {
            var node = JsonNode.Parse(SessionManager.SerializeEntry(entry))!.AsObject();
            var ordered = new JsonObject();
            foreach (var (key, value) in node)
            {
                ordered[key] = key == "parentId" ? parentId is null ? null : JsonValue.Create(parentId) : value?.DeepClone();
            }
            if (!ordered.ContainsKey("parentId")) ordered["parentId"] = parentId;
            lines.Add(ordered.ToJsonString(PiJson.Options));
            parentId = entry.Id;
        }
        File.WriteAllText(filePath, string.Join("\n", lines) + "\n");
        return filePath;
    }
}
