using System.Globalization;
using System.Text.RegularExpressions;

namespace Iris.Tui;

/// <summary>
/// Keyboard input parsing for legacy terminal sequences, xterm modifyOtherKeys and the Kitty keyboard protocol. Key ids
/// look like "ctrl+c", "shift+tab", "alt+enter". Port of pi-tui keys.ts.
/// </summary>
public static partial class Keys
{
    private static volatile bool _kittyProtocolActive;

    public static void SetKittyProtocolActive(bool active) => _kittyProtocolActive = active;

    public static bool IsKittyProtocolActive => _kittyProtocolActive;

    private const string SymbolKeys = "`-=[]\\;',./!@#$%^&*()_+|~{}:<>?";

    private const int Shift = 1, Alt = 2, Ctrl = 4, Super = 8;
    private const int LockMask = 64 + 128;

    private const int CpEscape = 27, CpTab = 9, CpEnter = 13, CpSpace = 32, CpBackspace = 127, CpKpEnter = 57414;
    private const int ArrowUp = -1, ArrowDown = -2, ArrowRight = -3, ArrowLeft = -4;
    private const int FnDelete = -10, FnInsert = -11, FnPageUp = -12, FnPageDown = -13, FnHome = -14, FnEnd = -15;

    private static bool IsSymbolKey(string key) => key.Length == 1 && SymbolKeys.Contains(key[0]);

    private static readonly Dictionary<int, int> KittyFunctionalEquivalents = new()
    {
        [57399] = 48, [57400] = 49, [57401] = 50, [57402] = 51, [57403] = 52, [57404] = 53, [57405] = 54, [57406] = 55,
        [57407] = 56, [57408] = 57, [57409] = 46, [57410] = 47, [57411] = 42, [57412] = 45, [57413] = 43, [57415] = 61,
        [57416] = 44, [57417] = ArrowLeft, [57418] = ArrowRight, [57419] = ArrowUp, [57420] = ArrowDown,
        [57421] = FnPageUp, [57422] = FnPageDown, [57423] = FnHome, [57424] = FnEnd, [57425] = FnInsert, [57426] = FnDelete,
    };

    private static int NormalizeKittyFunctional(int cp) => KittyFunctionalEquivalents.GetValueOrDefault(cp, cp);

    private static int NormalizeShiftedLetter(int cp, int modifier) =>
        ((modifier & ~LockMask) & Shift) != 0 && cp >= 65 && cp <= 90 ? cp + 32 : cp;

    private static readonly Dictionary<string, string[]> LegacyKeySequences = new()
    {
        ["up"] = ["\e[A", "\eOA"], ["down"] = ["\e[B", "\eOB"], ["right"] = ["\e[C", "\eOC"], ["left"] = ["\e[D", "\eOD"],
        ["home"] = ["\e[H", "\eOH", "\e[1~", "\e[7~"], ["end"] = ["\e[F", "\eOF", "\e[4~", "\e[8~"],
        ["insert"] = ["\e[2~"], ["delete"] = ["\e[3~"], ["pageUp"] = ["\e[5~", "\e[[5~"], ["pageDown"] = ["\e[6~", "\e[[6~"],
        ["clear"] = ["\e[E", "\eOE"],
        ["f1"] = ["\eOP", "\e[11~", "\e[[A"], ["f2"] = ["\eOQ", "\e[12~", "\e[[B"], ["f3"] = ["\eOR", "\e[13~", "\e[[C"],
        ["f4"] = ["\eOS", "\e[14~", "\e[[D"], ["f5"] = ["\e[15~", "\e[[E"], ["f6"] = ["\e[17~"], ["f7"] = ["\e[18~"],
        ["f8"] = ["\e[19~"], ["f9"] = ["\e[20~"], ["f10"] = ["\e[21~"], ["f11"] = ["\e[23~"], ["f12"] = ["\e[24~"],
    };

    private static readonly Dictionary<string, string[]> LegacyShiftSequences = new()
    {
        ["up"] = ["\e[a"], ["down"] = ["\e[b"], ["right"] = ["\e[c"], ["left"] = ["\e[d"], ["clear"] = ["\e[e"],
        ["insert"] = ["\e[2$"], ["delete"] = ["\e[3$"], ["pageUp"] = ["\e[5$"], ["pageDown"] = ["\e[6$"], ["home"] = ["\e[7$"], ["end"] = ["\e[8$"],
    };

