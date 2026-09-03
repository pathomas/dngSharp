using Dng.Sdk.Container;
using Dng.Sdk.IO;
using Dng.Sdk.Metadata.Exif;

namespace Dng.Sdk.Tests.Metadata;

public class ExifReaderIntegrationTests
{
    private static readonly string SamplesDir = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "dng_sdk_1_7_1", "sample_files");

    public static IEnumerable<object[]> AllSamples()
    {
        if (!Directory.Exists(SamplesDir))
        {
            yield return new object[] { "__no_samples__" };
            yield break;
        }

        foreach (var dng in Directory.EnumerateFiles(SamplesDir, "*.dng"))
            yield return new object[] { Path.GetFileName(dng) };
    }

    [Theory]
    [MemberData(nameof(AllSamples))]
    public void Top_level_ifd_yields_a_populated_exif(string name)
    {
        if (name == "__no_samples__") return;

        string path = Path.GetFullPath(Path.Combine(SamplesDir, name));

        using var stream = DngFileStream.OpenRead(path);
        var container = DngContainer.Parse(stream);

        var exif = new DngExif();
        ExifReader.Read(stream, container.TopLevelIfds[0], container.Header.BigEndian, exif);

        // Every sample is processed by Adobe's cr_validate test harness, so
        // Software is always populated.
        Assert.False(string.IsNullOrEmpty(exif.Software), $"{name}: Software should be populated");

        // And the reader retains unknown tags for host inspection — every
        // real DNG has more tags than ExifReader explicitly handles.
        Assert.NotEmpty(exif.UnknownTags);
    }

    [Fact]
    public void Sony_sample_extracts_make_and_model()
    {
        string path = Path.GetFullPath(Path.Combine(SamplesDir, "01_jxl_linear_raw_integer.dng"));
        if (!File.Exists(path)) return;

        using var stream = DngFileStream.OpenRead(path);
        var container = DngContainer.Parse(stream);

        var exif = new DngExif();
        ExifReader.Read(stream, container.TopLevelIfds[0], container.Header.BigEndian, exif);

        Assert.Equal("SONY", exif.Make);
        Assert.Equal("ILCE-7RM4", exif.Model);
        Assert.Contains("cr_validate", exif.Software);
    }
}
