using System.Buffers.Binary;
using Dng.Sdk.Imaging;
using Dng.Sdk.IO;
using Dng.Sdk.Pixels;
using Dng.Sdk.Tiff;
using Dng.Sdk.Writer;

namespace Dng.Sdk.Pipeline;

/// <summary>
/// Writes a Stage-1/2/3 <see cref="SimpleImage"/> as an uncompressed
/// little-endian TIFF file. Mirrors the output format of
/// <c>dng_validate -1 / -2 / -3</c>.
///
/// <para>Supported pixel types:
/// <list type="bullet">
///   <item><see cref="PixelType.UInt8"/>  → TIFF BYTE  (8-bit, SampleFormat=1)</item>
///   <item><see cref="PixelType.UInt16"/> → TIFF SHORT (16-bit, SampleFormat=1)</item>
///   <item><see cref="PixelType.Float32"/>→ TIFF FLOAT (32-bit, SampleFormat=3)</item>
/// </list>
/// Always produces little-endian (II) TIFF.</para>
/// </summary>
public static class StageImageWriter
{
    /// <summary>Write <paramref name="image"/> to <paramref name="path"/>.</summary>
    public static void Write(SimpleImage image, string path)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(path);

        using var stream = DngFileStream.Create(path);
        Write(image, stream);
    }

    /// <summary>Write <paramref name="image"/> to <paramref name="stream"/>.</summary>
    public static void Write(SimpleImage image, DngStream stream)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(stream);

        // Float16 is a non-standard TIFF type that most viewers reject.
        // Convert to Float32 so the output is a valid, widely-readable TIFF.
        if (image.PixelType == PixelType.Float16)
        {
            image = ConvertFloat16ToFloat32(image);
        }

        const bool be = false; // always little-endian

        uint w = image.Bounds.W, h = image.Bounds.H, planes = image.Planes;

        (ushort bps, ushort fmt) = image.PixelType switch
        {
            PixelType.UInt8   => ((ushort) 8, (ushort)1), // UnsignedInteger
            PixelType.SInt8   => ((ushort) 8, (ushort)2), // SignedInteger
            PixelType.UInt16  => ((ushort)16, (ushort)1),
            PixelType.SInt16  => ((ushort)16, (ushort)2),
            PixelType.UInt32  => ((ushort)32, (ushort)1),
            PixelType.Float32 => ((ushort)32, (ushort)3), // FloatingPoint
            _ => throw new NotSupportedException(
                     $"StageImageWriter: unsupported pixel type {image.PixelType}"),
        };

        byte[] pixelBytes = image.GetTile(image.Bounds).Memory.ToArray();

        // ── Build the IFD ────────────────────────────────────────────────────

        var ifd = new TiffIfdToWrite();

        ifd.Entries.Add(TagBuilder.UInt32(DngTagCode.ImageWidth,  w, be));
        ifd.Entries.Add(TagBuilder.UInt32(DngTagCode.ImageLength, h, be));
        ifd.Entries.Add(UInt16Array(DngTagCode.BitsPerSample, Repeat(bps, planes), be));
        ifd.Entries.Add(TagBuilder.UInt16(DngTagCode.Compression, (ushort)Compression.Uncompressed, be));
        ifd.Entries.Add(TagBuilder.UInt16(DngTagCode.PhotometricInterpretation,
            (ushort)(planes == 1 ? Photometric.BlackIsZero : Photometric.Rgb), be));
        ifd.Entries.Add(TagBuilder.UInt16(DngTagCode.SamplesPerPixel, (ushort)planes, be));
        ifd.Entries.Add(TagBuilder.UInt32(DngTagCode.RowsPerStrip, h, be));
        ifd.Entries.Add(TagBuilder.UInt32(DngTagCode.StripByteCounts, (uint)pixelBytes.Length, be));
        ifd.Entries.Add(UInt16Array(DngTagCode.SampleFormat, Repeat(fmt, planes), be));
        ifd.Entries.Add(TagBuilder.UInt16(DngTagCode.PlanarConfiguration, 1, be));

        // StripOffsets: placeholder in the IFD; the DeferredBlob patches it
        // once TiffWriter knows the blob's absolute file position.
        long stripOffsetSlot = 0;
        ifd.Entries.Add(new TiffEntryToWrite
        {
            Tag     = DngTagCode.StripOffsets,
            Type    = TiffDataType.Long,
            Count   = 1,
            Payload = new byte[4], // placeholder
            // TiffWriter calls this with the position of the 4-byte slot in the stream.
            OffsetSlotCallback = slotPos => { stripOffsetSlot = slotPos; },
        });

        // DeferredBlob: the actual pixel data. TiffWriter writes it after all IFDs,
        // then calls each OffsetWriter with the blob's absolute file position so we
        // can patch the IFD's StripOffsets slot.
        var pixelBlob = new DeferredBlob { Bytes = pixelBytes };
        pixelBlob.OffsetWriters.Add(blobPos =>
        {
            // Patch the 4-byte StripOffsets slot with the blob's file position.
            long savedPos = stream.Position;
            stream.Position = stripOffsetSlot;
            var buf = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(buf, (uint)blobPos);
            stream.Write(buf);
            stream.Position = savedPos;
        });
        ifd.Blobs.Add(pixelBlob);

        new TiffWriter(be).Write(stream, [ifd]);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Convert a Float16 image to Float32 so it can be written as a standard TIFF.
    /// TIFF type 11 (FLOAT) is universally supported; half-float (type 19) is not.
    /// </summary>
    private static SimpleImage ConvertFloat16ToFloat32(SimpleImage src)
    {
        var dst = new SimpleImage(src.Bounds, src.Planes, PixelType.Float32);
        var srcTile = src.GetTile(src.Bounds);
        var dstTile = dst.GetTile(dst.Bounds);
        var srcBytes = srcTile.Memory.Span;
        var dstBytes = dstTile.Memory.Span;

        int total = (int)(src.Bounds.W * src.Bounds.H * src.Planes);
        for (int i = 0; i < total; i++)
        {
            Half h = BinaryPrimitives.ReadHalfLittleEndian(srcBytes.Slice(i * 2, 2));
            BinaryPrimitives.WriteSingleLittleEndian(dstBytes.Slice(i * 4, 4), (float)h);
        }
        dst.WriteTile(dstTile);
        return dst;
    }

    private static ushort[] Repeat(ushort v, uint n)
    {
        var a = new ushort[n];
        System.Array.Fill(a, v);
        return a;
    }

    private static TiffEntryToWrite UInt16Array(DngTagCode tag, ushort[] values, bool be)
    {
        var bytes = new byte[values.Length * 2];
        for (int i = 0; i < values.Length; i++)
        {
            if (be) BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(i * 2), values[i]);
            else    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), values[i]);
        }
        return new()
        {
            Tag     = tag,
            Type    = TiffDataType.Short,
            Count   = (uint)values.Length,
            Payload = bytes,
        };
    }
}
