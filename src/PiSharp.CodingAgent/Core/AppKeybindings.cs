using System.Text.Json.Nodes;
using PiSharp.CodingAgent.Config;
using PiSharp.CodingAgent.Utils;
using PiSharp.Tui;

namespace PiSharp.CodingAgent.Core;

/// <summary>Coding-agent keybinding definitions and keybindings.json loading. Port of core/keybindings.ts.</summary>
public static class AppKeybindings
{
    public static bool UseWindowsKeybindings() =>
        OperatingSystem.IsWindows() || (OperatingSystem.IsLinux() && (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WSL_DISTRO_NAME")) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WSL_INTEROP"))));

    public static readonly IReadOnlyDictionary<string, KeybindingDefinition> Definitions = BuildDefinitions();

    private static Dictionary<string, KeybindingDefinition> BuildDefinitions()
    {
        var windows = UseWindowsKeybindings();
        var defs = new Dictionary<string, KeybindingDefinition>();
        foreach (var (id, def) in KeybindingsManager.TuiKeybindings) defs[id] = def;
        defs["tui.editor.undo"] = defs["tui.editor.undo"] with { DefaultKeys = [OperatingSystem.IsWindows() ? "ctrl+z" : windows ? "alt+z" : "ctrl+-"] };

        void Add(string id, string description, params string[] keys) => defs[id] = new KeybindingDefinition(keys, description);

        Add("app.interrupt", "Cancel or abort", "escape");
        Add("app.clear", "Clear editor", "ctrl+c");
        Add("app.exit", "Exit when editor is empty", "ctrl+d");
        Add("app.suspend", "Suspend to background", OperatingSystem.IsWindows() ? [] : ["ctrl+z"]);
        Add("app.thinking.cycle", "Cycle thinking level", "shift+tab");
        Add("app.thinking.save", "Save thinking level", "ctrl+s");
        Add("app.model.cycleForward", "Cycle to next model", "ctrl+p");
        Add("app.model.cycleBackward", "Cycle to previous model", windows ? "alt+p" : "shift+ctrl+p");
        Add("app.model.select", "Open model selector", "ctrl+l");
        Add("app.tools.expand", "Toggle tool output", "ctrl+o");
        Add("app.thinking.toggle", "Toggle thinking blocks", "ctrl+t");
        Add("app.session.toggleNamedFilter", "Toggle named session filter", "ctrl+n");
        Add("app.editor.external", "Open external editor", "ctrl+g");
        Add("app.message.copy", "Copy message to clipboard", "ctrl+x");
        Add("app.message.followUp", "Queue follow-up message", windows ? "ctrl+q" : "alt+enter");
        Add("app.message.dequeue", "Restore queued messages", windows ? "alt+q" : "alt+up");
        Add("app.clipboard.pasteImage", "Paste image from clipboard (text fallback)", windows ? "alt+v" : "ctrl+v");
        Add("app.session.new", "Start a new session");
        Add("app.session.tree", "Open session tree");
        Add("app.session.fork", "Fork current session");
        Add("app.session.resume", "Resume a session");
        Add("app.tree.foldOrUp", "Fold tree branch or move up", OperatingSystem.IsMacOS() ? ["alt+left", "ctrl+left"] : ["ctrl+left", "alt+left"]);
        Add("app.tree.unfoldOrDown", "Unfold tree branch or move down", OperatingSystem.IsMacOS() ? ["alt+right", "ctrl+right"] : ["ctrl+right", "alt+right"]);
        Add("app.tree.editLabel", "Edit tree label", "shift+l");
        Add("app.tree.toggleLabelTimestamp", "Toggle tree label timestamps", "shift+t");
        Add("app.session.togglePath", "Toggle session path display", "ctrl+p");
        Add("app.session.toggleSort", "Toggle session sort mode", "ctrl+s");
        Add("app.session.rename", "Rename session", "ctrl+r");
        Add("app.session.delete", "Delete session", "ctrl+d");
        Add("app.session.deleteNoninvasive", "Delete session when query is empty", "ctrl+backspace");
        Add("app.models.save", "Save model selection", "ctrl+s");
        Add("app.models.enableAll", "Enable all models", "ctrl+a");
        Add("app.models.clearAll", "Clear all models", "ctrl+x");
        Add("app.models.toggleProvider", "Toggle all models for provider", "ctrl+p");
        Add("app.models.reorderUp", "Move model up in order", "alt+up");
        Add("app.models.reorderDown", "Move model down in order", "alt+down");
        Add("app.tree.filter.default", "Tree filter: default view", "ctrl+d");
        Add("app.tree.filter.noTools", "Tree filter: hide tool results", "ctrl+t");
        Add("app.tree.filter.userOnly", "Tree filter: user messages only", "ctrl+u");
        Add("app.tree.filter.labeledOnly", "Tree filter: labeled entries only", "ctrl+l");
        Add("app.tree.filter.all", "Tree filter: show all entries", "ctrl+a");
        Add("app.tree.filter.cycleForward", "Tree filter: cycle forward", "ctrl+o");
        Add("app.tree.filter.cycleBackward", "Tree filter: cycle backward", "shift+ctrl+o");
        return defs;
    }

    private static readonly Dictionary<string, string> NameMigrations = new()
    {
        ["cursorUp"] = "tui.editor.cursorUp", ["cursorDown"] = "tui.editor.cursorDown", ["cursorLeft"] = "tui.editor.cursorLeft",
        ["cursorRight"] = "tui.editor.cursorRight", ["cursorWordLeft"] = "tui.editor.cursorWordLeft", ["cursorWordRight"] = "tui.editor.cursorWordRight",
        ["cursorLineStart"] = "tui.editor.cursorLineStart", ["cursorLineEnd"] = "tui.editor.cursorLineEnd", ["jumpForward"] = "tui.editor.jumpForward",
        ["jumpBackward"] = "tui.editor.jumpBackward", ["pageUp"] = "tui.editor.pageUp", ["pageDown"] = "tui.editor.pageDown",
        ["deleteCharBackward"] = "tui.editor.deleteCharBackward", ["deleteCharForward"] = "tui.editor.deleteCharForward",
        ["deleteWordBackward"] = "tui.editor.deleteWordBackward", ["deleteWordForward"] = "tui.editor.deleteWordForward",
        ["deleteToLineStart"] = "tui.editor.deleteToLineStart", ["deleteToLineEnd"] = "tui.editor.deleteToLineEnd", ["yank"] = "tui.editor.yank",
        ["yankPop"] = "tui.editor.yankPop", ["undo"] = "tui.editor.undo", ["newLine"] = "tui.input.newLine", ["submit"] = "tui.input.submit",
        ["tab"] = "tui.input.tab", ["copy"] = "tui.input.copy", ["selectUp"] = "tui.select.up", ["selectDown"] = "tui.select.down",
        ["selectPageUp"] = "tui.select.pageUp", ["selectPageDown"] = "tui.select.pageDown", ["selectConfirm"] = "tui.select.confirm",
        ["selectCancel"] = "tui.select.cancel", ["interrupt"] = "app.interrupt", ["clear"] = "app.clear", ["exit"] = "app.exit",
        ["suspend"] = "app.suspend", ["cycleThinkingLevel"] = "app.thinking.cycle", ["cycleModelForward"] = "app.model.cycleForward",
        ["cycleModelBackward"] = "app.model.cycleBackward", ["selectModel"] = "app.model.select", ["expandTools"] = "app.tools.expand",
        ["toggleThinking"] = "app.thinking.toggle", ["toggleSessionNamedFilter"] = "app.session.toggleNamedFilter",
        ["externalEditor"] = "app.editor.external", ["followUp"] = "app.message.followUp", ["dequeue"] = "app.message.dequeue",
        ["pasteImage"] = "app.clipboard.pasteImage", ["newSession"] = "app.session.new", ["tree"] = "app.session.tree", ["fork"] = "app.session.fork",
        ["resume"] = "app.session.resume", ["treeFoldOrUp"] = "app.tree.foldOrUp", ["treeUnfoldOrDown"] = "app.tree.unfoldOrDown",
        ["treeEditLabel"] = "app.tree.editLabel", ["treeToggleLabelTimestamp"] = "app.tree.toggleLabelTimestamp",
        ["toggleSessionPath"] = "app.session.togglePath", ["toggleSessionSort"] = "app.session.toggleSort", ["renameSession"] = "app.session.rename",
        ["deleteSession"] = "app.session.delete", ["deleteSessionNoninvasive"] = "app.session.deleteNoninvasive",
    };

    public static (JsonObject Config, bool Migrated) MigrateKeybindingsConfig(JsonObject rawConfig)
    {
        var config = new JsonObject();
        var migrated = false;
        foreach (var (key, value) in rawConfig)
        {
            var nextKey = NameMigrations.GetValueOrDefault(key, key);
            if (nextKey != key) migrated = true;
            if (key != nextKey && rawConfig.ContainsKey(nextKey))
            {
                migrated = true;
                continue;
            }
            config[nextKey] = value?.DeepClone();
        }
        return (OrderConfig(config), migrated);
    }

    private static JsonObject OrderConfig(JsonObject config)
    {
        var ordered = new JsonObject();
        foreach (var id in Definitions.Keys)
        {
            if (config.ContainsKey(id)) ordered[id] = config[id]?.DeepClone();
        }
        foreach (var key in config.Select(kv => kv.Key).Where(k => !ordered.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal))
        {
            ordered[key] = config[key]?.DeepClone();
        }
        return ordered;
    }

    private static Dictionary<string, IReadOnlyList<string>?> LoadFromFile(string path)
    {
        var result = new Dictionary<string, IReadOnlyList<string>?>();
        if (!File.Exists(path)) return result;
        JsonObject? raw;
        try
        {
            raw = JsonNode.Parse(TextHelpers.StripBom(File.ReadAllText(path))) as JsonObject;
        }
        catch
        {
            return result;
        }
        if (raw is null) return result;
        foreach (var (key, binding) in MigrateKeybindingsConfig(raw).Config)
        {
            if (binding is JsonValue v && v.TryGetValue<string>(out var single)) result[key] = [single];
            else if (binding is JsonArray array && array.All(e => e is JsonValue ev && ev.TryGetValue<string>(out _))) result[key] = array.Select(e => e!.GetValue<string>()).ToList();
        }
        return result;
    }

    public static string ConfigPath(string? agentDir = null) => Path.Combine(agentDir ?? AppConfig.AgentDir, "keybindings.json");

    public static KeybindingsManager Create(string? agentDir = null) => new(Definitions, LoadFromFile(ConfigPath(agentDir)));

    public static void Reload(KeybindingsManager manager, string? agentDir = null) => manager.SetUserBindings(LoadFromFile(ConfigPath(agentDir)));
}
