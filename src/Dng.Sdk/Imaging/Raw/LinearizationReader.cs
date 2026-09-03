using System.Buffers.Binary;
using Dng.Sdk.Container;
using Dng.Sdk.IO;
using Dng.Sdk.Primitives;
using Dng.Sdk.Tiff;

namespace Dng.Sdk.Imaging.Raw;

/// <summary>
/// Reads linearization metadata from a parsed DNG/TIFF main IFD into a
/// <see cref="LinearizationInfo"/>. Mirrors the relevant tag-reading body of
/// <c>dng_ifd::PostParse</c> / <c>dng_negative::Parse</c>.
///
/// <para>Handles the full type variety that real DNG writers use:
/// <list type="bullet">
///   <item><c>BlackLevel</c> may be RATIONAL, SHORT, or LONG.</item>
///   <item><c>WhiteLevel</c> may be SHORT or LONG.</item>
///   <item><c>BlackLevelDeltaH</c> / <c>BlackLevelDeltaV</c> are SRATIONAL.</item>
///   <item><c>LinearizationTable</c> is SHORT.</item>
/// </list>
/// </para>
/// </summary>
public static class LinearizationReader
{
    /// <summary>
    /// Populate a new <see cref="LinearizationInfo"/> from <paramref name="ifd"/>.
    /// Returns a reasonable default (single-plane 0/1 black/white) when tags
    /// are absent or the pixel type is floating-point (already normalised).
    /// </summary>
    public static LinearizationInfo Read(DngStream stream, TiffIfd ifd, bool bigEndian, bool isFloat)
    {
        var info = new LinearizationInfo();

        // ── BlackLevelRepeatDim ──────────────────────────────────────────────
        if (ifd.Find(DngTagCode.BlackLevelRepeatDim) is { } repeatDimEntry)
        {
            var shorts = ReadShortArray(stream, repeatDimEntry, bigEndian);
            if (shorts.Length >= 2)
                info.BlackLevelRepeatDim = ((uint)shorts[0], (uint)shorts[1]);
        }

        // ── BlackLevel ───────────────────────────────────────────────────────
        if (ifd.Find(DngTagCode.BlackLevel) is { } blackEntry)
        {
            info.BlackLevel = ReadBlackLevel(stream, blackEntry, bigEndian, isFloat);
        }
        else
        {
            info.BlackLevel = [0.0];
        }

        // ── WhiteLevel ───────────────────────────────────────────────────────
        if (ifd.Find(DngTagCode.WhiteLevel) is { } whiteEntry)
        {
            info.WhiteLevel = ReadWhiteLevel(stream, whiteEntry, bigEndian, isFloat);
        }
        else
        {
            info.WhiteLevel = isFloat ? [1.0] : [65535.0];
        }

        // ── LinearizationTable ───────────────────────────────────────────────
        if (ifd.Find(DngTagCode.LinearizationTable) is { } lutEntry)
        {
            info.LinearizationTable = ReadShortArray(stream, lutEntry, bigEndian);
        }

        // ── BlackLevelDeltaH / BlackLevelDeltaV ─────────────────────────────
        if (ifd.Find(DngTagCode.BlackLevelDeltaH) is { } dh)
            info.BlackLevelDeltaH = ReadSRationalDoubles(stream, dh, bigEndian);

        if (ifd.Find(DngTagCode.BlackLevelDeltaV) is { } dv)
            info.BlackLevelDeltaV = ReadSRationalDoubles(stream, dv, bigEndian);

        return info;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static double[] ReadBlackLevel(
        DngStream stream, TiffIfdEntry entry, bool bigEndian, bool isFloat)
    {
        // For both integer and float images, read the actual tag value.
        // Float images can have non-zero black levels (e.g. as Rational 0/256 = 0.0,
        // but some formats use a small positive value). Default to 0.0 only on failure.
        return entry.Type switch
        {
            TiffDataType.Rational => ReadURationalDoubles(stream, entry, bigEndian),
            TiffDataType.Short    => ReadShortDoubles(stream, entry, bigEndian),
            TiffDataType.Long     => ReadLongDoubles(stream, entry, bigEndian),
            TiffDataType.Float    => ReadFloatDoubles(stream, entry, bigEndian),
            _                     => [0.0],
        };
    }

    private static double[] ReadWhiteLevel(
        DngStream stream, TiffIfdEntry entry, bool bigEndian, bool isFloat)
    {
        // Always read the actual tag value — do NOT hard-code 1.0 for float images.
        //
        // Float images store their meaningful maximum white level as an integer tag:
        //   iPhone ProRAW (Float16): WhiteLevel = 32768  → float 32768.0 is white
        //   PGTM2 sample (UInt16):   WhiteLevel = 65535  → integer 65535 is white
        //
        // Callers use 1.0 as the default only when the tag is absent (handled above).
        return entry.Type switch
        {
            TiffDataType.Short    => ReadShortDoubles(stream, entry, bigEndian),
            TiffDataType.Long     => ReadLongDoubles(stream, entry, bigEndian),
            TiffDataType.Rational => ReadURationalDoubles(stream, entry, bigEndian),
            _                     => isFloat ? [1.0] : [65535.0],
        };
    }

    private static double[] ReadURationalDoubles(DngStream stream, TiffIfdEntry entry, bool bigEndian)
    {
        var bytes = ReadAllBytes(stream, entry);
        int n = bytes.Length / 8;
        var result = new double[n];
        var s = bytes.Span;
        for (int i = 0; i < n; i++)
        {
            uint num = bigEndian
                ? BinaryPrimitives.ReadUInt32BigEndian(s[(i * 8)..])
                : BinaryPrimitives.ReadUInt32LittleEndian(s[(i * 8)..]);
            uint den = bigEndian
                ? BinaryPrimitives.ReadUInt32BigEndian(s[(i * 8 + 4)..])
                : BinaryPrimitives.ReadUInt32LittleEndian(s[(i * 8 + 4)..]);
            result[i] = den != 0 ? (double)num / den : 0.0;
        }
        return result;
    }

    private static double[] ReadSRationalDoubles(DngStream stream, TiffIfdEntry entry, bool bigEndian)
    {
        if (entry.Type != TiffDataType.SRational) return [];
        var bytes = ReadAllBytes(stream, entry);
        int n = bytes.Length / 8;
        var result = new double[n];
        var s = bytes.Span;
        for (int i = 0; i < n; i++)
        {
            int num = bigEndian
                ? BinaryPrimitives.ReadInt32BigEndian(s[(i * 8)..])
                : BinaryPrimitives.ReadInt32LittleEndian(s[(i * 8)..]);
            int den = bigEndian
                ? BinaryPrimitives.ReadInt32BigEndian(s[(i * 8 + 4)..])
                : BinaryPrimitives.ReadInt32LittleEndian(s[(i * 8 + 4)..]);
            result[i] = den != 0 ? (double)num / den : 0.0;
        }
        return result;
    }

    private static double[] ReadShortDoubles(DngStream stream, TiffIfdEntry entry, bool bigEndian)
    {
        var shorts = ReadShortArray(stream, entry, bigEndian);
        var result = new double[shorts.Length];
        for (int i = 0; i < shorts.Length; i++) result[i] = shorts[i];
        return result;
    }

    private static double[] ReadLongDoubles(DngStream stream, TiffIfdEntry entry, bool bigEndian)
    {
        var bytes = ReadAllBytes(stream, entry);
        int n = bytes.Length / 4;
        var result = new double[n];
        var s = bytes.Span;
        for (int i = 0; i < n; i++)
            result[i] = bigEndian
                ? BinaryPrimitives.ReadUInt32BigEndian(s[(i * 4)..])
                : BinaryPrimitives.ReadUInt32LittleEndian(s[(i * 4)..]);
        return result;
    }

    private static double[] ReadFloatDoubles(DngStream stream, TiffIfdEntry entry, bool bigEndian)
    {
        var bytes = ReadAllBytes(stream, entry);
        int n = bytes.Length / 4;
        var result = new double[n];
        var s = bytes.Span;
        for (int i = 0; i < n; i++)
            result[i] = bigEndian
                ? BinaryPrimitives.ReadSingleBigEndian(s[(i * 4)..])
                : BinaryPrimitives.ReadSingleLittleEndian(s[(i * 4)..]);
        return result;
    }

    internal static ushort[] ReadShortArray(DngStream stream, TiffIfdEntry entry, bool bigEndian)
    {
        if (entry.Type is not (TiffDataType.Short or TiffDataType.SShort)) return [];
        var bytes = ReadAllBytes(stream, entry);
        int n = bytes.Length / 2;
        var result = new ushort[n];
        var s = bytes.Span;
        for (int i = 0; i < n; i++)
            result[i] = bigEndian
                ? BinaryPrimitives.ReadUInt16BigEndian(s[(i * 2)..])
                : BinaryPrimitives.ReadUInt16LittleEndian(s[(i * 2)..]);
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
