using DngSharp.Dng.Sdk.Primitives;

namespace DngSharp.Dng.Sdk.Tests.Primitives;

public class DngRationalTests
{
    [Fact]
    public void SRational_validity_and_value()
    {
        Assert.False(new DngSRational(0, 0).IsValid);
        Assert.True(new DngSRational(1, 2).IsValid);
        Assert.Equal(0.5, new DngSRational(1, 2).AsDouble);
        Assert.Equal(0.0, new DngSRational(1, 0).AsDouble); // safe divide
    }

    [Fact]
    public void SRational_SetDouble_zero_canonical()
    {
        var r = new DngSRational();
        r.SetDouble(0.0);
        Assert.Equal(0, r.N);
        Assert.Equal(1, r.D); // canonical (0,1), not (0, large)
    }

    [Theory]
    [InlineData(40000.0, 1)]          // |x| >= 32768  -> dd=1
    [InlineData(2.5, 32768)]          // 1 <= |x| < 32768 -> dd=32768
    [InlineData(0.001, 32768 * 32768)] // |x| < 1
    public void SRational_SetDouble_picks_dd(double value, int expectedDenom)
    {
        var r = new DngSRational();
        r.SetDouble(value);
        Assert.Equal(expectedDenom, r.D);
        Assert.Equal(value, r.AsDouble, 6);
    }

    [Fact]
    public void URational_negative_input_clamps_to_zero()
    {
        var r = new DngURational();
        r.SetDouble(-1.5);
        Assert.Equal(0u, r.N);
        Assert.Equal(1u, r.D);
    }

    [Fact]
    public void URational_reduce_by_factor()
    {
        var r = new DngURational(8u, 16u);
        r.ReduceByFactor(2u);
        Assert.Equal(1u, r.N);
        Assert.Equal(2u, r.D);
    }
}
