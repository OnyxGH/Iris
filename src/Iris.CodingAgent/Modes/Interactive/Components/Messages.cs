using System.Globalization;
using System.Text.RegularExpressions;
using Iris.Ai;
using Iris.CodingAgent.Core;
using Iris.CodingAgent.Core.Tools;
using Iris.CodingAgent.Utils;
using Iris.Tui;
using Iris.Tui.Components;

namespace Iris.CodingAgent.Modes.Interactive.Components;

/// <summary>Components that can toggle between collapsed and expanded views.</summary>
public interface IExpandable
{
    void SetExpanded(bool expanded);
}

/// <summary>Compaction summary with collapsed/expanded state. Port of compaction-summary-message.ts.</summary>
public sealed class CompactionSummaryMessageComponent : Box, IExpandable
{
    private bool _expanded;
    private readonly CompactionSummaryMessage _message;
    private readonly MarkdownTheme _markdownTheme;

    public CompactionSummaryMessageComponent(CompactionSummaryMessage message, MarkdownTheme? markdownTheme = null)
        : base(1, 1, t => ThemeManager.Current.Bg("customMessageBg", t))
    {
        _message = message;
        _markdownTheme = markdownTheme ?? ThemeManager.GetMarkdownTheme();
        UpdateDisplay();
    }

    public void SetExpanded(bool expanded)
    {
        _expanded = expanded;
        UpdateDisplay();
    }

    public override void Invalidate()
    {
        base.Invalidate();
        UpdateDisplay();
    }

    private void UpdateDisplay()
    {
        Clear();
        var theme = ThemeManager.Current;
        var tokenStr = _message.TokensBefore.ToString("N0", CultureInfo.InvariantCulture);
        AddChild(new Text(theme.Fg("customMessageLabel", "\e[1m[compaction]\e[22m"), 0, 0));
        AddChild(new Spacer(1));
        if (_expanded)
        {
            AddChild(new MarkdownComponent($"**Compacted from {tokenStr} tokens**\n\n" + _message.Summary, 0, 0, _markdownTheme,
                new DefaultTextStyle { Color = t => ThemeManager.Current.Fg("customMessageText", t) }));
        }
        else
        {
            AddChild(new Text(theme.Fg("customMessageText", $"Compacted from {tokenStr} tokens (") + theme.Fg("dim", KeyHints.KeyText("app.tools.expand")) + theme.Fg("customMessageText", " to expand)"), 0, 0));
        }
    }
}

/// <summary>Branch summary with collapsed/expanded state. Port of branch-summary-message.ts.</summary>
public sealed class BranchSummaryMessageComponent : Box, IExpandable
{
    private bool _expanded;
    private readonly BranchSummaryMessage _message;
    private readonly MarkdownTheme _markdownTheme;

    public BranchSummaryMessageComponent(BranchSummaryMessage message, MarkdownTheme? markdownTheme = null)
        : base(1, 1, t => ThemeManager.Current.Bg("customMessageBg", t))
    {
        _message = message;
        _markdownTheme = markdownTheme ?? ThemeManager.GetMarkdownTheme();
        UpdateDisplay();
    }

    public void SetExpanded(bool expanded)
    {
        _expanded = expanded;
        UpdateDisplay();
    }

    public override void Invalidate()
    {
        base.Invalidate();
        UpdateDisplay();
    }

    private void UpdateDisplay()
    {
        Clear();
        var theme = ThemeManager.Current;
        AddChild(new Text(theme.Fg("customMessageLabel", "\e[1m[branch]\e[22m"), 0, 0));
        AddChild(new Spacer(1));
        if (_expanded)
        {
            AddChild(new MarkdownComponent("**Branch Summary**\n\n" + _message.Summary, 0, 0, _markdownTheme,
                new DefaultTextStyle { Color = t => ThemeManager.Current.Fg("customMessageText", t) }));
        }
        else
        {
            AddChild(new Text(theme.Fg("customMessageText", "Branch summary (") + theme.Fg("dim", KeyHints.KeyText("app.tools.expand")) + theme.Fg("customMessageText", " to expand)"), 0, 0));
        }
    }
}

