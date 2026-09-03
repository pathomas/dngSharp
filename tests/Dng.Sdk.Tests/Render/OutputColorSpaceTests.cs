using Dng.Sdk.Math;
using Dng.Sdk.Render;

namespace Dng.Sdk.Tests.Render;

public class OutputColorSpaceTests
{
    private static readonly DngMatrix IdentityMatrix = DngMatrix.Identity3x3();

    private static readonly DngMatrix ArbitraryCameraToXyzD50 = DngMatrix.Matrix3x3(
        0.75, 0.10, 0.05,
        0.20, 0.85, 0.10,
        0.05, 0.15, 0.95);

    [Fact]
    public void CameraToOutputSpace_srgb_matches_CameraToLinearSrgb()
    {
        var expected = Stage3Renderer.CameraToLinearSrgb(ArbitraryCameraToXyzD50);
        var actual = Stage3Renderer.CameraToOutputSpace(ArbitraryCameraToXyzD50, OutputColorSpace.Srgb);

        Assert.True(expected.AlmostEqual(actual, 1e-12));
    }

    [Fact]
    public void CameraToOutputSpace_prophoto_skips_cat()
    {
        var actual = Stage3Renderer.CameraToOutputSpace(IdentityMatrix, OutputColorSpace.ProPhotoRgb);
        var expected = Stage3Renderer.XyzD50ToLinearProPhoto * IdentityMatrix;
        var withCat = Stage3Renderer.XyzD50ToLinearProPhoto * Stage3Renderer.D50ToD65Cat * IdentityMatrix;

        Assert.True(expected.AlmostEqual(actual, 1e-12));
        Assert.False(withCat.AlmostEqual(actual, 1e-6));
    }

    [Fact]
    public void CameraToOutputSpace_all_spaces_produce_valid_matrices()
    {
        foreach (OutputColorSpace colorSpace in Enum.GetValues<OutputColorSpace>())
        {
            var matrix = Stage3Renderer.CameraToOutputSpace(IdentityMatrix, colorSpace);

            Assert.Equal(3, matrix.Rows);
            Assert.Equal(3, matrix.Cols);

            for (int row = 0; row < matrix.Rows; row++)
            {
                for (int col = 0; col < matrix.Cols; col++)
                {
                    Assert.True(double.IsFinite(matrix[row, col]),
                        $"Matrix entry [{row},{col}] for {colorSpace} was not finite.");
                }
            }
        }
    }

    [Fact]
    public void ParseOptions_cs2_sets_adobe_rgb()
    {
        var options = Cli.ParseOptions(["-cs2", "input.dng"]);

        Assert.Equal(OutputColorSpace.AdobeRgb, options.ColorSpace);
    }
}
