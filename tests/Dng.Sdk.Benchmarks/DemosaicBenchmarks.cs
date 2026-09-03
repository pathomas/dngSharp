using BenchmarkDotNet.Attributes;
using Dng.Sdk.Imaging;
using Dng.Sdk.Imaging.Raw;
using Dng.Sdk.Pipeline;
using Dng.Sdk.Pixels;
using Dng.Sdk.Primitives;

namespace Dng.Sdk.Benchmarks;

/// <summary>
/// Phase 10 pipeline-stage benchmark: measures <see cref="DemosaicBilinear.Build"/>
/// (bilinear Bayer demosaic, Stage 2 → Stage 3) on a representative sensor-sized
/// single-plane CFA image (6000×4000, matching the iPhone ProRAW samples under
/// <c>images/</c>).
///
/// <para>Run: <c>dotnet run -c Release --project tests/Dng.Sdk.Benchmarks -- --filter *DemosaicBenchmarks*</c></para>
/// </summary>
[MemoryDiagnoser]
public class DemosaicBenchmarks
{
    private const int Width = 6000;
    private const int Height = 4000;

    private DngImage _stage2 = null!;
    private MosaicInfo _mosaic = null!;

    [GlobalSetup]
    public void Setup()
    {
        var image = new SimpleImage(new DngRect(0, 0, Height, Width), 1, PixelType.Float32);
        var span = image.Buffer.AsTypedSpan<float>();
        for (int i = 0; i < span.Length; i++) span[i] = (i % 1000) / 1000f;
        _stage2 = image;

        // Standard RGGB Bayer pattern.
        _mosaic = new MosaicInfo
        {
            Pattern = (2, 2),
            CfaPlaneColor = [0, 1, 1, 2], // R G / G B
        };
    }

    [Benchmark]
    public SimpleImage Demosaic_Bilinear() => DemosaicBilinear.Build(_stage2, _mosaic);
}
