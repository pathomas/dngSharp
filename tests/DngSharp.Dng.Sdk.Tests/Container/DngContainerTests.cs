using DngSharp.Dng.Sdk.Container;
using DngSharp.Dng.Sdk.IO;
using DngSharp.Dng.Sdk.Tiff;

namespace DngSharp.Dng.Sdk.Tests.Container;

public class DngContainerTests
{
    private static readonly string SamplesDir = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "dng_sdk_1_7_1", "sample_files");

    private static string SamplePath(string name) => Path.GetFullPath(Path.Combine(SamplesDir, name));

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

    [Fact]
    public void Synthetic_tiff_header_parses()
    {
        byte[] bytes =
        [
            (byte)'I', (byte)'I',
            42, 0,
            8, 0, 0, 0,
            0, 0,
            0, 0, 0, 0,
        ];
        using var s = DngMemoryStream.WrapNoCopy(bytes);
        var c = DngContainer.Parse(s);
        Assert.False(c.Header.BigEndian);
        Assert.False(c.Header.BigTiff);
        Assert.Equal(8, c.Header.FirstIfdOffset);
        Assert.Single(c.TopLevelIfds);
        Assert.Empty(c.TopLevelIfds[0].Entries);
    }

    [Fact]
    public void Big_endian_header_detected()
    {
        byte[] bytes =
        [
            (byte)'M', (byte)'M',
            0, 42,
            0, 0, 0, 8,
            0, 0,
            0, 0, 0, 0,
        ];
        using var s = DngMemoryStream.WrapNoCopy(bytes);
        var c = DngContainer.Parse(s);
        Assert.True(c.Header.BigEndian);
    }

    [Fact]
    public void BigTiff_magic_43_detected()
    {
        byte[] bytes =
        [
            (byte)'I', (byte)'I',
            43, 0,
            8, 0, 0, 0,
            16, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0,
        ];
        using var s = DngMemoryStream.WrapNoCopy(bytes);
        var c = DngContainer.Parse(s);
        Assert.True(c.Header.BigTiff);
        Assert.Equal(16, c.Header.FirstIfdOffset);
    }

    [Theory]
    [MemberData(nameof(AllSamples))]
    public void Sample_dng_parses_and_classifies(string name)
    {
        if (name == "__no_samples__") return;

        string path = SamplePath(name);

        using var stream = DngFileStream.OpenRead(path);
        var c = DngContainer.Parse(stream);

        // Every shipped sample is little-endian TIFF (II) — verify.
        Assert.False(c.Header.BigEndian, $"{name}: expected LE");
        Assert.True(c.AllIfds.Count > 0, $"{name}: must have at least one IFD");
        Assert.True(c.MainIndex >= 0, $"{name}: must have a main IFD, got {c.MainIndex}");

        // Main IFD must declare ImageWidth/ImageLength.
        var main = c.AllIfds[c.MainIndex];
        Assert.NotNull(main.Find(DngTagCode.ImageWidth));
        Assert.NotNull(main.Find(DngTagCode.ImageLength));

        // Top-level IFD 0 must carry DNGVersion (DNG marker).
        var dngVer = c.TopLevelIfds[0].Find(DngTagCode.DNGVersion);
        Assert.NotNull(dngVer);
        Assert.Equal(TiffDataType.Byte, dngVer.Type);
        Assert.Equal(4u, dngVer.Count);
    }

    [Fact]
    public void Jxl_sample_main_image_uses_jxl_compression()
    {
        string path = SamplePath("01_jxl_linear_raw_integer.dng");
        if (!File.Exists(path)) return;

        using var stream = DngFileStream.OpenRead(path);
        var c = DngContainer.Parse(stream);

        var main = c.AllIfds[c.MainIndex];
        var compEntry = main.Find(DngTagCode.Compression);
        Assert.NotNull(compEntry);
        var comp = (Compression)compEntry.GetScalarUInt(c.Header.BigEndian);
        Assert.Equal(Compression.Jxl, comp);
    }
}
