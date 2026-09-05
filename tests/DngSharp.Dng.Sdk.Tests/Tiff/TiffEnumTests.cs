using DngSharp.Dng.Sdk.Tiff;

namespace DngSharp.Dng.Sdk.Tests.Tiff;

public class TiffEnumTests
{
    [Theory]
    [InlineData(TiffDataType.Byte, 1u)]
    [InlineData(TiffDataType.Ascii, 1u)]
    [InlineData(TiffDataType.Short, 2u)]
    [InlineData(TiffDataType.Long, 4u)]
    [InlineData(TiffDataType.Rational, 8u)]
    [InlineData(TiffDataType.Float, 4u)]
    [InlineData(TiffDataType.Double, 8u)]
    [InlineData(TiffDataType.Long8, 8u)]
    [InlineData(TiffDataType.HalfFloat, 2u)]
    [InlineData((TiffDataType)999, 0u)] // unknown returns 0 (matches C++)
    public void Type_size_matches_spec(TiffDataType type, uint size)
    {
        Assert.Equal(size, type.Size());
    }

    [Fact]
    public void Dng_tag_codes_have_known_values()
    {
        // Spot checks against TIFF 6.0 / DNG spec.
        Assert.Equal(254u, (uint)DngTagCode.NewSubFileType);
        Assert.Equal(256u, (uint)DngTagCode.ImageWidth);
        Assert.Equal(257u, (uint)DngTagCode.ImageLength);
        Assert.Equal(258u, (uint)DngTagCode.BitsPerSample);
        Assert.Equal(259u, (uint)DngTagCode.Compression);
        Assert.Equal(262u, (uint)DngTagCode.PhotometricInterpretation);
        // DNG 1.7+ tags.
        Assert.Equal(52546u, (uint)Compression.Jxl);
        Assert.Equal(34892u, (uint)Compression.LossyJpeg);
    }

    [Fact]
    public void NewSubFileType_classification_values()
    {
        Assert.Equal(0u, (uint)NewSubFileType.MainImage);
        Assert.Equal(1u, (uint)NewSubFileType.PreviewImage);
        Assert.Equal(4u, (uint)NewSubFileType.TransparencyMask);
        Assert.Equal(8u, (uint)NewSubFileType.DepthMap);
        Assert.Equal(16u, (uint)NewSubFileType.EnhancedImage);
        Assert.Equal(0x10001u, (uint)NewSubFileType.AltPreviewImage);
        Assert.Equal(0x10004u, (uint)NewSubFileType.SemanticMask);
    }

    [Fact]
    public void Photometric_dng_extensions()
    {
        Assert.Equal(32803u, (uint)Photometric.Cfa);
        Assert.Equal(34892u, (uint)Photometric.LinearRaw);
        Assert.Equal(52527u, (uint)Photometric.PhotometricMask);
    }
}
