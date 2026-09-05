using System.Buffers.Binary;
using System.Runtime.InteropServices;
using DngSharp.Dng.Sdk.Errors;
using DngSharp.Dng.Sdk.Pixels;

namespace DngSharp.Dng.Sdk.Imaging.Opcodes;

/// <summary>
/// Decodes and applies the <c>FixVignetteRadial</c> opcode
/// (<see cref="OpcodeId.FixVignetteRadial"/>, id 3). Mirrors
/// <c>dng_opcode_FixVignetteRadial</c> / <c>dng_vignette_radial_params</c> /
/// <c>dng_vignette_radial_function</c> / <c>RefVignette32</c> in
/// <c>dng_lens_correction.cpp</c> / <c>dng_reference.cpp</c>: multiplies
/// every pixel by a radially-symmetric gain that compensates for lens
/// vignetting (peripheral illumination falloff).
///
/// <para>Wire format (all big-endian, following this port's established
/// <see cref="WarpRectilinearParams"/>/<see cref="WarpRectilinear2Params"/>
/// convention — no leading self-describing byte-count field, since
/// <c>DngOpcodeList</c>'s generic <c>bodySize</c> framing already accounts
/// for it):
/// <code>
///   real64 k0, k1, k2, k3, k4   // 5 radial gain-polynomial coefficients
///   real64 centerH, centerV    // optical center, normalized [0,1]
/// </code>
/// </para>
///
/// <para><b>Gain formula.</b> Let <c>r</c> be the Euclidean distance from a
/// pixel to the optical center, normalized so that the farthest image corner
/// from the center is at <c>r == 1</c> (assuming square pixels — see the
/// simplification note below). With <c>x = r^2</c>:
/// <code>
///   gain(x) = 1 + k0*x + k1*x^2 + k2*x^3 + k3*x^4 + k4*x^5
///           = 1 + k0*r^2 + k1*r^4 + k2*r^6 + k3*r^8 + k4*r^10
/// </code>
/// evaluated via Horner's method on <c>x</c> (matching native's
/// <c>dng_vignette_radial_function::Evaluate</c>, which walks its
/// coefficient vector in reverse). Every pixel is multiplied by
/// <c>gain(x)</c> and the result is clipped to <c>&lt;= 1.0</c> (matching
/// native's <c>RefVignette32</c>, which never clips to a floor since gain is
/// always <c>&gt;= 1</c> for a well-formed vignette-brightening curve).</para>
///
/// <para><b>Simplifications versus native</b> (consistent with this port's
/// established pattern for lens-correction and per-row/column opcodes):
/// <list type="bullet">
/// <item>Native computes the optical center and max radius using
/// <c>negative.PixelAspectRatio()</c> to scale the vertical axis; this port
/// always assumes square pixels (<c>pixelAspectRatio == 1.0</c>), which is
/// true for the overwhelming majority of DNG files.</item>
/// <item>Native quantizes the gain curve into a 16-bit lookup table (with a
/// fixed-point radius accumulator) for performance; this port evaluates the
/// polynomial directly in double precision per pixel, which is at least as
/// accurate.</item>
/// <item>Native subtracts/restores a per-file <c>Stage3BlackLevel()</c>
/// black offset around the gain multiply when <c>Stage() &gt;= 2</c>; this
/// port always uses <c>blackLevel = 0</c>, matching the simplification
/// already used by the Delta/Scale/MapPolynomial opcodes — this only affects
/// legacy files with a nonzero Stage-3 black level.</item>
/// </list>
/// </para>
///
/// <para>This port applies the opcode in place to <see cref="PixelType.Float32"/>
/// images only (matching native's <c>BufferPixelType</c> override, which
/// always forces <c>ttFloat</c>). The same gain is applied uniformly to every
/// plane at a given pixel position (matching native's <c>RefVignette32</c>,
/// which reuses one mask value across all color planes).</para>
/// </summary>
public static class FixVignetteRadialOpcode
{
    public const int NumTerms = 5;

    /// <summary>Decoded radial vignette-correction parameters.</summary>
    public sealed class Params
    {
        /// <summary>5 gain-polynomial coefficients <c>[k0..k4]</c> (see class doc comment).</summary>
        public required double[] Coefficients { get; init; }

        /// <summary>Optical center, normalized to the image bounds ([0,1] on each axis).</summary>
        public required (double H, double V) Center { get; init; }

