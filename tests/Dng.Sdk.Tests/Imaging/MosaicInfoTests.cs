using Dng.Sdk.Errors;
using Dng.Sdk.Imaging.Raw;
using Dng.Sdk.Primitives;

namespace Dng.Sdk.Tests.Imaging;

public class MosaicInfoTests
{
    [Fact]
    public void Default_factors_do_not_require_dng_171()
    {
        var m = new MosaicInfo();
        Assert.False(m.RequiresDng171);
    }

    [Theory]
    [InlineData(2u, 1u, true)]
    [InlineData(1u, 2u, true)]
    [InlineData(2u, 2u, true)]
    [InlineData(1u, 1u, false)]
    public void RequiresDng171_reflects_interleave_factors(uint row, uint col, bool expected)
    {
        var m = new MosaicInfo { RowInterleaveFactor = row, ColumnInterleaveFactor = col };
        Assert.Equal(expected, m.RequiresDng171);
    }

    [Fact]
    public void Validate_rejects_non_divisible_image_size()
    {
        var m = new MosaicInfo { RowInterleaveFactor = 2, ColumnInterleaveFactor = 2 };
        var ex = Assert.Throws<DngException>(() => m.Validate(new DngPoint(101, 100)));  // odd height
        Assert.Equal(DngError.BadFormat, ex.ErrorCode);
    }

    [Fact]
    public void Validate_rejects_zero_factors()
    {
        var m = new MosaicInfo { RowInterleaveFactor = 0 };
        Assert.Throws<DngException>(() => m.Validate(new DngPoint(100, 100)));
    }

    [Fact]
    public void Validate_accepts_even_division()
    {
        var m = new MosaicInfo { RowInterleaveFactor = 2, ColumnInterleaveFactor = 2 };
        m.Validate(new DngPoint(100, 100));  // both divide evenly
    }
}
