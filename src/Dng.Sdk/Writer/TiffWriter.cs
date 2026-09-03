using System.Buffers.Binary;
using System.Text;
using Dng.Sdk.Container;
using Dng.Sdk.Errors;
using Dng.Sdk.IO;
using Dng.Sdk.Primitives;
using Dng.Sdk.Tiff;

namespace Dng.Sdk.Writer;

/// <summary>
/// One entry to write to an IFD: tag + type + count + payload bytes. Payload
/// bytes are the raw on-disk representation in the host's chosen endianness;
/// pre-compose them with <see cref="TagBuilder"/> for typed convenience.
/// </summary>
public sealed class TiffEntryToWrite
{
    public required DngTagCode Tag { get; init; }
    public required TiffDataType Type { get; init; }
    public required uint Count { get; init; }
    public required ReadOnlyMemory<byte> Payload { get; init; }

    /// <summary>
    /// Optional callback invoked by the writer when this entry refers to a
    /// large blob (strip/tile data) whose absolute file offset isn't known
    /// until the IFD is laid out. The callback is given the file offset of
    /// the value-or-offset slot (4 bytes for TIFF, 8 for BigTIFF) so the
    /// writer can patch it after the strip data is positioned.
    /// </summary>
    public Action<long>? OffsetSlotCallback { get; init; }

    /// <summary>
    /// When set, this entry is a SubIFDs-style link (e.g. tag 0x14A): rather
    /// than writing <see cref="Payload"/> verbatim, the writer lays out each
    /// referenced IFD immediately after this IFD's own out-of-line entries,
    /// then writes an array of the resulting absolute offsets (one per
    /// sub-IFD, one <c>uint32</c> slot each — 32-bit TIFF only) as this
    /// entry's payload. <see cref="Payload"/> and <see cref="Count"/> are
    /// ignored when this is set; the effective count is
    /// <c>SubIfds.Count</c>.
    /// </summary>
    public IReadOnlyList<TiffIfdToWrite>? SubIfds { get; init; }
}

/// <summary>
/// One IFD to write: a sorted-by-tag set of entries + an optional next-IFD link.
/// </summary>
public sealed class TiffIfdToWrite
{
    public List<TiffEntryToWrite> Entries { get; } = [];

    /// <summary>
    /// Strip / tile data blobs that follow the IFD body. Each gets aligned to
    /// a 2-byte boundary (TIFF requirement) and patched into the matching
    /// entry's value-or-offset slot via <see cref="TiffEntryToWrite.OffsetSlotCallback"/>.
    /// </summary>
    public List<DeferredBlob> Blobs { get; } = [];
}

/// <summary>A large blob whose absolute offset is patched into one or more IFD entries after layout.</summary>
public sealed class DeferredBlob
{
    public required ReadOnlyMemory<byte> Bytes { get; init; }
    public List<Action<long>> OffsetWriters { get; } = [];
}

/// <summary>
/// Helpers for building entry payloads. Returns the bytes with the correct
/// endian + element packing for the requested type.
/// </summary>
public static class TagBuilder
{
    public static TiffEntryToWrite UInt16(DngTagCode tag, ushort value, bool bigEndian) =>
        new()
        {
            Tag = tag,
            Type = TiffDataType.Short,
            Count = 1,
            Payload = EncodeUInt16([value], bigEndian),
        };

    public static TiffEntryToWrite UInt16Array(DngTagCode tag, ReadOnlySpan<ushort> values, bool bigEndian) =>
        new()
        {
            Tag = tag,
            Type = TiffDataType.Short,
            Count = (uint)values.Length,
            Payload = EncodeUInt16(values, bigEndian),
        };

    public static TiffEntryToWrite UInt32(DngTagCode tag, uint value, bool bigEndian) =>
        new()
        {
            Tag = tag,
            Type = TiffDataType.Long,
            Count = 1,
            Payload = EncodeUInt32([value], bigEndian),
        };

    public static TiffEntryToWrite UInt32Array(DngTagCode tag, ReadOnlySpan<uint> values, bool bigEndian) =>
        new()
        {
            Tag = tag,
            Type = TiffDataType.Long,
            Count = (uint)values.Length,
            Payload = EncodeUInt32(values, bigEndian),
        };

    public static TiffEntryToWrite Bytes(DngTagCode tag, ReadOnlySpan<byte> bytes) =>
        new()
        {
            Tag = tag,
            Type = TiffDataType.Byte,
            Count = (uint)bytes.Length,
            Payload = bytes.ToArray(),
        };

    public static TiffEntryToWrite Ascii(DngTagCode tag, string s)
    {
        var bytes = Encoding.ASCII.GetBytes(s + "\0");  // NUL-terminated per TIFF
        return new()
        {
            Tag = tag,
            Type = TiffDataType.Ascii,
            Count = (uint)bytes.Length,
            Payload = bytes,
        };
    }

