using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Agent;
using PiSharp.CodingAgent.Utils;

namespace PiSharp.CodingAgent.Core.Tools;

/// <summary>Pluggable operations for the grep tool.</summary>
public sealed class GrepOperations
{
    /// <summary>Whether the path is a directory. Throws when the path does not exist.</summary>
    public Func<string, Task<bool>> IsDirectory { get; init; } = path =>
    {
        if (Directory.Exists(path)) return Task.FromResult(true);
        if (File.Exists(path)) return Task.FromResult(false);
        throw NodeCompat.NoEntry("stat", path);
    };

    public Func<string, Task<string>> ReadFile { get; init; } = path => NodeCompat.ReadUtf8Async(path);
}

/// <summary>Pluggable operations for the find tool. When Glob is provided it replaces fd.</summary>
public sealed class FindOperations
{
    public Func<string, Task<bool>> Exists { get; init; } = path => Task.FromResult(ToolPaths.PathExists(path));

    /// <summary>(pattern, cwd, ignore, limit) → relative or absolute paths.</summary>
    public Func<string, string, IReadOnlyList<string>, double, Task<IReadOnlyList<string>>>? Glob { get; init; }
}

internal static class ExternalProcess
{
    public static Process Start(string fileName, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        return Process.Start(psi) ?? throw new InvalidOperationException($"spawn {fileName} failed");
    }

    public static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill();
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }
}

/// <summary>Port of core/tools/grep.ts.</summary>
public static class GrepTool
{
    public const string Snippet = "Search file contents for patterns (respects .gitignore)";
    private const int DefaultLimit = 100;

    private static readonly JsonObject Schema = ToolSchema.Object([
        ("pattern", ToolSchema.String("Search pattern (regex or literal string)"), false),
        ("path", ToolSchema.String("Directory or file to search (default: current directory)"), true),
        ("glob", ToolSchema.String("Filter files by glob pattern, e.g. '*.ts' or '**/*.spec.ts'"), true),
        ("ignoreCase", ToolSchema.Boolean("Case-insensitive search (default: false)"), true),
        ("literal", ToolSchema.Boolean("Treat pattern as literal string instead of regex (default: false)"), true),
        ("context", ToolSchema.Number("Number of lines to show before and after each match (default: 0)"), true),
        ("limit", ToolSchema.Number("Maximum number of matches to return (default: 100)"), true),
    ]);

    private sealed record Match(string FilePath, int LineNumber, string? LineText);

