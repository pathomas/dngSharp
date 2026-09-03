using Dng.Sdk.Imaging;
using Dng.Sdk.Imaging.Raw;
using Dng.Sdk.Pipeline;
using Dng.Sdk.Pixels;
using Dng.Sdk.Primitives;

namespace Dng.Sdk.Tests.Pipeline;

public class Stage2BuilderTests
{
    [Fact]
    public void Linear_uint16_input_rescales_to_zero_to_one()
    {
        // 4x4 single-plane uint16 image with values 0, 100, 1000, 4095 in row 0.
        var stage1 = new SimpleImage(new DngRect(0, 0, 4, 4), 1, PixelType.UInt16);
        var s1 = stage1.Buffer.AsTypedSpan<ushort>();
        s1[0] = 0;
        s1[1] = 100;
        s1[2] = 1000;
        s1[3] = 4095;

        var lin = new LinearizationInfo
        {
            BlackLevel = [0.0],
            WhiteLevel = [4095.0],
        };

        var stage2 = Stage2Builder.Build(stage1, lin);

        var s2 = stage2.Buffer.AsTypedSpan<float>();
        Assert.Equal(0f, s2[0]);
        Assert.Equal(100f / 4095f, s2[1], 6);
        Assert.Equal(1000f / 4095f, s2[2], 6);
        Assert.Equal(1f, s2[3]);
    }

    [Fact]
    public void Black_level_subtraction_centers_zero()
    {
        // BlackLevel = 64, WhiteLevel = 4095. Sample == BlackLevel maps to 0.
        var stage1 = new SimpleImage(new DngRect(0, 0, 1, 4), 1, PixelType.UInt16);
        var s1 = stage1.Buffer.AsTypedSpan<ushort>();
        s1[0] = 64;          // at black
        s1[1] = 0;           // below black -> NEGATIVE (preserved per spec)
        s1[2] = 4095;        // at white -> 1.0
        s1[3] = 5000;        // above white -> clipped to 1.0

        var lin = new LinearizationInfo
        {
            BlackLevel = [64.0],
            WhiteLevel = [4095.0],
        };

        var stage2 = Stage2Builder.Build(stage1, lin);
        var s2 = stage2.Buffer.AsTypedSpan<float>();

        Assert.Equal(0f, s2[0], 6);
        Assert.True(s2[1] < 0f, $"sub-black sample must remain negative for sensor noise preservation; got {s2[1]}");
        Assert.Equal(1f, s2[2], 6);
        Assert.Equal(1f, s2[3], 6);
    }

    [Fact]
    public void Top_is_clipped_but_bottom_is_not()
    {
        // Spec ch. 5: clip above 1.0, preserve sub-zero.
        var stage1 = new SimpleImage(new DngRect(0, 0, 1, 2), 1, PixelType.SInt16);
        var s1 = stage1.Buffer.AsTypedSpan<short>();
        s1[0] = -10;     // very low
        s1[1] = 10000;   // very high

        var lin = new LinearizationInfo
        {
            BlackLevel = [0.0],
            WhiteLevel = [1000.0],
        };

        var stage2 = Stage2Builder.Build(stage1, lin);
        var s2 = stage2.Buffer.AsTypedSpan<float>();
        Assert.True(s2[0] < 0f);   // preserved negative
        Assert.Equal(1f, s2[1], 6); // clipped to 1
    }

    [Fact]
    public void Linearization_table_LUT_is_applied_before_black_subtract()
    {
        // 4-entry LUT that doubles every value.
        var stage1 = new SimpleImage(new DngRect(0, 0, 1, 4), 1, PixelType.UInt8);
        var s1 = stage1.Buffer.AsTypedSpan<byte>();
        s1[0] = 0; s1[1] = 1; s1[2] = 2; s1[3] = 3;

        var lin = new LinearizationInfo
        {
            LinearizationTable = [0, 2, 4, 6],  // doubles
            BlackLevel = [0.0],
            WhiteLevel = [6.0],
        };

        var stage2 = Stage2Builder.Build(stage1, lin);
        var s2 = stage2.Buffer.AsTypedSpan<float>();
        // LUT doubles, then divides by white=6 → 0/6, 2/6, 4/6, 6/6.
        Assert.Equal(0f, s2[0], 6);
        Assert.Equal(2f / 6f, s2[1], 6);
        Assert.Equal(4f / 6f, s2[2], 6);
        Assert.Equal(1f, s2[3], 6);
    }

