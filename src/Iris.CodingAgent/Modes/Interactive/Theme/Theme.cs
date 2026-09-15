using System.Globalization;
using System.Text.Json.Nodes;
using Iris.Ai;
using Iris.Ai.Json;
using Iris.CodingAgent.Config;
using Iris.CodingAgent.Core;
using Iris.CodingAgent.Utils;
using Iris.Tui;
using Iris.Tui.Components;

namespace Iris.CodingAgent.Modes.Interactive;

/// <summary>chalk 5 modifiers (bold, italic, ...) including its nested-close and line-break handling.</summary>
public static class AnsiStyle
{
    private static string Apply(string text, string open, string close)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (text.Contains('\e')) text = ReplaceAll(text, close, open);
        var lf = text.IndexOf('\n');
        if (lf != -1) text = EncaseCrlf(text, close, open, lf);
        return open + text + close;
    }

    private static string ReplaceAll(string text, string substring, string replacer)
    {
        var index = text.IndexOf(substring, StringComparison.Ordinal);
        if (index == -1) return text;
        var end = 0;
        var result = new System.Text.StringBuilder();
        do
        {
            result.Append(text, end, index - end).Append(substring).Append(replacer);
            end = index + substring.Length;
            index = text.IndexOf(substring, end, StringComparison.Ordinal);
        } while (index != -1);
        result.Append(text, end, text.Length - end);
        return result.ToString();
    }

    private static string EncaseCrlf(string text, string prefix, string postfix, int index)
    {
        var end = 0;
        var result = new System.Text.StringBuilder();
        do
        {
            var gotCr = index > 0 && text[index - 1] == '\r';
            var sliceEnd = gotCr ? index - 1 : index;
            result.Append(text, end, sliceEnd - end).Append(prefix).Append(gotCr ? "\r\n" : "\n").Append(postfix);
            end = index + 1;
            index = text.IndexOf('\n', end);
        } while (index != -1);
        result.Append(text, end, text.Length - end);
        return result.ToString();
    }

    public static string Bold(string text) => Apply(text, "\e[1m", "\e[22m");

    public static string Dim(string text) => Apply(text, "\e[2m", "\e[22m");

    public static string Italic(string text) => Apply(text, "\e[3m", "\e[23m");

    public static string Underline(string text) => Apply(text, "\e[4m", "\e[24m");

    public static string Inverse(string text) => Apply(text, "\e[7m", "\e[27m");

    public static string Strikethrough(string text) => Apply(text, "\e[9m", "\e[29m");
}

/// <summary>Resolved color theme. Port of pi's Theme class (theme.ts).</summary>
public sealed class Theme
{
    public static readonly HashSet<string> BgColorKeys = ["selectedBg", "searchMatchBg", "userMessageBg", "customMessageBg", "toolPendingBg", "toolSuccessBg", "toolErrorBg"];

    private readonly Dictionary<string, string> _fgColors = [];
    private readonly Dictionary<string, string> _bgColors = [];

    public string? Name { get; }
    public string? SourcePath { get; }
    public SourceInfo? SourceInfo { get; set; }
    public string ColorMode { get; }

    /// <summary>True when the theme targets a light terminal background (dark body text).</summary>
    public bool IsLight { get; }

    public Theme(Dictionary<string, JsonNode?> fgColors, Dictionary<string, JsonNode?> bgColors, string colorMode, string? name = null, string? sourcePath = null, SourceInfo? sourceInfo = null)
    {
        Name = name;
        SourcePath = sourcePath;
        SourceInfo = sourceInfo;
        ColorMode = colorMode;
        IsLight = DetectLight(fgColors.GetValueOrDefault("text"), name);
        var colors = new Dictionary<string, JsonNode?>(fgColors);
        colors["scrollbarTrack"] = fgColors.GetValueOrDefault("scrollbarTrack") ?? fgColors.GetValueOrDefault("muted");
        colors["scrollbarThumb"] = fgColors.GetValueOrDefault("scrollbarThumb") ?? fgColors.GetValueOrDefault("text");
        colors["thinkingMax"] = fgColors.GetValueOrDefault("thinkingMax") ?? fgColors.GetValueOrDefault("thinkingXhigh");
        colors["searchMatchText"] = fgColors.GetValueOrDefault("searchMatchText") ?? fgColors.GetValueOrDefault("text");
        foreach (var (key, value) in colors) _fgColors[key] = ThemeColors.FgAnsi(value, colorMode);
        var backgrounds = new Dictionary<string, JsonNode?>(bgColors);
        backgrounds["searchMatchBg"] = bgColors.GetValueOrDefault("searchMatchBg") ?? bgColors.GetValueOrDefault("selectedBg");
        foreach (var (key, value) in backgrounds) _bgColors[key] = ThemeColors.BgAnsi(value, colorMode);
    }

