using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.Ai;
using PiSharp.CodingAgent.Core;
using PiSharp.Tui;
using PiSharp.Tui.Components;

namespace PiSharp.CodingAgent.Modes.Interactive.Components;

/// <summary>Session tree navigator. Port of tree-selector.ts.</summary>
public sealed partial class TreeSelectorComponent : Container, IInputComponent, IFocusable
{
    private sealed record GutterInfo(int Position, bool Show);

    private sealed class FlatNode(SessionTreeNode node)
    {
        public SessionTreeNode Node { get; } = node;
        public int Indent;
        public bool ShowConnector;
        public bool IsLast;
        public List<GutterInfo> Gutters = [];
        public bool IsVirtualRootChild;
    }

    private sealed record ViewportRow(string Gutter, string Body, int AnchorCol, int BodyWidth, bool IsSelected);

    private const int TreeGutterWidth = 2;

    private static List<string> RenderHorizontalViewport(List<ViewportRow> rows, int width)
    {
        var viewportWidth = Math.Max(0, width - TreeGutterWidth);
        var maxBodyWidth = rows.Count == 0 ? 0 : rows.Max(r => r.BodyWidth);
        var maxScroll = Math.Max(0, maxBodyWidth - viewportWidth);
        var selected = rows.FirstOrDefault(r => r.IsSelected);
        var scroll = 0;
        if (selected is not null && maxScroll > 0)
        {
            var minVisible = Math.Min(20, Math.Max(4, viewportWidth / 3));
            if (selected.AnchorCol > viewportWidth - minVisible)
            {
                var context = Math.Min(12, Math.Max(2, viewportWidth / 4));
                scroll = Math.Min(maxScroll, selected.AnchorCol - context);
            }
        }
        return rows.Select(row =>
        {
            var line = scroll > 0 ? $"{row.Gutter}{TextUtils.SliceByColumn(row.Body, scroll, viewportWidth, true)}\e[0m" : row.Gutter + row.Body;
            return TextUtils.TruncateToWidth(line, width, "");
        }).ToList();
    }

    private static string? ParentIdOf(SessionTreeNode node) => node.Entry.ParentId;

    private sealed partial class TreeList : IInputComponent
    {
        private readonly List<FlatNode> _flatNodes;
        private List<FlatNode> _filteredNodes = [];
        private int _selectedIndex;
        private readonly string? _currentLeafId;
        private readonly int _maxVisibleLines;
        private string _filterMode;
        private string _searchQuery = "";
        private readonly Dictionary<string, (string Name, JsonObject Arguments)> _toolCallMap = [];
        private bool _multipleRoots;
        private bool _showLabelTimestamps;
        private readonly HashSet<string> _activePathIds = [];
        private Dictionary<string, string?> _visibleParentMap = [];
        private Dictionary<string, List<string>> _visibleChildrenMap = [];
        private List<string> _visibleRoots = [];
        private string? _lastSelectedId;
        private readonly HashSet<string> _foldedNodes = [];

        public Action<string>? OnSelect;
        public Action? OnCancel;
        public Action<string?>? OnCopy;
        public Action<string, string?>? OnLabelEdit;

        public TreeList(List<SessionTreeNode> tree, string? currentLeafId, int maxVisibleLines, string? initialSelectedId, string? initialFilterMode)
        {
            _currentLeafId = currentLeafId;
            _maxVisibleLines = maxVisibleLines;
            _filterMode = initialFilterMode ?? "default";
            _multipleRoots = tree.Count > 1;
            _flatNodes = FlattenTree(tree);
            BuildActivePath();
            ApplyFilter();
            _selectedIndex = FindNearestVisibleIndex(initialSelectedId ?? currentLeafId);
            _lastSelectedId = _selectedIndex < _filteredNodes.Count ? _filteredNodes[_selectedIndex].Node.Entry.Id : null;
        }

        private int FindNearestVisibleIndex(string? entryId)
        {
            if (_filteredNodes.Count == 0) return 0;
            var entryMap = _flatNodes.ToDictionary(n => n.Node.Entry.Id);
            var visible = new Dictionary<string, int>();
            for (var i = 0; i < _filteredNodes.Count; i++) visible[_filteredNodes[i].Node.Entry.Id] = i;
            var currentId = entryId;
            while (currentId is not null)
            {
                if (visible.TryGetValue(currentId, out var index)) return index;
                if (!entryMap.TryGetValue(currentId, out var node)) break;
                currentId = ParentIdOf(node.Node);
            }
            return _filteredNodes.Count - 1;
        }

        private void BuildActivePath()
        {
            _activePathIds.Clear();
            if (_currentLeafId is null) return;
            var entryMap = _flatNodes.ToDictionary(n => n.Node.Entry.Id);
            var currentId = _currentLeafId;
            while (!string.IsNullOrEmpty(currentId))
            {
                _activePathIds.Add(currentId);
                if (!entryMap.TryGetValue(currentId, out var node)) break;
                currentId = ParentIdOf(node.Node);
            }
        }