    private static readonly Dictionary<string, string[]> LegacyCtrlSequences = new()
    {
        ["up"] = ["\eOa"], ["down"] = ["\eOb"], ["right"] = ["\eOc"], ["left"] = ["\eOd"], ["clear"] = ["\eOe"],
        ["insert"] = ["\e[2^"], ["delete"] = ["\e[3^"], ["pageUp"] = ["\e[5^"], ["pageDown"] = ["\e[6^"], ["home"] = ["\e[7^"], ["end"] = ["\e[8^"],
    };

    private static readonly Dictionary<string, string> LegacySequenceKeyIds = new()
    {
        ["\eOA"] = "up", ["\eOB"] = "down", ["\eOC"] = "right", ["\eOD"] = "left", ["\eOH"] = "home", ["\eOF"] = "end",
        ["\e[E"] = "clear", ["\eOE"] = "clear", ["\eOe"] = "ctrl+clear", ["\e[e"] = "shift+clear",
        ["\e[2~"] = "insert", ["\e[2$"] = "shift+insert", ["\e[2^"] = "ctrl+insert", ["\e[3$"] = "shift+delete", ["\e[3^"] = "ctrl+delete",
        ["\e[[5~"] = "pageUp", ["\e[[6~"] = "pageDown",
        ["\e[a"] = "shift+up", ["\e[b"] = "shift+down", ["\e[c"] = "shift+right", ["\e[d"] = "shift+left",
        ["\eOa"] = "ctrl+up", ["\eOb"] = "ctrl+down", ["\eOc"] = "ctrl+right", ["\eOd"] = "ctrl+left",
        ["\e[5$"] = "shift+pageUp", ["\e[6$"] = "shift+pageDown", ["\e[7$"] = "shift+home", ["\e[8$"] = "shift+end",
        ["\e[5^"] = "ctrl+pageUp", ["\e[6^"] = "ctrl+pageDown", ["\e[7^"] = "ctrl+home", ["\e[8^"] = "ctrl+end",
        ["\eOP"] = "f1", ["\eOQ"] = "f2", ["\eOR"] = "f3", ["\eOS"] = "f4",
        ["\e[11~"] = "f1", ["\e[12~"] = "f2", ["\e[13~"] = "f3", ["\e[14~"] = "f4",
        ["\e[[A"] = "f1", ["\e[[B"] = "f2", ["\e[[C"] = "f3", ["\e[[D"] = "f4", ["\e[[E"] = "f5",
        ["\e[15~"] = "f5", ["\e[17~"] = "f6", ["\e[18~"] = "f7", ["\e[19~"] = "f8", ["\e[20~"] = "f9", ["\e[21~"] = "f10",
        ["\e[23~"] = "f11", ["\e[24~"] = "f12",
        ["\eb"] = "alt+left", ["\ef"] = "alt+right", ["\ep"] = "alt+up", ["\en"] = "alt+down",
    };

    private static bool MatchesLegacyModifier(string data, string key, int modifier) => modifier switch
    {
        Shift => LegacyShiftSequences.TryGetValue(key, out var s) && s.Contains(data),
        Ctrl => LegacyCtrlSequences.TryGetValue(key, out var c) && c.Contains(data),
        _ => false,
    };

    private static bool MatchesLegacy(string data, string key) => LegacyKeySequences.TryGetValue(key, out var seqs) && seqs.Contains(data);

    // ---- Kitty ----

    private sealed record KittySequence(int Codepoint, int? ShiftedKey, int? BaseLayoutKey, int Modifier, string EventType);

    [GeneratedRegex("^\\e\\[(\\d+)(?::(\\d*))?(?::(\\d+))?(?:;(\\d+))?(?::(\\d+))?u$")]
    private static partial Regex CsiU();

    [GeneratedRegex("^\\e\\[1;(\\d+)(?::(\\d+))?([ABCD])$")]
    private static partial Regex ArrowMod();

    [GeneratedRegex("^\\e\\[(\\d+)(?:;(\\d+))?(?::(\\d+))?~$")]
    private static partial Regex FuncKey();

    [GeneratedRegex("^\\e\\[1;(\\d+)(?::(\\d+))?([HF])$")]
    private static partial Regex HomeEndMod();

    [GeneratedRegex("^\\e\\[27;(\\d+);(\\d+)~$")]
    private static partial Regex ModifyOtherKeys();

    private static int ParseInt(string s) => int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static string ParseEventType(Group group) =>
        !group.Success || group.Value.Length == 0 ? "press" : ParseInt(group.Value) switch { 2 => "repeat", 3 => "release", _ => "press" };

    /// <summary>Whether data is a Kitty key release event (never true for bracketed paste content).</summary>
    public static bool IsKeyRelease(string data)
    {
        if (data.Contains("\e[200~")) return false;
        return data.Contains(":3u") || data.Contains(":3~") || data.Contains(":3A") || data.Contains(":3B")
            || data.Contains(":3C") || data.Contains(":3D") || data.Contains(":3H") || data.Contains(":3F");
    }

