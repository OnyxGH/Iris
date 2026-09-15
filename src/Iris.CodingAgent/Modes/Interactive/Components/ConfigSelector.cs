using System.Text.Json.Nodes;
using Iris.Ai.Json;
using Iris.CodingAgent.Config;
using Iris.CodingAgent.Core;
using Iris.CodingAgent.Utils;
using Iris.Tui;
using Iris.Tui.Components;

namespace Iris.CodingAgent.Modes.Interactive.Components;

/// <summary>TUI for enabling and disabling extensions, skills, prompts and themes (`config` command).</summary>
public sealed class ConfigSelectorComponent : Container, IFocusable
{
    private static readonly string[] ResourceTypes = ["extensions", "skills", "prompts", "themes"];

    private static readonly Dictionary<string, string> ResourceTypeLabels = new()
    {
        ["extensions"] = "Extensions", ["skills"] = "Skills", ["prompts"] = "Prompts", ["themes"] = "Themes",
    };

    internal sealed class ResourceItem
    {
        public required string Path { get; init; }
        public bool Enabled { get; set; }
        public required PathMetadata Metadata { get; init; }
        public required string ResourceType { get; init; }
        public required string DisplayName { get; init; }
    }

    internal sealed class ResourceSubgroup
    {
        public required string Type { get; init; }
        public required string Label { get; init; }
        public List<ResourceItem> Items { get; } = [];
    }

    internal sealed class ResourceGroup
    {
        public required string Key { get; init; }
        public required string Label { get; init; }
        public required string Scope { get; init; }
        public required string Origin { get; init; }
        public required string Source { get; init; }
        public List<ResourceSubgroup> Subgroups { get; } = [];
    }

    private readonly ConfigSelectorHeader _header;
    private readonly ResourceList _resourceList;
    private string _writeScope;
    private bool _focused;

    public ConfigSelectorComponent(ResolvedPaths globalPaths, ResolvedPaths projectPaths, SettingsManager settingsManager, string cwd, string agentDir,
        Action onClose, Action onExit, Action requestRender, int? terminalHeight = null, string writeScope = "global", bool projectModeAvailable = true)
    {
        _writeScope = writeScope;
        var groupsByScope = new Dictionary<string, List<ResourceGroup>>
        {
            ["global"] = BuildGroups(globalPaths, agentDir),
            ["project"] = BuildGroups(projectPaths, agentDir),
        };
        AddChild(new Spacer(1));
        AddChild(new DynamicBorder());
        AddChild(new Spacer(1));
        _header = new ConfigSelectorHeader(_writeScope, projectModeAvailable);
        AddChild(_header);
        AddChild(new Spacer(1));
        _resourceList = new ResourceList(groupsByScope, settingsManager, cwd, agentDir, terminalHeight, _writeScope)
        {
            OnCancel = onClose,
            OnExit = onExit,
            OnToggle = (_, _) => requestRender(),
        };
        if (projectModeAvailable)
        {
            _resourceList.OnSwitchMode = () =>
            {
                _writeScope = _writeScope == "global" ? "project" : "global";
                _header.WriteScope = _writeScope;
                _resourceList.SetWriteScope(_writeScope);
                requestRender();
            };
        }
        AddChild(_resourceList);
        AddChild(new Spacer(1));
        AddChild(new DynamicBorder());
    }

    public bool Focused
    {
        get => _focused;
        set
        {
            _focused = value;
            _resourceList.Focused = value;
        }
    }

    public IInputComponent GetResourceList() => _resourceList;

    private static string FormatBaseDir(string baseDir)
    {
        var homeDir = AppConfig.HomeDir;
        string displayPath;
        if (baseDir == homeDir) displayPath = "~";
        else if (baseDir.StartsWith(homeDir, StringComparison.Ordinal)) displayPath = "~" + baseDir[homeDir.Length..].Replace('\\', '/');
        else displayPath = baseDir.Replace('\\', '/');
        return displayPath.EndsWith('/') ? displayPath : displayPath + "/";
    }

