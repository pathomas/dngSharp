using System.Buffers.Binary;
using DngSharp.Dng.Sdk.Codecs;
using DngSharp.Dng.Sdk.Pixels;
using DngSharp.Dng.Sdk.Primitives;

namespace DngSharp.Dng.Sdk.TestSupport;

/// <summary>
/// Hand-rolled minimal little-endian DNG writer that emits a single
/// LinearRaw IFD whose pixel data is split into <em>many</em> Deflate
/// (compression 8) strips or tiles. Used to exercise the parallel strip/tile
/// decode path in <c>StripReader</c> without depending on the vendored
/// sample files. Shared between the test and benchmark projects via a
/// linked <c>Compile</c> item.
/// </summary>
public static class MultiStripDngFactory
{
    private const int HeaderSize = 8;

    /// <summary>
    /// Build a <paramref name="width"/>×<paramref name="height"/> single-plane
    /// 16-bit LinearRaw DNG with one Deflate strip per <paramref name="rowsPerStrip"/>
    /// rows. Pixel values are a deterministic ramp seeded by row/col.
    /// </summary>
    public static byte[] BuildStripped(int width, int height, int rowsPerStrip)
    {
        var pixels = MakePixels(width, height);
        var encoder = new DeflateEncoder();

        int stripCount = (height + rowsPerStrip - 1) / rowsPerStrip;
        var blobs = new byte[stripCount][];
        for (int s = 0; s < stripCount; s++)
        {
            int top = s * rowsPerStrip;
            int h = System.Math.Min(rowsPerStrip, height - top);
            var area = new DngRect(0, 0, h, width);
            var bytes = new byte[width * h * 2];
            Buffer.BlockCopy(pixels, top * width * 2, bytes, 0, bytes.Length);
            blobs[s] = encoder.Encode(PixelBuffer.Interleaved(area, 1, PixelType.UInt16, bytes), bigEndian: false);
        }

        return Assemble(width, height, blobs, isTiled: false, tileW: 0, tileH: 0, rowsPerStrip: rowsPerStrip);
    }

    /// <summary>
    /// Build a tiled variant: <paramref name="tileW"/>×<paramref name="tileH"/>
    /// Deflate tiles covering the image (edge tiles padded to full size, per
    /// the TIFF spec).
    /// </summary>
    public static byte[] BuildTiled(int width, int height, int tileW, int tileH)
    {
        var pixels = MakePixels(width, height);
        var encoder = new DeflateEncoder();

        int across = (width + tileW - 1) / tileW;
        int down = (height + tileH - 1) / tileH;
        var blobs = new byte[across * down][];

        for (int ty = 0; ty < down; ty++)
        {
            for (int tx = 0; tx < across; tx++)
            {
                var bytes = new byte[tileW * tileH * 2];
                for (int r = 0; r < tileH; r++)
                {
                    int srcRow = ty * tileH + r;
                    if (srcRow >= height) break;
                    int srcCol = tx * tileW;
                    int cols = System.Math.Min(tileW, width - srcCol);
                    Buffer.BlockCopy(pixels, (srcRow * width + srcCol) * 2, bytes, r * tileW * 2, cols * 2);
                }
                var area = new DngRect(0, 0, tileH, tileW);
                blobs[ty * across + tx] = encoder.Encode(
                    PixelBuffer.Interleaved(area, 1, PixelType.UInt16, bytes), bigEndian: false);
            }
        }

        return Assemble(width, height, blobs, isTiled: true, tileW, tileH, rowsPerStrip: 0);
    }

    /// <summary>The expected decoded pixel value at (row, col) for images built by this factory.</summary>
    public static ushort ExpectedPixel(int row, int col) => (ushort)((row * 7 + col * 13) & 0x0FFF);

    private static byte[] MakePixels(int width, int height)
    {
        var bytes = new byte[width * height * 2];
        var span = bytes.AsSpan();
        for (int r = 0; r < height; r++)
            for (int c = 0; c < width; c++)
                BinaryPrimitives.WriteUInt16LittleEndian(span.Slice((r * width + c) * 2), ExpectedPixel(r, c));
        return bytes;
    }