        private List<FlatNode> FlattenTree(List<SessionTreeNode> roots)
        {
            var result = new List<FlatNode>();
            _toolCallMap.Clear();

            var containsActive = new Dictionary<SessionTreeNode, bool>(ReferenceEqualityComparer.Instance);
            var all = new List<SessionTreeNode>();
            var pre = new Stack<SessionTreeNode>(roots.AsEnumerable().Reverse());
            // Match JS: pop from the end of [...roots], pushing children reversed.
            pre.Clear();
            foreach (var r in roots) pre.Push(r);
            while (pre.Count > 0)
            {
                var node = pre.Pop();
                all.Add(node);
                for (var i = node.Children.Count - 1; i >= 0; i--) pre.Push(node.Children[i]);
            }
            for (var i = all.Count - 1; i >= 0; i--)
            {
                var node = all[i];
                var has = _currentLeafId is not null && node.Entry.Id == _currentLeafId;
                foreach (var child in node.Children)
                {
                    if (containsActive.GetValueOrDefault(child)) has = true;
                }
                containsActive[node] = has;
            }

            var multipleRoots = roots.Count > 1;
            var orderedRoots = roots.OrderByDescending(r => containsActive.GetValueOrDefault(r) ? 1 : 0).ToList();
            var stack = new Stack<(SessionTreeNode Node, int Indent, bool JustBranched, bool ShowConnector, bool IsLast, List<GutterInfo> Gutters, bool IsVirtualRootChild)>();
            for (var i = orderedRoots.Count - 1; i >= 0; i--)
            {
                stack.Push((orderedRoots[i], multipleRoots ? 1 : 0, multipleRoots, multipleRoots, i == orderedRoots.Count - 1, [], multipleRoots));
            }

            while (stack.Count > 0)
            {
                var (node, indent, justBranched, showConnector, isLast, gutters, isVirtualRootChild) = stack.Pop();
                if (node.Entry is SessionMessageEntry { Message: AssistantMessage assistant })
                {
                    foreach (var tc in assistant.Content.OfType<ToolCall>()) _toolCallMap[tc.Id] = (tc.Name, tc.Arguments);
                }
                result.Add(new FlatNode(node) { Indent = indent, ShowConnector = showConnector, IsLast = isLast, Gutters = gutters, IsVirtualRootChild = isVirtualRootChild });

                var children = node.Children;
                var multipleChildren = children.Count > 1;
                var ordered = children.Where(c => containsActive.GetValueOrDefault(c)).Concat(children.Where(c => !containsActive.GetValueOrDefault(c))).ToList();
                var childIndent = multipleChildren || (justBranched && indent > 0) ? indent + 1 : indent;
                var connectorDisplayed = showConnector && !isVirtualRootChild;
                var displayIndent = _multipleRoots ? Math.Max(0, indent - 1) : indent;
                var connectorPosition = Math.Max(0, displayIndent - 1);
                var childGutters = connectorDisplayed ? [.. gutters, new GutterInfo(connectorPosition, !isLast)] : gutters;
                for (var i = ordered.Count - 1; i >= 0; i--)
                {
                    stack.Push((ordered[i], childIndent, multipleChildren, multipleChildren, i == ordered.Count - 1, childGutters, false));
                }
            }
            return result;
        }

        private static bool IsSettingsEntry(SessionEntry entry) => entry is LabelEntry or CustomEntry or ModelChangeEntry or ThinkingLevelChangeEntry or SessionInfoEntry;

        private void ApplyFilter()
        {
            if (_filteredNodes.Count > 0 && _selectedIndex < _filteredNodes.Count) _lastSelectedId = _filteredNodes[_selectedIndex].Node.Entry.Id;
            var tokens = _searchQuery.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            _filteredNodes = _flatNodes.Where(flat =>
            {
                var entry = flat.Node.Entry;
                var isCurrentLeaf = entry.Id == _currentLeafId;
                if (entry is SessionMessageEntry { Message: AssistantMessage assistant } && !isCurrentLeaf)
                {
                    var hasText = assistant.Content.OfType<TextContent>().Any(t => t.Text.Trim().Length > 0);
                    var errorOrAborted = assistant.StopReason is not (StopReason.Stop or StopReason.ToolUse);
                    if (!hasText && !errorOrAborted) return false;
                }

                var passes = _filterMode switch
                {
                    "user-only" => entry is SessionMessageEntry { Message: UserMessage },
                    "no-tools" => !IsSettingsEntry(entry) && entry is not SessionMessageEntry { Message: ToolResultMessage },
                    "labeled-only" => flat.Node.Label is not null,
                    "all" => true,
                    _ => !IsSettingsEntry(entry),
                };
                if (!passes) return false;
                if (tokens.Length > 0)
                {
                    var text = GetSearchableText(flat.Node).ToLowerInvariant();
                    return tokens.All(t => text.Contains(t, StringComparison.Ordinal));
                }
                return true;
            }).ToList();

            if (_foldedNodes.Count > 0)
            {
                var skip = new HashSet<string>();
                foreach (var flat in _flatNodes)
                {
                    var parentId = flat.Node.Entry.ParentId;
                    if (parentId is not null && (_foldedNodes.Contains(parentId) || skip.Contains(parentId))) skip.Add(flat.Node.Entry.Id);
                }
                _filteredNodes = _filteredNodes.Where(f => !skip.Contains(f.Node.Entry.Id)).ToList();
            }

            RecalculateVisualStructure();

            if (_lastSelectedId is not null) _selectedIndex = FindNearestVisibleIndex(_lastSelectedId);
            else if (_selectedIndex >= _filteredNodes.Count) _selectedIndex = Math.Max(0, _filteredNodes.Count - 1);
            if (_filteredNodes.Count > 0 && _selectedIndex < _filteredNodes.Count) _lastSelectedId = _filteredNodes[_selectedIndex].Node.Entry.Id;
        }

