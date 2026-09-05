using System.Buffers.Binary;
using DngSharp.Dng.Sdk.Container;
using DngSharp.Dng.Sdk.IO;
using DngSharp.Dng.Sdk.Primitives;
using DngSharp.Dng.Sdk.Tiff;

namespace DngSharp.Dng.Sdk.Imaging.Raw;

/// <summary>
/// Reads <c>DefaultCropOrigin</c> (0xC61F) and <c>DefaultCropSize</c>
/// (0xC620) from a main IFD into a <see cref="DngRect"/>. Mirrors
/// <c>dng_ifd::PostParse</c>'s handling of these tags and
/// <c>dng_negative::DefaultCropArea</c>: both tags carry two values,
/// <c>[H, V]</c>, stored as SHORT, LONG, or RATIONAL (all three are legal
/// per spec and seen in the wild).
///
/// <para>This is the "clean" rendered-image rect — tighter than
/// <c>ActiveArea</c> — that <c>dng_render.cpp</c> uses as the source bounds
/// for the color pipeline. Unlike <c>ActiveArea</c>, it commonly excludes a
/// few extra pixels at the sensor/lens-correction edges (e.g. where
/// <c>WarpRectilinear</c> has to clamp to the image boundary and would
/// otherwise show streaking), so it must be applied for final render output,
/// not just for raw dumps.</para>
/// </summary>
public static class DefaultCropAreaReader
{
    /// <summary>
    /// Read <c>DefaultCropOrigin</c>/<c>DefaultCropSize</c> from <paramref name="ifd"/>,
    /// or <see langword="null"/> if either tag is absent or malformed. The
    /// result is clamped to <paramref name="imageBounds"/> (when non-empty),
    /// mirroring the slide-back-into-bounds logic in
    /// <c>dng_negative::DefaultCropArea</c>.
    /// </summary>
    public static DngRect? ReadDefaultCropArea(DngStream stream, TiffIfd ifd, bool bigEndian, DngRect imageBounds)
    {
        var originEntry = ifd.Find(DngTagCode.DefaultCropOrigin);
        var sizeEntry = ifd.Find(DngTagCode.DefaultCropSize);
        if (originEntry is null || sizeEntry is null) return null;

        var origin = ReadDoubleArray(stream, originEntry, bigEndian);
        var size = ReadDoubleArray(stream, sizeEntry, bigEndian);
        if (origin.Length < 2 || size.Length < 2) return null;

        double originH = origin[0], originV = origin[1];
        double sizeH = size[0], sizeV = size[1];

        int left = (int)System.Math.Round(originH);
        int top = (int)System.Math.Round(originV);
        int right = left + (int)System.Math.Round(sizeH);
        int bottom = top + (int)System.Math.Round(sizeV);

        if (!imageBounds.IsEmpty)
        {
            // Slide the crop back into the image bounds instead of letting it
            // run off the edge (mirrors dng_negative::DefaultCropArea).
            if (right > imageBounds.R)
            {
                left -= right - imageBounds.R;
                right = imageBounds.R;
            }
            left = System.Math.Max(imageBounds.L, left);

            if (bottom > imageBounds.B)
            {
                top -= bottom - imageBounds.B;
                bottom = imageBounds.B;
            }
            top = System.Math.Max(imageBounds.T, top);
        }

        if (right <= left || bottom <= top) return null;

        return new DngRect(top, left, bottom, right);
    }

    private static double[] ReadDoubleArray(DngStream stream, TiffIfdEntry entry, bool bigEndian)
    {
        int count = (int)System.Math.Min((ulong)int.MaxValue, entry.Count);
        var result = new double[count];

        switch (entry.Type)
        {
            case TiffDataType.Short:
            case TiffDataType.SShort:
                {
                    var bytes = ReadAllBytes(stream, entry);
                    var s = bytes.Span;
                    for (int i = 0; i < count && (i * 2 + 2) <= s.Length; i++)
                        result[i] = bigEndian
                            ? BinaryPrimitives.ReadUInt16BigEndian(s[(i * 2)..])
                            : BinaryPrimitives.ReadUInt16LittleEndian(s[(i * 2)..]);
                    break;
                }
            case TiffDataType.Long:
            case TiffDataType.SLong:
                {
                    var bytes = ReadAllBytes(stream, entry);
                    var s = bytes.Span;
                    for (int i = 0; i < count && (i * 4 + 4) <= s.Length; i++)
                        result[i] = bigEndian
                            ? BinaryPrimitives.ReadUInt32BigEndian(s[(i * 4)..])
                            : BinaryPrimitives.ReadUInt32LittleEndian(s[(i * 4)..]);
                    break;
                }
            case TiffDataType.Rational:
            case TiffDataType.SRational:
                {
                    var bytes = ReadAllBytes(stream, entry);
                    var s = bytes.Span;
                    for (int i = 0; i < count && (i * 8 + 8) <= s.Length; i++)
                    {
                        uint num = bigEndian
                            ? BinaryPrimitives.ReadUInt32BigEndian(s[(i * 8)..])
                            : BinaryPrimitives.ReadUInt32LittleEndian(s[(i * 8)..]);
                        uint den = bigEndian
                            ? BinaryPrimitives.ReadUInt32BigEndian(s[(i * 8 + 4)..])
                            : BinaryPrimitives.ReadUInt32LittleEndian(s[(i * 8 + 4)..]);
                        result[i] = den == 0 ? 0.0 : (double)num / den;
                    }
                    break;
                }
            default:
                return [];
        }

        return result;
    }

    private static ReadOnlyMemory<byte> ReadAllBytes(DngStream stream, TiffIfdEntry entry)
    {
        if (entry.IsInline)
        {
            int len = (int)System.Math.Min(entry.PayloadSize, (ulong)entry.InlineValue.Length);
            return entry.InlineValue[..len];
        }
        if (entry.PayloadSize > int.MaxValue) return ReadOnlyMemory<byte>.Empty;
        var buf = new byte[(int)entry.PayloadSize];
        long saved = stream.Position;
        try
        {
            stream.Position = entry.ValueOffset;
            stream.ReadExactly(buf);
        }
        finally { stream.Position = saved; }
        return buf;
    }
}
