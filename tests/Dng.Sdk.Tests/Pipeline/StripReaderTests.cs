using Dng.Sdk.Codecs;
using Dng.Sdk.Codecs.LosslessJpeg;
using Dng.Sdk.Container;
using Dng.Sdk.Imaging.Raw;
using Dng.Sdk.IO;
using Dng.Sdk.Jxl;
using Dng.Sdk.Pipeline;
using Dng.Sdk.Pixels;
using Dng.Sdk.Tiff;

namespace Dng.Sdk.Tests.Pipeline;

/// <summary>
/// StripReader integration tests. Use real sample DNG files from
/// <c>dng_sdk_1_7_1/sample_files/</c> where available; guard on JXL
/// availability for the JXL-compressed samples.
/// </summary>
public class StripReaderTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    private static string SamplesDir => Path.Combine(RepoRoot, "dng_sdk_1_7_1", "sample_files");

    private static CodecRegistry BuildRegistry()
    {
        // Create a fresh registry — do NOT mutate CodecRegistry.Default (it's
        // a singleton that other tests assert on).
        var r = new CodecRegistry();
        r.Register(new UncompressedDecoder());
        r.Register(new DeflateDecoder());
        r.Register(new LosslessJpegDecoder());
        if (JxlDecoder.IsAvailable) r.Register(new JxlDecoder());
        return r;
    }

    [Fact]
    public void ReadStage1_uncompressed_pgtm2_sample()
    {
        var sample = Path.Combine(SamplesDir, "05_PGTM2_unsigned8.dng");
        if (!File.Exists(sample)) return;

        using var stream = DngFileStream.OpenRead(sample);
        var container = DngContainer.Parse(stream);
        var reg = BuildRegistry();

        var r = StripReader.ReadStage1(stream, container, reg);

        Assert.Equal(200u, r.Stage1.Bounds.W);
        Assert.Equal(200u, r.Stage1.Bounds.H);
        Assert.Equal(3u, r.Stage1.Planes);
        Assert.Equal(PixelType.UInt16, r.Stage1.PixelType);
    }

    [Fact]
    public void ReadStage1_returns_non_empty_image()
    {
        var sample = Path.Combine(SamplesDir, "05_PGTM2_unsigned8.dng");
        if (!File.Exists(sample)) return;

        using var stream = DngFileStream.OpenRead(sample);
        var container = DngContainer.Parse(stream);
        var reg = BuildRegistry();

        var r = StripReader.ReadStage1(stream, container, reg);

        var tile = r.Stage1.GetTile(r.Stage1.Bounds);
        var samples = tile.AsTypedSpan<ushort>();
        Assert.True(samples.Length > 0);

        bool anyNonZero = false;
        foreach (var s in samples) if (s != 0) { anyNonZero = true; break; }
        Assert.True(anyNonZero, "Stage1 image is all zeros — strip decode may have failed");
    }

    [Fact]
    public void ReadStage1_reads_linearization_from_ifd()
    {
        // 05_PGTM2_unsigned8.dng: WhiteLevel=65535 (Short), BlackLevel=0.
        var sample = Path.Combine(SamplesDir, "05_PGTM2_unsigned8.dng");
        if (!File.Exists(sample)) return;

        using var stream = DngFileStream.OpenRead(sample);
        var container = DngContainer.Parse(stream);
        var r = StripReader.ReadStage1(stream, container, BuildRegistry());

        Assert.NotEmpty(r.Linearization.WhiteLevel);
        Assert.Equal(65535.0, r.Linearization.WhiteLevel[0]);
        Assert.NotNull(r.Linearization.BlackLevel);
        Assert.Null(r.Mosaic); // LinearRaw → no mosaic
        Assert.Equal(Photometric.LinearRaw, r.Photometric);
    }

    [Fact]
    public void ReadStage1_jxl_sample_when_available()
    {
        if (!JxlDecoder.IsAvailable) return;
        var sample = Path.Combine(SamplesDir, "01_jxl_linear_raw_integer.dng");
        if (!File.Exists(sample)) return;

        using var stream = DngFileStream.OpenRead(sample);
        var container = DngContainer.Parse(stream);
        var reg = BuildRegistry();

        var r = StripReader.ReadStage1(stream, container, reg);

        Assert.Equal(9504u, r.Stage1.Bounds.W);
        Assert.Equal(6336u, r.Stage1.Bounds.H);
        Assert.Equal(PixelType.UInt16, r.Stage1.PixelType);

        var tile = r.Stage1.GetTile(r.Stage1.Bounds);
        var samples = tile.AsTypedSpan<ushort>();
        bool anyNonZero = false;
        foreach (var s in samples) if (s != 0) { anyNonZero = true; break; }
        Assert.True(anyNonZero, "JXL stage1 image is all zeros");
    }
}
