using Xunit.Abstractions;
using DngSharp.Dng.Sdk.Imaging;
using DngSharp.Dng.Sdk.Pipeline;
using DngSharp.Dng.Sdk.Pixels;
using DngSharp.Dng.Sdk.Primitives;
using DngSharp.Dng.Sdk.Render;
using DngSharp.Dng.Sdk.Tiff;

namespace DngSharp.Dng.Sdk.Gpu.Tests;

/// <summary>
/// Stage-by-stage GPU-vs-CPU equivalence. The GPU kernels compute in
/// float32 where the CPU path uses double, so every comparison is
/// tolerance-based; the bounds below are ~10× the differences observed on
/// the CPU accelerator and the Quadro P520 (see docs/perf/phase10-gpu.md).
/// </summary>
public class GpuStageEquivalenceTests(GpuFixture gpu, ITestOutputHelper output) : IClassFixture<GpuFixture>
{
    private readonly GpuRenderPipeline _gpu = gpu.Pipeline;

    private void Log(string what, string diff) => output.WriteLine($"[{gpu.Device.Name}] {what}: {diff}");

    // ── Stage 2 ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Linearize_matches_Stage2Builder_for_2x2_black_repeat(bool lut, bool deltas)
    {
        var stage1 = Synthetic.BayerStage1();
        var lin = Synthetic.BayerLinearization(lut, deltas);

        var cpu = Stage2Builder.Build(stage1, lin);
        var dev = _gpu.Linearize(stage1, lin);

        var (max, mean) = Diff.Floats(cpu, dev);
        Log(nameof(Linearize_matches_Stage2Builder_for_2x2_black_repeat), $"max {max:E2} mean {mean:E2}");
        Assert.True(max < 1e-5, $"max {max:E2} mean {mean:E2}");
    }

    [Fact]
    public void Linearize_matches_Stage2Builder_for_three_plane_LinearRaw()
    {
        var stage1 = Synthetic.LinearRawStage1();
        var lin = Synthetic.LinearRawLinearization();

        var cpu = Stage2Builder.Build(stage1, lin);
        var dev = _gpu.Linearize(stage1, lin);

        var (max, _) = Diff.Floats(cpu, dev);
        Log(nameof(Linearize_matches_Stage2Builder_for_three_plane_LinearRaw), $"max {max:E2}");
        Assert.True(max < 1e-6, $"max {max:E2}");
    }

    [Fact]
    public void Linearize_clips_above_one_and_preserves_negative()
    {
        var stage1 = new SimpleImage(new DngRect(0, 0, 2, 4), 1, PixelType.UInt16);
        var px = stage1.Buffer.AsTypedSpan<ushort>();
        px[0] = 0; px[1] = 100; px[2] = 1000; px[3] = 65535;
        px[4] = 50; px[5] = 1100; px[6] = 1000; px[7] = 1050;
        var lin = new Imaging.Raw.LinearizationInfo { BlackLevel = [100], WhiteLevel = [1000] };

        var dev = _gpu.Linearize(stage1, lin);
        var f = dev.Buffer.AsTypedSpan<float>();

        Assert.True(f[0] < 0f);           // sub-zero preserved
        Assert.Equal(0f, f[1]);
        Assert.Equal(1f, f[2]);
        Assert.Equal(1f, f[3]);           // clipped
        Assert.Equal(1f, f[5]);
    }

    // ── Stage 3 ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(new byte[] { 0, 1, 1, 2 })] // RGGB
    [InlineData(new byte[] { 1, 0, 2, 1 })] // GRBG
    [InlineData(new byte[] { 2, 1, 1, 0 })] // BGGR
    [InlineData(new byte[] { 1, 2, 0, 1 })] // GBRG
    public void Demosaic_matches_DemosaicBilinear_including_edges(byte[] cfa)
    {
        var stage2 = Stage2Builder.Build(Synthetic.BayerStage1(), Synthetic.BayerLinearization());
        var mosaic = new Imaging.Raw.MosaicInfo { Pattern = (2, 2), CfaPlaneColor = cfa };

        var cpu = (SimpleImage)DemosaicBilinear.Build(stage2, mosaic);
        var dev = _gpu.Demosaic(stage2, mosaic);

        var (max, _) = Diff.Floats(cpu, dev);
        Log(nameof(Demosaic_matches_DemosaicBilinear_including_edges), $"max {max:E2}");
        Assert.True(max < 1e-6, $"max {max:E2}");
    }

    [Fact]
    public void Demosaic_handles_odd_sized_images()
    {
        var stage2 = Stage2Builder.Build(Synthetic.BayerStage1(37, 23), Synthetic.BayerLinearization());
        var cpu = (SimpleImage)DemosaicBilinear.Build(stage2, Synthetic.Rggb());
        var dev = _gpu.Demosaic(stage2, Synthetic.Rggb());
        var (max, _) = Diff.Floats(cpu, dev);
        Log(nameof(Demosaic_handles_odd_sized_images), $"max {max:E2}");
        Assert.True(max < 1e-6, $"max {max:E2}");
    }

