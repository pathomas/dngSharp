using System.Buffers.Binary;
using DngSharp.Dng.Sdk.Container;
using DngSharp.Dng.Sdk.IO;
using DngSharp.Dng.Sdk.Tiff;

namespace DngSharp.Dng.Sdk.Imaging.Raw;

/// <summary>
/// Reads CFA mosaic metadata from a parsed DNG main IFD into a
/// <see cref="MosaicInfo"/>. Mirrors tag-reading in <c>dng_mosaic_info::Parse</c>.
///
/// <para>Returns <see langword="null"/> when no <c>CFAPattern</c> tag is
/// present (non-CFA images: LinearRaw, RGB, etc.).</para>
/// </summary>
public static class MosaicInfoReader
{
    /// <summary>
    /// Read mosaic tags from <paramref name="ifd"/>. Returns <see langword="null"/>
    /// when <c>CFAPattern</c> is absent (indicating a non-CFA image).
    /// </summary>
    public static MosaicInfo? Read(DngStream stream, TiffIfd ifd, bool bigEndian)
    {
        if (ifd.Find(DngTagCode.CFAPattern) is not { } cfaEntry) return null;

        var info = new MosaicInfo();

        // ── CFARepeatPatternDim ──────────────────────────────────────────────
        // Default: 2×2 Bayer
        if (ifd.Find(DngTagCode.CFARepeatPatternDim) is { } dimEntry)
        {
            var shorts = LinearizationReader.ReadShortArray(stream, dimEntry, bigEndian);
            if (shorts.Length >= 2)
                info.Pattern = ((uint)shorts[0], (uint)shorts[1]);
        }
        else
        {
            info.Pattern = (2, 2);
        }

        // ── CFAPattern ───────────────────────────────────────────────────────
        // Byte array, length = PatternRows × PatternCols.
        var cfaBytes = ReadByteArray(stream, cfaEntry);
        info.CfaPlaneColor = cfaBytes;

        // ── BayerGreenSplit ──────────────────────────────────────────────────
        if (ifd.Find(DngTagCode.BayerGreenSplit) is { } bgsEntry)
        {
            try { info.BayerGreenSplit = bgsEntry.GetScalarUInt(bigEndian); }
            catch { /* optional tag; non-scalar form is unusual */ }
        }

        // ── RowInterleaveFactor ──────────────────────────────────────────────
        if (ifd.Find(DngTagCode.RowInterleaveFactor) is { } rifEntry)
        {
            try { info.RowInterleaveFactor = rifEntry.GetScalarUInt(bigEndian); }
            catch { }
        }

        // ── ColumnInterleaveFactor ───────────────────────────────────────────
        if (ifd.Find(DngTagCode.ColumnInterleaveFactor) is { } cifEntry)
        {
            try { info.ColumnInterleaveFactor = cifEntry.GetScalarUInt(bigEndian); }
            catch { }
        }

        return info;
    }

    private static byte[] ReadByteArray(DngStream stream, TiffIfdEntry entry)
    {
        if (entry.IsInline)
        {
            int len = (int)System.Math.Min(entry.PayloadSize, (ulong)entry.InlineValue.Length);
            return entry.InlineValue.Span[..len].ToArray();
        }
        if (entry.PayloadSize > int.MaxValue) return [];
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
