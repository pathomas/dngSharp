using Dng.Sdk.Imaging.Raw;
using Dng.Sdk.Metadata;
using Dng.Sdk.Pixels;
using Dng.Sdk.Tiff;

namespace Dng.Sdk.Writer;

/// <summary>
/// Per-spec features that bump the required <c>DNGBackwardVersion</c>.
/// Mirrors the <c>SetBackwardVersion</c> logic in <c>dng_image_writer</c>.
/// Each entry is the minimum version a reader must claim in order to
/// understand the corresponding feature.
/// </summary>
public static class DngBackwardVersion
{
    /// <summary>
    /// Compute the minimum reader version a host needs to consume a file
    /// that uses the given features. Returns at least <see cref="DngVersion.V1_0_0"/>.
    /// </summary>
    public static DngVersion Compute(DngBackwardVersionInputs inputs)
    {
        var v = DngVersion.V1_0_0;

        // 1.3.0.0 — opcode lists present and required
        if (inputs.HasOpcodes) v = Max(v, DngVersion.V1_3_0);

        // 1.4.0.0 — float pixel data, deflate, lossy JPEG, proxy DNG
        if (inputs.UsesFloatPixels
            || inputs.Compression == Compression.Deflate
            || inputs.Compression == Compression.LossyJpeg
            || inputs.IsProxy)
            v = Max(v, DngVersion.V1_4_0);

        // 1.5.0.0 — depth maps, enhanced IFD
        if (inputs.HasDepthMap || inputs.HasEnhancedIfd)
            v = Max(v, DngVersion.V1_5_0);

        // 1.6.0.0 — semantic masks, third calibration illuminant
        if (inputs.HasSemanticMask || inputs.IlluminantCount >= 3)
            v = Max(v, DngVersion.V1_6_0);

        // 1.7.0.0 — JPEG XL compression, ProfileGainTableMap2, ProfileDynamicRange, ImageStats, ImageSequenceInfo
        if (inputs.Compression == Compression.Jxl
            || inputs.HasProfileGainTableMap2
            || inputs.HasHdrProfile
            || inputs.HasImageStats
            || inputs.HasImageSequenceInfo)
            v = Max(v, DngVersion.V1_7_0);

        // 1.7.1.0 — ColumnInterleaveFactor (already covered by RowInterleaveFactor > 1 in C++ writer)
        if (inputs.RequiresColumnInterleaveFactor)
            v = Max(v, DngVersion.V1_7_1);

        return v;
    }

    private static DngVersion Max(DngVersion a, DngVersion b) => a >= b ? a : b;
}

/// <summary>
/// Bag of feature toggles used by <see cref="DngBackwardVersion.Compute"/>.
/// Build from a <see cref="Pipeline.DngNegative"/> at write time.
/// </summary>
public sealed class DngBackwardVersionInputs
{
    public Compression Compression { get; set; } = Compression.Uncompressed;
    public bool UsesFloatPixels { get; set; }
    public bool HasOpcodes { get; set; }
    public bool IsProxy { get; set; }
    public bool HasDepthMap { get; set; }
    public bool HasEnhancedIfd { get; set; }
    public bool HasSemanticMask { get; set; }
    public int IlluminantCount { get; set; }
    public bool HasProfileGainTableMap2 { get; set; }
    public bool HasHdrProfile { get; set; }
    public bool HasImageStats { get; set; }
    public bool HasImageSequenceInfo { get; set; }
    public bool RequiresColumnInterleaveFactor { get; set; }

    /// <summary>
    /// Build a set of inputs from a mosaic + linearization snapshot. Pure
    /// data → data helper, no side effects.
    /// </summary>
    public static DngBackwardVersionInputs FromMosaicAndPixel(
        MosaicInfo? mosaic, PixelType pixelType, Compression compression) =>
        new()
        {
            Compression = compression,
            UsesFloatPixels = pixelType.IsFloat(),
            RequiresColumnInterleaveFactor = mosaic?.RequiresDng171 == true,
        };
}
