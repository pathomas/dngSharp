using System.Buffers.Binary;
using DngSharp.Dng.Sdk.Container;
using DngSharp.Dng.Sdk.IO;
using DngSharp.Dng.Sdk.Primitives;
using DngSharp.Dng.Sdk.Tiff;

namespace DngSharp.Dng.Sdk.Metadata.Exif;

/// <summary>
/// Populates a <see cref="DngExif"/> from a parsed EXIF/main IFD. Covers the
/// common tag set declared on <see cref="DngExif"/>; unknown tags are
/// retained verbatim in <see cref="DngExif.UnknownTags"/> so a host can do
/// custom processing without re-parsing the file.
/// </summary>
public static class ExifReader
{
    /// <summary>
    /// Read every entry in <paramref name="ifd"/> into
    /// <paramref name="exif"/>. <paramref name="stream"/> is used for
    /// out-of-line payloads; <paramref name="bigEndian"/> matches the host
    /// TIFF byte order.
    /// </summary>
    public static void Read(DngStream stream, TiffIfd ifd, bool bigEndian, DngExif exif)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(ifd);
        ArgumentNullException.ThrowIfNull(exif);

        foreach (var entry in ifd.Entries)
        {
            switch (entry.Tag)
            {
                // --- Identification ---
                case DngTagCode.Make: exif.Make = ReadAscii(stream, entry); break;
                case DngTagCode.Model: exif.Model = ReadAscii(stream, entry); break;
                case DngTagCode.Software: exif.Software = ReadAscii(stream, entry); break;
                case DngTagCode.Artist: exif.Artist = ReadAscii(stream, entry); break;
                case DngTagCode.Copyright: exif.Copyright = ReadAscii(stream, entry); break;
                case DngTagCode.ImageDescription: exif.ImageDescription = ReadAscii(stream, entry); break;
                case DngTagCode.CameraSerialNumber: exif.CameraSerialNumber = ReadAscii(stream, entry); break;

                // --- Lens ---
                case DngTagCode.LensMakeExif: exif.LensMake = ReadAscii(stream, entry); break;
                case DngTagCode.LensModelExif: exif.LensModel = ReadAscii(stream, entry); break;
                case DngTagCode.LensSerialNumberExif: exif.LensSerialNumber = ReadAscii(stream, entry); break;
                case DngTagCode.LensInfo:
                    {
                        var rats = ReadURationalArray(stream, entry, bigEndian);
                        if (rats.Length == 4)
                        {
                            exif.LensInfoMinFocal = rats[0];
                            exif.LensInfoMaxFocal = rats[1];
                            exif.LensInfoMinApertureMinFocal = rats[2];
                            exif.LensInfoMinApertureMaxFocal = rats[3];
                        }
                        break;
                    }

                // --- Exposure ---
                case DngTagCode.ExposureTime: exif.ExposureTime = ReadURationalScalar(stream, entry, bigEndian); break;
                case DngTagCode.FNumber: exif.FNumber = ReadURationalScalar(stream, entry, bigEndian); break;
                case DngTagCode.ExposureBiasValue: exif.ExposureBias = ReadSRationalScalar(stream, entry, bigEndian); break;
                case DngTagCode.FocalLength: exif.FocalLength = ReadURationalScalar(stream, entry, bigEndian); break;
                case DngTagCode.FocalLengthIn35mmFilm: exif.FocalLengthIn35mmFilm = entry.GetScalarUInt(bigEndian); break;
                case DngTagCode.ISOSpeedRatings: exif.IsoSpeedRating = entry.GetScalarUInt(bigEndian); break;
                case DngTagCode.Flash: exif.Flash = entry.GetScalarUInt(bigEndian); break;
                case DngTagCode.MeteringMode: exif.MeteringMode = entry.GetScalarUInt(bigEndian); break;
                case DngTagCode.ExposureProgram: exif.ExposureProgram = entry.GetScalarUInt(bigEndian); break;
                case DngTagCode.LightSource: exif.LightSource = entry.GetScalarUInt(bigEndian); break;
                case DngTagCode.WhiteBalance: exif.WhiteBalance = entry.GetScalarUInt(bigEndian); break;

                // --- Time ---
                case DngTagCode.DateTime: TryParseExif(ReadAscii(stream, entry), v => exif.DateTime = v); break;
                case DngTagCode.DateTimeOriginal: TryParseExif(ReadAscii(stream, entry), v => exif.DateTimeOriginal = v); break;
                case DngTagCode.DateTimeDigitized: TryParseExif(ReadAscii(stream, entry), v => exif.DateTimeDigitized = v); break;
                case DngTagCode.OffsetTime: exif.OffsetTime = ReadAscii(stream, entry); break;
                case DngTagCode.OffsetTimeOriginal: exif.OffsetTimeOriginal = ReadAscii(stream, entry); break;
                case DngTagCode.OffsetTimeDigitized: exif.OffsetTimeDigitized = ReadAscii(stream, entry); break;

                // --- Color ---
                case DngTagCode.ColorSpace: exif.ColorSpace = entry.GetScalarUInt(bigEndian); break;
                case DngTagCode.ExifVersion:
                    {
                        var bytes = ReadAllBytes(stream, entry);
                        if (bytes.Length == 4)
                            exif.ExifVersion = BinaryPrimitives.ReadUInt32BigEndian(bytes.Span);
                        break;
                    }

                // --- Anything else: stash for host inspection ---
                default:
                    exif.UnknownTags[(uint)entry.Tag] = ReadAllBytes(stream, entry);
                    break;
            }
        }
    }

    private static string ReadAscii(DngStream stream, TiffIfdEntry entry)
    {
        if (entry.Type is not (TiffDataType.Ascii or TiffDataType.Byte)) return string.Empty;
        var bytes = ReadAllBytes(stream, entry);
        // EXIF strings are NUL-terminated; drop trailing zero(s).
        int len = bytes.Length;
        while (len > 0 && bytes.Span[len - 1] == 0) len--;
        return System.Text.Encoding.UTF8.GetString(bytes.Span[..len]);
    }

    private static DngURational ReadURationalScalar(DngStream stream, TiffIfdEntry entry, bool bigEndian)
    {
        var arr = ReadURationalArray(stream, entry, bigEndian);
        return arr.Length > 0 ? arr[0] : default;
    }

    private static DngSRational ReadSRationalScalar(DngStream stream, TiffIfdEntry entry, bool bigEndian)
    {
        var arr = ReadSRationalArray(stream, entry, bigEndian);
        return arr.Length > 0 ? arr[0] : default;
    }

    private static DngURational[] ReadURationalArray(DngStream stream, TiffIfdEntry entry, bool bigEndian)
    {
        if (entry.Type != TiffDataType.Rational) return [];
        var bytes = ReadAllBytes(stream, entry);
        int n = bytes.Length / 8;
        var result = new DngURational[n];
        var s = bytes.Span;
        for (int i = 0; i < n; i++)
        {
            uint num = bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(s[(i * 8)..]) : BinaryPrimitives.ReadUInt32LittleEndian(s[(i * 8)..]);
            uint den = bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(s[(i * 8 + 4)..]) : BinaryPrimitives.ReadUInt32LittleEndian(s[(i * 8 + 4)..]);
            result[i] = new DngURational(num, den);
        }
        return result;
    }

    private static DngSRational[] ReadSRationalArray(DngStream stream, TiffIfdEntry entry, bool bigEndian)
    {
        if (entry.Type != TiffDataType.SRational) return [];
        var bytes = ReadAllBytes(stream, entry);
        int n = bytes.Length / 8;
        var result = new DngSRational[n];
        var s = bytes.Span;
        for (int i = 0; i < n; i++)
        {
            int num = bigEndian ? BinaryPrimitives.ReadInt32BigEndian(s[(i * 8)..]) : BinaryPrimitives.ReadInt32LittleEndian(s[(i * 8)..]);
            int den = bigEndian ? BinaryPrimitives.ReadInt32BigEndian(s[(i * 8 + 4)..]) : BinaryPrimitives.ReadInt32LittleEndian(s[(i * 8 + 4)..]);
            result[i] = new DngSRational(num, den);
        }
        return result;
    }

    private static ReadOnlyMemory<byte> ReadAllBytes(DngStream stream, TiffIfdEntry entry)
    {
        if (entry.IsInline)
        {
            // Inline values may be padded — return only the payload bytes.
            int len = (int)System.Math.Min(entry.PayloadSize, (ulong)entry.InlineValue.Length);
            return entry.InlineValue[..len];
        }

        if (entry.PayloadSize > int.MaxValue)
            // Defer >2 GiB out-of-line metadata payloads — well beyond any sane EXIF tag.
            return ReadOnlyMemory<byte>.Empty;

        var buf = new byte[(int)entry.PayloadSize];
        long savedPos = stream.Position;
        try
        {
            stream.Position = entry.ValueOffset;
            stream.ReadExactly(buf);
        }
        finally
        {
            stream.Position = savedPos;
        }
        return buf;
    }

    private static void TryParseExif(string s, Action<DngDateTime> sink)
    {
        var dt = default(DngDateTime);
        if (dt.TryParseExif(s)) sink(dt);
    }
}