/// <summary>Skill invocation block. Port of skill-invocation-message.ts.</summary>
public sealed class SkillInvocationMessageComponent : Box, IExpandable
{
    private bool _expanded;
    private readonly ParsedSkillBlock _skillBlock;
    private readonly MarkdownTheme _markdownTheme;

    public SkillInvocationMessageComponent(ParsedSkillBlock skillBlock, MarkdownTheme? markdownTheme = null)
        : base(1, 1, t => ThemeManager.Current.Bg("customMessageBg", t))
    {
        _skillBlock = skillBlock;
        _markdownTheme = markdownTheme ?? ThemeManager.GetMarkdownTheme();
        UpdateDisplay();
    }

    public void SetExpanded(bool expanded)
    {
        _expanded = expanded;
        UpdateDisplay();
    }

    public override void Invalidate()
    {
        base.Invalidate();
        UpdateDisplay();
    }

    private void UpdateDisplay()
    {
        Clear();
        var theme = ThemeManager.Current;
        if (_expanded)
        {
            AddChild(new Text(theme.Fg("customMessageLabel", "\e[1m[skill]\e[22m"), 0, 0));
            AddChild(new MarkdownComponent($"**{_skillBlock.Name}**\n\n" + _skillBlock.Content, 0, 0, _markdownTheme,
                new DefaultTextStyle { Color = t => ThemeManager.Current.Fg("customMessageText", t) }));
        }
        else
        {
            AddChild(new Text(theme.Fg("customMessageLabel", "\e[1m[skill]\e[22m ") + theme.Fg("customMessageText", _skillBlock.Name) + theme.Fg("dim", $" ({KeyHints.KeyText("app.tools.expand")} to expand)"), 0, 0));
        }
    }
}

public delegate IComponent? MessageRenderer(CustomMessage message, bool expanded, int outputPad, Theme theme);

/// <summary>Extension custom message. Port of custom-message.ts.</summary>
public sealed class CustomMessageComponent : Container, IExpandable
{
    private readonly CustomMessage _message;
    private readonly MessageRenderer? _customRenderer;
    private readonly Box _box;
    private IComponent? _customComponent;
    private readonly MarkdownTheme _markdownTheme;
    private bool _expanded;
    private int _outputPad;

    public CustomMessageComponent(CustomMessage message, MessageRenderer? customRenderer = null, MarkdownTheme? markdownTheme = null, int outputPad = 1)
    {
        _message = message;
        _customRenderer = customRenderer;
        _markdownTheme = markdownTheme ?? ThemeManager.GetMarkdownTheme();
        _outputPad = outputPad;
        AddChild(new Spacer(1));
        _box = new Box(1, 1, t => ThemeManager.Current.Bg("customMessageBg", t));
        Rebuild();
    }

    public void SetExpanded(bool expanded)
    {
        if (_expanded == expanded) return;
        _expanded = expanded;
        Rebuild();
    }

    public void SetOutputPad(int outputPad)
    {
        if (_outputPad == outputPad) return;
        _outputPad = outputPad;
        Rebuild();
    }

    public override void Invalidate()
    {
        base.Invalidate();
        Rebuild();
    }

    private void Rebuild()
    {
        if (_customComponent is not null)
        {
            RemoveChild(_customComponent);
            _customComponent = null;
        }
        RemoveChild(_box);

        if (_customRenderer is not null)
        {
            try
            {
                if (_customRenderer(_message, _expanded, _outputPad, ThemeManager.Current) is { } component)
                {
                    _customComponent = component;
                    AddChild(component);
                    return;
                }
            }
            catch
            {
                // Fall through to default rendering.
            }
        }

        AddChild(_box);
        _box.Clear();
        var theme = ThemeManager.Current;
        _box.AddChild(new Text(theme.Fg("customMessageLabel", $"\e[1m[{_message.CustomType}]\e[22m"), 0, 0));
        _box.AddChild(new Spacer(1));
        var text = _message.Content.IsText ? _message.Content.Text! : string.Join("\n", _message.Content.AsBlocks().OfType<TextContent>().Select(c => c.Text));
        _box.AddChild(new MarkdownComponent(text, 0, 0, _markdownTheme, new DefaultTextStyle { Color = t => ThemeManager.Current.Fg("customMessageText", t) }));
    }
}