        private void RecalculateVisualStructure()
        {
            if (_filteredNodes.Count == 0) return;
            var visibleIds = _filteredNodes.Select(n => n.Node.Entry.Id).ToHashSet();
            var entryMap = _flatNodes.ToDictionary(n => n.Node.Entry.Id);

            string? FindVisibleAncestor(string nodeId)
            {
                var currentId = entryMap.TryGetValue(nodeId, out var n) ? n.Node.Entry.ParentId : null;
                while (currentId is not null)
                {
                    if (visibleIds.Contains(currentId)) return currentId;
                    currentId = entryMap.TryGetValue(currentId, out var p) ? p.Node.Entry.ParentId : null;
                }
                return null;
            }

            var visibleParent = new Dictionary<string, string?>();
            var visibleChildren = new Dictionary<string, List<string>>();
            var rootIds = new List<string>();
            foreach (var flat in _filteredNodes)
            {
                var id = flat.Node.Entry.Id;
                var ancestor = FindVisibleAncestor(id);
                visibleParent[id] = ancestor;
                if (ancestor is null)
                {
                    rootIds.Add(id);
                }
                else
                {
                    if (!visibleChildren.TryGetValue(ancestor, out var list)) visibleChildren[ancestor] = list = [];
                    list.Add(id);
                }
            }

            _multipleRoots = rootIds.Count > 1;
            var filteredMap = _filteredNodes.ToDictionary(n => n.Node.Entry.Id);
            var stack = new Stack<(string Id, int Indent, bool JustBranched, bool ShowConnector, bool IsLast, List<GutterInfo> Gutters, bool IsVirtualRootChild)>();
            for (var i = rootIds.Count - 1; i >= 0; i--)
            {
                stack.Push((rootIds[i], _multipleRoots ? 1 : 0, _multipleRoots, _multipleRoots, i == rootIds.Count - 1, [], _multipleRoots));
            }

            while (stack.Count > 0)
            {
                var (id, indent, justBranched, showConnector, isLast, gutters, isVirtualRootChild) = stack.Pop();
                if (!filteredMap.TryGetValue(id, out var flat)) continue;
                flat.Indent = indent;
                flat.ShowConnector = showConnector;
                flat.IsLast = isLast;
                flat.Gutters = gutters;
                flat.IsVirtualRootChild = isVirtualRootChild;

                var children = visibleChildren.GetValueOrDefault(id) ?? [];
                var multipleChildren = children.Count > 1;
                var childIndent = multipleChildren || (justBranched && indent > 0) ? indent + 1 : indent;
                var connectorDisplayed = showConnector && !isVirtualRootChild;
                var displayIndent = _multipleRoots ? Math.Max(0, indent - 1) : indent;
                var connectorPosition = Math.Max(0, displayIndent - 1);
                var childGutters = connectorDisplayed ? [.. gutters, new GutterInfo(connectorPosition, !isLast)] : gutters;
                for (var i = children.Count - 1; i >= 0; i--)
                {
                    stack.Push((children[i], childIndent, multipleChildren, multipleChildren, i == children.Count - 1, childGutters, false));
                }
            }

            _visibleParentMap = visibleParent;
            _visibleChildrenMap = visibleChildren;
            _visibleRoots = rootIds;
        }

        private static string ExtractFullContent(object? content) => content switch
        {
            UserContent { IsText: true } uc => uc.Text!,
            UserContent uc => string.Concat(uc.AsBlocks().OfType<TextContent>().Select(t => t.Text)),
            IEnumerable<ContentBlock> blocks => string.Concat(blocks.OfType<TextContent>().Select(t => t.Text)),
            _ => "",
        };

        private static string ExtractContent(object? content)
        {
            var full = ExtractFullContent(content);
            return full.Length > 200 ? full[..200] : full;
        }

        private static object? MessageContent(Message message) => message switch
        {
            UserMessage u => u.Content,
            AssistantMessage a => a.Content,
            ToolResultMessage t => t.Content,
            CustomMessage c => c.Content,
            _ => null,
        };

        private static string GetSearchableText(SessionTreeNode node)
        {
            var parts = new List<string>();
            if (node.Label is not null) parts.Add(node.Label);
            switch (node.Entry)
            {
                case SessionMessageEntry { Message: var msg }:
                    parts.Add(msg.Role);
                    if (MessageContent(msg) is { } content) parts.Add(ExtractContent(content));
                    if (msg is BashExecutionMessage bash && bash.Command.Length > 0) parts.Add(bash.Command);
                    break;
                case CustomMessageEntry custom:
                    parts.Add(custom.CustomType);
                    parts.Add(ExtractContent(custom.Content));
                    break;
                case CompactionEntry:
                    parts.Add("compaction");
                    break;
                case BranchSummaryEntry branch:
                    parts.Add("branch summary");
                    parts.Add(branch.Summary);
                    break;
                case SessionInfoEntry info:
                    parts.Add("title");
                    if (!string.IsNullOrEmpty(info.Name)) parts.Add(info.Name);
                    break;
                case ModelChangeEntry model:
                    parts.Add("model");
                    parts.Add(model.ModelId);
                    break;
                case ThinkingLevelChangeEntry thinking:
                    parts.Add("thinking");
                    parts.Add(thinking.ThinkingLevel);
                    break;
                case CustomEntry custom:
                    parts.Add("custom");
                    parts.Add(custom.CustomType);
                    break;
                case LabelEntry label:
                    parts.Add("label");
                    parts.Add(label.Label ?? "");
                    break;
            }
            return string.Join(" ", parts);
        }

        public void Invalidate()
        {
        }

        public string GetSearchQuery() => _searchQuery;