    public static TiffEntryToWrite URational(DngTagCode tag, DngURational value, bool bigEndian)
    {
        var bytes = new byte[8];
        if (bigEndian)
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes, value.N);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), value.D);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value.N);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), value.D);
        }
        return new()
        {
            Tag = tag,
            Type = TiffDataType.Rational,
            Count = 1,
            Payload = bytes,
        };
    }

    private static byte[] EncodeUInt16(ReadOnlySpan<ushort> values, bool bigEndian)
    {
        var bytes = new byte[values.Length * 2];
        for (int i = 0; i < values.Length; i++)
        {
            if (bigEndian) BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(i * 2), values[i]);
            else BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), values[i]);
        }
        return bytes;
    }

    private static byte[] EncodeUInt32(ReadOnlySpan<uint> values, bool bigEndian)
    {
        var bytes = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
        {
            if (bigEndian) BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(i * 4), values[i]);
            else BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), values[i]);
        }
        return bytes;
    }
}

/// <summary>
/// Lays out and writes a sequence of IFDs as a valid TIFF / DNG file.
/// Mirrors the framing logic of <c>dng_image_writer::WriteTIFF</c>.
///
/// <para><b>Layout strategy:</b>
/// <list type="number">
///   <item>Write the 8-byte TIFF header (II/MM + magic + first-IFD offset
///         placeholder).</item>
///   <item>For each IFD: estimate inline-vs-offset slot decisions, reserve
///         the IFD body, queue out-of-line payloads to be written after.</item>
///   <item>After all IFDs, write the deferred payloads (strip data, large
///         entry payloads) and patch their absolute offsets into the
///         reserved IFD slots.</item>
///   <item>Patch the first-IFD offset at file offset 4.</item>
/// </list>
/// </para>
///
/// <para>BigTIFF (magic 43) is rejected at construction time today — the
/// writer only emits 32-bit TIFF. Per spec, BigTIFF is needed when the
/// output would exceed 4 GiB; <see cref="Write"/> validates this and throws
/// <see cref="DngError.ImageTooBigDng"/> if the total payload would
/// overflow.</para>
/// </summary>
public sealed class TiffWriter
{
    public bool BigEndian { get; }

    public TiffWriter(bool bigEndian = false)
    {
        BigEndian = bigEndian;
    }

    /// <summary>
    /// Write <paramref name="ifds"/> as a complete TIFF file into
    /// <paramref name="stream"/>. <paramref name="stream"/> is positioned at
    /// 0 on entry and flushed (but not disposed) on exit.
    /// </summary>
    public void Write(DngStream stream, IReadOnlyList<TiffIfdToWrite> ifds)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(ifds);
        if (ifds.Count == 0) DngThrow.ProgramError("TiffWriter: at least one IFD required");

        stream.Position = 0;
        stream.SetBigEndian(BigEndian);

        // --- Header (8 bytes) ---
        // BOM
        stream.WriteUInt8((byte)(BigEndian ? 'M' : 'I'));
        stream.WriteUInt8((byte)(BigEndian ? 'M' : 'I'));
        // Magic (42 — 32-bit TIFF). Stay in the host endian.
        stream.WriteUInt16(TiffHeader.MagicTiff);
        // First-IFD offset — placeholder, patched after layout.
        long firstIfdOffsetSlot = stream.Position;
        stream.WriteUInt32(0);

        // --- IFD bodies ---
        var ifdStartOffsets = new long[ifds.Count];
        var nextIfdOffsetSlots = new long[ifds.Count];
        // All IFDs actually laid out (top-level + any nested SubIFDs
        // discovered along the way) — their Blobs (strip/tile data) still
        // need to be written in the final "Deferred blobs" pass below.
        var allLaidOutIfds = new List<TiffIfdToWrite>(ifds);
        for (int i = 0; i < ifds.Count; i++)
        {
            Align2(stream);
            ifdStartOffsets[i] = stream.Position;
            WriteIfdBody(stream, ifds[i], out long nextOffsetSlot, allLaidOutIfds);
            nextIfdOffsetSlots[i] = nextOffsetSlot;
        }

        // --- Patch chain (each IFD's next-pointer → the next IFD's start, or 0) ---
        for (int i = 0; i < ifds.Count; i++)
        {
            long next = i + 1 < ifds.Count ? ifdStartOffsets[i + 1] : 0;
            PatchUInt32At(stream, nextIfdOffsetSlots[i], (uint)next);
        }

        // --- Deferred blobs (strip data, big entry payloads) ---
        foreach (var ifd in allLaidOutIfds)
            foreach (var blob in ifd.Blobs)
            {
                Align2(stream);
                long here = stream.Position;
                if (here > uint.MaxValue)
                    throw new DngException(DngError.ImageTooBigDng,
                        $"TIFF (32-bit) write would exceed 4 GiB at blob offset {here}");
                stream.Write(blob.Bytes.Span);
                foreach (var writeOffset in blob.OffsetWriters)
                    writeOffset(here);
            }

        // --- Patch first-IFD offset ---
        PatchUInt32At(stream, firstIfdOffsetSlot, (uint)ifdStartOffsets[0]);

