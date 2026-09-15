using SkiaSharp;

namespace Iris.CodingAgent.Utils;

/// <summary>EXIF orientation lookup for JPEG and WebP. Port of utils/exif-orientation.ts.</summary>
public static class ExifOrientation
{
    private static int ReadOrientationFromTiff(byte[] bytes, int tiffStart)
    {
        if (tiffStart + 8 > bytes.Length) return 1;
        var le = ((bytes[tiffStart] << 8) | bytes[tiffStart + 1]) == 0x4949;
        int Read16(int pos) => le ? bytes[pos] | (bytes[pos + 1] << 8) : (bytes[pos] << 8) | bytes[pos + 1];
        long Read32(int pos) => le
            ? (uint)(bytes[pos] | (bytes[pos + 1] << 8) | (bytes[pos + 2] << 16) | (bytes[pos + 3] << 24))
            : (uint)((bytes[pos] << 24) | (bytes[pos + 1] << 16) | (bytes[pos + 2] << 8) | bytes[pos + 3]);

        var ifdStart = tiffStart + Read32(tiffStart + 4);
        if (ifdStart + 2 > bytes.Length) return 1;
        var entryCount = Read16((int)ifdStart);
        for (var i = 0; i < entryCount; i++)
        {
            var entryPos = ifdStart + 2 + i * 12;
            if (entryPos + 12 > bytes.Length) return 1;
            if (Read16((int)entryPos) == 0x0112)
            {
                var value = Read16((int)entryPos + 8);
                return value is >= 1 and <= 8 ? value : 1;
            }
        }
        return 1;
    }

    private static bool HasExifHeader(byte[] bytes, int offset) =>
        offset + 6 <= bytes.Length && bytes[offset] == 0x45 && bytes[offset + 1] == 0x78 && bytes[offset + 2] == 0x69 && bytes[offset + 3] == 0x66
        && bytes[offset + 4] == 0x00 && bytes[offset + 5] == 0x00;

    private static int FindJpegTiffOffset(byte[] bytes)
    {
        var offset = 2;
        while (offset < bytes.Length - 1)
        {
            if (bytes[offset] != 0xff) return -1;
            var marker = bytes[offset + 1];
            if (marker == 0xff)
            {
                offset++;
                continue;
            }
            if (marker == 0xe1)
            {
                if (offset + 4 >= bytes.Length) return -1;
                var segmentStart = offset + 4;
                if (segmentStart + 6 > bytes.Length) return -1;
                if (HasExifHeader(bytes, segmentStart)) return segmentStart + 6;
            }
            if (offset + 4 > bytes.Length) return -1;
            var length = (bytes[offset + 2] << 8) | bytes[offset + 3];
            offset += 2 + length;
        }
        return -1;
    }

    private static int FindWebpTiffOffset(byte[] bytes)
    {
        var offset = 12;
        while (offset + 8 <= bytes.Length)
        {
            var chunkId = System.Text.Encoding.ASCII.GetString(bytes, offset, 4);
            var chunkSize = bytes[offset + 4] | (bytes[offset + 5] << 8) | (bytes[offset + 6] << 16) | (bytes[offset + 7] << 24);
            var dataStart = offset + 8;
            if (chunkId == "EXIF")
            {
                if (dataStart + chunkSize > bytes.Length) return -1;
                return chunkSize >= 6 && HasExifHeader(bytes, dataStart) ? dataStart + 6 : dataStart;
            }
            offset = dataStart + chunkSize + (chunkSize % 2);
        }
        return -1;
    }

    public static int Get(byte[] bytes)
    {
        var tiffOffset = -1;
        if (bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xd8)
        {
            tiffOffset = FindJpegTiffOffset(bytes);
        }
        else if (bytes.Length >= 12 && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
            && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
        {
            tiffOffset = FindWebpTiffOffset(bytes);
        }
        return tiffOffset == -1 ? 1 : ReadOrientationFromTiff(bytes, tiffOffset);
    }
}

/// <summary>
/// SkiaSharp image codec standing in for pi's Photon (WASM): decoding, EXIF orientation, resizing and PNG/JPEG encoding.
/// Port of utils/image-resize-core.ts and image-convert.ts. Encoded bytes differ from Photon's, but the size limits,
/// quality steps and dimension strategy are the same.
/// </summary>
public sealed class SkiaImageCodec : IImageCodec
{
    private static readonly Lazy<bool> Available = new(() =>
    {
        try
        {
            using var probe = new SKBitmap(1, 1);
            return true;
        }
        catch
        {
            return false;
        }
    });

