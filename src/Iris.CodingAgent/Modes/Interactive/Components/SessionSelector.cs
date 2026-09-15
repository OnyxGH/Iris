using System.Text.RegularExpressions;
using Iris.CodingAgent.Core;
using Iris.CodingAgent.Utils;
using Iris.Tui;
using Iris.Tui.Components;
using Iris.Tui.Markdown;

namespace Iris.CodingAgent.Modes.Interactive.Components;

/// <summary>Session search parsing and ranking.</summary>
public static partial class SessionSelectorSearch
{
    public sealed record ParsedSearchQuery(string Mode, List<(string Kind, string Value)> Tokens, Regex? Regex, string? Error = null);

    [GeneratedRegex(@"[\t\n\x0B\f\r    -     　﻿]+")]
    private static partial Regex Whitespace();

    private static string NormalizeWhitespaceLower(string text) => Whitespace().Replace(text.ToLowerInvariant(), " ").Trim();

    private static string SearchText(SessionInfo session) => $"{session.Id} {session.Name ?? ""} {session.AllMessagesText} {session.Cwd}";

    public static bool HasSessionName(SessionInfo session) => !string.IsNullOrWhiteSpace(session.Name);

    public static ParsedSearchQuery Parse(string query)
    {
        var trimmed = query.Trim();
        if (trimmed.Length == 0) return new ParsedSearchQuery("tokens", [], null);

        if (trimmed.StartsWith("re:", StringComparison.Ordinal))
        {
            var pattern = trimmed[3..].Trim();
            if (pattern.Length == 0) return new ParsedSearchQuery("regex", [], null, "Empty regex");
            try
            {
                return new ParsedSearchQuery("regex", [], JsRegex.Create(pattern, "i"));
            }
            catch (Exception ex)
            {
                return new ParsedSearchQuery("regex", [], null, ex.Message);
            }
        }

        var tokens = new List<(string, string)>();
        var buf = "";
        var inQuote = false;
        void Flush(string kind)
        {
            var v = buf.Trim();
            buf = "";
            if (v.Length > 0) tokens.Add((kind, v));
        }

        foreach (var ch in trimmed)
        {
            if (ch == '"')
            {
                if (inQuote)
                {
                    Flush("phrase");
                    inQuote = false;
                }
                else
                {
                    Flush("fuzzy");
                    inQuote = true;
                }
                continue;
            }
            if (!inQuote && TextUtils.IsJsWhitespace(ch))
            {
                Flush("fuzzy");
                continue;
            }
            buf += ch;
        }

        if (inQuote)
        {
            return new ParsedSearchQuery("tokens", Whitespace().Split(trimmed).Select(t => t.Trim()).Where(t => t.Length > 0).Select(t => ("fuzzy", t)).ToList(), null);
        }
        Flush("fuzzy");
        return new ParsedSearchQuery("tokens", tokens, null);
    }

    public static (bool Matches, double Score) Match(SessionInfo session, ParsedSearchQuery parsed)
    {
        var text = SearchText(session);
        if (parsed.Mode == "regex")
        {
            if (parsed.Regex is null) return (false, 0);
            var m = parsed.Regex.Match(text);
            return m.Success ? (true, m.Index * 0.1) : (false, 0);
        }
        if (parsed.Tokens.Count == 0) return (true, 0);

        double total = 0;
        string? normalized = null;
        foreach (var (kind, value) in parsed.Tokens)
        {
            if (kind == "phrase")
            {
                normalized ??= NormalizeWhitespaceLower(text);
                var phrase = NormalizeWhitespaceLower(value);
                if (phrase.Length == 0) continue;
                var idx = normalized.IndexOf(phrase, StringComparison.Ordinal);
                if (idx < 0) return (false, 0);
                total += idx * 0.1;
                continue;
            }
            var match = Fuzzy.Match(value, text);
            if (!match.Matches) return (false, 0);
            total += match.Score;
        }
        return (true, total);
    }

