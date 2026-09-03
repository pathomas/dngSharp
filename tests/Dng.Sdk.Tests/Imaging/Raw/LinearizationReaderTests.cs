using Dng.Sdk.Container;
using Dng.Sdk.Imaging.Raw;
using Dng.Sdk.IO;

namespace Dng.Sdk.Tests.Imaging.Raw;

/// <summary>
/// LinearizationReader + MosaicInfoReader tests. Uses the shipped
/// Bayer sample to verify the readers produce correct values.
/// </summary>
public class LinearizationReaderTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    private static string SamplesDir => Path.Combine(RepoRoot, "dng_sdk_1_7_1", "sample_files");

    // ── LinearizationReader ───────────────────────────────────────────────────

    [Fact]
    public void LinearizationReader_reads_pgtm2_sample()
    {
        var sample = Path.Combine(SamplesDir, "05_PGTM2_unsigned8.dng");
        if (!File.Exists(sample)) return;

        using var stream = DngFileStream.OpenRead(sample);
        var container = DngContainer.Parse(stream);
        var ifd = container.AllIfds[container.MainIndex];
        bool be = container.Header.BigEndian;

        var lin = LinearizationReader.Read(stream, ifd, be, isFloat: false);

        Assert.NotEmpty(lin.WhiteLevel);
        Assert.Equal(65535.0, lin.WhiteLevel[0]);
        Assert.NotEmpty(lin.BlackLevel);
    }

    [Fact]
    public void LinearizationReader_reads_bayer_rational_black_level()
    {
        // 03_jxl_bayer_raw_integer.dng: BlackLevel = Rational 131072/256 = 512,
        // 4 entries (RGGB each = 512), WhiteLevel = 16383.
        var sample = Path.Combine(SamplesDir, "03_jxl_bayer_raw_integer.dng");
        if (!File.Exists(sample)) return;

        using var stream = DngFileStream.OpenRead(sample);
        var container = DngContainer.Parse(stream);
        var ifd = container.AllIfds[container.MainIndex];
        bool be = container.Header.BigEndian;

        var lin = LinearizationReader.Read(stream, ifd, be, isFloat: false);

        Assert.NotEmpty(lin.BlackLevel);
        Assert.All(lin.BlackLevel, v => Assert.InRange(v, 511.9, 512.1));
        Assert.NotEmpty(lin.WhiteLevel);
        Assert.Equal(16383.0, lin.WhiteLevel[0]);
        Assert.Equal((2u, 2u), lin.BlackLevelRepeatDim);
    }

    [Fact]
    public void LinearizationReader_pgtm2_float16_has_actual_white_level()
    {
        // 07_PGTM2_float16.dng: despite the "float16" name, this sample has NO
        // SampleFormat tag so it is actually stored as UInt16. WhiteLevel tag = 65535.
        // The reader must return the actual tag value, not hard-code 1.0.
        var sample = Path.Combine(SamplesDir, "07_PGTM2_float16.dng");
        if (!File.Exists(sample)) return;

        using var stream = DngFileStream.OpenRead(sample);
        var container = DngContainer.Parse(stream);
        var ifd = container.AllIfds[container.MainIndex];

        // isFloat=false because the file has no SampleFormat tag (defaults to UnsignedInt).
        var lin = LinearizationReader.Read(stream, ifd, bigEndian: false, isFloat: false);

        Assert.Equal(0.0, lin.BlackLevel[0]);
        Assert.Equal(65535.0, lin.WhiteLevel[0]);   // actual tag value, not 1.0
    }

    [Fact]
    public void LinearizationReader_float_image_uses_tag_white_level()
    {
        // For a true float image (SampleFormat=3), the WhiteLevel tag stores the
        // actual maximum float value (e.g. 32768 for iPhone ProRAW), NOT 1.0.
        // Test that we read the tag rather than returning the hard-coded 1.0.
        var sample = Path.Combine(
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")),
            "images", "IMG_3353-HDR.dng");
        if (!File.Exists(sample)) return;   // skip if images/ not present

        using var stream = DngFileStream.OpenRead(sample);
        var container = DngContainer.Parse(stream);
        // The raw main IFD is the SubIFD; WhiteLevel/BlackLevel live there.
        var ifd = container.AllIfds[container.MainIndex];

        var lin = LinearizationReader.Read(stream, ifd, bigEndian: false, isFloat: true);

        // iPhone ProRAW: BlackLevel = 0, WhiteLevel = 32768 (Float16 HDR range)
        Assert.Equal(0.0, lin.BlackLevel[0]);
        Assert.Equal(32768.0, lin.WhiteLevel[0]);
    }

    // ── MosaicInfoReader ──────────────────────────────────────────────────────

    [Fact]
    public void MosaicInfoReader_returns_null_for_linear_raw()
    {
        var sample = Path.Combine(SamplesDir, "05_PGTM2_unsigned8.dng");
        if (!File.Exists(sample)) return;

        using var stream = DngFileStream.OpenRead(sample);
        var container = DngContainer.Parse(stream);
        var ifd = container.AllIfds[container.MainIndex];

        var mosaic = MosaicInfoReader.Read(stream, ifd, bigEndian: false);

        Assert.Null(mosaic);
    }

    [Fact]
    public void MosaicInfoReader_reads_rggb_bayer_pattern()
    {
        var sample = Path.Combine(SamplesDir, "03_jxl_bayer_raw_integer.dng");
        if (!File.Exists(sample)) return;

        using var stream = DngFileStream.OpenRead(sample);
        var container = DngContainer.Parse(stream);
        var ifd = container.AllIfds[container.MainIndex];
        bool be = container.Header.BigEndian;

        var mosaic = MosaicInfoReader.Read(stream, ifd, be);

        Assert.NotNull(mosaic);
        Assert.Equal((2u, 2u), mosaic!.Pattern);
        // CFAPattern = [0, 1, 1, 2] → RGGB
        Assert.Equal(4, mosaic.CfaPlaneColor.Length);
        Assert.Equal(0, mosaic.CfaPlaneColor[0]); // R
        Assert.Equal(1, mosaic.CfaPlaneColor[1]); // G
        Assert.Equal(1, mosaic.CfaPlaneColor[2]); // G
        Assert.Equal(2, mosaic.CfaPlaneColor[3]); // B
    }
}