    private static byte[] Assemble(int width, int height, byte[][] blobs, bool isTiled, int tileW, int tileH, int rowsPerStrip)
    {
        var entries = new List<(ushort Tag, ushort Type, uint Count, byte[] Payload)>
        {
            (254, 4, 1, U32(0)),                 // NewSubFileType
            (256, 4, 1, U32((uint)width)),
            (257, 4, 1, U32((uint)height)),
            (258, 3, 1, U16(16)),                // BitsPerSample
            (259, 3, 1, U16(8)),                 // Compression = Deflate
            (262, 3, 1, U16(34892)),             // Photometric = LinearRaw
            (277, 3, 1, U16(1)),                 // SamplesPerPixel
            (284, 3, 1, U16(1)),                 // PlanarConfiguration
            (50706, 1, 4, [1, 4, 0, 0]),         // DNGVersion
            (50707, 1, 4, [1, 4, 0, 0]),         // DNGBackwardVersion
        };

        int n = blobs.Length;
        var counts = new byte[n * 4];
        for (int i = 0; i < n; i++) BinaryPrimitives.WriteUInt32LittleEndian(counts.AsSpan(i * 4), (uint)blobs[i].Length);

        int offsetsTag, countsTag;
        if (isTiled)
        {
            entries.Add((322, 4, 1, U32((uint)tileW)));
            entries.Add((323, 4, 1, U32((uint)tileH)));
            offsetsTag = 324; countsTag = 325;
        }
        else
        {
            entries.Add((278, 4, 1, U32((uint)rowsPerStrip)));
            offsetsTag = 273; countsTag = 279;
        }
        entries.Add(((ushort)countsTag, 4, (uint)n, counts));
        entries.Add(((ushort)offsetsTag, 4, (uint)n, new byte[n * 4])); // patched below
        entries.Sort((a, b) => a.Tag.CompareTo(b.Tag));

        // Layout: header | IFD (2 + 12*E + 4) | out-of-line payloads | blobs.
        int ifdStart = HeaderSize;
        int ifdSize = 2 + 12 * entries.Count + 4;
        int cursor = ifdStart + ifdSize;

        var outOfLine = new Dictionary<int, int>();
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].Payload.Length > 4)
            {
                outOfLine[i] = cursor;
                cursor += entries[i].Payload.Length;
                if ((cursor & 1) != 0) cursor++;
            }
        }

        var blobOffsets = new uint[n];
        for (int i = 0; i < n; i++)
        {
            blobOffsets[i] = (uint)cursor;
            cursor += blobs[i].Length;
            if ((cursor & 1) != 0) cursor++;
        }

        int offsetsIdx = entries.FindIndex(e => e.Tag == offsetsTag);
        var offsetsPayload = entries[offsetsIdx].Payload;
        for (int i = 0; i < n; i++) BinaryPrimitives.WriteUInt32LittleEndian(offsetsPayload.AsSpan(i * 4), blobOffsets[i]);

        var file = new byte[cursor];
        var w = file.AsSpan();
        w[0] = (byte)'I'; w[1] = (byte)'I';
        BinaryPrimitives.WriteUInt16LittleEndian(w[2..], 42);
        BinaryPrimitives.WriteUInt32LittleEndian(w[4..], (uint)ifdStart);

        int p = ifdStart;
        BinaryPrimitives.WriteUInt16LittleEndian(w[p..], (ushort)entries.Count); p += 2;
        for (int i = 0; i < entries.Count; i++)
        {
            var (tag, type, count, payload) = entries[i];
            BinaryPrimitives.WriteUInt16LittleEndian(w[p..], tag);
            BinaryPrimitives.WriteUInt16LittleEndian(w[(p + 2)..], type);
            BinaryPrimitives.WriteUInt32LittleEndian(w[(p + 4)..], count);
            if (payload.Length > 4)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(w[(p + 8)..], (uint)outOfLine[i]);
                payload.CopyTo(w[outOfLine[i]..]);
            }
            else
            {
                payload.CopyTo(w[(p + 8)..]);
            }
            p += 12;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(w[p..], 0); // next IFD

        for (int i = 0; i < n; i++) blobs[i].CopyTo(w[(int)blobOffsets[i]..]);
        return file;
    }

    private static byte[] U16(ushort v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); return b; }
    private static byte[] U32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); return b; }
}