    [Fact]
    public void White_le_black_throws_bad_format()
    {
        var stage1 = new SimpleImage(new DngRect(0, 0, 4, 4), 1, PixelType.UInt16);
        var lin = new LinearizationInfo
        {
            BlackLevel = [1000.0],
            WhiteLevel = [1000.0],   // not strictly greater
        };
        Assert.Throws<Errors.DngException>(() => Stage2Builder.Build(stage1, lin));
    }

    // ── SIMD fast-path coverage ─────────────────────────────────────────────
    // Single-plane (mosaic), no LUT/repeat/deltas → triggers Stage2Builder's
    // vectorized (sample - black) * scale fast path. Width 600 spans multiple
    // Vector<float> batches plus the 256-element block boundary and a scalar
    // remainder, so this exercises the chunking logic, not just one lane.

    [Fact]
    public void Simd_fast_path_matches_expected_values_across_block_boundary()
    {
        const int width = 600; // > 2 × SimdBlockSize(256) to cross block + remainder boundaries
        var stage1 = new SimpleImage(new DngRect(0, 0, 1, width), 1, PixelType.UInt16);
        var s1 = stage1.Buffer.AsTypedSpan<ushort>();
        for (int i = 0; i < width; i++)
            s1[i] = (ushort)System.Math.Min(65535, i * 10); // ramp, including values > white (clip test)

        var lin = new LinearizationInfo
        {
            BlackLevel = [50.0],
            WhiteLevel = [4050.0],
        };

        var stage2 = Stage2Builder.Build(stage1, lin);
        var s2 = stage2.Buffer.AsTypedSpan<float>();

        double scale = 1.0 / (4050.0 - 50.0);
        for (int i = 0; i < width; i++)
        {
            double expected = (s1[i] - 50.0) * scale;
            if (expected > 1.0) expected = 1.0;
            Assert.Equal((float)expected, s2[i], 4);
        }
    }

    [Fact]
    public void Simd_fast_path_preserves_sub_zero_values()
    {
        const int width = 300;
        var stage1 = new SimpleImage(new DngRect(0, 0, 1, width), 1, PixelType.SInt16);
        var s1 = stage1.Buffer.AsTypedSpan<short>();
        for (int i = 0; i < width; i++) s1[i] = (short)(i - 50); // some negative, some positive

        var lin = new LinearizationInfo
        {
            BlackLevel = [0.0],
            WhiteLevel = [1000.0],
        };

        var stage2 = Stage2Builder.Build(stage1, lin);
        var s2 = stage2.Buffer.AsTypedSpan<float>();

        for (int i = 0; i < width; i++)
        {
            if (s1[i] < 0)
                Assert.True(s2[i] < 0f, $"index {i}: sub-zero must be preserved, got {s2[i]}");
        }
    }

    [Fact]
    public void Repeating_black_level_pattern_falls_back_to_scalar_and_is_correct()
    {
        // BlackLevelRepeatDim > (1,1) disables the SIMD fast path — verify the
        // scalar fallback still produces correct per-pattern-cell results
        // over a width that would otherwise qualify for SIMD.
        const int width = 8;
        var stage1 = new SimpleImage(new DngRect(0, 0, 1, width), 1, PixelType.UInt16);
        var s1 = stage1.Buffer.AsTypedSpan<ushort>();
        for (int i = 0; i < width; i++) s1[i] = 1000;

        var lin = new LinearizationInfo
        {
            BlackLevel = [0.0, 100.0], // 2×1 repeat: even cols black=0, odd cols black=100
            WhiteLevel = [1000.0],
            BlackLevelRepeatDim = (1, 2),
        };

        var stage2 = Stage2Builder.Build(stage1, lin);
        var s2 = stage2.Buffer.AsTypedSpan<float>();

        for (int i = 0; i < width; i++)
        {
            double black = i % 2 == 0 ? 0.0 : 100.0;
            double expected = (1000.0 - black) / 1000.0;
            Assert.Equal((float)expected, s2[i], 4);
        }
    }
}
