using DngSharp.Dng.Sdk.Imaging;
using DngSharp.Dng.Sdk.Imaging.Opcodes;
using DngSharp.Dng.Sdk.Imaging.Profile;
using DngSharp.Dng.Sdk.Imaging.Raw;
using DngSharp.Dng.Sdk.Metadata;
using DngSharp.Dng.Sdk.Primitives;
using DngSharp.Dng.Sdk.Tiff;

namespace DngSharp.Dng.Sdk.Pipeline;

/// <summary>
/// Result of <see cref="StripReader.ReadStage1"/>. Bundles the decoded
/// Stage-1 image with the IFD-derived linearization and mosaic metadata so
/// callers don't have to re-parse the IFD.
/// </summary>
public sealed class StripReaderResult
{
    /// <summary>Decoded Stage-1 pixel data (camera-space, unlinearized).</summary>
    public required SimpleImage Stage1 { get; init; }

    /// <summary>
    /// Linearization parameters read from the IFD: black level, white level,
    /// optional LUT, per-row/column deltas.
    /// </summary>
    public required LinearizationInfo Linearization { get; init; }

    /// <summary>
    /// CFA mosaic info read from the IFD, or <see langword="null"/> for
    /// non-CFA images (LinearRaw, RGB, etc.).
    /// </summary>
    public required MosaicInfo? Mosaic { get; init; }

    /// <summary>Photometric interpretation of the main IFD.</summary>
    public required Photometric Photometric { get; init; }

    /// <summary>Embedded camera profile read from IFD 0, when present.</summary>
    public DngCameraProfile? CameraProfile { get; init; }

    /// <summary>Shared file-level DNG metadata read from IFD 0.</summary>
    public required DngShared Shared { get; init; }

    /// <summary>
    /// <c>ActiveArea</c> tag from the main IFD (sensor-active pixel rect,
    /// pre-DefaultCrop), or <see langword="null"/> if absent.
    /// </summary>
    public DngRect? ActiveArea { get; init; }

    /// <summary>
    /// <c>DefaultCropOrigin</c>/<c>DefaultCropSize</c> from the main IFD —
    /// the "clean" rendered-image rect (mirrors
    /// <c>dng_negative::DefaultCropArea</c>). Tighter than
    /// <see cref="ActiveArea"/>; this is what <c>dng_render.cpp</c> actually
    /// crops to before the color pipeline, or <see langword="null"/> if
    /// absent.
    /// </summary>
    public DngRect? DefaultCropArea { get; init; }

    /// <summary>
    /// OpcodeList1 parsed from the main IFD's <c>OpcodeList1</c> tag (runs on
    /// the raw Stage-1 image, before linearization), or <see langword="null"/>
    /// if absent.
    /// </summary>
    public DngOpcodeList? OpcodeList1 { get; init; }

    /// <summary>
    /// OpcodeList2 parsed from the main IFD's <c>OpcodeList2</c> tag (runs on
    /// the linearized Stage-2 image), or <see langword="null"/> if absent.
    /// </summary>
    public DngOpcodeList? OpcodeList2 { get; init; }

    /// <summary>
    /// OpcodeList3 parsed from the main IFD's <c>OpcodeList3</c> tag (runs on
    /// the demosaiced Stage-3 image), or <see langword="null"/> if absent.
    /// </summary>
    public DngOpcodeList? OpcodeList3 { get; init; }
}
