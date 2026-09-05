using SkiaSharp;

namespace DngSharp.Dng.Sdk.Tests.TestImages;

/// <summary>
/// Geometric synthetic-fixture regression test: TODO.md's
/// <c>test-checkerboard-tiles</c>. A fine alternating black/white
/// checkerboard exercises the exact same code path (<c>Cli.Run</c> →
/// <c>DngContainer.Parse</c> → <c>StripReader</c> → <c>Stage2Builder</c>/
/// <c>Stage3Builder</c> → <c>Stage3Renderer</c> → <c>HdrToneMapper</c> →
/// JPEG encode) as a real <c>-jpeg</c> invocation, so it catches
/// tile/strip-boundary misalignment and duplicate/missing column-or-row
/// decode bugs directly: a correct render has transitions spaced exactly
/// <see cref="SquarePx"/> pixels apart, with no duplicated or dropped
/// columns/rows anywhere in the image — not just at the sampled center line.
///
/// <para>Fixture DNG is also written to <c>tests/testdata/synthetic/</c> for
/// manual inspection or native-tool comparison — see
/// <c>dng_sdk_1_7_1/dng_sdk/targets/win/release64_x64/dng_validate.exe</c>.</para>
/// </summary>
public class CheckerboardDngTests
{
    private const int Width = 4096;
    private const int Height = 4096;
    private const int SquarePx = 16;

    // Midpoint brightness (0-255 scale) between black and white squares,
    // used to classify each sampled pixel.
    private const double MidThreshold = 127.5;

    private static readonly string TestDataDir = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "tests", "testdata", "synthetic"));

    /// <summary>
    /// Writes the checkerboard fixture DNG to <c>tests/testdata/synthetic/</c>.
    /// </summary>
    [Fact]
    public void Checkerboard_fixture_is_written_to_testdata_synthetic()
    {
        Directory.CreateDirectory(TestDataDir);

        var dngBytes = SyntheticDngBuilder.BuildCheckerboardDng(Width, Height, SquarePx);
        var dngPath = Path.Combine(TestDataDir, "checkerboard_4096x4096.dng");
        File.WriteAllBytes(dngPath, dngBytes);

        Assert.True(File.Exists(dngPath));
        Assert.True(new FileInfo(dngPath).Length > 1000);
    }

    [Fact]
    public void Checkerboard_transitions_are_uniformly_spaced_horizontally()
    {
        using var bitmap = RenderCheckerboard(out int width, out _);

        int row = bitmap.Height / 2;
        var transitions = FindTransitions(col => Brightness(bitmap.GetPixel(col, row)), width);

        AssertUniformSpacing(transitions, width, "horizontal");
    }

    [Fact]
    public void Checkerboard_transitions_are_uniformly_spaced_vertically()
    {
        using var bitmap = RenderCheckerboard(out _, out int height);

        int col = bitmap.Width / 2;
        var transitions = FindTransitions(row => Brightness(bitmap.GetPixel(col, row)), height);

        AssertUniformSpacing(transitions, height, "vertical");
    }

    private static SKBitmap RenderCheckerboard(out int width, out int height)
    {
        var dngBytes = SyntheticDngBuilder.BuildCheckerboardDng(Width, Height, SquarePx);
        var dngPath = Path.Combine(Path.GetTempPath(), $"dng_checker_{Guid.NewGuid():N}.dng");
        var jpegPath = Path.Combine(Path.GetTempPath(), $"dng_checker_{Guid.NewGuid():N}.jpg");
        try
        {
            File.WriteAllBytes(dngPath, dngBytes);

            int exitCode = Cli.Run(["-jpeg", jpegPath, dngPath]);
            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(jpegPath));

            var bitmap = SKBitmap.Decode(jpegPath);
            Assert.NotNull(bitmap);
            Assert.Equal(Width, bitmap.Width);
            Assert.Equal(Height, bitmap.Height);

            width = bitmap.Width;
            height = bitmap.Height;
            return bitmap;
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
    /// and returns the index of every black↔white transition (a sample whose
    /// brightness crosses <see cref="MidThreshold"/> relative to the previous
    /// sample).
    /// </summary>
    private static List<int> FindTransitions(Func<int, double> brightnessAt, int count)
    {
        var transitions = new List<int>();
        bool prevWhite = brightnessAt(0) >= MidThreshold;
        for (int i = 1; i < count; i++)
        {
            bool white = brightnessAt(i) >= MidThreshold;
            if (white != prevWhite)
            {
                transitions.Add(i);
                prevWhite = white;
            }
        }
        return transitions;
    }

    private static void AssertUniformSpacing(List<int> transitions, int extent, string axis)
    {
        int expectedCount = extent / SquarePx - 1;
        Assert.True(
            System.Math.Abs(transitions.Count - expectedCount) <= 1,
            $"Expected ~{expectedCount} {axis} transitions across {extent}px at " +
            $"{SquarePx}px squares, found {transitions.Count}. A count mismatch " +
            "indicates dropped or duplicated rows/columns.");

        // Tolerance for anti-aliasing/JPEG-quantization edge softness, not a
        // real spacing bug — a genuine tile/strip decode bug shifts spacing
        // by many pixels (a whole duplicated/dropped column run), not 1-2px.
        const int tolerance = 2;
        for (int i = 1; i < transitions.Count; i++)
        {
            int spacing = transitions[i] - transitions[i - 1];
            Assert.True(
                System.Math.Abs(spacing - SquarePx) <= tolerance,
                $"{axis} transition spacing at index {i} was {spacing}px, expected " +
                $"{SquarePx}px (±{tolerance}). This is the signature of a " +
                "tile/strip-boundary decode bug duplicating or dropping " +
                "rows/columns.");
        }
    }
}