    public static List<SessionInfo> FilterAndSort(List<SessionInfo> sessions, string query, string sortMode, string nameFilter = "all")
    {
        var nameFiltered = nameFilter == "all" ? sessions : sessions.Where(HasSessionName).ToList();
        if (query.Trim().Length == 0) return nameFiltered;
        var parsed = Parse(query);
        if (parsed.Error is not null) return [];
        if (sortMode == "recent") return nameFiltered.Where(s => Match(s, parsed).Matches).ToList();
        return nameFiltered
            .Select(s => (Session: s, Result: Match(s, parsed)))
            .Where(x => x.Result.Matches)
            .OrderBy(x => x.Result.Score)
            .ThenByDescending(x => x.Session.Modified)
            .Select(x => x.Session)
            .ToList();
    }
}

/// <summary>Resume session selector.</summary>
public sealed partial class SessionSelectorComponent : Container, IInputComponent, IFocusable
{
    public delegate Task<List<SessionInfo>> SessionsLoader(Action<int, int>? onProgress);

    private static string ShortenPath(string path)
    {
        var home = Iris.CodingAgent.Config.AppConfig.HomeDir;
        if (string.IsNullOrEmpty(path)) return path;
        return path.StartsWith(home, StringComparison.Ordinal) ? "~" + path[home.Length..] : path;
    }

    private static string FormatSessionDate(DateTimeOffset date)
    {
        var diff = DateTimeOffset.UtcNow - date;
        var mins = (long)Math.Floor(diff.TotalMilliseconds / 60000);
        var hours = (long)Math.Floor(diff.TotalMilliseconds / 3600000);
        var days = (long)Math.Floor(diff.TotalMilliseconds / 86400000);
        if (mins < 1) return "now";
        if (mins < 60) return $"{mins}m";
        if (hours < 24) return $"{hours}h";
        if (days < 7) return $"{days}d";
        if (days < 30) return $"{days / 7}w";
        if (days < 365) return $"{days / 30}mo";
        return $"{days / 365}y";
    }

    private static string? Canonicalize(string? path) => string.IsNullOrEmpty(path) ? path : PathUtils.CanonicalizePath(path);

    private sealed class Header(string scope, string sortMode, string nameFilter, Action requestRender) : IComponent
    {
        public string Scope = scope;
        public string SortMode = sortMode;
        public string NameFilter = nameFilter;
        private bool _loading;
        private (int Loaded, int Total)? _progress;
        public bool ShowPath;
        public string? ConfirmingDeletePath;
        private (string Type, string Message)? _status;
        private IDisposable? _statusTimeout;
        public bool ShowRenameHint;

        public void SetLoading(bool loading)
        {
            _loading = loading;
            _progress = null;
        }

        public void SetProgress(int loaded, int total) => _progress = (loaded, total);

        public void SetStatusMessage((string Type, string Message)? message, int? autoHideMs = null)
        {
            _statusTimeout?.Dispose();
            _statusTimeout = null;
            _status = message;
            if (message is null || autoHideMs is null) return;
            _statusTimeout = UiDispatcher.Current?.SetTimeout(() =>
            {
                _status = null;
                _statusTimeout = null;
                requestRender();
            }, autoHideMs.Value);
        }

        public void Invalidate()
        {
        }

