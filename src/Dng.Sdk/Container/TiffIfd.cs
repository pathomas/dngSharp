using System.Collections.Frozen;
using Dng.Sdk.Errors;
using Dng.Sdk.IO;
using Dng.Sdk.Tiff;

namespace Dng.Sdk.Container;

/// <summary>
/// One parsed IFD: its file offset, its entries (keyed by tag), the next-IFD
/// link, and any SubIFDs it references.
/// </summary>
public sealed class TiffIfd
{
    public required long Offset { get; init; }
    public required IReadOnlyList<TiffIfdEntry> Entries { get; init; }
    public required long NextIfdOffset { get; init; }

    /// <summary>SubIFDs referenced by the <c>SubIFDs</c> tag (0x14A).</summary>
    public IReadOnlyList<TiffIfd> SubIfds { get; init; } = [];

    /// <summary>
    /// Eagerly-built tag→entry index. <see cref="FrozenDictionary{TKey,TValue}"/>
    /// is immutable after construction, so concurrent <see cref="Find"/>
    /// calls across threads are safe.
    /// </summary>
    private FrozenDictionary<DngTagCode, TiffIfdEntry>? _byTag;

    public TiffIfdEntry? Find(DngTagCode tag)
    {
        // Race-tolerant: BuildIndex is pure; if two threads both compute it,
        // they install structurally identical frozen instances.
        _byTag ??= BuildIndex();
        return _byTag.GetValueOrDefault(tag);
    }

    public NewSubFileType Classify(bool bigEndian)
    {
        var nsft = Find(DngTagCode.NewSubFileType);
        if (nsft is null) return NewSubFileType.MainImage; // C++ default
        return (NewSubFileType)nsft.GetScalarUInt(bigEndian);
    }

    private FrozenDictionary<DngTagCode, TiffIfdEntry> BuildIndex()
    {
        var d = new Dictionary<DngTagCode, TiffIfdEntry>(Entries.Count);
        foreach (var e in Entries) d[e.Tag] = e;  // last-wins
        return d.ToFrozenDictionary();
    }

    public static TiffIfd Read(DngStream stream, bool bigTiff, long offset)
    {
        if (offset <= 0 || offset > stream.Length)
            throw new DngException(DngError.BadFormat, $"IFD offset out of range: {offset}");

        stream.Position = offset;
        ulong count = bigTiff ? stream.ReadUInt64() : stream.ReadUInt16();
        if (count > DngLimits.MaxColorPlanes * 4096) // generous upper bound; spec doesn't fix it
            throw new DngException(DngError.BadFormat, $"IFD claims {count} entries — likely corrupt");

        int entrySize = bigTiff ? 20 : 12;
        int valueSlot = bigTiff ? 8 : 4;

        var entries = new List<TiffIfdEntry>((int)System.Math.Min(count, 1 << 20));
        // Hoist the value-or-offset slot buffer out of the loop (CA2014).
        Span<byte> slot = stackalloc byte[8];
        for (ulong i = 0; i < count; i++)
        {
            var tag = (DngTagCode)stream.ReadUInt16();
            var type = (TiffDataType)stream.ReadUInt16();
            ulong elemCount = bigTiff ? stream.ReadUInt64() : stream.ReadUInt32();

            uint elemSize = type.Size();
            // Guard against absurd sizes that would overflow on multiply.
            // ulong.MaxValue / elemSize is the largest count that produces a
            // representable payload size; anything bigger is malformed.
            if (elemSize > 0 && elemCount > ulong.MaxValue / elemSize)
                throw new DngException(DngError.BadFormat,
                    $"Tag 0x{(uint)tag:X4}: count {elemCount} overflows for type {type}");

            ulong payload = (ulong)elemSize * elemCount;

            ReadOnlyMemory<byte> inline = ReadOnlyMemory<byte>.Empty;
            long valueOffset = 0;

            // Read the value-or-offset slot into the hoisted buffer.
            var slotUsed = slot[..valueSlot];
            stream.ReadExactly(slotUsed);

            if (payload <= (ulong)valueSlot)
            {
                inline = slotUsed[..(int)payload].ToArray();
            }
            else
            {
                // The 4- or 8-byte slot encodes an absolute file offset using
                // the stream's byte order (the raw bytes are already on disk
                // in that order; we just need to interpret them).
                valueOffset = bigTiff
                    ? (long)(stream.BigEndian
                        ? System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(slotUsed)
                        : System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(slotUsed))
                    : (stream.BigEndian
                        ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(slotUsed)
                        : System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(slotUsed));
            }

            entries.Add(new TiffIfdEntry
            {
                Tag = tag,
                Type = type,
                Count = elemCount,
                PayloadSize = payload,
                InlineValue = inline,
                ValueOffset = valueOffset,
            });
        }

        long nextOffset = bigTiff ? (long)stream.ReadUInt64() : stream.ReadUInt32();

        return new TiffIfd { Offset = offset, Entries = entries, NextIfdOffset = nextOffset };
    }
}