/// <summary>Git branch and extension status data for the footer. Port of core/footer-data-provider.ts.</summary>
public sealed class FooterDataProvider : IDisposable
{
    private const int WatchDebounceMs = 500;
    private string _cwd;
    private readonly Dictionary<string, string> _extensionStatuses = [];
    private string? _cachedBranch;
    private bool _branchResolved;
    private GitHeadPaths? _gitPaths;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly List<Action> _branchChangeCallbacks = [];
    private int _availableProviderCount;
    private IDisposable? _refreshTimer;
    private bool _disposed;
    private readonly UiDispatcher? _dispatcher = UiDispatcher.Current;

    private sealed record GitHeadPaths(string RepoDir, string CommonGitDir, string HeadPath);

    public FooterDataProvider(string cwd)
    {
        _cwd = cwd;
        _gitPaths = FindGitPaths(cwd);
        SetupGitWatcher();
    }

    private static GitHeadPaths? FindGitPaths(string cwd)
    {
        var dir = cwd;
        while (true)
        {
            var gitPath = Path.Combine(dir, ".git");
            try
            {
                if (File.Exists(gitPath))
                {
                    var content = File.ReadAllText(gitPath).Trim();
                    if (content.StartsWith("gitdir: ", StringComparison.Ordinal))
                    {
                        var gitDir = Path.GetFullPath(Path.Combine(dir, content[8..].Trim()));
                        var headPath = Path.Combine(gitDir, "HEAD");
                        if (!File.Exists(headPath)) return null;
                        var commonDirPath = Path.Combine(gitDir, "commondir");
                        var commonGitDir = File.Exists(commonDirPath) ? Path.GetFullPath(Path.Combine(gitDir, File.ReadAllText(commonDirPath).Trim())) : gitDir;
                        return new GitHeadPaths(dir, commonGitDir, headPath);
                    }
                }
                else if (Directory.Exists(gitPath))
                {
                    var headPath = Path.Combine(gitPath, "HEAD");
                    return File.Exists(headPath) ? new GitHeadPaths(dir, gitPath, headPath) : null;
                }
            }
            catch
            {
                return null;
            }
            var parent = Path.GetDirectoryName(dir);
            if (parent is null || parent == dir) return null;
            dir = parent;
        }
    }

    public string? GetGitBranch()
    {
        if (!_branchResolved)
        {
            _cachedBranch = ResolveGitBranch();
            _branchResolved = true;
        }
        return _cachedBranch;
    }

    public IReadOnlyDictionary<string, string> GetExtensionStatuses() => _extensionStatuses;

    public Action OnBranchChange(Action callback)
    {
        _branchChangeCallbacks.Add(callback);
        return () => _branchChangeCallbacks.Remove(callback);
    }

    public void SetExtensionStatus(string key, string? text)
    {
        if (text is null) _extensionStatuses.Remove(key);
        else _extensionStatuses[key] = text;
    }

    public void ClearExtensionStatuses() => _extensionStatuses.Clear();

    public int GetAvailableProviderCount() => _availableProviderCount;

    public void SetAvailableProviderCount(int count) => _availableProviderCount = count;

    public void SetCwd(string cwd)
    {
        if (_cwd == cwd) return;
        _cwd = cwd;
        _refreshTimer?.Dispose();
        _refreshTimer = null;
        ClearWatchers();
        _branchResolved = false;
        _gitPaths = FindGitPaths(cwd);
        SetupGitWatcher();
        NotifyBranchChange();
    }

    public void Dispose()
    {
        _disposed = true;
        _refreshTimer?.Dispose();
        ClearWatchers();
        _branchChangeCallbacks.Clear();
    }

    private void NotifyBranchChange()
    {
        foreach (var cb in _branchChangeCallbacks.ToList()) cb();
    }