        public List<string> Render(int width)
        {
            var theme = ThemeManager.Current;
            var left0 = theme.Bold(Scope == "current" ? "Resume Session (Current Folder)" : "Resume Session (All)");
            var sortText = theme.Fg("muted", "Sort: ") + theme.Fg("accent", SortMode == "threaded" ? "Threaded" : SortMode == "recent" ? "Recent" : "Fuzzy");
            var nameText = theme.Fg("muted", "Name: ") + theme.Fg("accent", NameFilter == "all" ? "All" : "Named");
            string scopeText;
            if (_loading)
            {
                var progress = _progress is { } p ? $"{p.Loaded}/{p.Total}" : "...";
                scopeText = $"{theme.Fg("muted", "○ Current Folder | ")}{theme.Fg("accent", $"Loading {progress}")}";
            }
            else if (Scope == "current")
            {
                scopeText = $"{theme.Fg("accent", "◉ Current Folder")}{theme.Fg("muted", " | ○ All")}";
            }
            else
            {
                scopeText = $"{theme.Fg("muted", "○ Current Folder | ")}{theme.Fg("accent", "◉ All")}";
            }

            var rightText = TextUtils.TruncateToWidth($"{scopeText}  {nameText}  {sortText}", width, "");
            var availableLeft = Math.Max(0, width - TextUtils.VisibleWidth(rightText) - 1);
            var left = TextUtils.TruncateToWidth(left0, availableLeft, "");
            var spacing = Math.Max(0, width - TextUtils.VisibleWidth(left) - TextUtils.VisibleWidth(rightText));

            string hint1, hint2;
            if (ConfirmingDeletePath is not null)
            {
                hint1 = theme.Fg("error", TextUtils.TruncateToWidth($"Delete session? {KeyHints.KeyHint("tui.select.confirm", "confirm")} · {KeyHints.KeyHint("tui.select.cancel", "cancel")}", width, "…"));
                hint2 = "";
            }
            else if (_status is { } status)
            {
                hint1 = theme.Fg(status.Type == "error" ? "error" : "accent", TextUtils.TruncateToWidth(status.Message, width, "…"));
                hint2 = "";
            }
            else
            {
                var sep = theme.Fg("muted", " · ");
                var h1 = KeyHints.KeyHint("tui.input.tab", "scope") + sep + theme.Fg("muted", "re:<pattern> regex · \"phrase\" exact");
                var parts = new List<string>
                {
                    KeyHints.KeyHint("app.session.toggleSort", "sort"),
                    KeyHints.KeyHint("app.session.toggleNamedFilter", "named"),
                    KeyHints.KeyHint("app.session.delete", "delete"),
                    KeyHints.KeyHint("app.session.togglePath", $"path {(ShowPath ? "(on)" : "(off)")}"),
                };
                if (ShowRenameHint) parts.Add(KeyHints.KeyHint("app.session.rename", "rename"));
                hint1 = TextUtils.TruncateToWidth(h1, width, "…");
                hint2 = TextUtils.TruncateToWidth(string.Join(sep, parts), width, "…");
            }
            return [$"{left}{new string(' ', spacing)}{rightText}", hint1, hint2];
        }
    }

    private sealed class TreeNode(SessionInfo session)
    {
        public SessionInfo Session { get; } = session;
        public List<TreeNode> Children { get; } = [];
        public long LatestActivity = session.Modified.ToUnixTimeMilliseconds();
    }

    private sealed record FlatNode(SessionInfo Session, int Depth, bool IsLast, List<bool> AncestorContinues);

    private static List<TreeNode> BuildTree(List<SessionInfo> sessions)
    {
        var byPath = new Dictionary<string, TreeNode>();
        foreach (var session in sessions) byPath[Canonicalize(session.Path) ?? session.Path] = new TreeNode(session);
        var roots = new List<TreeNode>();
        foreach (var session in sessions)
        {
            var node = byPath[Canonicalize(session.Path) ?? session.Path];
            var parentPath = Canonicalize(session.ParentSessionPath);
            if (!string.IsNullOrEmpty(parentPath) && byPath.TryGetValue(parentPath, out var parent)) parent.Children.Add(node);
            else roots.Add(node);
        }
        long Update(TreeNode node)
        {
            var latest = node.Session.Modified.ToUnixTimeMilliseconds();
            foreach (var child in node.Children) latest = Math.Max(latest, Update(child));
            node.LatestActivity = latest;
            return latest;
        }
        foreach (var root in roots) Update(root);
        List<TreeNode> SortNodes(List<TreeNode> nodes)
        {
            var sorted = nodes.OrderByDescending(n => n.LatestActivity).ToList();
            nodes.Clear();
            nodes.AddRange(sorted);
            foreach (var node in nodes) SortNodes(node.Children);
            return nodes;
        }
        return SortNodes(roots);
    }

    private static List<FlatNode> Flatten(List<TreeNode> roots)
    {
        var result = new List<FlatNode>();
        void Walk(TreeNode node, int depth, List<bool> ancestorContinues, bool isLast)
        {
            result.Add(new FlatNode(node.Session, depth, isLast, ancestorContinues));
            for (var i = 0; i < node.Children.Count; i++)
            {
                var continues = depth > 0 && !isLast;
                Walk(node.Children[i], depth + 1, [.. ancestorContinues, continues], i == node.Children.Count - 1);
            }
        }
        for (var i = 0; i < roots.Count; i++) Walk(roots[i], 0, [], i == roots.Count - 1);
        return result;
    }

