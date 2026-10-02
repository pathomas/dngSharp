using DngSharp.Dng.Sdk.Imaging.Raw;
using DngSharp.Dng.Sdk.Metadata;
using DngSharp.Dng.Sdk.Pixels;
using DngSharp.Dng.Sdk.Tiff;
using DngSharp.Dng.Sdk.Writer;

namespace DngSharp.Dng.Sdk.Tests.Writer;

public class DngBackwardVersionTests
{
    [Fact]
    public void Plain_uncompressed_only_needs_1_0()
    {
        var v = DngBackwardVersion.Compute(new DngBackwardVersionInputs());
        Assert.Equal(DngVersion.V1_0_0, v);
    }

    [Fact]
    public void Opcodes_bump_to_1_3()
    {
        var v = DngBackwardVersion.Compute(new DngBackwardVersionInputs { HasOpcodes = true });
        Assert.Equal(DngVersion.V1_3_0, v);
    }

    [Theory]
    [InlineData(Compression.Deflate)]
    [InlineData(Compression.LossyJpeg)]
    public void Deflate_or_lossy_jpeg_bumps_to_1_4(Compression c)
    {
        var v = DngBackwardVersion.Compute(new DngBackwardVersionInputs { Compression = c });
        Assert.Equal(DngVersion.V1_4_0, v);
    }

    [Fact]
    public void Float_pixels_bump_to_1_4()
    {
        var v = DngBackwardVersion.Compute(new DngBackwardVersionInputs { UsesFloatPixels = true });
        Assert.Equal(DngVersion.V1_4_0, v);
    }

    [Fact]
    public void Depth_or_enhanced_bump_to_1_5()
    {
        var v1 = DngBackwardVersion.Compute(new DngBackwardVersionInputs { HasDepthMap = true });
        var v2 = DngBackwardVersion.Compute(new DngBackwardVersionInputs { HasEnhancedIfd = true });
        Assert.Equal(DngVersion.V1_5_0, v1);
        Assert.Equal(DngVersion.V1_5_0, v2);
    }

    [Fact]
    public void Semantic_mask_or_third_illuminant_bumps_to_1_6()
    {
        var v1 = DngBackwardVersion.Compute(new DngBackwardVersionInputs { HasSemanticMask = true });
        var v2 = DngBackwardVersion.Compute(new DngBackwardVersionInputs { IlluminantCount = 3 });
        Assert.Equal(DngVersion.V1_6_0, v1);
        Assert.Equal(DngVersion.V1_6_0, v2);
    }

    [Fact]
    public void Jxl_or_pgtm2_or_hdr_bumps_to_1_7()
    {
        Assert.Equal(DngVersion.V1_7_0,
            DngBackwardVersion.Compute(new DngBackwardVersionInputs { Compression = Compression.Jxl }));
        Assert.Equal(DngVersion.V1_7_0,
            DngBackwardVersion.Compute(new DngBackwardVersionInputs { HasProfileGainTableMap2 = true }));
        Assert.Equal(DngVersion.V1_7_0,
            DngBackwardVersion.Compute(new DngBackwardVersionInputs { HasHdrProfile = true }));
    }

    [Fact]
    public void Column_interleave_factor_bumps_to_1_7_1()
    {
        var inputs = DngBackwardVersionInputs.FromMosaicAndPixel(
            new MosaicInfo { ColumnInterleaveFactor = 2 },
            PixelType.UInt16, Compression.Uncompressed);
        var v = DngBackwardVersion.Compute(inputs);
        Assert.Equal(DngVersion.V1_7_1, v);
    }

    [Fact]
    public void Highest_feature_wins()
    {
        // Opcodes + Deflate + JXL + ColumnInterleaveFactor → 1.7.1
        var inputs = new DngBackwardVersionInputs
        {
            HasOpcodes = true,
            Compression = Compression.Jxl,
            HasHdrProfile = true,
            RequiresColumnInterleaveFactor = true,
        };
        Assert.Equal(DngVersion.V1_7_1, DngBackwardVersion.Compute(inputs));
    }
}
