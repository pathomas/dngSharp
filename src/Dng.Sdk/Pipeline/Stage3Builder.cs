using Dng.Sdk.Imaging;
using Dng.Sdk.Imaging.Raw;
using Dng.Sdk.Pixels;
using Dng.Sdk.Tiff;

namespace Dng.Sdk.Pipeline;

/// <summary>
/// Stage 2 → Stage 3 transition. Mirrors the relevant body of
/// <c>dng_negative::BuildStage3Image</c>.
///
/// <para>For <see cref="Photometric.LinearRaw"/> and <see cref="Photometric.Rgb"/>
/// images this is a no-op passthrough. For Bayer CFA images,
/// <see cref="DemosaicBilinear"/> is invoked when a
/// <see cref="MosaicInfo"/> is supplied.</para>
///
/// <para>OpcodeList3 (run after demosaic on Stage 3 values) is dispatched
/// separately by the orchestrator.</para>
/// </summary>
public static class Stage3Builder
{
    /// <summary>
    /// Build a Stage-3 image from a Stage-2 image.
    ///
    /// <para>When <paramref name="photometric"/> is <see cref="Photometric.LinearRaw"/>
    /// or <see cref="Photometric.Rgb"/> the input is returned unchanged (zero-copy
    /// passthrough).</para>
    ///
    /// <para>When <paramref name="photometric"/> is <see cref="Photometric.Cfa"/>
    /// and <paramref name="mosaic"/> is non-null, bilinear demosaic is run via
    /// <see cref="DemosaicBilinear"/>.</para>
    /// </summary>
    /// <param name="stage2">Stage-2 image (camera-space [0,1] float).</param>
    /// <param name="photometric">Photometric interpretation of the main IFD.</param>
    /// <param name="mosaic">CFA mosaic info (required for Bayer CFA; ignored otherwise).</param>
    /// <returns>The Stage-3 image. May be the same instance as <paramref name="stage2"/>.</returns>
    public static DngImage Build(DngImage stage2, Photometric photometric, MosaicInfo? mosaic = null)
    {
        ArgumentNullException.ThrowIfNull(stage2);
        return photometric switch
        {
            Photometric.LinearRaw => stage2,
            Photometric.Rgb       => stage2,
            Photometric.Cfa when mosaic is not null =>
                DemosaicBilinear.Build(stage2, mosaic),
            _ => throw new NotSupportedException(
                     $"Stage3Builder: demosaic for photometric={photometric} is not supported. "
                     + "Supported today: LinearRaw, RGB passthrough; Bayer CFA with a MosaicInfo."),
        };
    }

    /// <summary>
    /// Returns true when <see cref="Build"/> can handle the given photometric
    /// without throwing.
    /// </summary>
    public static bool CanBuild(Photometric photometric, MosaicInfo? mosaic = null) =>
        photometric is Photometric.LinearRaw or Photometric.Rgb
        || (photometric == Photometric.Cfa && mosaic is not null);
}