    private string? ResolveGitBranch()
    {
        try
        {
            if (_gitPaths is null) return null;
            var content = File.ReadAllText(_gitPaths.HeadPath).Trim();
            if (content.StartsWith("ref: refs/heads/", StringComparison.Ordinal))
            {
                var branch = content[16..];
                return branch == ".invalid" ? ResolveBranchWithGit(_gitPaths.RepoDir) ?? "detached" : branch;
            }
            return "detached";
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveBranchWithGit(string repoDir)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = repoDir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "--no-optional-locks", "symbolic-ref", "--quiet", "--short", "HEAD" }) psi.ArgumentList.Add(arg);
            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            var branch = process.ExitCode == 0 ? output.Trim() : "";
            return branch.Length > 0 ? branch : null;
        }
        catch
        {
            return null;
        }
    }

    private void ScheduleRefresh()
    {
        if (_disposed || _dispatcher is null) return;
        _dispatcher.Post(() =>
        {
            if (_disposed || _refreshTimer is not null) return;
            _refreshTimer = _dispatcher.SetTimeout(async () =>
            {
                _refreshTimer = null;
                var next = await Task.Run(ResolveGitBranch);
                if (_disposed) return;
                if (_branchResolved && _cachedBranch != next)
                {
                    _cachedBranch = next;
                    NotifyBranchChange();
                    return;
                }
                _cachedBranch = next;
                _branchResolved = true;
            }, WatchDebounceMs);
        });
    }

    private void ClearWatchers()
    {
        foreach (var watcher in _watchers) watcher.Dispose();
        _watchers.Clear();
    }

    private void SetupGitWatcher()
    {
        ClearWatchers();
        if (_gitPaths is null) return;
        try
        {
            var headDir = Path.GetDirectoryName(_gitPaths.HeadPath)!;
            var watcher = new FileSystemWatcher(headDir) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
            FileSystemEventHandler handler = (_, e) =>
            {
                if (string.IsNullOrEmpty(e.Name) || e.Name == "HEAD") ScheduleRefresh();
            };
            watcher.Changed += handler;
            watcher.Created += handler;
            watcher.Renamed += (_, e) => handler(null, e);
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);

            var reftableDir = Path.Combine(_gitPaths.CommonGitDir, "reftable");
            if (Directory.Exists(reftableDir))
            {
                var reftable = new FileSystemWatcher(reftableDir) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
                reftable.Changed += (_, _) => ScheduleRefresh();
                reftable.Created += (_, _) => ScheduleRefresh();
                reftable.EnableRaisingEvents = true;
                _watchers.Add(reftable);
            }
        }
        catch
        {
            ClearWatchers();
        }
    }
}

/// <summary>Footer with cwd, token stats and model. Port of footer.ts.</summary>
public sealed partial class FooterComponent(AgentSession session, FooterDataProvider footerData) : IComponent
{
    private AgentSession _session = session;
    private bool _autoCompactEnabled = true;

    public void SetSession(AgentSession session) => _session = session;

    public void SetAutoCompactEnabled(bool enabled) => _autoCompactEnabled = enabled;

    public void Invalidate()
    {
    }

    [GeneratedRegex(@"[\r\n\t]")]
    private static partial Regex ControlWhitespace();

    [GeneratedRegex(" +")]
    private static partial Regex MultipleSpaces();

    private static string SanitizeStatusText(string text) => MultipleSpaces().Replace(ControlWhitespace().Replace(text, " "), " ").Trim();

    public static string FormatTokens(long count)
    {
        if (count < 1000) return count.ToString(CultureInfo.InvariantCulture);
        if (count < 10000) return NodeCompat.ToFixed(count / 1000.0, 1) + "k";
        if (count < 1000000) return Math.Round(count / 1000.0, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture) + "k";
        if (count < 10000000) return NodeCompat.ToFixed(count / 1000000.0, 1) + "M";
        return Math.Round(count / 1000000.0, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture) + "M";
    }