    // ── Colour transform ──────────────────────────────────────────────────────

    private static SimpleImage CpuStage3() =>
        (SimpleImage)DemosaicBilinear.Build(
            Stage2Builder.Build(Synthetic.BayerStage1(), Synthetic.BayerLinearization()), Synthetic.Rggb());

    [Theory]
    [InlineData(OutputColorSpace.Srgb, 0.0, false)]
    [InlineData(OutputColorSpace.Srgb, 0.7, true)]
    [InlineData(OutputColorSpace.AdobeRgb, -0.3, true)]
    [InlineData(OutputColorSpace.ProPhotoRgb, 0.0, false)]
    [InlineData(OutputColorSpace.DisplayP3, 0.25, true)]
    [InlineData(OutputColorSpace.Rec2020, 0.0, true)]
    public void Render_without_HueSatMap_matches_Stage3Renderer(OutputColorSpace cs, double exposure, bool curve)
    {
        var stage3 = CpuStage3();
        var m = Synthetic.CameraToXyzD50();
        var tc = curve ? Synthetic.ToneCurve() : null;

        var cpu = Stage3Renderer.Render(stage3, m, exposure,
            toneCurve: tc is null ? null : x => HdrToneMapper.EvaluateCurve(x, tc), colorSpace: cs);
        var dev = _gpu.Render(stage3, new GpuRenderParams(m, exposure, cs, ToneCurve: tc));

        var (max, mean) = Diff.Floats(cpu, dev);
        Log(nameof(Render_without_HueSatMap_matches_Stage3Renderer), $"max {max:E2} mean {mean:E2}");
        Assert.True(max < 2e-5, $"max {max:E2} mean {mean:E2}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Render_with_HueSatMap_matches_Stage3Renderer(bool threeD)
    {
        var stage3 = CpuStage3();
        var m = Synthetic.CameraToXyzD50();
        var hsm = threeD ? Synthetic.HueSatMap3D() : Synthetic.HueSatMap25D();
        var tc = Synthetic.ToneCurve();

        var cpu = Stage3Renderer.Render(stage3, m, 0.4,
            toneCurve: x => HdrToneMapper.EvaluateCurve(x, tc), hueSatMap: hsm);
        var dev = _gpu.Render(stage3, new GpuRenderParams(m, 0.4, HueSatMap: hsm, ToneCurve: tc));

        // HSV round-trips in float32 lose a few ULPs around hue-sector
        // boundaries; the table interpolation amplifies that slightly.
        var (max, mean) = Diff.Floats(cpu, dev);
        Log(nameof(Render_with_HueSatMap_matches_Stage3Renderer), $"max {max:E2} mean {mean:E2}");
        Assert.True(max < 1e-3, $"max {max:E2} mean {mean:E2}");
        Log(nameof(Render_with_HueSatMap_matches_Stage3Renderer), $"max {max:E2} mean {mean:E2}");
        Assert.True(mean < 1e-5, $"max {max:E2} mean {mean:E2}");
    }

    [Fact]
    public void Render_crop_window_matches_cropped_CPU_render()
    {
        var stage3 = CpuStage3();
        var m = Synthetic.CameraToXyzD50();
        var crop = new DngRect(10, 17, Synthetic.Height - 5, Synthetic.Width - 3);

        var cpu = Stage3Renderer.Render(ImageCrop.Crop(stage3, crop), m, 0.2);
        var dev = _gpu.Render(stage3, new GpuRenderParams(m, 0.2), crop);

        Assert.Equal(new DngRect(0, 0, (int)crop.H, (int)crop.W), dev.Bounds);
        var (max, _) = Diff.Floats(cpu, dev);
        Log(nameof(Render_crop_window_matches_cropped_CPU_render), $"max {max:E2}");
        Assert.True(max < 2e-5, $"max {max:E2}");
    }

    // ── Tone map / gamma ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ToneMap_matches_HdrToneMapper(bool profileCurve)
    {
        var m = Synthetic.CameraToXyzD50();
        var cpu = Stage3Renderer.Render(CpuStage3(), m, 1.5); // push some values > 1
        var dev = ImageCrop.Crop(cpu, cpu.Bounds);            // independent copy
        var tc = profileCurve ? Synthetic.ToneCurve() : null;

        HdrToneMapper.Apply(cpu, tc);
        _gpu.ToneMap(dev, tc);

        var (max, _) = Diff.Floats(cpu, dev);
        Log(nameof(ToneMap_matches_HdrToneMapper), $"max {max:E2}");
        Assert.True(max < 1e-4, $"max {max:E2}");
    }

    [Fact]
    public void GammaAndQuantize_matches_within_one_lsb()
    {
        var rgb = Stage3Renderer.Render(CpuStage3(), Synthetic.CameraToXyzD50(), 0.3);
        HdrToneMapper.Apply(rgb, null);

        var cpu = new byte[rgb.Bounds.W * rgb.Bounds.H * 3];
        Stage3Renderer.GammaAndQuantize(rgb, cpu);
        var dev = _gpu.GammaAndQuantize(rgb);

        var (max, mean, _) = Diff.Bytes(cpu, dev);
        Log(nameof(GammaAndQuantize_matches_within_one_lsb), $"max {max} mean {mean:F4}");
        Assert.True(max <= 1, $"max {max} mean {mean:F4}");
        Log(nameof(GammaAndQuantize_matches_within_one_lsb), $"max {max} mean {mean:F4}");
        Assert.True(mean < 0.02, $"max {max} mean {mean:F4}");
    }

    // ── Resident end-to-end ───────────────────────────────────────────────────

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void RenderToRgb8_matches_full_CPU_pipeline(bool hueSat, bool curve)
    {
        var stage1 = Synthetic.BayerStage1();
        var lin = Synthetic.BayerLinearization(withLut: true);
        var mosaic = Synthetic.Rggb();
        var m = Synthetic.CameraToXyzD50();
        var hsm = hueSat ? Synthetic.HueSatMap25D() : null;
        var tc = curve ? Synthetic.ToneCurve() : null;
        var crop = new DngRect(8, 8, Synthetic.Height - 8, Synthetic.Width - 8);

        var cpu = CpuPipeline(stage1, lin, Photometric.Cfa, mosaic, m, 0.35, hsm, tc, crop);
        var dev = _gpu.RenderToRgb8(stage1, lin, Photometric.Cfa, mosaic,
            new GpuRenderParams(m, 0.35, HueSatMap: hsm, ToneCurve: tc), crop);

        Assert.Equal(cpu.Length, dev.Length);
        var (max, mean, over1) = Diff.Bytes(cpu, dev);
        Log(nameof(RenderToRgb8_matches_full_CPU_pipeline), $"max {max} mean {mean:F4} >1: {over1:P3}");
        Assert.True(max <= 2, $"max {max} mean {mean:F4} >1: {over1:P3}");
        Log(nameof(RenderToRgb8_matches_full_CPU_pipeline), $"max {max} mean {mean:F4} >1: {over1:P3}");
        Assert.True(over1 < 1e-3, $"max {max} mean {mean:F4} >1: {over1:P3}");
    }

    [Fact]
    public void RenderToRgb8_LinearRaw_passthrough_matches_CPU_pipeline()
    {
        var stage1 = Synthetic.LinearRawStage1();
        var lin = Synthetic.LinearRawLinearization();
        var m = Synthetic.CameraToXyzD50();

        var cpu = CpuPipeline(stage1, lin, Photometric.LinearRaw, null, m, 0.0, null, null, null);
        var dev = _gpu.RenderToRgb8(stage1, lin, Photometric.LinearRaw, null, new GpuRenderParams(m));

        var (max, mean, over1) = Diff.Bytes(cpu, dev);
        Log(nameof(RenderToRgb8_LinearRaw_passthrough_matches_CPU_pipeline), $"max {max} mean {mean:F4} >1: {over1:P3}");
        Assert.True(max <= 2 && over1 < 1e-3, $"max {max} mean {mean:F4} >1: {over1:P3}");
    }

    [Fact]
    public void RenderToRgb8_rejects_crop_outside_bounds()
    {
        var stage1 = Synthetic.BayerStage1();
        Assert.Throws<ArgumentException>(() => _gpu.RenderToRgb8(stage1, Synthetic.BayerLinearization(),
            Photometric.Cfa, Synthetic.Rggb(), new GpuRenderParams(Synthetic.CameraToXyzD50()),
            new DngRect(0, 0, Synthetic.Height + 1, Synthetic.Width)));
    }

    /// <summary>The CPU reference chain, mirroring <c>DngSharp.Dng.Validate -jpeg</c> (minus opcodes/mask/encode).</summary>
    internal static byte[] CpuPipeline(
        SimpleImage stage1, Imaging.Raw.LinearizationInfo lin, Photometric photometric, Imaging.Raw.MosaicInfo? mosaic,
        Math.DngMatrix cameraToXyz, double exposure, HueSatMap? hsm, (double, double)[]? tc, DngRect? crop,
        OutputColorSpace cs = OutputColorSpace.Srgb)
    {
        var stage2 = Stage2Builder.Build(stage1, lin);
        var stage3 = (SimpleImage)Stage3Builder.Build(stage2, photometric, mosaic);
        if (crop is { } c) stage3 = ImageCrop.Crop(stage3, c);
        var rgb = Stage3Renderer.Render(stage3, cameraToXyz, exposure,
            toneCurve: tc is null ? null : x => HdrToneMapper.EvaluateCurve(x, tc),
            colorSpace: cs, hueSatMap: hsm);
        HdrToneMapper.Apply(rgb, tc);
        var bytes = new byte[rgb.Bounds.W * rgb.Bounds.H * 3];
        Stage3Renderer.GammaAndQuantize(rgb, bytes);
        return bytes;
    }
}
