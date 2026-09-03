using SkiaSharp;

namespace Dng.Sdk.Tests.TestImages;

/// <summary>
/// Geometric synthetic-fixture regression test: TODO.md's
/// <c>test-border-crop-margin</c>. A raw sensor-sized buffer with a
/// centered <c>ActiveArea</c>/<c>DefaultCropArea</c> sub-rect detects
/// off-by-one crop bugs directly: everything outside the crop rect is
/// filled with a mid-gray "poison" value that must never survive the
/// render, and the cropped interior is solid black with a known-width
/// white border at its exact edges.
///
/// <para>Fixture DNG is also written to <c>tests/testdata/synthetic/</c> for
/// manual inspection or native-tool comparison — see
/// <c>dng_sdk_1_7_1/dng_sdk/targets/win/release64_x64/dng_validate.exe</c>.</para>
/// </summary>
public class BorderCropMarginDngTests
{
    private const int RawSize = 2000;
    private const int Margin = 200;
    private const int InnerSize = RawSize - 2 * Margin; // 1600
    private const int BorderPx = 4;

    // Poison (~15% raw fraction) renders to a distinct mid-brightness band
    // between black and white — see BorderedActiveArea's remarks for why a
    // naive 50% mid-gray wouldn't work given the default tone curve's
    // highlight compression. Thresholds below carve out dark/light zones
    // with wide margins on both sides of the observed poison brightness.
    private const double DarkThreshold = 90.0;
    private const double LightThreshold = 190.0;

    private static readonly string TestDataDir = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "tests", "testdata", "synthetic"));

    /// <summary>
    /// Writes the border/crop-margin fixture DNG to
    /// <c>tests/testdata/synthetic/</c>.
    /// </summary>
    [Fact]
    public void BorderCrop_fixture_is_written_to_testdata_synthetic()
    {
        Directory.CreateDirectory(TestDataDir);

        var dngBytes = SyntheticDngBuilder.BuildBorderCropDng(RawSize, Margin, InnerSize, BorderPx);
        var dngPath = Path.Combine(TestDataDir, "border_crop_2000x2000.dng");
        File.WriteAllBytes(dngPath, dngBytes);

        Assert.True(File.Exists(dngPath));
        Assert.True(new FileInfo(dngPath).Length > 1000);
    }

    [Fact]
    public void Crop_excludes_sensor_padding_and_yields_exact_inner_size()
    {
        var dngBytes = SyntheticDngBuilder.BuildBorderCropDng(RawSize, Margin, InnerSize, BorderPx);
        var dngPath = Path.Combine(Path.GetTempPath(), $"dng_bordercrop_{Guid.NewGuid():N}.dng");
        var jpegPath = Path.Combine(Path.GetTempPath(), $"dng_bordercrop_{Guid.NewGuid():N}.jpg");
        try
        {
            File.WriteAllBytes(dngPath, dngBytes);

            int exitCode = Cli.Run(["-jpeg", jpegPath, dngPath]);
            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(jpegPath));

            using var bitmap = SKBitmap.Decode(jpegPath);
            Assert.NotNull(bitmap);

            // The render must be cropped to exactly the DefaultCropArea/
            // ActiveArea sub-rect — no sensor padding leaking through as
            // extra rows/columns.
            Assert.Equal(InnerSize, bitmap.Width);
            Assert.Equal(InnerSize, bitmap.Height);

            // No poison mid-gray anywhere in the output: sample a grid of
            // points and assert every one classifies cleanly as dark or
            // light, never mid-gray (which would mean sensor padding leaked
            // into the crop, or the crop rect itself is misaligned).
            const int step = 25;
            for (int row = 0; row < bitmap.Height; row += step)
            {
                for (int col = 0; col < bitmap.Width; col += step)
                {
                    double b = Brightness(bitmap.GetPixel(col, row));
                    Assert.True(
                        b <= DarkThreshold || b >= LightThreshold,
                        $"Pixel ({col},{row}) has mid-gray brightness {b}, " +
                        "indicating sensor padding ('poison' value) leaked " +
                        "past the crop boundary — an off-by-one ActiveArea/" +
                        "DefaultCropArea bug.");
                }
            }

            // Border along the top edge: exactly BorderPx white rows, then
            // black. Same tolerance rationale as GradientDngTests/CircleDngTests
            // (a couple of pixels for JPEG quantization/anti-aliasing, not a
            // real off-by-one).
            const int tolerance = 2;
            int midCol = bitmap.Width / 2;
            int topBorderWidth = MeasureRun(row => Brightness(bitmap.GetPixel(midCol, row)) >= LightThreshold, bitmap.Height);
            int bottomBorderWidth = MeasureRun(row => Brightness(bitmap.GetPixel(midCol, bitmap.Height - 1 - row)) >= LightThreshold, bitmap.Height);
            int midRow = bitmap.Height / 2;
            int leftBorderWidth = MeasureRun(col => Brightness(bitmap.GetPixel(col, midRow)) >= LightThreshold, bitmap.Width);
            int rightBorderWidth = MeasureRun(col => Brightness(bitmap.GetPixel(bitmap.Width - 1 - col, midRow)) >= LightThreshold, bitmap.Width);

            foreach (var (name, width) in new[]
            {
                ("top", topBorderWidth), ("bottom", bottomBorderWidth),
                ("left", leftBorderWidth), ("right", rightBorderWidth),
            })
            {
                Assert.True(
                    System.Math.Abs(width - BorderPx) <= tolerance,
                    $"{name} border measured {width}px, expected {BorderPx}px " +
                    $"(±{tolerance}). A mismatch indicates an off-by-one crop " +
                    "bug shifting the crop rect relative to the border pattern.");
            }
        }
        finally
        {
            if (File.Exists(dngPath)) File.Delete(dngPath);
            if (File.Exists(jpegPath)) File.Delete(jpegPath);
        }
    }

    private static double Brightness(SKColor px) => (px.Red + px.Green + px.Blue) / 3.0;

    /// <summary>Counts the leading run of samples satisfying <paramref name="predicate"/>.</summary>
    private static int MeasureRun(Func<int, bool> predicate, int count)
    {
        int run = 0;
        while (run < count && predicate(run)) run++;
        return run;
    }
}
