using DngSharp.Dng.Sdk.Imaging;
using DngSharp.Dng.Sdk.Pixels;
using DngSharp.Dng.Sdk.Primitives;

namespace DngSharp.Dng.Sdk.Pipeline;

/// <summary>
/// Derives the crop rect implied by a DNG transparency mask
/// (<c>NewSubFileType</c> 4/5, <c>PhotometricInterpretation</c> =
/// <c>TransparencyMask</c>).
/// </summary>
public static class TransparencyMaskCrop
{
    /// <summary>
    /// Finds the largest rect (in <paramref name="mask"/>'s own zero-origin
    /// coordinate space) in which <em>every</em> pixel is fully opaque — the
    /// inscribed rect of the mask's opaque region, not its bounding box.
    /// <para>
    /// The distinction is the whole point. Lens-correction warps and
    /// multi-frame HDR alignment leave the valid-pixel region as a slightly
    /// rotated/barrel-shaped quadrilateral. The <em>bounding box</em> of that
    /// region still contains the transparent corner wedges, so cropping to it
    /// leaves them in frame to be painted white by the mask composite — the
    /// exact white-corner artifact the crop exists to remove. Trimming until
    /// every edge row/column is fully opaque removes them.
    /// </para>
    /// Edges are trimmed one pass at a time against the <em>current</em> rect,
    /// so the result stays balanced across all four sides and its border is
    /// guaranteed opaque (and therefore so is its interior, the opaque region
    /// being convex).
    /// </summary>
    /// <returns>
    /// The fully-opaque rect, or <see langword="null"/> when no fully-opaque
    /// pixel exists or the mask's <see cref="DngImage.PixelType"/> isn't
    /// supported.
    /// </returns>
    public static DngRect? FindFullyOpaqueRect(SimpleImage mask)
    {
        ArgumentNullException.ThrowIfNull(mask);

        int w = (int)mask.Bounds.W, h = (int)mask.Bounds.H;
        if (w <= 0 || h <= 0) return null;

        var tile = mask.GetTile(mask.Bounds);

        double maxValue = mask.PixelType switch
        {
            PixelType.UInt8 => 255.0,
            PixelType.UInt16 => 65535.0,
            PixelType.Float32 => 1.0,
            _ => 255.0,
        };
        // Tolerance for rounding: half a code value for integer masks, a
        // relative epsilon for unit-scaled float masks (an absolute 0.5 there
        // would treat half-transparent pixels as fully opaque).
        double tolerance = mask.PixelType == PixelType.Float32 ? maxValue * 1e-6 : 0.5;
        double opaqueThreshold = maxValue - tolerance;

        Func<int, double>? valueAt = mask.PixelType switch
        {
            PixelType.UInt8 => i => tile.AsTypedSpan<byte>()[i],
            PixelType.UInt16 => i => tile.AsTypedSpan<ushort>()[i],
            PixelType.Float32 => i => tile.AsTypedSpan<float>()[i],
            _ => null,
        };
        if (valueAt is null) return null;

        bool RowIsFullyOpaque(int row, int left, int right)
        {
            int rowBase = row * w;
            for (int c = left; c < right; c++)
                if (valueAt(rowBase + c) < opaqueThreshold) return false;
            return true;
        }

        bool ColIsFullyOpaque(int col, int top, int bottom)
        {
            for (int r = top; r < bottom; r++)
                if (valueAt(r * w + col) < opaqueThreshold) return false;
            return true;
        }

        int top = 0, bottom = h, left = 0, right = w;
        bool trimmed = true;
        while (trimmed && top < bottom && left < right)
        {
            trimmed = false;

            if (!RowIsFullyOpaque(top, left, right)) { top++; trimmed = true; }
            if (bottom > top && !RowIsFullyOpaque(bottom - 1, left, right)) { bottom--; trimmed = true; }
            if (top >= bottom) break;

            if (!ColIsFullyOpaque(left, top, bottom)) { left++; trimmed = true; }
            if (right > left && !ColIsFullyOpaque(right - 1, top, bottom)) { right--; trimmed = true; }
        }

        if (top >= bottom || left >= right) return null;

        return new DngRect(top, left, bottom, right);
    }
}
