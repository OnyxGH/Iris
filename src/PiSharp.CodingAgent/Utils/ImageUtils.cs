namespace PiSharp.CodingAgent.Utils;

/// <summary>Image MIME sniffing. Port of utils/mime.ts.</summary>
public static class MimeDetect
{
    private const int ImageTypeSniffBytes = 4100;
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

    public static string? DetectSupportedImageMimeType(ReadOnlySpan<byte> buffer)
    {
        if (StartsWith(buffer, [0xff, 0xd8, 0xff])) return buffer.Length > 3 && buffer[3] == 0xf7 ? null : "image/jpeg";
        if (StartsWith(buffer, PngSignature)) return IsPng(buffer) && !IsAnimatedPng(buffer) ? "image/png" : null;
        if (StartsWithAscii(buffer, 0, "GIF")) return "image/gif";
        if (StartsWithAscii(buffer, 0, "RIFF") && StartsWithAscii(buffer, 8, "WEBP")) return "image/webp";
        if (StartsWithAscii(buffer, 0, "BM") && IsBmp(buffer)) return "image/bmp";
        return null;
    }

    public static async Task<string?> DetectSupportedImageMimeTypeFromFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, useAsync: true);
        var buffer = new byte[ImageTypeSniffBytes];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken);
            if (read == 0) break;
            total += read;
        }
        return DetectSupportedImageMimeType(buffer.AsSpan(0, total));
    }

    private static bool IsPng(ReadOnlySpan<byte> buffer) =>
        buffer.Length >= 16 && ReadUint32BE(buffer, PngSignature.Length) == 13 && StartsWithAscii(buffer, 12, "IHDR");

    private static bool IsAnimatedPng(ReadOnlySpan<byte> buffer)
    {
        long offset = PngSignature.Length;
        while (offset + 8 <= buffer.Length)
        {
            var chunkLength = ReadUint32BE(buffer, (int)offset);
            var chunkTypeOffset = (int)offset + 4;
            if (StartsWithAscii(buffer, chunkTypeOffset, "acTL")) return true;
            if (StartsWithAscii(buffer, chunkTypeOffset, "IDAT")) return false;
            var nextOffset = offset + 8 + chunkLength + 4;
            if (nextOffset <= offset || nextOffset > buffer.Length) return false;
            offset = nextOffset;
        }
        return false;
    }

    private static bool IsBmp(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 26) return false;
        var declaredFileSize = ReadUint32LE(buffer, 2);
        var pixelDataOffset = ReadUint32LE(buffer, 10);
        var dibHeaderSize = ReadUint32LE(buffer, 14);
        if (declaredFileSize != 0 && declaredFileSize < 26) return false;
        if (pixelDataOffset < 14 + dibHeaderSize) return false;
        if (declaredFileSize != 0 && pixelDataOffset >= declaredFileSize) return false;

        int colorPlanes, bitsPerPixel;
        if (dibHeaderSize == 12)
        {
            colorPlanes = ReadUint16LE(buffer, 22);
            bitsPerPixel = ReadUint16LE(buffer, 24);
        }
        else if (dibHeaderSize is >= 40 and <= 124)
        {
            if (buffer.Length < 30) return false;
            colorPlanes = ReadUint16LE(buffer, 26);
            bitsPerPixel = ReadUint16LE(buffer, 28);
        }
        else
        {
            return false;
        }
        return colorPlanes == 1 && bitsPerPixel is 1 or 4 or 8 or 16 or 24 or 32;
    }

    private static int At(ReadOnlySpan<byte> buffer, int index) => index < buffer.Length ? buffer[index] : 0;

    private static int ReadUint16LE(ReadOnlySpan<byte> b, int o) => At(b, o) + (At(b, o + 1) << 8);

    private static long ReadUint32BE(ReadOnlySpan<byte> b, int o) =>
        ((long)At(b, o) << 24) + (At(b, o + 1) << 16) + (At(b, o + 2) << 8) + At(b, o + 3);

    private static long ReadUint32LE(ReadOnlySpan<byte> b, int o) =>
        At(b, o) + (At(b, o + 1) << 8) + (At(b, o + 2) << 16) + ((long)At(b, o + 3) << 24);

    private static bool StartsWith(ReadOnlySpan<byte> buffer, ReadOnlySpan<byte> bytes) =>
        buffer.Length >= bytes.Length && buffer[..bytes.Length].SequenceEqual(bytes);

    private static bool StartsWithAscii(ReadOnlySpan<byte> buffer, int offset, string text)
    {
        if (buffer.Length < offset + text.Length) return false;
        for (var i = 0; i < text.Length; i++)
        {
            if (buffer[offset + i] != text[i]) return false;
        }
        return true;
    }
}

public sealed record ProcessImageResult(bool Ok, string Data, string MimeType, List<string> Hints, string Message)
{
    public static ProcessImageResult Success(string data, string mimeType, List<string> hints) => new(true, data, mimeType, hints, "");

    public static ProcessImageResult Failure(string message) => new(false, "", "", [], message);
}

public sealed class ResizedImage
{
    public required string Data { get; init; }
    public required string MimeType { get; init; }
    public int OriginalWidth { get; init; }
    public int OriginalHeight { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public bool WasResized { get; init; }
}

/// <summary>
/// Pluggable image codec. pi uses Photon (WASM) for conversion and resizing; PiSharp has no bundled codec yet, so the
/// default implementation passes supported images through unchanged when they fit the inline size limit.
/// </summary>
public interface IImageCodec
{
    /// <summary>Convert arbitrary image bytes to PNG, or null when unsupported.</summary>
    Task<byte[]?> ConvertToPngAsync(byte[] bytes);

