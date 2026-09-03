using Dng.Sdk.Container;
using Dng.Sdk.Imaging.Profile;
using Dng.Sdk.IO;
using Dng.Sdk.Render;

namespace Dng.Sdk.Tests.Render;

/// <summary>Diagnostic: verify white balance matrix for real DNG.</summary>
public class WbMatrixDiagTests
{
    private static readonly string ImagesDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "images"));

    [Fact]
    public void WhiteBalance_neutral_maps_to_D50_white_when_applied()
    {
        var dng = Path.Combine(ImagesDir, "IMG_3353-HDR.dng");
        if (!File.Exists(dng)) return;

        using var stream = DngFileStream.OpenRead(dng);
        var container = DngContainer.Parse(stream);
        bool be = container.Header.BigEndian;
        var sharedIfd = container.TopLevelIfds[0];
        var shared = new Dng.Sdk.Metadata.DngShared();
        var profile = CameraProfileReader.Read(stream, sharedIfd, be, shared);

        Assert.NotNull(shared.AsShotNeutral);
        var n = shared.AsShotNeutral!;
        Assert.Equal(3, n.Count);
        Assert.InRange(n[1], 0.99, 1.01);

        var m = Stage3Renderer.ResolveCameraToXyzD50(profile, shared);

        double X = m[0, 0] * n[0] + m[0, 1] * n[1] + m[0, 2] * n[2];
        double Y = m[1, 0] * n[0] + m[1, 1] * n[1] + m[1, 2] * n[2];
        double Z = m[2, 0] * n[0] + m[2, 1] * n[1] + m[2, 2] * n[2];

        // Write diagnostics to a file so PowerShell can read them
        var diagLines = new[]
        {
            $"AsShotNeutral: {n[0]:F4} {n[1]:F4} {n[2]:F4}",
            $"cameraToXyzD50 row0: {m[0,0]:F4} {m[0,1]:F4} {m[0,2]:F4}",
            $"cameraToXyzD50 row1: {m[1,0]:F4} {m[1,1]:F4} {m[1,2]:F4}",
            $"cameraToXyzD50 row2: {m[2,0]:F4} {m[2,1]:F4} {m[2,2]:F4}",
            $"Neutral->XYZ_D50: X={X:F4} Y={Y:F4} Z={Z:F4}",
        };
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "wb_diag.txt"), diagLines);

        // Normalised chromaticity should be close to D50 white
        Assert.True(Y > 0.01, $"Y={Y:F4} is too small — white balance may not be applied");
        if (Y > 0.001)
        {
            Assert.InRange(X / Y, 0.80, 1.10);
            Assert.InRange(Z / Y, 0.50, 1.20);
        }
    }
}
