using System.Buffers.Binary;
using DngSharp.Dng.Sdk.Codecs;
using DngSharp.Dng.Sdk.Container;
using DngSharp.Dng.Sdk.Errors;
using DngSharp.Dng.Sdk.Imaging;
using DngSharp.Dng.Sdk.Imaging.Opcodes;
using DngSharp.Dng.Sdk.Imaging.Raw;
using DngSharp.Dng.Sdk.IO;
using DngSharp.Dng.Sdk.Math;
using DngSharp.Dng.Sdk.Pipeline;
using DngSharp.Dng.Sdk.Pixels;
using DngSharp.Dng.Sdk.Primitives;
using DngSharp.Dng.Sdk.Render;
using DngSharp.Dng.Sdk.Tasks;
using DngSharp.Dng.Sdk.TestSupport;

namespace DngSharp.Dng.Sdk.Tests.Tasks;

/// <summary>
/// Every kernel that was parallelised in the Phase 10 parallel-for pass must
/// produce output that is byte-identical to the serial (<c>MaxThreads = 1</c>)
/// path. These tests pin that invariant so future SIMD/reassociation work
/// cannot silently make the multi-threaded result diverge.
/// </summary>
public sealed class ParallelEquivalenceTests
{
    // Odd sizes so band boundaries (64 rows) and chunk boundaries (1<<16
    // samples) fall mid-image and edge bands are non-empty.
    private const int W = 301;
    private const int H = 203;

    private static readonly DngHost Serial = new() { MaxThreads = 1 };
    private static readonly DngHost Parallel = new() { MaxThreads = System.Math.Max(2, Environment.ProcessorCount) };

    private static SimpleImage MakeFloat(int planes, int seed)
    {
        var img = new SimpleImage(new DngRect(0, 0, H, W), (uint)planes, PixelType.Float32);
        var s = img.Buffer.AsTypedSpan<float>();
        for (int i = 0; i < s.Length; i++) s[i] = ((i * 31 + seed) % 1000) / 1000f;
        return img;
    }

    private static SimpleImage MakeUInt16(int planes, int seed)
    {
        var img = new SimpleImage(new DngRect(0, 0, H, W), (uint)planes, PixelType.UInt16);
        var s = img.Buffer.AsTypedSpan<ushort>();
        for (int i = 0; i < s.Length; i++) s[i] = (ushort)((i * 31 + seed) % 4096);
        return img;
    }

    private static SimpleImage Clone(SimpleImage src)
    {
        var dst = new SimpleImage(src.Bounds, src.Planes, src.PixelType);
        src.Buffer.AsByteSpan().CopyTo(dst.Buffer.AsByteSpan());
        return dst;
    }

    private static void AssertSameBytes(SimpleImage a, SimpleImage b)
    {
        Assert.Equal(a.Bounds, b.Bounds);
        Assert.Equal(a.Planes, b.Planes);
        Assert.True(a.Buffer.AsByteSpan().SequenceEqual(b.Buffer.AsByteSpan()), "serial and parallel outputs differ");
    }

    private static DngAreaSpec FullArea(int planes, uint rowPitch = 1, uint colPitch = 1) => new()
    {
        Area = new DngRect(0, 0, H, W),
        Plane = 0,
        Planes = (uint)planes,
        RowPitch = rowPitch,
        ColPitch = colPitch,
    };

    /// <summary>Apply an in-place opcode to two copies of <paramref name="src"/> and compare.</summary>
    private static void AssertInPlaceEquivalent(SimpleImage src, Action<SimpleImage, DngHost> apply)
    {
        var a = Clone(src);
        var b = Clone(src);
        apply(a, Serial);
        apply(b, Parallel);
        Assert.False(a.Buffer.AsByteSpan().SequenceEqual(src.Buffer.AsByteSpan()), "opcode was a no-op; test is vacuous");
        AssertSameBytes(a, b);
    }

    // ---- pipeline stages -------------------------------------------------

    [Fact]
    public void Stage2Builder_serial_equals_parallel()
    {
        var s1 = MakeUInt16(1, 3);
        var lin = new LinearizationInfo { BlackLevel = [64.0], WhiteLevel = [4095.0] };
        AssertSameBytes(Stage2Builder.Build(s1, lin, Serial), Stage2Builder.Build(s1, lin, Parallel));
    }

    [Fact]
    public void DemosaicBilinear_serial_equals_parallel()
    {
        var s2 = MakeFloat(1, 5);
        var mosaic = new MosaicInfo { Pattern = (2, 2), CfaPlaneColor = [0, 1, 1, 2] };
        AssertSameBytes(DemosaicBilinear.Build(s2, mosaic, Serial), DemosaicBilinear.Build(s2, mosaic, Parallel));
    }

