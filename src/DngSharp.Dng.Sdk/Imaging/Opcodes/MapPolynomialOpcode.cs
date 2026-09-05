using System.Buffers.Binary;
using System.Runtime.InteropServices;
using DngSharp.Dng.Sdk.Errors;
using DngSharp.Dng.Sdk.Pixels;

namespace DngSharp.Dng.Sdk.Imaging.Opcodes;

/// <summary>
/// Decodes and applies the <c>MapPolynomial</c> opcode
/// (<see cref="OpcodeId.MapPolynomial"/>, id 8). Mirrors
/// <c>dng_opcode_MapPolynomial</c> in <c>dng_misc_opcodes.cpp</c>: applies a
/// polynomial curve (degree 0-8) to every sample in the opcode's area, one
/// evaluation per <c>RowPitch</c>/<c>ColPitch</c>-th row/column.
///
/// <para>Wire format (all big-endian; no leading self-describing
/// byte-count field, since <c>DngOpcodeList</c>'s generic <c>bodySize</c>
/// framing already accounts for it):
/// <code>
///   DngAreaSpec areaSpec       // 32 bytes
///   uint32 degree              // 0..8
///   real64 coefficient[degree + 1]
/// </code>
/// </para>
///
/// <para><b>Evaluation.</b> Native evaluates the polynomial as an odd-symmetric
/// function of every non-constant term (so the response never flips sign
/// discontinuously at zero): with <c>ax = |x|</c>,
/// <code>
///   q = c[degree]
///   for k = degree-1 downto 1: q = c[k] + ax * q
///   y = c[0] + x * q               // degree == 0: y = c[0]
/// </code>
/// then <c>y</c> is clipped to <c>[-1, 1]</c>. This single loop reproduces
/// native's per-degree hand-unrolled cases (which special-case each degree
/// 0-8 with explicit +/- nesting) exactly, since <c>c[k] + ax*q</c> is
/// algebraically identical to native's branch-on-sign-of-x recurrence for
/// every degree.</para>
///
/// <para>This port always uses <c>blackLevel = 0</c> (native only applies a
/// pre/post black-level rescale when
/// <c>Stage() &gt;= 2 &amp;&amp; negative.Stage3BlackLevel() != 0</c>),
/// matching the simplification already used by the Delta/Scale opcodes; this
/// only affects legacy files with a nonzero Stage-3 black level.</para>
///
/// <para>This port applies the opcode in place to <see cref="PixelType.Float32"/>
/// images only (Stage 2/3 values). A <see cref="PixelType"/> other than
/// <see cref="PixelType.Float32"/> throws <see cref="DngError.NotYetImplemented"/>.</para>
/// </summary>
public static class MapPolynomialOpcode
{
    public const int MaxDegree = 8;

    /// <summary>Decoded polynomial coefficients plus the area/plane/pitch they apply to.</summary>
    public sealed class Params
    {
        public required DngAreaSpec AreaSpec { get; init; }
        public required uint Degree { get; init; }

        /// <summary>Exactly <c>Degree + 1</c> coefficients, <c>Coefficients[0]</c> is the constant term.</summary>
        public required float[] Coefficients { get; init; }
    }

    /// <summary>Decode a <c>MapPolynomial</c> opcode body.</summary>
    public static Params Decode(ReadOnlySpan<byte> body)
    {
        int offset = 0;
        var areaSpec = DngAreaSpec.Decode(body, ref offset);

        if (body.Length - offset < 4)
            DngThrow.BadFormat("MapPolynomial: body too short (missing degree)");

        uint degree = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(offset, 4));
        offset += 4;

        if (degree > MaxDegree)
            DngThrow.BadFormat($"MapPolynomial: degree {degree} > max {MaxDegree}");

        if (body.Length - offset < (degree + 1) * 8)
            DngThrow.BadFormat("MapPolynomial: body too short for coefficients");

        var coefficients = new float[degree + 1];
        for (int i = 0; i <= degree; i++)
        {
            double v = BinaryPrimitives.ReadDoubleBigEndian(body.Slice(offset, 8));
            offset += 8;
            if (!double.IsFinite(v))
                DngThrow.BadFormat("MapPolynomial: non-finite coefficient");
            coefficients[i] = (float)v;
        }

        return new Params { AreaSpec = areaSpec, Degree = degree, Coefficients = coefficients };
    }

    private static float Evaluate(float x, float[] c, uint degree)
    {
        if (degree == 0) return c[0];

        float ax = System.Math.Abs(x);
        float q = c[degree];
        for (int k = (int)degree - 1; k >= 1; k--)
            q = c[k] + ax * q;

        return c[0] + x * q;
    }

    /// <summary>
    /// Apply the decoded polynomial to <paramref name="image"/> in place. No-op
    /// if the opcode's area doesn't overlap the image.
    /// </summary>
    public static void Apply(SimpleImage image, Params p)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(p);

        if (image.PixelType != PixelType.Float32)
            DngThrow.NotYetImplemented(
                $"MapPolynomial: only Float32 images are supported by this port (got {image.PixelType})");

        var overlap = p.AreaSpec.Overlap(image.Bounds);
        if (overlap.IsEmpty) return;

        uint rowPitch = p.AreaSpec.RowPitch;
        uint colPitch = p.AreaSpec.ColPitch;

        var buf = image.Buffer;
        var floats = MemoryMarshal.Cast<byte, float>(buf.AsByteSpan());
        var coefficients = p.Coefficients;
        uint degree = p.Degree;

        uint planeStart = p.AreaSpec.Plane;
        uint planeEnd = System.Math.Min(p.AreaSpec.Plane + p.AreaSpec.Planes, image.Planes);

        for (uint plane = planeStart; plane < planeEnd; plane++)
        {
            for (int row = overlap.T; row < overlap.B; row += (int)rowPitch)
            {
                for (int col = overlap.L; col < overlap.R; col += (int)colPitch)
                {
                    long idx = buf.OffsetBytes(row, col, plane) / sizeof(float);
                    float x = floats[(int)idx];
                    float y = Evaluate(x, coefficients, degree);
                    floats[(int)idx] = float.Clamp(y, -1.0f, 1.0f);
                }
            }
        }
    }
}
