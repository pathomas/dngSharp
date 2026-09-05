using DngSharp.Dng.Sdk.Container;
using DngSharp.Dng.Sdk.Imaging.Profile;
using DngSharp.Dng.Sdk.IO;
using DngSharp.Dng.Sdk.Metadata;
using DngSharp.Dng.Sdk.Render;

namespace DngSharp.Dng.Sdk.Tests.Imaging.Profile;

public class ToneCurveTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static string SamplesDir => Path.Combine(RepoRoot, "dng_sdk_1_7_1", "sample_files");

    [Fact]
    public void ToneCurve_is_read_from_pgtm2_sample()
    {
        var sample = Path.Combine(SamplesDir, "05_PGTM2_unsigned8.dng");
        if (!File.Exists(sample)) return;

        using var stream = DngFileStream.OpenRead(sample);
        var container = DngContainer.Parse(stream);
        var shared = new DngShared();

        var profile = CameraProfileReader.Read(
            stream,
            container.TopLevelIfds[0],
            container.Header.BigEndian,
            shared);

        Assert.NotNull(profile);
        Assert.NotNull(profile!.ToneCurve);
        Assert.Equal([(double)0.0, (double)0.0], [profile.ToneCurve![0].Input, profile.ToneCurve[0].Output]);
        Assert.Equal([(double)1.0, (double)1.0], [profile.ToneCurve[^1].Input, profile.ToneCurve[^1].Output]);
    }

    [Fact]
    public void ToneCurve_identity_returns_same_value()
    {
        var curve = new (double Input, double Output)[] { (0.0, 0.0), (1.0, 1.0) };

        Assert.Equal(0.5, HdrToneMapper.EvaluateCurve(0.5, curve));
    }

    [Fact]
    public void ToneCurve_nonlinear_maps_correctly()
    {
        var curve = new (double Input, double Output)[] { (0.0, 0.0), (0.5, 0.25), (1.0, 1.0) };

        Assert.InRange(HdrToneMapper.EvaluateCurve(0.25, curve), 0.124, 0.126);
    }
}
