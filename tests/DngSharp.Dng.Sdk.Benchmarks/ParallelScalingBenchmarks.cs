using BenchmarkDotNet.Attributes;
using DngSharp.Dng.Sdk.Codecs;
using DngSharp.Dng.Sdk.Container;
using DngSharp.Dng.Sdk.IO;
using DngSharp.Dng.Sdk.TestSupport;
using DngSharp.Dng.Sdk.Imaging;
using DngSharp.Dng.Sdk.Imaging.Opcodes;
using DngSharp.Dng.Sdk.Imaging.Raw;
using DngSharp.Dng.Sdk.Math;
using DngSharp.Dng.Sdk.Pipeline;
using DngSharp.Dng.Sdk.Pixels;
using DngSharp.Dng.Sdk.Primitives;
using DngSharp.Dng.Sdk.Render;

namespace DngSharp.Dng.Sdk.Benchmarks;

/// <summary>
/// Phase 10 parallel-scaling benchmark: runs each parallelized pixel kernel
/// with <see cref="DngHost.MaxThreads"/> swept across thread counts on a
/// representative sensor-sized image (6000×4000, matching the iPhone ProRAW
/// samples under <c>images/</c>). <c>Threads = 1</c> is the fully serial
/// baseline; the last value is the machine's logical core count.
///
/// <para>Run: <c>dotnet run -c Release --project tests/DngSharp.Dng.Sdk.Benchmarks -- --filter *ParallelScalingBenchmarks*</c></para>
/// <para>Results recorded in <c>docs/perf/phase10-parallel.md</c>.</para>
/// </summary>
[MemoryDiagnoser]
public class ParallelScalingBenchmarks
{
    private const int Width = 6000;
    private const int Height = 4000;

    [Params(1, 2, 4, 0)] // 0 → Environment.ProcessorCount (resolved in Setup)
    public int Threads { get; set; }

    private DngHost _host = null!;

    private SimpleImage _stage1Uint16 = null!;
    private LinearizationInfo _lin = null!;
    private MosaicInfo _mosaic = null!;

    private SimpleImage _stage2Bayer = null!;
    private SimpleImage _stage3 = null!;
    private DngMatrix _matrix = null!;

    private SimpleImage _mapPolyScratch = null!;
    private MapPolynomialOpcode.Params _mapPoly = null!;
    private FixVignetteRadialOpcode.Params _vignette = null!;

    private byte[] _rgb8 = null!;

    private byte[] _strippedDng = null!;
    private byte[] _tiledDng = null!;
    private DngContainer _strippedContainer = null!;
    private DngContainer _tiledContainer = null!;
    private CodecRegistry _registry = null!;

    [GlobalSetup]
    public void Setup()
    {
        _host = new DngHost { MaxThreads = Threads == 0 ? Environment.ProcessorCount : Threads };

        var bounds = new DngRect(0, 0, Height, Width);

        _stage1Uint16 = new SimpleImage(bounds, 1, PixelType.UInt16);
        var s1 = _stage1Uint16.Buffer.AsTypedSpan<ushort>();
        for (int i = 0; i < s1.Length; i++) s1[i] = (ushort)(i % 4096);
        _lin = new LinearizationInfo { BlackLevel = [64.0], WhiteLevel = [4095.0] };

        _stage2Bayer = new SimpleImage(bounds, 1, PixelType.Float32);
        var s2 = _stage2Bayer.Buffer.AsTypedSpan<float>();
        for (int i = 0; i < s2.Length; i++) s2[i] = (i % 1000) / 1000f;
        _mosaic = new MosaicInfo { Pattern = (2, 2), CfaPlaneColor = [0, 1, 1, 2] };

        _stage3 = new SimpleImage(bounds, 3, PixelType.Float32);
        var s3 = _stage3.Buffer.AsTypedSpan<float>();
        for (int i = 0; i < s3.Length; i++) s3[i] = (i % 1000) / 1000f;
        _matrix = DngMatrix.Matrix3x3(
            0.9, 0.05, 0.05,
            0.1, 0.85, 0.05,
            0.0, 0.1, 0.9);

        _mapPolyScratch = new SimpleImage(bounds, 3, PixelType.Float32);
        _mapPoly = new MapPolynomialOpcode.Params
        {
            AreaSpec = new DngAreaSpec { Area = bounds, Plane = 0, Planes = 3, RowPitch = 1, ColPitch = 1 },
            Degree = 3,
            Coefficients = [0.01f, 0.9f, 0.1f, -0.02f],
        };
        _vignette = new FixVignetteRadialOpcode.Params
        {
            Coefficients = [0.2, 0.1, 0.05, 0.0, 0.0],
            Center = (0.5, 0.5),
        };

        _rgb8 = new byte[Width * Height * 3];

        // Multi-strip / tiled Deflate DNGs so StripReader's per-blob parallel
        // decode has enough independent work items to scale.
        _registry = CodecRegistry.Default;
        _strippedDng = MultiStripDngFactory.BuildStripped(Width, Height, rowsPerStrip: 64);
        _tiledDng = MultiStripDngFactory.BuildTiled(Width, Height, tileW: 256, tileH: 256);
        using (var s = DngMemoryStream.WrapNoCopy(_strippedDng)) _strippedContainer = DngContainer.Parse(s);
        using (var s = DngMemoryStream.WrapNoCopy(_tiledDng)) _tiledContainer = DngContainer.Parse(s);
    }

    [Benchmark]
    public SimpleImage StripDecode_Deflate()
    {
        using var s = DngMemoryStream.WrapNoCopy(_strippedDng);
        return StripReader.ReadStage1(s, _strippedContainer, _registry, _host).Stage1;
    }

    [Benchmark]
    public SimpleImage TileDecode_Deflate()
    {
        using var s = DngMemoryStream.WrapNoCopy(_tiledDng);
        return StripReader.ReadStage1(s, _tiledContainer, _registry, _host).Stage1;
    }

    [Benchmark]
    public SimpleImage Linearization() => Stage2Builder.Build(_stage1Uint16, _lin, _host);

    [Benchmark]
    public SimpleImage Demosaic() => DemosaicBilinear.Build(_stage2Bayer, _mosaic, _host);

    [Benchmark]
    public SimpleImage Render() => Stage3Renderer.Render(_stage3, _matrix, baselineExposure: 0.5, host: _host);

    [Benchmark]
    public void MapPolynomial()
    {
        // In-place opcode; refresh the scratch so every iteration sees the same input.
        _stage3.Buffer.AsByteSpan().CopyTo(_mapPolyScratch.Buffer.AsByteSpan());
        MapPolynomialOpcode.Apply(_mapPolyScratch, _mapPoly, _host);
    }

    [Benchmark]
    public void FixVignetteRadial()
    {
        _stage3.Buffer.AsByteSpan().CopyTo(_mapPolyScratch.Buffer.AsByteSpan());
        FixVignetteRadialOpcode.Apply(_mapPolyScratch, _vignette, _host);
    }

    [Benchmark]
    public int GammaAndQuantize() => Stage3Renderer.GammaAndQuantize(_stage3, _rgb8, _host).Length;
}