    public static bool IsKeyRepeat(string data)
    {
        if (data.Contains("\e[200~")) return false;
        return data.Contains(":2u") || data.Contains(":2~") || data.Contains(":2A") || data.Contains(":2B")
            || data.Contains(":2C") || data.Contains(":2D") || data.Contains(":2H") || data.Contains(":2F");
    }

    private static KittySequence? ParseKittySequence(string data)
    {
        var m = CsiU().Match(data);
        if (m.Success)
        {
            var codepoint = ParseInt(m.Groups[1].Value);
            int? shifted = m.Groups[2].Success && m.Groups[2].Value.Length > 0 ? ParseInt(m.Groups[2].Value) : null;
            int? baseKey = m.Groups[3].Success ? ParseInt(m.Groups[3].Value) : null;
            var mod = m.Groups[4].Success ? ParseInt(m.Groups[4].Value) : 1;
            return new KittySequence(codepoint, shifted, baseKey, mod - 1, ParseEventType(m.Groups[5]));
        }

        m = ArrowMod().Match(data);
        if (m.Success)
        {
            var code = m.Groups[3].Value switch { "A" => ArrowUp, "B" => ArrowDown, "C" => ArrowRight, _ => ArrowLeft };
            return new KittySequence(code, null, null, ParseInt(m.Groups[1].Value) - 1, ParseEventType(m.Groups[2]));
        }

        m = FuncKey().Match(data);
        if (m.Success)
        {
            int? code = ParseInt(m.Groups[1].Value) switch { 2 => FnInsert, 3 => FnDelete, 5 => FnPageUp, 6 => FnPageDown, 7 => FnHome, 8 => FnEnd, _ => null };
            if (code is not null)
            {
                var mod = m.Groups[2].Success ? ParseInt(m.Groups[2].Value) : 1;
                return new KittySequence(code.Value, null, null, mod - 1, ParseEventType(m.Groups[3]));
            }
        }

        m = HomeEndMod().Match(data);
        if (m.Success)
        {
            return new KittySequence(m.Groups[3].Value == "H" ? FnHome : FnEnd, null, null, ParseInt(m.Groups[1].Value) - 1, ParseEventType(m.Groups[2]));
        }
        return null;
    }

    private static bool MatchesKittySequence(string data, int expectedCodepoint, int expectedModifier)
    {
        var parsed = ParseKittySequence(data);
        if (parsed is null) return false;
        if ((parsed.Modifier & ~LockMask) != (expectedModifier & ~LockMask)) return false;

        var normalized = NormalizeShiftedLetter(NormalizeKittyFunctional(parsed.Codepoint), parsed.Modifier);
        var normalizedExpected = NormalizeShiftedLetter(NormalizeKittyFunctional(expectedCodepoint), expectedModifier);
        if (normalized == normalizedExpected) return true;

        if (parsed.BaseLayoutKey is { } baseKey && baseKey == expectedCodepoint)
        {
            var isLatin = normalized is >= 97 and <= 122;
            var isSymbol = normalized is >= 0 and <= 0xFFFF && IsSymbolKey(((char)normalized).ToString());
            if (!isLatin && !isSymbol) return true;
        }
        return false;
    }

    private static (int Codepoint, int Modifier)? ParseModifyOtherKeys(string data)
    {
        var m = ModifyOtherKeys().Match(data);
        return m.Success ? (ParseInt(m.Groups[2].Value), ParseInt(m.Groups[1].Value) - 1) : null;
    }

    private static bool MatchesModifyOtherKeys(string data, int expectedKeycode, int expectedModifier) =>
        ParseModifyOtherKeys(data) is { } p && p.Codepoint == expectedKeycode && p.Modifier == expectedModifier;

    private static bool IsWindowsTerminalSession() =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WT_SESSION"))
        && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SSH_CONNECTION"))
        && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SSH_CLIENT"))
        && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SSH_TTY"));

    private static bool MatchesRawBackspace(string data, int expectedModifier)
    {
        if (data == "\x7f") return expectedModifier == 0;
        if (data != "\b") return false;
        return IsWindowsTerminalSession() ? expectedModifier == Ctrl : expectedModifier == 0;
    }

    private static string? RawCtrlChar(string key)
    {
        var ch = key.ToLowerInvariant()[0];
        if (ch is >= 'a' and <= 'z' or '[' or '\\' or ']' or '_') return ((char)(ch & 0x1f)).ToString();
        if (ch == '-') return ((char)31).ToString();
        return null;
    }

