using System.Buffers.Binary;
using System.Runtime.InteropServices;
using DngSharp.Dng.Sdk.Errors;
using DngSharp.Dng.Sdk.Pixels;

namespace DngSharp.Dng.Sdk.Imaging.Opcodes;

/// <summary>
/// Decodes and applies the <c>MapTable</c> opcode
/// (<see cref="OpcodeId.MapTable"/>, id 7). Mirrors
/// <c>dng_opcode_MapTable</c> in <c>dng_misc_opcodes.cpp</c>: applies a
/// 16-bit-indexed lookup table to every sample in the opcode's area (one
/// evaluation per <c>RowPitch</c>/<c>ColPitch</c>-th row/column).
///
/// <para>Wire format (all big-endian; no leading self-describing
/// byte-count field, since <c>DngOpcodeList</c>'s generic <c>bodySize</c>
/// framing already accounts for it):
/// <code>
///   DngAreaSpec areaSpec       // 32 bytes
///   uint32 count               // number of explicit table entries, 1..0x10000
///   uint16 table[count]
/// </code>
/// Entries beyond <c>count</c> (up to the full 0x10000-entry table) are
/// implicitly replicated from <c>table[count - 1]</c>, matching native's
/// <c>ReplicateLastEntry</c>.</para>
///
/// <para>This port always uses the raw (non-black-level-adjusted) table —
/// native's <c>Prepare</c> only rescales the table when
/// <c>Stage() &gt;= 2 &amp;&amp; negative.Stage3BlackLevel() != 0</c>, which
/// doesn't apply to the Stage-1/raw usage this port targets. This is a known
/// simplification affecting only legacy files with a nonzero Stage-3 black
/// level.</para>
///
/// <para>This opcode only makes sense on a 16-bit raw image — matching
/// native's <c>BufferPixelType</c> override, which always requests
/// <c>ttShort</c>. A non-<see cref="PixelType.UInt16"/> image throws
/// <see cref="DngError.NotYetImplemented"/>.</para>
/// </summary>
public static class MapTableOpcode
{
    private const int TableSize = 0x10000;

    /// <summary>Decoded full 65536-entry lookup table plus the area/plane/pitch it applies to.</summary>
    public sealed class Params
    {
        public required DngAreaSpec AreaSpec { get; init; }

        /// <summary>Always exactly 65536 entries (trailing entries replicated from the last explicit one).</summary>
        public required ushort[] Table { get; init; }
    }

    /// <summary>Decode a <c>MapTable</c> opcode body.</summary>
    public static Params Decode(ReadOnlySpan<byte> body)
    {
        int offset = 0;
        var areaSpec = DngAreaSpec.Decode(body, ref offset);

        if (body.Length - offset < 4)
            DngThrow.BadFormat("MapTable: body too short (missing count)");

        uint count = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(offset, 4));
        offset += 4;

        if (count == 0 || count > TableSize)
            DngThrow.BadFormat($"MapTable: count {count} out of range [1, {TableSize}]");

        if (body.Length - offset < count * 2)
            DngThrow.BadFormat("MapTable: body too short for lookup table");

        var table = new ushort[TableSize];
        for (int i = 0; i < count; i++)
        {
            table[i] = BinaryPrimitives.ReadUInt16BigEndian(body.Slice(offset, 2));
            offset += 2;
        }

        ushort lastEntry = table[count - 1];
        for (int i = (int)count; i < TableSize; i++)
            table[i] = lastEntry;

        return new Params { AreaSpec = areaSpec, Table = table };
    }

    /// <summary>
    /// Apply the decoded lookup table to <paramref name="image"/> in place.
    /// No-op if the opcode's area doesn't overlap the image.
    /// </summary>
    public static void Apply(SimpleImage image, Params p)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(p);

        if (image.PixelType != PixelType.UInt16)
            DngThrow.NotYetImplemented(
                $"MapTable: only UInt16 images are supported by this port (got {image.PixelType})");

        var overlap = p.AreaSpec.Overlap(image.Bounds);
        if (overlap.IsEmpty) return;

        uint rowPitch = p.AreaSpec.RowPitch;
        uint colPitch = p.AreaSpec.ColPitch;

        var buf = image.Buffer;
        var pixels = MemoryMarshal.Cast<byte, ushort>(buf.AsByteSpan());
        var table = p.Table;

        uint planeStart = p.AreaSpec.Plane;
        uint planeEnd = System.Math.Min(p.AreaSpec.Plane + p.AreaSpec.Planes, image.Planes);

        for (uint plane = planeStart; plane < planeEnd; plane++)
        {
            for (int row = overlap.T; row < overlap.B; row += (int)rowPitch)
            {
                for (int col = overlap.L; col < overlap.R; col += (int)colPitch)
                {
                    long idx = buf.OffsetBytes(row, col, plane) / sizeof(ushort);
                    pixels[(int)idx] = table[pixels[(int)idx]];
                }
            }
        }
    }
}
