using SkiaSharp;

namespace DngSharp.Dng.Sdk.Tests.TestImages;

/// <summary>
/// Geometric synthetic-fixture regression test: TODO.md's
/// <c>test-orientation-tags</c>. The same gradient fixture used by
/// <see cref="GradientDngTests"/> is re-tagged with each of the 9 defined
/// TIFF/EXIF <c>Orientation</c> values (1-8 plus 9=unknown; see
/// <see cref="DngSharp.Dng.Sdk.Primitives.DngOrientation.FromTiff"/>).
///
/// <para><b>Current scope:</b> the render pipeline (<c>DngSharp.Dng.Validate.Program</c>)
/// does not read or apply the <c>Orientation</c> tag at all today — it always
/// emits pixels in raw sensor row/column order. This test locks in that
/// documented behavior: every orientation value renders identically (same
/// dimensions, same pixel content), so a future change that starts applying
/// <c>Orientation</c> for some values but not others (an inconsistent partial
/// implementation) is caught immediately. When <c>Orientation</c> support is
/// added, this test should be rewritten to assert the *transformed* output
/// per tag value instead.</para>
/// </summary>
public class OrientationTagDngTests
{
    private const int Width = 400;
    private const int Height = 300;

    // All defined TIFF Orientation tag values: 1-8 are the 8 dihedral-group
    // transforms, 9 is "unknown" (DngOrientation.Unknown).
    public static IEnumerable<object[]> AllOrientationValues =>
        Enumerable.Range(1, 9).Select(v => new object[] { (ushort)v });

    [Theory]
    [MemberData(nameof(AllOrientationValues))]
    public void Renders_successfully_for_every_orientation_value(ushort tiffOrientation)
    {
        var dngBytes = SyntheticDngBuilder.BuildOrientedGradientDng(Width, Height, tiffOrientation);
        var dngPath = Path.Combine(Path.GetTempPath(), $"dng_orient_{tiffOrientation}_{Guid.NewGuid():N}.dng");
        var jpegPath = Path.Combine(Path.GetTempPath(), $"dng_orient_{tiffOrientation}_{Guid.NewGuid():N}.jpg");
        try
        {
            File.WriteAllBytes(dngPath, dngBytes);

            int exitCode = Cli.Run(["-jpeg", jpegPath, dngPath]);
            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(jpegPath));

            using var bitmap = SKBitmap.Decode(jpegPath);
            Assert.NotNull(bitmap);

            // Documents current scope: dimensions are never swapped for any
            // Orientation value (a 90°/270° rotation, if applied, would swap
            // width/height). If this starts failing for some tag values but
            // not others, orientation handling has been added inconsistently.
            Assert.Equal(Width, bitmap.Width);
            Assert.Equal(Height, bitmap.Height);
        }
        finally
        {
            if (File.Exists(dngPath)) File.Delete(dngPath);
            if (File.Exists(jpegPath)) File.Delete(jpegPath);
        }
    }

    [Fact]
    public void All_orientation_values_render_pixel_identical_content()
    {
        // Renders the gradient with Orientation=1 (Normal) as the baseline,
        // then re-renders with every other defined value and asserts the
        // rendered pixels are identical. Since the renderer doesn't apply
        // Orientation today, this must hold — a divergence would mean some
        // code path started reacting to the tag inconsistently.
        using var baseline = Render(tiffOrientation: 1);

        for (ushort tiffOrientation = 2; tiffOrientation <= 9; tiffOrientation++)
        {
            using var candidate = Render(tiffOrientation);
            Assert.Equal(baseline.Width, candidate.Width);
            Assert.Equal(baseline.Height, candidate.Height);

            // Sample a coarse grid rather than every pixel (JPEG re-encode
            // per file introduces a little independent quantization noise;
            // a coarse grid with a small tolerance is enough to catch a real
            // orientation transform being applied to some tag values).
            const int step = 20;
            const int tolerance = 6;
            for (int row = 0; row < baseline.Height; row += step)
            {
                for (int col = 0; col < baseline.Width; col += step)
                {
                    var a = baseline.GetPixel(col, row);
                    var b = candidate.GetPixel(col, row);
                    double da = (a.Red + a.Green + a.Blue) / 3.0;
                    double db = (b.Red + b.Green + b.Blue) / 3.0;
                    Assert.True(
                        System.Math.Abs(da - db) <= tolerance,
                        $"Orientation={tiffOrientation} pixel ({col},{row}) brightness " +
                        $"{db} diverges from Orientation=1 baseline {da} by more than " +
                        $"{tolerance}. Orientation handling appears to have been added " +
                        "inconsistently across tag values.");
                }
            }
        }
    }

    private static SKBitmap Render(ushort tiffOrientation)
    {
        var dngBytes = SyntheticDngBuilder.BuildOrientedGradientDng(Width, Height, tiffOrientation);
        var dngPath = Path.Combine(Path.GetTempPath(), $"dng_orient_{tiffOrientation}_{Guid.NewGuid():N}.dng");
        var jpegPath = Path.Combine(Path.GetTempPath(), $"dng_orient_{tiffOrientation}_{Guid.NewGuid():N}.jpg");
        try
        {
            File.WriteAllBytes(dngPath, dngBytes);
            int exitCode = Cli.Run(["-jpeg", jpegPath, dngPath]);
            Assert.Equal(0, exitCode);
            var bitmap = SKBitmap.Decode(jpegPath);
            Assert.NotNull(bitmap);
            return bitmap;
        }
        finally
        {
            if (File.Exists(dngPath)) File.Delete(dngPath);
            if (File.Exists(jpegPath)) File.Delete(jpegPath);
        }
    }
}
