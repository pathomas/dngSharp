using SkiaSharp;

namespace Dng.Sdk.Preview;

/// <summary>
/// Encodes a pixel buffer to WebP using SkiaSharp.
///
/// <para>Two output modes are supported:
/// <list type="bullet">
///   <item><b>SDR (8-bit per channel):</b> Input is R, G, B bytes
///         (gamma-encoded, width × height × 3). Emits a lossy or lossless
///         WebP. This is the default path for <c>-webp</c>.</item>
///   <item><b>HDR (linear float):</b> Input is linear-sRGB Float32 triples
///         (width × height × 3 × 4 bytes). Values beyond [0,1] are preserved
///         by writing as a 16-bit per channel (RGBA F16) WebP. Emits an
///         extended-range VP8L WebP supported by modern browsers. This is the
///         path for <c>-webp -hdr</c>.</item>
/// </list>
/// </para>
/// </summary>
public static class WebPEncoder
{
    // ── SDR path ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Encode a gamma-encoded sRGB byte buffer to WebP (8-bit per channel).
    /// </summary>
    /// <param name="rgbBytes">Interleaved R, G, B bytes (no alpha), row-major.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="quality">WebP quality 0–100 (default 90). 100 triggers
    /// lossless encoding.</param>
    /// <returns>WebP file bytes.</returns>
    public static byte[] EncodeSdr(ReadOnlySpan<byte> rgbBytes, int width, int height, int quality = 90)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (quality < 0 || quality > 100) throw new ArgumentOutOfRangeException(nameof(quality));
        if (rgbBytes.Length < width * height * 3)
            throw new ArgumentException($"rgbBytes too small for {width}×{height} RGB24");

        using var bmp = CreateRgb888xBitmap(rgbBytes, width, height);
        using var stream = new MemoryStream();
        bmp.Encode(stream, SKEncodedImageFormat.Webp, quality);
        return stream.ToArray();
    }

    // ── HDR path ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Encode a linear-sRGB Float32 buffer to an HDR WebP (F16 per channel).
    /// Values outside [0, 1] are preserved up to the F16 range.
    /// </summary>
    /// <param name="linearRgbFloat">Interleaved R, G, B Float32 (no alpha),
    /// row-major. Length must be <c>width × height × 3 × 4</c> bytes.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <returns>WebP file bytes (F16 VP8L).</returns>
    public static byte[] EncodeHdr(ReadOnlySpan<byte> linearRgbFloat, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (linearRgbFloat.Length < width * height * 12)
            throw new ArgumentException($"linearRgbFloat too small for {width}×{height} RGB32F");

        using var bmp = CreateRgbaF16Bitmap(linearRgbFloat, width, height);
        using var stream = new MemoryStream();
        // Quality 100 = lossless for WebP; F16 data is preserved.
        bmp.Encode(stream, SKEncodedImageFormat.Webp, 100);
        return stream.ToArray();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static SKBitmap CreateRgb888xBitmap(ReadOnlySpan<byte> rgb, int width, int height)
    {
        var info = new SKImageInfo(width, height, SKColorType.Rgb888x, SKAlphaType.Opaque);
        var bmp = new SKBitmap(info);
        unsafe
        {
            byte* dst = (byte*)bmp.GetPixels().ToPointer();
            int srcIdx = 0;
            for (int i = 0; i < width * height; i++, srcIdx += 3, dst += 4)
            {
                dst[0] = rgb[srcIdx];
                dst[1] = rgb[srcIdx + 1];
                dst[2] = rgb[srcIdx + 2];
                dst[3] = 255;
            }
        }
        return bmp;
    }

    private static SKBitmap CreateRgbaF16Bitmap(ReadOnlySpan<byte> rgbFloat, int width, int height)
    {
        // Use RgbaF16 (8 bytes/pixel: R-F16, G-F16, B-F16, A-F16).
        var info = new SKImageInfo(width, height, SKColorType.RgbaF16, SKAlphaType.Unpremul);
        var bmp = new SKBitmap(info);
        unsafe
        {
            ushort* dst = (ushort*)bmp.GetPixels().ToPointer();
            int srcByteIdx = 0;
            for (int i = 0; i < width * height; i++, srcByteIdx += 12, dst += 4)
            {
                // Read three Float32 values, convert to Float16.
                float r = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(rgbFloat.Slice(srcByteIdx,     4));
                float g = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(rgbFloat.Slice(srcByteIdx + 4, 4));
                float b = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(rgbFloat.Slice(srcByteIdx + 8, 4));
                dst[0] = FloatToHalf(r);
                dst[1] = FloatToHalf(g);
                dst[2] = FloatToHalf(b);
                dst[3] = FloatToHalf(1.0f); // fully opaque
            }
        }
        return bmp;
    }

    /// <summary>
    /// Convert a 32-bit float to a 16-bit half-precision float (IEEE 754-2008).
    /// Uses hardware intrinsics when available (.NET 5+).
    /// </summary>
    private static ushort FloatToHalf(float value) =>
        (ushort)System.BitConverter.HalfToInt16Bits((Half)value);
}