        /// <summary>True when every coefficient is zero (gain is identically 1 everywhere).</summary>
        public bool IsNop => Coefficients.All(static c => c == 0.0);
    }

    /// <summary>Decode a <c>FixVignetteRadial</c> opcode body (see class doc comment for the wire format).</summary>
    public static Params Decode(ReadOnlySpan<byte> body)
    {
        const int expectedBytes = NumTerms * 8 + 2 * 8;
        if (body.Length != expectedBytes)
            DngThrow.BadFormat($"FixVignetteRadial: body size {body.Length} != expected {expectedBytes}");

        int offset = 0;
        var coefficients = new double[NumTerms];
        for (int i = 0; i < NumTerms; i++)
        {
            double v = BinaryPrimitives.ReadDoubleBigEndian(body.Slice(offset, 8));
            offset += 8;
            if (!double.IsFinite(v))
                DngThrow.BadFormat("FixVignetteRadial: non-finite coefficient");
            coefficients[i] = v;
        }

        double centerH = BinaryPrimitives.ReadDoubleBigEndian(body.Slice(offset, 8));
        offset += 8;
        double centerV = BinaryPrimitives.ReadDoubleBigEndian(body.Slice(offset, 8));
        offset += 8;

        if (!double.IsFinite(centerH) || !double.IsFinite(centerV) ||
            centerH < 0.0 || centerH > 1.0 || centerV < 0.0 || centerV > 1.0)
            DngThrow.BadFormat($"FixVignetteRadial: invalid optical center ({centerH}, {centerV})");

        return new Params { Coefficients = coefficients, Center = (centerH, centerV) };
    }

    /// <summary>Evaluate <c>gain(x) = 1 + k0*x + k1*x^2 + ... + k4*x^5</c> via Horner's method.</summary>
    private static double EvaluateGain(double x, double[] k)
    {
        double sum = 0.0;
        for (int i = NumTerms - 1; i >= 0; i--)
            sum = x * (k[i] + sum);
        return sum + 1.0;
    }

    /// <summary>
    /// Apply the decoded radial vignette gain to <paramref name="image"/> in
    /// place. No-op if the parameters are a NOP (all coefficients zero).
    /// </summary>
    public static void Apply(SimpleImage image, Params p)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(p);

        if (image.PixelType != PixelType.Float32)
            DngThrow.NotYetImplemented(
                $"FixVignetteRadial: only Float32 images are supported by this port (got {image.PixelType})");

        if (p.IsNop) return;

        var bounds = image.Bounds;
        if (bounds.IsEmpty) return;

        // Optical center in pixel coordinates (lerp across the image bounds).
        double centerRow = bounds.T + p.Center.V * (bounds.B - bounds.T);
        double centerCol = bounds.L + p.Center.H * (bounds.R - bounds.L);

        // Max radius: distance from the center to the farthest corner,
        // assuming square pixels (pixelAspectRatio == 1.0 simplification).
        double maxDv = System.Math.Max(
            System.Math.Abs(centerRow - bounds.T), System.Math.Abs(centerRow - bounds.B));
        double maxDh = System.Math.Max(
            System.Math.Abs(centerCol - bounds.L), System.Math.Abs(centerCol - bounds.R));
        double maxRadius = System.Math.Sqrt(maxDv * maxDv + maxDh * maxDh);

        if (maxRadius <= 0.0) return;

        double invMaxRadius2 = 1.0 / (maxRadius * maxRadius);

        var buf = image.Buffer;
        var floats = MemoryMarshal.Cast<byte, float>(buf.AsByteSpan());
        var k = p.Coefficients;

        for (int row = bounds.T; row < bounds.B; row++)
        {
            double dv = (row + 0.5) - centerRow;
            double dv2 = dv * dv;

            for (int col = bounds.L; col < bounds.R; col++)
            {
                double dh = (col + 0.5) - centerCol;
                double r2 = (dv2 + dh * dh) * invMaxRadius2;
                if (r2 > 1.0) r2 = 1.0;

                double gain = EvaluateGain(r2, k);

                for (uint plane = 0; plane < image.Planes; plane++)
                {
                    long idx = buf.OffsetBytes(row, col, plane) / sizeof(float);
                    float v = floats[(int)idx] * (float)gain;
                    floats[(int)idx] = float.Min(v, 1.0f);
                }
            }
        }
    }
}