    private static bool MatchesPrintableModifyOtherKeys(string data, int expectedKeycode, int expectedModifier)
    {
        if (expectedModifier == 0) return false;
        if (ParseModifyOtherKeys(data) is not { } p || p.Modifier != expectedModifier) return false;
        return NormalizeShiftedLetter(p.Codepoint, p.Modifier) == NormalizeShiftedLetter(expectedKeycode, expectedModifier);
    }

    private static string? FormatKeyNameWithModifiers(string keyName, int modifier)
    {
        var effective = modifier & ~LockMask;
        if ((effective & ~(Shift | Ctrl | Alt | Super)) != 0) return null;
        var mods = new List<string>();
        if ((effective & Shift) != 0) mods.Add("shift");
        if ((effective & Ctrl) != 0) mods.Add("ctrl");
        if ((effective & Alt) != 0) mods.Add("alt");
        if ((effective & Super) != 0) mods.Add("super");
        return mods.Count > 0 ? $"{string.Join("+", mods)}+{keyName}" : keyName;
    }

    /// <summary>Match raw terminal input against a key id such as "ctrl+c", "shift+tab" or "alt+enter".</summary>
    public static bool Matches(string data, string keyId)
    {
        var parts = keyId.ToLowerInvariant().Split('+');
        var key = parts[^1];
        if (key.Length == 0) return false;
        var modifier = 0;
        if (parts.Contains("shift")) modifier |= Shift;
        if (parts.Contains("alt")) modifier |= Alt;
        if (parts.Contains("ctrl")) modifier |= Ctrl;
        if (parts.Contains("super")) modifier |= Super;
        var kitty = _kittyProtocolActive;

        switch (key)
        {
            case "escape" or "esc":
                if (modifier != 0) return false;
                return data == "\e" || MatchesKittySequence(data, CpEscape, 0) || MatchesModifyOtherKeys(data, CpEscape, 0);

            case "space":
                if (!kitty)
                {
                    if (modifier == Ctrl && data == "\0") return true;
                    if (modifier == Alt && data == "\e ") return true;
                }
                if (modifier == 0) return data == " " || MatchesKittySequence(data, CpSpace, 0) || MatchesModifyOtherKeys(data, CpSpace, 0);
                return MatchesKittySequence(data, CpSpace, modifier) || MatchesModifyOtherKeys(data, CpSpace, modifier);

            case "tab":
                if (modifier == Shift) return data == "\e[Z" || MatchesKittySequence(data, CpTab, Shift) || MatchesModifyOtherKeys(data, CpTab, Shift);
                if (modifier == 0) return data == "\t" || MatchesKittySequence(data, CpTab, 0);
                return MatchesKittySequence(data, CpTab, modifier) || MatchesModifyOtherKeys(data, CpTab, modifier);

            case "enter" or "return":
                if (modifier == Shift)
                {
                    if (MatchesKittySequence(data, CpEnter, Shift) || MatchesKittySequence(data, CpKpEnter, Shift)) return true;
                    if (MatchesModifyOtherKeys(data, CpEnter, Shift)) return true;
                    return kitty && (data == "\e\r" || data == "\n");
                }
                if (modifier == Alt)
                {
                    if (MatchesKittySequence(data, CpEnter, Alt) || MatchesKittySequence(data, CpKpEnter, Alt)) return true;
                    if (MatchesModifyOtherKeys(data, CpEnter, Alt)) return true;
                    return !kitty && data == "\e\r";
                }
                if (modifier == 0)
                {
                    return data == "\r" || (!kitty && data == "\n") || data == "\eOM"
                        || MatchesKittySequence(data, CpEnter, 0) || MatchesKittySequence(data, CpKpEnter, 0);
                }
                return MatchesKittySequence(data, CpEnter, modifier) || MatchesKittySequence(data, CpKpEnter, modifier) || MatchesModifyOtherKeys(data, CpEnter, modifier);

            case "backspace":
                if (modifier == Alt)
                {
                    if (data is "\e\x7f" or "\e\b") return true;
                    return MatchesKittySequence(data, CpBackspace, Alt) || MatchesModifyOtherKeys(data, CpBackspace, Alt);
                }
                if (modifier == Ctrl)
                {
                    if (MatchesRawBackspace(data, Ctrl)) return true;
                    return MatchesKittySequence(data, CpBackspace, Ctrl) || MatchesModifyOtherKeys(data, CpBackspace, Ctrl);
                }
                if (modifier == 0) return MatchesRawBackspace(data, 0) || MatchesKittySequence(data, CpBackspace, 0) || MatchesModifyOtherKeys(data, CpBackspace, 0);
                return MatchesKittySequence(data, CpBackspace, modifier) || MatchesModifyOtherKeys(data, CpBackspace, modifier);

            case "insert":
                return MatchFunctional(data, "insert", FnInsert, modifier);
            case "delete":
                return MatchFunctional(data, "delete", FnDelete, modifier);
            case "clear":
                return modifier == 0 ? MatchesLegacy(data, "clear") : MatchesLegacyModifier(data, "clear", modifier);
            case "home":
                return MatchFunctional(data, "home", FnHome, modifier);
            case "end":
                return MatchFunctional(data, "end", FnEnd, modifier);
            case "pageup":
                return MatchFunctional(data, "pageUp", FnPageUp, modifier);
            case "pagedown":
                return MatchFunctional(data, "pageDown", FnPageDown, modifier);

            case "up":
                if (modifier == Alt) return data == "\ep" || MatchesKittySequence(data, ArrowUp, Alt);
                return MatchFunctional(data, "up", ArrowUp, modifier);
            case "down":
                if (modifier == Alt) return data == "\en" || MatchesKittySequence(data, ArrowDown, Alt);
                return MatchFunctional(data, "down", ArrowDown, modifier);
            case "left":
                if (modifier == Alt) return data == "\e[1;3D" || (!kitty && data == "\eB") || data == "\eb" || MatchesKittySequence(data, ArrowLeft, Alt);
                if (modifier == Ctrl) return data == "\e[1;5D" || MatchesLegacyModifier(data, "left", Ctrl) || MatchesKittySequence(data, ArrowLeft, Ctrl);
                return MatchFunctional(data, "left", ArrowLeft, modifier);
            case "right":
                if (modifier == Alt) return data == "\e[1;3C" || (!kitty && data == "\eF") || data == "\ef" || MatchesKittySequence(data, ArrowRight, Alt);
                if (modifier == Ctrl) return data == "\e[1;5C" || MatchesLegacyModifier(data, "right", Ctrl) || MatchesKittySequence(data, ArrowRight, Ctrl);
                return MatchFunctional(data, "right", ArrowRight, modifier);

            case "f1" or "f2" or "f3" or "f4" or "f5" or "f6" or "f7" or "f8" or "f9" or "f10" or "f11" or "f12":
                return modifier == 0 && MatchesLegacy(data, key);
        }

        if (key.Length == 1 && (key[0] is >= 'a' and <= 'z' or >= '0' and <= '9' || IsSymbolKey(key)))
        {
            int codepoint = key[0];
            var rawCtrl = RawCtrlChar(key);
            var isLetter = key[0] is >= 'a' and <= 'z';
            var isDigit = key[0] is >= '0' and <= '9';

            if (modifier == Ctrl + Alt && !kitty && rawCtrl is not null && data == "\e" + rawCtrl) return true;
            if (modifier == Alt && !kitty && (isLetter || isDigit || IsSymbolKey(key)) && data == "\e" + key) return true;

            if (modifier == Ctrl)
            {
                if (rawCtrl is not null && data == rawCtrl) return true;
                return MatchesKittySequence(data, codepoint, Ctrl) || MatchesPrintableModifyOtherKeys(data, codepoint, Ctrl);
            }
            if (modifier == Shift + Ctrl)
            {
                return MatchesKittySequence(data, codepoint, Shift + Ctrl) || MatchesPrintableModifyOtherKeys(data, codepoint, Shift + Ctrl);
            }
            if (modifier == Shift)
            {
                if (isLetter && data == key.ToUpperInvariant()) return true;
                return MatchesKittySequence(data, codepoint, Shift) || MatchesPrintableModifyOtherKeys(data, codepoint, Shift);
            }
            if (modifier != 0)
            {
                return MatchesKittySequence(data, codepoint, modifier) || MatchesPrintableModifyOtherKeys(data, codepoint, modifier);
            }
            return data == key || MatchesKittySequence(data, codepoint, 0);
        }
        return false;
    }