        public SessionTreeNode? GetSelectedNode() => _selectedIndex < _filteredNodes.Count ? _filteredNodes[_selectedIndex].Node : null;

        public void UpdateNodeLabel(string entryId, string? label)
        {
            foreach (var flat in _flatNodes)
            {
                if (flat.Node.Entry.Id != entryId) continue;
                flat.Node.Label = label;
                flat.Node.LabelTimestamp = label is not null ? DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) : null;
                break;
            }
        }

        private string StatusLabels()
        {
            var labels = _filterMode switch
            {
                "no-tools" => " [no-tools]",
                "user-only" => " [user]",
                "labeled-only" => " [labeled]",
                "all" => " [all]",
                _ => "",
            };
            if (_showLabelTimestamps) labels += " [+label time]";
            return labels;
        }

        public List<string> Render(int width)
        {
            var theme = ThemeManager.Current;
            var lines = new List<string>();
            if (_filteredNodes.Count == 0)
            {
                lines.Add(TextUtils.TruncateToWidth(theme.Fg("muted", "  No entries found"), width));
                lines.Add(TextUtils.TruncateToWidth(theme.Fg("muted", $"  (0/0){StatusLabels()}"), width));
                return lines;
            }

            var start = Math.Max(0, Math.Min(_selectedIndex - _maxVisibleLines / 2, _filteredNodes.Count - _maxVisibleLines));
            var end = Math.Min(start + _maxVisibleLines, _filteredNodes.Count);
            var rows = new List<ViewportRow>();
            for (var i = start; i < end; i++)
            {
                var flat = _filteredNodes[i];
                var entry = flat.Node.Entry;
                var selected = i == _selectedIndex;
                var cursor = selected ? theme.Fg("accent", "› ") : "  ";
                var displayIndent = _multipleRoots ? Math.Max(0, flat.Indent - 1) : flat.Indent;
                var connector = flat.ShowConnector && !flat.IsVirtualRootChild;
                var connectorPosition = connector ? displayIndent - 1 : -1;
                var isFolded = _foldedNodes.Contains(entry.Id);
                var prefix = new System.Text.StringBuilder();
                for (var c = 0; c < displayIndent * 3; c++)
                {
                    var level = c / 3;
                    var pos = c % 3;
                    var gutter = flat.Gutters.FirstOrDefault(g => g.Position == level);
                    if (gutter is not null) prefix.Append(pos == 0 ? (gutter.Show ? "│" : " ") : " ");
                    else if (connector && level == connectorPosition)
                    {
                        prefix.Append(pos switch
                        {
                            0 => flat.IsLast ? "└" : "├",
                            1 => isFolded ? "⊞" : IsFoldable(entry.Id) ? "⊟" : "─",
                            _ => " ",
                        });
                    }
                    else prefix.Append(' ');
                }

                var foldMarker = isFolded && !connector ? theme.Fg("accent", "⊞ ") : "";
                var pathMarker = _activePathIds.Contains(entry.Id) ? theme.Fg("accent", "• ") : "";
                var label = flat.Node.Label is not null ? theme.Fg("warning", $"[{flat.Node.Label}] ") : "";
                var labelTimestamp = _showLabelTimestamps && flat.Node.Label is not null && flat.Node.LabelTimestamp is not null ? theme.Fg("muted", $"{FormatLabelTimestamp(flat.Node.LabelTimestamp)} ") : "";
                var content = GetEntryDisplayText(flat.Node, selected);
                var prefixPart = theme.Fg("dim", prefix.ToString()) + foldMarker + pathMarker;
                var anchorCol = TextUtils.VisibleWidth(prefixPart);
                var gutterText = cursor;
                var body = prefixPart + label + labelTimestamp + content;
                if (selected)
                {
                    gutterText = theme.Bg("selectedBg", gutterText);
                    body = theme.Bg("selectedBg", body);
                }
                rows.Add(new ViewportRow(gutterText, body, anchorCol, TextUtils.VisibleWidth(body), selected));
            }
            lines.AddRange(RenderHorizontalViewport(rows, width));
            lines.Add(TextUtils.TruncateToWidth(theme.Fg("muted", $"  ({_selectedIndex + 1}/{_filteredNodes.Count}){StatusLabels()}"), width));
            return lines;
        }

        [GeneratedRegex(@"[\n\t]")]
        private static partial Regex NewlineTab();

        private static string Normalize(string s) => NewlineTab().Replace(s, " ").Trim();

