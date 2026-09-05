using System.Buffers.Binary;
using DngSharp.Dng.Sdk.Errors;

namespace DngSharp.Dng.Sdk.Imaging.Opcodes;

/// <summary>
/// Decoded parameters for the <c>WarpFisheye</c> opcode (<see cref="OpcodeId.WarpFisheye"/>,
/// id 2). Mirrors <c>dng_warp_params_fisheye</c> in <c>dng_lens_correction.h</c>/<c>.cpp</c>:
/// a radial-only lens-distortion model (no tangential component) built on
/// <c>atan</c> of the normalized radius rather than a plain polynomial of
/// <c>r</c>/<c>r2</c>.
///
/// <para>Radial warp math (from the <c>dng_warp_params_fisheye::fRadParams</c>
/// doc comment): let <c>kr0..kr3</c> be this plane's 4 coefficients, and let
/// <c>r</c> be the Euclidean distance (normalized so the farthest image
/// corner from the optical center is at <c>r == 1</c>) from a corrected
/// (destination) pixel to the optical center. With <c>t = atan(r)</c>:
/// <code>
///   rWarp = kr0*t + kr1*t^3 + kr2*t^5 + kr3*t^7
///   ratio = rWarp / r
/// </code>
/// the uncorrected (source) pixel position is then
/// <c>center + (dst - center) * ratio</c> along each axis. <c>kr0..kr3</c>
/// must define a strictly increasing function of <c>r</c> for the model to
/// be well-formed (native does not verify this at decode time, and neither
/// does this port — an invalid/non-monotonic curve just produces a
/// visually-wrong warp, not a decode error, matching native's
/// <c>dng_warp_params_fisheye::IsValid</c> which only checks coefficient
/// counts and the shared center-range check).</para>
///
/// <para>Unlike <see cref="WarpRectilinearParams"/>/<see cref="WarpRectilinear2Params"/>,
/// this model has <b>no tangential (lateral chromatic aberration) component</b>
/// — <see cref="IsTanNop"/> always returns <c>true</c> and
/// <see cref="EvaluateTangential"/> is never called by <see cref="LensWarpFilter"/>
/// as a result (matches native's <c>dng_warp_params_fisheye::EvaluateTangential</c>,
/// which throws a program error since it should be unreachable). Also unlike
/// the rectilinear models, <see cref="IsRadNop"/> <b>always returns
/// <c>false</c></b> (matches native's <c>dng_warp_params_fisheye::IsRadNOP</c>)
/// — the fisheye correction curve can never be an exact identity warp for a
/// finite <c>r</c>, so the opcode always resamples the image when present.</para>
///
/// <para>Wire format (all big-endian; following this port's established
/// <see cref="WarpRectilinearParams"/>/<see cref="WarpRectilinear2Params"/>
/// convention — no leading self-describing byte-count field, since
/// <c>DngOpcodeList</c>'s generic <c>bodySize</c> framing already accounts
/// for it):
/// <code>
///   uint32 planes
///   for each plane: real64 kr0, kr1, kr2, kr3
///   real64 centerH, centerV
/// </code>
/// </para>
/// </summary>
public sealed class WarpFisheyeParams : IWarpRectilinearParams
{
    private const int MaxColorPlanes = 4;
    public const int RadialTerms = 4;

    /// <summary>Number of planes with distinct parameters (1, or equal to the image's plane count).</summary>
    public int Planes { get; private set; }

    /// <summary>4-term radial coefficients per plane: <c>[kr0..kr3]</c>.</summary>
    public double[][] Radial { get; }

    /// <summary>Optical center in normalized [0,1] image-relative coordinates.</summary>
    public (double H, double V) Center { get; }

    private WarpFisheyeParams(int planes, double[][] radial, (double, double) center)
    {
        Planes = planes;
        Radial = radial;
        Center = center;
    }

