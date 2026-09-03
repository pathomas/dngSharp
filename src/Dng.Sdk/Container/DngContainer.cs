using Dng.Sdk.Errors;
using Dng.Sdk.IO;
using Dng.Sdk.Tiff;

namespace Dng.Sdk.Container;

/// <summary>
/// Minimal top-level TIFF/DNG container: header + chained top-level IFDs +
/// SubIFDs. Mirrors the pre-PostParse state of <c>dng_info</c>.
///
/// <para>This intentionally does <b>not</b> do EXIF, XMP, MakerNote, or
/// per-IFD interpretation — those come in later phases. What it gives you
/// is enough structure to:
///   <list type="bullet">
///     <item>walk every IFD in the file,</item>
///     <item>look up tags by code,</item>
///     <item>classify IFDs by <c>NewSubFileType</c>,</item>
///     <item>locate main / preview / mask / depth / enhanced / semantic IFDs.</item>
///   </list>
/// </para>
/// </summary>
public sealed class DngContainer
{
    public required TiffHeader Header { get; init; }

    /// <summary>
    /// Top-level chain: IFD 0 first, then each IFD reached by following the
    /// previous IFD's <c>NextIfdOffset</c>. Each entry's
    /// <see cref="TiffIfd.SubIfds"/> contains the SubIFDs referenced by tag
    /// 0x14A.
    /// </summary>
    public required IReadOnlyList<TiffIfd> TopLevelIfds { get; init; }

    /// <summary>
    /// Flattened (top-level + sub-IFD) list. Index into this list is what
    /// <see cref="MainIndex"/> et al. refer to.
    /// </summary>
    public required IReadOnlyList<TiffIfd> AllIfds { get; init; }

    public int MainIndex { get; init; } = -1;
    public int MaskIndex { get; init; } = -1;
    public int DepthIndex { get; init; } = -1;
    public int EnhancedIndex { get; init; } = -1;
    public int GainMapIndex { get; init; } = -1;
    public IReadOnlyList<int> SemanticMaskIndices { get; init; } = [];
    public IReadOnlyList<int> PreviewIndices { get; init; } = [];

    public static DngContainer Parse(DngStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var header = TiffHeader.Parse(stream);

        var topLevel = new List<TiffIfd>();
        var visited = new HashSet<long>();
        long nextOffset = header.FirstIfdOffset;
        int hops = 0;

        while (nextOffset != 0)
        {
            if (!visited.Add(nextOffset))
                throw new DngException(DngError.BadFormat, $"IFD chain loop at offset {nextOffset}");
            if (++hops > DngLimits.MaxChainedIFDs)
                throw new DngException(DngError.BadFormat, $"Too many chained IFDs (> {DngLimits.MaxChainedIFDs})");

            var ifd = TiffIfd.Read(stream, header.BigTiff, nextOffset);

            // SubIFDs (tag 0x14A) — array of IFD offsets to nested IFDs.
            var subOffsets = ReadSubIfdOffsets(stream, header, ifd);
            if (subOffsets.Count > 0)
            {
                var subIfds = new List<TiffIfd>(subOffsets.Count);
                foreach (var off in subOffsets)
                {
                    if (off <= 0) continue;
                    if (!visited.Add(off))
                        throw new DngException(DngError.BadFormat, $"SubIFD cycle at offset {off}");
                    subIfds.Add(TiffIfd.Read(stream, header.BigTiff, off));
                }
                // Replace the IFD with one that has SubIfds populated.
                ifd = new TiffIfd
                {
                    Offset = ifd.Offset,
                    Entries = ifd.Entries,
                    NextIfdOffset = ifd.NextIfdOffset,
                    SubIfds = subIfds,
                };
            }

            topLevel.Add(ifd);
            nextOffset = ifd.NextIfdOffset;
        }

        // Flatten and classify.
        var all = new List<TiffIfd>();
        foreach (var ifd in topLevel)
        {
            all.Add(ifd);
            foreach (var sub in ifd.SubIfds) all.Add(sub);
        }

        int main = -1, mask = -1, depth = -1, enhanced = -1, gainMap = -1;
        var semantic = new List<int>();
        var previews = new List<int>();

        for (int i = 0; i < all.Count; i++)
        {
            switch (all[i].Classify(header.BigEndian))
            {
                case NewSubFileType.MainImage:
                    if (main < 0) main = i;
                    break;
                case NewSubFileType.PreviewImage:
                case NewSubFileType.AltPreviewImage:
                case NewSubFileType.PreviewMask:
                case NewSubFileType.PreviewDepthMap:
                case NewSubFileType.PreviewGainMap:
                case NewSubFileType.PreviewSemanticMask:
                    previews.Add(i);
                    break;
                case NewSubFileType.TransparencyMask:
                    if (mask < 0) mask = i;
                    break;
                case NewSubFileType.DepthMap:
                    if (depth < 0) depth = i;
                    break;
                case NewSubFileType.EnhancedImage:
                    if (enhanced < 0) enhanced = i;
                    break;
                case NewSubFileType.GainMap:
                    if (gainMap < 0) gainMap = i;
                    break;
                case NewSubFileType.SemanticMask:
                    semantic.Add(i);
                    break;
            }
        }

        return new DngContainer
        {
            Header = header,
            TopLevelIfds = topLevel,
            AllIfds = all,
            MainIndex = main,
            MaskIndex = mask,
            DepthIndex = depth,
            EnhancedIndex = enhanced,
            GainMapIndex = gainMap,
            SemanticMaskIndices = semantic,
            PreviewIndices = previews,
        };
    }

    private static List<long> ReadSubIfdOffsets(DngStream stream, TiffHeader header, TiffIfd ifd)
    {
        var entry = ifd.Find(DngTagCode.SubIFDs);
        if (entry is null) return [];

        // Cap the SubIFD count BEFORE any allocation/IO. A crafted file can
        // claim millions of SubIFDs; without this guard the List<>(capacity)
        // below either OOMs or throws ArgumentOutOfRangeException (when the
        // cast to int wraps negative).
        if (entry.Count > DngLimits.MaxSubIFDs)
            throw new DngException(DngError.BadFormat,
                $"Too many SubIFDs ({entry.Count} > {DngLimits.MaxSubIFDs})");

        var result = new List<long>((int)entry.Count);
        if (entry.IsInline)
        {
            var s = entry.InlineValue.Span;
            int slotSize = entry.Type == TiffDataType.Long8 || entry.Type == TiffDataType.Ifd8 ? 8 : 4;
            for (uint i = 0; i < entry.Count; i++)
            {
                long off = slotSize == 8
                    ? (long)(header.BigEndian
                        ? System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(s[(int)(i * 8)..])
                        : System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(s[(int)(i * 8)..]))
                    : (header.BigEndian
                        ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(s[(int)(i * 4)..])
                        : System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(s[(int)(i * 4)..]));
                result.Add(off);
            }
        }
        else
        {
            long savedPos = stream.Position;
            stream.Position = entry.ValueOffset;
            for (uint i = 0; i < entry.Count; i++)
            {
                long off = entry.Type == TiffDataType.Long8 || entry.Type == TiffDataType.Ifd8
                    ? (long)stream.ReadUInt64()
                    : stream.ReadUInt32();
                result.Add(off);
            }
            stream.Position = savedPos;
        }

        return result;
    }
}
