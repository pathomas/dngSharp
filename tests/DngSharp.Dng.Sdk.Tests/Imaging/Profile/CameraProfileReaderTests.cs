using DngSharp.Dng.Sdk.Container;
using DngSharp.Dng.Sdk.Imaging.Profile;
using DngSharp.Dng.Sdk.IO;
using DngSharp.Dng.Sdk.Math;
using DngSharp.Dng.Sdk.Metadata;
using DngSharp.Dng.Sdk.Pipeline;

namespace DngSharp.Dng.Sdk.Tests.Imaging.Profile;

public class CameraProfileReaderTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    private static string SamplesDir => Path.Combine(RepoRoot, "dng_sdk_1_7_1", "sample_files");

    [Fact]
    public void CameraProfileReader_reads_pgtm2_sample_profile()
    {
        var sample = Path.Combine(SamplesDir, "05_PGTM2_unsigned8.dng");
        if (!File.Exists(sample)) return;

        using var stream = DngFileStream.OpenRead(sample);
        var container = DngContainer.Parse(stream);
        var ifd = container.TopLevelIfds[0];
        var shared = new DngShared();

        var profile = CameraProfileReader.Read(stream, ifd, container.Header.BigEndian, shared);

        Assert.NotNull(profile);
        Assert.NotEmpty(profile!.Illuminants);
        Assert.Equal(5003.0, profile.Illuminants[0].Kelvin);
        Assert.NotNull(profile.Illuminants[0].ColorMatrix);
        Assert.NotNull(profile.Illuminants[0].ForwardMatrix);
        AssertMatrixHasNonZeroEntry(profile.Illuminants[0].ColorMatrix!);
        Assert.NotNull(shared.AsShotNeutral);
    }

    [Fact]
    public void CameraProfileReader_reads_bayer_sample_profile()
    {
        var sample = Path.Combine(SamplesDir, "03_jxl_bayer_raw_integer.dng");
        if (!File.Exists(sample)) return;

        using var stream = DngFileStream.OpenRead(sample);
        var container = DngContainer.Parse(stream);
        var ifd = container.TopLevelIfds[0];
        var shared = new DngShared();

        var profile = CameraProfileReader.Read(stream, ifd, container.Header.BigEndian, shared);

        Assert.NotNull(profile);
        Assert.NotEmpty(profile!.Illuminants);
        Assert.True(profile.Illuminants[0].Kelvin > 0.0);
        Assert.NotNull(profile.Illuminants[0].ColorMatrix);
        Assert.NotNull(profile.Illuminants[0].ForwardMatrix);
        AssertMatrixHasNonZeroEntry(profile.Illuminants[0].ColorMatrix!);
        Assert.True(shared.AsShotNeutral is not null || shared.AsShotWhiteXy is not null);
    }

    [Fact]
    public void EstimateAsShotKelvin_returns_value_for_real_dng_profile()
    {
        var sample = Path.Combine(SamplesDir, "05_PGTM2_unsigned8.dng");
        if (!File.Exists(sample)) return;

        using var stream = DngFileStream.OpenRead(sample);
        var container = DngContainer.Parse(stream);
        var ifd = container.TopLevelIfds[0];
        var shared = new DngShared();
        var profile = CameraProfileReader.Read(stream, ifd, container.Header.BigEndian, shared);

        Assert.NotNull(profile);

        var negative = new DngNegative(new DngHost())
        {
            Shared = shared,
        };
        negative.Profiles.Add(profile!);

        var kelvin = negative.EstimateAsShotKelvin();

        Assert.NotNull(kelvin);
        Assert.True(kelvin.Value > 0.0);
    }

    private static void AssertMatrixHasNonZeroEntry(DngMatrix matrix)
    {
        int nonZeroCount = 0;
        for (int row = 0; row < matrix.Rows; row++)
            for (int col = 0; col < matrix.Cols; col++)
                if (matrix[row, col] != 0.0)
                    nonZeroCount++;

        Assert.NotEqual(0, nonZeroCount);
    }
}