    public static string FormatCwdForFooter(string cwd, string? home)
    {
        if (string.IsNullOrEmpty(home)) return cwd;
        var resolvedCwd = Path.GetFullPath(cwd);
        var resolvedHome = Path.GetFullPath(home);
        var relative = Path.GetRelativePath(resolvedHome, resolvedCwd);
        if (relative == ".") relative = "";
        var sep = Path.DirectorySeparatorChar;
        var inside = relative == "" || (relative != ".." && !relative.StartsWith($"..{sep}", StringComparison.Ordinal) && !Path.IsPathRooted(relative));
        if (!inside) return cwd;
        return relative == "" ? "~" : $"~{sep}{relative}";
    }

    public List<string> Render(int width)
    {
        var theme = ThemeManager.Current;
        var state = _session.State;
        long input = 0, output = 0, cacheRead = 0, cacheWrite = 0;
        double cost = 0;
        double? latestCacheHitRate = null;

        void AddUsage(Usage usage)
        {
            input += usage.Input;
            output += usage.Output;
            cacheRead += usage.CacheRead;
            cacheWrite += usage.CacheWrite;
            cost += usage.Cost.Total;
        }

        foreach (var entry in _session.SessionManager.GetEntries())
        {
            if (entry is SessionMessageEntry { Message: AssistantMessage assistant })
            {
                AddUsage(assistant.Usage);
                var promptTokens = assistant.Usage.Input + assistant.Usage.CacheRead + assistant.Usage.CacheWrite;
                latestCacheHitRate = promptTokens > 0 ? (double)assistant.Usage.CacheRead / promptTokens * 100 : null;
            }
            else if (entry is SessionMessageEntry { Message: ToolResultMessage { Usage: { } toolUsage } })
            {
                AddUsage(toolUsage);
            }
            else if (entry is BranchSummaryEntry { Usage: { } branchUsage })
            {
                AddUsage(branchUsage);
            }
            else if (entry is CompactionEntry { Usage: { } compactionUsage })
            {
                AddUsage(compactionUsage);
            }
        }

        var contextUsage = _session.GetContextUsage();
        var contextWindow = contextUsage?.ContextWindow ?? state.Model.ContextWindow;
        var contextPercentValue = contextUsage?.Percent ?? 0;
        var contextPercent = contextUsage is null || contextUsage.Percent is not null ? NodeCompat.ToFixed(contextPercentValue, 1) : "?";

        var pwd = FormatCwdForFooter(_session.SessionManager.Cwd, Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } h ? h : Environment.GetEnvironmentVariable("USERPROFILE"));
        if (footerData.GetGitBranch() is { } branch) pwd = $"{pwd} ({branch})";
        if (_session.SessionManager.SessionName is { Length: > 0 } sessionName) pwd = $"{pwd} • {sessionName}";

        var statsParts = new List<string>();
        if (input != 0) statsParts.Add($"↑{FormatTokens(input)}");
        if (output != 0) statsParts.Add($"↓{FormatTokens(output)}");
        if (cacheRead != 0) statsParts.Add($"R{FormatTokens(cacheRead)}");
        if (cacheWrite != 0) statsParts.Add($"W{FormatTokens(cacheWrite)}");
        if ((cacheRead > 0 || cacheWrite > 0) && latestCacheHitRate is { } rate) statsParts.Add($"CH{NodeCompat.ToFixed(rate, 1)}%");

        var usingSubscription = state.Model.Provider == "kimi-coding" || _session.ModelRuntime.IsUsingSubscription(state.Model.Provider);
        if (cost != 0 || usingSubscription) statsParts.Add($"${NodeCompat.ToFixed(cost, 3)}{(usingSubscription ? " (sub)" : "")}");

        var autoIndicator = _autoCompactEnabled ? " (auto)" : "";
        var contextDisplay = contextPercent == "?" ? $"?/{FormatTokens(contextWindow)}{autoIndicator}" : $"{contextPercent}%/{FormatTokens(contextWindow)}{autoIndicator}";
        statsParts.Add(contextPercentValue > 90 ? theme.Fg("error", contextDisplay) : contextPercentValue > 70 ? theme.Fg("warning", contextDisplay) : contextDisplay);
        if (Environment.GetEnvironmentVariable("PI_EXPERIMENTAL") == "1") statsParts.Add($"{theme.Fg("dim", "•")} {theme.Bold(theme.Fg("warning", "xp"))}");

