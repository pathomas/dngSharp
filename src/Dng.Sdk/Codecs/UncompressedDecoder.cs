using System.Buffers.Binary;
using Dng.Sdk.Errors;
using Dng.Sdk.Pixels;
using Dng.Sdk.Tiff;

namespace Dng.Sdk.Codecs;

/// <summary>
/// "Decoder" for <see cref="Compression.Uncompressed"/> data. Copies bytes
/// from the source into the destination, byte-swapping multi-byte samples
/// when the host platform's endianness doesn't match the file's.
/// </summary>
public sealed class UncompressedDecoder : IRawDecoder
{
    public Compression Compression => Compression.Uncompressed;

    public void Decode(ReadOnlySpan<byte> compressed, PixelBuffer destination, bool bigEndian)
    {
        int sampleSize = destination.PixelSize;
        long expected = (long)destination.Area.W * destination.Area.H * destination.Planes * sampleSize;
        if (compressed.Length < expected)
            throw new DngException(DngError.BadFormat,
                $"Uncompressed: payload too small ({compressed.Length} < {expected})");

        // The source layout is interleaved row-major: row 0 then row 1 etc.,
        // each row carrying width × planes samples.
        int rowBytes = (int)(destination.Area.W * destination.Planes * sampleSize);
        var dst = destination.Memory.Span;

        for (int row = 0; row < destination.Area.H; row++)
        {
            long dstOff = destination.OffsetBytes(destination.Area.T + row, destination.Area.L);
            var srcRow = compressed.Slice(row * rowBytes, rowBytes);
            var dstRow = dst.Slice((int)dstOff, rowBytes);

            if (sampleSize == 1 || !bigEndian || BitConverter.IsLittleEndian == !bigEndian)
            {
                // Byte order matches host (or is irrelevant for byte samples).
                // For LE files on LE hosts this is a straight copy.
                if (!bigEndian)
                {
                    srcRow.CopyTo(dstRow);
                    continue;
                }
            }

            // Big-endian file on LE host: swap per sample.
            srcRow.CopyTo(dstRow);
            if (bigEndian && sampleSize > 1)
                SwapBytesInPlace(dstRow, sampleSize);
        }
    }

    private static void SwapBytesInPlace(Span<byte> bytes, int sampleSize)
    {
        switch (sampleSize)
        {
            case 2:
                {
                    for (int i = 0; i + 2 <= bytes.Length; i += 2)
                    {
                        ushort v = BinaryPrimitives.ReadUInt16BigEndian(bytes[i..]);
                        BinaryPrimitives.WriteUInt16LittleEndian(bytes[i..], v);
                    }
                    break;
                }
            case 4:
                {
                    for (int i = 0; i + 4 <= bytes.Length; i += 4)
                    {
                        uint v = BinaryPrimitives.ReadUInt32BigEndian(bytes[i..]);
                        BinaryPrimitives.WriteUInt32LittleEndian(bytes[i..], v);
                    }
                    break;
                }
            case 8:
                {
                    for (int i = 0; i + 8 <= bytes.Length; i += 8)
                    {
                        ulong v = BinaryPrimitives.ReadUInt64BigEndian(bytes[i..]);
                        BinaryPrimitives.WriteUInt64LittleEndian(bytes[i..], v);
                    }
                    break;
                }
            default:
                DngThrow.ProgramError($"Uncompressed: unsupported sample size {sampleSize}");
                break;
        }
    }
}
