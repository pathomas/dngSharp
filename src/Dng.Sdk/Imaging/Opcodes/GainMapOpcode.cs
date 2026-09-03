using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Dng.Sdk.Errors;
using Dng.Sdk.Pixels;

namespace Dng.Sdk.Imaging.Opcodes;

/// <summary>
/// Decodes and applies the <c>GainMap</c> opcode (<see cref="OpcodeId.GainMap"/>,
/// id 9). Mirrors <c>dng_opcode_GainMap</c> / <c>dng_gain_map</c> /
/// <c>dng_gain_map_interpolator</c> in <c>dng_gain_map.cpp</c>: multiplies
/// every pixel in the opcode's area by a gain that is bilinearly interpolated
/// from a small 2D grid of sample points (used to correct 2D-varying
/// uniformity defects such as lens shading).
///
/// <para>Wire format (all big-endian; no leading self-describing
/// byte-count field, since <c>DngOpcodeList</c>'s generic <c>bodySize</c>
/// framing already accounts for it):
/// <code>
///   DngAreaSpec areaSpec       // 32 bytes
///   uint32 pointsV, pointsH    // grid dimensions (rows, columns)
///   real64 spacingV, spacingH  // sample spacing, in fractions of the image bounds
///   real64 originV, originH   // position of the first (top-left) sample
///   uint32 planes              // number of gain-map color planes
///   real32 sample[pointsV * pointsH * planes]   // row-major: row, then column, then plane
/// </code>
/// When <c>pointsV == 1</c>, native forces <c>spacingV = 1.0</c> and
/// <c>originV = 0.0</c> (and likewise for <c>pointsH == 1</c>) regardless of
/// what was written to the stream — a single-row/column grid has no
/// meaningful spacing, and this avoids a spurious divide-by-zero later. This
/// port replicates that override.</para>
///
/// <para><b>Interpolation.</b> For a destination pixel at (row, col), let
/// <c>bounds</c> be the full image bounds (not just the opcode's area —
/// matches native, which always interpolates against
/// <c>imageBounds</c> rather than the opcode's <c>dstArea</c>). Compute the
/// fractional row/column index into the sample grid:
/// <code>
///   rowIndexF = ((row + 0.5 - bounds.T) / bounds.H - originV) / spacingV
///   colIndexF = ((col + 0.5 - bounds.L) / bounds.W - originH) / spacingH
/// </code>
/// (non-finite results are treated as 0, matching native's NaN/Inf guard),
/// clamp each to <c>[0, points - 1]</c>, then bilinearly interpolate the 2×2
/// neighborhood of grid samples surrounding that fractional position. This
/// port evaluates the bilinear interpolation directly in closed form rather
/// than native's incremental per-tile-column optimization
/// (<c>dng_gain_map_interpolator::Increment</c>/<c>ResetColumn</c>), which is
/// algebraically identical (it interpolates the same 2×2 neighborhood, just
/// incrementally re-derived per column for performance) and produces the
/// same result.</para>
///
/// <para>This port always uses <c>blackLevel = 0</c> (native only applies a
/// pre/post black-level rescale when
/// <c>Stage() &gt;= 2 &amp;&amp; negative.Stage3BlackLevel() != 0</c>),
/// matching the simplification already used by the other in-place opcodes;
/// this only affects legacy files with a nonzero Stage-3 black level. Every
/// pixel is multiplied by its interpolated gain and clipped to
/// <c>&lt;= 1.0</c> (matches native's <c>Min_real32</c> clamp).</para>
///
/// <para>This port applies the opcode in place to <see cref="PixelType.Float32"/>
/// images only, matching native's <c>BufferPixelType</c> override (always
/// <c>ttFloat</c>).</para>
/// </summary>
public static class GainMapOpcode
{
    private const int HeaderBytes = 44; // pointsV/H (4+4) + spacing (8+8) + origin (8+8) + planes (4)

    /// <summary>Decoded gain-map opcode: the area/plane/pitch it applies to, plus the sample grid.</summary>
    public sealed class Params
    {
        public required DngAreaSpec AreaSpec { get; init; }

        /// <summary>Grid dimensions: (rows, columns).</summary>
        public required (int V, int H) Points { get; init; }

        /// <summary>Sample spacing, as a fraction of the image bounds along each axis.</summary>
        public required (double V, double H) Spacing { get; init; }

        /// <summary>Position of the first (top-left) sample, as a fraction of the image bounds.</summary>
        public required (double V, double H) Origin { get; init; }

        public required uint Planes { get; init; }

        /// <summary>Row-major samples: <c>Samples[(row * Points.H + col) * Planes + plane]</c>.</summary>
        public required float[] Samples { get; init; }

        public float Entry(int row, int col, int plane) => Samples[(row * Points.H + col) * (int)Planes + plane];
    }

    /// <summary>Decode a <c>GainMap</c> opcode body.</summary>
    public static Params Decode(ReadOnlySpan<byte> body)
    {
        int offset = 0;
        var areaSpec = DngAreaSpec.Decode(body, ref offset);

        if (body.Length - offset < HeaderBytes)
            DngThrow.BadFormat("GainMap: body too short (missing gain map header)");

        int pointsV = (int)BinaryPrimitives.ReadUInt32BigEndian(body.Slice(offset, 4)); offset += 4;
        int pointsH = (int)BinaryPrimitives.ReadUInt32BigEndian(body.Slice(offset, 4)); offset += 4;

        double spacingV = BinaryPrimitives.ReadDoubleBigEndian(body.Slice(offset, 8)); offset += 8;
        double spacingH = BinaryPrimitives.ReadDoubleBigEndian(body.Slice(offset, 8)); offset += 8;

        double originV = BinaryPrimitives.ReadDoubleBigEndian(body.Slice(offset, 8)); offset += 8;
        double originH = BinaryPrimitives.ReadDoubleBigEndian(body.Slice(offset, 8)); offset += 8;

        uint planes = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(offset, 4)); offset += 4;

