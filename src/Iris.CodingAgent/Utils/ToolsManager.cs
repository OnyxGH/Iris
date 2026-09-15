using System.Diagnostics;
using System.Runtime.InteropServices;
using Iris.CodingAgent.Config;

namespace Iris.CodingAgent.Utils;

public sealed record ToolStatus(string Type, string Message);

/// <summary>Locates or downloads fd and ripgrep.</summary>
public static class ToolsManager
{
    private const int NetworkTimeoutMs = 10_000;
    private const int DownloadTimeoutMs = 120_000;

    private sealed record ExternalTool(string Name, string Repo, string BinaryName, string[] SystemBinaryNames, string TagPrefix, Func<string, string, string, string?> GetAssetName);

    private static readonly Dictionary<string, ExternalTool> Tools = new()
    {
        ["fd"] = new("fd", "sharkdp/fd", "fd", ["fd", "fdfind"], "v", (version, plat, arch) =>
        {
            var archStr = arch == "arm64" ? "aarch64" : "x86_64";
            return plat switch
            {
                "darwin" => $"fd-v{version}-{archStr}-apple-darwin.tar.gz",
                "linux" => $"fd-v{version}-{archStr}-unknown-linux-musl.tar.gz",
                "win32" => $"fd-v{version}-{archStr}-pc-windows-msvc.zip",
                _ => null,
            };
        }),
        ["rg"] = new("ripgrep", "BurntSushi/ripgrep", "rg", ["rg"], "", (version, plat, arch) =>
        {
            var archStr = arch == "arm64" ? "aarch64" : "x86_64";
            return plat switch
            {
                "darwin" => $"ripgrep-{version}-{archStr}-apple-darwin.tar.gz",
                "linux" => $"ripgrep-{version}-{archStr}-unknown-linux-musl.tar.gz",
                "win32" => $"ripgrep-{version}-{archStr}-pc-windows-msvc.zip",
                _ => null,
            };
        }),
    };

