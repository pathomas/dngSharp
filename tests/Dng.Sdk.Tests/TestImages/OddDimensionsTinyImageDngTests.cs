using SkiaSharp;

namespace Dng.Sdk.Tests.TestImages;

/// <summary>
/// Geometric synthetic-fixture regression test: TODO.md's
/// <c>test-odd-dimensions-tiny-image</c>. Very small (down to 1x1) and
/// non-even-dimension DNGs exercise off-by-one/padding/rounding bugs in the
/// strip decode and crop-adjacent math that a "nice round" 4:3 or 3:2 test
/// fixture would never hit (e.g. odd width means no exact mid-column, odd
/// height means strip/row math can't silently rely on divisibility by 2).
///
/// <para>Sizes are deliberately smaller than TODO.md's illustrative
/// "4001x2999" example to keep the suite fast — the property under test
/// (odd, non-square, non-power-of-two dimensions) doesn't depend on absolute
/// scale.</para>
/// </summary>
public class OddDimensionsTinyImageDngTests
{
    public static IEnumerable<object[]> TinyAndOddSizes =>
    [
        [1, 1],
        [3, 3],
        [4, 4],
        [7, 5],
        [401, 299],
    ];

    [Theory]
    [MemberData(nameof(TinyAndOddSizes))]
    public void Renders_to_exact_requested_dimensions(int width, int height)
    {
        var dngBytes = SyntheticDngBuilder.BuildGradientLeftToRightDng(width, height);
        var dngPath = Path.Combine(Path.GetTempPath(), $"dng_odd_{width}x{height}_{Guid.NewGuid():N}.dng");
        var jpegPath = Path.Combine(Path.GetTempPath(), $"dng_odd_{width}x{height}_{Guid.NewGuid():N}.jpg");
        try
        {
            File.WriteAllBytes(dngPath, dngBytes);

            int exitCode = Cli.Run(["-jpeg", jpegPath, dngPath]);
            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(jpegPath));

            using var bitmap = SKBitmap.Decode(jpegPath);
            Assert.NotNull(bitmap);

            // No off-by-one padding/truncation: the rendered image must be
            // exactly the requested (possibly odd, possibly tiny) size, not
            // rounded up/down to an even number or a decode-buffer size.
            Assert.Equal(width, bitmap.Width);
            Assert.Equal(height, bitmap.Height);
        }
        finally
        {
            if (File.Exists(dngPath)) File.Delete(dngPath);
            if (File.Exists(jpegPath)) File.Delete(jpegPath);
        }
    }

    /// <summary>
    /// The 1x1 case has no room for a gradient (both DNG builder and the raw
    /// pattern generator special-case width==1 as a flat 0-valued pixel — see
    /// <see cref="SyntheticPixelPatterns.GradientLeftToRight"/>); this test
    /// only needs to assert the single pixel decodes without throwing and
    /// produces a plausible (non-corrupt) grayscale value.
    /// </summary>
    [Fact]
    public void Single_pixel_image_renders_without_error()
    {
        var dngBytes = SyntheticDngBuilder.BuildGradientLeftToRightDng(1, 1);
        var dngPath = Path.Combine(Path.GetTempPath(), $"dng_1x1_{Guid.NewGuid():N}.dng");
        var jpegPath = Path.Combine(Path.GetTempPath(), $"dng_1x1_{Guid.NewGuid():N}.jpg");
        try
        {
            File.WriteAllBytes(dngPath, dngBytes);

            int exitCode = Cli.Run(["-jpeg", jpegPath, dngPath]);
            Assert.Equal(0, exitCode);

            using var bitmap = SKBitmap.Decode(jpegPath);
            Assert.NotNull(bitmap);
            Assert.Equal(1, bitmap.Width);
            Assert.Equal(1, bitmap.Height);

            var px = bitmap.GetPixel(0, 0);
            Assert.True(px.Red == px.Green && px.Green == px.Blue,
                "Single-pixel fixture is a neutral (R=G=B) raw value; a " +
                "correct render should stay grayscale.");
        }
        finally
        {
            if (File.Exists(dngPath)) File.Delete(dngPath);
            if (File.Exists(jpegPath)) File.Delete(jpegPath);
        }
    }

    /// <summary>
    /// A larger odd, non-4:3/3:2, non-power-of-two fixture (401x299) still
    /// preserves the left→right monotonic gradient — same invariant as
    /// <see cref="GradientDngTests.Gradient_renders_with_monotonic_non_decreasing_brightness"/>,
    /// scaled down and re-asserted at an odd size to catch strip/row math
    /// that only breaks on non-round dimensions.
    /// </summary>
    [Fact]
    public void Odd_sized_gradient_stays_monotonic_left_to_right()
    {
        const int width = 401;
        const int height = 299;
        var dngBytes = SyntheticDngBuilder.BuildGradientLeftToRightDng(width, height);
        var dngPath = Path.Combine(Path.GetTempPath(), $"dng_oddgrad_{Guid.NewGuid():N}.dng");
        var jpegPath = Path.Combine(Path.GetTempPath(), $"dng_oddgrad_{Guid.NewGuid():N}.jpg");
        try
        {
            File.WriteAllBytes(dngPath, dngBytes);

            int exitCode = Cli.Run(["-jpeg", jpegPath, dngPath]);
            Assert.Equal(0, exitCode);

            using var bitmap = SKBitmap.Decode(jpegPath);
            Assert.NotNull(bitmap);
            Assert.Equal(width, bitmap.Width);
            Assert.Equal(height, bitmap.Height);

            int row = height / 2;
            var means = new List<double>();
            for (int col = 0; col < width; col++)
            {
                var px = bitmap.GetPixel(col, row);
                means.Add((px.Red + px.Green + px.Blue) / 3.0);
            }

            const double tolerance = 3.0;
            for (int i = 1; i < means.Count; i++)
            {
                Assert.True(means[i] >= means[i - 1] - tolerance,
                    $"Brightness decreased moving left→right at column {i}: " +
                    $"{means[i - 1]} -> {means[i]}. Odd-dimension strip/row " +
                    "math likely off-by-one somewhere in the decode path.");
            }
        }
        finally
        {
            if (File.Exists(dngPath)) File.Delete(dngPath);
            if (File.Exists(jpegPath)) File.Delete(jpegPath);
        }
    }
}
