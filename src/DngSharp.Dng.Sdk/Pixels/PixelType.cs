using DngSharp.Dng.Sdk.Tiff;

namespace DngSharp.Dng.Sdk.Pixels;

/// <summary>
/// Storage type for pixel samples. Mirrors how <c>dng_pixel_buffer</c> uses
/// <c>fPixelType</c> (a TIFF data-type code) to identify the in-memory sample
/// representation.
///
/// <para>The DNG SDK reuses TIFF type codes — we keep that mapping so that
/// container parse results can be projected directly into a pixel buffer
/// without translation.</para>
/// </summary>
public enum PixelType : uint
{
    UInt8 = TiffDataType.Byte,
    SInt8 = TiffDataType.SByte,
    UInt16 = TiffDataType.Short,
    SInt16 = TiffDataType.SShort,
    UInt32 = TiffDataType.Long,
    SInt32 = TiffDataType.SLong,
    Float32 = TiffDataType.Float,
    Float64 = TiffDataType.Double,

    /// <summary>16-bit IEEE-754 half-precision (DNG internal use).</summary>
    Float16 = TiffDataType.HalfFloat,
}

public static class PixelTypeExtensions
{
    /// <summary>Size of one sample in bytes.</summary>
    public static int SizeBytes(this PixelType t) => t switch
    {
        PixelType.UInt8 or PixelType.SInt8 => 1,
        PixelType.UInt16 or PixelType.SInt16 or PixelType.Float16 => 2,
        PixelType.UInt32 or PixelType.SInt32 or PixelType.Float32 => 4,
        PixelType.Float64 => 8,
        _ => 0,
    };

    public static bool IsFloat(this PixelType t) =>
        t is PixelType.Float16 or PixelType.Float32 or PixelType.Float64;

    public static bool IsSigned(this PixelType t) =>
        t is PixelType.SInt8 or PixelType.SInt16 or PixelType.SInt32
          || t.IsFloat();
}
