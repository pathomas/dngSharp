using Dng.Sdk.Imaging;
using Dng.Sdk.Primitives;

namespace Dng.Sdk.Pipeline;

/// <summary>
/// Crops a <see cref="SimpleImage"/> to a sub-rect, producing a new
/// zero-origin image. Used to apply <c>ActiveArea</c> (and, in principle,
/// <c>DefaultCropArea</c>) after Stage-3 construction, mirroring how
/// <c>dng_validate -3</c> dumps only the active-area sub-rect of the
/// interpolated image.
/// </summary>
public static class ImageCrop
{
    /// <summary>
    /// Crop <paramref name="src"/> to <paramref name="rect"/> (in
    /// <paramref name="src"/>'s coordinate space) and return a new
    /// zero-origin <see cref="SimpleImage"/> of size <c>rect.W x rect.H</c>.
    /// </summary>
    public static SimpleImage Crop(SimpleImage src, DngRect rect)
    {
        ArgumentNullException.ThrowIfNull(src);

        var clipped = DngRect.Intersect(src.Bounds, rect);
        if (clipped.IsEmpty)
            throw new ArgumentException($"Crop rect {rect} does not intersect image bounds {src.Bounds}", nameof(rect));

        var dst = new SimpleImage(new DngRect(clipped.Size), src.Planes, src.PixelType);

        var srcTile = src.GetTile(clipped);
        var dstTile = dst.GetTile(dst.Bounds);

        int rowBytes = (int)clipped.W * dstTile.PixelSize * (int)src.Planes;
        int srcRowStride = (int)(srcTile.RowStep * srcTile.PixelSize);
        int dstRowStride = (int)(dstTile.RowStep * dstTile.PixelSize);
        var srcSpan = srcTile.AsByteSpan();
        var dstSpan = dstTile.AsByteSpan();

        for (int r = 0; r < (int)clipped.H; r++)
        {
            srcSpan.Slice(r * srcRowStride, rowBytes)
                   .CopyTo(dstSpan.Slice(r * dstRowStride, rowBytes));
        }

        dst.WriteTile(dstTile);
        return dst;
    }
}