    [GeneratedRegex(@"[\x00-\x1f\x7f]")]
    private static partial Regex ControlChars();

    public sealed class SessionList : IInputComponent, IFocusable
    {
        private List<SessionInfo> _allSessions;
        private List<FlatNode> _filtered = [];
        private int _selectedIndex;
        private readonly Input _searchInput = new();
        private bool _showCwd;
        private string _sortMode;
        private string _nameFilter;
        private readonly KeybindingsManager _keybindings;
        private bool _showPath;
        private string? _confirmingDeletePath;
        private readonly string? _currentSessionCanonicalPath;
        private const int MaxVisible = 10;
        private bool _focused;

        public Action<string>? OnSelect;
        public Action? OnCancel;
        public Action OnExit = () => { };
        public Action? OnToggleScope;
        public Action? OnToggleSort;
        public Action? OnToggleNameFilter;
        public Action<bool>? OnTogglePath;
        public Action<string?>? OnDeleteConfirmationChange;
        public Func<string, Task>? OnDeleteSession;
        public Action<string>? OnRenameSession;
        public Action<string>? OnError;

        public bool Focused
        {
            get => _focused;
            set
            {
                _focused = value;
                _searchInput.Focused = value;
            }
        }

        internal SessionList(List<SessionInfo> sessions, bool showCwd, string sortMode, string nameFilter, KeybindingsManager keybindings, string? currentSessionFilePath)
        {
            _allSessions = sessions;
            _showCwd = showCwd;
            _sortMode = sortMode;
            _nameFilter = nameFilter;
            _keybindings = keybindings;
            _currentSessionCanonicalPath = Canonicalize(currentSessionFilePath);
            FilterSessions("");
            _searchInput.OnSubmit = _ =>
            {
                if (_selectedIndex < _filtered.Count) OnSelect?.Invoke(_filtered[_selectedIndex].Session.Path);
            };
        }

        public string? GetSelectedSessionPath() => _selectedIndex < _filtered.Count ? _filtered[_selectedIndex].Session.Path : null;

        public void SetSortMode(string sortMode)
        {
            _sortMode = sortMode;
            FilterSessions(_searchInput.GetValue());
        }

        public void SetNameFilter(string nameFilter)
        {
            _nameFilter = nameFilter;
            FilterSessions(_searchInput.GetValue());
        }

        public void SetSessions(List<SessionInfo> sessions, bool showCwd)
        {
            _allSessions = sessions;
            _showCwd = showCwd;
            FilterSessions(_searchInput.GetValue());
        }

        private void FilterSessions(string query)
        {
            var nameFiltered = _nameFilter == "all" ? _allSessions : _allSessions.Where(SessionSelectorSearch.HasSessionName).ToList();
            if (_sortMode == "threaded" && query.Trim().Length == 0)
            {
                _filtered = Flatten(BuildTree(nameFiltered));
            }
            else
            {
                _filtered = SessionSelectorSearch.FilterAndSort(nameFiltered, query, _sortMode).Select(s => new FlatNode(s, 0, true, [])).ToList();
            }
            _selectedIndex = Math.Min(_selectedIndex, Math.Max(0, _filtered.Count - 1));
        }

        private void SetConfirmingDeletePath(string? path)
        {
            _confirmingDeletePath = path;
            OnDeleteConfirmationChange?.Invoke(path);
        }

        private void StartDeleteConfirmation()
        {
            if (_selectedIndex >= _filtered.Count) return;
            var selected = _filtered[_selectedIndex];
            if (IsCurrentSessionPath(selected.Session.Path))
            {
                OnError?.Invoke("Cannot delete the currently active session");
                return;
            }
            SetConfirmingDeletePath(selected.Session.Path);
        }

        private bool IsCurrentSessionPath(string path) => _currentSessionCanonicalPath is not null && (Canonicalize(path) ?? path) == _currentSessionCanonicalPath;