    private static readonly HttpClient NoRedirectClient = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = true, Proxy = Iris.Ai.Utils.EnvHttpProxy.Instance });

    private static string Platform =>
        OperatingSystem.IsWindows() ? "win32" : OperatingSystem.IsMacOS() ? "darwin" : OperatingSystem.IsAndroid() ? "android" : "linux";

    private static string Arch => RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";

    private static bool IsOfflineModeEnabled()
    {
        var value = Environment.GetEnvironmentVariable("IRIS_OFFLINE");
        if (string.IsNullOrEmpty(value)) return false;
        return value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static bool CommandExists(string cmd)
    {
        try
        {
            var psi = new ProcessStartInfo(cmd, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null) return false;
            process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Path to a tool in the managed bin dir, or its command name when it is on PATH.</summary>
    public static string? GetToolPath(string tool)
    {
        if (!Tools.TryGetValue(tool, out var config)) return null;
        var localPath = Path.Combine(AppConfig.BinDir, config.BinaryName + (OperatingSystem.IsWindows() ? ".exe" : ""));
        if (File.Exists(localPath)) return localPath;
        foreach (var name in config.SystemBinaryNames)
        {
            if (CommandExists(name)) return name;
        }
        return null;
    }

    public static async Task<string> GetLatestVersionAsync(string repo, CancellationToken cancellationToken = default)
    {
        using var response = await ManagementHttp.FetchWithRetryAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"https://github.com/{repo}/releases/latest");
            request.Headers.TryAddWithoutValidation("User-Agent", $"{AppConfig.AppName}-coding-agent");
            return request;
        }, cancellationToken, timeoutMs: NetworkTimeoutMs, client: NoRedirectClient);

        var status = (int)response.StatusCode;
        var location = status is >= 300 and < 400 ? response.Headers.Location?.ToString() : null;
        if (location is null) throw new InvalidOperationException($"Failed to resolve latest {repo} release: HTTP {status} without redirect");

        var uri = new Uri(new Uri("https://github.com"), location);
        var tag = uri.AbsolutePath.Split('/').LastOrDefault();
        if (string.IsNullOrEmpty(tag) || !location.Contains("/releases/tag/"))
        {
            throw new InvalidOperationException($"Failed to resolve latest {repo} release: unexpected redirect to {location}");
        }
        var decoded = Uri.UnescapeDataString(tag);
        return decoded.StartsWith('v') ? decoded[1..] : decoded;
    }

    private static async Task DownloadFileAsync(string url, string dest, CancellationToken cancellationToken)
    {
        using var response = await ManagementHttp.FetchWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken, timeoutMs: DownloadTimeoutMs);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Download failed with HTTP {(int)response.StatusCode}: {url}");
        await using var fileStream = File.Create(dest);
        await response.Content.CopyToAsync(fileStream, cancellationToken);
    }

    private static string? RunExtractionCommand(string command, IEnumerable<string> args)
    {
        try
        {
            var psi = new ProcessStartInfo(command)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in args) psi.ArgumentList.Add(arg);
            using var process = Process.Start(psi)!;
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEnd();
            var stdout = stdoutTask.GetAwaiter().GetResult();
            process.WaitForExit();
            if (process.ExitCode == 0) return null;
            var detail = stderr.Trim().Length > 0 ? stderr.Trim() : stdout.Trim().Length > 0 ? stdout.Trim() : $"exit status {process.ExitCode}";
            return $"{command}: {detail}";
        }
        catch (Exception ex)
        {
            return $"{command}: {ex.Message}";
        }
    }

    private static void ExtractZipArchive(string archivePath, string extractDir, string assetName)
    {
        var failures = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            try
            {
                System.IO.Compression.ZipFile.ExtractToDirectory(archivePath, extractDir, overwriteFiles: true);
                return;
            }
            catch (Exception ex)
            {
                failures.Add($"zip: {ex.Message}");
            }
        }
        else
        {
            var unzipFailure = RunExtractionCommand("unzip", ["-q", archivePath, "-d", extractDir]);
            if (unzipFailure is null) return;
            failures.Add(unzipFailure);
            var tarFailure = RunExtractionCommand("tar", ["xf", archivePath, "-C", extractDir]);
            if (tarFailure is null) return;
            failures.Add(tarFailure);
        }
        throw new InvalidOperationException($"Failed to extract {assetName}: {string.Join("; ", failures)}");
    }

    private static async Task<string> DownloadToolAsync(string tool, CancellationToken cancellationToken)
    {
        var config = Tools[tool];
        var plat = Platform;
        var arch = Arch;
        var version = tool == "fd" && plat == "darwin" && arch == "x64" ? "10.3.0" : await GetLatestVersionAsync(config.Repo, cancellationToken);
        var assetName = config.GetAssetName(version, plat, arch) ?? throw new InvalidOperationException($"Unsupported platform: {plat}/{arch}");

        var toolsDir = AppConfig.BinDir;
        Directory.CreateDirectory(toolsDir);
        var downloadUrl = $"https://github.com/{config.Repo}/releases/download/{config.TagPrefix}{version}/{assetName}";
        var archivePath = Path.Combine(toolsDir, assetName);
        var binaryExt = plat == "win32" ? ".exe" : "";
        var binaryPath = Path.Combine(toolsDir, config.BinaryName + binaryExt);

        await DownloadFileAsync(downloadUrl, archivePath, cancellationToken);

        var extractDir = Path.Combine(toolsDir, $"extract_tmp_{config.BinaryName}_{Environment.ProcessId}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Guid.NewGuid():N}"[..^24]);
        Directory.CreateDirectory(extractDir);
        try
        {
            if (assetName.EndsWith(".tar.gz", StringComparison.Ordinal))
            {
                var failure = RunExtractionCommand("tar", ["xzf", archivePath, "-C", extractDir]);
                if (failure is not null) throw new InvalidOperationException($"Failed to extract {assetName}: {failure}");
            }
            else if (assetName.EndsWith(".zip", StringComparison.Ordinal))
            {
                ExtractZipArchive(archivePath, extractDir, assetName);
            }
            else
            {
                throw new InvalidOperationException($"Unsupported archive format: {assetName}");
            }

            var binaryFileName = config.BinaryName + binaryExt;
            var extractedDir = Path.Combine(extractDir, assetName.EndsWith(".tar.gz") ? assetName[..^7] : assetName[..^4]);
            var extractedBinary = new[] { Path.Combine(extractedDir, binaryFileName), Path.Combine(extractDir, binaryFileName) }.FirstOrDefault(File.Exists)
                ?? Directory.EnumerateFiles(extractDir, binaryFileName, SearchOption.AllDirectories).FirstOrDefault()
                ?? throw new InvalidOperationException($"Binary not found in archive: expected {binaryFileName} under {extractDir}");
            File.Move(extractedBinary, binaryPath, overwrite: true);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(binaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
        }
        finally
        {
            try
            {
                File.Delete(archivePath);
            }
            catch (IOException)
            {
            }
            try
            {
                Directory.Delete(extractDir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
        return binaryPath;
    }

    /// <summary>Ensure a tool is available, downloading it if necessary. Returns null when unavailable.</summary>
    public static async Task<string?> EnsureToolAsync(string tool, Action<ToolStatus>? onStatus = null, CancellationToken cancellationToken = default)
    {
        var existingPath = GetToolPath(tool);
        if (existingPath is not null) return existingPath;
        if (!Tools.TryGetValue(tool, out var config)) return null;

        if (IsOfflineModeEnabled())
        {
            onStatus?.Invoke(new ToolStatus("warning", $"{config.Name} not found. Offline mode enabled, skipping download."));
            return null;
        }
        if (Platform == "android")
        {
            onStatus?.Invoke(new ToolStatus("warning", $"{config.Name} not found. Install with: pkg install {(tool == "rg" ? "ripgrep" : tool)}"));
            return null;
        }

        onStatus?.Invoke(new ToolStatus("info", $"{config.Name} not found. Downloading..."));
        try
        {
            var path = await DownloadToolAsync(tool, cancellationToken);
            onStatus?.Invoke(new ToolStatus("info", $"{config.Name} installed to {path}"));
            return path;
        }
        catch (Exception ex)
        {
            var messages = new List<string>();
            for (Exception? current = ex; current is not null && messages.Count < 5; current = current.InnerException)
            {
                if (!messages.Contains(current.Message)) messages.Add(current.Message);
            }
            onStatus?.Invoke(new ToolStatus("warning", $"Failed to download {config.Name}: {string.Join(": ", messages)}"));
            return null;
        }
    }
}