    private static bool MatchFunctional(string data, string legacyKey, int codepoint, int modifier)
    {
        if (modifier == 0) return MatchesLegacy(data, legacyKey) || MatchesKittySequence(data, codepoint, 0);
        if (MatchesLegacyModifier(data, legacyKey, modifier)) return true;
        return MatchesKittySequence(data, codepoint, modifier);
    }

    private static string? FormatParsedKey(int codepoint, int modifier, int? baseLayoutKey = null)
    {
        var normalized = NormalizeKittyFunctional(codepoint);
        var identity = NormalizeShiftedLetter(normalized, modifier);
        var isLatin = identity is >= 97 and <= 122;
        var isDigit = identity is >= 48 and <= 57;
        var isSymbol = identity is >= 0 and <= 0xFFFF && IsSymbolKey(((char)identity).ToString());
        var effective = isLatin || isDigit || isSymbol ? identity : baseLayoutKey ?? identity;

        string? keyName = effective switch
        {
            CpEscape => "escape",
            CpTab => "tab",
            CpEnter or CpKpEnter => "enter",
            CpSpace => "space",
            CpBackspace => "backspace",
            FnDelete => "delete",
            FnInsert => "insert",
            FnHome => "home",
            FnEnd => "end",
            FnPageUp => "pageUp",
            FnPageDown => "pageDown",
            ArrowUp => "up",
            ArrowDown => "down",
            ArrowLeft => "left",
            ArrowRight => "right",
            >= 48 and <= 57 or >= 97 and <= 122 => ((char)effective).ToString(),
            _ when effective is >= 0 and <= 0xFFFF && IsSymbolKey(((char)effective).ToString()) => ((char)effective).ToString(),
            _ => null,
        };
        return keyName is null ? null : FormatKeyNameWithModifiers(keyName, modifier);
    }

