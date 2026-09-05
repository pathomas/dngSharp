using DngSharp.Dng.Sdk.Imaging;
using DngSharp.Dng.Sdk.Pipeline;
using DngSharp.Dng.Sdk.Pixels;
using DngSharp.Dng.Sdk.Primitives;
using DngSharp.Dng.Sdk.Render;

namespace DngSharp.Dng.Sdk.Tests.Render;

public class HdrToneMapperTests
{
    private static SimpleImage MakeFloat32(int w, int h, float value)
    {
        var img = new SimpleImage(new DngRect(0, 0, h, w), 3, PixelType.Float32);
        var tile = img.GetTile(img.Bounds);
        tile.AsTypedSpan<float>().Fill(value);
        img.WriteTile(tile);
        return img;
    }

    [Fact]
    public void Reinhard_zero_stays_zero()
    {
        Assert.Equal(0.0, HdrToneMapper.Reinhard(0.0));
    }

    [Fact]
    public void Reinhard_compresses_large_values()
    {
        double v = HdrToneMapper.Reinhard(10.0);
        Assert.True(v < 1.0);
        Assert.True(v > 0.9);
    }

    [Fact]
    public void Reinhard_negative_clamped_to_zero()
    {
        Assert.Equal(0.0, HdrToneMapper.Reinhard(-1.0));
    }

    [Fact]
    public void SCurve_zero_lifts_shadows()
    {
        double v = HdrToneMapper.SCurve(0.0);
        Assert.True(v > 0.0, "S-curve should lift shadows above zero");
        Assert.True(v < 0.1, "Shadow lift should be small");
    }

    [Fact]
    public void SCurve_negative_clamped_to_lift()
    {
        Assert.Equal(HdrToneMapper.SCurve(0.0), HdrToneMapper.SCurve(-1.0));
    }

    [Fact]
    public void SCurve_midtone_slope_exceeds_one()
    {
        // Numeric derivative around the 0.2-0.5 range should be > 1
        // (mid-tone contrast boost).
        const double h = 1e-4;
        double x = 0.35;
        double slope = (HdrToneMapper.SCurve(x + h) - HdrToneMapper.SCurve(x - h)) / (2 * h);
        Assert.True(slope > 1.0, $"Expected mid-tone slope > 1, got {slope}");
    }

    [Fact]
    public void SCurve_highlight_rolloff_slope_below_one()
    {
        const double h = 1e-4;
        double x = 0.8;
        double slope = (HdrToneMapper.SCurve(x + h) - HdrToneMapper.SCurve(x - h)) / (2 * h);
        Assert.True(slope < 1.0, $"Expected highlight roll-off slope < 1, got {slope}");
    }

    [Fact]
    public void SCurve_compresses_large_values_towards_one()
    {
        double v = HdrToneMapper.SCurve(10.0);
        Assert.True(v < 1.0);
        Assert.True(v > 0.9);
    }

    [Fact]
    public void SCurve_extreme_value_does_not_produce_nan()
    {
        double v = HdrToneMapper.SCurve(1e150);
        Assert.False(double.IsNaN(v));
        Assert.True(v <= 1.0);
    }

    [Fact]
    public void Apply_scurve_default_compresses_overexposed_image()
    {
        var img = MakeFloat32(2, 2, 5.0f); // all pixels at 5x over-exposure
        HdrToneMapper.Apply(img);          // S-curve fallback (no profile curve)

        var tile = img.GetTile(img.Bounds);
        foreach (var s in tile.AsTypedSpan<float>())
        {
            Assert.True(s < 1.0f, "S-curve should compress >1 to <1");
            Assert.True(s > 0.8f, "S-curve at x=5 should approach 1 asymptotically");
        }
    }

    [Fact]
    public void Apply_scurve_default_lifts_black_pixels()
    {
        var img = MakeFloat32(2, 2, 0.0f);
        HdrToneMapper.Apply(img);

        var tile = img.GetTile(img.Bounds);
        foreach (var s in tile.AsTypedSpan<float>())
            Assert.True(s > 0.0f, "S-curve should lift pure-black pixels above zero");
    }

    [Fact]
    public void Apply_scurve_default_preserves_hue_on_saturated_color()
    {
        // A saturated red pixel (R >> G, B): after luminance-based tone
        // mapping, the R:G:B ratio should be preserved rather than each
        // channel being compressed independently (which would desaturate).
        var img = new SimpleImage(new DngRect(0, 0, 1, 1), 3, PixelType.Float32);
        var tile = img.GetTile(img.Bounds);
        var span = tile.AsTypedSpan<float>();
        span[0] = 4.0f; // R
        span[1] = 0.2f; // G
        span[2] = 0.1f; // B
        img.WriteTile(tile);

        HdrToneMapper.Apply(img);

        var outTile = img.GetTile(img.Bounds);
        var outSpan = outTile.AsTypedSpan<float>();
        double originalRatioGR = 0.2 / 4.0;
        double mappedRatioGR = outSpan[1] / outSpan[0];
        Assert.InRange(mappedRatioGR, originalRatioGR - 0.01, originalRatioGR + 0.01);
    }

    [Fact]
    public void Apply_with_profile_curve_applies_mapping()
    {
        var img = MakeFloat32(2, 2, 0.5f);
        // Simple curve that maps 0.5 → 0.25 (halves all values).
        var curve = new (double, double)[] { (0.0, 0.0), (1.0, 0.5) };
        HdrToneMapper.Apply(img, curve);

        var tile = img.GetTile(img.Bounds);
        foreach (var s in tile.AsTypedSpan<float>())
            Assert.InRange(s, 0.24f, 0.26f);
    }

    [Fact]
    public void EvaluateCurve_clamps_below_minimum()
    {
        var curve = new (double, double)[] { (0.0, 0.0), (1.0, 1.0) };
        Assert.Equal(0.0, HdrToneMapper.EvaluateCurve(-0.5, curve));
    }

    [Fact]
    public void EvaluateCurve_clamps_above_maximum()
    {
        var curve = new (double, double)[] { (0.0, 0.0), (1.0, 1.0) };
        Assert.Equal(1.0, HdrToneMapper.EvaluateCurve(1.5, curve));
    }

    [Fact]
    public void EvaluateCurve_interpolates_midpoint()
    {
        var curve = new (double, double)[] { (0.0, 0.0), (0.5, 0.25), (1.0, 1.0) };
        // At x=0.25 (midpoint of first segment [0, 0.5] → [0, 0.25]):
        // t = 0.25/0.5 = 0.5 → y = 0.25 * 0.5 = 0.125
        Assert.InRange(HdrToneMapper.EvaluateCurve(0.25, curve), 0.124, 0.126);
    }
}
