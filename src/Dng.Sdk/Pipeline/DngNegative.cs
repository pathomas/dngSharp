using Dng.Sdk.Color;
using Dng.Sdk.Hashing;
using Dng.Sdk.Imaging;
using Dng.Sdk.Imaging.Opcodes;
using Dng.Sdk.Imaging.Profile;
using Dng.Sdk.Imaging.Raw;
using Dng.Sdk.Math;
using Dng.Sdk.Metadata;
using Dng.Sdk.Metadata.Exif;
using Dng.Sdk.Metadata.Iptc;
using Dng.Sdk.Metadata.Xmp;
using Dng.Sdk.Primitives;
using Dng.Sdk.Render;

namespace Dng.Sdk.Pipeline;

/// <summary>
/// The central object that holds everything about one DNG image: pixel data
/// at each pipeline stage, opcode lists, camera profiles, metadata. Mirrors
/// <c>dng_negative</c>.
///
/// <para>The C++ class is the SDK's biggest single file (~175 KB) and owns
/// far more than this port covers today. This skeleton carries the slots
/// that the rest of Phase 6 (linearization, stage assembly) actually fills;
/// long-tail accessors land at point of use as later phases need them.</para>
///
/// <para><b>Stage model.</b> The render pipeline runs in three stages
/// (spec ch. 5–6):
/// <list type="number">
///   <item><b>Stage 1</b> — raw sensor data as written to the file (camera
///         color space, post-LinearizationTable but pre-black-subtract).</item>
///   <item><b>Stage 2</b> — linearized [0, 1] floating-point camera-space
///         values after OpcodeList1 → LinearizationTable → black subtract →
///         WhiteLevel rescale → clip → OpcodeList2.</item>
///   <item><b>Stage 3</b> — demosaiced [0, 1] floating-point camera-space
///         values after demosaic → OpcodeList3. Ready for color conversion.</item>
/// </list>
/// </para>
/// </summary>
public sealed class DngNegative
{
    public DngHost Host { get; }

    public DngNegative(DngHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        Host = host;
    }

    // ---- Geometry ----------------------------------------------------------

    /// <summary>Full sensor area, including masked rows/columns (ActiveArea is a sub-rect).</summary>
    public DngRect SensorArea { get; set; }

    /// <summary>Active (usable) area of the sensor. Crops away masked pixels.</summary>
    public DngRect ActiveArea { get; set; }

    /// <summary>The DefaultCropOrigin/Size rect (clean image after lens correction).</summary>
    public DngRect DefaultCropArea { get; set; }

    public DngOrientation Orientation { get; set; } = DngOrientation.FromAdobe(DngOrientation.Normal);

    // ---- Color setup -------------------------------------------------------

    public uint ColorPlanes { get; set; } = 1;
    public List<DngCameraProfile> Profiles { get; } = [];
    public LinearizationInfo Linearization { get; set; } = new();
    public MosaicInfo? Mosaic { get; set; }   // null when not a CFA image

    // ---- Shared / metadata -------------------------------------------------
    public DngShared Shared { get; set; } = new();
    public DngExif Exif { get; set; } = new();
    public DngIptc? Iptc { get; set; }
    public DngXmpPacket? Xmp { get; set; }

    // ---- Fingerprints ------------------------------------------------------
    public DngFingerprint OriginalRawFileDigest { get; set; }
    public DngFingerprint RawDataUniqueId { get; set; }

    // ---- Opcode lists ------------------------------------------------------
    public DngOpcodeList OpcodeList1 { get; set; } = new(stage: 1);
    public DngOpcodeList OpcodeList2 { get; set; } = new(stage: 2);
    public DngOpcodeList OpcodeList3 { get; set; } = new(stage: 3);

    // ---- Stage images ------------------------------------------------------
    public DngImage? Stage1Image { get; set; }
    public DngImage? Stage2Image { get; set; }
    public DngImage? Stage3Image { get; set; }

    /// <summary>
    /// Pick the camera profile to use for rendering. Default policy: the
    /// first profile in <see cref="Profiles"/>; SDR is preferred over HDR
    /// when both are present (unless the host has opted into HDR via
    /// <see cref="DngHost.PreviewMode"/> = false and a paired profile group).
    /// Real selection logic lands in Phase 8.
    /// </summary>
    public DngCameraProfile? SelectProfile()
    {
        if (Profiles.Count == 0) return null;
        // Prefer SDR when both SDR and HDR exist in the same group.
        var sdr = Profiles.Find(p => p.DynamicRange == ProfileDynamicRange.StandardDynamicRange);
        return sdr ?? Profiles[0];
    }

    /// <summary>
    /// As-shot illuminant CCT in kelvin. Computed from the AsShotNeutral or
    /// AsShotWhiteXY tag (mutually exclusive — enforced by
    /// <see cref="DngShared.SetAsShotNeutral"/>/<see cref="DngShared.SetAsShotWhiteXy"/>).
    ///
    /// <para>For AsShotWhiteXY the conversion is direct via Robertson. For
    /// AsShotNeutral this mirrors spec 5.4.2's fixed-point iteration: solve
    /// for the CCT whose interpolated profile matrix projects the normalized
    /// camera-space neutral back to the matching xy chromaticity. If a
    /// profile is not yet available, fall back to a daylight default of
    /// 6500 K.</para>
    /// </summary>
    public double? EstimateAsShotKelvin()
    {
        if (Shared.AsShotWhiteXy is { } xy)
            return ColorSpec.XyToKelvin(xy);
        if (Shared.AsShotNeutral is { } neutral)
        {
            var profile = SelectProfile();
            return profile is null
                ? 6500.0
                : SolveAsShotNeutralKelvin(neutral, profile);
        }
        return null;
    }

    private double SolveAsShotNeutralKelvin(DngVector neutral, DngCameraProfile profile)
    {
        ArgumentNullException.ThrowIfNull(neutral);
        ArgumentNullException.ThrowIfNull(profile);

        if (neutral.Count != 3)
            Errors.DngThrow.MatrixMath($"AsShotNeutral must be a 3-vector, got {neutral.Count}");

        var normalizedNeutral = new DngVector(neutral);
        double maxEntry = normalizedNeutral.MaxEntry();
        if (maxEntry <= 0.0)
            return 6500.0;

        normalizedNeutral.Scale(1.0 / maxEntry);

        double currentKelvin = 6500.0;

        for (int iteration = 0; iteration < 30; iteration++)
        {
            var cameraToXyzD50 = CameraColorMatrix.BuildCameraToXyzD50(profile, currentKelvin);
            if (cameraToXyzD50.Rows != 3 || cameraToXyzD50.Cols != 3)
                Errors.DngThrow.MatrixMath(
                    $"AsShotNeutral solver requires a 3×3 matrix, got {cameraToXyzD50.Rows}×{cameraToXyzD50.Cols}");

            var neutralToXyz = DngMatrix.Invert(cameraToXyzD50);
            var xyz = neutralToXyz * normalizedNeutral;
            var xy = XyCoord.FromXyz(xyz);
            double newKelvin = Color.Cct.CctRobertson.XyToTemperatureTint(xy).Kelvin;

            if (System.Math.Abs(newKelvin - currentKelvin) < 0.5)
                return newKelvin;

            currentKelvin = newKelvin;
        }

        return currentKelvin;
    }
}
