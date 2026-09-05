using DngSharp.Dng.Sdk.Color;
using DngSharp.Dng.Sdk.Math;

namespace DngSharp.Dng.Sdk.Tests.Color;

public class BradfordTests
{
    [Fact]
    public void Identity_adapt_for_same_white_is_identity()
    {
        var m = Bradford.MakeAdaptationMatrix(XyCoord.D65, XyCoord.D65);
        Assert.True(m.AlmostIdentity(1e-9));
    }

    [Fact]
    public void Inverse_matrix_is_a_real_inverse()
    {
        var product = Bradford.Matrix * Bradford.InverseMatrix;
        Assert.True(product.AlmostIdentity(1e-9));
    }

    [Fact]
    public void Adapts_d65_to_d50_white_point_correctly()
    {
        var m = Bradford.MakeAdaptationMatrix(XyCoord.D65, XyCoord.D50);
        var srcWhite = XyCoord.D65.ToXyz();   // Y = 1.0
        var adapted = m * srcWhite;
        var d50 = XyCoord.D50.ToXyz();
        // The matrix should map src white -> dst white (within FP error).
        Assert.Equal(d50[0], adapted[0], 1e-8);
        Assert.Equal(d50[1], adapted[1], 1e-8);
        Assert.Equal(d50[2], adapted[2], 1e-8);
    }

    [Fact]
    public void Forward_and_reverse_adapt_compose_to_identity()
    {
        var fwd = Bradford.MakeAdaptationMatrix(XyCoord.D65, XyCoord.D50);
        var rev = Bradford.MakeAdaptationMatrix(XyCoord.D50, XyCoord.D65);
        var roundTrip = fwd * rev;
        Assert.True(roundTrip.AlmostIdentity(1e-9));
    }
}