    /// <summary>Resize to fit the given limits, or null when that is impossible.</summary>
    Task<ResizedImage?> ResizeAsync(byte[] bytes, string mimeType, int maxWidth, int maxHeight, long maxBytes, int jpegQuality);
}

public sealed class PassThroughImageCodec : IImageCodec
{
    public Task<byte[]?> ConvertToPngAsync(byte[] bytes) => Task.FromResult<byte[]?>(null);

    public Task<ResizedImage?> ResizeAsync(byte[] bytes, string mimeType, int maxWidth, int maxHeight, long maxBytes, int jpegQuality)
    {
        var data = Convert.ToBase64String(bytes);
        if (data.Length > maxBytes) return Task.FromResult<ResizedImage?>(null);
        return Task.FromResult<ResizedImage?>(new ResizedImage { Data = data, MimeType = mimeType, WasResized = false });
    }
}

/// <summary>Port of utils/image-process.ts.</summary>
public static class ImageProcessor
{
    public static IImageCodec Codec { get; set; } = SkiaImageCodec.IsAvailable ? new SkiaImageCodec() : new PassThroughImageCodec();

    public const int DefaultMaxWidth = 2000;
    public const int DefaultMaxHeight = 2000;
    public const long DefaultMaxBytes = (long)(4.5 * 1024 * 1024);
    public const int DefaultJpegQuality = 80;

    private static string BaseMimeType(string mimeType) => mimeType.Split(';')[0].Trim().ToLowerInvariant();

    private static string? NormalizeSupportedImageMimeType(string mimeType) => BaseMimeType(mimeType) switch
    {
        "image/png" => "image/png",
        "image/jpeg" or "image/jpg" => "image/jpeg",
        "image/gif" => "image/gif",
        "image/webp" => "image/webp",
        _ => null,
    };

    private static string? ConversionHint(string? from, string to) =>
        from is null || from == to ? null : $"[Image converted from {from} to {to}.]";

    /// <summary>Port of formatDimensionNote from utils/image-resize.ts.</summary>
    public static string? FormatDimensionNote(ResizedImage result)
    {
        if (!result.WasResized) return null;
        var scale = (double)result.OriginalWidth / result.Width;
        return $"[Image: original {result.OriginalWidth}x{result.OriginalHeight}, displayed at {result.Width}x{result.Height}. Multiply coordinates by {scale.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)} to map to original image.]";
    }

    /// <summary>
    /// Normalize image blocks returned by tool results (port of utils/tool-result-images.ts). Images that fail processing are
    /// kept as-is. Returns the original list when nothing changed.
    /// </summary>
    public static async Task<List<PiSharp.Ai.ContentBlock>> NormalizeToolResultImagesAsync(List<PiSharp.Ai.ContentBlock> content, bool autoResizeImages = true)
    {
        if (!content.Any(b => b is PiSharp.Ai.ImageContent)) return content;
        var normalized = new List<PiSharp.Ai.ContentBlock>();
        var changed = false;
        foreach (var block in content)
        {
            if (block is not PiSharp.Ai.ImageContent image)
            {
                normalized.Add(block);
                continue;
            }

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(image.Data);
            }
            catch (FormatException)
            {
                normalized.Add(block);
                continue;
            }

            var processed = await ProcessAsync(bytes, image.MimeType, autoResizeImages);
            if (!processed.Ok || (processed.Data == image.Data && processed.MimeType == image.MimeType && processed.Hints.Count == 0))
            {
                normalized.Add(block);
                continue;
            }
            normalized.Add(new PiSharp.Ai.ImageContent(processed.Data, processed.MimeType));
            if (processed.Hints.Count > 0) normalized.Add(new PiSharp.Ai.TextContent(string.Join("\n", processed.Hints)));
            changed = true;
        }
        return changed ? normalized : content;
    }

    public static async Task<ProcessImageResult> ProcessAsync(byte[] bytes, string mimeType, bool autoResizeImages = true)
    {
        var normalizedMime = NormalizeSupportedImageMimeType(mimeType);
        string? convertedFrom = null;
        if (normalizedMime is null)
        {
            var png = await Codec.ConvertToPngAsync(bytes);
            if (png is null) return ProcessImageResult.Failure("[Image omitted: could not be converted to a supported inline image format.]");
            bytes = png;
            normalizedMime = "image/png";
            convertedFrom = BaseMimeType(mimeType);
        }

        var hints = new List<string>();
        if (autoResizeImages)
        {
            var resized = await Codec.ResizeAsync(bytes, normalizedMime, DefaultMaxWidth, DefaultMaxHeight, DefaultMaxBytes, DefaultJpegQuality);
            if (resized is null) return ProcessImageResult.Failure("[Image omitted: could not be resized below the inline image size limit.]");
            if (ConversionHint(convertedFrom, resized.MimeType) is { } convertedHint) hints.Add(convertedHint);
            if (FormatDimensionNote(resized) is { } dimensionNote) hints.Add(dimensionNote);
            return ProcessImageResult.Success(resized.Data, resized.MimeType, hints);
        }

        if (ConversionHint(convertedFrom, normalizedMime) is { } hint) hints.Add(hint);
        return ProcessImageResult.Success(Convert.ToBase64String(bytes), normalizedMime, hints);
    }
}