        var statsLeft = string.Join(" ", statsParts);
        var modelName = state.Model.Id is { Length: > 0 } id ? id : "no-model";
        var statsLeftWidth = TextUtils.VisibleWidth(statsLeft);
        if (statsLeftWidth > width)
        {
            statsLeft = TextUtils.TruncateToWidth(statsLeft, width, "...");
            statsLeftWidth = TextUtils.VisibleWidth(statsLeft);
        }

        const int minPadding = 2;
        var rightWithoutProvider = modelName;
        if (state.Model.Reasoning)
        {
            var level = state.ThinkingLevel.ToWire();
            rightWithoutProvider = level == "off" ? $"{modelName} • thinking off" : $"{modelName} • {level}";
        }

        var rightSide = rightWithoutProvider;
        if (footerData.GetAvailableProviderCount() > 1)
        {
            rightSide = $"({state.Model.Provider}) {rightWithoutProvider}";
            if (statsLeftWidth + minPadding + TextUtils.VisibleWidth(rightSide) > width) rightSide = rightWithoutProvider;
        }

        var rightSideWidth = TextUtils.VisibleWidth(rightSide);
        string statsLine;
        if (statsLeftWidth + minPadding + rightSideWidth <= width)
        {
            statsLine = statsLeft + new string(' ', width - statsLeftWidth - rightSideWidth) + rightSide;
        }
        else
        {
            var availableForRight = width - statsLeftWidth - minPadding;
            if (availableForRight > 0)
            {
                var truncatedRight = TextUtils.TruncateToWidth(rightSide, availableForRight, "");
                statsLine = statsLeft + new string(' ', Math.Max(0, width - statsLeftWidth - TextUtils.VisibleWidth(truncatedRight))) + truncatedRight;
            }
            else
            {
                statsLine = statsLeft;
            }
        }

        var dimStatsLeft = theme.Fg("dim", statsLeft);
        var dimRemainder = theme.Fg("dim", statsLine[statsLeft.Length..]);
        var pwdLine = TextUtils.TruncateToWidth(theme.Fg("dim", pwd), width, theme.Fg("dim", "..."));
        var lines = new List<string> { pwdLine, dimStatsLeft + dimRemainder };

        var statuses = footerData.GetExtensionStatuses();
        if (statuses.Count > 0)
        {
            var statusLine = string.Join(" ", statuses.OrderBy(kv => kv.Key, Comparer<string>.Create(NodeCompare.LocaleCompare)).Select(kv => SanitizeStatusText(kv.Value)));
            lines.Add(TextUtils.TruncateToWidth(statusLine, width, theme.Fg("dim", "...")));
        }
        return lines;
    }
}

/// <summary>Colored diff rendering with intra-line word highlights. Port of diff.ts.</summary>
public static partial class DiffRenderer
{
    [GeneratedRegex(@"^([+\-\s])(\s*\d*)\s(.*)$")]
    private static partial Regex DiffLine();

    private static (string Prefix, string LineNum, string Content)? ParseDiffLine(string line)
    {
        var m = DiffLine().Match(line);
        return m.Success ? (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value) : null;
    }

    private static string ReplaceTabs(string text) => text.Replace("\t", "   ");

    private static int LeadingWhitespaceLength(string value)
    {
        var i = 0;
        while (i < value.Length && TextUtils.IsJsWhitespace(value[i])) i++;
        return i;
    }

    private static (string Removed, string Added) RenderIntraLineDiff(string oldContent, string newContent)
    {
        var theme = ThemeManager.Current;
        var removedLine = "";
        var addedLine = "";
        var isFirstRemoved = true;
        var isFirstAdded = true;
        foreach (var part in WordDiff.DiffWords(oldContent, newContent))
        {
            if (part.Removed)
            {
                var value = part.Value;
                if (isFirstRemoved)
                {
                    var ws = LeadingWhitespaceLength(value);
                    removedLine += value[..ws];
                    value = value[ws..];
                    isFirstRemoved = false;
                }
                if (value.Length > 0) removedLine += theme.Inverse(value);
            }
            else if (part.Added)
            {
                var value = part.Value;
                if (isFirstAdded)
                {
                    var ws = LeadingWhitespaceLength(value);
                    addedLine += value[..ws];
                    value = value[ws..];
                    isFirstAdded = false;
                }
                if (value.Length > 0) addedLine += theme.Inverse(value);
            }
            else
            {
                removedLine += part.Value;
                addedLine += part.Value;
            }
        }
        return (removedLine, addedLine);
    }

