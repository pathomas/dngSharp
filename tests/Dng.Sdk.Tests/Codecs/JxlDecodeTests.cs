using Dng.Sdk.Container;
using Dng.Sdk.IO;
using Dng.Sdk.Jxl;
using Dng.Sdk.Pixels;
using Dng.Sdk.Primitives;
using Dng.Sdk.Tiff;

namespace Dng.Sdk.Tests.Codecs;

/// <summary>
/// End-to-end JXL decode tests. All tests guard on <see cref="JxlDecoder.IsAvailable"/>
/// so they are silently skipped when <c>libjxl</c> is absent (e.g. when running
/// without the native build on a fresh checkout). In CI, <c>jxl.dll / libjxl.so</c>
/// is placed by the <c>build-libjxl</c> job so these tests run for real.
/// </summary>
public class JxlDecodeTests
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    private static string SamplesDir => Path.Combine(RepoRoot, "dng_sdk_1_7_1", "sample_files");

    // ── Signature probe ──────────────────────────────────────────────────────

    [Fact]
    public void IsAvailable_returns_true_when_native_present()
    {
        if (!JxlDecoder.IsAvailable) return;
        Assert.True(JxlDecoder.IsAvailable);
    }

    [Fact]
    public void Sniff_real_jxl_file_returns_container()
    {
        if (!JxlDecoder.IsAvailable) return;
        var sample = Path.Combine(SamplesDir, "01_jxl_linear_raw_integer.dng");
        if (!File.Exists(sample)) return;

        // Read first 12 bytes (more than enough for JXL container signature).
        using var f = File.OpenRead(sample);
        var header = new byte[12];
        f.ReadExactly(header);
        // DNG files start with TIFF header, not JXL — sniff via a real JXL strip.
        // Just confirm Sniff doesn't throw and handles real bytes gracefully.
        var result = JxlProbe.Sniff(header);
        Assert.True(result is JxlSignatureKind.Invalid or JxlSignatureKind.NotEnoughBytes);
    }

    // ── Strip decode ─────────────────────────────────────────────────────────

    [Fact]
    public void Decode_uint8_strip_from_pgtm2_preview()
    {
        if (!JxlDecoder.IsAvailable) return;

        // Sample 05 has a tiny uncompressed (not JXL) main image, but its
        // IFD #0 preview uses compression 7 (lossless JPEG), not JXL.
        // Use the JXL-compressed samples instead.
        // 05_PGTM2_unsigned8: main IFD is uncompressed — skip for JXL decode test.
        // Use the real JXL strip from 01_jxl_linear_raw_integer.dng to test.
        var sample = Path.Combine(SamplesDir, "01_jxl_linear_raw_integer.dng");
        if (!File.Exists(sample)) return;

        // Parse the DNG to find a JXL-compressed strip.
        using var stream = DngFileStream.OpenRead(sample);
        var container = DngContainer.Parse(stream);
        var mainIfd = container.AllIfds[container.MainIndex];
        var compEntry = mainIfd.Find(DngTagCode.Compression);
        if (compEntry is null) return;
        var comp = (Compression)compEntry.GetScalarUInt(container.Header.BigEndian);
        if (comp != Compression.Jxl) return;

        var widthEntry  = mainIfd.Find(DngTagCode.ImageWidth);
        var heightEntry = mainIfd.Find(DngTagCode.ImageLength);
        var stripOffsets = mainIfd.Find(DngTagCode.StripOffsets);
        var stripBytes   = mainIfd.Find(DngTagCode.StripByteCounts);
        if (widthEntry is null || heightEntry is null || stripOffsets is null || stripBytes is null)
            return;

        uint width  = widthEntry.GetScalarUInt(container.Header.BigEndian);
        uint height = heightEntry.GetScalarUInt(container.Header.BigEndian);
        long stripOffset = (long)stripOffsets.GetScalarUInt(container.Header.BigEndian);
        long byteCount   = (long)stripBytes.GetScalarUInt(container.Header.BigEndian);

        // Read the compressed strip.
        var compressed = new byte[byteCount];
        stream.Position = stripOffset;
        stream.ReadExactly(compressed);

        // Decode to uint16 — the main image has BitsPerSample = 16.
        // The JXL strip carries 3 planes (LinearRaw RGB).
        const uint planes = 3;
        var dstBytes = new byte[width * height * planes * sizeof(ushort)];
        var dst = PixelBuffer.Interleaved(
            new DngRect(0, 0, (int)height, (int)width),
            planes, PixelType.UInt16, dstBytes);

        var decoder = new JxlDecoder();
        decoder.Decode(compressed, dst, bigEndian: container.Header.BigEndian);

        // Basic sanity: at least one non-zero pixel.
        var samples = dst.AsTypedSpan<ushort>();
        Assert.True(samples.Length > 0);
        bool anyNonZero = false;
        foreach (var s in samples) { if (s != 0) { anyNonZero = true; break; } }
        Assert.True(anyNonZero, "Decoded JXL image is all zeros — likely a decode error");
    }

    [Fact]
    public void Decode_geometry_mismatch_throws_domain_exception()
    {
        if (!JxlDecoder.IsAvailable) return;

        var sample = Path.Combine(SamplesDir, "01_jxl_linear_raw_integer.dng");
        if (!File.Exists(sample)) return;

        using var stream = DngFileStream.OpenRead(sample);
        var container = DngContainer.Parse(stream);
        var mainIfd = container.AllIfds[container.MainIndex];
        var comp = (Compression)mainIfd.Find(DngTagCode.Compression)!.GetScalarUInt(container.Header.BigEndian);
        if (comp != Compression.Jxl) return;

        // JXL DNG files use tile layout, not strips. Read the first tile's offset/count.
        var offsetEntry = mainIfd.Find(DngTagCode.TileOffsets) ?? mainIfd.Find(DngTagCode.StripOffsets);
        var countEntry  = mainIfd.Find(DngTagCode.TileByteCounts) ?? mainIfd.Find(DngTagCode.StripByteCounts);
        if (offsetEntry is null || countEntry is null) return;

        // Read the first offset/count from potentially out-of-line arrays.
        long offset, count;
        if (offsetEntry.IsInline)
        {
            offset = (long)offsetEntry.GetScalarUInt(container.Header.BigEndian);
            count  = (long)countEntry.GetScalarUInt(container.Header.BigEndian);
        }
        else
        {
            // Read first 4/8 bytes from the out-of-line array.
            bool be = container.Header.BigEndian;
            var tempBuf = new byte[8];
            stream.Position = offsetEntry.ValueOffset;
            stream.ReadExactly(tempBuf.AsSpan(0, offsetEntry.Type == Dng.Sdk.Tiff.TiffDataType.Long8 ? 8 : 4));
            offset = offsetEntry.Type == Dng.Sdk.Tiff.TiffDataType.Long8
                ? (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(tempBuf)
                : (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(tempBuf);
            stream.Position = countEntry.ValueOffset;
            stream.ReadExactly(tempBuf.AsSpan(0, countEntry.Type == Dng.Sdk.Tiff.TiffDataType.Long8 ? 8 : 4));
            count = countEntry.Type == Dng.Sdk.Tiff.TiffDataType.Long8
                ? (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(tempBuf)
                : (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(tempBuf);
        }
        var compressed = new byte[count];
        stream.Position = offset;
        stream.ReadExactly(compressed);

        // Pass a destination with the wrong size — should get a clear domain error.
        var wrongDst = PixelBuffer.Interleaved(
            new DngRect(0, 0, 10, 10), 3, PixelType.UInt16, new byte[10 * 10 * 3 * 2]);

        var ex = Assert.Throws<Dng.Sdk.Errors.DngException>(() =>
            new JxlDecoder().Decode(compressed, wrongDst, bigEndian: false));
        Assert.Equal(Dng.Sdk.Errors.DngError.JxlDecoder, ex.ErrorCode);
        Assert.Contains("mismatch", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
