using System.Buffers.Binary;
using Dng.Sdk.Errors;
using Dng.Sdk.Primitives;

namespace Dng.Sdk.Imaging.Opcodes;

/// <summary>
/// Decoded <c>dng_area_spec</c> — the common area/plane/pitch header shared
/// by several in-place opcode bodies (<c>DeltaPerRow</c>,
/// <c>DeltaPerColumn</c>, <c>ScalePerRow</c>, <c>ScalePerColumn</c>,
/// <c>MapTable</c>, <c>MapPolynomial</c>, <c>GainMap</c>). Mirrors
/// <c>dng_area_spec::GetData</c> in <c>dng_misc_opcodes.cpp</c>.
///
/// <para>Wire format (all big-endian, 32 bytes total):
/// <code>
///   int32  areaTop, areaLeft, areaBottom, areaRight
///   uint32 plane, planes
///   uint32 rowPitch, colPitch
/// </code>
/// </para>
///
/// <para>When <see cref="Area"/> is empty (zero-size rect), several callers
/// treat it as "the entire image/tile" (<c>dng_area_spec::Overlap</c>); for
/// row/column delta and scale opcodes this degenerates to zero entries
/// (matching native, since <c>rowPitch</c>/<c>colPitch</c> must be 1 and the
/// entry count is derived from the — zero-sized — area), i.e. effectively a
/// no-op. Real-world files always encode the true image rect here.</para>
/// </summary>
public sealed class DngAreaSpec
{
    /// <summary>Size in bytes of the area-spec header on disk.</summary>
    public const int WireSize = 32;

    public required DngRect Area { get; init; }
    public required uint Plane { get; init; }
    public required uint Planes { get; init; }
    public required uint RowPitch { get; init; }
    public required uint ColPitch { get; init; }

    /// <summary>
    /// Decode a <see cref="DngAreaSpec"/> from <paramref name="body"/> at
    /// <paramref name="offset"/>, advancing <paramref name="offset"/> by
    /// <see cref="WireSize"/> bytes.
    /// </summary>
    public static DngAreaSpec Decode(ReadOnlySpan<byte> body, ref int offset)
    {
        if (body.Length - offset < WireSize)
            DngThrow.BadFormat("Area spec: body too short");

        int t = BinaryPrimitives.ReadInt32BigEndian(body.Slice(offset, 4)); offset += 4;
        int l = BinaryPrimitives.ReadInt32BigEndian(body.Slice(offset, 4)); offset += 4;
        int b = BinaryPrimitives.ReadInt32BigEndian(body.Slice(offset, 4)); offset += 4;
        int r = BinaryPrimitives.ReadInt32BigEndian(body.Slice(offset, 4)); offset += 4;

        uint plane = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(offset, 4)); offset += 4;
        uint planes = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(offset, 4)); offset += 4;

        uint rowPitch = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(offset, 4)); offset += 4;
        uint colPitch = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(offset, 4)); offset += 4;

        if (planes < 1)
            DngThrow.BadFormat("Area spec: planes must be >= 1");
        if (plane > uint.MaxValue - planes)
            DngThrow.BadFormat("Area spec: plane + planes overflows");
        if (rowPitch < 1 || colPitch < 1)
            DngThrow.BadFormat("Area spec: rowPitch/colPitch must be >= 1");

        var area = new DngRect(t, l, b, r);

        if (area.IsEmpty)
        {
            if (rowPitch != 1 || colPitch != 1)
                DngThrow.BadFormat(
                    $"Area spec: empty area requires rowPitch == colPitch == 1 " +
                    $"(t={t}, l={l}, b={b}, r={r}, plane={plane}, planes={planes}, rowPitch={rowPitch}, colPitch={colPitch})");
        }
        else
        {
            if (rowPitch > area.H || colPitch > area.W)
                DngThrow.BadFormat("Area spec: rowPitch/colPitch exceeds area size");
        }

        return new DngAreaSpec
        {
            Area = area,
            Plane = plane,
            Planes = planes,
            RowPitch = rowPitch,
            ColPitch = colPitch,
        };
    }

    /// <summary>
    /// Intersect this spec's area with <paramref name="tile"/>. Mirrors
    /// <c>dng_area_spec::Overlap</c> — an empty <see cref="Area"/> is treated
    /// as covering the entire <paramref name="tile"/>.
    /// </summary>
    public DngRect Overlap(DngRect tile) => Area.IsEmpty ? tile : DngRect.Intersect(Area, tile);
}
