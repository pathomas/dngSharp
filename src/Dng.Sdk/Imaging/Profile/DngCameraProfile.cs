using Dng.Sdk.Color;
using Dng.Sdk.Hashing;
using Dng.Sdk.Math;
using Dng.Sdk.Render;

namespace Dng.Sdk.Imaging.Profile;

/// <summary>
/// One DNG camera profile. Mirrors <c>dng_camera_profile</c>. A profile
/// describes how camera-native RGB maps to scene-referred CIE XYZ_D50, plus
/// optional creative-rendering tables (HSV, tone curve, look table) and
/// gain map.
///
/// <para>Each profile is calibrated for 1, 2, or 3 illuminants. The render
/// pipeline interpolates matrices between calibrations using
/// <see cref="ColorSpec.InterpolationWeight"/> in <b>inverse CCT</b>.</para>
/// </summary>
public sealed class DngCameraProfile
{
    public string Name { get; set; } = string.Empty;
    public string CopyrightInfo { get; set; } = string.Empty;
    public DngFingerprint CalibrationSignature { get; set; }
    public string EmbedPolicy { get; set; } = string.Empty;

    /// <summary>
    /// Calibration illuminants, by index. Each entry pairs an xy
    /// chromaticity (or null when the profile uses <c>CalibrationIlluminantN</c>
    /// EXIF light-source codes) with the camera→XYZ_D50 matrices.
    /// </summary>
    public List<CalibrationIlluminant> Illuminants { get; } = [];

    public DngVector? AsShotNeutralOverride { get; set; }

    /// <summary>HDR vs SDR (DNG 1.7+).</summary>
    public ProfileDynamicRange DynamicRange { get; set; } = ProfileDynamicRange.StandardDynamicRange;

    /// <summary>
    /// Optional DNG <c>ProfileToneCurve</c> expressed as piecewise-linear
    /// (input, output) control-point pairs.
    /// </summary>
    public (double Input, double Output)[]? ToneCurve { get; set; }

    /// <summary>
    /// For <see cref="ProfileDynamicRange.HighDynamicRange"/> profiles, this
    /// is the hint that drives the HDR encode/decode <c>f(x) = x(256+x) / (256(1+x))</c>
    /// around table lookups (spec ch. 6).
    /// </summary>
    public float HintMaxOutputValue { get; set; } = 1.0f;

    /// <summary>SDR/HDR group name (DNG 1.7+) for paired-profile selection.</summary>
    public string GroupName { get; set; } = string.Empty;

    // ---- Gain table map (PrecedenceRule below) -----------------------------
    private byte[]? _gainTable;
    private GainTableSource _gainTableSource = GainTableSource.None;

    /// <summary>
    /// Where the currently-installed gain table came from. <see cref="GainTableSource.None"/>
    /// means no map present.
    /// </summary>
    public GainTableSource GainTableSource => _gainTableSource;

    /// <summary>Read-only handle to the gain table payload (or empty).</summary>
    public ReadOnlySpan<byte> GainTable => _gainTable ?? [];

    /// <summary>
    /// Install a gain table, observing the precedence rule:
    /// <c>Camera Profile IFD &gt; IFD 0 &gt; Raw IFD legacy</c>.
    /// A higher-priority source displaces a lower; a lower source is
    /// silently dropped if a higher is already installed.
    /// </summary>
    public void SetGainTable(GainTableSource source, ReadOnlySpan<byte> payload)
    {
        if (source == GainTableSource.None)
        {
            _gainTable = null;
            _gainTableSource = GainTableSource.None;
            return;
        }

        // Only install if the new source has strictly higher precedence than
        // what's already there.
        if (source <= _gainTableSource) return;

        _gainTable = payload.ToArray();
        _gainTableSource = source;
    }
}

/// <summary>
/// One calibration point of a camera profile. Mirrors the per-illuminant
/// fields of <c>dng_camera_profile</c>.
/// </summary>
public sealed class CalibrationIlluminant
{
    /// <summary>Calibration illuminant CCT in kelvin. 0 means "unknown".</summary>
    public double Kelvin { get; set; }

    /// <summary>The illuminant's xy chromaticity (derived from <see cref="Kelvin"/> when not explicit).</summary>
    public XyCoord WhitePoint { get; set; }

    /// <summary>3×n camera→XYZ matrix.</summary>
    public DngMatrix? ColorMatrix { get; set; }

    /// <summary>3×n forward (camera→XYZ_D50) matrix when present. Preferred over ColorMatrix.</summary>
    public DngMatrix? ForwardMatrix { get; set; }

    /// <summary>n×n camera calibration matrix.</summary>
    public DngMatrix? CameraCalibration { get; set; }

    /// <summary>Reduction matrix for non-square color matrices.</summary>
    public DngMatrix? ReductionMatrix { get; set; }

    /// <summary>
    /// Optional <c>ProfileHueSatMapData</c> table for this calibration
    /// illuminant (spec 6.3.7). When two or three illuminants each carry a
    /// table, the render path interpolates between them the same way it
    /// interpolates <see cref="ForwardMatrix"/>/<see cref="ColorMatrix"/> —
    /// by inverse-CCT weight (see <see cref="Dng.Sdk.Render.CameraColorMatrix"/>).
    /// </summary>
    public HueSatMap? HueSatMap { get; set; }
}
