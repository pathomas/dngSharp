using System.Buffers.Binary;
using System.Runtime.InteropServices;
using DngSharp.Dng.Sdk.Errors;
using DngSharp.Dng.Sdk.Pixels;
using DngSharp.Dng.Sdk.Primitives;

namespace DngSharp.Dng.Sdk.Imaging.Opcodes;

/// <summary>
/// Decodes and applies the <c>FixBadPixelsList</c> opcode
/// (<see cref="OpcodeId.FixBadPixelsList"/>, id 5). Parses the explicit list
/// of known-bad single pixels and bad rectangles (usually single rows or
/// columns) that native carries in <c>dng_bad_pixel_list</c>
/// (<c>dng_bad_pixels.h/cpp</c>).
///
/// <para>Wire format (all big-endian; no leading self-describing
/// byte-count field, since <c>DngOpcodeList</c>'s generic <c>bodySize</c>
/// framing already accounts for it):
/// <code>
///   uint32 bayerPhase   // 0-3, encodes which 2x2 CFA phase (0,0) belongs to
///   uint32 pointCount
///   uint32 rectCount
///   { int32 v, int32 h } * pointCount   // bad single-pixel coordinates
///   { int32 t,l,b,r }    * rectCount    // bad rectangles
/// </code>
/// </para>
///
/// <para><b>Known simplification:</b> native's repair logic is split into
/// five distinct cases (<c>FixIsolatedPixel</c>, <c>FixClusteredPixel</c>,
/// <c>FixSingleColumn</c>, <c>FixSingleRow</c>, <c>FixClusteredRect</c>),
/// each with its own multi-pixel search/diffusion strategy spanning
/// hundreds of lines in <c>dng_bad_pixels.cpp</c>. This port unifies all of
/// them into a single rule: every flagged pixel (whether an explicit bad
/// point or inside a bad rectangle) is replaced by the average of its
/// same-color Bayer neighbors (diagonal neighbors for green sites, axis
/// neighbors two pixels away for red/blue sites — the same neighborhood
/// <see cref="FixBadPixelsConstantOpcode"/> uses), skipping any neighbor
/// that is itself flagged bad. This correctly repairs isolated hot/dead
/// pixels and thin (1-pixel-wide) bad rows/columns — the overwhelming
/// majority of real-world camera defect maps — but does not reproduce
/// native's exact diffusion behavior for wide clustered defects.</para>
///
/// <para>Only single-plane, 16-bit raw Bayer mosaic images (Stage-1, before
/// demosaic) are supported, matching native's <c>Prepare</c> check. A
/// non-matching image throws <see cref="DngError.NotYetImplemented"/>.</para>
/// </summary>
public static class FixBadPixelsListOpcode
{
    public sealed class Params
    {
        public required uint BayerPhase { get; init; }
        public required DngPoint[] Points { get; init; }
        public required DngRect[] Rects { get; init; }
    }

    /// <summary>Decode a <c>FixBadPixelsList</c> opcode body.</summary>
    public static Params Decode(ReadOnlySpan<byte> body)
    {
        if (body.Length < 12)
            DngThrow.BadFormat("FixBadPixelsList: body too short");

        uint bayerPhase = BinaryPrimitives.ReadUInt32BigEndian(body[..4]);
        uint pointCount = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(4, 4));
        uint rectCount = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(8, 4));

        int offset = 12;
        long remaining = (long)pointCount * 8 + (long)rectCount * 16;
        if (body.Length - offset < remaining)
            DngThrow.BadFormat("FixBadPixelsList: body too short for point/rect tables");

        var points = new DngPoint[pointCount];
        for (int i = 0; i < pointCount; i++)
        {
            int v = BinaryPrimitives.ReadInt32BigEndian(body.Slice(offset, 4));
            offset += 4;
            int h = BinaryPrimitives.ReadInt32BigEndian(body.Slice(offset, 4));
            offset += 4;
            points[i] = new DngPoint(v, h);
        }

        var rects = new DngRect[rectCount];
        for (int i = 0; i < rectCount; i++)
        {
            int t = BinaryPrimitives.ReadInt32BigEndian(body.Slice(offset, 4));
            offset += 4;
            int l = BinaryPrimitives.ReadInt32BigEndian(body.Slice(offset, 4));
            offset += 4;
            int b = BinaryPrimitives.ReadInt32BigEndian(body.Slice(offset, 4));
            offset += 4;
            int r = BinaryPrimitives.ReadInt32BigEndian(body.Slice(offset, 4));
            offset += 4;
            rects[i] = new DngRect(t, l, b, r);
        }

        return new Params { BayerPhase = bayerPhase, Points = points, Rects = rects };
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
                $"FixBadPixelsList: only single-plane images are supported (got {image.Planes} planes)");

        if (image.PixelType != PixelType.UInt16)
            DngThrow.NotYetImplemented(
                $"FixBadPixelsList: only UInt16 images are supported by this port (got {image.PixelType})");

        var bounds = image.Bounds;
        var buf = image.Buffer;
        var pixels = MemoryMarshal.Cast<byte, ushort>(buf.AsByteSpan());

        bool IsFlaggedBad(int row, int col)
        {
            foreach (var pt in p.Points)
                if (pt.V == row && pt.H == col) return true;

            foreach (var r in p.Rects)
                if (row >= r.T && row < r.B && col >= r.L && col < r.R) return true;

            return false;
        }

        // Gather every flagged coordinate that actually falls within the
        // image (points directly, rects expanded cell-by-cell).
        var targets = new List<(int Row, int Col)>();
        foreach (var pt in p.Points)
            if (pt.V >= bounds.T && pt.V < bounds.B && pt.H >= bounds.L && pt.H < bounds.R)
                targets.Add((pt.V, pt.H));

        foreach (var r in p.Rects)
        {
            var clipped = DngRect.Intersect(r, bounds);
            for (int row = clipped.T; row < clipped.B; row++)
                for (int col = clipped.L; col < clipped.R; col++)
                    targets.Add((row, col));
        }

        Span<(int dr, int dc)> greenOffsets = [(-1, -1), (-1, 1), (1, -1), (1, 1)];
        Span<(int dr, int dc)> otherOffsets = [(-2, 0), (2, 0), (0, -2), (0, 2)];

        var writes = new List<(int Row, int Col, ushort Value)>();

        foreach (var (row, col) in targets)
        {
            uint count = 0;
            uint total = 0;

            var offsets = IsGreen(row, col, p.BayerPhase) ? greenOffsets : otherOffsets;

            foreach (var (dr, dc) in offsets)
            {
                int r = row + dr;
                int c = col + dc;
                if (r < bounds.T || r >= bounds.B || c < bounds.L || c >= bounds.R) continue;
                if (IsFlaggedBad(r, c)) continue;

                long idx = buf.OffsetBytes(r, c, 0) / sizeof(ushort);
                total += pixels[(int)idx];
                count++;
            }

            if (count == 4)
                writes.Add((row, col, (ushort)((total + 2) >> 2)));
            else if (count > 0)
                writes.Add((row, col, (ushort)((total + (count >> 1)) / count)));
        }

        foreach (var (r, c, v) in writes)
        {
            long idx = buf.OffsetBytes(r, c, 0) / sizeof(ushort);
            pixels[(int)idx] = v;
        }
    }
}
