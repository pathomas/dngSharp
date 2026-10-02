using DngSharp.Dng.Sdk.Codecs;
using DngSharp.Dng.Sdk.Errors;
using DngSharp.Dng.Sdk.Pixels;
using DngSharp.Dng.Sdk.Primitives;
using DngSharp.Dng.Sdk.Tiff;

namespace DngSharp.Dng.Sdk.Tests.Codecs;

public class UncompressedDecoderTests
{
    [Fact]
    public void Round_trips_uint8_le()
    {
        // 4x4 single-plane uint8 — payload is just the raw bytes in row-major order.
        byte[] payload = new byte[16];
        for (int i = 0; i < 16; i++) payload[i] = (byte)i;

        var dst = new byte[16];
        var buf = PixelBuffer.Interleaved(new DngRect(0, 0, 4, 4), 1, PixelType.UInt8, dst);
        new UncompressedDecoder().Decode(payload, buf, bigEndian: false);

        Assert.Equal(payload, dst);
    }

    [Fact]
    public void Round_trips_uint16_with_big_endian_swap()
    {
        // 2x2 uint16 big-endian payload: 0x1234, 0x5678, 0x9ABC, 0xDEF0.
        byte[] payload = [0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC, 0xDE, 0xF0];

        var dst = new byte[8];
        var buf = PixelBuffer.Interleaved(new DngRect(0, 0, 2, 2), 1, PixelType.UInt16, dst);
        new UncompressedDecoder().Decode(payload, buf, bigEndian: true);

        var samples = buf.AsTypedSpan<ushort>();
        Assert.Equal(0x1234, samples[0]);
        Assert.Equal(0x5678, samples[1]);
        Assert.Equal(0x9ABC, samples[2]);
        Assert.Equal(0xDEF0, samples[3]);
    }

    [Fact]
    public void Short_payload_throws_bad_format()
    {
        var dst = new byte[16];
        var buf = PixelBuffer.Interleaved(new DngRect(0, 0, 4, 4), 1, PixelType.UInt8, dst);
        Assert.Throws<DngException>(() =>
            new UncompressedDecoder().Decode(new byte[8], buf, bigEndian: false));
    }
}

public class DeflateDecoderTests
{
    [Fact]
    public void Zlib_inflate_round_trips()
    {
        // Build a deflated payload from known bytes (zlib framing).
        byte[] original = new byte[256];
        for (int i = 0; i < 256; i++) original[i] = (byte)(i ^ 0x55);

        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            zip.Write(original);
        }
        var compressed = ms.ToArray();

        var dst = new byte[256];
        var buf = PixelBuffer.Interleaved(new DngRect(0, 0, 16, 16), 1, PixelType.UInt8, dst);
        new DeflateDecoder().Decode(compressed, buf, bigEndian: false);

        Assert.Equal(original, dst);
    }

    [Fact]
    public void Empty_payload_throws_bad_format()
    {
        var buf = PixelBuffer.Interleaved(new DngRect(0, 0, 4, 4), 1, PixelType.UInt8, new byte[16]);
        Assert.Throws<DngException>(() => new DeflateDecoder().Decode([], buf, bigEndian: false));
    }
}

public class CodecRegistryTests
{
    [Fact]
    public void Default_registry_has_uncompressed_deflate_lossless_jpeg()
    {
        Assert.True(CodecRegistry.Default.HasDecoder(Compression.Uncompressed));
        Assert.True(CodecRegistry.Default.HasDecoder(Compression.Deflate));
        Assert.True(CodecRegistry.Default.HasDecoder(Compression.Jpeg));
    }

    [Fact]
    public void Default_registry_lacks_jxl_unless_host_registers()
    {
        // DngSharp.Dng.Sdk doesn't depend on DngSharp.Dng.Sdk.Jxl — JXL is opt-in by the host.
        Assert.False(CodecRegistry.Default.HasDecoder(Compression.Jxl));
    }

    [Fact]
    public void Unknown_compression_throws_not_yet_implemented()
    {
        var reg = new CodecRegistry();
        var ex = Assert.Throws<DngException>(() => reg.GetDecoder(Compression.Lzw));
        Assert.Equal(DngError.NotYetImplemented, ex.ErrorCode);
    }

    [Fact]
    public void Register_replaces_existing_decoder()
    {
        var reg = new CodecRegistry();
        reg.Register(new UncompressedDecoder());
        var first = reg.GetDecoder(Compression.Uncompressed);
        var replacement = new UncompressedDecoder();
        reg.Register(replacement);
        Assert.Same(replacement, reg.GetDecoder(Compression.Uncompressed));
        Assert.NotSame(first, reg.GetDecoder(Compression.Uncompressed));
    }
}