    public static ToolDefinition CreateDefinition(string cwd, GrepOperations? operations = null)
    {
        var ops = operations ?? new GrepOperations();
        return new ToolDefinition
        {
            Name = "grep",
            Label = "grep",
            Description = $"Search file contents for a pattern. Returns matching lines with file paths and line numbers. Respects .gitignore. Output is truncated to {DefaultLimit} matches or {Truncate.DefaultMaxBytes / 1024}KB (whichever is hit first). Long lines are truncated to {Truncate.GrepMaxLineLength} chars.",
            PromptSnippet = Snippet,
            Parameters = Schema.DeepClone().AsObject(),
            Execute = async (_, args, ct, _, ctx) =>
            {
                ToolSchema.ThrowIfAborted(ct);
                var pattern = ToolSchema.GetString(args, "pattern") ?? "";
                var searchDir = ToolSchema.GetString(args, "path");
                var glob = ToolSchema.GetString(args, "glob");
                var ignoreCase = ToolSchema.GetBool(args, "ignoreCase");
                var literal = ToolSchema.GetBool(args, "literal");
                var context = ToolSchema.GetNumber(args, "context");
                var limit = ToolSchema.GetNumber(args, "limit");

                var rgPath = await ToolsManager.EnsureToolAsync("rg", cancellationToken: ct)
                    ?? throw new InvalidOperationException("ripgrep (rg) is not available and could not be downloaded");

                var searchPath = ToolPaths.ResolveToCwd(string.IsNullOrEmpty(searchDir) ? "." : searchDir, string.IsNullOrEmpty(ctx?.Cwd) ? cwd : ctx.Cwd);
                bool isDirectory;
                try
                {
                    isDirectory = await ops.IsDirectory(searchPath);
                }
                catch
                {
                    throw new InvalidOperationException($"Path not found: {searchPath}");
                }

                var contextValue = context is > 0 ? (int)context.Value : 0;
                var effectiveLimit = Math.Max(1, limit ?? DefaultLimit);

                string FormatPath(string filePath)
                {
                    if (isDirectory)
                    {
                        var relative = Path.GetRelativePath(searchPath, filePath);
                        if (relative.Length > 0 && relative != "." && !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
                        {
                            return relative.Replace('\\', '/');
                        }
                    }
                    return Path.GetFileName(filePath);
                }

                var fileCache = new Dictionary<string, string[]>();
                async Task<string[]> GetFileLines(string filePath)
                {
                    if (fileCache.TryGetValue(filePath, out var cached)) return cached;
                    string[] lines;
                    try
                    {
                        var content = await ops.ReadFile(filePath);
                        lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                    }
                    catch
                    {
                        lines = [];
                    }
                    fileCache[filePath] = lines;
                    return lines;
                }

                var rgArgs = new List<string> { "--json", "--line-number", "--color=never", "--hidden" };
                if (ignoreCase) rgArgs.Add("--ignore-case");
                if (literal) rgArgs.Add("--fixed-strings");
                if (!string.IsNullOrEmpty(glob)) rgArgs.AddRange(["--glob", glob]);
                rgArgs.AddRange(["--", pattern, searchPath]);

                Process child;
                try
                {
                    child = ExternalProcess.Start(rgPath, rgArgs);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Failed to run ripgrep: {ex.Message}");
                }

                using (child)
                {
                    var matchCount = 0;
                    var matchLimitReached = false;
                    var linesTruncated = false;
                    var aborted = false;
                    var killedDueToLimit = false;
                    var matches = new List<Match>();
                    var stderrTask = child.StandardError.ReadToEndAsync(CancellationToken.None);

                    using (ct.Register(() =>
                    {
                        aborted = true;
                        ExternalProcess.TryKill(child);
                    }))
                    {
                        string? line;
                        while ((line = await child.StandardOutput.ReadLineAsync(CancellationToken.None)) is not null)
                        {
                            if (string.IsNullOrWhiteSpace(line) || matchCount >= effectiveLimit) continue;
                            JsonNode? evt;
                            try
                            {
                                evt = JsonNode.Parse(line);
                            }
                            catch (JsonException)
                            {
                                continue;
                            }
                            if (evt?["type"]?.GetValueKind() != JsonValueKind.String || evt["type"]!.GetValue<string>() != "match") continue;

                            matchCount++;
                            var data = evt["data"];
                            var filePath = data?["path"]?["text"] is JsonValue p && p.GetValueKind() == JsonValueKind.String ? p.GetValue<string>() : null;
                            var lineNumber = data?["line_number"] is JsonValue n && n.GetValueKind() == JsonValueKind.Number ? n.GetValue<int>() : (int?)null;
                            var lineText = data?["lines"]?["text"] is JsonValue t && t.GetValueKind() == JsonValueKind.String ? t.GetValue<string>() : null;
                            if (!string.IsNullOrEmpty(filePath) && lineNumber is { } ln) matches.Add(new Match(filePath, ln, lineText));
                            if (matchCount >= effectiveLimit)
                            {
                                matchLimitReached = true;
                                killedDueToLimit = true;
                                ExternalProcess.TryKill(child);
                            }
                        }
                        await child.WaitForExitAsync(CancellationToken.None);
                    }

                    var stderr = await stderrTask;
                    if (aborted) throw new OperationAbortedException();
                    var code = child.ExitCode;
                    if (!killedDueToLimit && code != 0 && code != 1)
                    {
                        throw new InvalidOperationException(stderr.Trim().Length > 0 ? stderr.Trim() : $"ripgrep exited with code {code}");
                    }
                    if (matchCount == 0) return AgentToolResult.Text("No matches found");

                    var outputLines = new List<string>();
                    foreach (var match in matches)
                    {
                        if (contextValue == 0 && match.LineText is not null)
                        {
                            var sanitized = match.LineText.Replace("\r\n", "\n").Replace("\r", "");
                            if (sanitized.EndsWith('\n')) sanitized = sanitized[..^1];
                            var (truncatedText, wasTruncated) = Truncate.TruncateLine(sanitized);
                            if (wasTruncated) linesTruncated = true;
                            outputLines.Add($"{FormatPath(match.FilePath)}:{match.LineNumber}: {truncatedText}");
                            continue;
                        }

                        var relativePath = FormatPath(match.FilePath);
                        var lines = await GetFileLines(match.FilePath);
                        if (lines.Length == 0)
                        {
                            outputLines.Add($"{relativePath}:{match.LineNumber}: (unable to read file)");
                            continue;
                        }
                        var start = contextValue > 0 ? Math.Max(1, match.LineNumber - contextValue) : match.LineNumber;
                        var end = contextValue > 0 ? Math.Min(lines.Length, match.LineNumber + contextValue) : match.LineNumber;
                        for (var current = start; current <= end; current++)
                        {
                            var lineText = current - 1 < lines.Length && current >= 1 ? lines[current - 1] : "";
                            var (truncatedText, wasTruncated) = Truncate.TruncateLine(lineText.Replace("\r", ""));
                            if (wasTruncated) linesTruncated = true;
                            outputLines.Add(current == match.LineNumber
                                ? $"{relativePath}:{current}: {truncatedText}"
                                : $"{relativePath}-{current}- {truncatedText}");
                        }
                    }

                    var truncation = Truncate.TruncateHead(string.Join("\n", outputLines), Truncate.Unlimited);
                    var output = truncation.Content;
                    var details = new JsonObject();
                    var notices = new List<string>();
                    if (matchLimitReached)
                    {
                        notices.Add($"{NodeCompat.FormatNumber(effectiveLimit)} matches limit reached. Use limit={NodeCompat.FormatNumber(effectiveLimit * 2)} for more, or refine pattern");
                        details["matchLimitReached"] = effectiveLimit;
                    }
                    if (truncation.Truncated)
                    {
                        notices.Add($"{Truncate.FormatSize(Truncate.DefaultMaxBytes)} limit reached");
                        details["truncation"] = truncation.ToJson();
                    }
                    if (linesTruncated)
                    {
                        notices.Add($"Some lines truncated to {Truncate.GrepMaxLineLength} chars. Use read tool to see full lines");
                        details["linesTruncated"] = true;
                    }
                    if (notices.Count > 0) output += $"\n\n[{string.Join(". ", notices)}]";
                    return AgentToolResult.Text(output, details.Count > 0 ? details : null);
                }
            },
        };
    }

    public static AgentTool Create(string cwd, GrepOperations? operations = null) => CreateDefinition(cwd, operations).ToAgentTool();
}

/// <summary>Port of core/tools/find.ts.</summary>
public static class FindTool
{
    public const string Snippet = "Find files by glob pattern (respects .gitignore)";
    private const int DefaultLimit = 1000;

