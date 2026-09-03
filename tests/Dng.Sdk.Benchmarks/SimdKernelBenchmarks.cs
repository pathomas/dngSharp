using BenchmarkDotNet.Attributes;
using Dng.Sdk.Imaging;
using Dng.Sdk.Imaging.Raw;
using Dng.Sdk.Math;
using Dng.Sdk.Pipeline;
using Dng.Sdk.Pixels;
using Dng.Sdk.Primitives;
using Dng.Sdk.Render;

namespace Dng.Sdk.Benchmarks;

/// <summary>
/// Phase 10 SIMD-kernel benchmark: compares the <see cref="Stage2Builder"/>
/// linearization multiply-add and the <see cref="Stage3Renderer"/> 3×3
/// matrix + exposure transform with their vectorized fast paths engaged vs.
/// disengaged (forced onto the scalar fallback) on a representative
/// sensor-sized image (6000×4000, matching the iPhone ProRAW samples under
/// <c>images/</c>).
///
/// <para>Run: <c>dotnet run -c Release --project tests/Dng.Sdk.Benchmarks -- --filter *SimdKernelBenchmarks*</c></para>
/// <para>Results recorded in <c>docs/perf/phase10-simd.md</c>.</para>
/// </summary>
[MemoryDiagnoser]
public class SimdKernelBenchmarks
{
    private const int Width = 6000;
    private const int Height = 4000;

    private SimpleImage _stage1Uint16 = null!;
    private LinearizationInfo _linSimd = null!;   // qualifies for the SIMD fast path
    private LinearizationInfo _linScalar = null!; // BlackLevelRepeatDim disables it

    private SimpleImage _stage3 = null!;
    private DngMatrix _matrix = null!;

    [GlobalSetup]
    public void Setup()
    {
        _stage1Uint16 = new SimpleImage(new DngRect(0, 0, Height, Width), 1, PixelType.UInt16);
        var s1 = _stage1Uint16.Buffer.AsTypedSpan<ushort>();
        for (int i = 0; i < s1.Length; i++) s1[i] = (ushort)(i % 4096);

        _linSimd = new LinearizationInfo { BlackLevel = [64.0], WhiteLevel = [4095.0] };
        _linScalar = new LinearizationInfo
        {
            BlackLevel = [64.0, 64.0],
            WhiteLevel = [4095.0],
            BlackLevelRepeatDim = (1, 2), // same effective black on both cells — disables SIMD only
        };

        _stage3 = new SimpleImage(new DngRect(0, 0, Height, Width), 3, PixelType.Float32);
        var s3 = _stage3.Buffer.AsTypedSpan<float>();
        for (int i = 0; i < s3.Length; i++) s3[i] = (i % 1000) / 1000f;

        _matrix = DngMatrix.Matrix3x3(
            0.9, 0.05, 0.05,
            0.1, 0.85, 0.05,
            0.0, 0.1, 0.9);
    }

    [Benchmark(Baseline = true)]
    public SimpleImage Linearization_Simd() => Stage2Builder.Build(_stage1Uint16, _linSimd);

    [Benchmark]
    public SimpleImage Linearization_ScalarFallback() => Stage2Builder.Build(_stage1Uint16, _linScalar);

    [Benchmark]
    public SimpleImage MatrixTransform_Simd() => Stage3Renderer.Render(_stage3, _matrix, baselineExposure: 0.5);

    [Benchmark]
    public SimpleImage MatrixTransform_ScalarFallback() => Stage3Renderer.Render(
        _stage3, _matrix, baselineExposure: 0.5, toneCurve: static x => x); // identity curve disables SIMD path
}