    /// <summary>Parse raw input into a key id, or null when unrecognized.</summary>
    public static string? Parse(string data)
    {
        if (ParseKittySequence(data) is { } kittySeq) return FormatParsedKey(kittySeq.Codepoint, kittySeq.Modifier, kittySeq.BaseLayoutKey);
        if (ParseModifyOtherKeys(data) is { } mok) return FormatParsedKey(mok.Codepoint, mok.Modifier);

        var kitty = _kittyProtocolActive;
        if (kitty && data is "\e\r" or "\n") return "shift+enter";
        if (LegacySequenceKeyIds.TryGetValue(data, out var legacy)) return legacy;

        switch (data)
        {
            case "\e": return "escape";
            case "\x1c": return "ctrl+\\";
            case "\x1d": return "ctrl+]";
            case "\x1f": return "ctrl+-";
            case "\e\e": return "ctrl+alt+[";
            case "\e\x1c": return "ctrl+alt+\\";
            case "\e\x1d": return "ctrl+alt+]";
            case "\e\x1f": return "ctrl+alt+-";
            case "\t": return "tab";
        }
        if (data == "\r" || (!kitty && data == "\n") || data == "\eOM") return "enter";
        if (data == "\0") return "ctrl+space";
        if (data == " ") return "space";
        if (data == "\x7f") return "backspace";
        if (data == "\b") return IsWindowsTerminalSession() ? "ctrl+backspace" : "backspace";
        if (data == "\e[Z") return "shift+tab";
        if (!kitty && data == "\e\r") return "alt+enter";
        if (!kitty && data == "\e ") return "alt+space";
        if (data is "\e\x7f" or "\e\b") return "alt+backspace";
        if (!kitty && data == "\eB") return "alt+left";
        if (!kitty && data == "\eF") return "alt+right";
        if (!kitty && data.Length == 2 && data[0] == '\e')
        {
            int code = data[1];
            if (code is >= 1 and <= 26) return $"ctrl+alt+{(char)(code + 96)}";
            var k = data[1].ToString();
            if (code is >= 97 and <= 122 or >= 48 and <= 57 || IsSymbolKey(k)) return $"alt+{k}";
        }
        switch (data)
        {
            case "\e[A": return "up";
            case "\e[B": return "down";
            case "\e[C": return "right";
            case "\e[D": return "left";
            case "\e[H" or "\eOH": return "home";
            case "\e[F" or "\eOF": return "end";
            case "\e[3~": return "delete";
            case "\e[5~": return "pageUp";
            case "\e[6~": return "pageDown";
        }
        if (data.Length == 1)
        {
            int code = data[0];
            if (code is >= 1 and <= 26) return $"ctrl+{(char)(code + 96)}";
            if (code is >= 32 and <= 126) return data;
        }
        return null;
    }

    private const int KittyPrintableAllowedModifiers = Shift | LockMask;

    /// <summary>Decode a Kitty CSI-u sequence carrying a plain or shifted printable character.</summary>
    public static string? DecodeKittyPrintable(string data)
    {
        var m = CsiU().Match(data);
        if (!m.Success) return null;
        var codepoint = ParseInt(m.Groups[1].Value);
        int? shifted = m.Groups[2].Success && m.Groups[2].Value.Length > 0 ? ParseInt(m.Groups[2].Value) : null;
        var modValue = m.Groups[4].Success ? ParseInt(m.Groups[4].Value) : 1;
        var modifier = modValue - 1;
        if ((modifier & ~KittyPrintableAllowedModifiers) != 0) return null;
        if ((modifier & (Alt | Ctrl)) != 0) return null;
        var effective = (modifier & Shift) != 0 && shifted is { } s ? s : codepoint;
        effective = NormalizeKittyFunctional(effective);
        if (effective < 32 || effective > 0x10FFFF || effective is >= 0xD800 and <= 0xDFFF) return null;
        return char.ConvertFromUtf32(effective);
    }

