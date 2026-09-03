using Dng.Sdk.Color;
using Dng.Sdk.Errors;

namespace Dng.Sdk.Tests.Color;

public class ColorSpecTests
{
    [Fact]
    public void Weight_at_endpoints_returns_one_and_zero()
    {
        // T1=2856 (A), T2=6504 (D65); at T1 weight on T1 = 1, at T2 = 0.
        Assert.Equal(1.0, ColorSpec.InterpolationWeight(2856, 2856, 6504), 9);
        Assert.Equal(0.0, ColorSpec.InterpolationWeight(6504, 2856, 6504), 9);
    }

    [Fact]
    public void Weight_uses_inverse_cct_not_linear_cct()
    {
        // Halfway in *kelvin* between 2856 and 6504 = 4680.
        // Halfway in *mireds* between 1/2856 and 1/6504 ≈ 3940 K.
        // The spec mandates the mired (inverse-CCT) midpoint to yield weight 0.5.
        double t1 = 2856, t2 = 6504;
        double miredMid = 2.0 / (1.0 / t1 + 1.0 / t2);
        double w = ColorSpec.InterpolationWeight(miredMid, t1, t2);
        Assert.Equal(0.5, w, 9);

        // For the kelvin midpoint, the weight should NOT be 0.5 (would prove
        // a linear-in-kelvin bug).
        double w2 = ColorSpec.InterpolationWeight((t1 + t2) * 0.5, t1, t2);
        Assert.NotEqual(0.5, System.Math.Round(w2, 4));
    }

    [Fact]
    public void Weight_clamps_outside_range()
    {
        Assert.Equal(1.0, ColorSpec.InterpolationWeight(2000, 2856, 6504), 9); // below T1 → all T1
        Assert.Equal(0.0, ColorSpec.InterpolationWeight(10000, 2856, 6504), 9); // above T2 → all T2
    }

    [Fact]
    public void Weight_rejects_inverted_order()
    {
        Assert.Throws<DngException>(() => ColorSpec.InterpolationWeight(5000, 6504, 2856));
    }

    [Fact]
    public void Pick_illuminants_single_yields_same_index_twice()
    {
        var picks = ColorSpec.PickIlluminants(5000, [2856]);
        Assert.Equal((0, 0), picks);
    }

    [Fact]
    public void Pick_illuminants_two_brackets_correctly()
    {
        var picks = ColorSpec.PickIlluminants(5000, [2856, 6504]);
        Assert.Equal((0, 1), picks);

        // Below the lowest -> both = lowest.
        picks = ColorSpec.PickIlluminants(2000, [2856, 6504]);
        Assert.Equal((0, 0), picks);

        // Above the highest -> both = highest.
        picks = ColorSpec.PickIlluminants(8000, [2856, 6504]);
        Assert.Equal((1, 1), picks);
    }

    [Fact]
    public void Pick_illuminants_three_brackets_in_sorted_order()
    {
        // Three illuminants stored out of order; bracket should sort first.
        var picks = ColorSpec.PickIlluminants(4000, [6504, 2856, 5000]);
        // Sorted: idx1(2856) < idx2(5000) < idx0(6504). 4000 sits between idx1 and idx2.
        Assert.Equal((1, 2), picks);
    }
}