    private static bool DetectLight(JsonNode? textColor, string? name)
    {
        var hex = textColor is JsonValue v && v.TryGetValue<double>(out var index) ? ThemeColors.Ansi256ToHex((int)index) : PiJson.GetString(textColor);
        if (hex is null || hex.Length != 7 || hex[0] != '#') return name == "light";
        var (r, g, b) = ThemeColors.HexToRgb(hex);
        return 0.2126 * r + 0.7152 * g + 0.0722 * b < 128;
    }

    public string Fg(string color, string text)
    {
        if (!_fgColors.TryGetValue(color, out var ansi)) throw new InvalidOperationException($"Unknown theme color: {color}");
        return $"{ansi}{text}\e[39m";
    }

    public string Bg(string color, string text)
    {
        if (!_bgColors.TryGetValue(color, out var ansi)) throw new InvalidOperationException($"Unknown theme background color: {color}");
        return $"{ansi}{text}\e[49m";
    }

    public string Bold(string text) => AnsiStyle.Bold(text);

    public string Italic(string text) => AnsiStyle.Italic(text);

    public string Underline(string text) => AnsiStyle.Underline(text);

    public string Inverse(string text) => AnsiStyle.Inverse(text);

    public string Strikethrough(string text) => AnsiStyle.Strikethrough(text);

    public string GetFgAnsi(string color) => _fgColors.TryGetValue(color, out var ansi) ? ansi : throw new InvalidOperationException($"Unknown theme color: {color}");

    public string GetBgAnsi(string color) => _bgColors.TryGetValue(color, out var ansi) ? ansi : throw new InvalidOperationException($"Unknown theme background color: {color}");

    public Func<string, string> GetThinkingBorderColor(ThinkingLevel level) => level switch
    {
        ThinkingLevel.Minimal => s => Fg("thinkingMinimal", s),
        ThinkingLevel.Low => s => Fg("thinkingLow", s),
        ThinkingLevel.Medium => s => Fg("thinkingMedium", s),
        ThinkingLevel.High => s => Fg("thinkingHigh", s),
        ThinkingLevel.XHigh => s => Fg("thinkingXhigh", s),
        ThinkingLevel.Max => s => Fg("thinkingMax", s),
        _ => s => Fg("thinkingOff", s),
    };

    public Func<string, string> GetBashModeBorderColor() => s => Fg("bashMode", s);
}

internal static class ThemeColors
{
    private static readonly int[] CubeValues = [0, 95, 135, 175, 215, 255];
    private static readonly int[] GrayValues = Enumerable.Range(0, 24).Select(i => 8 + i * 10).ToArray();

