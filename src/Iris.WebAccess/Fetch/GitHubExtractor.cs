using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Iris.WebAccess.Fetch;

internal sealed record GitHubUrlInfo(string Owner, string Repo, string? Ref, bool RefIsFullSha, string? Path, string Type);

internal sealed record GitHubCloneConfig(bool Enabled, double MaxRepoSizeMB, double CloneTimeoutSeconds, string ClonePath)
{
    public static GitHubCloneConfig Load()
    {
        var clone = WebConfig.GetObject("githubClone");
        var maxSize = clone?["maxRepoSizeMB"] is System.Text.Json.Nodes.JsonValue s && s.TryGetValue<double>(out var mb) && double.IsFinite(mb) && mb > 0 ? mb : 350;
        var timeout = clone?["cloneTimeoutSeconds"] is System.Text.Json.Nodes.JsonValue t && t.TryGetValue<double>(out var seconds) && double.IsFinite(seconds) && seconds > 0 ? seconds : 30;
        var path = clone?["clonePath"] is System.Text.Json.Nodes.JsonValue p && p.TryGetValue<string>(out var configured) && configured.Trim().Length > 0
            ? ExpandPath(configured.Trim())
            : System.IO.Path.Combine(System.IO.Path.GetTempPath(), "iris-github-repos");
        return new GitHubCloneConfig(WebConfig.GetBool(clone, "enabled") ?? true, maxSize, timeout, path);
    }

    private static string ExpandPath(string value)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (value == "~") value = home;
        else if (value.StartsWith("~/", StringComparison.Ordinal) || value.StartsWith("~\\", StringComparison.Ordinal)) value = home + value[1..];
        return Regex.Replace(value, @"\$([A-Za-z_][A-Za-z0-9_]*)", m => Environment.GetEnvironmentVariable(m.Groups[1].Value) ?? m.Value);
    }
}

/// <summary>
/// GitHub repository URLs are shallow-cloned (gh when installed, otherwise git) so the agent can explore real files;
/// oversized repositories and commit URLs use an API view through gh instead.
/// </summary>
internal static partial class GitHubExtractor
{
    private const int MaxInlineFileChars = 100_000;
    private const int MaxTreeEntries = 200;

    private static readonly HashSet<string> BinaryExtensions =
    [
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp", ".svg", ".tiff", ".tif",
        ".mp3", ".mp4", ".avi", ".mov", ".mkv", ".flv", ".wmv", ".wav", ".ogg", ".webm", ".flac", ".aac",
        ".zip", ".tar", ".gz", ".bz2", ".xz", ".7z", ".rar", ".zst",
        ".exe", ".dll", ".so", ".dylib", ".bin", ".o", ".a", ".lib",
        ".woff", ".woff2", ".ttf", ".otf", ".eot",
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
        ".sqlite", ".db", ".sqlite3",
        ".pyc", ".pyo", ".class", ".jar", ".war",
        ".iso", ".img", ".dmg",
    ];

    private static readonly HashSet<string> NoiseDirs =
    [
        "node_modules", "vendor", ".next", "dist", "build", "__pycache__", ".venv", "venv", ".tox", ".mypy_cache", ".pytest_cache",
        "target", ".gradle", ".idea", ".vscode", "bin", "obj",
    ];

    private static readonly HashSet<string> NonCodeSegments =
    [
        "issues", "pull", "pulls", "discussions", "releases", "wiki", "actions", "settings", "security", "projects", "graphs",
        "compare", "commits", "tags", "branches", "stargazers", "watchers", "network", "forks", "milestone", "labels",
        "packages", "codespaces", "contribute", "community", "sponsors", "invitations", "notifications", "insights",
    ];

    private static readonly ConcurrentDictionary<string, Lazy<Task<string?>>> Clones = new();
    private static readonly Lock RuntimeGate = new();
    private static string? _runtimeRoot;
    private static bool? _ghAvailable;

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?$")]
    private static partial Regex OwnerPattern();

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex RepoPattern();

