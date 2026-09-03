using System.Diagnostics;

namespace Dng.Sdk.Tests.Writer;

/// <summary>
/// End-to-end CLI tests: invoke the published Dng.Validate binary against a
/// real sample DNG and verify it produces the expected output. The test
/// runner builds Dng.Validate as a transitive ProjectReference; we look up
/// its assembly path via Type.GetType("Cli, Dng.Validate") indirection
/// through the dll's location.
/// </summary>
public class DngValidateCliTests
{
    private static readonly string SamplesDir = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "dng_sdk_1_7_1", "sample_files");

    private static string Sample(string name) =>
        Path.GetFullPath(Path.Combine(SamplesDir, name));

    private static readonly string CliDll = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "src", "Dng.Validate", "bin", "Release", "net10.0", "Dng.Validate.dll"));

    private static (int ExitCode, string Stdout, string Stderr) Invoke(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(CliDll);
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(milliseconds: 30_000);
        return (p.ExitCode, stdout, stderr);
    }

    [Fact]
    public void Help_prints_usage_with_zero_exit_when_requested()
    {
        if (!File.Exists(CliDll)) return; // skip if not built yet
        var (code, stdout, _) = Invoke("--help");
        Assert.Equal(0, code);
        Assert.Contains("Usage:", stdout, StringComparison.Ordinal);
        Assert.Contains("-v", stdout, StringComparison.Ordinal);
        Assert.Contains("-dng", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void No_args_prints_help_and_exits_nonzero()
    {
        if (!File.Exists(CliDll)) return;
        var (code, stdout, _) = Invoke();
        Assert.NotEqual(0, code);
        Assert.Contains("Usage:", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Summary_mode_extracts_main_image_geometry()
    {
        var path = Sample("01_jxl_linear_raw_integer.dng");
        if (!File.Exists(CliDll) || !File.Exists(path)) return;
        var (code, stdout, _) = Invoke(path);
        Assert.Equal(0, code);
        Assert.Contains("byte order", stdout, StringComparison.Ordinal);
        Assert.Contains("9504x6336", stdout, StringComparison.Ordinal);
        Assert.Contains("compression=Jxl", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Verbose_dumps_dng_version_tag_with_resolved_name()
    {
        var path = Sample("05_PGTM2_unsigned8.dng");
        if (!File.Exists(CliDll) || !File.Exists(path)) return;
        var (code, stdout, _) = Invoke("-v", path);
        Assert.Equal(0, code);
        Assert.Contains("DNGVersion", stdout, StringComparison.Ordinal);
        Assert.Contains("DNGBackwardVersion", stdout, StringComparison.Ordinal);
        // Verbose mode resolves tag IDs to their named enum values.
        Assert.Contains("UniqueCameraModel", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Dng_round_trip_writes_a_file_that_re_parses()
    {
        var src = Sample("05_PGTM2_unsigned8.dng");
        if (!File.Exists(CliDll) || !File.Exists(src)) return;

        var outPath = Path.Combine(Path.GetTempPath(), $"dng_cli_rt_{Guid.NewGuid():N}.dng");
        try
        {
            var (code, _, _) = Invoke("-dng", outPath, src);
            Assert.Equal(0, code);
            Assert.True(File.Exists(outPath), $"-dng failed to produce {outPath}");
            Assert.True(new FileInfo(outPath).Length > 100, "round-trip file is implausibly small");

            // Parse it back via the CLI and verify it still parses.
            var (code2, stdout2, _) = Invoke(outPath);
            Assert.Equal(0, code2);
            Assert.Contains("200x200", stdout2, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(outPath)) File.Delete(outPath);
        }
    }

    [Fact]
    public void Unknown_flag_fails_with_helpful_message()
    {
        var path = Sample("01_jxl_linear_raw_integer.dng");
        if (!File.Exists(CliDll) || !File.Exists(path)) return;
        var (code, _, stderr) = Invoke("-bogus", path);
        Assert.NotEqual(0, code);
        Assert.Contains("-bogus", stderr, StringComparison.Ordinal);
    }
}