    private static readonly JsonObject Schema = ToolSchema.Object([
        ("pattern", ToolSchema.String("Glob pattern to match files, e.g. '*.ts', '**/*.json', or 'src/**/*.spec.ts'"), false),
        ("path", ToolSchema.String("Directory to search in (default: current directory)"), true),
        ("limit", ToolSchema.Number("Maximum number of results (default: 1000)"), true),
    ]);

    /// <summary>Relativize a find result against the search root and normalize it to posix separators.</summary>
    public static string RelativizeFindResultPath(string resultPath, string searchPath)
    {
        var sep = Path.DirectorySeparatorChar;
        var hadTrailingSeparator = resultPath.EndsWith(sep) || (sep == '\\' && resultPath.EndsWith('/'));
        var relativePath = Path.IsPathRooted(resultPath) ? Path.GetRelativePath(searchPath, resultPath) : resultPath;
        if (relativePath == ".") relativePath = "";
        var posixPath = relativePath.Replace(sep, '/');
        return hadTrailingSeparator && !posixPath.EndsWith('/') ? $"{posixPath}/" : posixPath;
    }

    private static AgentToolResult BuildResult(List<string> relativized, double effectiveLimit, bool withHint)
    {
        var resultLimitReached = relativized.Count >= effectiveLimit;
        var truncation = Truncate.TruncateHead(string.Join("\n", relativized), Truncate.Unlimited);
        var resultOutput = truncation.Content;
        var details = new JsonObject();
        var notices = new List<string>();
        if (resultLimitReached)
        {
            notices.Add(withHint
                ? $"{NodeCompat.FormatNumber(effectiveLimit)} results limit reached. Use limit={NodeCompat.FormatNumber(effectiveLimit * 2)} for more, or refine pattern"
                : $"{NodeCompat.FormatNumber(effectiveLimit)} results limit reached");
            details["resultLimitReached"] = effectiveLimit;
        }
        if (truncation.Truncated)
        {
            notices.Add($"{Truncate.FormatSize(Truncate.DefaultMaxBytes)} limit reached");
            details["truncation"] = truncation.ToJson();
        }
        if (notices.Count > 0) resultOutput += $"\n\n[{string.Join(". ", notices)}]";
        return AgentToolResult.Text(resultOutput, details.Count > 0 ? details : null);
    }