    [GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial Regex FullSha();

    public static GitHubUrlInfo? ParseUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var host = uri.Host.ToLowerInvariant();
        if (host is not ("github.com" or "www.github.com")) return null;
        var segments = new List<string>();
        foreach (var segment in uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                segments.Add(Uri.UnescapeDataString(segment));
            }
            catch (UriFormatException)
            {
                return null;
            }
        }
        if (segments.Count < 2) return null;
        var owner = segments[0];
        var repo = segments[1].EndsWith(".git", StringComparison.Ordinal) ? segments[1][..^4] : segments[1];
        if (!OwnerPattern().IsMatch(owner) || owner.Contains("--", StringComparison.Ordinal)) return null;
        if (!RepoPattern().IsMatch(repo) || repo is "." or "..") return null;
        if (segments.Count > 2 && NonCodeSegments.Contains(segments[2].ToLowerInvariant())) return null;
        if (segments.Count == 2) return new GitHubUrlInfo(owner, repo, null, false, null, "root");
        var action = segments[2];
        if (action is not ("blob" or "tree") || segments.Count < 4) return null;
        var reference = segments[3];
        if (reference.Length == 0 || reference.Length > 1024 || reference.Any(c => c < 0x20 || c == 0x7f)) return null;
        return new GitHubUrlInfo(owner, repo, reference, FullSha().IsMatch(reference), string.Join("/", segments.Skip(4)), action);
    }

    public static async Task<ExtractedContent?> ExtractAsync(string url, bool forceClone, CancellationToken cancellationToken)
    {
        if (ParseUrl(url) is not { } info) return null;
        var config = GitHubCloneConfig.Load();
        if (!config.Enabled) return null;
        var title = string.IsNullOrEmpty(info.Path) ? $"{info.Owner}/{info.Repo}" : $"{info.Owner}/{info.Repo} - {info.Path}";

        if (info.RefIsFullSha) return await FetchViaApiAsync(url, info, "Note: Commit SHA URLs use the GitHub API instead of cloning.", cancellationToken);

        var key = $"{info.Owner}/{info.Repo}@{info.Ref ?? ""}";
        if (!forceClone && !Clones.ContainsKey(key) && await RepoSizeKBAsync(info, cancellationToken) is { } sizeKB && sizeKB / 1024 > config.MaxRepoSizeMB)
        {
            var note = $"Note: Repository is {Math.Round(sizeKB / 1024)}MB (threshold: {config.MaxRepoSizeMB}MB). Showing API-fetched content instead of full clone. " +
                "Ask the user if they'd like to clone the full repo -- if yes, call fetch_content again with the same URL and add forceClone: true to the params.";
            return await FetchViaApiAsync(url, info, note, cancellationToken);
        }

        var clone = Clones.GetOrAdd(key, _ => new Lazy<Task<string?>>(() => CloneAsync(info, config)));
        var localPath = await clone.Value.WaitAsync(cancellationToken);
        if (localPath is null)
        {
            Clones.TryRemove(key, out _);
            return await FetchViaApiAsync(url, info, null, cancellationToken);
        }
        return new ExtractedContent { Url = url, Title = title, Content = GenerateContent(localPath, info) };
    }

    /// <summary>Delete clones made by this process (on session change and shutdown).</summary>
    public static void ClearClones()
    {
        Clones.Clear();
        lock (RuntimeGate)
        {
            if (_runtimeRoot is null) return;
            TryDeleteDirectory(_runtimeRoot);
            _runtimeRoot = null;
        }
    }

    private static string? RuntimeRoot(GitHubCloneConfig config)
    {
        lock (RuntimeGate)
        {
            if (_runtimeRoot is not null && Directory.Exists(_runtimeRoot)) return _runtimeRoot;
            try
            {
                Directory.CreateDirectory(config.ClonePath);
                var root = Path.Combine(Path.GetFullPath(config.ClonePath), $"runtime-{Environment.ProcessId}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}");
                Directory.CreateDirectory(root);
                return _runtimeRoot = root;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    private static async Task<string?> CloneAsync(GitHubUrlInfo info, GitHubCloneConfig config)
    {
        if (RuntimeRoot(config) is not { } root) return null;
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { info.Owner, info.Repo, info.Ref }))));
        var localPath = Path.Combine(root, digest);
        TryDeleteDirectory(localPath);

        // Clones are shared between calls, so they are not tied to one call's cancellation; only the timeout stops them.
        var timeout = TimeSpan.FromSeconds(config.CloneTimeoutSeconds);
        List<string> args;
        string command;
        if (await IsGhAvailableAsync())
        {
            command = "gh";
            args = ["repo", "clone", $"{info.Owner}/{info.Repo}", localPath, "--", "--depth", "1", "--single-branch"];
        }
        else
        {
            command = "git";
            args = ["clone", "--depth", "1", "--single-branch"];
            if (info.Ref is not null) args.AddRange(["--branch", info.Ref]);
            args.AddRange([$"https://github.com/{info.Owner}/{info.Repo}.git", localPath]);
        }
        if (command == "gh" && info.Ref is not null) args.AddRange(["--branch", info.Ref]);

        var result = await RunAsync(command, args, timeout, CancellationToken.None);
        if (result?.ExitCode == 0 && Directory.Exists(localPath)) return localPath;
        TryDeleteDirectory(localPath);
        return null;
    }

    private static string GenerateContent(string localPath, GitHubUrlInfo info)
    {
        const string exploreHint = "Use `read` and `bash` tools at the path above to explore further.";
        var lines = new List<string> { $"Repository cloned to: {localPath}", "" };

        void AddRootStructure()
        {
            lines.Add("## Structure");
            lines.Add(BuildTree(localPath));
        }

        if (info.Type == "root")
        {
            AddRootStructure();
            lines.Add("");
            if (ReadReadme(localPath) is { } readme) lines.AddRange(["## README.md", readme, ""]);
            lines.Add(exploreHint);
            return string.Join("\n", lines);
        }

        var relative = info.Path ?? "";
        var target = ResolveWithinRepo(localPath, relative);
        if (target is null || !Path.Exists(target))
        {
            lines.Add($"Path `{relative}` not found in clone. Showing repository root instead.");
            lines.Add("");
            AddRootStructure();
            lines.Add("");
            lines.Add(exploreHint);
            return string.Join("\n", lines);
        }

        if (Directory.Exists(target))
        {
            lines.Add($"## {(relative.Length > 0 ? relative : "/")}");
            lines.Add(BuildDirListing(localPath, relative));
        }
        else if (IsBinaryFile(target))
        {
            lines.Add($"## {relative}");
            lines.Add($"Binary file ({Path.GetExtension(relative).TrimStart('.')}, {FormatSize(new FileInfo(target).Length)}). Use `read` or `bash` tools at the path above to inspect.");
            return string.Join("\n", lines);
        }
        else
        {
            string content;
            try
            {
                content = File.ReadAllText(target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lines.Add($"Could not read `{relative}` as UTF-8 text.");
                lines.Add("");
                lines.Add(exploreHint);
                return string.Join("\n", lines);
            }
            lines.Add($"## {relative}");
            if (content.Length > MaxInlineFileChars)
            {
                lines.Add(content[..MaxInlineFileChars]);
                lines.Add("");
                lines.Add($"[File truncated at 100K chars. Full file: {target}]");
            }
            else
            {
                lines.Add(content);
            }
        }
        lines.Add("");
        lines.Add(exploreHint);
        return string.Join("\n", lines);
    }

    private static string? ResolveWithinRepo(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root);
        var candidate = Path.GetFullPath(Path.Combine(fullRoot, relative));
        var prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        if (candidate != fullRoot && !candidate.StartsWith(prefix, StringComparison.Ordinal)) return null;
        if (!Path.Exists(candidate)) return candidate;
        // Symlinks inside a cloned repository must not point outside it.
        var info = new FileInfo(candidate);
        if (info.LinkTarget is not null)
        {
            var resolved = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
            if (resolved is null || resolved != fullRoot && !resolved.StartsWith(prefix, StringComparison.Ordinal)) return null;
        }
        return candidate;
    }

    private static string BuildTree(string root)
    {
        var entries = new List<string>();

        void Walk(string relative)
        {
            if (entries.Count >= MaxTreeEntries) return;
            var directory = relative.Length == 0 ? root : Path.Combine(root, relative);
            IEnumerable<string> items;
            try
            {
                items = Directory.EnumerateFileSystemEntries(directory).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }
            foreach (var item in items)
            {
                if (entries.Count >= MaxTreeEntries) return;
                if (item == ".git") continue;
                var rel = relative.Length == 0 ? item : $"{relative}/{item}";
                if (ResolveWithinRepo(root, rel) is not { } safe)
                {
                    entries.Add($"{rel}  [outside repo skipped]");
                    continue;
                }
                if (Directory.Exists(safe))
                {
                    if (NoiseDirs.Contains(item))
                    {
                        entries.Add($"{rel}/  [skipped]");
                        continue;
                    }
                    entries.Add($"{rel}/");
                    Walk(rel);
                }
                else
                {
                    entries.Add(rel);
                }
            }
        }

        Walk("");
        if (entries.Count >= MaxTreeEntries) entries.Add($"... (truncated at {MaxTreeEntries} entries)");
        return string.Join("\n", entries);
    }

    private static string BuildDirListing(string root, string relative)
    {
        if (ResolveWithinRepo(root, relative) is not { } target) return "(path escapes repository root)";
        List<string> items;
        try
        {
            items = Directory.EnumerateFileSystemEntries(target).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "(directory not readable)";
        }
        var lines = new List<string>();
        foreach (var item in items.Where(i => i != ".git"))
        {
            var rel = relative.Length == 0 ? item : $"{relative}/{item}";
            if (ResolveWithinRepo(root, rel) is not { } safe)
            {
                lines.Add($"  {item}  (outside repo)");
                continue;
            }
            lines.Add(Directory.Exists(safe) ? $"  {item}/" : $"  {item}  ({FormatSize(new FileInfo(safe).Length)})");
        }
        return string.Join("\n", lines);
    }

    private static string? ReadReadme(string localPath)
    {
        foreach (var name in new[] { "README.md", "readme.md", "README", "README.txt", "README.rst" })
        {
            var path = Path.Combine(localPath, name);
            if (!File.Exists(path)) continue;
            try
            {
                var content = File.ReadAllText(path);
                return content.Length > 8192 ? $"{content[..8192]}\n\n[README truncated at 8K chars]" : content;
            }
            catch (IOException)
            {
                // Try the next candidate.
            }
        }
        return null;
    }

    private static bool IsBinaryFile(string path)
    {
        if (BinaryExtensions.Contains(Path.GetExtension(path).ToLowerInvariant())) return true;
        try
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[512];
            var read = stream.Read(buffer, 0, buffer.Length);
            return buffer.AsSpan(0, read).Contains((byte)0);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static string FormatSize(long bytes) =>
        bytes < 1024 ? $"{bytes} B" : bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.0} KB" : $"{bytes / (1024.0 * 1024):0.0} MB";

    // ----- gh API view -----

    private static async Task<bool> IsGhAvailableAsync()
    {
        if (_ghAvailable is { } known) return known;
        var result = await RunAsync("gh", ["--version"], TimeSpan.FromSeconds(5), CancellationToken.None);
        return (_ghAvailable = result?.ExitCode == 0).Value;
    }

    private static async Task<double?> RepoSizeKBAsync(GitHubUrlInfo info, CancellationToken cancellationToken)
    {
        if (!await IsGhAvailableAsync()) return null;
        var result = await RunAsync("gh", ["api", $"repos/{info.Owner}/{info.Repo}", "--jq", ".size"], TimeSpan.FromSeconds(10), cancellationToken);
        return result?.ExitCode == 0 && double.TryParse(result.Stdout.Trim(), System.Globalization.CultureInfo.InvariantCulture, out var kb) ? kb : null;
    }

    private static async Task<ExtractedContent?> FetchViaApiAsync(string url, GitHubUrlInfo info, string? note, CancellationToken cancellationToken)
    {
        if (!await IsGhAvailableAsync()) return null;
        var reference = info.Ref;
        if (reference is null)
        {
            var branch = await RunAsync("gh", ["api", $"repos/{info.Owner}/{info.Repo}", "--jq", ".default_branch"], TimeSpan.FromSeconds(10), cancellationToken);
            reference = branch?.ExitCode == 0 ? branch.Stdout.Trim() : null;
            if (string.IsNullOrEmpty(reference)) return null;
        }
        var lines = new List<string>();
        if (note is not null) lines.AddRange([note, ""]);
        var title = string.IsNullOrEmpty(info.Path) ? $"{info.Owner}/{info.Repo}" : $"{info.Owner}/{info.Repo} - {info.Path}";

        if (info.Type == "blob" && !string.IsNullOrEmpty(info.Path))
        {
            var file = await RunAsync("gh", ["api", $"repos/{info.Owner}/{info.Repo}/contents/{info.Path}?ref={Uri.EscapeDataString(reference)}", "--jq", ".content"], TimeSpan.FromSeconds(10), cancellationToken);
            if (file?.ExitCode != 0 || DecodeBase64(file.Stdout) is not { } content) return null;
            lines.Add($"## {info.Path}");
            lines.Add(content.Length > MaxInlineFileChars ? $"{content[..MaxInlineFileChars]}\n\n[File truncated at 100K chars]" : content);
            return new ExtractedContent { Url = url, Title = title, Content = string.Join("\n", lines) };
        }

        var treeTask = RunAsync("gh", ["api", $"repos/{info.Owner}/{info.Repo}/git/trees/{Uri.EscapeDataString(reference)}?recursive=1", "--jq", ".tree[].path"], TimeSpan.FromSeconds(15), cancellationToken);
        var readmeTask = RunAsync("gh", ["api", $"repos/{info.Owner}/{info.Repo}/readme?ref={Uri.EscapeDataString(reference)}", "--jq", ".content"], TimeSpan.FromSeconds(10), cancellationToken);
        var (tree, readme) = (await treeTask, await readmeTask);
        var paths = tree?.ExitCode == 0 ? tree.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [];
        var readmeText = readme?.ExitCode == 0 ? DecodeBase64(readme.Stdout) : null;
        if (paths.Length == 0 && readmeText is null) return null;
        if (paths.Length > 0)
        {
            lines.Add("## Structure");
            lines.Add(string.Join("\n", paths.Take(MaxTreeEntries)) + (paths.Length > MaxTreeEntries ? $"\n... ({paths.Length} total entries)" : ""));
            lines.Add("");
        }
        if (readmeText is not null)
        {
            lines.Add("## README.md");
            lines.Add(readmeText.Length > 8192 ? $"{readmeText[..8192]}\n\n[README truncated at 8K chars]" : readmeText);
            lines.Add("");
        }
        lines.Add("This is an API-only view. Clone the repo or use `read`/`bash` for deeper exploration.");
        return new ExtractedContent { Url = url, Title = title, Content = string.Join("\n", lines) };
    }

    private static string? DecodeBase64(string text)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(text.Trim().Replace("\\n", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal)));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private sealed record ProcessResult(int ExitCode, string Stdout);

    private static async Task<ProcessResult?> RunAsync(string command, IEnumerable<string> args, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(command)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            Environment = { ["GIT_TERMINAL_PROMPT"] = "0", ["GCM_INTERACTIVE"] = "Never", ["GH_PROMPT_DISABLED"] = "1" },
        };
        foreach (var arg in args) startInfo.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start()) return null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
        process.StandardInput.Close();
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }
            if (cancellationToken.IsCancellationRequested) throw;
            return null;
        }
        await stderr;
        return new ProcessResult(process.ExitCode, await stdout);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            // git marks pack files read-only, which blocks deletion on Windows.
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the temp directory cleanup.
        }
    }
}
