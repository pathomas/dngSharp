using BenchmarkDotNet.Attributes;
using DngSharp.Dng.Sdk.Codecs;
using DngSharp.Dng.Sdk.Codecs.LosslessJpeg;
using DngSharp.Dng.Sdk.Container;
using DngSharp.Dng.Sdk.Imaging;
using DngSharp.Dng.Sdk.IO;
using DngSharp.Dng.Sdk.Jxl;
using DngSharp.Dng.Sdk.Pipeline;
using DngSharp.Dng.Sdk.Render;

namespace DngSharp.Dng.Sdk.Benchmarks;

/// <summary>
/// Phase 10 end-to-end benchmark: the whole render pipeline as driven by
/// <c>DngSharp.Dng.Validate -jpeg</c>, minus the JPEG encode and file write —
/// parse → Stage 1 decode → OpcodeList1 → Stage 2 linearize → OpcodeList2 →
/// Stage 3 demosaic → OpcodeList3 → crop → colour transform → tone-map.
/// This is the number a user of the CLI actually experiences.
///
/// <para>Runs on the real camera DNGs under <c>images/</c> (iPhone ProRAW,
/// 6288×4056 16-bit lossless-JPEG-tiled Bayer). The directory is gitignored;
/// drop any <c>*.dng</c> there. Files whose enhanced IFD needs <c>libjxl</c>
/// still work because the main (CFA) IFD is what gets rendered.</para>
///
/// <para><c>Threads = 1</c> is the fully serial baseline; <c>Threads = 0</c>
/// resolves to every logical core.</para>
///
/// <para>Run: <c>dotnet run -c Release --project tests/DngSharp.Dng.Sdk.Benchmarks -- --filter *EndToEnd*</c></para>
/// <para>Results recorded in <c>docs/perf/phase10-end-to-end.md</c>.</para>
/// </summary>
[MemoryDiagnoser]
public class EndToEndBenchmarks
{
    private static readonly string ImagesDir = FindImagesDir();

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
        throw new DirectoryNotFoundException(
            "Could not locate an images/ directory containing *.dng walking up from "
            + AppContext.BaseDirectory);
    }

    public static IEnumerable<string> Images =>
        Directory.EnumerateFiles(ImagesDir, "*.dng").Select(Path.GetFileName).Order()!;

    [ParamsSource(nameof(Images))]
    public string Image { get; set; } = "";

    [Params(1, 0)] // 0 → Environment.ProcessorCount (resolved in Setup)
    public int Threads { get; set; }

    private byte[] _file = null!;
    private CodecRegistry _registry = null!;
    private DngHost _host = null!;

    [GlobalSetup]
    public void Setup()
    {
        // Read the file into memory once so the benchmark measures the
        // pipeline, not the page cache / disk.
        _file = File.ReadAllBytes(Path.Combine(ImagesDir, Image));

        _registry = new CodecRegistry();
        _registry.Register(new UncompressedDecoder());
        _registry.Register(new DeflateDecoder());
        _registry.Register(new LosslessJpegDecoder());
        if (JxlDecoder.IsAvailable) _registry.Register(new JxlDecoder());

        _host = new DngHost { MaxThreads = Threads == 0 ? Environment.ProcessorCount : Threads };
    }

    /// <summary>Parse + Stage 1 decode only (lossless-JPEG tile decode dominates).</summary>
    [Benchmark]
    public SimpleImage DecodeStage1()
    {
        using var stream = DngMemoryStream.WrapNoCopy(_file);
        var container = DngContainer.Parse(stream);
        var r = StripReader.ReadStage1(stream, container, _registry, _host);
        return (SimpleImage)r.Stage1;
    }

    /// <summary>Everything <c>-jpeg</c> does up to (not including) JPEG encoding.</summary>
    [Benchmark(Baseline = true)]
    public SimpleImage RenderToSdrRgb8()
    {
        using var stream = DngMemoryStream.WrapNoCopy(_file);
        var container = DngContainer.Parse(stream);
        var r = StripReader.ReadStage1(stream, container, _registry, _host);

        var stage1 = OpcodeList1Applier.Apply(r.Stage1, r.OpcodeList1, _host);
        var stage2 = Stage2Builder.Build(stage1, r.Linearization, _host);
        stage2 = OpcodeList2Applier.Apply(stage2, r.OpcodeList2, _host);

        var stage3 = (SimpleImage)Stage3Builder.Build(stage2, r.Photometric, r.Mosaic, _host);
        stage3 = OpcodeList3Applier.Apply(stage3, r.OpcodeList3, _host);
        if ((r.DefaultCropArea ?? r.ActiveArea) is { } crop)
            stage3 = ImageCrop.Crop(stage3, crop);

        var camToXyz = Stage3Renderer.ResolveCameraToXyzD50(r.CameraProfile, r.Shared);
        Func<double, double>? toneCurve = r.CameraProfile?.ToneCurve is { } tc
            ? x => HdrToneMapper.EvaluateCurve(x, tc)
            : null;
        HueSatMap? hueSat = null;
        if (r.CameraProfile is { Illuminants.Count: > 0 } profile)
            hueSat = CameraColorMatrix.ResolveHueSatMap(profile, Stage3Renderer.EstimateAsShotKelvin(r.Shared));

        var rgb = Stage3Renderer.Render(stage3, camToXyz, r.Shared.BaselineExposure,
            toneCurve: toneCurve, host: _host, hueSatMap: hueSat);
        HdrToneMapper.Apply(rgb, r.CameraProfile?.ToneCurve, _host);
        return rgb;
    }
}
