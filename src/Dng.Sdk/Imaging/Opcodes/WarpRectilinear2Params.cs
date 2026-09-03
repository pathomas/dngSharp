using System.Buffers.Binary;
using Dng.Sdk.Errors;

namespace Dng.Sdk.Imaging.Opcodes;

/// <summary>
/// Decoded parameters for the <c>WarpRectilinear2</c> opcode (DNG 1.6+,
/// <see cref="OpcodeId.WarpRectilinear2"/>). Mirrors
/// <c>dng_warp_params_rectilinear</c> combined with the general 15-term
/// <c>dng_warp_params_radial</c> model — see
/// <c>dng_opcode_WarpRectilinear2::dng_opcode_WarpRectilinear2(dng_stream&amp;)</c>
/// in <c>dng_lens_correction.cpp</c>.
///
/// <para>This extends <see cref="WarpRectilinearParams"/> (which only
/// supports the restricted DNG 1.3 model: even radial powers r^0/r^2/r^4/r^6,
/// full [0,1] valid range, no reciprocal mode) with:
/// <list type="bullet">
/// <item>a general 15-term radial polynomial (<c>kr0..kr14</c>, both even and
/// odd powers of the normalized radius <c>r</c>, not just <c>r2</c>),</item>
/// <item>an optional "reciprocal" radial mapping mode
/// (<c>r_real = r_ideal / f(r_ideal)</c> instead of <c>r_real = r_ideal *
/// f(r_ideal)</c>),</item>
/// <item>a per-plane valid radius range <c>[minValidRadius,
/// maxValidRadius]</c> that <c>r</c> is clamped to before evaluating
/// <c>f(r)</c>.</item>
/// </list>
/// </para>
///
/// <para>Wire format (all big-endian; following this port's established
/// <see cref="WarpRectilinearParams"/> convention, the body has no leading
/// self-describing byte-count field — <c>DngOpcodeList</c>'s generic
/// <c>bodySize</c> framing already accounts for it):
/// <code>
///   uint32 planes
///   for each plane:
///     real64 kr[15]                 // f(r) = sum_i kr[i] * r^i
///     real64 kt0, kt1                // tangential coefficients
///     real64 minValidRadius, maxValidRadius
///   real64 centerH, centerV
///   uint32 useReciprocal            // 0 or 1, applies to all planes
/// </code>
/// </para>
///
/// <para><c>RadialRatio(plane, r2)</c> mirrors
/// <c>dng_warp_params_radial::EvaluateRatio</c>: clamp <c>r2</c> to
/// <c>[minValidRadius^2, maxValidRadius^2]</c>, take <c>r = sqrt(r2)</c>,
/// evaluate the 15-term polynomial via Horner's method on <c>r</c> (not
/// <c>r2</c> — the model allows odd powers), then return the reciprocal of
/// the result when <see cref="UseReciprocal"/> is set.</para>
/// </summary>
public sealed class WarpRectilinear2Params : IWarpRectilinearParams
{
    private const int MaxColorPlanes = 4;
    public const int RadialTerms = 15;

    /// <summary>Number of planes with distinct parameters (1, or equal to the image's plane count).</summary>
    public int Planes { get; private set; }

    /// <summary>15-term radial coefficients per plane: <c>[kr0..kr14]</c>.</summary>
    public double[][] Radial { get; }

    /// <summary>Tangential coefficients per plane: [kt0, kt1].</summary>
    public double[][] Tangential { get; }

    /// <summary>Per-plane valid radius range: [minValidRadius, maxValidRadius].</summary>
    public double[][] ValidRange { get; }

    /// <summary>Optical center in normalized [0,1] image-relative coordinates.</summary>
    public (double H, double V) Center { get; }

    /// <summary>When true, <c>r_real = r_ideal / f(r_ideal)</c> instead of <c>r_real = r_ideal * f(r_ideal)</c>.</summary>
    public bool UseReciprocal { get; }

    private WarpRectilinear2Params(
        int planes, double[][] radial, double[][] tangential, double[][] validRange,
        (double, double) center, bool useReciprocal)
    {
        Planes = planes;
        Radial = radial;
        Tangential = tangential;
        ValidRange = validRange;
        Center = center;
        UseReciprocal = useReciprocal;
    }