    private static string? DecodeModifyOtherKeysPrintable(string data)
    {
        if (ParseModifyOtherKeys(data) is not { } p) return null;
        if (((p.Modifier & ~LockMask) & ~Shift) != 0) return null;
        if (p.Codepoint < 32 || p.Codepoint > 0x10FFFF || p.Codepoint is >= 0xD800 and <= 0xDFFF) return null;
        return char.ConvertFromUtf32(p.Codepoint);
    }

    public static string? DecodePrintableKey(string data) => DecodeKittyPrintable(data) ?? DecodeModifyOtherKeysPrintable(data);
}

public sealed record KeybindingDefinition(IReadOnlyList<string> DefaultKeys, string? Description = null);

public sealed record KeybindingConflict(string Key, IReadOnlyList<string> Keybindings);

/// <summary>Keybinding registry with user overrides. Port of pi-tui keybindings.ts.</summary>
public sealed class KeybindingsManager
{
    public static readonly IReadOnlyDictionary<string, KeybindingDefinition> TuiKeybindings = new Dictionary<string, KeybindingDefinition>
    {
        ["tui.editor.cursorUp"] = new(["up"], "Move cursor up"),
        ["tui.editor.cursorDown"] = new(["down"], "Move cursor down"),
        ["tui.editor.historyPrevious"] = new([], "Select previous prompt history entry"),
        ["tui.editor.historyNext"] = new([], "Select next prompt history entry"),
        ["tui.editor.cursorLeft"] = new(["left", "ctrl+b"], "Move cursor left"),
        ["tui.editor.cursorRight"] = new(["right", "ctrl+f"], "Move cursor right"),
        ["tui.editor.cursorWordLeft"] = new(["alt+left", "ctrl+left", "alt+b"], "Move cursor word left"),
        ["tui.editor.cursorWordRight"] = new(["alt+right", "ctrl+right", "alt+f"], "Move cursor word right"),
        ["tui.editor.cursorLineStart"] = new(["home", "ctrl+home", "ctrl+a"], "Move to line start"),
        ["tui.editor.cursorLineEnd"] = new(["end", "ctrl+end", "ctrl+e"], "Move to line end"),
        ["tui.editor.jumpForward"] = new(["ctrl+]"], "Jump forward to character"),
        ["tui.editor.jumpBackward"] = new(["ctrl+alt+]"], "Jump backward to character"),
        ["tui.editor.pageUp"] = new(["pageUp", "ctrl+pageUp"], "Page up"),
        ["tui.editor.pageDown"] = new(["pageDown", "ctrl+pageDown"], "Page down"),
        ["tui.editor.deleteCharBackward"] = new(["backspace"], "Delete character backward"),
        ["tui.editor.deleteCharForward"] = new(["delete", "ctrl+d"], "Delete character forward"),
        ["tui.editor.deleteWordBackward"] = new(["ctrl+w", "alt+backspace"], "Delete word backward"),
        ["tui.editor.deleteWordForward"] = new(["alt+d", "alt+delete"], "Delete word forward"),
        ["tui.editor.deleteToLineStart"] = new(["ctrl+u"], "Delete to line start"),
        ["tui.editor.deleteToLineEnd"] = new(["ctrl+k"], "Delete to line end"),
        ["tui.editor.yank"] = new(["ctrl+y"], "Yank"),
        ["tui.editor.yankPop"] = new(["alt+y"], "Yank pop"),
        ["tui.editor.undo"] = new(["ctrl+-"], "Undo"),
        ["tui.input.newLine"] = new(["shift+enter", "ctrl+j"], "Insert newline"),
        ["tui.input.submit"] = new(["enter"], "Submit input"),
        ["tui.input.tab"] = new(["tab"], "Tab / autocomplete"),
        ["tui.input.copy"] = new(["ctrl+c"], "Copy selection"),
        ["tui.select.up"] = new(["up"], "Move selection up"),
        ["tui.select.down"] = new(["down"], "Move selection down"),
        ["tui.select.pageUp"] = new(["pageUp"], "Selection page up"),
        ["tui.select.pageDown"] = new(["pageDown"], "Selection page down"),
        ["tui.select.confirm"] = new(["enter"], "Confirm selection"),
        ["tui.select.cancel"] = new(["escape", "ctrl+c"], "Cancel selection"),
        // These intentionally shadow the unmodified editor bindings in fullscreen mode.
        ["tui.altScreen.pageUp"] = new(["pageUp"], "Scroll viewport up one page"),
        ["tui.altScreen.pageDown"] = new(["pageDown"], "Scroll viewport down one page"),
        ["tui.altScreen.halfPageUp"] = new([], "Scroll viewport up half a page"),
        ["tui.altScreen.halfPageDown"] = new([], "Scroll viewport down half a page"),
        ["tui.altScreen.lineUp"] = new([], "Scroll viewport up one line"),
        ["tui.altScreen.lineDown"] = new([], "Scroll viewport down one line"),
        ["tui.altScreen.previousPrompt"] = new(["ctrl+shift+up", "ctrl+up"], "Jump to previous semantic prompt"),
        ["tui.altScreen.nextPrompt"] = new(["ctrl+shift+down", "ctrl+down"], "Jump to next semantic prompt"),
        ["tui.altScreen.search"] = new(["ctrl+shift+f"], "Search the primary scroll view"),
        ["tui.altScreen.searchNext"] = new(["enter", "ctrl+g"], "Select the next search match"),
        ["tui.altScreen.searchPrevious"] = new(["shift+enter", "ctrl+shift+g"], "Select the previous search match"),
        ["tui.altScreen.searchClose"] = new(["escape"], "Close transcript search"),
        ["tui.altScreen.top"] = new(["home"], "Scroll viewport to top"),
        ["tui.altScreen.bottom"] = new(["end"], "Scroll viewport to bottom"),
    };

