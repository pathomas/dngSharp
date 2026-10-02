using DngSharp.Dng.Sdk.Color;
using DngSharp.Dng.Sdk.Color.Cct;

namespace DngSharp.Dng.Sdk.Tests.Color;

public class CctRobertsonTests
{
    [Theory]
    // The Robertson table tracks the Planckian locus + isothermal lines, NOT
    // the CIE Daylight (D-illuminant) curve. So we expect Planckian-locus
    // values here, not CIE-D values.
    //   - Illuminant A (2856 K) IS Planckian — should match to ~0.001.
    //   - At 5000 K and 6500 K the Planckian locus differs slightly from
    //     CIE D50/D65 (which use a different chromaticity polynomial).
    [InlineData(2856, 0.4476, 0.4074, 0.003)]   // Illuminant A — true Planckian
    [InlineData(5000, 0.3458, 0.3516, 0.003)]   // Planckian at 5000K (≠ D50!)
    [InlineData(6500, 0.3135, 0.3236, 0.003)]   // Planckian at 6500K (≠ D65!)
    public void Temperature_to_xy_lands_on_planckian_locus(double k, double xExpected, double yExpected, double tol)
    {
        var xy = CctRobertson.TemperatureTintToXy(k, 0.0);
        Assert.InRange(xy.X, xExpected - tol, xExpected + tol);
        Assert.InRange(xy.Y, yExpected - tol, yExpected + tol);
    }

    [Theory]
    [InlineData(3200, 50)]
    [InlineData(5000, -20)]
    [InlineData(6500, 10)]
    public void Xy_to_temp_then_back_round_trips_within_tolerance(double k, double tint)
    {
        var xy = CctRobertson.TemperatureTintToXy(k, tint);
        var (rtKelvin, rtTint) = CctRobertson.XyToTemperatureTint(xy);
        Assert.InRange(rtKelvin, k * 0.99, k * 1.01);
        Assert.InRange(rtTint, tint - 2.0, tint + 2.0);
    }

    [Fact]
    public void DngTemperature_setxy_getxy_round_trip()
    {
        var dt = new DngTemperature();
        dt.SetXy(new XyCoord(0.4476, 0.4074));  // Illuminant A
        Assert.InRange(dt.Kelvin, 2800, 2900);
        var xy = dt.GetXy();
        Assert.InRange(xy.X, 0.4476 - 0.003, 0.4476 + 0.003);
    }
}
