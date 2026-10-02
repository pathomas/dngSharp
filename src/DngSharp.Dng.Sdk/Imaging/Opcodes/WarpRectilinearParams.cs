using System.Buffers.Binary;
using DngSharp.Dng.Sdk.Errors;
using DngSharp.Dng.Sdk.Tiff;

namespace DngSharp.Dng.Sdk.Imaging.Opcodes;

/// <summary>
/// Decoded parameters for the <c>WarpRectilinear</c> opcode (DNG 1.3+,
/// <see cref="OpcodeId.WarpRectilinear"/>). Mirrors
/// <c>dng_warp_params_rectilinear</c> restricted to the DNG 1.3 wire format
/// (radial terms at r^0/r^2/r^4/r^6 only, no reciprocal mode, full [0,1]
/// valid range) — see <c>dng_opcode_WarpRectilinear::dng_opcode_WarpRectilinear
/// (dng_stream&amp;)</c> in the native SDK.
///
/// <para>Defines a warp from corrected (destination) pixel coordinates to
/// uncorrected (source) pixel coordinates for each color plane:
/// <code>
///   dx = (xDst - xCenter) / maxDist
///   dy = (yDst - yCenter) / maxDist
///   r2 = dx^2 + dy^2
///   f(r) = k0 + k2*r^2 + k4*r^4 + k6*r^6
///   dxRad = dx * f(r), dyRad = dy * f(r)
///   dxTan = 2*kt0*dx*dy + kt1*(r2 + 2*dx^2)
///   dyTan = 2*kt1*dx*dy + kt0*(r2 + 2*dy^2)
///   xSrc = xCenter + (dxRad + dxTan) * maxDist
///   ySrc = yCenter + (dyRad + dyTan) * maxDist
/// </code>
/// </para>
/// </summary>
public sealed class WarpRectilinearParams : IWarpRectilinearParams
{
    private const int MaxColorPlanes = 4;

    /// <summary>Number of planes with distinct parameters (1, or equal to the image's plane count).</summary>
    public int Planes { get; private set; }

    /// <summary>Radial coefficients per plane: [k0, k2, k4, k6].</summary>
    public double[][] Radial { get; }

    /// <summary>Tangential coefficients per plane: [kt0, kt1].</summary>
    public double[][] Tangential { get; }

    /// <summary>Optical center in normalized [0,1] image-relative coordinates.</summary>
    public (double H, double V) Center { get; }

    private WarpRectilinearParams(int planes, double[][] radial, double[][] tangential, (double, double) center)
    {
        Planes = planes;
        Radial = radial;
        Tangential = tangential;
        Center = center;
    }

    /// <summary>
    /// Parse a <c>WarpRectilinear</c> opcode body. Format (all big-endian,
    /// per opcode-list framing):
    /// <code>
    ///   uint32 planes
    ///   for each plane: real64 k0, k2, k4, k6, kt0, kt1
    ///   real64 centerH, centerV
    /// </code>
    /// </summary>
    public static WarpRectilinearParams Decode(ReadOnlySpan<byte> body)
    {
        if (body.Length < 4)
            DngThrow.BadFormat("WarpRectilinear opcode body too short (missing plane count)");

        int offset = 0;
        uint planes = BinaryPrimitives.ReadUInt32BigEndian(body);
        offset += 4;

        if (planes == 0 || planes > MaxColorPlanes)
            DngThrow.BadFormat($"WarpRectilinear: invalid plane count {planes}");

        int expectedBytes = 4 + (int)planes * 6 * 8 + 2 * 8;
        if (body.Length != expectedBytes)
            DngThrow.BadFormat(
                $"WarpRectilinear: body size {body.Length} doesn't match expected {expectedBytes} for {planes} plane(s)");

        var radial = new double[MaxColorPlanes][];
        var tangential = new double[MaxColorPlanes][];

        for (int p = 0; p < planes; p++)
        {
            double k0 = ReadReal64(body, ref offset);
            double k2 = ReadReal64(body, ref offset);
            double k4 = ReadReal64(body, ref offset);
            double k6 = ReadReal64(body, ref offset);
            double kt0 = ReadReal64(body, ref offset);
            double kt1 = ReadReal64(body, ref offset);

            radial[p] = [k0, k2, k4, k6];
            tangential[p] = [kt0, kt1];
        }

        double centerH = ReadReal64(body, ref offset);
        double centerV = ReadReal64(body, ref offset);

        return new WarpRectilinearParams((int)planes, radial, tangential, (centerH, centerV));
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
            DngThrow.BadFormat($"WarpRectilinear: too many planes requested ({totalPlanes})");

        for (int p = Planes; p < totalPlanes; p++)
        {
            Radial[p] = (double[])Radial[0].Clone();
            Tangential[p] = (double[])Tangential[0].Clone();
        }
        Planes = totalPlanes;
    }

    /// <summary>Radial correction is a NOP for this plane: f(r) ≡ 1 (identity).</summary>
    public bool IsRadNop(int plane)
    {
        var r = Radial[plane];
        return r[0] == 1.0 && r[1] == 0.0 && r[2] == 0.0 && r[3] == 0.0;
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
    /// Evaluate f(r2) = k0 + k2*r2 + k4*r2^2 + k6*r2^3 via Horner's method on
    /// r2 (the squared normalized radius), matching
    /// <c>dng_warp_params_radial::EvaluateRatio</c> restricted to the DNG 1.3
    /// even-power-only model (odd terms are always zero, so evaluating the
    /// full septic Horner recurrence on r collapses to this cubic-in-r2
    /// form).
    /// </summary>
    public double RadialRatio(int plane, double r2)
    {
        r2 = double.Clamp(r2, 0.0, 1.0);
        var k = Radial[plane];
        // k0 + k2*r2 + k4*r2^2 + k6*r2^3
        return k[0] + r2 * (k[1] + r2 * (k[2] + r2 * k[3]));
    }

    /// <summary>
    /// Evaluate the 2D tangential warp offset. <paramref name="diffH"/>/<paramref name="diffV"/>
    /// are the pixel-aspect-scaled normalized distances from the optical
    /// center; <paramref name="diffH2"/>/<paramref name="diffV2"/> are their
    /// squares. Returns (tanH, tanV). Mirrors
    /// <c>dng_warp_params_rectilinear::EvaluateTangential</c>.
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