    private readonly IReadOnlyDictionary<string, KeybindingDefinition> _definitions;
    private Dictionary<string, IReadOnlyList<string>?> _userBindings;
    private readonly Dictionary<string, List<string>> _keysById = [];
    private List<KeybindingConflict> _conflicts = [];

    public KeybindingsManager(IReadOnlyDictionary<string, KeybindingDefinition> definitions, Dictionary<string, IReadOnlyList<string>?>? userBindings = null)
    {
        _definitions = definitions;
        _userBindings = userBindings ?? [];
        Rebuild();
    }

    private static List<string> NormalizeKeys(IReadOnlyList<string>? keys) => keys is null ? [] : keys.Distinct(StringComparer.Ordinal).ToList();

    private void Rebuild()
    {
        _keysById.Clear();
        _conflicts = [];
        var claims = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (id, keys) in _userBindings)
        {
            if (!_definitions.ContainsKey(id)) continue;
            foreach (var key in NormalizeKeys(keys))
            {
                if (!claims.TryGetValue(key, out var list)) claims[key] = list = [];
                if (!list.Contains(id)) list.Add(id);
            }
        }
        foreach (var (key, ids) in claims)
        {
            if (ids.Count > 1) _conflicts.Add(new KeybindingConflict(key, ids));
        }
        foreach (var (id, definition) in _definitions)
        {
            _keysById[id] = _userBindings.TryGetValue(id, out var user) && user is not null ? NormalizeKeys(user) : NormalizeKeys(definition.DefaultKeys);
        }
    }

    public bool Matches(string data, string keybinding)
    {
        if (!_keysById.TryGetValue(keybinding, out var keys)) return false;
        foreach (var key in keys)
        {
            if (Keys.Matches(data, key)) return true;
        }
        return false;
    }

    public IReadOnlyList<string> GetKeys(string keybinding) => _keysById.TryGetValue(keybinding, out var keys) ? [.. keys] : [];

    /// <summary>Effective keys for every definition (pi-tui getResolvedBindings).</summary>
    public Dictionary<string, IReadOnlyList<string>> GetResolvedBindings() => _keysById.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)[.. kv.Value]);

    public KeybindingDefinition? GetDefinition(string keybinding) => _definitions.GetValueOrDefault(keybinding);

    public IReadOnlyList<KeybindingConflict> GetConflicts() => [.. _conflicts];

    public void SetUserBindings(Dictionary<string, IReadOnlyList<string>?> userBindings)
    {
        _userBindings = userBindings;
        Rebuild();
    }

    public Dictionary<string, IReadOnlyList<string>?> GetUserBindings() => new(_userBindings);

    public IReadOnlyDictionary<string, KeybindingDefinition> Definitions => _definitions;

    private static KeybindingsManager? _global;

    public static KeybindingsManager Global
    {
        get => _global ??= new KeybindingsManager(TuiKeybindings);
        set => _global = value;
    }
}