    public static string RenderDiff(string diffText)
    {
        var theme = ThemeManager.Current;
        var lines = diffText.Split('\n');
        var result = new List<string>();
        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];
            if (ParseDiffLine(line) is not { } parsed)
            {
                result.Add(theme.Fg("toolDiffContext", line));
                i++;
                continue;
            }

            if (parsed.Prefix == "-")
            {
                var removed = new List<(string LineNum, string Content)>();
                while (i < lines.Length && ParseDiffLine(lines[i]) is { Prefix: "-" } p)
                {
                    removed.Add((p.LineNum, p.Content));
                    i++;
                }
                var added = new List<(string LineNum, string Content)>();
                while (i < lines.Length && ParseDiffLine(lines[i]) is { Prefix: "+" } p)
                {
                    added.Add((p.LineNum, p.Content));
                    i++;
                }
                if (removed.Count == 1 && added.Count == 1)
                {
                    var (removedLine, addedLine) = RenderIntraLineDiff(ReplaceTabs(removed[0].Content), ReplaceTabs(added[0].Content));
                    result.Add(theme.Fg("toolDiffRemoved", $"-{removed[0].LineNum} {removedLine}"));
                    result.Add(theme.Fg("toolDiffAdded", $"+{added[0].LineNum} {addedLine}"));
                }
                else
                {
                    foreach (var r in removed) result.Add(theme.Fg("toolDiffRemoved", $"-{r.LineNum} {ReplaceTabs(r.Content)}"));
                    foreach (var a in added) result.Add(theme.Fg("toolDiffAdded", $"+{a.LineNum} {ReplaceTabs(a.Content)}"));
                }
            }
            else if (parsed.Prefix == "+")
            {
                result.Add(theme.Fg("toolDiffAdded", $"+{parsed.LineNum} {ReplaceTabs(parsed.Content)}"));
                i++;
            }
            else
            {
                result.Add(theme.Fg("toolDiffContext", $" {parsed.LineNum} {ReplaceTabs(parsed.Content)}"));
                i++;
            }
        }
        return string.Join("\n", result);
    }
}

/// <summary>Streaming bash (! command) execution display. Port of bash-execution.ts.</summary>
public sealed class BashExecutionComponent : Container, IExpandable
{
    private const int PreviewLines = 20;
    private readonly string _command;
    private readonly List<string> _outputLines = [];
    private string _status = "running";
    private int? _exitCode;
    private readonly Loader _loader;
    private TruncationResult? _truncationResult;
    private string? _fullOutputPath;
    private bool _expanded;
    private readonly Container _contentContainer = new();

    public BashExecutionComponent(string command, TuiBase ui, bool excludeFromContext = false)
    {
        _command = command;
        var colorKey = excludeFromContext ? "dim" : "bashMode";
        Func<string, string> borderColor = s => ThemeManager.Current.Fg(colorKey, s);
        AddChild(new Spacer(1));
        AddChild(new DynamicBorder(borderColor));
        AddChild(_contentContainer);
        _contentContainer.AddChild(new Text(ThemeManager.Current.Fg(colorKey, ThemeManager.Current.Bold($"$ {command}")), 1, 0));
        _loader = new Loader(ui, s => ThemeManager.Current.Fg(colorKey, s), t => ThemeManager.Current.Fg("muted", t), $"Running... ({KeyHints.KeyText("tui.select.cancel")} to cancel)");
        _contentContainer.AddChild(_loader);
        AddChild(new DynamicBorder(borderColor));
    }

    public void SetExpanded(bool expanded)
    {
        _expanded = expanded;
        UpdateDisplay();
    }

