using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Iris.Tui;

public sealed record TerminalCapabilities(string? Images, bool TrueColor, bool Hyperlinks);

public readonly record struct CellDimensions(int WidthPx, int HeightPx);

public readonly record struct ImageDimensions(int WidthPx, int HeightPx);

public sealed record RenderedImage(string Sequence, int Columns, int Rows, int? ImageId);

public readonly record struct RgbColor(int R, int G, int B);

/// <summary>Terminal capability detection and inline image protocols (Kitty, iTerm2).</summary>
public static partial class TerminalImage
{
    private static TerminalCapabilities? _cached;
    private static (string? Images, bool? TrueColor, bool? Hyperlinks, bool HasImages) _overrides;
    private static CellDimensions _cellDimensions = new(9, 18);

    public static CellDimensions GetCellDimensions() => _cellDimensions;

    public static void SetCellDimensions(CellDimensions dims) => _cellDimensions = dims;

    private static string Env(string name) => Environment.GetEnvironmentVariable(name) ?? "";

    private static bool ProbeTmuxHyperlinks()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("tmux", ["display-message", "-p", "#{client_termfeatures}"])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var p = System.Diagnostics.Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(250)) return false;
            return output.Split(',').Select(f => f.Trim()).Contains("hyperlinks");
        }
        catch
        {
            return false;
        }
    }

    private static TerminalCapabilities DetectFromEnvironment(Func<bool> tmuxForwardsHyperlinks)
    {
        var termProgram = Env("TERM_PROGRAM").ToLowerInvariant();
        var terminalEmulator = Env("TERMINAL_EMULATOR").ToLowerInvariant();
        var term = Env("TERM").ToLowerInvariant();
        var colorTerm = Env("COLORTERM").ToLowerInvariant();
        var trueColorHint = colorTerm is "truecolor" or "24bit";

        if (Env("TMUX").Length > 0 || term.StartsWith("tmux", StringComparison.Ordinal)) return new(null, trueColorHint, tmuxForwardsHyperlinks());
        if (term.StartsWith("screen", StringComparison.Ordinal)) return new(null, trueColorHint, false);
        if (Env("KITTY_WINDOW_ID").Length > 0 || termProgram == "kitty") return new("kitty", true, true);
        if (termProgram == "ghostty" || term.Contains("ghostty") || Env("GHOSTTY_RESOURCES_DIR").Length > 0) return new("kitty", true, true);
        if (Env("WEZTERM_PANE").Length > 0 || termProgram == "wezterm") return new("kitty", true, true);
        if (termProgram == "warpterminal" || Env("WARP_SESSION_ID").Length > 0 || Env("WARP_TERMINAL_SESSION_UUID").Length > 0) return new("kitty", true, true);
        if (Env("ITERM_SESSION_ID").Length > 0 || termProgram == "iterm.app") return new("iterm2", true, true);
        if (Env("WT_SESSION").Length > 0) return new(null, true, true);
        if (termProgram is "alacritty" or "vscode" or "zed") return new(null, true, true);
        if (terminalEmulator == "jetbrains-jediterm") return new(null, true, false);
        if (OperatingSystem.IsWindows()) return new(null, true, false);
        return new(null, trueColorHint, false);
    }

    private static bool? ParseBoolOverride(string value) => value == "1" ? true : value == "0" ? false : null;

    public static TerminalCapabilities DetectCapabilities()
    {
        var hyperlinks = ParseBoolOverride(Env("IRIS_HYPERLINKS"));
        var detected = DetectFromEnvironment(hyperlinks is { } h ? () => h : ProbeTmuxHyperlinks);
        var protocol = Env("IRIS_IMAGE_PROTOCOL").ToLowerInvariant();
        var images = protocol is "kitty" or "iterm2" ? protocol : protocol is "none" or "0" ? null : detected.Images;
        var trueColor = ParseBoolOverride(Env("IRIS_TRUE_COLOR"));
        return new(images, trueColor ?? detected.TrueColor, hyperlinks ?? detected.Hyperlinks);
    }

    public static TerminalCapabilities GetCapabilities()
    {
        if (_cached is null)
        {
            var detected = DetectCapabilities();
            _cached = new(
                _overrides.HasImages ? _overrides.Images : detected.Images,
                _overrides.TrueColor ?? detected.TrueColor,
                _overrides.Hyperlinks ?? detected.Hyperlinks);
        }
        return _cached;
    }

    public static void ResetCapabilitiesCache() => _cached = null;

    /// <summary>Override selected capabilities (null leaves a capability auto-detected; pass hasImages to override images).</summary>
    public static void SetCapabilityOverrides(bool? trueColor = null, bool? hyperlinks = null, bool hasImages = false, string? images = null)
    {
        _overrides = (images, trueColor, hyperlinks, hasImages);
        _cached = null;
    }

    public static void SetCapabilities(TerminalCapabilities caps) => _cached = caps;

    private const string KittyPrefix = "\e_G";
    private const string ITerm2Prefix = "\e]1337;File=";

    public static bool IsImageLine(string line) => line.Contains(KittyPrefix, StringComparison.Ordinal) || line.Contains(ITerm2Prefix, StringComparison.Ordinal);

    public static int AllocateImageId() => Random.Shared.Next(1, int.MaxValue);

    public static string EncodeKitty(string base64Data, int? columns = null, int? rows = null, int? imageId = null, bool moveCursor = true)
    {
        const int chunkSize = 4096;
        var p = new List<string> { "a=T", "f=100", "q=2" };
        if (!moveCursor) p.Add("C=1");
        if (columns is > 0) p.Add($"c={columns}");
        if (rows is > 0) p.Add($"r={rows}");
        if (imageId is > 0) p.Add($"i={imageId}");
        var header = string.Join(",", p);

        if (base64Data.Length <= chunkSize) return $"\e_G{header};{base64Data}\e\\";

        var sb = new StringBuilder();
        for (var offset = 0; offset < base64Data.Length; offset += chunkSize)
        {
            var chunk = base64Data.Substring(offset, Math.Min(chunkSize, base64Data.Length - offset));
            var isLast = offset + chunkSize >= base64Data.Length;
            if (offset == 0) sb.Append($"\e_G{header},m=1;{chunk}\e\\");
            else if (isLast) sb.Append($"\e_Gm=0;{chunk}\e\\");
            else sb.Append($"\e_Gm=1;{chunk}\e\\");
        }
        return sb.ToString();
    }

    public static string DeleteKittyImage(long imageId) => $"\e_Ga=d,d=I,i={imageId},q=2\e\\";

    public static string DeleteAllKittyImages() => "\e_Ga=d,d=A,q=2\e\\";

    public static string EncodeITerm2(string base64Data, string? width = null, string? height = null, string? name = null, bool preserveAspectRatio = true, bool inline = true)
    {
        var size = (base64Data.Length * 3 / 4) - (base64Data.EndsWith("==", StringComparison.Ordinal) ? 2 : base64Data.EndsWith('=') ? 1 : 0);
        var p = new List<string> { $"inline={(inline ? 1 : 0)}", $"size={size}" };
        if (width is not null) p.Add($"width={width}");
        if (height is not null) p.Add($"height={height}");
        if (!string.IsNullOrEmpty(name)) p.Add($"name={Convert.ToBase64String(Encoding.UTF8.GetBytes(name))}");
        if (!preserveAspectRatio) p.Add("preserveAspectRatio=0");
        return $"\e]1337;File={string.Join(";", p)}:{base64Data}\a";
    }

    public static (int Columns, int Rows) CalculateImageCellSize(ImageDimensions image, int maxWidthCells, int? maxHeightCells = null, CellDimensions? cell = null)
    {
        var c = cell ?? new CellDimensions(9, 18);
        var maxWidth = Math.Max(1, maxWidthCells);
        int? maxHeight = maxHeightCells is { } mh ? Math.Max(1, mh) : null;
        var imageWidth = Math.Max(1, image.WidthPx);
        var imageHeight = Math.Max(1, image.HeightPx);
        var widthScale = (double)(maxWidth * c.WidthPx) / imageWidth;
        var heightScale = maxHeight is { } h ? (double)(h * c.HeightPx) / imageHeight : widthScale;
        var scale = Math.Min(widthScale, heightScale);
        var columns = (int)Math.Ceiling(imageWidth * scale / c.WidthPx);
        var rows = (int)Math.Ceiling(imageHeight * scale / c.HeightPx);
        return (Math.Max(1, Math.Min(maxWidth, columns)), Math.Max(1, maxHeight is { } mh2 ? Math.Min(mh2, rows) : rows));
    }

    public static int CalculateImageRows(ImageDimensions image, int targetWidthCells, CellDimensions? cell = null) =>
        CalculateImageCellSize(image, targetWidthCells, null, cell).Rows;

    private static byte[]? DecodeBase64(string base64)
    {
        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static ImageDimensions? GetImageDimensions(string base64Data, string mimeType)
    {
        var b = DecodeBase64(base64Data);
        if (b is null) return null;
        switch (mimeType)
        {
            case "image/png":
                if (b.Length < 24 || b[0] != 0x89 || b[1] != 0x50 || b[2] != 0x4e || b[3] != 0x47) return null;
                return new ImageDimensions((int)ReadU32Be(b, 16), (int)ReadU32Be(b, 20));
            case "image/jpeg":
            {
                if (b.Length < 2 || b[0] != 0xff || b[1] != 0xd8) return null;
                var offset = 2;
                while (offset < b.Length - 9)
                {
                    if (b[offset] != 0xff)
                    {
                        offset++;
                        continue;
                    }
                    var marker = b[offset + 1];
                    if (marker is >= 0xc0 and <= 0xc2) return new ImageDimensions((b[offset + 7] << 8) | b[offset + 8], (b[offset + 5] << 8) | b[offset + 6]);
                    if (offset + 3 >= b.Length) return null;
                    var length = (b[offset + 2] << 8) | b[offset + 3];
                    if (length < 2) return null;
                    offset += 2 + length;
                }
                return null;
            }
            case "image/gif":
            {
                if (b.Length < 10) return null;
                var sig = Encoding.ASCII.GetString(b, 0, 6);
                if (sig is not ("GIF87a" or "GIF89a")) return null;
                return new ImageDimensions(b[6] | (b[7] << 8), b[8] | (b[9] << 8));
            }
            case "image/webp":
            {
                if (b.Length < 30 || Encoding.ASCII.GetString(b, 0, 4) != "RIFF" || Encoding.ASCII.GetString(b, 8, 4) != "WEBP") return null;
                var chunk = Encoding.ASCII.GetString(b, 12, 4);
                if (chunk == "VP8 ") return new ImageDimensions((b[26] | (b[27] << 8)) & 0x3fff, (b[28] | (b[29] << 8)) & 0x3fff);
                if (chunk == "VP8L")
                {
                    var bits = b[21] | (b[22] << 8) | (b[23] << 16) | (b[24] << 24);
                    return new ImageDimensions((bits & 0x3fff) + 1, ((bits >> 14) & 0x3fff) + 1);
                }
                if (chunk == "VP8X") return new ImageDimensions((b[24] | (b[25] << 8) | (b[26] << 16)) + 1, (b[27] | (b[28] << 8) | (b[29] << 16)) + 1);
                return null;
            }
            default:
                return null;
        }
    }

    private static uint ReadU32Be(byte[] b, int o) => ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];

    public static RenderedImage? RenderImage(string base64Data, ImageDimensions dimensions, int maxWidthCells = 80, int? maxHeightCells = null, bool preserveAspectRatio = true, int? imageId = null, bool moveCursor = true)
    {
        var caps = GetCapabilities();
        if (caps.Images is null) return null;
        var (columns, rows) = CalculateImageCellSize(dimensions, maxWidthCells, maxHeightCells, GetCellDimensions());
        if (caps.Images == "kitty") return new RenderedImage(EncodeKitty(base64Data, columns, rows, imageId, moveCursor), columns, rows, imageId);
        if (caps.Images == "iterm2") return new RenderedImage(EncodeITerm2(base64Data, columns.ToString(CultureInfo.InvariantCulture), "auto", preserveAspectRatio: preserveAspectRatio), columns, rows, null);
        return null;
    }

    /// <summary>Wrap text in an OSC 8 hyperlink.</summary>
    public static string Hyperlink(string text, string url) => $"\e]8;;{url}\e\\{text}\e]8;;\e\\";

    public static string ImageFallback(string mimeType, ImageDimensions? dimensions = null, string? filename = null)
    {
        var parts = new List<string>();
        if (filename is not null)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var display = home.Length > 0 && (filename == home || filename.StartsWith(home + "/", StringComparison.Ordinal) || filename.StartsWith(home + "\\", StringComparison.Ordinal))
                ? "~" + filename[home.Length..]
                : filename;
            parts.Add(GetCapabilities().Hyperlinks && Path.IsPathRooted(filename) ? Hyperlink(display, new Uri(filename).AbsoluteUri) : display);
        }
        parts.Add($"[{mimeType}]");
        if (dimensions is { } d) parts.Add($"{d.WidthPx}x{d.HeightPx}");
        return $"[Image: {string.Join(" ", parts)}]";
    }

    // ---- terminal colors ----

    [GeneratedRegex("^\\e\\]11;([^\\a\\e]*)(?:\\a|\\e\\\\)$", RegexOptions.IgnoreCase)]
    private static partial Regex Osc11Response();

    [GeneratedRegex("^(?:\\e\\[\\?997;(1|2)n)+$")]
    private static partial Regex ColorSchemeReport();

    public static bool IsOsc11BackgroundColorResponse(string data) => Osc11Response().IsMatch(data);

    private static int? ParseOscHexChannel(string channel)
    {
        if (channel.Length == 0 || !channel.All(Uri.IsHexDigit)) return null;
        var max = Math.Pow(16, channel.Length) - 1;
        return (int)Math.Round(Convert.ToInt64(channel, 16) / max * 255, MidpointRounding.AwayFromZero);
    }

    public static RgbColor? ParseOsc11BackgroundColor(string data)
    {
        var m = Osc11Response().Match(data);
        if (!m.Success) return null;
        var value = m.Groups[1].Value.Trim();
        if (value.StartsWith('#'))
        {
            var hex = value[1..];
            if (hex.Length == 6 && hex.All(Uri.IsHexDigit)) return new RgbColor(Convert.ToInt32(hex[..2], 16), Convert.ToInt32(hex[2..4], 16), Convert.ToInt32(hex[4..6], 16));
            if (hex.Length == 12 && hex.All(Uri.IsHexDigit))
            {
                return ParseOscHexChannel(hex[..4]) is { } r && ParseOscHexChannel(hex[4..8]) is { } g && ParseOscHexChannel(hex[8..12]) is { } bl ? new RgbColor(r, g, bl) : null;
            }
            return null;
        }
        var rgb = Regex.Replace(value, "^rgba?:", "", RegexOptions.IgnoreCase).Split('/');
        if (rgb.Length < 3) return null;
        return ParseOscHexChannel(rgb[0]) is { } r2 && ParseOscHexChannel(rgb[1]) is { } g2 && ParseOscHexChannel(rgb[2]) is { } b2 ? new RgbColor(r2, g2, b2) : null;
    }

    /// <summary>"dark" | "light", or null when data is not a color scheme report.</summary>
    public static string? ParseTerminalColorSchemeReport(string data)
    {
        var m = ColorSchemeReport().Match(data);
        return m.Success ? m.Groups[1].Captures[^1].Value == "2" ? "light" : "dark" : null;
    }
}
