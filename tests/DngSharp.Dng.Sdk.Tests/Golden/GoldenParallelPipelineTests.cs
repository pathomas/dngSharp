using DngSharp.Dng.Sdk.Hashing;
using DngSharp.Dng.Sdk.Tests.TestImages;

namespace DngSharp.Dng.Sdk.Tests.Golden;

/// <summary>
/// Self-contained golden test for the parallelised pipeline. For each
/// deterministic synthetic fixture it runs the full CLI
/// (<c>-1 -2 -3 -jpeg</c>) once with <c>-threads 1</c> (fully serial) and once
/// with the default thread count (all cores), then asserts that every output
/// artifact's MD5 matches the pinned golden fingerprint below.
///
/// <para>Unlike the other <c>Golden*</c> tests this does not depend on the
/// native Adobe <c>dng_validate</c> or the vendored <c>sample_files</c>; the
/// goldens were captured from the serial path of this port at the time the
/// parallel-for work landed (see <c>docs/perf/phase10-parallel.md</c>). Its
/// job is to catch two classes of regression:</para>
/// <list type="bullet">
///   <item>Any thread-count-dependent divergence (FP reassociation, banding
///   off-by-one, race in a parallel decode/apply).</item>
///   <item>Any unintended change to pipeline output at all — a legitimate
///   algorithm change must update the fingerprints here deliberately.</item>
/// </list>
///
/// <para>To regenerate after an intentional change, run the test; the
/// failure message prints the actual fingerprints in table form.</para>
/// </summary>
public class GoldenParallelPipelineTests
{
    private const int Serial = 1;

    // Fixture name → (stage1, stage2, stage3, jpeg) MD5 hex, captured with -threads 1.
    private static readonly Dictionary<string, string[]> Golden = new()
    {
        ["border_crop_2000"] = [
            "b5d7e27575484470e02dada85e3d7fef",
            "87777dbcb9974dd2c429accacf536310",
            "e3abded84adec32555357b2fb71d1ec6",
            "0f5058e8764fad049c9dc92b2f4c292b",
        ],
        ["checkerboard_1024"] = [
            "dfec4109a0561d62431f1255d9a07ae4",
            "9d5843b5310b96692fb00db6fe6c17fc",
            "9d5843b5310b96692fb00db6fe6c17fc",
            "c8c7f17ea9ba625cb42e5b3e90e6c457",
        ],
        ["gradient_1500x1000"] = [
            "2811261c7a611cdb0735973e20cc5bb4",
            "55d66fbed1cf1d84dba54dd9003bc737",
            "55d66fbed1cf1d84dba54dd9003bc737",
            "898ef49901116c76433cb7e08623245d",
        ],
    };

    private static byte[] BuildFixture(string name) => name switch
    {
        "border_crop_2000" => SyntheticDngBuilder.BuildBorderCropDng(rawSize: 2000, margin: 200, innerSize: 1600, borderPx: 4),
        "checkerboard_1024" => SyntheticDngBuilder.BuildCheckerboardDng(1024, 1024, squarePx: 16),
        "gradient_1500x1000" => SyntheticDngBuilder.BuildGradientLeftToRightDng(1500, 1000),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    public static TheoryData<string> Fixtures => [.. Golden.Keys];

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Serial_and_parallel_outputs_match_golden_fingerprints(string fixture)
    {
        var dng = BuildFixture(fixture);
        string dir = Path.Combine(Path.GetTempPath(), $"dng_golden_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string dngPath = Path.Combine(dir, fixture + ".dng");
            File.WriteAllBytes(dngPath, dng);

            var serial = RunAndFingerprint(dir, dngPath, "s", Serial);
            var parallel = RunAndFingerprint(dir, dngPath, "p", Environment.ProcessorCount);

            Assert.True(serial.SequenceEqual(parallel),
                $"{fixture}: serial vs parallel outputs differ\n  serial:   {string.Join(' ', serial)}\n  parallel: {string.Join(' ', parallel)}");

            var expected = Golden[fixture];
            Assert.True(expected.SequenceEqual(serial),
                $"{fixture}: output differs from pinned golden\n"
                + $"  expected: [\"{string.Join("\", \"", expected)}\"]\n"
                + $"  actual:   [\"{string.Join("\", \"", serial)}\"]");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string[] RunAndFingerprint(string dir, string dngPath, string tag, int threads)
    {
        string s1 = Path.Combine(dir, $"{tag}_stage1.tif");
        string s2 = Path.Combine(dir, $"{tag}_stage2.tif");
        string s3 = Path.Combine(dir, $"{tag}_stage3.tif");
        string jpg = Path.Combine(dir, $"{tag}_render.jpg");

        int exit = Cli.Run(["-threads", threads.ToString(), "-1", s1, "-2", s2, "-3", s3, "-jpeg", jpg, dngPath]);
        Assert.Equal(0, exit);

        return [Md5(s1), Md5(s2), Md5(s3), Md5(jpg)];
    }

    private static string Md5(string path) => DngFingerprint.MD5(File.ReadAllBytes(path)).ToString();
}
