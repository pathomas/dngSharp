using DngSharp.Dng.Sdk.Errors;
using DngSharp.Dng.Sdk.IO;

namespace DngSharp.Dng.Sdk.Container;

/// <summary>
/// Parsed TIFF/BigTIFF container header. Mirrors the first 8 (TIFF) or 16
/// (BigTIFF) bytes of a DNG file.
/// </summary>
public readonly record struct TiffHeader(
    bool BigEndian,
    bool BigTiff,
    long FirstIfdOffset)
{
    public const ushort MagicTiff = 42;
    public const ushort MagicBigTiff = 43;

    /// <summary>
    /// Read the TIFF header from the start of <paramref name="stream"/>. Side-
    /// effect: leaves <paramref name="stream"/>'s endianness flipped to match
    /// the file and its position at the start of the first IFD.
    /// </summary>
    public static TiffHeader Parse(DngStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        stream.Position = 0;
        // First two bytes are the byte-order mark. II = little-endian, MM = big.
        byte b0 = stream.ReadUInt8();
        byte b1 = stream.ReadUInt8();

        bool bigEndian = (b0, b1) switch
        {
            ((byte)'I', (byte)'I') => false,
            ((byte)'M', (byte)'M') => true,
            _ => throw new DngException(DngError.BadFormat, $"Not a TIFF file (BOM = 0x{b0:X2}{b1:X2})"),
        };
        stream.SetBigEndian(bigEndian);

        ushort magic = stream.ReadUInt16();
        long firstIfd;
        bool bigTiff;
        switch (magic)
        {
            case MagicTiff:
                bigTiff = false;
                firstIfd = stream.ReadUInt32();
                break;

            case MagicBigTiff:
                bigTiff = true;
                ushort offsetSize = stream.ReadUInt16();
                ushort constant = stream.ReadUInt16();
                if (offsetSize != 8 || constant != 0)
                    throw new DngException(DngError.BadFormat,
                        $"Invalid BigTIFF header: offsetSize={offsetSize}, constant={constant}");
                firstIfd = (long)stream.ReadUInt64();
                break;

            default:
                throw new DngException(DngError.BadFormat, $"Unknown TIFF magic 0x{magic:X4}");
        }

        if (firstIfd <= 0 || firstIfd > stream.Length)
            throw new DngException(DngError.BadFormat, $"First IFD offset out of range: {firstIfd}");

        return new TiffHeader(bigEndian, bigTiff, firstIfd);
    }
}