    private static string GetGroupLabel(PathMetadata metadata, string agentDir)
    {
        if (metadata.Source == "auto")
        {
            if (metadata.BaseDir is not null) return metadata.Scope == "user" ? $"User ({FormatBaseDir(metadata.BaseDir)})" : $"Project ({FormatBaseDir(metadata.BaseDir)})";
            return metadata.Scope == "user" ? $"User ({FormatBaseDir(agentDir)})" : $"Project ({AppConfig.ConfigDirName}/)";
        }
        return metadata.Scope == "user" ? "User settings" : "Project settings";
    }

    private static List<ResourceGroup> BuildGroups(ResolvedPaths resolved, string agentDir)
    {
        var groupMap = new Dictionary<string, ResourceGroup>();
        var groupOrder = new List<ResourceGroup>();
        void AddToGroup(List<ResolvedResource> resources, string resourceType)
        {
            foreach (var res in resources)
            {
                var metadata = res.Metadata;
                var groupKey = $"{metadata.Origin}:{metadata.Scope}:{metadata.Source}:{metadata.BaseDir ?? ""}";
                if (!groupMap.TryGetValue(groupKey, out var group))
                {
                    group = new ResourceGroup { Key = groupKey, Label = GetGroupLabel(metadata, agentDir), Scope = metadata.Scope, Origin = metadata.Origin, Source = metadata.Source };
                    groupMap[groupKey] = group;
                    groupOrder.Add(group);
                }
                var subgroup = group.Subgroups.FirstOrDefault(sg => sg.Type == resourceType);
                if (subgroup is null)
                {
                    subgroup = new ResourceSubgroup { Type = resourceType, Label = ResourceTypeLabels[resourceType] };
                    group.Subgroups.Add(subgroup);
                }
                var fileName = System.IO.Path.GetFileName(res.Path);
                var parentFolder = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(res.Path) ?? "");
                var displayName = resourceType == "extensions" && parentFolder != "extensions" ? $"{parentFolder}/{fileName}"
                    : resourceType == "skills" && fileName == "SKILL.md" ? parentFolder
                    : fileName;
                subgroup.Items.Add(new ResourceItem { Path = res.Path, Enabled = res.Enabled, Metadata = metadata, ResourceType = resourceType, DisplayName = displayName });
            }
        }
        AddToGroup(resolved.Extensions, "extensions");
        AddToGroup(resolved.Skills, "skills");
        AddToGroup(resolved.Prompts, "prompts");
        AddToGroup(resolved.Themes, "themes");