        private string GetEntryDisplayText(SessionTreeNode node, bool selected)
        {
            var theme = ThemeManager.Current;
            string result;
            switch (node.Entry)
            {
                case SessionMessageEntry { Message: var msg }:
                    switch (msg)
                    {
                        case UserMessage user:
                            result = theme.Fg("accent", "user: ") + Normalize(ExtractContent(user.Content));
                            break;
                        case AssistantMessage assistant:
                        {
                            var text = Normalize(ExtractContent(assistant.Content));
                            if (text.Length > 0) result = theme.Fg("success", "assistant: ") + text;
                            else if (assistant.StopReason == StopReason.Aborted) result = theme.Fg("success", "assistant: ") + theme.Fg("muted", "(aborted)");
                            else if (!string.IsNullOrEmpty(assistant.ErrorMessage))
                            {
                                var err = Normalize(assistant.ErrorMessage);
                                result = theme.Fg("success", "assistant: ") + theme.Fg("error", err.Length > 80 ? err[..80] : err);
                            }
                            else result = theme.Fg("success", "assistant: ") + theme.Fg("muted", "(no content)");
                            break;
                        }
                        case ToolResultMessage tool:
                            result = _toolCallMap.TryGetValue(tool.ToolCallId, out var call)
                                ? theme.Fg("muted", FormatToolCall(call.Name, call.Arguments))
                                : theme.Fg("muted", $"[{(string.IsNullOrEmpty(tool.ToolName) ? "tool" : tool.ToolName)}]");
                            break;
                        case BashExecutionMessage bash:
                            result = theme.Fg("dim", $"[bash]: {Normalize(bash.Command)}");
                            break;
                        default:
                            result = theme.Fg("dim", $"[{msg.Role}]");
                            break;
                    }
                    break;
                case CustomMessageEntry custom:
                    result = theme.Fg("customMessageLabel", $"[{custom.CustomType}]: ") + Normalize(ExtractFullContent(custom.Content));
                    break;
                case CompactionEntry compaction:
                    result = theme.Fg("borderAccent", $"[compaction: {Math.Round(compaction.TokensBefore / 1000.0, MidpointRounding.AwayFromZero)}k tokens]");
                    break;
                case BranchSummaryEntry branch:
                    result = theme.Fg("warning", "[branch summary]: ") + Normalize(branch.Summary);
                    break;
                case ModelChangeEntry model:
                    result = theme.Fg("dim", $"[model: {model.ModelId}]");
                    break;
                case ThinkingLevelChangeEntry thinking:
                    result = theme.Fg("dim", $"[thinking: {thinking.ThinkingLevel}]");
                    break;
                case CustomEntry customEntry:
                    result = theme.Fg("dim", $"[custom: {customEntry.CustomType}]");
                    break;
                case LabelEntry labelEntry:
                    result = theme.Fg("dim", $"[label: {labelEntry.Label ?? "(cleared)"}]");
                    break;
                case SessionInfoEntry info:
                    result = !string.IsNullOrEmpty(info.Name)
                        ? theme.Fg("dim", "[title: ") + theme.Fg("dim", info.Name) + theme.Fg("dim", "]")
                        : theme.Fg("dim", "[title: ") + theme.Italic(theme.Fg("dim", "empty")) + theme.Fg("dim", "]");
                    break;
                default:
                    result = "";
                    break;
            }
            return selected ? theme.Bold(result) : result;
        }

        private static string FormatLabelTimestamp(string timestamp)
        {
            if (!DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) return "";
            var date = parsed.ToLocalTime();
            var now = DateTimeOffset.Now;
            var time = $"{date.Hour:D2}:{date.Minute:D2}";
            if (date.Date == now.Date) return time;
            if (date.Year == now.Year) return $"{date.Month}/{date.Day} {time}";
            return $"{date.Year.ToString(CultureInfo.InvariantCulture)[^2..]}/{date.Month}/{date.Day} {time}";
        }

        private static string? GetEntryCopyText(SessionTreeNode node)
        {
            string? text = node.Entry switch
            {
                SessionMessageEntry { Message: BashExecutionMessage bash } => bash.Command,
                SessionMessageEntry { Message: AssistantMessage assistant } => ExtractFullContent(assistant.Content) is { Length: > 0 } t ? t : assistant.ErrorMessage,
                SessionMessageEntry { Message: var msg } => MessageContent(msg) is { } c ? ExtractFullContent(c) : null,
                CustomMessageEntry custom => ExtractFullContent(custom.Content),
                CompactionEntry compaction => compaction.Summary,
                BranchSummaryEntry branch => branch.Summary,
                _ => null,
            };
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        private static string FormatToolCall(string name, JsonObject args)
        {
            static string Shorten(string p)
            {
                var home = Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } h ? h : Environment.GetEnvironmentVariable("USERPROFILE") ?? "";
                return home.Length > 0 && p.StartsWith(home, StringComparison.Ordinal) ? "~" + p[home.Length..] : p;
            }
            static string Js(JsonNode? node) => node switch
            {
                null => "",
                JsonValue v when v.TryGetValue<string>(out var s) => s,
                JsonValue v when v.TryGetValue<double>(out var d) => PiSharp.CodingAgent.Core.Tools.NodeCompat.FormatNumber(d),
                JsonValue v when v.TryGetValue<bool>(out var b) => b ? "true" : "false",
                JsonObject => "[object Object]",
                JsonArray a => string.Join(",", a.Select(Js)),
                _ => node.ToJsonString(),
            };
            static bool Truthy(JsonNode? node) => node switch
            {
                null => false,
                JsonValue v when v.TryGetValue<string>(out var s) => s.Length > 0,
                JsonValue v when v.TryGetValue<double>(out var d) => d != 0,
                JsonValue v when v.TryGetValue<bool>(out var b) => b,
                _ => true,
            };
            JsonNode? Or(string a, string b) => Truthy(args[a]) ? args[a] : args[b];

            switch (name)
            {
                case "read":
                {
                    var display = Shorten(Js(Or("path", "file_path")));
                    if (args["offset"] is not null || args["limit"] is not null)
                    {
                        var start = args["offset"] is JsonValue ov && ov.TryGetValue<double>(out var o) ? o : 1;
                        var endText = args["limit"] is JsonValue lv && lv.TryGetValue<double>(out var l) ? PiSharp.CodingAgent.Core.Tools.NodeCompat.FormatNumber(start + l - 1) : "";
                        display += $":{PiSharp.CodingAgent.Core.Tools.NodeCompat.FormatNumber(start)}{(endText.Length > 0 ? $"-{endText}" : "")}";
                    }
                    return $"[read: {display}]";
                }
                case "write":
                    return $"[write: {Shorten(Js(Or("path", "file_path")))}]";
                case "edit":
                    return $"[edit: {Shorten(Js(Or("path", "file_path")))}]";
                case "bash":
                {
                    var raw = Js(args["command"]);
                    var cmd = NewlineTab().Replace(raw, " ").Trim();
                    if (cmd.Length > 50) cmd = cmd[..50];
                    return $"[bash: {cmd}{(raw.Length > 50 ? "..." : "")}]";
                }
                case "grep":
                    return $"[grep: /{Js(args["pattern"])}/ in {Shorten(Truthy(args["path"]) ? Js(args["path"]) : ".")}]";
                case "find":
                    return $"[find: {Js(args["pattern"])} in {Shorten(Truthy(args["path"]) ? Js(args["path"]) : ".")}]";
                case "ls":
                    return $"[ls: {Shorten(Truthy(args["path"]) ? Js(args["path"]) : ".")}]";
                default:
                {
                    var json = args.ToJsonString();
                    return $"[{name}: {(json.Length > 40 ? json[..40] : json)}{(json.Length > 40 ? "..." : "")}]";
                }
            }
        }

