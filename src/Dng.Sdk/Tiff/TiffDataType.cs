namespace Dng.Sdk.Tiff;

/// <summary>
/// TIFF/EXIF data type codes used in IFD entries. Mirrors the <c>ttXxx</c>
/// constants in <c>dng_tag_types.h</c>.
/// </summary>
public enum TiffDataType : uint
{
    Byte = 1,
    Ascii = 2,
    Short = 3,
    Long = 4,
    Rational = 5,
    SByte = 6,
    Undefined = 7,
    SShort = 8,
    SLong = 9,
    SRational = 10,
    Float = 11,
    Double = 12,
    Ifd = 13,
    Unicode = 14,
    Complex = 15,

    // BigTIFF additions:
    Long8 = 16,
    SLong8 = 17,
    Ifd8 = 18,

    /// <summary>
    /// Non-standard. Used internally only — never written to disk in DNG/TIFF
    /// files. Mirrors the warning in <c>dng_tag_types.h</c>.
    /// </summary>
    HalfFloat = 19,
}

public static class TiffDataTypeExtensions
{
    /// <summary>
    /// Size in bytes of one element of the given TIFF type. Returns 0 for
    /// unknown types — matches C++ <c>TagTypeSize</c>.
    /// </summary>
    public static uint Size(this TiffDataType t) => t switch
    {
        TiffDataType.Byte or TiffDataType.Ascii or TiffDataType.SByte or TiffDataType.Undefined => 1,
        TiffDataType.Short or TiffDataType.SShort or TiffDataType.Unicode or TiffDataType.HalfFloat => 2,
        TiffDataType.Long or TiffDataType.SLong or TiffDataType.Float or TiffDataType.Ifd => 4,
        TiffDataType.Rational or TiffDataType.Double or TiffDataType.SRational or TiffDataType.Complex
            or TiffDataType.Long8 or TiffDataType.SLong8 or TiffDataType.Ifd8 => 8,
        _ => 0,
    };
}
