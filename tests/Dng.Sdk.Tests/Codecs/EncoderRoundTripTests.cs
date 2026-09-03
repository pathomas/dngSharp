using Dng.Sdk.Codecs;
using Dng.Sdk.Pixels;
using Dng.Sdk.Primitives;
using Dng.Sdk.Tiff;

namespace Dng.Sdk.Tests.Codecs;

public class EncoderRoundTripTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Uncompressed_uint16_round_trips_with_decoder(bool bigEndian)
    {
        // Source: 4x4 uint16 with a recognizable ramp.
        var srcBytes = new byte[32];
        var src = PixelBuffer.Interleaved(new DngRect(0, 0, 4, 4), 1, PixelType.UInt16, srcBytes);
        var samples = src.AsTypedSpan<ushort>();
        for (int i = 0; i < 16; i++) samples[i] = (ushort)(i * 0x1111);

        var encoded = new UncompressedEncoder().Encode(src, bigEndian);

        // Decode into a fresh buffer and verify equality.
        var dstBytes = new byte[32];
        var dst = PixelBuffer.Interleaved(new DngRect(0, 0, 4, 4), 1, PixelType.UInt16, dstBytes);
        new UncompressedDecoder().Decode(encoded, dst, bigEndian);

        var dstSamples = dst.AsTypedSpan<ushort>();
        for (int i = 0; i < 16; i++) Assert.Equal(samples[i], dstSamples[i]);
    }

    [Fact]
    public void Deflate_round_trips()
    {
        // Use a compressible pattern (long run of identical bytes) so the
        // round trip exercises both inflate and a meaningful deflate ratio.
        var srcBytes = new byte[256];
        Array.Fill(srcBytes, (byte)0x42);
        var src = PixelBuffer.Interleaved(new DngRect(0, 0, 16, 16), 1, PixelType.UInt8, srcBytes);

        var encoded = new DeflateEncoder().Encode(src, bigEndian: false);
        // Identical-byte payload compresses dramatically.
        Assert.True(encoded.Length < srcBytes.Length, $"Compressed {encoded.Length} not < raw {srcBytes.Length}");

        var dstBytes = new byte[256];
        var dst = PixelBuffer.Interleaved(new DngRect(0, 0, 16, 16), 1, PixelType.UInt8, dstBytes);
        new DeflateDecoder().Decode(encoded, dst, bigEndian: false);
        Assert.Equal(srcBytes, dstBytes);
    }
}