    /// <summary>False when the native Skia library cannot be loaded on this platform.</summary>
    public static bool IsAvailable => Available.Value;

    private static SKBitmap? Decode(byte[] bytes)
    {
        using var decoded = SKBitmap.Decode(bytes);
        if (decoded is null) return null;
        var normalized = new SKBitmap(new SKImageInfo(decoded.Width, decoded.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (!decoded.CopyTo(normalized, SKColorType.Rgba8888))
        {
            using var canvas = new SKCanvas(normalized);
            canvas.DrawBitmap(decoded, 0, 0);
        }
        return ApplyOrientation(normalized, ExifOrientation.Get(bytes));
    }

    private static SKBitmap Transform(SKBitmap source, int width, int height, Action<SKCanvas> setup)
    {
        var result = new SKBitmap(new SKImageInfo(width, height, source.ColorType, source.AlphaType));
        using (var canvas = new SKCanvas(result))
        {
            canvas.Clear(SKColors.Transparent);
            setup(canvas);
            using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
            canvas.DrawBitmap(source, 0, 0, paint);
        }
        source.Dispose();
        return result;
    }

    private static SKBitmap FlipHorizontal(SKBitmap b) => Transform(b, b.Width, b.Height, c =>
    {
        c.Translate(b.Width, 0);
        c.Scale(-1, 1);
    });

    private static SKBitmap FlipVertical(SKBitmap b) => Transform(b, b.Width, b.Height, c =>
    {
        c.Translate(0, b.Height);
        c.Scale(1, -1);
    });

    private static SKBitmap RotateClockwise(SKBitmap b) => Transform(b, b.Height, b.Width, c =>
    {
        c.Translate(b.Height, 0);
        c.RotateDegrees(90);
    });

    private static SKBitmap RotateCounterClockwise(SKBitmap b) => Transform(b, b.Height, b.Width, c =>
    {
        c.Translate(0, b.Width);
        c.RotateDegrees(-90);
    });

    internal static SKBitmap ApplyOrientation(SKBitmap image, int orientation) => orientation switch
    {
        2 => FlipHorizontal(image),
        3 => FlipVertical(FlipHorizontal(image)),
        4 => FlipVertical(image),
        5 => FlipHorizontal(RotateClockwise(image)),
        6 => RotateClockwise(image),
        7 => FlipHorizontal(RotateCounterClockwise(image)),
        8 => RotateCounterClockwise(image),
        _ => image,
    };

    /// <summary>High-quality downscale (Photon uses Lanczos3): box halving, then a Catmull-Rom cubic pass.</summary>
    private static SKBitmap Resize(SKBitmap source, int width, int height)
    {
        var current = source;
        var owned = false;
        while (current.Width / 2 >= width && current.Height / 2 >= height && current.Width >= 2 && current.Height >= 2)
        {
            var half = current.Resize(new SKImageInfo(current.Width / 2, current.Height / 2, current.ColorType, current.AlphaType), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
            if (owned) current.Dispose();
            current = half;
            owned = true;
        }
        var result = current.Resize(new SKImageInfo(width, height, current.ColorType, current.AlphaType), new SKSamplingOptions(SKCubicResampler.CatmullRom));
        if (owned) current.Dispose();
        return result;
    }

    private static byte[] Encode(SKBitmap bitmap, SKEncodedImageFormat format, int quality)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = format == SKEncodedImageFormat.Jpeg ? EncodeJpeg(bitmap, quality) : image.Encode(format, quality);
        return data.ToArray();
    }

    private static SKData EncodeJpeg(SKBitmap bitmap, int quality)
    {
        // JPEG has no alpha: flatten like Photon, which drops the alpha channel (transparent pixels keep their RGB).
        using var opaque = new SKBitmap(new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Rgb888x, SKAlphaType.Opaque));
        var src = bitmap.GetPixelSpan();
        var dst = opaque.GetPixelSpan();
        for (var i = 0; i + 3 < src.Length && i + 3 < dst.Length; i += 4)
        {
            // Pixels are premultiplied; undo that so semi-transparent pixels keep their color, as with Photon.
            var alpha = src[i + 3];
            dst[i] = alpha is 0 or 255 ? src[i] : (byte)Math.Min(255, src[i] * 255 / alpha);
            dst[i + 1] = alpha is 0 or 255 ? src[i + 1] : (byte)Math.Min(255, src[i + 1] * 255 / alpha);
            dst[i + 2] = alpha is 0 or 255 ? src[i + 2] : (byte)Math.Min(255, src[i + 2] * 255 / alpha);
            dst[i + 3] = 255;
        }
        using var image = SKImage.FromBitmap(opaque);
        return image.Encode(SKEncodedImageFormat.Jpeg, quality);
    }

    public Task<byte[]?> ConvertToPngAsync(byte[] bytes)
    {
        if (!IsAvailable) return Task.FromResult<byte[]?>(null);
        try
        {
            using var image = Decode(bytes);
            return Task.FromResult(image is null ? null : Encode(image, SKEncodedImageFormat.Png, 100));
        }
        catch
        {
            return Task.FromResult<byte[]?>(null);
        }
    }

    public Task<ResizedImage?> ResizeAsync(byte[] bytes, string mimeType, int maxWidth, int maxHeight, long maxBytes, int jpegQuality) =>
        Task.Run(() => ResizeCore(bytes, mimeType, maxWidth, maxHeight, maxBytes, jpegQuality));

    private static ResizedImage? ResizeCore(byte[] bytes, string mimeType, int maxWidth, int maxHeight, long maxBytes, int jpegQuality)
    {
        if (!IsAvailable) return null;
        var inputBase64Size = (long)Math.Ceiling(bytes.Length / 3.0) * 4;
        SKBitmap? image = null;
        try
        {
            image = Decode(bytes);
            if (image is null) return null;
            var originalWidth = image.Width;
            var originalHeight = image.Height;
            var format = mimeType.Split('/').ElementAtOrDefault(1) ?? "png";

            if (originalWidth <= maxWidth && originalHeight <= maxHeight && inputBase64Size < maxBytes)
            {
                return new ResizedImage
                {
                    Data = Convert.ToBase64String(bytes),
                    MimeType = string.IsNullOrEmpty(mimeType) ? $"image/{format}" : mimeType,
                    OriginalWidth = originalWidth,
                    OriginalHeight = originalHeight,
                    Width = originalWidth,
                    Height = originalHeight,
                    WasResized = false,
                };
            }

            var targetWidth = originalWidth;
            var targetHeight = originalHeight;
            if (targetWidth > maxWidth)
            {
                targetHeight = (int)Math.Round((double)targetHeight * maxWidth / targetWidth, MidpointRounding.AwayFromZero);
                targetWidth = maxWidth;
            }
            if (targetHeight > maxHeight)
            {
                targetWidth = (int)Math.Round((double)targetWidth * maxHeight / targetHeight, MidpointRounding.AwayFromZero);
                targetHeight = maxHeight;
            }

            var qualitySteps = new[] { jpegQuality, 85, 70, 55, 40 }.Distinct().ToArray();
            var currentWidth = Math.Max(1, targetWidth);
            var currentHeight = Math.Max(1, targetHeight);
            while (true)
            {
                using var resized = Resize(image, currentWidth, currentHeight);
                var candidates = new List<(string Data, string MimeType)> { (Convert.ToBase64String(Encode(resized, SKEncodedImageFormat.Png, 100)), "image/png") };
                foreach (var quality in qualitySteps) candidates.Add((Convert.ToBase64String(Encode(resized, SKEncodedImageFormat.Jpeg, quality)), "image/jpeg"));
                foreach (var candidate in candidates)
                {
                    if (candidate.Data.Length < maxBytes)
                    {
                        return new ResizedImage
                        {
                            Data = candidate.Data,
                            MimeType = candidate.MimeType,
                            OriginalWidth = originalWidth,
                            OriginalHeight = originalHeight,
                            Width = currentWidth,
                            Height = currentHeight,
                            WasResized = true,
                        };
                    }
                }
                if (currentWidth == 1 && currentHeight == 1) break;
                var nextWidth = currentWidth == 1 ? 1 : Math.Max(1, (int)Math.Floor(currentWidth * 0.75));
                var nextHeight = currentHeight == 1 ? 1 : Math.Max(1, (int)Math.Floor(currentHeight * 0.75));
                if (nextWidth == currentWidth && nextHeight == currentHeight) break;
                currentWidth = nextWidth;
                currentHeight = nextHeight;
            }
            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            image?.Dispose();
        }
    }
}