    [Fact]
    public void Stage3Renderer_Render_serial_equals_parallel()
    {
        var s3 = MakeFloat(3, 7);
        var m = DngMatrix.Matrix3x3(0.9, 0.05, 0.05, 0.1, 0.85, 0.05, 0.0, 0.1, 0.9);
        AssertSameBytes(
            Stage3Renderer.Render(s3, m, baselineExposure: 0.5, host: Serial),
            Stage3Renderer.Render(s3, m, baselineExposure: 0.5, host: Parallel));
    }

    [Fact]
    public void GammaAndQuantize_serial_equals_parallel()
    {
        var s3 = MakeFloat(3, 9);
        var a = new byte[W * H * 3];
        var b = new byte[W * H * 3];
        Stage3Renderer.GammaAndQuantize(s3, a, Serial);
        Stage3Renderer.GammaAndQuantize(s3, b, Parallel);
        Assert.Equal(a, b);
    }

    [Fact]
    public void HdrToneMapper_serial_equals_parallel()
    {
        var src = MakeFloat(3, 11);
        var s = src.Buffer.AsTypedSpan<float>();
        for (int i = 0; i < s.Length; i++) s[i] *= 4f; // push above 1.0 so the mapper does work
        AssertInPlaceEquivalent(src, (img, host) => HdrToneMapper.Apply(img, null, host));
    }

    // ---- opcodes ----------------------------------------------------------

    [Fact]
    public void MapPolynomial_serial_equals_parallel()
    {
        var p = new MapPolynomialOpcode.Params { AreaSpec = FullArea(3), Degree = 3, Coefficients = [0.01f, 0.9f, 0.1f, -0.02f] };
        AssertInPlaceEquivalent(MakeFloat(3, 1), (img, host) => MapPolynomialOpcode.Apply(img, p, host));
    }

    [Fact]
    public void MapTable_serial_equals_parallel()
    {
        var table = new ushort[65536];
        for (int i = 0; i < table.Length; i++) table[i] = (ushort)(65535 - i);
        var p = new MapTableOpcode.Params { AreaSpec = FullArea(1), Table = table };
        AssertInPlaceEquivalent(MakeUInt16(1, 2), (img, host) => MapTableOpcode.Apply(img, p, host));
    }

    [Theory]
    [InlineData(1u, 1u)]
    [InlineData(2u, 3u)] // pitch > 1 exercises the strided row/col path
    public void DeltaPerRow_serial_equals_parallel(uint rowPitch, uint colPitch)
    {
        var deltas = new float[H];
        for (int i = 0; i < deltas.Length; i++) deltas[i] = i * 0.001f;
        var p = new DeltaPerRowOpcode.Params { AreaSpec = FullArea(1, rowPitch, colPitch), Deltas = deltas };
        AssertInPlaceEquivalent(MakeFloat(1, 4), (img, host) => DeltaPerRowOpcode.Apply(img, p, host));
    }

    [Theory]
    [InlineData(1u, 1u, W)]
    [InlineData(2u, 3u, W)]
    [InlineData(1u, 1u, 100)] // table shorter than width → effectiveCols clamp
    public void DeltaPerColumn_serial_equals_parallel(uint rowPitch, uint colPitch, int tableLength)
    {
        var deltas = new float[tableLength];
        for (int i = 0; i < deltas.Length; i++) deltas[i] = i * 0.001f;
        var p = new DeltaPerColumnOpcode.Params { AreaSpec = FullArea(1, rowPitch, colPitch), Deltas = deltas };
        AssertInPlaceEquivalent(MakeFloat(1, 6), (img, host) => DeltaPerColumnOpcode.Apply(img, p, host));
    }

    [Theory]
    [InlineData(H)]
    [InlineData(50)] // table shorter than height → effectiveRows clamp
    public void ScalePerRow_serial_equals_parallel(int tableLength)
    {
        var scales = new float[tableLength];
        for (int i = 0; i < scales.Length; i++) scales[i] = 1f + i * 0.001f;
        var p = new ScalePerRowOpcode.Params { AreaSpec = FullArea(1), Scales = scales };
        AssertInPlaceEquivalent(MakeFloat(1, 8), (img, host) => ScalePerRowOpcode.Apply(img, p, host));
    }

    [Theory]
    [InlineData(W)]
    [InlineData(100)]
    public void ScalePerColumn_serial_equals_parallel(int tableLength)
    {
        var scales = new float[tableLength];
        for (int i = 0; i < scales.Length; i++) scales[i] = 1f + i * 0.001f;
        var p = new ScalePerColumnOpcode.Params { AreaSpec = FullArea(1), Scales = scales };
        AssertInPlaceEquivalent(MakeFloat(1, 10), (img, host) => ScalePerColumnOpcode.Apply(img, p, host));
    }

