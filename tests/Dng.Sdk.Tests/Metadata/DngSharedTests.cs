using Dng.Sdk.Color;
using Dng.Sdk.Errors;
using Dng.Sdk.Math;
using Dng.Sdk.Metadata;

namespace Dng.Sdk.Tests.Metadata;

public class DngVersionTests
{
    [Fact]
    public void Comparisons_match_lexicographic_order()
    {
        Assert.True(DngVersion.V1_3_0 < DngVersion.V1_4_0);
        Assert.True(DngVersion.V1_7_1 > DngVersion.V1_7_0);
        Assert.True(DngVersion.V1_7_0 < DngVersion.V1_7_1);
        Assert.Equal(DngVersion.V1_7_1, new DngVersion(1, 7, 1, 0));
    }

    [Fact]
    public void From_bytes_round_trips()
    {
        var v = DngVersion.FromBytes(new byte[] { 1, 7, 1, 0 });
        Assert.Equal(DngVersion.V1_7_1, v);
        Assert.Equal("1.7.1.0", v.ToString());
    }
}

public class DngSharedTests
{
    [Fact]
    public void AsShot_neutral_and_white_xy_are_mutually_exclusive()
    {
        var s = new DngShared();
        s.SetAsShotNeutral(DngVector.Of(0.5, 1.0, 0.7));
        Assert.NotNull(s.AsShotNeutral);
        Assert.Null(s.AsShotWhiteXy);

        // Setting xy clears neutral (spec 6.4: mutually exclusive).
        s.SetAsShotWhiteXy(XyCoord.D65);
        Assert.Null(s.AsShotNeutral);
        Assert.Equal(XyCoord.D65, s.AsShotWhiteXy);

        // And going back the other way clears xy.
        s.SetAsShotNeutral(DngVector.Of(0.5, 1.0, 0.7));
        Assert.NotNull(s.AsShotNeutral);
        Assert.Null(s.AsShotWhiteXy);
    }

    [Fact]
    public void ValidateReadable_throws_on_too_new_file()
    {
        var s = new DngShared { BackwardVersion = DngVersion.V1_7_1 };
        var ex = Assert.Throws<DngException>(() => s.ValidateReadable(DngVersion.V1_6_0));
        Assert.Equal(DngError.UnsupportedDng, ex.ErrorCode);
    }

    [Fact]
    public void ValidateReadable_allows_equal_or_newer_reader()
    {
        var s = new DngShared { BackwardVersion = DngVersion.V1_3_0 };
        s.ValidateReadable(DngVersion.V1_3_0);
        s.ValidateReadable(DngVersion.V1_7_1);
    }
}