        public void Invalidate()
        {
        }

        public List<string> Render(int width)
        {
            var theme = ThemeManager.Current;
            var lines = new List<string>();
            lines.AddRange(_searchInput.Render(width));
            lines.Add("");

            if (_filtered.Count == 0)
            {
                string emptyMessage;
                if (_nameFilter == "named")
                {
                    var toggleKey = KeyHints.KeyText("app.session.toggleNamedFilter");
                    emptyMessage = _showCwd ? $"  No named sessions found. Press {toggleKey} to show all." : $"  No named sessions in current folder. Press {toggleKey} to show all, or Tab to view all.";
                }
                else
                {
                    emptyMessage = _showCwd ? "  No sessions found" : "  No sessions in current folder. Press Tab to view all.";
                }
                lines.Add(theme.Fg("muted", TextUtils.TruncateToWidth(emptyMessage, width, "…")));
                return lines;
            }

            var start = Math.Max(0, Math.Min(_selectedIndex - MaxVisible / 2, _filtered.Count - MaxVisible));
            var end = Math.Min(start + MaxVisible, _filtered.Count);
            for (var i = start; i < end; i++)
            {
                var node = _filtered[i];
                var session = node.Session;
                var selected = i == _selectedIndex;
                var confirmingDelete = session.Path == _confirmingDeletePath;
                var isCurrent = IsCurrentSessionPath(session.Path);
                var prefix = node.Depth == 0 ? "" : string.Concat(node.AncestorContinues.Select(c => c ? "│  " : "   ")) + (node.IsLast ? "└─ " : "├─ ");
                var hasName = session.Name is not null;
                var normalized = ControlChars().Replace(session.Name ?? session.FirstMessage, " ").Trim();

                var rightPart = $"{session.MessageCount} {FormatSessionDate(session.Modified)}";
                if (_showCwd && !string.IsNullOrEmpty(session.Cwd)) rightPart = $"{ShortenPath(session.Cwd)} {rightPart}";
                if (_showPath) rightPart = $"{ShortenPath(session.Path)} {rightPart}";

                var cursor = selected ? theme.Fg("accent", "› ") : "  ";
                var availableForMsg = width - 2 - TextUtils.VisibleWidth(prefix) - (TextUtils.VisibleWidth(rightPart) + 2);
                var truncatedMsg = TextUtils.TruncateToWidth(normalized, Math.Max(10, availableForMsg), "…");
                var color = confirmingDelete ? "error" : isCurrent ? "accent" : hasName ? "warning" : null;
                var styledMsg = color is not null ? theme.Fg(color, truncatedMsg) : truncatedMsg;
                if (selected) styledMsg = theme.Bold(styledMsg);

                var leftPart = cursor + theme.Fg("dim", prefix) + styledMsg;
                var spacing = Math.Max(1, width - TextUtils.VisibleWidth(leftPart) - TextUtils.VisibleWidth(rightPart));
                var line = leftPart + new string(' ', spacing) + theme.Fg(confirmingDelete ? "error" : "dim", rightPart);
                if (selected) line = theme.Bg("selectedBg", line);
                lines.Add(TextUtils.TruncateToWidth(line, width));
            }

            if (start > 0 || end < _filtered.Count)
            {
                lines.Add(theme.Fg("muted", TextUtils.TruncateToWidth($"  ({_selectedIndex + 1}/{_filtered.Count})", width, "")));
            }
            return lines;
        }

