using DngSharp.Dng.Sdk.Codecs;
using DngSharp.Dng.Sdk.Container;
using DngSharp.Dng.Sdk.IO;
using DngSharp.Dng.Sdk.Pipeline;
using System.Buffers.Binary;

namespace DngSharp.Dng.Sdk.Tests.Golden;

/// <summary>
/// Golden diff for opcodes with no coverage in the bundled Adobe
/// <c>sample_files</c> (lens-shading/gain-map opcodes:
/// <c>FixVignetteRadial</c>, <c>WarpFisheye</c>, <c>WarpRectilinear2</c>,
/// <c>GainMap</c> — none of the 14 shipped samples exercise these). Since no
/// real-world reference file is available, this test uses a purpose-built
/// synthetic DNG (see <c>tools/SyntheticImageGenerator</c>'s
/// <c>--golden-opcodes</c> mode) with a <c>FixVignetteRadial</c> opcode
/// embedded in <c>OpcodeList2</c>, diffed against the real native
/// <c>dng_validate -2</c> reference output.
///
/// <para>Regenerating the fixture (from repo root, after building
/// <c>dng_validate</c> Release — see <c>tests/golden/README.md</c>):
/// <code>
///   dotnet run --project tools/SyntheticImageGenerator -c Release -- --golden-opcodes
///   dng_sdk_1_7_1\dng_sdk\targets\win\release64_x64\dng_validate.exe `
///     -2 tests\golden\15_synthetic_fixvignetteradial\stage2.tif `
///     tests\golden\15_synthetic_fixvignetteradial\sample.dng
/// </code>
/// </para>
///
/// <para>The test is silently skipped when the golden fixtures are missing
/// (e.g. a fresh checkout before regenerating them), matching
/// <see cref="GoldenBayerDiffTests"/>'s convention.</para>
/// </summary>
public class GoldenSyntheticOpcodeDiffTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    private static string GoldenDir => Path.Combine(RepoRoot, "tests", "golden", "15_synthetic_fixvignetteradial");

    private static CodecRegistry BuildRegistry()
    {
        var r = new CodecRegistry();
        r.Register(new UncompressedDecoder());
        return r;
    }

    [Fact]
    public void Stage2_fixvignetteradial_matches_native()
    {
        string dngPath = Path.Combine(GoldenDir, "sample.dng");
        string goldenPath = Path.Combine(GoldenDir, "stage2.tif");
        if (!File.Exists(dngPath) || !File.Exists(goldenPath)) return;

        using var stream = DngFileStream.OpenRead(dngPath);
        var container = DngContainer.Parse(stream);
        var result = StripReader.ReadStage1(stream, container, BuildRegistry());
        var stage1 = OpcodeList1Applier.Apply(result.Stage1, result.OpcodeList1);
        var stage2 = Stage2Builder.Build(stage1, result.Linearization);
        stage2 = OpcodeList2Applier.Apply(stage2, result.OpcodeList2);

        Assert.NotNull(result.OpcodeList2);
        Assert.False(result.OpcodeList2!.IsEmpty);

        float[] nativePixels = GoldenTiffReader.ReadFloat32(goldenPath, out uint nativeW, out uint nativeH, out uint nativePlanes);
        Assert.NotEmpty(nativePixels);
        Assert.Equal((uint)stage2.Bounds.W, nativeW);
        Assert.Equal((uint)stage2.Bounds.H, nativeH);
        Assert.Equal(stage2.Planes, nativePlanes);

        var tile = stage2.GetTile(stage2.Bounds);
        var managedBytes = tile.Memory.Span;

        int n = (int)(stage2.Bounds.W * stage2.Bounds.H * stage2.Planes);
        Assert.Equal(n, nativePixels.Length);

        // Native's stage2.tif is a normalized UInt16 dump (round(x * 65535)),
        // so use an absolute tolerance a bit above 1 LSB at 16-bit precision
        // rather than requiring bit-exactness.
        const float Tolerance = 4.0f / 65535.0f;
        float maxAbs = 0;
        for (int i = 0; i < n; i++)
        {
            float managed = BinaryPrimitives.ReadSingleLittleEndian(managedBytes.Slice(i * 4, 4));
            float diff = System.Math.Abs(managed - nativePixels[i]);
            if (diff > maxAbs) maxAbs = diff;
        }

        Assert.True(maxAbs <= Tolerance,
            $"Stage-2 FixVignetteRadial pixel diff exceeded tolerance: maxAbs={maxAbs} > {Tolerance}");
    }
}
