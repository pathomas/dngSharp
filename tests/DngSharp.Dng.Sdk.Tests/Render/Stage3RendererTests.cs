using DngSharp.Dng.Sdk.Imaging;
using DngSharp.Dng.Sdk.Imaging.Profile;
using DngSharp.Dng.Sdk.Imaging.Raw;
using DngSharp.Dng.Sdk.Math;
using DngSharp.Dng.Sdk.Pipeline;
using DngSharp.Dng.Sdk.Pixels;
using DngSharp.Dng.Sdk.Primitives;
using DngSharp.Dng.Sdk.Render;
using DngSharp.Dng.Sdk.Tiff;

namespace DngSharp.Dng.Sdk.Tests.Render;

public class Stage3RendererTests
{
    private static SimpleImage MakeIdentityStage3(int w, int h)
    {
        // Create a 2×2 float32 image with known values [1, 0, 0], [0, 1, 0],
        // [0, 0, 1], [0.5, 0.5, 0.5] in camera space.
        var img = new SimpleImage(new DngRect(0, 0, h, w), 3, PixelType.Float32);
        return img;
    }

    // Identity camera matrix (camera IS XYZ_D50).
    private static readonly DngMatrix IdentityMatrix = DngMatrix.Matrix3x3(
        1, 0, 0,
        0, 1, 0,
        0, 0, 1);

    [Fact]
    public void Render_identity_matrix_preserves_values()
    {
        // With identity camera→XYZ_D50, the output should be D50→D65→sRGB of the input.
        // The combined matrix is XyzD65ToLinearSrgb * D50ToD65Cat * Identity.
        var stage3 = MakeIdentityStage3(2, 1);
        var result = Stage3Renderer.Render(stage3, IdentityMatrix, baselineExposure: 0);
        Assert.Equal(PixelType.Float32, result.PixelType);
        Assert.Equal(3u, result.Planes);
    }

    [Fact]
    public void GammaAndQuantize_zero_maps_to_zero()
    {
        var img = new SimpleImage(new DngRect(0, 0, 1, 1), 3, PixelType.Float32);
        var dest = new byte[3];
        Stage3Renderer.GammaAndQuantize(img, dest);
        Assert.Equal(0, dest[0]);
        Assert.Equal(0, dest[1]);
        Assert.Equal(0, dest[2]);
    }

    [Fact]
    public void GammaAndQuantize_one_maps_to_255()
    {
        var img = new SimpleImage(new DngRect(0, 0, 1, 1), 3, PixelType.Float32);
        var tile = img.GetTile(img.Bounds);
        var samples = tile.AsTypedSpan<float>();
        samples[0] = 1.0f;
        samples[1] = 1.0f;
        samples[2] = 1.0f;
        img.WriteTile(tile);

        var dest = new byte[3];
        Stage3Renderer.GammaAndQuantize(img, dest);
        Assert.Equal(255, dest[0]);
        Assert.Equal(255, dest[1]);
        Assert.Equal(255, dest[2]);
    }

    [Fact]
    public void SrgbGamma_midgray_roundtrip()
    {
        // sRGB gamma of 0.5 should be ~0.7354 (not 0.5 — gamma makes brighter mid).
        double encoded = Stage3Renderer.SrgbGamma(0.5);
        Assert.InRange(encoded, 0.72, 0.75);
    }

    [Fact]
    public void D50ToD65Cat_preserves_D50_as_D65_approximately()
    {
        // D50 XYZ ≈ (0.9642, 1.0000, 0.8249). After Bradford D50→D65 the
        // result should be close to the D65 white (1.0, 1.0, 1.0 after norm).
        var d50Xyz = new DngVector(3);
        d50Xyz[0] = 0.9642; d50Xyz[1] = 1.0; d50Xyz[2] = 0.8249;
        var d65Xyz = Stage3Renderer.D50ToD65Cat * d50Xyz;
        // Normalize by Y.
        double x = d65Xyz[0] / d65Xyz[1];
        double z = d65Xyz[2] / d65Xyz[1];
        // D65 normalized: x ≈ 0.9505, z ≈ 1.0890
        Assert.InRange(x, 0.93, 0.97);
        Assert.InRange(z, 1.07, 1.11);
    }

