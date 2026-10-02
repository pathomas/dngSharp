using BenchmarkDotNet.Attributes;
using DngSharp.Dng.Sdk.Codecs;
using DngSharp.Dng.Sdk.Codecs.LosslessJpeg;
using DngSharp.Dng.Sdk.Container;
using DngSharp.Dng.Sdk.Gpu;
using DngSharp.Dng.Sdk.Imaging;
using DngSharp.Dng.Sdk.Imaging.Raw;
using DngSharp.Dng.Sdk.IO;
using DngSharp.Dng.Sdk.Jxl;
using DngSharp.Dng.Sdk.Math;
using DngSharp.Dng.Sdk.Pipeline;
using DngSharp.Dng.Sdk.Primitives;
using DngSharp.Dng.Sdk.Render;
using DngSharp.Dng.Sdk.Tiff;

namespace DngSharp.Dng.Sdk.Benchmarks;

/// <summary>
/// CPU vs ILGPU render: decoded Stage 1 → SDR RGB8, on the real camera DNGs
/// under <c>images/</c>. Stage 1 decode (lossless-JPEG) is done once in
/// setup for both paths because it is not GPU-portable and is identical for
/// the two variants; what is measured is linearize → demosaic → crop →
/// colour → tone map → gamma/quantize.
///
/// <para>The GPU number includes the Stage 1 upload and the RGB8 download —
/// it is the wall time a caller sees. Set <c>DNGSHARP_GPU_BACKEND</c> to
/// <c>cuda</c>, <c>opencl</c>, or <c>cpu</c> (default <c>auto</c>).</para>
///
/// <para>Run: <c>dotnet run -c Release --project tests/DngSharp.Dng.Sdk.Benchmarks -- --filter *GpuPipeline*</c></para>
/// <para>Results recorded in <c>docs/perf/phase10-gpu.md</c>.</para>
/// </summary>
[MemoryDiagnoser]
#pragma warning disable CA1001 // BDN owns the lifecycle via [GlobalCleanup]
public class GpuPipelineBenchmarks
#pragma warning restore CA1001
{
    public static IEnumerable<string> Images => EndToEndBenchmarks.Images;

    [ParamsSource(nameof(Images))]
    public string Image { get; set; } = "";

    private DngHost _host = null!;
    private GpuDevice _device = null!;
    private GpuRenderPipeline _gpu = null!;

    private SimpleImage _stage1 = null!;
    private LinearizationInfo _lin = null!;
    private Photometric _photometric;
    private MosaicInfo? _mosaic;
    private DngRect? _crop;
    private DngMatrix _camToXyz = null!;
    private double _exposure;
    private (double, double)[]? _toneCurve;
    private HueSatMap? _hueSat;
    private GpuRenderParams _gpuParams = null!;

    // Intermediates for the per-stage GPU benchmarks.
    private SimpleImage _stage2 = null!;
    private SimpleImage _stage3 = null!;
    private SimpleImage _rgbLinear = null!;

    [GlobalSetup]
    public void Setup()
    {
        var registry = new CodecRegistry();
        registry.Register(new UncompressedDecoder());
        registry.Register(new DeflateDecoder());
        registry.Register(new LosslessJpegDecoder());
        if (JxlDecoder.IsAvailable) registry.Register(new JxlDecoder());
        _host = new DngHost { MaxThreads = Environment.ProcessorCount };

        var file = File.ReadAllBytes(Path.Combine(FindImagesDir(), Image));
        using var stream = DngMemoryStream.WrapNoCopy(file);
        var container = DngContainer.Parse(stream);
        var r = StripReader.ReadStage1(stream, container, registry, _host);

        if ((r.OpcodeList1?.Count ?? 0) + (r.OpcodeList2?.Count ?? 0) + (r.OpcodeList3?.Count ?? 0) > 0)
            throw new NotSupportedException($"{Image} has opcodes; the resident GPU path does not apply them.");

        _stage1 = (SimpleImage)r.Stage1;
        _lin = r.Linearization;
        _photometric = r.Photometric;
        _mosaic = r.Mosaic;
        _crop = r.DefaultCropArea ?? r.ActiveArea;
        _camToXyz = Stage3Renderer.ResolveCameraToXyzD50(r.CameraProfile, r.Shared);
        _exposure = r.Shared.BaselineExposure;
        _toneCurve = r.CameraProfile?.ToneCurve;
        if (r.CameraProfile is { Illuminants.Count: > 0 } profile)
            _hueSat = CameraColorMatrix.ResolveHueSatMap(profile, Stage3Renderer.EstimateAsShotKelvin(r.Shared));
        _gpuParams = new GpuRenderParams(_camToXyz, _exposure, HueSatMap: _hueSat, ToneCurve: _toneCurve);

        var backend = Environment.GetEnvironmentVariable("DNGSHARP_GPU_BACKEND")?.ToLowerInvariant() switch
        {
            "cuda" => GpuBackend.Cuda,
            "opencl" => GpuBackend.OpenCL,
            "cpu" => GpuBackend.Cpu,
            _ => GpuBackend.Auto,
        };
        _device = GpuDevice.Create(backend);
        _gpu = new GpuRenderPipeline(_device);
        Console.WriteLine($"// GPU device: {_device.Name} ({_device.MemorySize >> 20} MiB)");

        _stage2 = Stage2Builder.Build(_stage1, _lin, _host);
        _stage3 = (SimpleImage)Stage3Builder.Build(_stage2, _photometric, _mosaic, _host);
        if (_crop is { } c) _stage3 = ImageCrop.Crop(_stage3, c);
        _rgbLinear = Stage3Renderer.Render(_stage3, _camToXyz, _exposure,
            toneCurve: ToneCurveFunc(), host: _host, hueSatMap: _hueSat);

        // Warm the JIT-to-PTX compile so it is not attributed to the first iteration.
        _gpu.RenderToRgb8(_stage1, _lin, _photometric, _mosaic, _gpuParams, _crop);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _gpu.Dispose();
        _device.Dispose();
    }

    private Func<double, double>? ToneCurveFunc() =>
        _toneCurve is { } tc ? x => HdrToneMapper.EvaluateCurve(x, tc) : null;

    // ── Whole pipeline ────────────────────────────────────────────────────────

    /// <summary>CPU reference path on all cores (what <c>-jpeg</c> runs today).</summary>
    [Benchmark(Baseline = true)]
    public byte[] Cpu_Stage1ToRgb8()
    {
        var stage2 = Stage2Builder.Build(_stage1, _lin, _host);
        var stage3 = (SimpleImage)Stage3Builder.Build(stage2, _photometric, _mosaic, _host);
        if (_crop is { } c) stage3 = ImageCrop.Crop(stage3, c);
        var rgb = Stage3Renderer.Render(stage3, _camToXyz, _exposure,
            toneCurve: ToneCurveFunc(), host: _host, hueSatMap: _hueSat);
        HdrToneMapper.Apply(rgb, _toneCurve, _host);
        var bytes = new byte[rgb.Bounds.W * rgb.Bounds.H * 3];
        Stage3Renderer.GammaAndQuantize(rgb, bytes, _host);
        return bytes;
    }

    /// <summary>Device-resident path: one upload, five kernels, one download.</summary>
    [Benchmark]
    public byte[] Gpu_Stage1ToRgb8() =>
        _gpu.RenderToRgb8(_stage1, _lin, _photometric, _mosaic, _gpuParams, _crop);

    // ── Per stage (each GPU stage includes its own upload + download) ─────────

    [Benchmark] public SimpleImage Cpu_Linearize() => Stage2Builder.Build(_stage1, _lin, _host);
    [Benchmark] public SimpleImage Gpu_Linearize() => _gpu.Linearize(_stage1, _lin);

    [Benchmark] public SimpleImage Cpu_Demosaic() => (SimpleImage)Stage3Builder.Build(_stage2, _photometric, _mosaic, _host);
    [Benchmark] public SimpleImage Gpu_Demosaic() => _mosaic is null ? _stage2 : _gpu.Demosaic(_stage2, _mosaic);

    [Benchmark]
    public SimpleImage Cpu_ColorRender() =>
        Stage3Renderer.Render(_stage3, _camToXyz, _exposure, toneCurve: ToneCurveFunc(), host: _host, hueSatMap: _hueSat);
    [Benchmark] public SimpleImage Gpu_ColorRender() => _gpu.Render(_stage3, _gpuParams);

    [Benchmark]
    public byte[] Cpu_GammaQuantize()
    {
        var bytes = new byte[_rgbLinear.Bounds.W * _rgbLinear.Bounds.H * 3];
        Stage3Renderer.GammaAndQuantize(_rgbLinear, bytes, _host);
        return bytes;
    }
    [Benchmark] public byte[] Gpu_GammaQuantize() => _gpu.GammaAndQuantize(_rgbLinear);

    private static string FindImagesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "images");
            if (Directory.Exists(candidate) && Directory.EnumerateFiles(candidate, "*.dng").Any())
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("No images/*.dng found above " + AppContext.BaseDirectory);
    }
}
