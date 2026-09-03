using SkiaSharp;

namespace Dng.Sdk.Tests.TestImages;

/// <summary>
/// End-to-end regression test built on
/// <see cref="SyntheticDngBuilder.BuildCenteredCircleDng"/>'s analytically-known
/// fixture: a white background with a black circle centered in a square
/// image, covering 50% of both width and height. Because the authored shape
/// is a true circle on a square canvas, any anisotropic (non-uniform
/// horizontal vs. vertical) scaling introduced by the render pipeline —
/// e.g. a crop/orientation/active-area bug that stretches one axis relative
/// to the other — shows up directly as the measured circle width and height
/// no longer matching, and as unequal margins on opposite/adjacent sides.
///
/// <para>Fixture DNG/TIFF are also written to <c>tests/testdata/synthetic/</c>
/// for manual inspection or native-tool comparison — see
/// <c>dng_sdk_1_7_1/dng_sdk/targets/win/release64_x64/dng_validate.exe</c>.</para>
/// </summary>
public class CircleDngTests
{
    private const int Size = 4000;
    private const double DiameterFraction = 0.5;

    // Midpoint brightness between the near-black circle (lifted by the
    // current default-tone-curve shadow lift to ~47/255 — see
    // GradientDngTests / the 'acr3-default-tone-curve' todo) and pure white
    // (255). Used to classify each sampled pixel as "circle" or "background".
    private const double DarkThreshold = 150.0;

    private static readonly string TestDataDir = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "tests", "testdata", "synthetic"));

    /// <summary>
    /// Writes the circle fixture (DNG + companion plain TIFF) to
    /// <c>tests/testdata/synthetic/</c>.
    /// </summary>
    [Fact]
    public void Circle_fixture_is_written_to_testdata_synthetic()
    {
        Directory.CreateDirectory(TestDataDir);

        var dngBytes = SyntheticDngBuilder.BuildCenteredCircleDng(Size, DiameterFraction);
        var dngPath = Path.Combine(TestDataDir, "circle_4000x4000.dng");
        File.WriteAllBytes(dngPath, dngBytes);

        Assert.True(File.Exists(dngPath));
        Assert.True(new FileInfo(dngPath).Length > 1000);

        var tiffBytes = SyntheticTiffBuilder.BuildCenteredCircleTiff(Size, DiameterFraction);
        var tiffPath = Path.Combine(TestDataDir, "circle_4000x4000.tiff");
        File.WriteAllBytes(tiffPath, tiffBytes);

        Assert.True(File.Exists(tiffPath));
        Assert.True(new FileInfo(tiffPath).Length > 1000);
    }

    [Fact]
    public void Circle_renders_without_anisotropic_stretching()
    {
        var dngBytes = SyntheticDngBuilder.BuildCenteredCircleDng(Size, DiameterFraction);
        var dngPath = Path.Combine(Path.GetTempPath(), $"dng_circle_{Guid.NewGuid():N}.dng");
        var jpegPath = Path.Combine(Path.GetTempPath(), $"dng_circle_{Guid.NewGuid():N}.jpg");
        try
        {
            File.WriteAllBytes(dngPath, dngBytes);

            int exitCode = Cli.Run(["-jpeg", jpegPath, dngPath]);
            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(jpegPath));

            using var bitmap = SKBitmap.Decode(jpegPath);
            Assert.NotNull(bitmap);
            Assert.Equal(Size, bitmap.Width);
            Assert.Equal(Size, bitmap.Height);

            int midRow = bitmap.Height / 2;
            int midCol = bitmap.Width / 2;

            (int first, int last) horizontalRun = FindDarkRun(
                i => Brightness(bitmap.GetPixel(i, midRow)), bitmap.Width);
            (int first, int last) verticalRun = FindDarkRun(
                i => Brightness(bitmap.GetPixel(midCol, i)), bitmap.Height);

            Assert.True(horizontalRun.first >= 0 && verticalRun.first >= 0,
                "Expected to find a dark circle region along both the " +
                "horizontal and vertical mid-lines, but found none.");

            int circleWidth = horizontalRun.last - horizontalRun.first + 1;
            int circleHeight = verticalRun.last - verticalRun.first + 1;

            int leftMargin = horizontalRun.first;
            int rightMargin = bitmap.Width - 1 - horizontalRun.last;
            int topMargin = verticalRun.first;
            int bottomMargin = bitmap.Height - 1 - verticalRun.last;

            // Tolerance for anti-aliasing/JPEG-quantization edge softness at
            // the circle boundary — a handful of pixels, not a stretch bug.
            const int tolerance = 8;

            Assert.True(System.Math.Abs(circleWidth - circleHeight) <= tolerance,
                $"Circle width ({circleWidth}px) and height ({circleHeight}px) " +
                "should match on a square canvas; a mismatch indicates " +
                "anisotropic (aspect-distorting) stretching in the render " +
                "pipeline.");

            Assert.True(System.Math.Abs(leftMargin - rightMargin) <= tolerance,
                $"Left margin ({leftMargin}px) and right margin ({rightMargin}px) " +
                "should match for a horizontally-centered circle.");

            Assert.True(System.Math.Abs(topMargin - bottomMargin) <= tolerance,
                $"Top margin ({topMargin}px) and bottom margin ({bottomMargin}px) " +
                "should match for a vertically-centered circle.");

            Assert.True(System.Math.Abs(leftMargin - topMargin) <= tolerance,
                $"Left/right margin ({leftMargin}px) and top/bottom margin " +
                $"({topMargin}px) should match — both should equal " +
                $"(1 - {DiameterFraction:0.0}) / 2 of the canvas size, " +
                "regardless of axis. A mismatch indicates the circle's " +
                "bounding box is off-center or non-uniformly scaled.");

            // Sanity: the circle should approximately cover the requested
            // fraction of the canvas (loose tolerance — this test's main
            // purpose is the equality checks above, not exact sizing).
            double expectedDiameter = Size * DiameterFraction;
            Assert.True(
                System.Math.Abs(circleWidth - expectedDiameter) <= expectedDiameter * 0.05,
                $"Circle width ({circleWidth}px) should be close to the " +
                $"requested diameter ({expectedDiameter}px).");
        }
        finally
        {
            if (File.Exists(dngPath)) File.Delete(dngPath);
            if (File.Exists(jpegPath)) File.Delete(jpegPath);
        }
    }

    private static double Brightness(SKColor px) => (px.Red + px.Green + px.Blue) / 3.0;

    /// <summary>
    /// Scans <paramref name="count"/> samples via <paramref name="brightnessAt"/>
    /// and returns the (first, last) index of the contiguous dark run
    /// straddling the center — i.e. the circle's extent along this line.
    /// Returns (-1, -1) if no dark run is found at the center.
    /// </summary>
    private static (int First, int Last) FindDarkRun(Func<int, double> brightnessAt, int count)
    {
        int mid = count / 2;
        if (brightnessAt(mid) >= DarkThreshold) return (-1, -1);

        int first = mid;
        while (first > 0 && brightnessAt(first - 1) < DarkThreshold) first--;

        int last = mid;
        while (last < count - 1 && brightnessAt(last + 1) < DarkThreshold) last++;

        return (first, last);
    }
}
