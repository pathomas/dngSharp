using DngSharp.Dng.Sdk.Pixels;

namespace DngSharp.Dng.Sdk.Tests.Pixels;

public class PixelTypeTests
{
    [Theory]
    [InlineData(PixelType.UInt8, 1)]
    [InlineData(PixelType.SInt8, 1)]
    [InlineData(PixelType.UInt16, 2)]
    [InlineData(PixelType.SInt16, 2)]
    [InlineData(PixelType.Float16, 2)]
    [InlineData(PixelType.UInt32, 4)]
    [InlineData(PixelType.SInt32, 4)]
    [InlineData(PixelType.Float32, 4)]
    [InlineData(PixelType.Float64, 8)]
    public void Size_bytes(PixelType type, int expected)
    {
        Assert.Equal(expected, type.SizeBytes());
    }

    [Theory]
    [InlineData(PixelType.UInt16, false)]
    [InlineData(PixelType.SInt16, true)]
    [InlineData(PixelType.Float32, true)]
    [InlineData(PixelType.Float64, true)]
    public void Signed_detection(PixelType type, bool signed)
    {
        Assert.Equal(signed, type.IsSigned());
    }

    [Theory]
    [InlineData(PixelType.Float16, true)]
    [InlineData(PixelType.Float32, true)]
    [InlineData(PixelType.UInt8, false)]
    [InlineData(PixelType.SInt32, false)]
    public void Float_detection(PixelType type, bool isFloat)
    {
        Assert.Equal(isFloat, type.IsFloat());
    }
}