    public static ToolDefinition CreateDefinition(string cwd, FindOperations? operations = null)
    {
        var customOps = operations;
        return new ToolDefinition
        {
            Name = "find",
            Label = "find",
            Description = $"Search for files by glob pattern. Returns matching file paths relative to the search directory. Respects .gitignore. Output is truncated to {DefaultLimit} results or {Truncate.DefaultMaxBytes / 1024}KB (whichever is hit first).",
            PromptSnippet = Snippet,
            Parameters = Schema.DeepClone().AsObject(),
            Execute = async (_, args, ct, _, ctx) =>
            {
                ToolSchema.ThrowIfAborted(ct);
                var pattern = ToolSchema.GetString(args, "pattern") ?? "";
                var searchDir = ToolSchema.GetString(args, "path");
                var limit = ToolSchema.GetNumber(args, "limit");
                var searchPath = ToolPaths.ResolveToCwd(string.IsNullOrEmpty(searchDir) ? "." : searchDir, string.IsNullOrEmpty(ctx?.Cwd) ? cwd : ctx.Cwd);
                var effectiveLimit = limit ?? DefaultLimit;

                if (customOps?.Glob is { } customGlob)
                {
                    if (!await customOps.Exists(searchPath)) throw new InvalidOperationException($"Path not found: {searchPath}");
                    ToolSchema.ThrowIfAborted(ct);
                    var results = await customGlob(pattern, searchPath, ["**/node_modules/**", "**/.git/**"], effectiveLimit);
                    ToolSchema.ThrowIfAborted(ct);
                    if (results.Count == 0) return AgentToolResult.Text("No files found matching pattern");
                    return BuildResult(results.Select(p => RelativizeFindResultPath(p, searchPath)).ToList(), effectiveLimit, withHint: false);
                }

                var fdPath = await ToolsManager.EnsureToolAsync("fd", cancellationToken: ct);
                ToolSchema.ThrowIfAborted(ct);
                if (fdPath is null) throw new InvalidOperationException("fd is not available and could not be downloaded");

                var fdArgs = new List<string> { "--glob", "--color=never", "--hidden" };

                // fd ignores .gitignore outside git repos unless --no-require-git; inside repos keep fd's git-aware behavior.
                var insideGitRepo = false;
                for (var current = searchPath; ;)
                {
                    if (ToolPaths.PathExists(Path.Combine(current, ".git")))
                    {
                        insideGitRepo = true;
                        break;
                    }
                    var parent = Path.GetDirectoryName(current);
                    if (parent is null || parent == current) break;
                    current = parent;
                }
                if (!insideGitRepo) fdArgs.Add("--no-require-git");
                fdArgs.AddRange(["--max-results", NodeCompat.FormatNumber(effectiveLimit)]);

                // fd --glob matches the basename unless --full-path is set, in which case it matches the absolute path.
                var effectivePattern = pattern;
                if (pattern.Contains('/'))
                {
                    fdArgs.Add("--full-path");
                    if (!pattern.StartsWith('/') && !pattern.StartsWith("**/", StringComparison.Ordinal) && pattern != "**")
                    {
                        effectivePattern = $"**/{pattern}";
                    }
                    if (OperatingSystem.IsWindows()) effectivePattern = effectivePattern.Replace("/", @"[/\\]");
                }
                fdArgs.AddRange(["--", effectivePattern, searchPath]);

                Process child;
                try
                {
                    child = ExternalProcess.Start(fdPath, fdArgs);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Failed to run fd: {ex.Message}");
                }

                using (child)
                {
                    var stderrTask = child.StandardError.ReadToEndAsync(CancellationToken.None);
                    var lines = new List<string>();
                    using (ct.Register(() => ExternalProcess.TryKill(child)))
                    {
                        string? line;
                        while ((line = await child.StandardOutput.ReadLineAsync(CancellationToken.None)) is not null)
                        {
                            lines.Add(line);
                        }
                        await child.WaitForExitAsync(CancellationToken.None);
                    }
                    var stderr = await stderrTask;
                    ToolSchema.ThrowIfAborted(ct);

                    var output = string.Join("\n", lines);
                    if (child.ExitCode != 0 && output.Length == 0)
                    {
                        throw new InvalidOperationException(stderr.Trim().Length > 0 ? stderr.Trim() : $"fd exited with code {child.ExitCode}");
                    }
                    if (output.Length == 0) return AgentToolResult.Text("No files found matching pattern");

                    var relativized = new List<string>();
                    foreach (var rawLine in lines)
                    {
                        var trimmed = rawLine.TrimEnd('\r').Trim();
                        if (trimmed.Length == 0) continue;
                        relativized.Add(RelativizeFindResultPath(trimmed, searchPath));
                    }
                    return BuildResult(relativized, effectiveLimit, withHint: true);
                }
            },
        };
    }

    public static AgentTool Create(string cwd, FindOperations? operations = null) => CreateDefinition(cwd, operations).ToAgentTool();
}