        private bool IsFoldable(string entryId)
        {
            if (!_visibleChildrenMap.TryGetValue(entryId, out var children) || children.Count == 0) return false;
            var parentId = _visibleParentMap.GetValueOrDefault(entryId);
            if (parentId is null) return true;
            return _visibleChildrenMap.TryGetValue(parentId, out var siblings) && siblings.Count > 1;
        }

        private List<string> ChildrenOf(string? id) => id is null ? _visibleRoots : _visibleChildrenMap.GetValueOrDefault(id) ?? [];

        private int FindBranchSegmentStart(bool down)
        {
            if (_selectedIndex >= _filteredNodes.Count) return _selectedIndex;
            var indexById = new Dictionary<string, int>();
            for (var i = 0; i < _filteredNodes.Count; i++) indexById[_filteredNodes[i].Node.Entry.Id] = i;
            var currentId = _filteredNodes[_selectedIndex].Node.Entry.Id;
            if (down)
            {
                while (true)
                {
                    var children = ChildrenOf(currentId);
                    if (children.Count == 0) return indexById[currentId];
                    if (children.Count > 1) return indexById[children[0]];
                    currentId = children[0];
                }
            }
            while (true)
            {
                var parentId = _visibleParentMap.GetValueOrDefault(currentId);
                if (parentId is null) return indexById[currentId];
                if (ChildrenOf(parentId).Count > 1)
                {
                    var segmentStart = indexById[currentId];
                    if (segmentStart < _selectedIndex) return segmentStart;
                }
                currentId = parentId;
            }
        }

        private static readonly string[] FilterModes = ["default", "no-tools", "user-only", "labeled-only", "all"];

        private void SetFilter(string mode)
        {
            _filterMode = mode;
            _foldedNodes.Clear();
            ApplyFilter();
        }

