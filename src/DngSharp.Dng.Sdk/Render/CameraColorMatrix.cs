using DngSharp.Dng.Sdk.Color;
using DngSharp.Dng.Sdk.Errors;
using DngSharp.Dng.Sdk.Imaging.Profile;
using DngSharp.Dng.Sdk.Math;

namespace DngSharp.Dng.Sdk.Render;

/// <summary>
/// Assembles the per-render <c>cameraToXYZ_D50</c> matrix from a camera
/// profile + as-shot illuminant. Mirrors the orchestration body of
/// <c>dng_color_spec::FindCameraToPCS</c>.
///
/// <para>The build follows spec ch. 5–6:
/// <list type="number">
///   <item>Pick the bracketing pair of calibration illuminants by inverse-CCT
///         distance (see <see cref="ColorSpec.PickIlluminants"/>).</item>
///   <item>Compute the interpolation weight in <b>inverse CCT</b>
///         (<see cref="ColorSpec.InterpolationWeight"/>).</item>
///   <item>Per the spec, prefer <c>ForwardMatrix</c> when present at <i>both</i>
///         calibrations — that path is more numerically stable because the
///         FM already targets D50. Otherwise invert the interpolated
///         <c>ColorMatrix</c> and apply <see cref="Bradford"/> chromatic
///         adaptation from the (interpolated) calibration white to D50.</item>
/// </list>
/// </para>
/// </summary>
public static class CameraColorMatrix
{
    /// <summary>
    /// Linearly interpolate two matrices with the same shape.
    /// </summary>
    public static DngMatrix Lerp(DngMatrix a, DngMatrix b, double tForA)
    {
        if (a.Rows != b.Rows || a.Cols != b.Cols)
            DngThrow.MatrixMath("Matrix.Lerp: shape mismatch");
        var r = new DngMatrix(a.Rows, a.Cols);
        for (int i = 0; i < a.Rows; i++)
            for (int j = 0; j < a.Cols; j++)
                r[i, j] = a[i, j] * tForA + b[i, j] * (1.0 - tForA);
        return r;
    }

    /// <summary>
    /// Build the camera→XYZ_D50 matrix for an as-shot CCT given a camera
    /// profile. Returns a 3×n matrix (n = color planes — typically 3 for RGB).
    ///
    /// <para>Throws <see cref="DngError.MatrixMath"/> if the profile has no
    /// calibration illuminants or required matrices.</para>
    /// </summary>
    public static DngMatrix BuildCameraToXyzD50(DngCameraProfile profile, double asShotKelvin)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Illuminants.Count == 0)
            DngThrow.MatrixMath("Profile has no calibration illuminants");

        // 1. Pick bracketing illuminants.
        Span<double> ccts = stackalloc double[profile.Illuminants.Count];
        for (int i = 0; i < profile.Illuminants.Count; i++)
            ccts[i] = profile.Illuminants[i].Kelvin;

        var (loIdx, hiIdx) = ColorSpec.PickIlluminants(asShotKelvin, ccts);
        var lo = profile.Illuminants[loIdx];
        var hi = profile.Illuminants[hiIdx];

        // 2. Weight on lower-CCT illuminant.
        double w = loIdx == hiIdx
            ? 1.0
            : ColorSpec.InterpolationWeight(asShotKelvin, lo.Kelvin, hi.Kelvin);

        // 3. Prefer ForwardMatrix path when present at both endpoints.
        bool useForward = lo.ForwardMatrix is not null
                       && (loIdx == hiIdx || hi.ForwardMatrix is not null);
        if (useForward)
        {
            var fm = loIdx == hiIdx
                ? lo.ForwardMatrix!
                : Lerp(lo.ForwardMatrix!, hi.ForwardMatrix!, w);
            // ForwardMatrix already maps camera → XYZ_D50, modulo the
            // camera-side calibration matrices. The render path applies
            // CameraCalibration and AsShotNeutral diagonals before this matrix
            // (built outside this function); we return the FM directly.
            return fm;
        }

        // 4. Fallback: invert interpolated ColorMatrix and adapt to D50.
        if (lo.ColorMatrix is null || (loIdx != hiIdx && hi.ColorMatrix is null))
            DngThrow.MatrixMath("Profile lacks ColorMatrix at one or both calibrations");

        var cm = loIdx == hiIdx
            ? lo.ColorMatrix!
            : Lerp(lo.ColorMatrix!, hi.ColorMatrix!, w);

        // ColorMatrix maps XYZ_calib → camera. Invert (or pseudo-inverse for
        // 3×4 / 4×3 cases — TODO Phase 8) and adapt to D50.
        if (cm.Rows != cm.Cols)
            DngThrow.MatrixMath($"Non-square ColorMatrix ({cm.Rows}×{cm.Cols}) requires pseudo-inverse — deferred");

        var camToCalib = DngMatrix.Invert(cm);

        // Bradford adapt from interpolated calibration white to D50.
        var loWhite = lo.WhitePoint.IsValid ? lo.WhitePoint : DngTemperatureToXy(lo.Kelvin);
        var hiWhite = hi.WhitePoint.IsValid ? hi.WhitePoint : DngTemperatureToXy(hi.Kelvin);
        var calibWhite = loIdx == hiIdx ? loWhite : LerpXy(loWhite, hiWhite, w);

        var adapt = Bradford.MakeAdaptationMatrix(calibWhite, XyCoord.D50);
        return adapt * camToCalib;
    }

    private static XyCoord DngTemperatureToXy(double kelvin)
    {
        if (kelvin <= 0) return XyCoord.D50;
        return Color.Cct.CctRobertson.TemperatureTintToXy(kelvin, 0);
    }

    private static XyCoord LerpXy(XyCoord a, XyCoord b, double tForA) =>
        new(a.X * tForA + b.X * (1.0 - tForA),
            a.Y * tForA + b.Y * (1.0 - tForA));

    /// <summary>
    /// Resolve the effective <see cref="HueSatMap"/> for an as-shot CCT,
    /// interpolating between calibration illuminants exactly as
    /// <see cref="BuildCameraToXyzD50"/> does for the color matrices. Returns
    /// <c>null</c> when the profile has no <c>ProfileHueSatMapData</c> tables
    /// (most third-party/legacy profiles).
    /// </summary>
    public static HueSatMap? ResolveHueSatMap(DngCameraProfile profile, double asShotKelvin)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Illuminants.Count == 0) return null;

        Span<double> ccts = stackalloc double[profile.Illuminants.Count];
        for (int i = 0; i < profile.Illuminants.Count; i++)
            ccts[i] = profile.Illuminants[i].Kelvin;

        var (loIdx, hiIdx) = ColorSpec.PickIlluminants(asShotKelvin, ccts);
        var lo = profile.Illuminants[loIdx];
        var hi = profile.Illuminants[hiIdx];

        if (lo.HueSatMap is null) return hi.HueSatMap; // Single-illuminant profile, or lo lacks a table.
        if (loIdx == hiIdx || hi.HueSatMap is null) return lo.HueSatMap;

        double w = ColorSpec.InterpolationWeight(asShotKelvin, lo.Kelvin, hi.Kelvin);
        return HueSatMap.Interpolate(lo.HueSatMap, hi.HueSatMap, w);
    }
}
