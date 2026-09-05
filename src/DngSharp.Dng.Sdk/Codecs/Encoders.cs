using System.Buffers.Binary;
using System.IO.Compression;
using DngSharp.Dng.Sdk.Errors;
using DngSharp.Dng.Sdk.Pixels;
using DngSharp.Dng.Sdk.Tiff;

namespace DngSharp.Dng.Sdk.Codecs;

/// <summary>
/// Writes a tightly-packed interleaved buffer as
/// <see cref="Compression.Uncompressed"/> bytes. Mirrors the reader's layout
/// so that <see cref="UncompressedDecoder"/>(encoder.Encode(buf)) == buf.
/// </summary>
public sealed class UncompressedEncoder : IRawEncoder
{
    public Compression Compression => Compression.Uncompressed;

    public byte[] Encode(PixelBuffer source, bool bigEndian)
    {
        long total = (long)source.Area.W * source.Area.H * source.Planes * source.PixelSize;
        if (total > int.MaxValue)
            throw new DngException(DngError.Overflow, $"Uncompressed: payload {total} > int.MaxValue");

        var output = new byte[(int)total];
        var src = source.Memory.Span;

        int rowBytes = (int)(source.Area.W * source.Planes * source.PixelSize);
        for (int row = 0; row < source.Area.H; row++)
        {
            long srcOff = source.OffsetBytes(source.Area.T + row, source.Area.L);
            var srcRow = src.Slice((int)srcOff, rowBytes);
            var dstRow = output.AsSpan(row * rowBytes, rowBytes);
            srcRow.CopyTo(dstRow);

            if (bigEndian && source.PixelSize > 1)
                SwapInPlace(dstRow, source.PixelSize);
        }
        return output;
    }

    private static void SwapInPlace(Span<byte> bytes, int sampleSize)
    {
        switch (sampleSize)
        {
            case 2:
                for (int i = 0; i + 2 <= bytes.Length; i += 2)
                {
                    ushort v = BinaryPrimitives.ReadUInt16LittleEndian(bytes[i..]);
                    BinaryPrimitives.WriteUInt16BigEndian(bytes[i..], v);
                }
                break;
            case 4:
                for (int i = 0; i + 4 <= bytes.Length; i += 4)
                {
                    uint v = BinaryPrimitives.ReadUInt32LittleEndian(bytes[i..]);
                    BinaryPrimitives.WriteUInt32BigEndian(bytes[i..], v);
                }
                break;
            case 8:
                for (int i = 0; i + 8 <= bytes.Length; i += 8)
                {
                    ulong v = BinaryPrimitives.ReadUInt64LittleEndian(bytes[i..]);
                    BinaryPrimitives.WriteUInt64BigEndian(bytes[i..], v);
                }
                break;
            default:
                DngThrow.ProgramError($"Uncompressed encoder: unsupported sample size {sampleSize}");
                break;
        }
    }
}

/// <summary>
/// Compresses a buffer with zlib (TIFF Deflate / DNG 1.4+ raw).
/// </summary>
public sealed class DeflateEncoder : IRawEncoder
{
    private static readonly UncompressedEncoder Inner = new();

    public Compression Compression => Compression.Deflate;

    public CompressionLevel Level { get; set; } = CompressionLevel.Optimal;

    public byte[] Encode(PixelBuffer source, bool bigEndian)
    {
        var uncompressed = Inner.Encode(source, bigEndian);
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, Level, leaveOpen: true))
        {
            z.Write(uncompressed);
        }
        return ms.ToArray();
    }
}