        public void HandleInput(string keyData)
        {
            var kb = KeybindingsManager.Global;
            if (kb.Matches(keyData, "tui.select.up"))
            {
                _selectedIndex = _selectedIndex == 0 ? _filteredNodes.Count - 1 : _selectedIndex - 1;
            }
            else if (kb.Matches(keyData, "tui.select.down"))
            {
                _selectedIndex = _selectedIndex == _filteredNodes.Count - 1 ? 0 : _selectedIndex + 1;
            }
            else if (kb.Matches(keyData, "app.tree.foldOrUp"))
            {
                var currentId = _selectedIndex < _filteredNodes.Count ? _filteredNodes[_selectedIndex].Node.Entry.Id : null;
                if (currentId is not null && IsFoldable(currentId) && !_foldedNodes.Contains(currentId))
                {
                    _foldedNodes.Add(currentId);
                    ApplyFilter();
                }
                else
                {
                    _selectedIndex = FindBranchSegmentStart(false);
                }
            }
            else if (kb.Matches(keyData, "app.tree.unfoldOrDown"))
            {
                var currentId = _selectedIndex < _filteredNodes.Count ? _filteredNodes[_selectedIndex].Node.Entry.Id : null;
                if (currentId is not null && _foldedNodes.Contains(currentId))
                {
                    _foldedNodes.Remove(currentId);
                    ApplyFilter();
                }
                else
                {
                    _selectedIndex = FindBranchSegmentStart(true);
                }
            }
            else if (kb.Matches(keyData, "tui.editor.cursorLeft") || kb.Matches(keyData, "tui.select.pageUp"))
            {
                _selectedIndex = Math.Max(0, _selectedIndex - _maxVisibleLines);
            }
            else if (kb.Matches(keyData, "tui.editor.cursorRight") || kb.Matches(keyData, "tui.select.pageDown"))
            {
                _selectedIndex = Math.Min(_filteredNodes.Count - 1, _selectedIndex + _maxVisibleLines);
            }
            else if (kb.Matches(keyData, "tui.select.confirm"))
            {
                if (_selectedIndex < _filteredNodes.Count) OnSelect?.Invoke(_filteredNodes[_selectedIndex].Node.Entry.Id);
            }
            else if (kb.Matches(keyData, "app.message.copy"))
            {
                OnCopy?.Invoke(GetSelectedNode() is { } node ? GetEntryCopyText(node) : null);
            }
            else if (kb.Matches(keyData, "tui.select.cancel"))
            {
                if (_searchQuery.Length > 0)
                {
                    _searchQuery = "";
                    _foldedNodes.Clear();
                    ApplyFilter();
                }
                else
                {
                    OnCancel?.Invoke();
                }
            }
            else if (kb.Matches(keyData, "app.tree.filter.default")) SetFilter("default");
            else if (kb.Matches(keyData, "app.tree.filter.noTools")) SetFilter(_filterMode == "no-tools" ? "default" : "no-tools");
            else if (kb.Matches(keyData, "app.tree.filter.userOnly")) SetFilter(_filterMode == "user-only" ? "default" : "user-only");
            else if (kb.Matches(keyData, "app.tree.filter.labeledOnly")) SetFilter(_filterMode == "labeled-only" ? "default" : "labeled-only");
            else if (kb.Matches(keyData, "app.tree.filter.all")) SetFilter(_filterMode == "all" ? "default" : "all");
            else if (kb.Matches(keyData, "app.tree.filter.cycleBackward"))
            {
                var index = Array.IndexOf(FilterModes, _filterMode);
                SetFilter(FilterModes[(index - 1 + FilterModes.Length) % FilterModes.Length]);
            }
            else if (kb.Matches(keyData, "app.tree.filter.cycleForward"))
            {
                var index = Array.IndexOf(FilterModes, _filterMode);
                SetFilter(FilterModes[(index + 1) % FilterModes.Length]);
            }
            else if (kb.Matches(keyData, "tui.editor.deleteCharBackward"))
            {
                if (_searchQuery.Length > 0)
                {
                    _searchQuery = _searchQuery[..^1];
                    _foldedNodes.Clear();
                    ApplyFilter();
                }
            }
            else if (kb.Matches(keyData, "app.tree.editLabel"))
            {
                if (_selectedIndex < _filteredNodes.Count) OnLabelEdit?.Invoke(_filteredNodes[_selectedIndex].Node.Entry.Id, _filteredNodes[_selectedIndex].Node.Label);
            }
            else if (kb.Matches(keyData, "app.tree.toggleLabelTimestamp"))
            {
                _showLabelTimestamps = !_showLabelTimestamps;
            }
            else
            {
                var hasControl = keyData.Any(ch => ch < 32 || ch == 0x7f || ch is >= (char)0x80 and <= (char)0x9f);
                if (!hasControl && keyData.Length > 0)
                {
                    _searchQuery += keyData;
                    _foldedNodes.Clear();
                    ApplyFilter();
                }
            }
        }
    }

    private sealed class SearchLine(TreeList treeList) : IComponent
    {
        public void Invalidate()
        {
        }

        public List<string> Render(int width)
        {
            var theme = ThemeManager.Current;
            var query = treeList.GetSearchQuery();
            return [TextUtils.TruncateToWidth(query.Length > 0 ? $"  {theme.Fg("muted", "Type to search:")} {theme.Fg("accent", query)}" : $"  {theme.Fg("muted", "Type to search:")}", width)];
        }
    }

    private static readonly (string[] Keys, string Label, bool LabelFirst)[] TreeHelpItems =
    [
        (["tui.select.up", "tui.select.down"], "move", false),
        (["tui.editor.cursorLeft", "tui.editor.cursorRight"], "page", false),
        (["app.tree.foldOrUp", "app.tree.unfoldOrDown"], "branch", false),
        (["app.message.copy"], "copy", false),
        (["app.tree.editLabel"], "label", false),
        (["app.tree.toggleLabelTimestamp"], "label time", false),
        (["app.tree.filter.default", "app.tree.filter.noTools", "app.tree.filter.userOnly", "app.tree.filter.labeledOnly", "app.tree.filter.all"], "filters", true),
        (["app.tree.filter.cycleForward", "app.tree.filter.cycleBackward"], "cycle", true),
    ];

    [GeneratedRegex(@"\bpageUp\b")]
    private static partial Regex PageUpWord();

    [GeneratedRegex(@"\bpageDown\b")]
    private static partial Regex PageDownWord();

    [GeneratedRegex(@"\bup\b")]
    private static partial Regex UpWord();

    [GeneratedRegex(@"\bdown\b")]
    private static partial Regex DownWord();

    [GeneratedRegex(@"\bleft\b")]
    private static partial Regex LeftWord();

    [GeneratedRegex(@"\bright\b")]
    private static partial Regex RightWord();

    private static string FormatHelpKeys(string[] keybindings)
    {
        var keys = keybindings.Select(k => KeybindingsManager.Global.GetKeys(k).FirstOrDefault()).OfType<string>().ToList();
        if (keys.Count == 0) return "";
        string compact;
        if (keys.Count == 1)
        {
            compact = keys[0];
        }
        else
        {
            var parts = keys.Select(k =>
            {
                var sep = k.LastIndexOf('+');
                return sep == -1 ? (Prefix: "", Suffix: k) : (Prefix: k[..(sep + 1)], Suffix: k[(sep + 1)..]);
            }).ToList();
            var prefix = parts[0].Prefix;
            compact = prefix.Length > 0 && parts.All(p => p.Prefix == prefix) ? prefix + string.Join("/", parts.Select(p => p.Suffix)) : string.Join("/", keys);
        }
        var text = KeyHints.FormatKeyText(compact);
        text = PageUpWord().Replace(text, "pgup");
        text = PageDownWord().Replace(text, "pgdn");
        text = UpWord().Replace(text, "↑");
        text = DownWord().Replace(text, "↓");
        text = LeftWord().Replace(text, "←");
        return RightWord().Replace(text, "→");
    }

    private sealed class TreeHelp : IComponent
    {
        public void Invalidate()
        {
        }

        public List<string> Render(int width)
        {
            var items = TreeHelpItems.Select(item =>
            {
                var text = FormatHelpKeys(item.Keys);
                if (text.Length == 0) return item.Label;
                return item.LabelFirst ? $"{item.Label} {text}" : $"{text} {item.Label}";
            });
            var available = Math.Max(1, width);
            const string indent = "  ";
            const string separator = " · ";
            var lines = new List<string>();
            var current = "";
            foreach (var item in items)
            {
                var candidate = current.Length > 0 ? $"{current}{separator}{item}" : TextUtils.VisibleWidth(indent + item) <= available ? indent + item : item;
                if (current.Length == 0 || TextUtils.VisibleWidth(candidate) <= available)
                {
                    current = candidate;
                    continue;
                }
                lines.AddRange(TextUtils.WrapTextWithAnsi(current.TrimEnd(), available));
                current = TextUtils.VisibleWidth(indent + item) <= available ? indent + item : item;
            }
            if (current.Length > 0) lines.AddRange(TextUtils.WrapTextWithAnsi(current.TrimEnd(), available));
            return lines.Select(l => ThemeManager.Current.Fg("muted", l)).ToList();
        }
    }

    private sealed class LabelInput : IInputComponent, IFocusable
    {
        private readonly Input _input = new();
        private readonly string _entryId;
        private bool _focused;

        public Action<string, string?>? OnSubmit;
        public Action? OnCancel;

        public bool Focused
        {
            get => _focused;
            set
            {
                _focused = value;
                _input.Focused = value;
            }
        }

        public LabelInput(string entryId, string? currentLabel)
        {
            _entryId = entryId;
            if (!string.IsNullOrEmpty(currentLabel)) _input.SetValue(currentLabel);
        }

        public void Invalidate()
        {
        }

        public List<string> Render(int width)
        {
            const string indent = "  ";
            var lines = new List<string> { TextUtils.TruncateToWidth($"{indent}{ThemeManager.Current.Fg("muted", "Label (empty to remove):")}", width) };
            lines.AddRange(_input.Render(width - indent.Length).Select(l => TextUtils.TruncateToWidth(indent + l, width)));
            lines.Add(TextUtils.TruncateToWidth($"{indent}{KeyHints.KeyHint("tui.select.confirm", "save")}  {KeyHints.KeyHint("tui.select.cancel", "cancel")}", width));
            return lines;
        }

        public void HandleInput(string keyData)
        {
            var kb = KeybindingsManager.Global;
            if (kb.Matches(keyData, "tui.select.confirm"))
            {
                var value = _input.GetValue().Trim();
                OnSubmit?.Invoke(_entryId, value.Length > 0 ? value : null);
            }
            else if (kb.Matches(keyData, "tui.select.cancel")) OnCancel?.Invoke();
            else _input.HandleInput(keyData);
        }
    }

    private readonly TreeList _treeList;
    private LabelInput? _labelInput;
    private readonly Container _labelInputContainer = new();
    private readonly Container _treeContainer = new();
    private readonly Action<string, string?>? _onLabelChange;
    private bool _focused;

    public Action<string?>? OnCopy { get; set; }

    public bool Focused
    {
        get => _focused;
        set
        {
            _focused = value;
            if (_labelInput is not null) _labelInput.Focused = value;
        }
    }

    public TreeSelectorComponent(List<SessionTreeNode> tree, string? currentLeafId, int terminalHeight, Action<string> onSelect, Action onCancel, Action<string, string?>? onLabelChange = null, string? initialSelectedId = null, string? initialFilterMode = null)
    {
        _onLabelChange = onLabelChange;
        var maxVisibleLines = Math.Max(5, terminalHeight / 2);
        _treeList = new TreeList(tree, currentLeafId, maxVisibleLines, initialSelectedId, initialFilterMode)
        {
            OnSelect = onSelect,
            OnCancel = onCancel,
        };
        _treeList.OnCopy = text => OnCopy?.Invoke(text);
        _treeList.OnLabelEdit = ShowLabelInput;
        _treeContainer.AddChild(_treeList);

        AddChild(new Spacer(1));
        AddChild(new DynamicBorder());
        AddChild(new Text(ThemeManager.Current.Bold("  Session Tree"), 1, 0));
        AddChild(new TreeHelp());
        AddChild(new SearchLine(_treeList));
        AddChild(new DynamicBorder());
        AddChild(new Spacer(1));
        AddChild(_treeContainer);
        AddChild(_labelInputContainer);
        AddChild(new Spacer(1));
        AddChild(new DynamicBorder());
        if (tree.Count == 0) UiDispatcher.Current?.SetTimeout(onCancel, 100);
    }

    private void ShowLabelInput(string entryId, string? currentLabel)
    {
        _labelInput = new LabelInput(entryId, currentLabel)
        {
            OnSubmit = (id, label) =>
            {
                _treeList.UpdateNodeLabel(id, label);
                _onLabelChange?.Invoke(id, label);
                HideLabelInput();
            },
            OnCancel = HideLabelInput,
            Focused = _focused,
        };
        _treeContainer.Clear();
        _labelInputContainer.Clear();
        _labelInputContainer.AddChild(_labelInput);
    }

    private void HideLabelInput()
    {
        _labelInput = null;
        _labelInputContainer.Clear();
        _treeContainer.Clear();
        _treeContainer.AddChild(_treeList);
    }

    public void HandleInput(string keyData)
    {
        if (_labelInput is not null) _labelInput.HandleInput(keyData);
        else _treeList.HandleInput(keyData);
    }
}
