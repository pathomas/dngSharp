using DngSharp.Dng.Sdk.Imaging;
using DngSharp.Dng.Sdk.Primitives;

namespace DngSharp.Dng.Sdk.Pipeline;

/// <summary>
/// Reverses DNG 1.7.1 row/column pixel interleaving (<c>RowInterleaveFactor</c>
/// / <c>ColumnInterleaveFactor</c> tags). Mirrors the decode direction of
/// <c>Interleave2D</c> / <c>dng_interleave_task::Process</c> in
/// <c>dng_read_image.cpp</c>.
///
/// <para>When a raw image is stored with row/column interleaving, the strip
/// or tile data on disk is <b>not</b> laid out in normal raster order.
/// Instead, the <c>rowFactor × colFactor</c> "fields" (e.g. the four Bayer
/// CFA quadrants for factor 2×2) are each stored as a contiguous
/// (approximately) <c>H/rowFactor × W/colFactor</c> block, stacked in
/// row-major then column-major field order. This lets a single-plane
/// compressor like JPEG XL efficiently compress each quadrant (which is a
/// smooth, same-color-filter sub-image) independently, then this step
/// reassembles the interleaved raster.</para>
///
/// <para>Field-to-source mapping for destination pixel (r, c):
/// <code>
///   rField = r % rowFactor;  cField = c % colFactor;
///   rBlockStart = rField * (H / rowFactor) + min(rField, H % rowFactor);
///   cBlockStart = cField * (W / colFactor) + min(cField, W % colFactor);
///   srcRow = rBlockStart + r / rowFactor;
///   srcCol = cBlockStart + c / colFactor;
/// </code>
/// </para>
/// </summary>
public static class RowColumnInterleave
{
    /// <summary>
    /// De-interleave <paramref name="src"/> (raw on-disk field layout) into a
    /// new image in normal raster order, using <paramref name="rowFactor"/>
    /// and <paramref name="colFactor"/>. Returns <paramref name="src"/>
    /// unchanged when both factors are 1 (the common case).
    /// </summary>
    public static SimpleImage Decode(SimpleImage src, int rowFactor, int colFactor)
    {
        ArgumentNullException.ThrowIfNull(src);

        int h = (int)src.Bounds.H;
        int w = (int)src.Bounds.W;

        // Mirrors dng_interleave_task's constructor guard: a factor that's
        // >= the corresponding dimension is meaningless — treat as 1 (NOP).
        if (rowFactor >= h) rowFactor = 1;
        if (colFactor >= w) colFactor = 1;

        if (rowFactor <= 1 && colFactor <= 1) return src;

        var dst = new SimpleImage(src.Bounds, src.Planes, src.PixelType);

        var srcTile = src.GetTile(src.Bounds);
        var dstTile = dst.GetTile(dst.Bounds);

        int pixelSize = srcTile.PixelSize;
        int planes = (int)src.Planes;
        int sampleBytes = pixelSize; // per-component byte size (PixelSize is per-component)
        int pixelStrideBytes = sampleBytes * planes;

        var srcBytes = srcTile.AsByteSpan();
        var dstBytes = dstTile.AsByteSpan();

        int srcRowStrideBytes = (int)(srcTile.RowStep * srcTile.PixelSize);
        int dstRowStrideBytes = (int)(dstTile.RowStep * dstTile.PixelSize);

        for (int r = 0; r < h; r++)
        {
            int rField = r % rowFactor;
            int rBlockStart = rField * (h / rowFactor) + System.Math.Min(rField, h % rowFactor);
            int srcRow = rBlockStart + r / rowFactor;

            int dstRowOff = r * dstRowStrideBytes;
            int srcRowOff = srcRow * srcRowStrideBytes;

            for (int c = 0; c < w; c++)
            {
                int cField = c % colFactor;
                int cBlockStart = cField * (w / colFactor) + System.Math.Min(cField, w % colFactor);
                int srcCol = cBlockStart + c / colFactor;

                int dstOff = dstRowOff + c * pixelStrideBytes;
                int srcOff = srcRowOff + srcCol * pixelStrideBytes;

                srcBytes.Slice(srcOff, pixelStrideBytes).CopyTo(dstBytes.Slice(dstOff, pixelStrideBytes));
            }
        }

        dst.WriteTile(dstTile);
        return dst;
    }
}