    public override void Invalidate()
    {
        base.Invalidate();
        UpdateDisplay();
    }

    public void AppendOutput(string chunk)
    {
        var clean = AnsiUtils.StripAnsi(chunk).Replace("\r\n", "\n").Replace('\r', '\n');
        var newLines = clean.Split('\n');
        if (_outputLines.Count > 0 && newLines.Length > 0)
        {
            _outputLines[^1] += newLines[0];
            _outputLines.AddRange(newLines.Skip(1));
        }
        else
        {
            _outputLines.AddRange(newLines);
        }
        UpdateDisplay();
    }

    public void SetComplete(int? exitCode, bool cancelled, TruncationResult? truncationResult = null, string? fullOutputPath = null)
    {
        _exitCode = exitCode;
        _status = cancelled ? "cancelled" : exitCode is not null and not 0 ? "error" : "complete";
        _truncationResult = truncationResult;
        _fullOutputPath = fullOutputPath;
        _loader.Stop();
        UpdateDisplay();
    }

    private void UpdateDisplay()
    {
        var theme = ThemeManager.Current;
        var contextTruncation = Truncate.TruncateTail(string.Join("\n", _outputLines), Truncate.DefaultMaxLines, Truncate.DefaultMaxBytes);
        var availableLines = contextTruncation.Content.Length > 0 ? contextTruncation.Content.Split('\n').ToList() : [];
        var previewLogical = availableLines.Skip(Math.Max(0, availableLines.Count - PreviewLines)).ToList();
        var hiddenLineCount = availableLines.Count - previewLogical.Count;

        _contentContainer.Clear();
        _contentContainer.AddChild(new Text(theme.Fg("bashMode", theme.Bold($"$ {_command}")), 1, 0));

        if (availableLines.Count > 0)
        {
            if (_expanded)
            {
                _contentContainer.AddChild(new Text("\n" + string.Join("\n", availableLines.Select(l => theme.Fg("muted", l))), 1, 0));
            }
            else
            {
                var styledInput = "\n" + string.Join("\n", previewLogical.Select(l => theme.Fg("muted", l)));
                _contentContainer.AddChild(new CachedRenderComponent(width => VisualTruncate.TruncateToVisualLines(styledInput, PreviewLines, width, 1).VisualLines));
            }
        }

        if (_status == "running")
        {
            _contentContainer.AddChild(_loader);
            return;
        }

        var statusParts = new List<string>();
        if (hiddenLineCount > 0)
        {
            statusParts.Add(_expanded
                ? $"{theme.Fg("muted", "(")}{KeyHints.KeyHint("app.tools.expand", "to collapse")}{theme.Fg("muted", ")")}"
                : $"{theme.Fg("muted", $"... {hiddenLineCount} more lines (")}{KeyHints.KeyHint("app.tools.expand", "to expand")}{theme.Fg("muted", ")")}");
        }
        if (_status == "cancelled") statusParts.Add(theme.Fg("warning", "(cancelled)"));
        else if (_status == "error") statusParts.Add(theme.Fg("error", $"(exit {_exitCode})"));
        if ((_truncationResult?.Truncated == true || contextTruncation.Truncated) && _fullOutputPath is not null)
        {
            statusParts.Add(theme.Fg("warning", $"Output truncated. Full output: {_fullOutputPath}"));
        }
        if (statusParts.Count > 0) _contentContainer.AddChild(new Text("\n" + string.Join("\n", statusParts), 1, 0));
    }

    public string GetOutput() => string.Join("\n", _outputLines);

    public string GetCommand() => _command;
}

/// <summary>Component rendering through a width-cached callback.</summary>
public sealed class CachedRenderComponent(Func<int, List<string>> render) : IComponent
{
    private int? _cachedWidth;
    private List<string>? _cachedLines;

    public List<string> Render(int width)
    {
        if (_cachedLines is null || _cachedWidth != width)
        {
            _cachedLines = render(width);
            _cachedWidth = width;
        }
        return _cachedLines;
    }

    public void Invalidate()
    {
        _cachedWidth = null;
        _cachedLines = null;
    }
}
