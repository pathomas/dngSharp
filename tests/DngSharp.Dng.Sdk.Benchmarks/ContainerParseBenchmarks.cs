using BenchmarkDotNet.Attributes;
using DngSharp.Dng.Sdk.Container;
using DngSharp.Dng.Sdk.IO;

namespace DngSharp.Dng.Sdk.Benchmarks;

/// <summary>
/// Baseline parse-cost benchmark. Measures how long <c>DngContainer.Parse</c>
/// takes on a representative subset of the shipped samples. This anchors the
/// "just walk the TIFF/IFD tree" cost that every pipeline stage pays before
/// doing real work — the number to beat when adding SIMD or reworking the
/// IFD scanner.
/// </summary>
[MemoryDiagnoser]
public class ContainerParseBenchmarks
{
    private static readonly string SamplesDir = FindSamplesDir();

    private static string FindSamplesDir()
    {
        // BDN spawns the benchmark process from a nested per-benchmark
        // build output (bin\Release\net10.0\<guid>\bin\Release\net10.0\),
        // so a fixed relative walk from AppContext.BaseDirectory misses
        // the repo root. Walk upwards until we find the samples dir.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "dng_sdk_1_7_1", "sample_files");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate dng_sdk_1_7_1/sample_files walking up from "
            + AppContext.BaseDirectory);
    }

    // Representative subset — avoids running BDN's outer×inner loop over
    // every 25 MB sample (which would swamp the interesting differences).
    public static IEnumerable<string> Samples =>
    [
        "05_PGTM2_unsigned8.dng",       // 240 KB, uncompressed LinearRaw
        "04_PGTM2_per_profile.dng",     // 5.7 MB, tests SubIFD handling
        "14_hdr_sdr_profiles.dng",      // 1.2 MB, tests ExtraCameraProfiles walk
        "03_jxl_bayer_raw_integer.dng", // 24 MB, largest sample
    ];

    [ParamsSource(nameof(Samples))]
    public string Sample { get; set; } = "";

    private string _path = "";

    [GlobalSetup]
    public void Setup()
    {
        _path = Path.Combine(SamplesDir, Sample);
        if (!File.Exists(_path))
            throw new FileNotFoundException($"sample not found: {_path}");
    }

    [Benchmark]
    public int Parse()
    {
        using var stream = DngFileStream.OpenRead(_path);
        var container = DngContainer.Parse(stream);
        return container.AllIfds.Count;
    }
}
