using System.Buffers.Binary;
using System.Runtime.InteropServices;
using DngSharp.Dng.Sdk.Errors;
using DngSharp.Dng.Sdk.Pixels;

namespace DngSharp.Dng.Sdk.Imaging.Opcodes;

/// <summary>
/// Decodes and applies the <c>FixBadPixelsConstant</c> opcode
/// (<see cref="OpcodeId.FixBadPixelsConstant"/>, id 4). Mirrors
/// <c>dng_opcode_FixBadPixelsConstant</c> in <c>dng_bad_pixels.cpp</c>:
/// replaces every raw sample matching a known "stuck" constant value with an
/// average of its same-color Bayer neighbors.
///
/// <para>Wire format (all big-endian; no leading self-describing
/// byte-count field, since <c>DngOpcodeList</c>'s generic <c>bodySize</c>
/// framing already accounts for it):
/// <code>
///   uint32 constant     // the "bad pixel" sentinel value
///   uint32 bayerPhase   // 0-3, encodes which 2x2 CFA phase (0,0) belongs to
/// </code>
/// </para>
///
/// <para>This opcode only makes sense on a single-plane, 16-bit raw Bayer
/// mosaic — i.e. a Stage-1 image before demosaic — matching native's
/// <c>Prepare</c> check (<c>imagePlanes == 1 &amp;&amp; bufferPixelType ==
/// ttShort</c>). A non-matching image throws
/// <see cref="DngError.NotYetImplemented"/>.</para>
///
/// <para>Bad pixels within 2 rows/cols of the image edge use whatever
/// same-color neighbors fall inside the image (matching native's edge
/// clamping via the buffer's own bounds check — out-of-bounds source reads
/// are impossible in native only because <c>SrcArea</c> pads the source tile
/// by 2px in each direction and the filter-task infrastructure clips that
/// pad to the image bounds; here we simply skip any neighbor offset that
/// falls outside the image).</para>
/// </summary>
public static class FixBadPixelsConstantOpcode
{
    public sealed class Params
    {
        public required uint Constant { get; init; }
        public required uint BayerPhase { get; init; }
    }

    /// <summary>Decode a <c>FixBadPixelsConstant</c> opcode body.</summary>
    public static Params Decode(ReadOnlySpan<byte> body)
    {
        if (body.Length < 8)
            DngThrow.BadFormat("FixBadPixelsConstant: body too short");

        uint constant = BinaryPrimitives.ReadUInt32BigEndian(body[..4]);
        uint bayerPhase = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(4, 4));

        return new Params { Constant = constant, BayerPhase = bayerPhase };
    }

    private static bool IsGreen(int row, int col, uint bayerPhase) =>
        (((uint)row + (uint)col + bayerPhase + (bayerPhase >> 1)) & 1) == 0;

    /// <summary>Apply the fix in place to a single-plane, UInt16 Bayer image.</summary>
    public static void Apply(SimpleImage image, Params p)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(p);

        if (image.Planes != 1)
            DngThrow.NotYetImplemented(
                $"FixBadPixelsConstant: only single-plane images are supported (got {image.Planes} planes)");

        if (image.PixelType != PixelType.UInt16)
            DngThrow.NotYetImplemented(
                $"FixBadPixelsConstant: only UInt16 images are supported by this port (got {image.PixelType})");

        ushort badPixel = (ushort)p.Constant;
        var bounds = image.Bounds;

        var buf = image.Buffer;
        var pixels = MemoryMarshal.Cast<byte, ushort>(buf.AsByteSpan());

        // Collect fixes first so a repaired pixel doesn't feed into a
        // neighboring pixel's average within the same pass (matches native,
        // which reads from a separate untouched srcBuffer while writing to
        // dstBuffer).
        var writes = new List<(int Row, int Col, ushort Value)>();

        Span<(int dr, int dc)> greenOffsets = [(-1, -1), (-1, 1), (1, -1), (1, 1)];
        Span<(int dr, int dc)> otherOffsets = [(-2, 0), (2, 0), (0, -2), (0, 2)];

        for (int row = bounds.T; row < bounds.B; row++)
        {
            for (int col = bounds.L; col < bounds.R; col++)
            {
                long selfIdx = buf.OffsetBytes(row, col, 0) / sizeof(ushort);
                if (pixels[(int)selfIdx] != badPixel) continue;

                uint count = 0;
                uint total = 0;

                var offsets = IsGreen(row, col, p.BayerPhase) ? greenOffsets : otherOffsets;

                foreach (var (dr, dc) in offsets)
                {
                    int r = row + dr;
                    int c = col + dc;
                    if (r < bounds.T || r >= bounds.B || c < bounds.L || c >= bounds.R) continue;

                    long idx = buf.OffsetBytes(r, c, 0) / sizeof(ushort);
                    ushort v = pixels[(int)idx];
                    if (v == badPixel) continue;

                    count++;
                    total += v;
                }

                if (count == 4)
                {
                    writes.Add((row, col, (ushort)((total + 2) >> 2)));
                }
                else if (count > 0)
                {
                    writes.Add((row, col, (ushort)((total + (count >> 1)) / count)));
                }
            }
        }

        foreach (var (r, c, v) in writes)
        {
            long idx = buf.OffsetBytes(r, c, 0) / sizeof(ushort);
            pixels[(int)idx] = v;
        }
    }
}
