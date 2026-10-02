using BenchmarkDotNet.Attributes;
using DngSharp.Dng.Sdk.Codecs;
using DngSharp.Dng.Sdk.Container;
using DngSharp.Dng.Sdk.IO;
using DngSharp.Dng.Sdk.Jxl;
using DngSharp.Dng.Sdk.Pipeline;

namespace DngSharp.Dng.Sdk.Benchmarks;

/// <summary>
/// Phase 10 pipeline-stage benchmark: measures full Stage-1 materialization
/// (<see cref="StripReader.ReadStage1"/>) for the JXL-compressed Bayer sample,
/// which is dominated by the <c>libjxl</c> decode call
/// (<see cref="JxlDecoder.Decode"/>) once IFD parsing is done.
///
/// <para>Pair this with <see cref="ContainerParseBenchmarks.Parse"/> on the
/// same sample (<c>03_jxl_bayer_raw_integer.dng</c>) to isolate decode cost:
/// <c>ReadStage1 time − Parse time ≈ JXL decode + strip-copy cost</c>.
/// Requires <c>libjxl</c> on the loader path (see
/// <c>tools/build-libjxl.ps1</c>); the benchmark throws in
/// <see cref="Setup"/> if unavailable rather than silently reporting an
/// empty/misleading result.</para>
///
/// <para>Run: <c>dotnet run -c Release --project tests/DngSharp.Dng.Sdk.Benchmarks -- --filter *JxlDecodeBenchmarks*</c></para>
/// </summary>
[MemoryDiagnoser]
public class JxlDecodeBenchmarks
{
    private static readonly string SamplePath = FindSample();

    private static string FindSample()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "dng_sdk_1_7_1", "sample_files",
                "03_jxl_bayer_raw_integer.dng");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException(
            "Could not locate 03_jxl_bayer_raw_integer.dng walking up from " + AppContext.BaseDirectory);
    }

    private byte[] _fileBytes = null!;

    [GlobalSetup]
    public void Setup()
    {
        if (!JxlDecoder.IsAvailable)
            throw new InvalidOperationException(
                "libjxl is not available on the loader path — build it via tools/build-libjxl.ps1 "
                + "before running JxlDecodeBenchmarks.");

        // Read the file into memory once so per-iteration cost is pure
        // parse + strip-read + JXL decode, not disk I/O.
        _fileBytes = File.ReadAllBytes(SamplePath);
    }

    [Benchmark]
    public int ReadStage1()
    {
        using var stream = DngMemoryStream.WrapNoCopy(_fileBytes);
        var container = DngContainer.Parse(stream);

        var registry = new CodecRegistry();
        registry.Register(new JxlDecoder());

        var result = StripReader.ReadStage1(stream, container, registry);
        return (int)result.Stage1.Bounds.W;
    }
}