        if (pointsV < 1 || pointsH < 1)
            DngThrow.BadFormat($"GainMap: invalid grid dimensions ({pointsV}, {pointsH})");
        if (planes < 1)
            DngThrow.BadFormat("GainMap: planes must be >= 1");

        // Single-row/column grids have no meaningful spacing; native forces
        // these to safe defaults regardless of what was on disk.
        if (pointsV == 1) { spacingV = 1.0; originV = 0.0; }
        if (pointsH == 1) { spacingH = 1.0; originH = 0.0; }

        if (!double.IsFinite(spacingV) || !double.IsFinite(spacingH) || spacingV <= 0.0 || spacingH <= 0.0)
            DngThrow.BadFormat("GainMap: invalid spacing");
        if (!double.IsFinite(originV) || !double.IsFinite(originH))
            DngThrow.BadFormat("GainMap: invalid origin");

        long sampleCount = (long)pointsV * pointsH * planes;

        if (body.Length - offset < sampleCount * 4)
            DngThrow.BadFormat("GainMap: body too short for sample grid");

        var samples = new float[sampleCount];
        for (long i = 0; i < sampleCount; i++)
        {
            float v = BinaryPrimitives.ReadSingleBigEndian(body.Slice(offset, 4));
            offset += 4;
            if (!float.IsFinite(v))
                DngThrow.BadFormat("GainMap: non-finite sample value");
            samples[i] = v;
        }

        return new Params
        {
            AreaSpec = areaSpec,
            Points = (pointsV, pointsH),
            Spacing = (spacingV, spacingH),
            Origin = (originV, originH),
            Planes = planes,
            Samples = samples,
        };
    }

    /// <summary>
    /// Compute the clamped grid-index pair and interpolation fraction for one
    /// axis. Mirrors the row/column logic in
    /// <c>dng_gain_map_interpolator</c>'s constructor and
    /// <c>ResetColumn</c>.
    /// </summary>
    private static (int Idx1, int Idx2, float Frac) ComputeAxis(double positionFrac, double origin, double spacing, int lastIndex)
    {
        double indexF = (positionFrac - origin) / spacing;
        if (!double.IsFinite(indexF)) indexF = 0.0;

        if (indexF <= 0.0) return (0, 0, 0f);
        if (indexF >= lastIndex) return (lastIndex, lastIndex, 0f);

        int idx1 = (int)indexF;
        int idx2 = idx1 + 1;
        float frac = (float)(indexF - idx1);
        return (idx1, idx2, frac);
    }

    /// <summary>
    /// Apply the decoded gain map to <paramref name="image"/> in place. No-op
    /// if the opcode's area doesn't overlap the image.
    /// </summary>
    public static void Apply(SimpleImage image, Params p)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(p);

        if (image.PixelType != PixelType.Float32)
            DngThrow.NotYetImplemented(
                $"GainMap: only Float32 images are supported by this port (got {image.PixelType})");

        var overlap = p.AreaSpec.Overlap(image.Bounds);
        if (overlap.IsEmpty) return;

        var bounds = image.Bounds;
        if (bounds.IsEmpty) return;

        int lastRow = p.Points.V - 1;
        int lastCol = p.Points.H - 1;

        double boundsH = bounds.H;
        double boundsW = bounds.W;

        uint rowPitch = p.AreaSpec.RowPitch;
        uint colPitch = p.AreaSpec.ColPitch;

        var buf = image.Buffer;
        var floats = MemoryMarshal.Cast<byte, float>(buf.AsByteSpan());

        uint planeStart = p.AreaSpec.Plane;
        uint planeEnd = System.Math.Min(p.AreaSpec.Plane + p.AreaSpec.Planes, image.Planes);

        for (uint plane = planeStart; plane < planeEnd; plane++)
        {
            int mapPlane = (int)System.Math.Min(plane, p.Planes - 1);

            for (int row = overlap.T; row < overlap.B; row += (int)rowPitch)
            {
                double rowPositionFrac = (row + 0.5 - bounds.T) / boundsH;
                var (row1, row2, rowFrac) = ComputeAxis(rowPositionFrac, p.Origin.V, p.Spacing.V, lastRow);

                for (int col = overlap.L; col < overlap.R; col += (int)colPitch)
                {
                    double colPositionFrac = (col + 0.5 - bounds.L) / boundsW;
                    var (col1, col2, colFrac) = ComputeAxis(colPositionFrac, p.Origin.H, p.Spacing.H, lastCol);

                    float v00 = p.Entry(row1, col1, mapPlane);
                    float v01 = p.Entry(row1, col2, mapPlane);
                    float v10 = p.Entry(row2, col1, mapPlane);
                    float v11 = p.Entry(row2, col2, mapPlane);

                    float top = v00 * (1.0f - colFrac) + v01 * colFrac;
                    float bottom = v10 * (1.0f - colFrac) + v11 * colFrac;
                    float gain = top * (1.0f - rowFrac) + bottom * rowFrac;

                    long idx = buf.OffsetBytes(row, col, plane) / sizeof(float);
                    floats[(int)idx] = float.Min(floats[(int)idx] * gain, 1.0f);
                }
            }
        }
    }
}
