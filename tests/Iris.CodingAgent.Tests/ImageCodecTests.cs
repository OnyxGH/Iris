using Iris.CodingAgent.Utils;
using SkiaSharp;

namespace Iris.CodingAgent.Tests;

public class ImageCodecTests
{
    private static byte[] Encode(SKBitmap bitmap, SKEncodedImageFormat format, int quality = 100)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, quality);
        return data.ToArray();
    }

    private static SKBitmap Noise(int width, int height)
    {
        var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var random = new Random(42);
        var pixels = bitmap.GetPixelSpan();
        random.NextBytes(pixels);
        for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        return bitmap;
    }

    [Fact]
    public async Task SmallImagesPassThroughUnchanged()
    {
        using var bitmap = Noise(40, 20);
        var png = Encode(bitmap, SKEncodedImageFormat.Png);
        var result = await ImageProcessor.ProcessAsync(png, "image/png");
        Assert.True(result.Ok);
        Assert.Equal(Convert.ToBase64String(png), result.Data);
        Assert.Equal("image/png", result.MimeType);
        Assert.Empty(result.Hints);
    }

    [Fact]
    public async Task LargeImagesAreResizedWithDimensionNote()
    {
        using var bitmap = new SKBitmap(3000, 1500, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(SKColors.CornflowerBlue);
        var png = Encode(bitmap, SKEncodedImageFormat.Png);
        var codec = new SkiaImageCodec();
        var resized = await codec.ResizeAsync(png, "image/png", 2000, 2000, (long)(4.5 * 1024 * 1024), 80);
        Assert.NotNull(resized);
        Assert.True(resized.WasResized);
        Assert.Equal((2000, 1000), (resized.Width, resized.Height));
        Assert.Equal((3000, 1500), (resized.OriginalWidth, resized.OriginalHeight));
        Assert.Equal("[Image: original 3000x1500, displayed at 2000x1000. Multiply coordinates by 1.50 to map to original image.]", ImageProcessor.FormatDimensionNote(resized));
        using var decoded = SKBitmap.Decode(Convert.FromBase64String(resized.Data));
        Assert.Equal((2000, 1000), (decoded.Width, decoded.Height));
    }

    [Fact]
    public async Task OversizedPayloadsShrinkUntilUnderTheByteLimit()
    {
        using var bitmap = Noise(600, 600);
        var png = Encode(bitmap, SKEncodedImageFormat.Png);
        var resized = await new SkiaImageCodec().ResizeAsync(png, "image/png", 2000, 2000, 60_000, 80);
        Assert.NotNull(resized);
        Assert.True(resized.Data.Length < 60_000);
        Assert.True(resized.WasResized);
    }

    [Fact]
    public async Task UnsupportedFormatsAreConvertedToPng()
    {
        using var bitmap = Noise(8, 6);
        var bmp = new byte[54 + 8 * 6 * 4];
        // Minimal 32-bit BMP.
        void W32(int offset, int value) => BitConverter.GetBytes(value).CopyTo(bmp, offset);
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        W32(2, bmp.Length);
        W32(10, 54);
        W32(14, 40);
        W32(18, 8);
        W32(22, 6);
        BitConverter.GetBytes((short)1).CopyTo(bmp, 26);
        BitConverter.GetBytes((short)32).CopyTo(bmp, 28);
        W32(34, 8 * 6 * 4);
        for (var i = 54; i < bmp.Length; i += 4) bmp[i + 3] = 255;
        var result = await ImageProcessor.ProcessAsync(bmp, "image/bmp");
        Assert.True(result.Ok, result.Message);
        Assert.Equal("image/png", result.MimeType);
        Assert.Contains("[Image converted from image/bmp to image/png.]", result.Hints);
    }

    [Fact]
    public void ExifOrientationIsReadFromJpegApp1()
    {
        // SOI, APP1 "Exif\0\0" + little-endian TIFF with one IFD entry: Orientation (0x0112) = 6.
        byte[] tiff = [0x49, 0x49, 0x2a, 0x00, 0x08, 0x00, 0x00, 0x00, 0x01, 0x00, 0x12, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00, 0x06, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        byte[] exif = [0x45, 0x78, 0x69, 0x66, 0x00, 0x00, .. tiff];
        var length = exif.Length + 2;
        byte[] jpeg = [0xff, 0xd8, 0xff, 0xe1, (byte)(length >> 8), (byte)length, .. exif, 0xff, 0xd9];
        Assert.Equal(6, ExifOrientation.Get(jpeg));
        Assert.Equal(1, ExifOrientation.Get([0x89, 0x50, 0x4e, 0x47]));
    }

    [Theory]
    [InlineData(6, 0, 0, 1, 0)] // rotate clockwise: top-left source pixel moves to top-right
    [InlineData(8, 0, 0, 0, 2)] // rotate counter-clockwise: top-left moves to bottom-left
    [InlineData(3, 0, 0, 2, 1)] // rotate 180
    [InlineData(2, 0, 0, 2, 0)] // flip horizontal
    public void OrientationTransformsMovePixelsLikePi(int orientation, int srcX, int srcY, int dstX, int dstY)
    {
        var bitmap = new SKBitmap(new SKImageInfo(3, 2, SKColorType.Rgba8888, SKAlphaType.Premul));
        bitmap.Erase(SKColors.Black);
        bitmap.SetPixel(srcX, srcY, SKColors.Red);
        using var result = SkiaImageCodec.ApplyOrientation(bitmap, orientation);
        Assert.Equal(SKColors.Red, result.GetPixel(dstX, dstY));
    }
}