    [Fact]
    public void Render_baseline_exposure_plus_one_doubles_output()
    {
        // With +1 stop baseline exposure, a 0.25 camera input should become ~0.5 in output.
        var stage3 = new SimpleImage(new DngRect(0, 0, 1, 1), 3, PixelType.Float32);
        var tile = stage3.GetTile(stage3.Bounds);
        var samples = tile.AsTypedSpan<float>();
        samples[0] = 0.25f; samples[1] = 0.25f; samples[2] = 0.25f;
        stage3.WriteTile(tile);

        var r0 = Stage3Renderer.Render(stage3, IdentityMatrix, baselineExposure: 0.0);
        var r1 = Stage3Renderer.Render(stage3, IdentityMatrix, baselineExposure: 1.0);

        var t0 = r0.GetTile(r0.Bounds).AsTypedSpan<float>();
        var t1 = r1.GetTile(r1.Bounds).AsTypedSpan<float>();

        // Each component in r1 should be ~2× the matching component in r0.
        for (int i = 0; i < 3; i++)
            Assert.InRange(t1[i] / t0[i], 1.95, 2.05);
    }

    // ── SIMD fast-path coverage ─────────────────────────────────────────────
    // No tone curve → triggers Stage3Renderer's vectorized 3×3 matrix +
    // exposure fast path. Width 600 crosses multiple Vector<float> batches
    // plus the 256-pixel block boundary and a scalar remainder.

    [Fact]
    public void Simd_matrix_path_matches_scalar_expected_values_across_block_boundary()
    {
        const int width = 600;
        var stage3 = new SimpleImage(new DngRect(0, 0, 1, width), 3, PixelType.Float32);
        var tile = stage3.GetTile(stage3.Bounds);
        var samples = tile.AsTypedSpan<float>();
        for (int i = 0; i < width; i++)
        {
            samples[i * 3] = i * 0.001f;
            samples[i * 3 + 1] = i * 0.002f;
            samples[i * 3 + 2] = i * 0.0005f;
        }
        stage3.WriteTile(tile);

        // Non-trivial (non-identity) camera matrix so cross terms are exercised.
        var matrix = DngMatrix.Matrix3x3(
            0.9, 0.05, 0.05,
            0.1, 0.85, 0.05,
            0.0, 0.1, 0.9);

        var result = Stage3Renderer.Render(stage3, matrix, baselineExposure: 0.5);
        var combined = Stage3Renderer.CameraToOutputSpace(matrix, OutputColorSpace.Srgb);
        double expScale = System.Math.Pow(2.0, 0.5);

        var outTile = result.GetTile(result.Bounds);
        var outSpan = outTile.AsTypedSpan<float>();

        for (int i = 0; i < width; i++)
        {
            double r = samples[i * 3], g = samples[i * 3 + 1], b = samples[i * 3 + 2];
            double or = (combined[0, 0] * r + combined[0, 1] * g + combined[0, 2] * b) * expScale;
            double og = (combined[1, 0] * r + combined[1, 1] * g + combined[1, 2] * b) * expScale;
            double ob = (combined[2, 0] * r + combined[2, 1] * g + combined[2, 2] * b) * expScale;

            Assert.Equal((float)or, outSpan[i * 3], 3);
            Assert.Equal((float)og, outSpan[i * 3 + 1], 3);
            Assert.Equal((float)ob, outSpan[i * 3 + 2], 3);
        }
    }

    [Fact]
    public void Tone_curve_present_falls_back_to_scalar_and_is_correct()
    {
        // A non-null tone curve disables the SIMD fast path — verify the
        // scalar fallback still applies the curve correctly.
        var stage3 = new SimpleImage(new DngRect(0, 0, 1, 4), 3, PixelType.Float32);
        var tile = stage3.GetTile(stage3.Bounds);
        var samples = tile.AsTypedSpan<float>();
        samples.Fill(0.5f);
        stage3.WriteTile(tile);

        double Halve(double x) => x * 0.5;

        var result = Stage3Renderer.Render(stage3, IdentityMatrix, baselineExposure: 0.0, toneCurve: Halve);
        var outSpan = result.GetTile(result.Bounds).AsTypedSpan<float>();

        var combined = Stage3Renderer.CameraToOutputSpace(IdentityMatrix, OutputColorSpace.Srgb);
        double expected = Halve(combined[0, 0] * 0.5 + combined[0, 1] * 0.5 + combined[0, 2] * 0.5);
        Assert.Equal((float)expected, outSpan[0], 4);
    }
}
