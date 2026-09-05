using System.Buffers.Binary;
using System.Text;
using DngSharp.Dng.Sdk.Errors;
using DngSharp.Dng.Sdk.IO;
using DngSharp.Dng.Sdk.Tiff;

namespace DngSharp.Dng.Sdk.Container;

/// <summary>
/// Format a parsed <see cref="TiffIfdEntry"/>'s value for human inspection.
/// Mirrors what <c>dng_validate -v</c> emits next to each tag.
///
/// <para>Out-of-line payloads are read on demand via <paramref name="stream"/>.
/// To keep diagnostic output bounded, arrays longer than
/// <c>maxElements</c> are truncated with an "…" suffix.</para>
/// </summary>
public static class TagValueFormatter
{
    public static string Format(TiffIfdEntry entry, DngStream stream, bool bigEndian, int maxElements = 16)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(stream);

        var payload = ReadAllBytes(entry, stream);

        return entry.Type switch
        {
            TiffDataType.Ascii => FormatAscii(payload.Span),
            TiffDataType.Byte or TiffDataType.SByte or TiffDataType.Undefined =>
                FormatByteArray(payload.Span, entry.Type, maxElements),
            TiffDataType.Short => FormatShortArray(payload.Span, bigEndian, signed: false, maxElements),
            TiffDataType.SShort => FormatShortArray(payload.Span, bigEndian, signed: true, maxElements),
            TiffDataType.Long => FormatLongArray(payload.Span, bigEndian, signed: false, maxElements),
            TiffDataType.SLong => FormatLongArray(payload.Span, bigEndian, signed: true, maxElements),
            TiffDataType.Rational => FormatRationalArray(payload.Span, bigEndian, signed: false, maxElements),
            TiffDataType.SRational => FormatRationalArray(payload.Span, bigEndian, signed: true, maxElements),
            TiffDataType.Float => FormatFloatArray(payload.Span, bigEndian, maxElements),
            TiffDataType.Double => FormatDoubleArray(payload.Span, bigEndian, maxElements),
            TiffDataType.Ifd => $"IFD@{(payload.Length >= 4 ? ReadU32(payload.Span, bigEndian) : 0)}",
            _ => $"<{entry.Type} count={entry.Count} bytes={payload.Length}>",
        };
    }

    private static ReadOnlyMemory<byte> ReadAllBytes(TiffIfdEntry entry, DngStream stream)
    {
        if (entry.IsInline)
        {
            int len = (int)System.Math.Min(entry.PayloadSize, (ulong)entry.InlineValue.Length);
            return entry.InlineValue[..len];
        }

        if (entry.PayloadSize > int.MaxValue) return ReadOnlyMemory<byte>.Empty;

        var buf = new byte[(int)entry.PayloadSize];
        long savedPos = stream.Position;
        try
        {
            stream.Position = entry.ValueOffset;
            stream.ReadExactly(buf);
        }
        catch (DngException)
        {
            return ReadOnlyMemory<byte>.Empty;
        }
        finally
        {
            stream.Position = savedPos;
        }
        return buf;
    }

    private static string FormatAscii(ReadOnlySpan<byte> bytes)
    {
        int len = bytes.Length;
        while (len > 0 && bytes[len - 1] == 0) len--;  // strip trailing NULs
        var s = Encoding.UTF8.GetString(bytes[..len]);
        // Escape control characters for human readability.
        var sb = new StringBuilder("\"");
        foreach (var c in s)
        {
            if (c is '\\' or '"') { sb.Append('\\').Append(c); }
            else if (c < 0x20 || c == 0x7F) sb.AppendFormat("\\x{0:X2}", (int)c);
            else sb.Append(c);
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static string FormatByteArray(ReadOnlySpan<byte> bytes, TiffDataType type, int maxElements)
    {
        if (bytes.Length <= 4 || type == TiffDataType.SByte)
        {
            var parts = new List<string>(bytes.Length);
            int shown = System.Math.Min(bytes.Length, maxElements);
            for (int i = 0; i < shown; i++)
                parts.Add(type == TiffDataType.SByte ? ((sbyte)bytes[i]).ToString() : bytes[i].ToString());
            string body = string.Join(", ", parts);
            if (bytes.Length > shown) body += $", … (+{bytes.Length - shown} more)";
            return $"[{body}]";
        }
        // Longer byte arrays — hex dump first few bytes.
        int hexLen = System.Math.Min(bytes.Length, maxElements);
        var hex = new StringBuilder("0x");
        for (int i = 0; i < hexLen; i++) hex.AppendFormat("{0:X2}", bytes[i]);
        if (bytes.Length > hexLen) hex.Append('…');
        return $"{hex} ({bytes.Length} bytes)";
    }

    private static string FormatShortArray(ReadOnlySpan<byte> bytes, bool bigEndian, bool signed, int maxElements)
    {
        int count = bytes.Length / 2;
        var parts = new List<string>(System.Math.Min(count, maxElements));
        int shown = System.Math.Min(count, maxElements);
        for (int i = 0; i < shown; i++)
        {
            ushort u = bigEndian
                ? BinaryPrimitives.ReadUInt16BigEndian(bytes[(i * 2)..])
                : BinaryPrimitives.ReadUInt16LittleEndian(bytes[(i * 2)..]);
            parts.Add(signed ? ((short)u).ToString() : u.ToString());
        }
        string body = string.Join(", ", parts);
        if (count > shown) body += $", … (+{count - shown} more)";
        return $"[{body}]";
    }

    private static string FormatLongArray(ReadOnlySpan<byte> bytes, bool bigEndian, bool signed, int maxElements)
    {
        int count = bytes.Length / 4;
        var parts = new List<string>(System.Math.Min(count, maxElements));
        int shown = System.Math.Min(count, maxElements);
        for (int i = 0; i < shown; i++)
        {
            uint u = ReadU32(bytes[(i * 4)..], bigEndian);
            parts.Add(signed ? ((int)u).ToString() : u.ToString());
        }
        string body = string.Join(", ", parts);
        if (count > shown) body += $", … (+{count - shown} more)";
        return $"[{body}]";
    }

    private static string FormatRationalArray(ReadOnlySpan<byte> bytes, bool bigEndian, bool signed, int maxElements)
    {
        int count = bytes.Length / 8;
        var parts = new List<string>(System.Math.Min(count, maxElements));
        int shown = System.Math.Min(count, maxElements);
        for (int i = 0; i < shown; i++)
        {
            uint n = ReadU32(bytes[(i * 8)..], bigEndian);
            uint d = ReadU32(bytes[(i * 8 + 4)..], bigEndian);
            if (signed)
            {
                int sn = (int)n, sd = (int)d;
                double val = sd != 0 ? (double)sn / sd : 0;
                parts.Add($"{sn}/{sd} ({val:G4})");
            }
            else
            {
                double val = d != 0 ? (double)n / d : 0;
                parts.Add($"{n}/{d} ({val:G4})");
            }
        }
        string body = string.Join(", ", parts);
        if (count > shown) body += $", … (+{count - shown} more)";
        return $"[{body}]";
    }

    private static string FormatFloatArray(ReadOnlySpan<byte> bytes, bool bigEndian, int maxElements)
    {
        int count = bytes.Length / 4;
        var parts = new List<string>(System.Math.Min(count, maxElements));
        int shown = System.Math.Min(count, maxElements);
        for (int i = 0; i < shown; i++)
        {
            float f = bigEndian
                ? BinaryPrimitives.ReadSingleBigEndian(bytes[(i * 4)..])
                : BinaryPrimitives.ReadSingleLittleEndian(bytes[(i * 4)..]);
            parts.Add(f.ToString("G6", System.Globalization.CultureInfo.InvariantCulture));
        }
        string body = string.Join(", ", parts);
        if (count > shown) body += $", … (+{count - shown} more)";
        return $"[{body}]";
    }

    private static string FormatDoubleArray(ReadOnlySpan<byte> bytes, bool bigEndian, int maxElements)
    {
        int count = bytes.Length / 8;
        var parts = new List<string>(System.Math.Min(count, maxElements));
        int shown = System.Math.Min(count, maxElements);
        for (int i = 0; i < shown; i++)
        {
            double v = bigEndian
                ? BinaryPrimitives.ReadDoubleBigEndian(bytes[(i * 8)..])
                : BinaryPrimitives.ReadDoubleLittleEndian(bytes[(i * 8)..]);
            parts.Add(v.ToString("G6", System.Globalization.CultureInfo.InvariantCulture));
        }
        string body = string.Join(", ", parts);
        if (count > shown) body += $", … (+{count - shown} more)";
        return $"[{body}]";
    }

    private static uint ReadU32(ReadOnlySpan<byte> bytes, bool bigEndian) =>
        bigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(bytes)
            : BinaryPrimitives.ReadUInt32LittleEndian(bytes);
}
