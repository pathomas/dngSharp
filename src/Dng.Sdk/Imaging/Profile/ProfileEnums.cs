namespace Dng.Sdk.Imaging.Profile;

/// <summary>
/// Profile dynamic-range hint. Mirrors <c>ProfileDynamicRange</c> (tag
/// 52551, DNG 1.7). Triggers HDR encode/decode around table lookups in the
/// render pipeline (spec ch. 6, HDR).
/// </summary>
public enum ProfileDynamicRange : uint
{
    StandardDynamicRange = 0,
    HighDynamicRange = 1,
}

/// <summary>
/// Source of a <c>ProfileGainTableMap2</c>. Used to enforce the precedence
/// rule in spec ch. 5: Camera Profile IFD &gt; IFD 0 &gt; Raw IFD
/// <c>ProfileGainTableMap</c> (legacy).
/// </summary>
public enum GainTableSource
{
    None,
    /// <summary>Legacy <c>ProfileGainTableMap</c> in the Raw IFD (DNG 1.3).</summary>
    RawIfdLegacy,
    /// <summary><c>ProfileGainTableMap2</c> in IFD 0 (DNG 1.7).</summary>
    Ifd0,
    /// <summary><c>ProfileGainTableMap2</c> in the Camera Profile IFD (DNG 1.7). Highest precedence.</summary>
    CameraProfileIfd,
}