        public void HandleInput(string keyData)
        {
            var kb = KeybindingsManager.Global;
            if (_confirmingDeletePath is not null)
            {
                if (kb.Matches(keyData, "tui.select.confirm"))
                {
                    var path = _confirmingDeletePath;
                    SetConfirmingDeletePath(null);
                    _ = OnDeleteSession?.Invoke(path);
                }
                else if (kb.Matches(keyData, "tui.select.cancel"))
                {
                    SetConfirmingDeletePath(null);
                }
                return;
            }

            if (kb.Matches(keyData, "tui.input.tab"))
            {
                OnToggleScope?.Invoke();
                return;
            }
            if (kb.Matches(keyData, "app.session.toggleSort"))
            {
                OnToggleSort?.Invoke();
                return;
            }
            if (_keybindings.Matches(keyData, "app.session.toggleNamedFilter"))
            {
                OnToggleNameFilter?.Invoke();
                return;
            }
            if (kb.Matches(keyData, "app.session.togglePath"))
            {
                _showPath = !_showPath;
                OnTogglePath?.Invoke(_showPath);
                return;
            }
            if (kb.Matches(keyData, "app.session.delete"))
            {
                StartDeleteConfirmation();
                return;
            }
            if (kb.Matches(keyData, "app.session.rename"))
            {
                if (_selectedIndex < _filtered.Count) OnRenameSession?.Invoke(_filtered[_selectedIndex].Session.Path);
                return;
            }
            if (kb.Matches(keyData, "app.session.deleteNoninvasive"))
            {
                if (_searchInput.GetValue().Length > 0)
                {
                    _searchInput.HandleInput(keyData);
                    FilterSessions(_searchInput.GetValue());
                    return;
                }
                StartDeleteConfirmation();
                return;
            }

            if (kb.Matches(keyData, "tui.select.up")) _selectedIndex = Math.Max(0, _selectedIndex - 1);
            else if (kb.Matches(keyData, "tui.select.down")) _selectedIndex = Math.Min(_filtered.Count - 1, _selectedIndex + 1);
            else if (kb.Matches(keyData, "tui.select.pageUp")) _selectedIndex = Math.Max(0, _selectedIndex - MaxVisible);
            else if (kb.Matches(keyData, "tui.select.pageDown")) _selectedIndex = Math.Min(_filtered.Count - 1, _selectedIndex + MaxVisible);
            else if (kb.Matches(keyData, "tui.select.confirm"))
            {
                if (_selectedIndex < _filtered.Count) OnSelect?.Invoke(_filtered[_selectedIndex].Session.Path);
            }
            else if (kb.Matches(keyData, "tui.select.cancel")) OnCancel?.Invoke();
            else
            {
                _searchInput.HandleInput(keyData);
                FilterSessions(_searchInput.GetValue());
            }
        }
    }

    private static async Task<(bool Ok, string Method, string? Error)> DeleteSessionFileAsync(string sessionPath)
    {
        string? trashError = null;
        var trashOk = false;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("trash") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
            if (sessionPath.StartsWith('-')) psi.ArgumentList.Add("--");
            psi.ArgumentList.Add(sessionPath);
            using var process = System.Diagnostics.Process.Start(psi);
            if (process is not null)
            {
                var stderr = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                trashOk = process.ExitCode == 0;
                if (!trashOk && stderr.Trim().Length > 0) trashError = stderr.Trim().Split('\n')[0];
            }
        }
        catch (Exception ex)
        {
            trashError = ex.Message;
        }

