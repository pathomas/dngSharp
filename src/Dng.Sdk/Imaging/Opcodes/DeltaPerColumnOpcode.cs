using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Dng.Sdk.Errors;
using Dng.Sdk.Pixels;
using Dng.Sdk.Primitives;

namespace Dng.Sdk.Imaging.Opcodes;

/// <summary>
/// Decodes and applies the <c>DeltaPerColumn</c> opcode
/// (<see cref="OpcodeId.DeltaPerColumn"/>, id 11). Mirrors
/// <c>dng_opcode_DeltaPerColumn</c> in <c>dng_misc_opcodes.cpp</c>: adds a
/// per-column additive delta to every sample in the opcode's area, one
/// delta value per <c>ColPitch</c>-th column, clipped to <c>[-1, 1]</c>.
///
/// <para>Wire format (all big-endian; no leading self-describing
/// byte-count field, since <c>DngOpcodeList</c>'s generic <c>bodySize</c>
/// framing already accounts for it):
/// <code>
///   DngAreaSpec areaSpec       // 32 bytes
///   uint32 count               // == ceil(areaSpec.Area.W / areaSpec.ColPitch)
///   real32 delta[count]
/// </code>
/// </para>
///
/// <para>This port applies the opcode in place to <see cref="PixelType.Float32"/>
/// images only (Stage 2/3 values, which is where this opcode is used in
/// practice — native's integer-buffer promotion path,
/// <c>dng_opcode_DeltaPerColumn::BufferPixelType</c>, is not implemented).
/// A <see cref="PixelType"/> other than <see cref="PixelType.Float32"/>
/// throws <see cref="DngError.NotYetImplemented"/> rather than silently
/// corrupting or ignoring data.</para>
/// </summary>
public static class DeltaPerColumnOpcode
{
    /// <summary>Decoded per-column additive deltas plus the area/plane/pitch they apply to.</summary>
    public sealed class Params
    {
        public required DngAreaSpec AreaSpec { get; init; }
        public required float[] Deltas { get; init; }
    }

    /// <summary>Decode a <c>DeltaPerColumn</c> opcode body.</summary>
    public static Params Decode(ReadOnlySpan<byte> body)
    {
        int offset = 0;
        var areaSpec = DngAreaSpec.Decode(body, ref offset);

        if (body.Length - offset < 4)
            DngThrow.BadFormat("DeltaPerColumn: body too short (missing count)");

        uint count = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(offset, 4));
        offset += 4;

        uint expectedCount = CeilDiv(areaSpec.Area.W, areaSpec.ColPitch);
        if (count != expectedCount)
            DngThrow.BadFormat($"DeltaPerColumn: count {count} != expected {expectedCount}");

        if (body.Length - offset < count * 4)
            DngThrow.BadFormat("DeltaPerColumn: body too short for delta table");

        var deltas = new float[count];
        for (int i = 0; i < count; i++)
        {
            float v = BinaryPrimitives.ReadSingleBigEndian(body.Slice(offset, 4));
            offset += 4;
            if (!float.IsFinite(v))
                DngThrow.BadFormat("DeltaPerColumn: non-finite delta value");
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
                $"DeltaPerColumn: only Float32 images are supported by this port (got {image.PixelType})");

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
            int tableStart = (overlap.L - p.AreaSpec.Area.L) / (int)colPitch;

            int col = overlap.L;
            for (uint colIdx = 0; colIdx < cols; colIdx++)
            {
                int tableIdx = tableStart + (int)colIdx;
                if ((uint)tableIdx >= (uint)p.Deltas.Length) break;

                float colDelta = p.Deltas[tableIdx];

                int row = overlap.T;
                for (uint rowIdx = 0; rowIdx < rows; rowIdx++)
                {
                    long idx = buf.OffsetBytes(row, col, plane) / sizeof(float);
                    float x = floats[(int)idx];
                    float y = float.Clamp(x + colDelta, -1.0f, 1.0f);
                    floats[(int)idx] = y;

                    row += (int)rowPitch;
                }

                col += (int)colPitch;
            }
        }
    }

    private static uint CeilDiv(uint numerator, uint denominator) =>
        denominator == 0 ? 0 : (numerator + denominator - 1) / denominator;
}
