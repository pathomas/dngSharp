namespace DngSharp.Dng.Sdk.Tiff;

/// <summary>
/// Values for <c>NewSubFileType</c> (tag 254). Mirrors <c>sfXxx</c> in
/// <c>dng_tag_values.h</c>. Spec: an IFD is classified by NewSubFileType.
/// </summary>
public enum NewSubFileType : uint
{
    /// <summary>The full-resolution raw image (main IFD).</summary>
    MainImage = 0,

    /// <summary>Primary rendered preview / reduced-resolution.</summary>
    PreviewImage = 1,

    /// <summary>Full-resolution transparency mask.</summary>
    TransparencyMask = 4,

    /// <summary>Reduced-resolution transparency mask.</summary>
    PreviewMask = PreviewImage + TransparencyMask,  // 5

    /// <summary>Full-resolution depth map (DNG 1.5+).</summary>
    DepthMap = 8,

    /// <summary>Reduced-resolution depth map (DNG 1.5+).</summary>
    PreviewDepthMap = PreviewImage + DepthMap,      // 9

    /// <summary>Enhanced image (demosaiced, LinearRaw color space; DNG 1.5+).</summary>
    EnhancedImage = 16,

    /// <summary>Gain Map (DNG 1.7+).</summary>
    GainMap = 32,
    PreviewGainMap = PreviewImage + GainMap,        // 33

    /// <summary>Alternate rendered preview (non-primary settings).</summary>
    AltPreviewImage = 0x10001,

    /// <summary>Semantic mask (DNG 1.6+).</summary>
    SemanticMask = 0x10004,
    PreviewSemanticMask = PreviewImage + SemanticMask,
}

/// <summary>
/// Values for <c>PhotometricInterpretation</c> (tag 262). Mirrors <c>piXxx</c>.
/// </summary>
public enum Photometric : uint
{
    WhiteIsZero = 0,
    BlackIsZero = 1,
    Rgb = 2,
    RgbPalette = 3,
    TransparencyMask = 4,
    Cmyk = 5,
    YCbCr = 6,
    CieLab = 8,
    IccLab = 9,
    /// <summary>Color Filter Array (Bayer/X-Trans/etc. — TIFF-EP).</summary>
    Cfa = 32803,
    /// <summary>Demosaiced linear scene-referred RGB.</summary>
    LinearRaw = 34892,
    /// <summary>Depth image (DNG 1.5+).</summary>
    Depth = 51177,
    /// <summary>Photometric mask (DNG 1.6+).</summary>
    PhotometricMask = 52527,
    /// <summary>Gain Map (DNG 1.7+).</summary>
    GainMap = 52553,
}

/// <summary>
/// Values for <c>Compression</c> (tag 259). Mirrors <c>ccXxx</c>.
/// </summary>
public enum Compression : uint
{
    Uncompressed = 1,
    Lzw = 5,
    OldJpeg = 6,
    /// <summary>
    /// Lossless Huffman JPEG (for raw data) or baseline DCT JPEG (8-bit YCbCr
    /// / grayscale for previews).
    /// </summary>
    Jpeg = 7,
    Deflate = 8,
    PackBits = 32773,
    OldDeflate = 32946,
    /// <summary>Lossy JPEG — 8-bit LinearRaw or PhotometricMask only.</summary>
    LossyJpeg = 34892,
    /// <summary>JPEG XL (DNG 1.7+). 8–16-bit integer or 16-bit float; 1 or 3 planes.</summary>
    Jxl = 52546,
}

/// <summary>
/// Values for <c>SampleFormat</c> (tag 339). Mirrors <c>sfXxx</c>
/// (note: name collision with NewSubFileType in C++; we use distinct enums).
/// </summary>
public enum SampleFormat : uint
{
    UnsignedInteger = 1,
    SignedInteger = 2,
    FloatingPoint = 3,
    Undefined = 4,
}

/// <summary>
/// Values for <c>PlanarConfiguration</c> (tag 284). Mirrors <c>pcXxx</c>.
/// </summary>
public enum PlanarConfiguration : uint
{
    Interleaved = 1,
    Planar = 2,
}

/// <summary>
/// Values for <c>Predictor</c> (tag 317). Mirrors <c>cpXxx</c>.
/// </summary>
public enum Predictor : uint
{
    None = 1,
    HorizontalDifference = 2,
    FloatingPoint = 3,
    HorizontalDifferenceX2 = 34892,
    HorizontalDifferenceX4 = 34893,
    FloatingPointX2 = 34894,
    FloatingPointX4 = 34895,
}