    public static (int R, int G, int B) HexToRgb(string hex)
    {
        var cleaned = hex.Replace("#", "");
        if (cleaned.Length != 6) throw new InvalidOperationException($"Invalid hex color: {hex}");
        if (!int.TryParse(cleaned[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r)
            || !int.TryParse(cleaned[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g)
            || !int.TryParse(cleaned[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
        {
            throw new InvalidOperationException($"Invalid hex color: {hex}");
        }
        return (r, g, b);
    }

    private static int ClosestIndex(int[] values, double value)
    {
        var minDist = double.PositiveInfinity;
        var minIdx = 0;
        for (var i = 0; i < values.Length; i++)
        {
            var dist = Math.Abs(value - values[i]);
            if (dist < minDist)
            {
                minDist = dist;
                minIdx = i;
            }
        }
        return minIdx;
    }

    private static double ColorDistance(int r1, int g1, int b1, int r2, int g2, int b2)
    {
        double dr = r1 - r2, dg = g1 - g2, db = b1 - b2;
        return dr * dr * 0.299 + dg * dg * 0.587 + db * db * 0.114;
    }

    public static int RgbTo256(int r, int g, int b)
    {
        var rIdx = ClosestIndex(CubeValues, r);
        var gIdx = ClosestIndex(CubeValues, g);
        var bIdx = ClosestIndex(CubeValues, b);
        var cubeIndex = 16 + 36 * rIdx + 6 * gIdx + bIdx;
        var cubeDist = ColorDistance(r, g, b, CubeValues[rIdx], CubeValues[gIdx], CubeValues[bIdx]);
        var gray = (int)Math.Round(0.299 * r + 0.587 * g + 0.114 * b, MidpointRounding.AwayFromZero);
        var grayIdx = ClosestIndex(GrayValues, gray);
        var grayValue = GrayValues[grayIdx];
        var grayDist = ColorDistance(r, g, b, grayValue, grayValue, grayValue);
        var spread = Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
        return spread < 10 && grayDist < cubeDist ? 232 + grayIdx : cubeIndex;
    }

    public static string FgAnsi(JsonNode? color, string mode) => Ansi(color, mode, 38, "\e[39m");

    public static string BgAnsi(JsonNode? color, string mode) => Ansi(color, mode, 48, "\e[49m");

    private static string Ansi(JsonNode? color, string mode, int code, string reset)
    {
        if (color is JsonValue v && v.TryGetValue<double>(out var number)) return $"\e[{code};5;{(long)number}m";
        var s = PiJson.GetString(color);
        if (s == "") return reset;
        if (s is not null && s.StartsWith('#'))
        {
            var (r, g, b) = HexToRgb(s);
            return mode == "truecolor" ? $"\e[{code};2;{r};{g};{b}m" : $"\e[{code};5;{RgbTo256(r, g, b)}m";
        }
        throw new InvalidOperationException($"Invalid color value: {color?.ToJsonString()}");
    }

    public static JsonNode? ResolveVarRefs(JsonNode? value, JsonObject vars, HashSet<string>? visited = null)
    {
        if (value is JsonValue v && v.TryGetValue<double>(out _)) return value;
        var s = PiJson.GetString(value);
        if (s is null || s == "" || s.StartsWith('#')) return value;
        visited ??= [];
        if (visited.Contains(s)) throw new InvalidOperationException($"Circular variable reference detected: {s}");
        if (!vars.ContainsKey(s)) throw new InvalidOperationException($"Variable reference not found: {s}");
        visited.Add(s);
        return ResolveVarRefs(vars[s], vars, visited);
    }

    public static string Ansi256ToHex(int index)
    {
        string[] basic = ["#000000", "#800000", "#008000", "#808000", "#000080", "#800080", "#008080", "#c0c0c0", "#808080", "#ff0000", "#00ff00", "#ffff00", "#0000ff", "#ff00ff", "#00ffff", "#ffffff"];
        if (index < 16) return basic[Math.Max(0, index)];
        if (index < 232)
        {
            var cube = index - 16;
            static string ToHex(int n) => (n == 0 ? 0 : 55 + n * 40).ToString("x2", CultureInfo.InvariantCulture);
            return $"#{ToHex(cube / 36)}{ToHex(cube % 36 / 6)}{ToHex(cube % 6)}";
        }
        var gray = (8 + (index - 232) * 10).ToString("x2", CultureInfo.InvariantCulture);
        return $"#{gray}{gray}{gray}";
    }
}

public sealed record TerminalThemeDetection(string Theme, string Source, string Detail, string Confidence);

/// <summary>Global theme state, loading and TUI theme adapters. Port of theme.ts module functions.</summary>
public static class ThemeManager
{
    private static Theme? _current;
    private static string? _currentThemeName;
    private static Dictionary<string, JsonObject>? _builtinThemes;
    private static readonly Dictionary<string, Theme> RegisteredThemes = [];
    private static FileSystemWatcher? _watcher;
    private static Timer? _reloadTimer;
    private static Action? _onThemeChange;

    /// <summary>The active theme (pi's global `theme` proxy).</summary>
    public static Theme Current => _current ?? throw new InvalidOperationException("Theme not initialized. Call initTheme() first.");

    public static bool IsInitialized => _current is not null;

    private static Dictionary<string, JsonObject> BuiltinThemes
    {
        get
        {
            if (_builtinThemes is not null) return _builtinThemes;
            var dir = AppConfig.ThemesDir;
            _builtinThemes = new Dictionary<string, JsonObject>
            {
                ["dark"] = (JsonObject)JsonNode.Parse(TextHelpers.StripBom(File.ReadAllText(Path.Combine(dir, "dark.json"))))!,
                ["light"] = (JsonObject)JsonNode.Parse(TextHelpers.StripBom(File.ReadAllText(Path.Combine(dir, "light.json"))))!,
            };
            return _builtinThemes;
        }
    }

    public sealed record ThemeInfo(string Name, string? Path);

    public static List<string> GetAvailableThemes() => GetAvailableThemesWithPaths().Select(t => t.Name).ToList();

    public static List<ThemeInfo> GetAvailableThemesWithPaths()
    {
        var result = new List<ThemeInfo>();
        var seen = new HashSet<string>();
        void Add(ThemeInfo info)
        {
            if (seen.Add(info.Name)) result.Add(info);
        }
        foreach (var name in BuiltinThemes.Keys) Add(new ThemeInfo(name, Path.Combine(AppConfig.ThemesDir, $"{name}.json")));
        foreach (var info in GetCustomThemeInfos()) Add(info);
        foreach (var (name, theme) in RegisteredThemes) Add(new ThemeInfo(name, theme.SourcePath));
        result.Sort((a, b) => NodeCompare.LocaleCompare(a.Name, b.Name));
        return result;
    }

    private static List<ThemeInfo> GetCustomThemeInfos()
    {
        var dir = AppConfig.CustomThemesDir;
        var result = new List<ThemeInfo>();
        if (!Directory.Exists(dir)) return result;
        foreach (var file in Directory.EnumerateFiles(dir).Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal))
        {
            if (file is null || !file.EndsWith(".json", StringComparison.Ordinal)) continue;
            var themePath = Path.Combine(dir, file);
            try
            {
                if (LoadThemeFromPath(themePath).Name is { } name) result.Add(new ThemeInfo(name, themePath));
            }
            catch
            {
                // Invalid themes are reported by the resource loader.
            }
        }
        return result;
    }

    private static JsonObject ParseThemeJson(string label, string content)
    {
        JsonNode? json;
        try
        {
            json = JsonNode.Parse(TextHelpers.StripBom(content));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to parse theme {label}: {ex.Message}");
        }
        // pi installs its typebox validator for every mode (main.ts), so user themes are always validated.
        return ThemeJsonValidator.Validate(label, json);
    }

    private static JsonObject LoadThemeJson(string name)
    {
        if (BuiltinThemes.TryGetValue(name, out var builtin)) return builtin;
        if (RegisteredThemes.TryGetValue(name, out var registered))
        {
            if (registered.SourcePath is { } source) return ParseThemeJson(source, File.ReadAllText(source));
            throw new InvalidOperationException($"Theme \"{name}\" does not have a source path for export");
        }
        var themePath = Path.Combine(AppConfig.CustomThemesDir, $"{name}.json");
        if (!File.Exists(themePath)) throw new InvalidOperationException($"Theme not found: {name}");
        return ParseThemeJson(name, File.ReadAllText(themePath));
    }

    private static Dictionary<string, JsonNode?> WithFallbacks(JsonObject colors)
    {
        var result = colors.ToDictionary(kv => kv.Key, kv => kv.Value);
        result["scrollbarTrack"] = colors["scrollbarTrack"] ?? colors["muted"];
        result["scrollbarThumb"] = colors["scrollbarThumb"] ?? colors["text"];
        result["thinkingMax"] = colors["thinkingMax"] ?? colors["thinkingXhigh"];
        result["searchMatchBg"] = colors["searchMatchBg"] ?? colors["selectedBg"];
        result["searchMatchText"] = colors["searchMatchText"] ?? colors["text"];
        return result;
    }

    private static Dictionary<string, JsonNode?> ResolveColors(JsonObject themeJson)
    {
        var vars = themeJson["vars"] as JsonObject ?? [];
        var colors = themeJson["colors"] as JsonObject ?? [];
        return WithFallbacks(colors).ToDictionary(kv => kv.Key, kv => ThemeColors.ResolveVarRefs(kv.Value, vars));
    }

    public static Theme CreateTheme(JsonObject themeJson, string? mode = null, string? sourcePath = null)
    {
        var colorMode = mode ?? (TerminalImage.GetCapabilities().TrueColor ? "truecolor" : "256color");
        var fg = new Dictionary<string, JsonNode?>();
        var bg = new Dictionary<string, JsonNode?>();
        foreach (var (key, value) in ResolveColors(themeJson))
        {
            if (Theme.BgColorKeys.Contains(key)) bg[key] = value;
            else fg[key] = value;
        }
        return new Theme(fg, bg, colorMode, PiJson.GetString(themeJson["name"]), sourcePath);
    }

    public static Theme LoadThemeFromPath(string themePath, string? mode = null) =>
        CreateTheme(ParseThemeJson(themePath, File.ReadAllText(themePath)), mode, themePath);

    public static Theme CreateThemeFromResource(ThemeResource resource) =>
        new Func<Theme>(() =>
        {
            var theme = CreateTheme(resource.Json, sourcePath: resource.SourcePath);
            theme.SourceInfo = resource.SourceInfo;
            return theme;
        })();

    private static Theme LoadTheme(string name, string? mode = null) =>
        RegisteredThemes.TryGetValue(name, out var registered) ? registered : CreateTheme(LoadThemeJson(name), mode);

    public static Theme? GetThemeByName(string name)
    {
        try
        {
            return LoadTheme(name);
        }
        catch
        {
            return null;
        }
    }

    public static (string LightTheme, string DarkTheme)? ParseAutoThemeSetting(string? themeSetting)
    {
        if (string.IsNullOrEmpty(themeSetting)) return null;
        var slash = themeSetting.IndexOf('/');
        if (slash == -1 || themeSetting.IndexOf('/', slash + 1) != -1) return null;
        var light = themeSetting[..slash].Trim();
        var dark = themeSetting[(slash + 1)..].Trim();
        return light.Length == 0 || dark.Length == 0 ? null : (light, dark);
    }

    public static string? ResolveThemeSetting(string? themeSetting, string terminalTheme)
    {
        if (ParseAutoThemeSetting(themeSetting) is { } auto) return terminalTheme == "light" ? auto.LightTheme : auto.DarkTheme;
        if (themeSetting?.Contains('/') == true) return null;
        return themeSetting;
    }

    private static int? GetColorFgBgBackgroundIndex(string colorfgbg)
    {
        var parts = colorfgbg.Split(';');
        for (var i = parts.Length - 1; i >= 0; i--)
        {
            if (int.TryParse(parts[i].Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var bg) && bg is >= 0 and <= 255) return bg;
        }
        return null;
    }

    private static double Luminance(int r, int g, int b)
    {
        static double ToLinear(int channel)
        {
            var value = channel / 255.0;
            return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * ToLinear(r) + 0.7152 * ToLinear(g) + 0.0722 * ToLinear(b);
    }

    public static string GetThemeForRgbColor(RgbColor rgb) => Luminance(rgb.R, rgb.G, rgb.B) >= 0.5 ? "light" : "dark";

    public static TerminalThemeDetection DetectTerminalBackgroundFromEnv()
    {
        var colorfgbg = Environment.GetEnvironmentVariable("COLORFGBG") ?? "";
        if (GetColorFgBgBackgroundIndex(colorfgbg) is { } bg)
        {
            var (r, g, b) = ThemeColors.HexToRgb(ThemeColors.Ansi256ToHex(bg));
            return new TerminalThemeDetection(Luminance(r, g, b) >= 0.5 ? "light" : "dark", "COLORFGBG", $"background color index {bg}", "high");
        }
        return new TerminalThemeDetection("dark", "fallback", "no terminal background hint found", "low");
    }

    public static async Task<TerminalThemeDetection> DetectTerminalBackgroundThemeAsync(TuiBase ui, int timeoutMs)
    {
        try
        {
            if (await ui.QueryTerminalBackgroundColorAsync(timeoutMs) is { } rgb)
            {
                return new TerminalThemeDetection(GetThemeForRgbColor(rgb), "terminal background", $"OSC 11 background rgb({rgb.R}, {rgb.G}, {rgb.B})", "high");
            }
        }
        catch
        {
            // Fall back to environment detection.
        }
        return DetectTerminalBackgroundFromEnv();
    }

    public static async Task<string> DetectTerminalThemeForAutoAsync(TuiBase ui, int timeoutMs)
    {
        Task<string?>? colorScheme = null;
        try
        {
            colorScheme = ui.QueryTerminalColorSchemeAsync(timeoutMs);
        }
        catch
        {
            // Fall back to OSC 11 / COLORFGBG.
        }
        var background = DetectTerminalBackgroundThemeAsync(ui, timeoutMs);
        try
        {
            if (colorScheme is not null && await colorScheme is { } scheme) return scheme;
        }
        catch
        {
            // Fall back to the concurrent background detection.
        }
        return (await background).Theme;
    }

    public static string GetDefaultTheme() => DetectTerminalBackgroundFromEnv().Theme;

    public static void SetRegisteredThemes(IEnumerable<Theme> themes)
    {
        RegisteredThemes.Clear();
        foreach (var theme in themes)
        {
            if (theme.Name is not { } name) continue;
            if (name.Contains('/')) throw new InvalidOperationException($"Invalid theme name \"{name}\": theme names cannot contain \"/\" because it is reserved for automatic light/dark theme settings.");
            RegisteredThemes[name] = theme;
        }
    }

    public static void InitTheme(string? themeName = null, bool enableWatcher = false)
    {
        var name = themeName ?? GetDefaultTheme();
        _currentThemeName = name;
        try
        {
            _current = LoadTheme(name);
            if (enableWatcher) StartThemeWatcher();
        }
        catch
        {
            _currentThemeName = "dark";
            _current = LoadTheme("dark");
        }
    }

    public static (bool Success, string? Error) SetTheme(string name, bool enableWatcher = false)
    {
        _currentThemeName = name;
        try
        {
            _current = LoadTheme(name);
            if (enableWatcher) StartThemeWatcher();
            _onThemeChange?.Invoke();
            return (true, null);
        }
        catch (Exception ex)
        {
            _currentThemeName = "dark";
            _current = LoadTheme("dark");
            return (false, ex.Message);
        }
    }

    public static void SetThemeInstance(Theme theme)
    {
        _current = theme;
        _currentThemeName = "<in-memory>";
        StopThemeWatcher();
        _onThemeChange?.Invoke();
    }

    public static void OnThemeChange(Action callback) => _onThemeChange = callback;

    public static string? CurrentThemeName => _currentThemeName;

    private static void StartThemeWatcher()
    {
        StopThemeWatcher();
        if (_currentThemeName is null or "dark" or "light") return;
        var dir = AppConfig.CustomThemesDir;
        var watchedName = _currentThemeName;
        var fileName = $"{watchedName}.json";
        var themeFile = Path.Combine(dir, fileName);
        if (!File.Exists(themeFile)) return;
        var dispatcher = UiDispatcher.Current;

        void ScheduleReload()
        {
            _reloadTimer?.Dispose();
            _reloadTimer = new Timer(_ =>
            {
                void Reload()
                {
                    if (_currentThemeName != watchedName || !File.Exists(themeFile)) return;
                    try
                    {
                        var reloaded = LoadThemeFromPath(themeFile);
                        RegisteredThemes[watchedName] = reloaded;
                        _current = reloaded;
                        _onThemeChange?.Invoke();
                    }
                    catch
                    {
                        // File may be mid-edit.
                    }
                }
                if (dispatcher is not null) dispatcher.Post(Reload);
                else Reload();
            }, null, 100, Timeout.Infinite);
        }

        try
        {
            _watcher = new FileSystemWatcher(dir) { EnableRaisingEvents = true };
            FileSystemEventHandler handler = (_, e) =>
            {
                if (_currentThemeName != watchedName) return;
                if (string.IsNullOrEmpty(e.Name) || e.Name == fileName) ScheduleReload();
            };
            _watcher.Changed += handler;
            _watcher.Created += handler;
            _watcher.Renamed += (_, e) => handler(null, e);
            _watcher.Error += (_, _) => StopThemeWatcher();
        }
        catch
        {
            _watcher = null;
        }
    }

    public static void StopThemeWatcher()
    {
        _reloadTimer?.Dispose();
        _reloadTimer = null;
        _watcher?.Dispose();
        _watcher = null;
    }

    public static Dictionary<string, string> GetResolvedThemeColors(string? themeName = null)
    {
        var name = themeName ?? _currentThemeName ?? GetDefaultTheme();
        var defaultText = name == "light" ? "#000000" : "#e5e5e7";
        var result = new Dictionary<string, string>();
        foreach (var (key, value) in ResolveColors(LoadThemeJson(name)))
        {
            if (value is JsonValue v && v.TryGetValue<double>(out var n)) result[key] = ThemeColors.Ansi256ToHex((int)n);
            else if (PiJson.GetString(value) is "" or null) result[key] = defaultText;
            else result[key] = PiJson.GetString(value)!;
        }
        return result;
    }

    public static bool IsLightTheme(string? themeName) => themeName == "light";

    public static (string? PageBg, string? CardBg, string? InfoBg) GetThemeExportColors(string? themeName = null)
    {
        var name = themeName ?? _currentThemeName ?? GetDefaultTheme();
        try
        {
            var json = LoadThemeJson(name);
            if (json["export"] is not JsonObject export) return (null, null, null);
            var vars = json["vars"] as JsonObject ?? [];
            string? Resolve(JsonNode? value)
            {
                if (value is null) return null;
                var resolved = ThemeColors.ResolveVarRefs(value, vars);
                if (resolved is JsonValue v && v.TryGetValue<double>(out var n)) return ThemeColors.Ansi256ToHex((int)n);
                var s = PiJson.GetString(resolved);
                return string.IsNullOrEmpty(s) ? null : s;
            }
            return (Resolve(export["pageBg"]), Resolve(export["cardBg"]), Resolve(export["infoBg"]));
        }
        catch
        {
            return (null, null, null);
        }
    }

    // ----- Syntax highlighting -----

    private static Dictionary<string, Func<string, string>> FallbackHighlightTheme(Theme t)
    {
        var theme = new Dictionary<string, Func<string, string>>();
        foreach (var (scope, hex) in TextMateHighlighter.FallbackScopeColors(t.IsLight))
        {
            var ansi = ThemeColors.FgAnsi(JsonValue.Create(hex), t.ColorMode);
            theme[scope] = s => $"{ansi}{s}\e[39m";
        }
        theme["emphasis"] = s => t.Italic(s);
        theme["strong"] = s => t.Bold(s);
        theme["link"] = s => t.Underline(s);
        return theme;
    }

    private static Theme? _cachedHighlightFor;
    private static Dictionary<string, Func<string, string>>? _cachedHighlightTheme;

    private static Dictionary<string, Func<string, string>> GetFallbackHighlightTheme(Theme t)
    {
        if (!ReferenceEquals(_cachedHighlightFor, t) || _cachedHighlightTheme is null)
        {
            _cachedHighlightFor = t;
            _cachedHighlightTheme = FallbackHighlightTheme(t);
        }
        return _cachedHighlightTheme;
    }

    /// <summary>True when a VS Code TextMate grammar or the fallback tokenizer knows the language.</summary>
    public static bool SupportsHighlightLanguage(string language) => TextMateHighlighter.SupportsLanguage(language) || SyntaxHighlighter.SupportsLanguage(language);

    /// <summary>
    /// Highlight with VS Code grammars and Dark Modern (or Light Modern) token colors; languages without a grammar use the
    /// fallback tokenizer with the same palette. Returns null when neither knows the language.
    /// </summary>
    private static List<string>? TryHighlight(string code, string lang)
    {
        var theme = Current;
        if (TextMateHighlighter.Highlight(code, lang, theme.IsLight, theme.ColorMode) is { } highlighted) return highlighted.Split('\n').ToList();
        if (!SyntaxHighlighter.SupportsLanguage(lang)) return null;
        // Unscoped text uses the editor foreground, as in VS Code, so restore it after every colored token.
        var defaultAnsi = ThemeColors.FgAnsi(JsonValue.Create(TextMateHighlighter.EditorForeground(theme.IsLight)), theme.ColorMode);
        return SyntaxHighlighter.Highlight(code, lang, GetFallbackHighlightTheme(theme))
            .Split('\n')
            .Select(line => line.Length == 0 ? line : defaultAnsi + line.Replace("\e[39m", defaultAnsi) + "\e[39m")
            .ToList();
    }

    public static List<string> HighlightCode(string code, string? lang = null)
    {
        var validLang = lang is not null && SupportsHighlightLanguage(lang) ? lang : null;
        if (validLang is null) return code.Split('\n').Select(line => Current.Fg("mdCodeBlock", line)).ToList();
        try
        {
            return TryHighlight(code, validLang) ?? code.Split('\n').Select(line => Current.Fg("mdCodeBlock", line)).ToList();
        }
        catch
        {
            return code.Split('\n').ToList();
        }
    }

    private static readonly Dictionary<string, string> ExtToLang = new()
    {
        ["ts"] = "typescript", ["tsx"] = "typescript", ["js"] = "javascript", ["jsx"] = "javascript", ["mjs"] = "javascript", ["cjs"] = "javascript",
        ["py"] = "python", ["rb"] = "ruby", ["rs"] = "rust", ["go"] = "go", ["java"] = "java", ["kt"] = "kotlin", ["swift"] = "swift",
        ["c"] = "c", ["h"] = "c", ["cpp"] = "cpp", ["cc"] = "cpp", ["cxx"] = "cpp", ["hpp"] = "cpp", ["cs"] = "csharp", ["php"] = "php",
        ["sh"] = "bash", ["bash"] = "bash", ["zsh"] = "bash", ["fish"] = "fish", ["ps1"] = "powershell", ["sql"] = "sql", ["html"] = "html",
        ["htm"] = "html", ["css"] = "css", ["scss"] = "scss", ["sass"] = "sass", ["less"] = "less", ["json"] = "json", ["yaml"] = "yaml",
        ["yml"] = "yaml", ["toml"] = "toml", ["xml"] = "xml", ["md"] = "markdown", ["markdown"] = "markdown", ["dockerfile"] = "dockerfile",
        ["makefile"] = "makefile", ["cmake"] = "cmake", ["lua"] = "lua", ["perl"] = "perl", ["r"] = "r", ["scala"] = "scala",
        ["clj"] = "clojure", ["ex"] = "elixir", ["exs"] = "elixir", ["erl"] = "erlang", ["hs"] = "haskell", ["ml"] = "ocaml", ["vim"] = "vim",
        ["graphql"] = "graphql", ["proto"] = "protobuf", ["tf"] = "hcl", ["hcl"] = "hcl",
    };

    public static string? GetLanguageFromPath(string filePath)
    {
        var ext = filePath.Split('.')[^1].ToLowerInvariant();
        return ext.Length == 0 ? null : ExtToLang.GetValueOrDefault(ext);
    }

    // ----- TUI theme adapters -----

    public static MarkdownTheme GetMarkdownTheme() => new()
    {
        Heading = t => Current.Fg("mdHeading", t),
        Link = t => Current.Fg("mdLink", t),
        LinkUrl = t => Current.Fg("mdLinkUrl", t),
        Code = t => Current.Fg("mdCode", t),
        CodeBlock = t => Current.Fg("mdCodeBlock", t),
        CodeBlockBorder = t => Current.Fg("mdCodeBlockBorder", t),
        Quote = t => Current.Fg("mdQuote", t),
        QuoteBorder = t => Current.Fg("mdQuoteBorder", t),
        Hr = t => Current.Fg("mdHr", t),
        ListBullet = t => Current.Fg("mdListBullet", t),
        Bold = t => Current.Bold(t),
        Italic = t => Current.Italic(t),
        Underline = t => Current.Underline(t),
        Strikethrough = AnsiStyle.Strikethrough,
        HighlightCode = (code, lang) =>
        {
            var validLang = lang is not null && SupportsHighlightLanguage(lang) ? lang : null;
            if (validLang is null) return code.Split('\n').Select(line => Current.Fg("mdCodeBlock", line)).ToList();
            try
            {
                return TryHighlight(code, validLang) ?? code.Split('\n').Select(line => Current.Fg("mdCodeBlock", line)).ToList();
            }
            catch
            {
                return code.Split('\n').Select(line => Current.Fg("mdCodeBlock", line)).ToList();
            }
        },
    };

    public static SelectListTheme GetSelectListTheme() => new()
    {
        SelectedPrefix = t => Current.Fg("accent", t),
        SelectedText = t => Current.Fg("accent", t),
        Description = t => Current.Fg("muted", t),
        ScrollInfo = t => Current.Fg("muted", t),
        NoMatch = t => Current.Fg("muted", t),
    };

    public static EditorTheme GetEditorTheme() => new()
    {
        BorderColor = t => Current.Fg("borderMuted", t),
        SelectList = GetSelectListTheme(),
    };

    public static SettingsListTheme GetSettingsListTheme() => new()
    {
        Label = (t, selected) => selected ? Current.Fg("accent", t) : t,
        Value = (t, selected) => selected ? Current.Fg("accent", t) : Current.Fg("muted", t),
        Description = t => Current.Fg("dim", t),
        Cursor = Current.Fg("accent", "→ "),
        Hint = t => Current.Fg("dim", t),
    };
}
