using System.IO.Compression;
using DngSharp.Dng.Sdk.Errors;
using DngSharp.Dng.Sdk.Pixels;
using DngSharp.Dng.Sdk.Tiff;

namespace DngSharp.Dng.Sdk.Codecs;

/// <summary>
/// Decoder for <see cref="Compression.Deflate"/> (TIFF zlib, DNG 1.4+).
/// Inflates the payload through <see cref="ZLibStream"/> then dispatches to
/// <see cref="UncompressedDecoder"/> to handle endian/layout.
/// </summary>
public sealed class DeflateDecoder : IRawDecoder
{
    private static readonly UncompressedDecoder Inner = new();

    public Compression Compression => Compression.Deflate;

    public void Decode(ReadOnlySpan<byte> compressed, PixelBuffer destination, bool bigEndian)
    {
        if (compressed.IsEmpty)
            throw new DngException(DngError.BadFormat, "Deflate: empty payload");

        long expected = (long)destination.Area.W * destination.Area.H * destination.Planes * destination.PixelSize;
        if (expected > int.MaxValue)
            throw new DngException(DngError.Overflow, $"Deflate: output size {expected} > int.MaxValue");

        // ZLibStream rather than DeflateStream — TIFF Deflate uses zlib framing
        // (RFC 1950) with the 2-byte CMF/FLG header, not raw RFC 1951.
        var scratch = new byte[(int)expected];
        using var src = new MemoryStream(compressed.ToArray(), writable: false);
        using var inflater = new ZLibStream(src, CompressionMode.Decompress);
        int written = 0;
        while (written < scratch.Length)
        {
            int n = inflater.Read(scratch, written, scratch.Length - written);
            if (n <= 0)
                throw new DngException(DngError.BadFormat,
                    $"Deflate: unexpected EOF (got {written} of {scratch.Length} bytes)");
            written += n;
        }

        Inner.Decode(scratch, destination, bigEndian);
    }
}