    [Fact]
    public void GainMap_serial_equals_parallel()
    {
        const int pts = 5;
        var samples = new float[pts * pts];
        for (int i = 0; i < samples.Length; i++) samples[i] = 1f + (i % 7) * 0.05f;
        var p = new GainMapOpcode.Params
        {
            AreaSpec = FullArea(1),
            Points = (pts, pts),
            Spacing = (1.0 / (pts - 1), 1.0 / (pts - 1)),
            Origin = (0.0, 0.0),
            Planes = 1,
            Samples = samples,
        };
        AssertInPlaceEquivalent(MakeFloat(1, 12), (img, host) => GainMapOpcode.Apply(img, p, host));
    }

    [Fact]
    public void FixVignetteRadial_serial_equals_parallel()
    {
        var p = new FixVignetteRadialOpcode.Params { Coefficients = [0.2, 0.1, 0.05, 0.0, 0.0], Center = (0.5, 0.5) };
        AssertInPlaceEquivalent(MakeFloat(3, 14), (img, host) => FixVignetteRadialOpcode.Apply(img, p, host));
    }

    [Fact]
    public void FixBadPixelsConstant_serial_equals_parallel()
    {
        // Plant the sentinel constant at scattered positions across band
        // boundaries so several bands contribute writes.
        var src = MakeUInt16(1, 16);
        var s = src.Buffer.AsTypedSpan<ushort>();
        for (int i = 0; i < s.Length; i += 997) s[i] = 0;
        var p = new FixBadPixelsConstantOpcode.Params { Constant = 0, BayerPhase = 0 };
        AssertInPlaceEquivalent(src, (img, host) => FixBadPixelsConstantOpcode.Apply(img, p, host));
    }

    [Fact]
    public void LensWarpFilter_serial_equals_parallel()
    {
        // WarpRectilinear body: planes(4) + per-plane 6 doubles + center 2 doubles, big-endian.
        const int planes = 3;
        var body = new byte[4 + planes * 6 * 8 + 2 * 8];
        BinaryPrimitives.WriteUInt32BigEndian(body, planes);
        int off = 4;
        for (int pl = 0; pl < planes; pl++)
        {
            double[] coeffs = [1.0, -0.05, 0.01, 0.0, 0.001, -0.001]; // r0..r3, t0, t1
            foreach (var c in coeffs) { BinaryPrimitives.WriteDoubleBigEndian(body.AsSpan(off), c); off += 8; }
        }
        BinaryPrimitives.WriteDoubleBigEndian(body.AsSpan(off), 0.5); off += 8;
        BinaryPrimitives.WriteDoubleBigEndian(body.AsSpan(off), 0.5);
        var warp = WarpRectilinearParams.Decode(body);

        var src = MakeFloat(planes, 18);
        var a = LensWarpFilter.Apply(src, warp, host: Serial);
        var b = LensWarpFilter.Apply(src, warp, host: Parallel);
        Assert.False(a.Buffer.AsByteSpan().SequenceEqual(src.Buffer.AsByteSpan()), "warp was a no-op; test is vacuous");
        AssertSameBytes(a, b);
    }

    // ---- strip / tile decode ----------------------------------------------

    private static SimpleImage Decode(byte[] dng, DngHost host)
    {
        using var s = DngMemoryStream.WrapNoCopy(dng);
        var container = DngContainer.Parse(s);
        return StripReader.ReadStage1(s, container, CodecRegistry.Default, host).Stage1;
    }

    [Fact]
    public void StripReader_multistrip_deflate_serial_equals_parallel_and_matches_expected()
    {
        var dng = MultiStripDngFactory.BuildStripped(W, H, rowsPerStrip: 16);
        var a = Decode(dng, Serial);
        var b = Decode(dng, Parallel);
        AssertSameBytes(a, b);
        AssertMatchesFactory(a);
    }

    [Fact]
    public void StripReader_tiled_deflate_serial_equals_parallel_and_matches_expected()
    {
        var dng = MultiStripDngFactory.BuildTiled(W, H, tileW: 64, tileH: 48);
        var a = Decode(dng, Serial);
        var b = Decode(dng, Parallel);
        AssertSameBytes(a, b);
        AssertMatchesFactory(a);
    }

    private static void AssertMatchesFactory(SimpleImage img)
    {
        Assert.Equal(new DngRect(0, 0, H, W), img.Bounds);
        var px = img.Buffer.AsTypedSpan<ushort>();
        for (int r = 0; r < H; r++)
            for (int c = 0; c < W; c++)
                if (px[r * W + c] != MultiStripDngFactory.ExpectedPixel(r, c))
                    Assert.Fail($"pixel ({r},{c}) = {px[r * W + c]}, expected {MultiStripDngFactory.ExpectedPixel(r, c)}");
    }

    [Fact]
    public void StripReader_parallel_decode_honours_cancellation()
    {
        var dng = MultiStripDngFactory.BuildStripped(W, H, rowsPerStrip: 16);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var host = new DngHost { MaxThreads = 4, Sniffer = new AbortSniffer(cts.Token) };

        var ex = Assert.Throws<DngException>(() => Decode(dng, host));
        Assert.Equal(DngError.UserCanceled, ex.ErrorCode);
    }
}
