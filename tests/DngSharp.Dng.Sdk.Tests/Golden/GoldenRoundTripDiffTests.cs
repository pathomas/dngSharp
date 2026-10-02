using DngSharp.Dng.Sdk.Container;
using DngSharp.Dng.Sdk.IO;

namespace DngSharp.Dng.Sdk.Tests.Golden;

/// <summary>
/// Tier-1 <c>-dng</c> round-trip golden diff (Phase 10). Runs the managed
/// <c>DngSharp.Dng.Validate -dng</c> pipeline over each shipped sample, re-parses
/// the produced file with the managed parser, and asserts the round-trip
/// preserves byte-order and every top-level (and nested SubIFD) IFD's entry
/// count (modulo the tags the round-tripper intentionally skips — Strip and
/// Tile offsets, which the minimal writer doesn't relocate today).
///
/// <para>This is stronger than the smoke test in
/// <c>Writer/DngValidateCliTests.Dng_round_trip_writes_a_file_that_re_parses</c>:
/// that one only checks that the output re-parses at all. Here we assert
/// per-IFD structural equivalence (including nested SubIFDs) and catch entry
/// loss or duplication.</para>
///
/// <para>A tier-2 diff against native <c>tests/golden/&lt;sample&gt;/roundtrip.dng</c>
/// is deferred — writer ordering differences between managed and native are
/// expected today, so a byte- or IFD-order comparison would fail for
/// reasons unrelated to correctness. The managed→managed self-diff below is
/// the useful invariant.</para>
/// </summary>
public class GoldenRoundTripDiffTests
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static string SamplesDir => Path.Combine(RepoRoot, "dng_sdk_1_7_1", "sample_files");

    public static IEnumerable<object[]> AllSamples()
    {
        if (!Directory.Exists(SamplesDir))
        {
            yield return new object[] { "__no_samples__" };
            yield break;
        }

        foreach (var dng in Directory.EnumerateFiles(SamplesDir, "*.dng").OrderBy(p => p, StringComparer.Ordinal))
            yield return new object[] { Path.GetFileNameWithoutExtension(dng) };
    }

    [Theory]
    [MemberData(nameof(AllSamples))]
    public void Round_trip_preserves_ifd_shape(string sampleStem)
    {
        if (sampleStem == "__no_samples__") return;

        string srcPath = Path.Combine(SamplesDir, sampleStem + ".dng");
        if (!File.Exists(srcPath)) return;

        DngContainer srcContainer;
        using (var srcStream = DngFileStream.OpenRead(srcPath))
            srcContainer = DngContainer.Parse(srcStream);

        string outPath = Path.Combine(Path.GetTempPath(), $"dng_rt_{sampleStem}_{Guid.NewGuid():N}.dng");
        try
        {
            InvokeManagedRoundTrip(srcPath, outPath);
            Assert.True(File.Exists(outPath), $"round-trip did not produce {outPath}");

            DngContainer rtContainer;
            using (var rtStream = DngFileStream.OpenRead(outPath))
                rtContainer = DngContainer.Parse(rtStream);

            // Byte order preserved.
            Assert.Equal(srcContainer.Header.BigEndian, rtContainer.Header.BigEndian);

            // Top-level IFD count preserved.
            Assert.Equal(srcContainer.TopLevelIfds.Count, rtContainer.TopLevelIfds.Count);

            for (int i = 0; i < srcContainer.TopLevelIfds.Count; i++)
                AssertIfdShapeMatches(srcContainer.TopLevelIfds[i], rtContainer.TopLevelIfds[i]);
        }
        finally
        {
            if (File.Exists(outPath)) File.Delete(outPath);
        }
    }

    /// <summary>
    /// Recursively asserts that <paramref name="rtIfd"/> preserves
    /// <paramref name="srcIfd"/>'s entry count (minus the intentionally
    /// dropped Strip/Tile offset tags) and SubIFD structure.
    /// </summary>
    private static void AssertIfdShapeMatches(TiffIfd srcIfd, TiffIfd rtIfd)
    {
        int expected = srcIfd.Entries.Count
            - srcIfd.Entries.Count(e =>
                e.Tag is DngSharp.Dng.Sdk.Tiff.DngTagCode.StripOffsets or DngSharp.Dng.Sdk.Tiff.DngTagCode.TileOffsets);

        Assert.Equal(expected, rtIfd.Entries.Count);
        Assert.Equal(srcIfd.SubIfds.Count, rtIfd.SubIfds.Count);

        for (int i = 0; i < srcIfd.SubIfds.Count; i++)
            AssertIfdShapeMatches(srcIfd.SubIfds[i], rtIfd.SubIfds[i]);
    }

    private static void InvokeManagedRoundTrip(string src, string dst)
    {
        var cliDll = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "DngSharp.Dng.Validate", "bin", "Release", "net10.0", "DngSharp.Dng.Validate.dll"));
        if (!File.Exists(cliDll))
            throw new FileNotFoundException($"managed CLI not built: {cliDll}");

        var psi = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(cliDll);
        psi.ArgumentList.Add("-dng");
        psi.ArgumentList.Add(dst);
        psi.ArgumentList.Add(src);

        using var p = System.Diagnostics.Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(milliseconds: 30_000);
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"managed -dng failed ({p.ExitCode}): {stderr}");
    }
}
