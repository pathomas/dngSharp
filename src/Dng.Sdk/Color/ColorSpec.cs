using Dng.Sdk.Color.Cct;
using Dng.Sdk.Errors;
using Dng.Sdk.Math;

namespace Dng.Sdk.Color;

/// <summary>
/// Camera-space → XYZ_D50 color transform builder. Mirrors
/// <c>dng_color_spec</c>.
///
/// <para>For each scene the DNG SDK chooses an "interpolation weight"
/// <c>w ∈ [0, 1]</c> based on the as-shot illuminant's distance (in
/// <b>inverse CCT</b> — mireds, 10⁶/K) to each of the profile's calibration
/// illuminants. That weight blends the corresponding <c>ColorMatrix</c>,
/// <c>ForwardMatrix</c>, and <c>CameraCalibration</c> data. <b>Linear
/// interpolation in inverse CCT</b> (not in kelvin) is required by spec 5.4.2.</para>
///
/// <para>This class handles the case-analysis (1 / 2 / 3 illuminants) and
/// the <see cref="InterpolationWeight"/> calculation. The matrix-assembly
/// path (<c>FindXYZtoCamera</c>, <c>FindCameraToPCS</c>) follows in a future
/// pass once <see cref="ICameraProfileInterpolator"/> has full coverage of
/// hue-sat maps, look tables, and gain map evaluation; the spec semantics
/// the interpolator depends on are captured here.</para>
/// </summary>
public static class ColorSpec
{
    /// <summary>
    /// Compute the interpolation weight for an as-shot illuminant CCT given
    /// two calibration illuminant CCTs. Returns 1.0 if the as-shot is at
    /// (or above) the lower-CCT calibration, 0.0 if at/below the higher,
    /// and a linear blend in <b>inverse CCT</b> between them.
    ///
    /// <para>Per spec 5.4.2: weight is calculated as
    /// <c>w = (1/T - 1/T2) / (1/T1 - 1/T2)</c> where T1 ≤ T2 are the two
    /// calibration CCTs (in kelvin). The returned weight multiplies the
    /// matrix associated with T1; (1 - w) multiplies the T2 matrix.</para>
    /// </summary>
    /// <param name="asShotKelvin">CCT of the scene's white balance.</param>
    /// <param name="lowerCalibrationKelvin">Lower CCT calibration illuminant (T1).</param>
    /// <param name="upperCalibrationKelvin">Higher CCT calibration illuminant (T2).</param>
    /// <returns>Weight in [0, 1] for the T1 matrix.</returns>
    public static double InterpolationWeight(
        double asShotKelvin,
        double lowerCalibrationKelvin,
        double upperCalibrationKelvin)
    {
        if (asShotKelvin <= 0 || lowerCalibrationKelvin <= 0 || upperCalibrationKelvin <= 0)
            DngThrow.MatrixMath("ColorSpec: CCT must be positive");
        if (lowerCalibrationKelvin > upperCalibrationKelvin)
            DngThrow.MatrixMath("ColorSpec: T1 must be <= T2 for InterpolationWeight");

        // Inverse-CCT space (mireds).
        double a = 1.0 / asShotKelvin;
        double t1 = 1.0 / lowerCalibrationKelvin;
        double t2 = 1.0 / upperCalibrationKelvin;

        if (t1 == t2) return 1.0; // degenerate (identical illuminants) → all weight on T1

        // Larger mired = lower CCT = closer to T1; clamp to [0, 1].
        double w = (a - t2) / (t1 - t2);
        return DngMath.Pin(0.0, w, 1.0);
    }

    /// <summary>
    /// Spec section 5.4.2: when 3 illuminants are present and the as-shot
    /// CCT is closer to the third illuminant than the lower two, use the
    /// third illuminant on its own. Otherwise pick the two of {1, 2, 3}
    /// that bracket the as-shot CCT.
    ///
    /// <para>Returns the indices of the two calibration illuminants to blend
    /// (each in [0, 1, 2]). If both indices are the same, no interpolation —
    /// use that single illuminant's matrices.</para>
    /// </summary>
    public static (int Lower, int Upper) PickIlluminants(
        double asShotKelvin,
        ReadOnlySpan<double> calibrationKelvins)
    {
        if (calibrationKelvins.Length == 0)
            DngThrow.MatrixMath("ColorSpec: no calibration illuminants");
        if (calibrationKelvins.Length == 1)
            return (0, 0);

        // Sort indices by ascending CCT — DNG profiles aren't required to
        // ship illuminants in order.
        Span<int> order = stackalloc int[3];
        int n = System.Math.Min(calibrationKelvins.Length, 3);
        for (int i = 0; i < n; i++) order[i] = i;
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
                if (calibrationKelvins[order[j]] < calibrationKelvins[order[i]])
                    (order[i], order[j]) = (order[j], order[i]);

        // Find the bracket in sorted CCT order. Below lowest → both = lowest;
        // above highest → both = highest.
        if (asShotKelvin <= calibrationKelvins[order[0]])
            return (order[0], order[0]);
        if (asShotKelvin >= calibrationKelvins[order[n - 1]])
            return (order[n - 1], order[n - 1]);

        for (int i = 0; i < n - 1; i++)
            if (asShotKelvin >= calibrationKelvins[order[i]]
             && asShotKelvin <= calibrationKelvins[order[i + 1]])
                return (order[i], order[i + 1]);

        // Unreachable given the bracket guards above.
        return (order[0], order[n - 1]);
    }

    /// <summary>
    /// Convenience: convert an xy chromaticity to CCT (kelvin) via the
    /// Robertson table. Equivalent to <c>DngTemperature</c>'s constructor.
    /// </summary>
    public static double XyToKelvin(XyCoord xy) =>
        CctRobertson.XyToTemperatureTint(xy).Kelvin;
}
