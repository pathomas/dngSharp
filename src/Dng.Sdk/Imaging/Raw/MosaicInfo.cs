using Dng.Sdk.Errors;
using Dng.Sdk.Primitives;

namespace Dng.Sdk.Imaging.Raw;

/// <summary>
/// CFA (Color Filter Array) mosaic pattern. Mirrors a subset of
/// <c>dng_mosaic_info</c>. Describes how the sensor's per-pixel filter is
/// laid out so the demosaicer can reconstruct full-color pixels.
///
/// <para><b>DNG 1.7.1 — <see cref="ColumnInterleaveFactor"/>:</b> combined
/// with <see cref="RowInterleaveFactor"/>, this splits a Bayer image into
/// 4 monochrome sub-images. Spec ch. 4: writers MUST set
/// <c>DNGBackwardVersion</c> to ≥ 1.7.1 when either factor is &gt; 1.</para>
/// </summary>
public sealed class MosaicInfo
{
    /// <summary>CFA repeat-pattern dimensions (rows, cols). Typical Bayer = (2, 2).</summary>
    public (uint Rows, uint Cols) Pattern { get; set; } = (1, 1);

    /// <summary>
    /// CFA pattern bytes. <c>0=Red, 1=Green, 2=Blue, 3=Cyan, 4=Magenta,
    /// 5=Yellow, 6=White</c>. Indexed row-major over <see cref="Pattern"/>.
    /// </summary>
    public byte[] CfaPlaneColor { get; set; } = [];

    /// <summary>BayerGreenSplit (tag 50723) — green-channel split factor.</summary>
    public uint BayerGreenSplit { get; set; }

    /// <summary>
    /// Number of times the CFA pattern repeats vertically per logical
    /// "super-row" (tag 50745). Defaults to 1.
    /// </summary>
    public uint RowInterleaveFactor { get; set; } = 1;

    /// <summary>
    /// DNG 1.7.1 — column interleave factor (tag 52547). Combined with
    /// <see cref="RowInterleaveFactor"/> to split sensor data into
    /// sub-images that can be coded as separate monochrome streams.
    /// </summary>
    public uint ColumnInterleaveFactor { get; set; } = 1;

    /// <summary>
    /// True if either interleave factor is &gt; 1 — caller must reject the
    /// file if its <c>DNGBackwardVersion</c> &lt; 1.7.1.
    /// </summary>
    public bool RequiresDng171 => RowInterleaveFactor > 1 || ColumnInterleaveFactor > 1;

    /// <summary>
    /// Validate the basic geometry — interleave factors must evenly divide
    /// the image dimensions (otherwise sub-image splitting is undefined).
    /// </summary>
    public void Validate(DngPoint imageSize)
    {
        if (RowInterleaveFactor == 0 || ColumnInterleaveFactor == 0)
            DngThrow.BadFormat("Interleave factor must be >= 1");
        if (imageSize.V % RowInterleaveFactor != 0)
            DngThrow.BadFormat($"RowInterleaveFactor={RowInterleaveFactor} doesn't divide height {imageSize.V}");
        if (imageSize.H % ColumnInterleaveFactor != 0)
            DngThrow.BadFormat($"ColumnInterleaveFactor={ColumnInterleaveFactor} doesn't divide width {imageSize.H}");
    }
}
