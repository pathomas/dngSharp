using DngSharp.Dng.Sdk.Imaging;
using DngSharp.Dng.Sdk.Pipeline;
using DngSharp.Dng.Sdk.Pixels;
using DngSharp.Dng.Sdk.Primitives;
using Xunit;

namespace DngSharp.Dng.Sdk.Tests.Pipeline;

public class TransparencyMaskCropTests
{
    private static SimpleImage BuildMask(int w, int h, Func<int, int, byte> valueAt)
    {
        var mask = new SimpleImage(new DngRect(0, 0, h, w), 1, PixelType.UInt8);
        var span = mask.Buffer.AsTypedSpan<byte>();
        for (int r = 0; r < h; r++)
            for (int c = 0; c < w; c++)
                span[(r * w) + c] = valueAt(r, c);
        return mask;
    }

    [Fact]
    public void Fully_opaque_mask_yields_the_whole_frame()
    {
        var mask = BuildMask(64, 48, (_, _) => 255);

        var rect = TransparencyMaskCrop.FindFullyOpaqueRect(mask);

        Assert.NotNull(rect);
        Assert.Equal(mask.Bounds, rect!.Value);
    }

    [Fact]
    public void Axis_aligned_border_is_trimmed_exactly()
    {
        const int margin = 5;
        var mask = BuildMask(64, 48, (r, c) =>
            r >= margin && r < 48 - margin && c >= margin && c < 64 - margin ? (byte)255 : (byte)0);

        var rect = TransparencyMaskCrop.FindFullyOpaqueRect(mask);

        Assert.NotNull(rect);
        Assert.Equal(new DngRect(margin, margin, 48 - margin, 64 - margin), rect!.Value);
    }

    [Fact]
    public void Partial_alpha_falloff_ring_is_trimmed_not_kept()
    {
        // 0 = outside, 128 = partial-alpha falloff ring, 255 = opaque core.
        var mask = BuildMask(64, 48, (r, c) =>
        {
            bool inCore = r >= 6 && r < 42 && c >= 6 && c < 58;
            bool inRing = r >= 3 && r < 45 && c >= 3 && c < 61;
            return inCore ? (byte)255 : inRing ? (byte)128 : (byte)0;
        });

        var rect = TransparencyMaskCrop.FindFullyOpaqueRect(mask);

        Assert.NotNull(rect);
        Assert.Equal(new DngRect(6, 6, 42, 58), rect!.Value);
    }

    /// <summary>
    /// The regression this rule exists for: a warped/rotated valid-pixel
    /// quadrilateral. Its *bounding box* is the whole frame, so a
    /// bounding-box crop leaves the transparent corner wedges in frame
    /// (they then get painted white by the mask composite). The result must
    /// be strictly inside the frame and contain no transparent pixel.
    /// </summary>
    [Fact]
    public void Rotated_mask_corners_are_excluded()
    {
        const int w = 128, h = 96;
        // Diamond-ish quad: opaque where |dx| + |dy| is small enough, so every
        // row and column touches the frame edge somewhere but no edge row or
        // column is fully opaque.
        var mask = BuildMask(w, h, (r, c) =>
        {
            double dx = (c - ((w - 1) / 2.0)) / (w / 2.0);
            double dy = (r - ((h - 1) / 2.0)) / (h / 2.0);
            return System.Math.Abs(dx) + System.Math.Abs(dy) <= 1.0 ? (byte)255 : (byte)0;
        });

        var rect = TransparencyMaskCrop.FindFullyOpaqueRect(mask);

        Assert.NotNull(rect);
        var box = rect!.Value;

        Assert.True(box.W < w && box.H < h,
            $"Expected the crop ({box.W}x{box.H}) to be strictly inside the frame "
            + $"({w}x{h}); a full-frame result means the corner wedges survived.");

        var span = mask.Buffer.AsTypedSpan<byte>();
        for (int r = (int)box.T; r < (int)box.B; r++)
            for (int c = (int)box.L; c < (int)box.R; c++)
                Assert.True(span[(r * w) + c] == 255,
                    $"Pixel ({c},{r}) inside the crop rect {box} is not fully opaque "
                    + "— the crop would leave a white wedge in the render.");
    }

    [Fact]
    public void Mask_with_no_opaque_pixel_returns_null()
    {
        var mask = BuildMask(32, 32, (_, _) => 0);

        Assert.Null(TransparencyMaskCrop.FindFullyOpaqueRect(mask));
    }

    [Fact]
    public void Float_mask_uses_unit_scale_for_opacity()
    {
        var mask = new SimpleImage(new DngRect(0, 0, 16, 16), 1, PixelType.Float32);
        var span = mask.Buffer.AsTypedSpan<float>();
        for (int r = 0; r < 16; r++)
            for (int c = 0; c < 16; c++)
                span[(r * 16) + c] = (r >= 2 && r < 14 && c >= 2 && c < 14) ? 1.0f : 0.5f;

        var rect = TransparencyMaskCrop.FindFullyOpaqueRect(mask);

        Assert.NotNull(rect);
        Assert.Equal(new DngRect(2, 2, 14, 14), rect!.Value);
    }
}