    /// <summary>Parse a <c>WarpRectilinear2</c> opcode body (see class doc comment for the wire format).</summary>
    public static WarpRectilinear2Params Decode(ReadOnlySpan<byte> body)
    {
        if (body.Length < 4)
            DngThrow.BadFormat("WarpRectilinear2 opcode body too short (missing plane count)");

        int offset = 0;
        uint planes = BinaryPrimitives.ReadUInt32BigEndian(body);
        offset += 4;

        if (planes == 0 || planes > MaxColorPlanes)
            DngThrow.BadFormat($"WarpRectilinear2: invalid plane count {planes}");

        const int perPlaneBytes = RadialTerms * 8 + 2 * 8 + 2 * 8; // radial + tangential + validRange
        int expectedBytes = 4 + (int)planes * perPlaneBytes + 2 * 8 + 4;
        if (body.Length != expectedBytes)
            DngThrow.BadFormat(
                $"WarpRectilinear2: body size {body.Length} doesn't match expected {expectedBytes} for {planes} plane(s)");

        var radial = new double[MaxColorPlanes][];
        var tangential = new double[MaxColorPlanes][];
        var validRange = new double[MaxColorPlanes][];

        for (int p = 0; p < planes; p++)
        {
            var kr = new double[RadialTerms];
            for (int i = 0; i < RadialTerms; i++)
                kr[i] = ReadReal64(body, ref offset);

            double kt0 = ReadReal64(body, ref offset);
            double kt1 = ReadReal64(body, ref offset);

            double minR = ReadReal64(body, ref offset);
            double maxR = ReadReal64(body, ref offset);

            if (!(minR >= 0.0 && minR < maxR && maxR <= 1.0))
                DngThrow.BadFormat($"WarpRectilinear2: invalid valid-radius range [{minR}, {maxR}] for plane {p}");

            radial[p] = kr;
            tangential[p] = [kt0, kt1];
            validRange[p] = [minR, maxR];
        }

        double centerH = ReadReal64(body, ref offset);
        double centerV = ReadReal64(body, ref offset);

        uint useReciprocal = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(offset, 4));
        offset += 4;

        return new WarpRectilinear2Params((int)planes, radial, tangential, validRange, (centerH, centerV), useReciprocal != 0);
    }

    private static double ReadReal64(ReadOnlySpan<byte> body, ref int offset)
    {
        double v = BinaryPrimitives.ReadDoubleBigEndian(body.Slice(offset, 8));
        offset += 8;
        return v;
    }

    /// <summary>
    /// Copy plane-0 parameters to any additional planes required by the
    /// image (mirrors <c>dng_warp_params_rectilinear::PropagateToAllPlanes</c>).
    /// </summary>
    public void PropagateToAllPlanes(int totalPlanes)
    {
        if (totalPlanes <= Planes) return;
        if (totalPlanes > MaxColorPlanes)
            DngThrow.BadFormat($"WarpRectilinear2: too many planes requested ({totalPlanes})");

        for (int p = Planes; p < totalPlanes; p++)
        {
            Radial[p] = (double[])Radial[0].Clone();
            Tangential[p] = (double[])Tangential[0].Clone();
            ValidRange[p] = (double[])ValidRange[0].Clone();
        }
        Planes = totalPlanes;
    }

    /// <summary>
    /// Radial correction is a NOP for this plane: <c>kr0 == 1</c> and every
    /// other radial term is zero (matches <c>dng_warp_params_radial::IsNOP</c>).
    /// </summary>
    public bool IsRadNop(int plane)
    {
        var r = Radial[plane];
        if (r[0] != 1.0) return false;
        for (int i = 1; i < RadialTerms; i++)
            if (r[i] != 0.0) return false;
        return true;
    }

    /// <summary>Tangential correction is a NOP for this plane: kt0 == kt1 == 0.</summary>
    public bool IsTanNop(int plane)
    {
        var t = Tangential[plane];
        return t[0] == 0.0 && t[1] == 0.0;
    }

    public bool IsNopAll()
    {
        for (int p = 0; p < Planes; p++)
            if (!IsRadNop(p) || !IsTanNop(p)) return false;
        return true;
    }

    /// <summary>
    /// Evaluate <c>f(r) = sum_i kr[i] * r^i</c> (15 terms, Horner's method on
    /// <c>r</c>, not <c>r2</c> — this general model allows odd powers unlike
    /// the restricted DNG 1.3 model), after clamping <c>r2</c> to
    /// <c>[minValidRadius^2, maxValidRadius^2]</c>. Returns <c>1/f(r)</c>
    /// instead when <see cref="UseReciprocal"/> is set. Mirrors
    /// <c>dng_warp_params_radial::EvaluateRatio</c>.
    /// </summary>
    public double RadialRatio(int plane, double r2)
    {
        var range = ValidRange[plane];
        double minR2 = range[0] * range[0];
        double maxR2 = range[1] * range[1];
        r2 = double.Clamp(r2, minR2, maxR2);

        double r = System.Math.Sqrt(r2);

        var kr = Radial[plane];
        double poly = kr[RadialTerms - 1];
        for (int i = RadialTerms - 2; i >= 0; i--)
            poly = poly * r + kr[i];

        return UseReciprocal ? 1.0 / poly : poly;
    }

    /// <summary>
    /// Evaluate the 2D tangential warp offset. Mirrors
    /// <c>dng_warp_params_rectilinear::EvaluateTangential</c> (identical
    /// formula to <see cref="WarpRectilinearParams.EvaluateTangential"/>).
    /// </summary>
    public (double TanH, double TanV) EvaluateTangential(
        int plane, double r2, double diffH, double diffV, double diffH2, double diffV2)
    {
        double kt0 = Tangential[plane][0];
        double kt1 = Tangential[plane][1];

        double tanV = kt0 * (r2 + 2.0 * diffV2) + 2.0 * kt1 * diffH * diffV;
        double tanH = kt1 * (r2 + 2.0 * diffH2) + 2.0 * kt0 * diffH * diffV;

        return (tanH, tanV);
    }
}
