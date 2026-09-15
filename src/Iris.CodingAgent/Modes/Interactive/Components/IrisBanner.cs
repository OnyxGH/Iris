using System.Text;
using System.Text.Json.Nodes;
using Iris.Tui;

namespace Iris.CodingAgent.Modes.Interactive.Components;

/// <summary>
/// Startup banner. Block letters get a violet-to-cyan gradient (the
/// iris flower's colors), their box-drawing shadows a muted tint of the same hue. Falls back to the plain
/// "iris vX" logo when the terminal is too narrow.
/// </summary>
public sealed class IrisBanner(string version, int paddingX = 1) : IComponent
{
    private static readonly string[] Art =
    [
        "██╗██████╗ ██╗███████╗",
        "██║██╔══██╗██║██╔════╝",
        "██║██████╔╝██║███████╗",
        "██║██╔══██╗██║╚════██║",
        "██║██║  ██║██║███████║",
        "╚═╝╚═╝  ╚═╝╚═╝╚══════╝",
    ];

    private static readonly (int R, int G, int B)[] DarkStops = [(0xC0, 0x84, 0xFC), (0x81, 0x8C, 0xF8), (0x60, 0xA5, 0xFA), (0x22, 0xD3, 0xEE)];
    private static readonly (int R, int G, int B)[] LightStops = [(0x7C, 0x3A, 0xED), (0x4F, 0x46, 0xE5), (0x25, 0x63, 0xEB), (0x08, 0x91, 0xB2)];

    private List<string>? _cache;
    private int _cacheWidth = -1;
    private Theme? _cacheTheme;

    public void Invalidate() => _cache = null;

    public List<string> Render(int width)
    {
        var theme = ThemeManager.Current;
        if (_cache is not null && _cacheWidth == width && ReferenceEquals(_cacheTheme, theme)) return _cache;
        var pad = new string(' ', paddingX);
        var versionText = $"v{version}";
        var artWidth = Art[0].Length;
        List<string> lines;
        if (width < artWidth + paddingX * 2)
        {
            lines = [pad + theme.Bold(theme.Fg("accent", Config.AppConfig.AppName)) + theme.Fg("dim", $" {versionText}")];
        }
        else
        {
            lines = [];
            var showVersionInline = width >= artWidth + 2 + versionText.Length + paddingX * 2;
            for (var row = 0; row < Art.Length; row++)
            {
                var line = new StringBuilder(pad);
                AppendGradientRow(line, Art[row], row, theme.IsLight, theme.ColorMode);
                if (row == Art.Length - 1 && showVersionInline) line.Append("  ").Append(theme.Fg("dim", versionText));
                lines.Add(line.ToString());
            }
            if (!showVersionInline) lines.Add(pad + theme.Fg("dim", versionText));
        }
        _cache = lines;
        _cacheWidth = width;
        _cacheTheme = theme;
        return lines;
    }

    private static void AppendGradientRow(StringBuilder sb, string row, int rowIndex, bool light, string colorMode)
    {
        var stops = light ? LightStops : DarkStops;
        var span = Art[0].Length - 1 + (Art.Length - 1) * 2;
        string? currentAnsi = null;
        for (var col = 0; col < row.Length; col++)
        {
            var ch = row[col];
            if (ch == ' ')
            {
                if (currentAnsi is not null) sb.Append("\e[39m");
                currentAnsi = null;
                sb.Append(ch);
                continue;
            }
            // Diagonal gradient: shift the hue a little per row so the letters read as lit from the top left.
            var (r, g, b) = Sample(stops, (col + rowIndex * 2) / (double)span);
            if (ch != '█')
            {
                // Shadow: blend toward the background so the block letters stay in front.
                var target = light ? 255 : 0;
                const double shadowBlend = 0.45;
                r = (int)Math.Round(r + (target - r) * shadowBlend);
                g = (int)Math.Round(g + (target - g) * shadowBlend);
                b = (int)Math.Round(b + (target - b) * shadowBlend);
            }
            var ansi = ThemeColors.FgAnsi(JsonValue.Create($"#{r:X2}{g:X2}{b:X2}"), colorMode);
            if (ansi != currentAnsi)
            {
                sb.Append(ansi);
                currentAnsi = ansi;
            }
            sb.Append(ch);
        }
        if (currentAnsi is not null) sb.Append("\e[39m");
    }

    private static (int R, int G, int B) Sample((int R, int G, int B)[] stops, double t)
    {
        t = Math.Clamp(t, 0, 1) * (stops.Length - 1);
        var index = Math.Min((int)t, stops.Length - 2);
        var local = t - index;
        var (a, b) = (stops[index], stops[index + 1]);
        return ((int)Math.Round(a.R + (b.R - a.R) * local), (int)Math.Round(a.G + (b.G - a.G) * local), (int)Math.Round(a.B + (b.B - a.B) * local));
    }
}
