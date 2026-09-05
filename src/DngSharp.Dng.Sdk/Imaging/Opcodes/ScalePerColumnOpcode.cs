using System.Buffers.Binary;
using System.Runtime.InteropServices;
using DngSharp.Dng.Sdk.Errors;
using DngSharp.Dng.Sdk.Pixels;
using DngSharp.Dng.Sdk.Primitives;

namespace DngSharp.Dng.Sdk.Imaging.Opcodes;

/// <summary>
/// Decodes and applies the <c>ScalePerColumn</c> opcode
/// (<see cref="OpcodeId.ScalePerColumn"/>, id 13). Mirrors
/// <c>dng_opcode_ScalePerColumn</c> in <c>dng_misc_opcodes.cpp</c>:
/// multiplies every sample in the opcode's area by a per-column scale
/// factor, one scale value per <c>ColPitch</c>-th column, clipped to
/// <c>[-1, 1]</c>.
///
/// <para>Wire format (all big-endian; no leading self-describing
/// byte-count field, since <c>DngOpcodeList</c>'s generic <c>bodySize</c>
/// framing already accounts for it):
/// <code>
///   DngAreaSpec areaSpec       // 32 bytes
///   uint32 count               // == ceil(areaSpec.Area.W / areaSpec.ColPitch)
///   real32 scale[count]
/// </code>
/// </para>
///
/// <para>This port applies the opcode in place to <see cref="PixelType.Float32"/>
/// images only, and always uses a black offset of zero (native's
/// <c>Stage3BlackLevel</c>-conditional black-relative scaling for legacy
/// files is not implemented — a known simplification, low real-world impact
/// since modern DNGs keep Stage 2/3 data black-subtracted). A
/// <see cref="PixelType"/> other than <see cref="PixelType.Float32"/> throws
/// <see cref="DngError.NotYetImplemented"/> rather than silently corrupting
/// or ignoring data.</para>
/// </summary>
public static class ScalePerColumnOpcode
{
    /// <summary>Decoded per-column multiplicative scales plus the area/plane/pitch they apply to.</summary>
    public sealed class Params
    {
        public required DngAreaSpec AreaSpec { get; init; }
        public required float[] Scales { get; init; }
    }

    /// <summary>Decode a <c>ScalePerColumn</c> opcode body.</summary>
    public static Params Decode(ReadOnlySpan<byte> body)
    {
        int offset = 0;
        var areaSpec = DngAreaSpec.Decode(body, ref offset);

        if (body.Length - offset < 4)
            DngThrow.BadFormat("ScalePerColumn: body too short (missing count)");

        uint count = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(offset, 4));
        offset += 4;

        uint expectedCount = CeilDiv(areaSpec.Area.W, areaSpec.ColPitch);
        if (count != expectedCount)
            DngThrow.BadFormat($"ScalePerColumn: count {count} != expected {expectedCount}");

        if (body.Length - offset < count * 4)
            DngThrow.BadFormat("ScalePerColumn: body too short for scale table");

        var scales = new float[count];
        for (int i = 0; i < count; i++)
        {
            float v = BinaryPrimitives.ReadSingleBigEndian(body.Slice(offset, 4));
            offset += 4;
            if (!float.IsFinite(v))
                DngThrow.BadFormat("ScalePerColumn: non-finite scale value");
            scales[i] = v;
        }

        return new Params { AreaSpec = areaSpec, Scales = scales };
    }

    /// <summary>
    /// Apply the decoded scales to <paramref name="image"/> in place. No-op
    /// if the opcode's area doesn't overlap the image, or if the table is
    /// empty (e.g. an empty area spec).
    /// </summary>
    public static void Apply(SimpleImage image, Params p)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(p);

        if (image.PixelType != PixelType.Float32)
            DngThrow.NotYetImplemented(
                $"ScalePerColumn: only Float32 images are supported by this port (got {image.PixelType})");

        var overlap = p.AreaSpec.Overlap(image.Bounds);
        if (overlap.IsEmpty || p.Scales.Length == 0) return;

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
                if ((uint)tableIdx >= (uint)p.Scales.Length) break;

                float colScale = p.Scales[tableIdx];

                int row = overlap.T;
                for (uint rowIdx = 0; rowIdx < rows; rowIdx++)
                {
                    long idx = buf.OffsetBytes(row, col, plane) / sizeof(float);
                    float x = floats[(int)idx];
                    float y = float.Clamp(x * colScale, -1.0f, 1.0f);
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