    /// <summary>Parse a <c>WarpFisheye</c> opcode body (see class doc comment for the wire format).</summary>
    public static WarpFisheyeParams Decode(ReadOnlySpan<byte> body)
    {
        if (body.Length < 4)
            DngThrow.BadFormat("WarpFisheye opcode body too short (missing plane count)");

        int offset = 0;
        uint planes = BinaryPrimitives.ReadUInt32BigEndian(body);
        offset += 4;

        if (planes == 0 || planes > MaxColorPlanes)
            DngThrow.BadFormat($"WarpFisheye: invalid plane count {planes}");

        int expectedBytes = 4 + (int)planes * RadialTerms * 8 + 2 * 8;
        if (body.Length != expectedBytes)
            DngThrow.BadFormat(
                $"WarpFisheye: body size {body.Length} doesn't match expected {expectedBytes} for {planes} plane(s)");

        var radial = new double[MaxColorPlanes][];

        for (int p = 0; p < planes; p++)
        {
            var kr = new double[RadialTerms];
            for (int i = 0; i < RadialTerms; i++)
            {
                double v = BinaryPrimitives.ReadDoubleBigEndian(body.Slice(offset, 8));
                offset += 8;
                if (!double.IsFinite(v))
                    DngThrow.BadFormat($"WarpFisheye: non-finite coefficient in plane {p}");
                kr[i] = v;
            }
            radial[p] = kr;
        }

        double centerH = BinaryPrimitives.ReadDoubleBigEndian(body.Slice(offset, 8));
        offset += 8;
        double centerV = BinaryPrimitives.ReadDoubleBigEndian(body.Slice(offset, 8));
        offset += 8;

        if (!double.IsFinite(centerH) || !double.IsFinite(centerV) ||
            centerH < 0.0 || centerH > 1.0 || centerV < 0.0 || centerV > 1.0)
            DngThrow.BadFormat($"WarpFisheye: invalid optical center ({centerH}, {centerV})");

        return new WarpFisheyeParams((int)planes, radial, (centerH, centerV));
    }

    /// <summary>
    /// Copy plane-0 parameters to any additional planes required by the
    /// image (mirrors <c>dng_warp_params_fisheye::PropagateToAllPlanes</c>).
    /// </summary>
    public void PropagateToAllPlanes(int totalPlanes)
    {
        if (totalPlanes <= Planes) return;
        if (totalPlanes > MaxColorPlanes)
            DngThrow.BadFormat($"WarpFisheye: too many planes requested ({totalPlanes})");

        for (int p = Planes; p < totalPlanes; p++)
            Radial[p] = (double[])Radial[0].Clone();

        Planes = totalPlanes;
    }

    /// <summary>The fisheye radial model can never be an exact identity warp for a finite radius.</summary>
    public bool IsRadNop(int plane) => false;

    /// <summary>This model has no tangential component — always a NOP.</summary>
    public bool IsTanNop(int plane) => true;

    public bool IsNopAll()
    {
        for (int p = 0; p < Planes; p++)
            if (!IsRadNop(p) || !IsTanNop(p)) return false;
        return true;
    }

    /// <summary>
    /// Evaluate <c>ratio = (kr0*t + kr1*t^3 + kr2*t^5 + kr3*t^7) / r</c> where
    /// <c>t = atan(r)</c>, <c>r = sqrt(r2)</c>. Returns <c>1.0</c> when
    /// <c>r2</c> is extremely close to zero (matches native's epsilon guard
    /// against a 0/0 division in <c>dng_warp_params_fisheye::EvaluateRatio</c>).
    /// </summary>
    public double RadialRatio(int plane, double r2)
    {
        const double eps = 1.0e-12;
        if (r2 < eps) return 1.0;

        double r = System.Math.Sqrt(r2);
        double t = System.Math.Atan(r);
        double t2 = t * t;

        var k = Radial[plane];
        double rWarp = t * (k[0] + t2 * (k[1] + t2 * (k[2] + t2 * k[3])));

        return rWarp / r;
    }

    /// <summary>
    /// Unreachable: the fisheye model has no tangential component
    /// (<see cref="IsTanNop"/> always returns <c>true</c>, so
    /// <see cref="LensWarpFilter"/> never calls this). Mirrors native's
    /// <c>dng_warp_params_fisheye::EvaluateTangential</c>, which throws a
    /// program error for the same reason.
    /// </summary>
    public (double TanH, double TanV) EvaluateTangential(
        int plane, double r2, double diffH, double diffV, double diffH2, double diffV2)
    {
        DngThrow.ProgramError("WarpFisheye: EvaluateTangential is unreachable (this model has no tangential component)");
        return (0.0, 0.0);
    }
}