        var groups = groupOrder.ToList();
        groups.Sort((a, b) =>
        {
            if (a.Scope != b.Scope) return a.Scope == "user" ? -1 : 1;
            return NodeCompare.LocaleCompare(a.Source, b.Source);
        });
        foreach (var group in groups)
        {
            group.Subgroups.Sort((a, b) => Array.IndexOf(ResourceTypes, a.Type) - Array.IndexOf(ResourceTypes, b.Type));
            foreach (var subgroup in group.Subgroups) subgroup.Items.Sort((a, b) => NodeCompare.LocaleCompare(a.DisplayName, b.DisplayName));
        }
        return groups;
    }

    private sealed class ConfigSelectorHeader(string writeScope, bool projectModeAvailable) : IComponent
    {
        public string WriteScope { get; set; } = writeScope;

        public void Invalidate()
        {
        }

        public List<string> Render(int width)
        {
            var theme = ThemeManager.Current;
            var title = theme.Bold(WriteScope == "project" ? "Project Local Resources" : "Global Resources");
            var sep = theme.Fg("muted", " · ");
            var switchHint = projectModeAvailable ? KeyHints.KeyHint("tui.input.tab", "switch mode") + sep : "";
            var actionHint = WriteScope == "project" ? KeyHints.RawKeyHint("space", "cycle inherit/+/-") : KeyHints.RawKeyHint("space", "toggle");
            var hint = switchHint + actionHint + sep + KeyHints.RawKeyHint("esc", "close");
            var spacing = Math.Max(1, width - TextUtils.VisibleWidth(title) - TextUtils.VisibleWidth(hint));
            var scopeHint = WriteScope == "project"
                ? theme.Fg("muted", $"{AppConfig.ConfigDirName}/settings.json · inherited global resources are dimmed")
                : theme.Fg("muted", $"~/{AppConfig.ConfigDirName}/agent/settings.json");
            return [TextUtils.TruncateToWidth($"{title}{new string(' ', spacing)}{hint}", width, ""), TextUtils.TruncateToWidth(scopeHint, width, "")];
        }
    }

    private abstract record FlatEntry;

    private sealed record GroupEntry(ResourceGroup Group) : FlatEntry;

    private sealed record SubgroupEntry(ResourceSubgroup Subgroup, ResourceGroup Group) : FlatEntry;

    private sealed record ItemEntry(ResourceItem Item) : FlatEntry;

    private sealed class ResourceList : IInputComponent, IFocusable
    {
        private readonly Dictionary<string, List<ResourceGroup>> _groupsByScope;
        private List<FlatEntry> _flatItems = [];
        private List<FlatEntry> _filteredItems;
        private int _selectedIndex;
        private readonly Input _searchInput = new();
        private readonly int _maxVisible;
        private readonly SettingsManager _settingsManager;
        private readonly string _cwd;
        private readonly string _agentDir;
        private string _writeScope;
        private readonly Dictionary<string, bool> _inheritedEnabledByKey;
        private bool _focused;

        public Action? OnCancel { get; init; }
        public Action? OnExit { get; init; }
        public Action<ResourceItem, bool>? OnToggle { get; init; }
        public Action? OnSwitchMode { get; set; }

        public ResourceList(Dictionary<string, List<ResourceGroup>> groupsByScope, SettingsManager settingsManager, string cwd, string agentDir, int? terminalHeight, string writeScope)
        {
            _groupsByScope = groupsByScope;
            _settingsManager = settingsManager;
            _cwd = cwd;
            _agentDir = agentDir;
            _writeScope = writeScope;
            _inheritedEnabledByKey = BuildInheritedEnabledMap(groupsByScope["global"]);
            // 8 lines of chrome: spacers, borders and the two-line header.
            _maxVisible = Math.Max(5, (terminalHeight ?? 24) - 8);
            BuildFlatList();
            _filteredItems = [.. _flatItems];
        }

        public bool Focused
        {
            get => _focused;
            set
            {
                _focused = value;
                _searchInput.Focused = value;
            }
        }

        private List<ResourceGroup> Groups => _groupsByScope[_writeScope];

        public void SetWriteScope(string writeScope)
        {
            _writeScope = writeScope;
            BuildFlatList();
            FilterItems(_searchInput.GetValue());
        }

        private Dictionary<string, bool> BuildInheritedEnabledMap(List<ResourceGroup> groups)
        {
            var result = new Dictionary<string, bool>();
            foreach (var item in groups.SelectMany(g => g.Subgroups).SelectMany(s => s.Items)) result[GetResourceItemKey(item)] = item.Enabled;
            return result;
        }

        private void BuildFlatList()
        {
            _flatItems = [];
            foreach (var group in Groups)
            {
                _flatItems.Add(new GroupEntry(group));
                foreach (var subgroup in group.Subgroups)
                {
                    _flatItems.Add(new SubgroupEntry(subgroup, group));
                    foreach (var item in subgroup.Items) _flatItems.Add(new ItemEntry(item));
                }
            }
            _selectedIndex = Math.Max(0, _flatItems.FindIndex(e => e is ItemEntry));
        }

        private int FindNextItem(int fromIndex, int direction)
        {
            for (var idx = fromIndex + direction; idx >= 0 && idx < _filteredItems.Count; idx += direction)
            {
                if (_filteredItems[idx] is ItemEntry) return idx;
            }
            return fromIndex;
        }

        private void FilterItems(string query)
        {
            if (query.Trim().Length == 0)
            {
                _filteredItems = [.. _flatItems];
                SelectFirstItem();
                return;
            }
            var lowerQuery = query.ToLowerInvariant();
            var matchingItems = _flatItems.OfType<ItemEntry>().Select(e => e.Item)
                .Where(item => item.DisplayName.ToLowerInvariant().Contains(lowerQuery) || item.ResourceType.Contains(lowerQuery) || item.Path.ToLowerInvariant().Contains(lowerQuery))
                .ToHashSet();
            var matchingSubgroups = new HashSet<ResourceSubgroup>();
            var matchingGroups = new HashSet<ResourceGroup>();
            foreach (var group in Groups)
            {
                foreach (var subgroup in group.Subgroups)
                {
                    if (!subgroup.Items.Any(matchingItems.Contains)) continue;
                    matchingSubgroups.Add(subgroup);
                    matchingGroups.Add(group);
                }
            }
            _filteredItems = _flatItems.Where(entry => entry switch
            {
                GroupEntry g => matchingGroups.Contains(g.Group),
                SubgroupEntry s => matchingSubgroups.Contains(s.Subgroup),
                ItemEntry i => matchingItems.Contains(i.Item),
                _ => false,
            }).ToList();
            SelectFirstItem();
        }

        private void SelectFirstItem() => _selectedIndex = Math.Max(0, _filteredItems.FindIndex(e => e is ItemEntry));

        private void UpdateItem(ResourceItem item, bool enabled)
        {
            item.Enabled = enabled;
            foreach (var found in Groups.SelectMany(g => g.Subgroups).SelectMany(s => s.Items).Where(i => i.Path == item.Path && i.ResourceType == item.ResourceType).Take(1))
            {
                found.Enabled = enabled;
            }
        }

        public void Invalidate()
        {
        }

        public List<string> Render(int width)
        {
            var theme = ThemeManager.Current;
            var lines = new List<string>();
            lines.AddRange(_searchInput.Render(width));
            lines.Add("");
            if (_filteredItems.Count == 0)
            {
                lines.Add(theme.Fg("muted", "  No resources found"));
                return lines;
            }
            var startIndex = Math.Max(0, Math.Min(_selectedIndex - _maxVisible / 2, _filteredItems.Count - _maxVisible));
            var endIndex = Math.Min(startIndex + _maxVisible, _filteredItems.Count);
            for (var i = startIndex; i < endIndex; i++)
            {
                switch (_filteredItems[i])
                {
                    case GroupEntry { Group: var group }:
                    {
                        var inherited = _writeScope == "project" && group.Scope == "user";
                        var label = theme.Bold($"{group.Label}{(inherited ? " · inherited global" : "")}");
                        lines.Add(TextUtils.TruncateToWidth($"  {theme.Fg(inherited ? "dim" : "accent", label)}", width, ""));
                        break;
                    }
                    case SubgroupEntry { Subgroup: var subgroup, Group: var group }:
                    {
                        var color = _writeScope == "project" && group.Scope == "user" ? "dim" : "muted";
                        lines.Add(TextUtils.TruncateToWidth($"    {theme.Fg(color, subgroup.Label)}", width, ""));
                        break;
                    }
                    case ItemEntry { Item: var item }:
                    {
                        var isSelected = i == _selectedIndex;
                        var cursor = isSelected ? "> " : "  ";
                        var dimmed = IsDimmedItem(item);
                        var nameText = isSelected && !dimmed ? theme.Bold(item.DisplayName) : item.DisplayName;
                        var name = dimmed ? theme.Fg("dim", nameText) : nameText;
                        lines.Add(TextUtils.TruncateToWidth($"{cursor}    {RenderCheckbox(item)} {name}{GetItemSuffix(item)}", width, "..."));
                        break;
                    }
                }
            }
            if (startIndex > 0 || endIndex < _filteredItems.Count)
            {
                var itemCount = _filteredItems.Count(e => e is ItemEntry);
                var currentItemIndex = _filteredItems.Take(_selectedIndex).Count(e => e is ItemEntry) + 1;
                lines.Add(theme.Fg("dim", $"  ({currentItemIndex}/{itemCount})"));
            }
            return lines;
        }

        public void HandleInput(string data)
        {
            var kb = KeybindingsManager.Global;
            if (kb.Matches(data, "tui.select.up"))
            {
                _selectedIndex = FindNextItem(_selectedIndex, -1);
                return;
            }
            if (kb.Matches(data, "tui.select.down"))
            {
                _selectedIndex = FindNextItem(_selectedIndex, 1);
                return;
            }
            if (kb.Matches(data, "tui.select.pageUp"))
            {
                var target = Math.Max(0, _selectedIndex - _maxVisible);
                while (target < _filteredItems.Count && _filteredItems[target] is not ItemEntry) target++;
                if (target < _filteredItems.Count) _selectedIndex = target;
                return;
            }
            if (kb.Matches(data, "tui.select.pageDown"))
            {
                var target = Math.Min(_filteredItems.Count - 1, _selectedIndex + _maxVisible);
                while (target >= 0 && _filteredItems[target] is not ItemEntry) target--;
                if (target >= 0) _selectedIndex = target;
                return;
            }
            if (kb.Matches(data, "tui.select.cancel"))
            {
                OnCancel?.Invoke();
                return;
            }
            if (Keys.Matches(data, "ctrl+c"))
            {
                OnExit?.Invoke();
                return;
            }
            if (kb.Matches(data, "tui.input.tab"))
            {
                OnSwitchMode?.Invoke();
                return;
            }
            if (data == " " || kb.Matches(data, "tui.select.confirm"))
            {
                if (_selectedIndex < _filteredItems.Count && _filteredItems[_selectedIndex] is ItemEntry { Item: var item }
                    && (_writeScope == "project" || GetItemScope(item) == "user"))
                {
                    if (ToggleResource(item) is { } newEnabled)
                    {
                        UpdateItem(item, newEnabled);
                        OnToggle?.Invoke(item, newEnabled);
                    }
                }
                return;
            }
            _searchInput.HandleInput(data);
            FilterItems(_searchInput.GetValue());
        }

        // ----- toggling -----

        private static string Relative(string from, string to)
        {
            var rel = System.IO.Path.GetRelativePath(from, to);
            return rel == "." ? "" : rel;
        }

        private static List<string> StringArray(JsonNode? node) => node is JsonArray arr ? arr.Select(PiJson.GetString).OfType<string>().ToList() : [];

        private static string PatternTarget(string entry) => entry.StartsWith('!') || entry.StartsWith('+') || entry.StartsWith('-') ? entry[1..] : entry;

        private bool? ToggleResource(ResourceItem item)
        {
            if (_writeScope == "project")
            {
                var state = GetNextOverrideState(item);
                if (!SetProjectResourceOverride(item, state)) return null;
                return state == "inherit" ? GetInheritedEnabled(item) : state == "load";
            }
            var enabled = !item.Enabled;
            ToggleTopLevelResource(item, enabled);
            return enabled;
        }

        private void SetTopLevelPaths(string scope, string key, List<string> paths)
        {
            if (scope == "project")
            {
                if (key == "extensions") _settingsManager.SetProjectExtensionPaths(paths);
                else if (key == "skills") _settingsManager.SetProjectSkillPaths(paths);
                else if (key == "prompts") _settingsManager.SetProjectPromptTemplatePaths(paths);
                else _settingsManager.SetProjectThemePaths(paths);
            }
            else
            {
                if (key == "extensions") _settingsManager.SetExtensionPaths(paths);
                else if (key == "skills") _settingsManager.SetSkillPaths(paths);
                else if (key == "prompts") _settingsManager.SetPromptTemplatePaths(paths);
                else _settingsManager.SetThemePaths(paths);
            }
        }

        private void ToggleTopLevelResource(ResourceItem item, bool enabled)
        {
            var scope = item.Metadata.Scope;
            var settings = scope == "project" ? _settingsManager.GetProjectSettings() : _settingsManager.GetGlobalSettings();
            var pattern = GetResourcePattern(item);
            var updated = StringArray(settings[item.ResourceType]).Where(p => PatternTarget(p) != pattern).ToList();
            updated.Add(enabled ? $"+{pattern}" : $"-{pattern}");
            SetTopLevelPaths(scope, item.ResourceType, updated);
        }

        private string RenderCheckbox(ResourceItem item)
        {
            var theme = ThemeManager.Current;
            if (_writeScope == "project")
            {
                var state = GetProjectOverrideState(item);
                if (state == "load") return theme.Fg("success", "[+]");
                if (state == "unload") return theme.Fg("warning", "[-]");
                return theme.Fg("dim", item.Enabled ? "[x]" : "[ ]");
            }
            return item.Enabled ? theme.Fg("success", "[x]") : theme.Fg("dim", "[ ]");
        }

        private string GetItemSuffix(ResourceItem item)
        {
            var theme = ThemeManager.Current;
            if (_writeScope != "project") return "";
            var state = GetProjectOverrideState(item);
            if (state == "load") return theme.Fg("muted", "  project load");
            if (state == "unload") return theme.Fg("muted", "  project unload");
            return IsInheritedGlobalItem(item) ? theme.Fg("dim", "  inherited global") : "";
        }

        private bool IsDimmedItem(ResourceItem item) => _writeScope == "project" && IsInheritedGlobalItem(item) && GetProjectOverrideState(item) == "inherit";

        private bool SetProjectResourceOverride(ResourceItem item, string state) => SetProjectTopLevelOverride(item, state);

        private bool SetProjectTopLevelOverride(ResourceItem item, string state)
        {
            var current = StringArray(_settingsManager.GetProjectSettings()[item.ResourceType]);
            var inherited = IsInheritedGlobalItem(item);
            var pattern = inherited ? item.Path : GetResourcePatternForScope(item, "project");
            var patterns = GetTopLevelOverridePatterns(item, "project");
            var updated = current.Where(entry =>
            {
                var target = PatternTarget(entry);
                if ((entry.StartsWith('!') || entry.StartsWith('+') || entry.StartsWith('-')) && patterns.Contains(target)) return false;
                return !(state == "inherit" && inherited && target == pattern);
            }).ToList();
            if (state != "inherit")
            {
                if (inherited && !updated.Contains(pattern)) updated.Add(pattern);
                updated.Add($"{(state == "load" ? "+" : "-")}{pattern}");
            }
            SetTopLevelPaths("project", item.ResourceType, updated);
            return true;
        }

        private string GetNextOverrideState(ResourceItem item)
        {
            var state = GetProjectOverrideState(item);
            var inheritedEnabled = GetInheritedEnabled(item);
            return state switch
            {
                "inherit" => inheritedEnabled ? "unload" : "load",
                "unload" => inheritedEnabled ? "load" : "inherit",
                _ => inheritedEnabled ? "inherit" : "unload",
            };
        }

        private string GetProjectOverrideState(ResourceItem item)
        {
            if (_writeScope != "project") return "inherit";
            return GetOverrideStateFromEntries(StringArray(_settingsManager.GetProjectSettings()[item.ResourceType]), GetTopLevelOverridePatterns(item, "project"), false);
        }

        private static string GetOverrideStateFromEntries(List<string> entries, HashSet<string> patterns, bool emptyArrayIsUnload)
        {
            if (entries.Count == 0 && emptyArrayIsUnload) return "unload";
            var state = "inherit";
            foreach (var entry in entries)
            {
                if (!patterns.Contains(PatternTarget(entry))) continue;
                state = entry.StartsWith('!') || entry.StartsWith('-') ? "unload" : "load";
            }
            return state;
        }

        private bool GetInheritedEnabled(ResourceItem item) =>
            _inheritedEnabledByKey.TryGetValue(GetResourceItemKey(item), out var enabled) ? enabled : GetItemScope(item) != "user" || item.Enabled;

        private bool IsInheritedGlobalItem(ResourceItem item) => GetItemScope(item) == "user" || _inheritedEnabledByKey.ContainsKey(GetResourceItemKey(item));

        private HashSet<string> GetTopLevelOverridePatterns(ResourceItem item, string scope)
        {
            var baseDir = GetTopLevelBaseDir(scope);
            var patterns = new HashSet<string> { GetResourcePatternForScope(item, scope), item.Path, Relative(baseDir, item.Path) };
            if (item.Metadata.BaseDir is not null) patterns.Add(Relative(item.Metadata.BaseDir, item.Path));
            return patterns;
        }

        private string GetResourcePatternForScope(ResourceItem item, string scope)
        {
            var sourceScope = GetItemScope(item);
            if (scope != sourceScope) return item.Path;
            return Relative(item.Metadata.BaseDir ?? GetTopLevelBaseDir(sourceScope), item.Path);
        }

        private static string GetResourceItemKey(ResourceItem item) => $"{item.ResourceType}:{PathUtils.CanonicalizePath(item.Path)}";

        private static string GetItemScope(ResourceItem item) => item.Metadata.Scope == "project" ? "project" : "user";

        private string GetTopLevelBaseDir(string scope) => scope == "project" ? System.IO.Path.Combine(_cwd, AppConfig.ConfigDirName) : _agentDir;

        private string GetResourcePattern(ResourceItem item) => Relative(item.Metadata.BaseDir ?? GetTopLevelBaseDir(item.Metadata.Scope), item.Path);
    }
}
