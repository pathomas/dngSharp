using SkiaSharp;

namespace DngSharp.Dng.Sdk.Preview;

/// <summary>
/// Encodes an 8-bit RGB pixel buffer (width × height × 3 bytes, sRGB) to JPEG
/// using SkiaSharp / Skia's Dct encoding.
///
/// <para>The input is expected to be the output of
/// <c>Stage3Renderer.GammaAndQuantize</c> — a contiguous row-major buffer of
/// R, G, B bytes (no alpha channel).</para>
/// </summary>
public static class JpegEncoder
{
    /// <summary>
    /// Encode a sRGB byte buffer to JPEG.
    /// </summary>
    /// <param name="rgbBytes">Interleaved R, G, B bytes (no alpha). Must be exactly
    /// <c>width × height × 3</c> bytes.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="quality">JPEG quality 1–100 (default 90).</param>
    /// <returns>JPEG file bytes (starts with FF D8 FF).</returns>
    public static byte[] Encode(ReadOnlySpan<byte> rgbBytes, int width, int height, int quality = 90)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (quality < 1 || quality > 100) throw new ArgumentOutOfRangeException(nameof(quality));
        if (rgbBytes.Length < width * height * 3)
            throw new ArgumentException($"rgbBytes too small for {width}×{height} RGB24");

        using var bmp = CreateBitmap(rgbBytes, width, height);
        using var stream = new MemoryStream();
        bmp.Encode(stream, SKEncodedImageFormat.Jpeg, quality);
        return stream.ToArray();
    }

    private static SKBitmap CreateBitmap(ReadOnlySpan<byte> rgb, int width, int height)
    {
        var info = new SKImageInfo(width, height, SKColorType.Rgb888x, SKAlphaType.Opaque);
        var bmp = new SKBitmap(info);

        // SKColorType.Rgb888x is 4 bytes/pixel (RGBX). We need to expand our
        // 3-byte RGB to 4-byte RGBX (X = unused/opaque).
        unsafe
        {
            byte* dst = (byte*)bmp.GetPixels().ToPointer();
            int srcIdx = 0;
            int pixels = width * height;
            for (int i = 0; i < pixels; i++, srcIdx += 3, dst += 4)
            {
                dst[0] = rgb[srcIdx];
                dst[1] = rgb[srcIdx + 1];
                dst[2] = rgb[srcIdx + 2];
                dst[3] = 255; // X / opaque
            }
        }
        return bmp;
    }
}
