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
        var srcBytes = srcTile.AsByteSpan();
        var dstBytes = dstTile.AsByteSpan();

        // Precompute the source row/col each destination row/col pulls from.
        var srcRows = new int[h];
        for (int r = 0; r < h; r++)
        {
            int rField = r % rowFactor;
            int rBlockStart = rField * (h / rowFactor) + System.Math.Min(rField, h % rowFactor);
            srcRows[r] = rBlockStart + r / rowFactor;
        }
        var srcCols = new int[w];
        for (int c = 0; c < w; c++)
        {
            int cField = c % colFactor;
            int cBlockStart = cField * (w / colFactor) + System.Math.Min(cField, w % colFactor);
            srcCols[c] = cBlockStart + c / colFactor;
        }

        for (uint p = 0; p < src.Planes; p++)
        {
            for (int r = 0; r < h; r++)
            {
                long dstRow = dstTile.OffsetBytes(r, 0, p);
                long srcRow = srcTile.OffsetBytes(srcRows[r], 0, p);
                long sCol = srcTile.ColStep * pixelSize;
                long dCol = dstTile.ColStep * pixelSize;
                for (int c = 0; c < w; c++)
                {
                    srcBytes.Slice((int)(srcRow + srcCols[c] * sCol), pixelSize)
                            .CopyTo(dstBytes.Slice((int)(dstRow + c * dCol), pixelSize));
                }
            }
        }

        dst.WriteTile(dstTile);
        return dst;
    }
}