        if (trashOk || !File.Exists(sessionPath)) return (true, "trash", null);
        try
        {
            File.Delete(sessionPath);
            return (true, "unlink", null);
        }
        catch (Exception ex)
        {
            var hint = trashError is not null ? $"trash: {(trashError.Length > 200 ? trashError[..200] : trashError)}" : null;
            return (false, "unlink", hint is not null ? $"{ex.Message} ({hint})" : ex.Message);
        }
    }

    private readonly bool _canRename;
    private readonly SessionList _sessionList;
    private readonly Header _header;
    private string _scope = "current";
    private string _sortMode = "threaded";
    private string _nameFilter = "all";
    private List<SessionInfo>? _currentSessions;
    private List<SessionInfo>? _allSessions;
    private readonly SessionsLoader _currentSessionsLoader;
    private readonly SessionsLoader _allSessionsLoader;
    private readonly Action _requestRender;
    private readonly Func<string, string?, Task>? _renameSession;
    private bool _currentLoading;
    private bool _allLoading;
    private int _allLoadSeq;
    private string _mode = "list";
    private readonly Input _renameInput = new();
    private string? _renameTargetPath;
    private bool _focused;

    public bool Focused
    {
        get => _focused;
        set
        {
            _focused = value;
            _sessionList.Focused = value;
            _renameInput.Focused = value;
        }
    }

    public SessionSelectorComponent(SessionsLoader currentSessionsLoader, SessionsLoader allSessionsLoader, Action<string> onSelect, Action onCancel, Action onExit, Action requestRender,
        Func<string, string?, Task>? renameSession = null, bool? showRenameHint = null, KeybindingsManager? keybindings = null, string? currentSessionFilePath = null)
    {
        var kb = keybindings ?? AppKeybindings.Create();
        _currentSessionsLoader = currentSessionsLoader;
        _allSessionsLoader = allSessionsLoader;
        _requestRender = requestRender;
        _header = new Header(_scope, _sortMode, _nameFilter, requestRender);
        _renameSession = renameSession;
        _canRename = renameSession is not null;
        _header.ShowRenameHint = showRenameHint ?? _canRename;
        _sessionList = new SessionList([], false, _sortMode, _nameFilter, kb, currentSessionFilePath);
        BuildBaseLayout(_sessionList);

        _renameInput.OnSubmit = value => _ = ConfirmRenameAsync(value);

        void ClearStatus() => _header.SetStatusMessage(null);
        _sessionList.OnSelect = path =>
        {
            ClearStatus();
            onSelect(path);
        };
        _sessionList.OnCancel = () =>
        {
            ClearStatus();
            onCancel();
        };
        _sessionList.OnExit = () =>
        {
            ClearStatus();
            onExit();
        };
        _sessionList.OnToggleScope = ToggleScope;
        _sessionList.OnToggleSort = ToggleSortMode;
        _sessionList.OnToggleNameFilter = ToggleNameFilter;
        _sessionList.OnRenameSession = path =>
        {
            if (renameSession is null) return;
            if (_scope == "current" && _currentLoading) return;
            if (_scope == "all" && _allLoading) return;
            var sessions = _scope == "all" ? _allSessions ?? [] : _currentSessions ?? [];
            EnterRenameMode(path, sessions.FirstOrDefault(s => s.Path == path)?.Name);
        };
        _sessionList.OnTogglePath = showPath =>
        {
            _header.ShowPath = showPath;
            _requestRender();
        };
        _sessionList.OnDeleteConfirmationChange = path =>
        {
            _header.ConfirmingDeletePath = path;
            _requestRender();
        };
        _sessionList.OnError = message =>
        {
            _header.SetStatusMessage(("error", message), 3000);
            _requestRender();
        };
        _sessionList.OnDeleteSession = async path =>
        {
            var result = await DeleteSessionFileAsync(path);
            if (result.Ok)
            {
                _currentSessions = _currentSessions?.Where(s => s.Path != path).ToList();
                _allSessions = _allSessions?.Where(s => s.Path != path).ToList();
                _sessionList.SetSessions(_scope == "all" ? _allSessions ?? [] : _currentSessions ?? [], _scope == "all");
                _header.SetStatusMessage(("info", result.Method == "trash" ? "Session moved to trash" : "Session deleted"), 2000);
                await LoadScopeAsync(_scope, "refresh");
            }
            else
            {
                _header.SetStatusMessage(("error", $"Failed to delete: {result.Error ?? "Unknown error"}"), 3000);
            }
            _requestRender();
        };

        _ = LoadScopeAsync("current", "initial");
    }

    public void HandleInput(string data)
    {
        if (_mode == "rename")
        {
            if (KeybindingsManager.Global.Matches(data, "tui.select.cancel"))
            {
                ExitRenameMode();
                return;
            }
            _renameInput.HandleInput(data);
            return;
        }
        _sessionList.HandleInput(data);
    }

    private void BuildBaseLayout(IComponent content, bool showHeader = true)
    {
        Clear();
        AddChild(new Spacer(1));
        AddChild(new DynamicBorder(s => ThemeManager.Current.Fg("accent", s)));
        AddChild(new Spacer(1));
        if (showHeader)
        {
            AddChild(_header);
            AddChild(new Spacer(1));
        }
        AddChild(content);
        AddChild(new Spacer(1));
        AddChild(new DynamicBorder(s => ThemeManager.Current.Fg("accent", s)));
    }

    private void EnterRenameMode(string sessionPath, string? currentName)
    {
        _mode = "rename";
        _renameTargetPath = sessionPath;
        _renameInput.SetValue(currentName ?? "");
        _renameInput.Focused = true;
        var theme = ThemeManager.Current;
        var panel = new Container();
        panel.AddChild(new Text(theme.Bold("Rename Session"), 1, 0));
        panel.AddChild(new Spacer(1));
        panel.AddChild(_renameInput);
        panel.AddChild(new Spacer(1));
        panel.AddChild(new Text(theme.Fg("muted", $"{KeyHints.KeyText("tui.select.confirm")} to save · {KeyHints.KeyText("tui.select.cancel")} to cancel"), 1, 0));
        BuildBaseLayout(panel, false);
        _requestRender();
    }

    private void ExitRenameMode()
    {
        _mode = "list";
        _renameTargetPath = null;
        BuildBaseLayout(_sessionList);
        _requestRender();
    }

    private async Task ConfirmRenameAsync(string value)
    {
        var next = value.Trim();
        if (next.Length == 0) return;
        if (_renameTargetPath is not { } target || _renameSession is null)
        {
            ExitRenameMode();
            return;
        }
        try
        {
            await _renameSession(target, next);
            await LoadScopeAsync(_scope, "refresh");
        }
        finally
        {
            ExitRenameMode();
        }
    }

    private async Task LoadScopeAsync(string scope, string reason)
    {
        var showCwd = scope == "all";
        if (scope == "current") _currentLoading = true;
        else _allLoading = true;
        int? seq = scope == "all" ? ++_allLoadSeq : null;
        _header.Scope = scope;
        _header.SetLoading(true);
        _requestRender();

        var dispatcher = UiDispatcher.Current;
        void OnProgress(int loaded, int total)
        {
            void Apply()
            {
                if (scope != _scope) return;
                if (seq is not null && seq != _allLoadSeq) return;
                _header.SetProgress(loaded, total);
                _requestRender();
            }
            if (dispatcher is not null) dispatcher.Post(Apply);
            else Apply();
        }

        try
        {
            var sessions = await (scope == "current" ? _currentSessionsLoader(OnProgress) : _allSessionsLoader(OnProgress));
            if (scope == "current")
            {
                _currentSessions = sessions;
                _currentLoading = false;
            }
            else
            {
                _allSessions = sessions;
                _allLoading = false;
            }
            if (scope != _scope || (seq is not null && seq != _allLoadSeq)) return;
            _header.SetLoading(false);
            _sessionList.SetSessions(sessions, showCwd);
            _requestRender();
        }
        catch (Exception ex)
        {
            if (scope == "current") _currentLoading = false;
            else _allLoading = false;
            if (scope != _scope || (seq is not null && seq != _allLoadSeq)) return;
            _header.SetLoading(false);
            _header.SetStatusMessage(("error", $"Failed to load sessions: {ex.Message}"), 4000);
            if (reason == "initial") _sessionList.SetSessions([], showCwd);
            _requestRender();
        }
    }

    private void ToggleSortMode()
    {
        _sortMode = _sortMode == "threaded" ? "recent" : _sortMode == "recent" ? "relevance" : "threaded";
        _header.SortMode = _sortMode;
        _sessionList.SetSortMode(_sortMode);
        _requestRender();
    }

    private void ToggleNameFilter()
    {
        _nameFilter = _nameFilter == "all" ? "named" : "all";
        _header.NameFilter = _nameFilter;
        _sessionList.SetNameFilter(_nameFilter);
        _requestRender();
    }

    private void ToggleScope()
    {
        if (_scope == "current")
        {
            _scope = "all";
            _header.Scope = _scope;
            if (_allSessions is not null)
            {
                _header.SetLoading(false);
                _sessionList.SetSessions(_allSessions, true);
                _requestRender();
                return;
            }
            if (!_allLoading) _ = LoadScopeAsync("all", "toggle");
            return;
        }
        _scope = "current";
        _header.Scope = _scope;
        _header.SetLoading(_currentLoading);
        _sessionList.SetSessions(_currentSessions ?? [], false);
        _requestRender();
    }

    public SessionList GetSessionList() => _sessionList;
}
