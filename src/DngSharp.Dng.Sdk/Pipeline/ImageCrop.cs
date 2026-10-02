using DngSharp.Dng.Sdk.Imaging;
using DngSharp.Dng.Sdk.Primitives;

using DngSharp.Dng.Sdk.Pixels;

namespace DngSharp.Dng.Sdk.Pipeline;

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

        // Relabel the destination's area with the source coordinates so the
        // layout-agnostic copy lines rows/planes up regardless of storage order.
        PixelKernels.Copy(src.GetTile(clipped), dst.Buffer.WithArea(clipped));
        return dst;
    }
}
