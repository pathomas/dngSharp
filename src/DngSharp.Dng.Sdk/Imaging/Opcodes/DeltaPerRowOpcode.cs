using System.Buffers.Binary;
using System.Runtime.InteropServices;
using DngSharp.Dng.Sdk.Errors;
using DngSharp.Dng.Sdk.Pixels;
using DngSharp.Dng.Sdk.Primitives;

namespace DngSharp.Dng.Sdk.Imaging.Opcodes;

/// <summary>
/// Decodes and applies the <c>DeltaPerRow</c> opcode
/// (<see cref="OpcodeId.DeltaPerRow"/>, id 10). Mirrors
/// <c>dng_opcode_DeltaPerRow</c> in <c>dng_misc_opcodes.cpp</c>: adds a
/// per-row additive delta to every sample in the opcode's area, one delta
/// value per <c>RowPitch</c>-th row, clipped to <c>[-1, 1]</c>.
///
/// <para>Wire format (all big-endian; no leading self-describing
/// byte-count field, since <c>DngOpcodeList</c>'s generic <c>bodySize</c>
/// framing already accounts for it):
/// <code>
///   DngAreaSpec areaSpec       // 32 bytes
///   uint32 count               // == ceil(areaSpec.Area.H / areaSpec.RowPitch)
///   real32 delta[count]
/// </code>
/// </para>
///
/// <para>This port applies the opcode in place to <see cref="PixelType.Float32"/>
/// images only (Stage 2/3 values, which is where this opcode is used in
/// practice — native's integer-buffer promotion path,
/// <c>dng_opcode_DeltaPerRow::BufferPixelType</c>, is not implemented). A
/// <see cref="PixelType"/> other than <see cref="PixelType.Float32"/> throws
/// <see cref="DngError.NotYetImplemented"/> rather than silently corrupting
/// or ignoring data.</para>
/// </summary>
public static class DeltaPerRowOpcode
{
    /// <summary>Decoded per-row additive deltas plus the area/plane/pitch they apply to.</summary>
    public sealed class Params
    {
        public required DngAreaSpec AreaSpec { get; init; }
        public required float[] Deltas { get; init; }
    }

    /// <summary>Decode a <c>DeltaPerRow</c> opcode body.</summary>
    public static Params Decode(ReadOnlySpan<byte> body)
    {
        int offset = 0;
        var areaSpec = DngAreaSpec.Decode(body, ref offset);

        if (body.Length - offset < 4)
            DngThrow.BadFormat("DeltaPerRow: body too short (missing count)");

        uint count = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(offset, 4));
        offset += 4;

        uint expectedCount = CeilDiv(areaSpec.Area.H, areaSpec.RowPitch);
        if (count != expectedCount)
            DngThrow.BadFormat($"DeltaPerRow: count {count} != expected {expectedCount}");

        if (body.Length - offset < count * 4)
            DngThrow.BadFormat("DeltaPerRow: body too short for delta table");

        var deltas = new float[count];
        for (int i = 0; i < count; i++)
        {
            float v = BinaryPrimitives.ReadSingleBigEndian(body.Slice(offset, 4));
            offset += 4;
            if (!float.IsFinite(v))
                DngThrow.BadFormat("DeltaPerRow: non-finite delta value");
            deltas[i] = v;
        }

        return new Params { AreaSpec = areaSpec, Deltas = deltas };
    }

    /// <summary>
    /// Apply the decoded deltas to <paramref name="image"/> in place. No-op
    /// if the opcode's area doesn't overlap the image, or if the table is
    /// empty (e.g. an empty area spec).
    /// </summary>
    public static void Apply(SimpleImage image, Params p)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(p);

        if (image.PixelType != PixelType.Float32)
            DngThrow.NotYetImplemented(
                $"DeltaPerRow: only Float32 images are supported by this port (got {image.PixelType})");

        var overlap = p.AreaSpec.Overlap(image.Bounds);
        if (overlap.IsEmpty || p.Deltas.Length == 0) return;

        uint rowPitch = p.AreaSpec.RowPitch;
        uint colPitch = p.AreaSpec.ColPitch;

        uint rows = CeilDiv(overlap.H, rowPitch);
        uint cols = CeilDiv(overlap.W, colPitch);

        var buf = image.Buffer;
        var floats = MemoryMarshal.Cast<byte, float>(buf.AsByteSpan());

        uint planeStart = p.AreaSpec.Plane;
        uint planeEnd = System.Math.Min(p.AreaSpec.Plane + p.AreaSpec.Planes, image.Planes);

        for (uint plane = planeStart; plane < planeEnd; plane++)
        {
            int tableStart = (overlap.T - p.AreaSpec.Area.T) / (int)rowPitch;

            int row = overlap.T;
            for (uint rowIdx = 0; rowIdx < rows; rowIdx++)
            {
                int tableIdx = tableStart + (int)rowIdx;
                if ((uint)tableIdx >= (uint)p.Deltas.Length) break;

                float rowDelta = p.Deltas[tableIdx];

                int col = overlap.L;
                for (uint colIdx = 0; colIdx < cols; colIdx++)
                {
                    long idx = buf.OffsetBytes(row, col, plane) / sizeof(float);
                    float x = floats[(int)idx];
                    float y = float.Clamp(x + rowDelta, -1.0f, 1.0f);
                    floats[(int)idx] = y;

                    col += (int)colPitch;
                }

                row += (int)rowPitch;
            }
        }
    }

    private static uint CeilDiv(uint numerator, uint denominator) =>
        denominator == 0 ? 0 : (numerator + denominator - 1) / denominator;
}
