using DngSharp.Dng.Sdk.Color;

namespace DngSharp.Dng.Sdk.Tests.Color;

public class XyCoordTests
{
    [Fact]
    public void Standard_illuminants_match_spec()
    {
        // Values from DNG spec section 5.1 (XyCoord constants in xy_coord.h).
        Assert.Equal(new XyCoord(0.3457, 0.3585), XyCoord.D50);
        Assert.Equal(new XyCoord(0.3127, 0.3290), XyCoord.D65);
    }

    [Fact]
    public void Xy_to_xyz_to_xy_round_trip()
    {
        var xy = XyCoord.D65;
        var xyz = xy.ToXyz();
        Assert.Equal(1.0, xyz[1], 12); // Y normalized to 1
        var rt = XyCoord.FromXyz(xyz);
        Assert.Equal(xy.X, rt.X, 6);
        Assert.Equal(xy.Y, rt.Y, 6);
    }

    [Fact]
    public void Invalid_xy_returns_d50_fallback_xyz()
    {
        var bad = new XyCoord(0.0, 0.0);
        // ToXyz substitutes D50 for invalid input (matches C++ behavior).
        var xyz = bad.ToXyz();
        var rt = XyCoord.FromXyz(xyz);
        Assert.Equal(XyCoord.D50.X, rt.X, 6);
        Assert.Equal(XyCoord.D50.Y, rt.Y, 6);
    }

    [Fact]
    public void Sum_chromaticity_pinned_below_one()
    {
        // Constructed extreme (x + y > 1) should be pinned so XYZ remains valid.
        var extreme = new XyCoord(0.8, 0.8);
        var xyz = extreme.ToXyz();
        Assert.True(xyz[2] >= 0.0, $"Z must remain non-negative; got {xyz[2]}");
    }
}
