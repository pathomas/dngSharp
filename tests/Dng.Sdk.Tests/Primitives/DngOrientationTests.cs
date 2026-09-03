using Dng.Sdk.Primitives;

namespace Dng.Sdk.Tests.Primitives;

public class DngOrientationTests
{
    [Theory]
    [InlineData(1u, DngOrientation.Normal)]
    [InlineData(2u, DngOrientation.Mirror)]
    [InlineData(3u, DngOrientation.Rotate180)]
    [InlineData(4u, DngOrientation.Mirror180)]
    [InlineData(5u, DngOrientation.Mirror90CCW)]
    [InlineData(6u, DngOrientation.Rotate90CW)]
    [InlineData(7u, DngOrientation.Mirror90CW)]
    [InlineData(8u, DngOrientation.Rotate90CCW)]
    [InlineData(9u, DngOrientation.Unknown)]
    public void Tiff_to_adobe_round_trip(uint tiff, uint adobe)
    {
        var o = DngOrientation.FromTiff(tiff);
        Assert.Equal(adobe, o.Adobe);
        Assert.Equal(tiff, o.ToTiff());
    }

    [Theory]
    [InlineData(DngOrientation.Normal, false)]
    [InlineData(DngOrientation.Rotate90CW, true)]
    [InlineData(DngOrientation.Rotate180, false)]
    [InlineData(DngOrientation.Rotate90CCW, true)]
    public void FlipD_reflects_bit0(uint adobe, bool expected)
    {
        Assert.Equal(expected, DngOrientation.FromAdobe(adobe).FlipD);
    }

    [Fact]
    public void Mirrored_orientations_are_detected()
    {
        for (uint a = 0; a < 4; a++)
            Assert.False(DngOrientation.FromAdobe(a).IsMirrored);
        for (uint a = 4; a < 8; a++)
            Assert.True(DngOrientation.FromAdobe(a).IsMirrored);
    }

    [Fact]
    public void Composition_round_trips_with_inverse()
    {
        // For every orientation o: o + (-o) == Normal.
        for (uint a = 0; a < 8; a++)
        {
            var o = DngOrientation.FromAdobe(a);
            var rt = o + (-o);
            Assert.Equal(DngOrientation.Normal, rt.Adobe);
        }
    }

    [Fact]
    public void Composition_not_commutative_in_general()
    {
        var rot = DngOrientation.FromAdobe(DngOrientation.Rotate90CW);
        var mir = DngOrientation.FromAdobe(DngOrientation.Mirror);
        // Different results -> non-commutative.
        Assert.NotEqual(rot + mir, mir + rot);
    }

    [Fact]
    public void Inverse_subtract_identity()
    {
        // If c = a + b then b = -a + c.
        var a = DngOrientation.FromAdobe(DngOrientation.Rotate90CW);
        var b = DngOrientation.FromAdobe(DngOrientation.Mirror90CW);
        var c = a + b;
        Assert.Equal(b, -a + c);
    }
}