        stream.Position = stream.Length;  // leave cursor at EOF for callers
        stream.Flush();
    }

    private void WriteIfdBody(DngStream stream, TiffIfdToWrite ifd, out long nextOffsetSlot, List<TiffIfdToWrite> allLaidOutIfds)
    {
        // Sort entries by tag — TIFF spec mandates ascending order.
        var sorted = ifd.Entries.OrderBy(e => (uint)e.Tag).ToList();

        // Entry count (2 bytes for TIFF).
        stream.WriteUInt16((ushort)sorted.Count);

        // Pass 1: write each entry's (tag, type, count, slot). For out-of-line
        // payloads, queue them as deferred blobs whose offsets get patched
        // later via DeferredBlob.OffsetWriters. SubIFDs-style entries get
        // their own queue since their payload (an offset array) isn't known
        // until the referenced IFDs are laid out.
        var oversizeEntries = new List<(TiffEntryToWrite Entry, long SlotPos)>();
        var subIfdEntries = new List<(TiffEntryToWrite Entry, long SlotPos)>();
        foreach (var e in sorted)
        {
            stream.WriteUInt16((ushort)e.Tag);
            stream.WriteUInt16((ushort)e.Type);

            if (e.SubIfds is { } subs)
            {
                stream.WriteUInt32((uint)subs.Count);
                long subSlotPos = stream.Position;
                stream.WriteUInt32(0); // placeholder; patched once sub-IFDs are laid out
                subIfdEntries.Add((e, subSlotPos));
                continue;
            }

            stream.WriteUInt32(e.Count);

            long slotPos = stream.Position;
            if (e.Payload.Length <= 4)
            {
                // Inline: write payload bytes verbatim, then zero-pad to 4.
                stream.Write(e.Payload.Span);
                for (int pad = e.Payload.Length; pad < 4; pad++) stream.WriteUInt8(0);
            }
            else
            {
                // Out-of-line: write a placeholder offset. Queue as a blob.
                stream.WriteUInt32(0);
                oversizeEntries.Add((e, slotPos));
            }

            // Strip/tile-style entries (e.g. StripOffsets) want a callback so
            // the writer can patch them when the strip data lands later.
            e.OffsetSlotCallback?.Invoke(slotPos);
        }

        // Next-IFD-offset slot is right after the entry array.
        nextOffsetSlot = stream.Position;
        stream.WriteUInt32(0);  // placeholder; patched outside

        // Pass 2: emit each out-of-line entry's payload immediately after the
        // IFD body (TIFF spec doesn't require this ordering, but it improves
        // locality and matches what Adobe's writer does for non-strip data).
        foreach (var (e, slotPos) in oversizeEntries)
        {
            Align2(stream);
            long payloadPos = stream.Position;
            if (payloadPos > uint.MaxValue)
                throw new DngException(DngError.ImageTooBigDng,
                    $"TIFF (32-bit) write would exceed 4 GiB at entry payload");
            stream.Write(e.Payload.Span);
            PatchUInt32At(stream, slotPos, (uint)payloadPos);
        }

        // Pass 3: lay out each SubIFDs-style entry's referenced IFDs (each is
        // an independent, unlinked IFD — its next-IFD-offset is always 0),
        // then write the resulting offset array as this entry's payload.
        foreach (var (e, slotPos) in subIfdEntries)
        {
            var subs = e.SubIfds!;
            var subOffsets = new uint[subs.Count];
            for (int i = 0; i < subs.Count; i++)
            {
                Align2(stream);
                long subStart = stream.Position;
                if (subStart > uint.MaxValue)
                    throw new DngException(DngError.ImageTooBigDng,
                        $"TIFF (32-bit) write would exceed 4 GiB at SubIFD offset");
                subOffsets[i] = (uint)subStart;
                allLaidOutIfds.Add(subs[i]);
                WriteIfdBody(stream, subs[i], out long subNextSlot, allLaidOutIfds);
                PatchUInt32At(stream, subNextSlot, 0); // SubIFDs never chain
            }

            if (subs.Count == 1)
            {
                // Fits inline — the slot IS the offset (no separate blob).
                PatchUInt32At(stream, slotPos, subOffsets[0]);
            }
            else
            {
                Align2(stream);
                long arrayPos = stream.Position;
                if (arrayPos > uint.MaxValue)
                    throw new DngException(DngError.ImageTooBigDng,
                        $"TIFF (32-bit) write would exceed 4 GiB at SubIFD offset array");
                foreach (var off in subOffsets) stream.WriteUInt32(off);
                PatchUInt32At(stream, slotPos, (uint)arrayPos);
            }
        }
    }

    private static void Align2(DngStream stream)
    {
        if ((stream.Position & 1) != 0) stream.WriteUInt8(0);
    }

    private static void PatchUInt32At(DngStream stream, long position, uint value)
    {
        long saved = stream.Position;
        stream.Position = position;
        stream.WriteUInt32(value);
        stream.Position = saved;
    }
}
