using System.Buffers.Binary;
using DngSharp.Dng.Sdk.Container;
using DngSharp.Dng.Sdk.IO;
using DngSharp.Dng.Sdk.Primitives;
using DngSharp.Dng.Sdk.Tiff;

namespace DngSharp.Dng.Sdk.Imaging.Raw;

/// <summary>
/// Reads the <c>ActiveArea</c> tag (0xC68D) from a main IFD into a
/// <see cref="DngRect"/>. Mirrors the relevant slice of
/// <c>dng_ifd::PostParse</c>: four values, <c>[top, left, bottom, right]</c>,
/// stored as SHORT or LONG (both seen in the wild).
/// </summary>
public static class CropAreaReader
{
    /// <summary>
    /// Read <c>ActiveArea</c> from <paramref name="ifd"/>, or <see langword="null"/>
    /// if the tag is absent or malformed.
    /// </summary>
    public static DngRect? ReadActiveArea(DngStream stream, TiffIfd ifd, bool bigEndian)
    {
        var entry = ifd.Find(DngTagCode.ActiveArea);
        if (entry is null) return null;

        var values = ReadUInt32Array(stream, entry, bigEndian);
        if (values.Length < 4) return null;

        return new DngRect((int)values[0], (int)values[1], (int)values[2], (int)values[3]);
    }

    private static uint[] ReadUInt32Array(DngStream stream, TiffIfdEntry entry, bool bigEndian)
    {
        int count = (int)System.Math.Min((ulong)int.MaxValue, entry.Count);
        var result = new uint[count];

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
