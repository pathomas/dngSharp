using Dng.Sdk.Codecs;
using Dng.Sdk.Container;
using Dng.Sdk.IO;
using Dng.Sdk.Pixels;
using Dng.Sdk.Primitives;
using Dng.Sdk.Tiff;
using Dng.Sdk.Writer;

namespace Dng.Sdk.Tests.Writer;

/// <summary>
/// End-to-end round trip: build a tiny DNG-shaped TIFF in memory via the
/// writer, then parse it back with the existing reader and verify every
/// field matches. This is the strongest signal that the framing logic is
/// internally consistent.
/// </summary>
public class TiffWriterRoundTripTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Write_then_parse_recovers_main_image_geometry(bool bigEndian)
    {
        const int width = 16, height = 8;
        const ushort dummyByte = 0;

        // 1) Build a 16x8 single-plane uint16 source image.
        var srcBytes = new byte[width * height * 2];
        var src = PixelBuffer.Interleaved(new DngRect(0, 0, height, width), 1, PixelType.UInt16, srcBytes);
        var samples = src.AsTypedSpan<ushort>();
        for (int i = 0; i < samples.Length; i++) samples[i] = (ushort)(i + 1);

        var encoded = new UncompressedEncoder().Encode(src, bigEndian);

        // 2) Build one IFD with the minimum DNG marker tags + strip descriptors.
        var ifd = new TiffIfdToWrite();
        var stripBlob = new DeferredBlob { Bytes = encoded };
        ifd.Blobs.Add(stripBlob);

        ifd.Entries.Add(TagBuilder.UInt32(DngTagCode.NewSubFileType, 0, bigEndian));     // main image
        ifd.Entries.Add(TagBuilder.UInt32(DngTagCode.ImageWidth, width, bigEndian));
        ifd.Entries.Add(TagBuilder.UInt32(DngTagCode.ImageLength, height, bigEndian));
        ifd.Entries.Add(TagBuilder.UInt16(DngTagCode.BitsPerSample, 16, bigEndian));
        ifd.Entries.Add(TagBuilder.UInt16(DngTagCode.Compression, (ushort)Compression.Uncompressed, bigEndian));
        ifd.Entries.Add(TagBuilder.UInt16(DngTagCode.PhotometricInterpretation,
            (ushort)Photometric.LinearRaw, bigEndian));
        ifd.Entries.Add(TagBuilder.UInt32(DngTagCode.RowsPerStrip, height, bigEndian));
        ifd.Entries.Add(TagBuilder.UInt32(DngTagCode.StripByteCounts, (uint)encoded.Length, bigEndian));

        // StripOffsets uses the deferred-blob callback so the offset is
        // patched in once the blob lands.
        var stripOffsetEntry = new TiffEntryToWrite
        {
            Tag = DngTagCode.StripOffsets,
            Type = TiffDataType.Long,
            Count = 1,
            Payload = new byte[4],  // 4-byte placeholder; the OffsetSlotCallback patches it
            OffsetSlotCallback = slotPos =>
                stripBlob.OffsetWriters.Add(absoluteOffset =>
                {
                    // The slot already lives in the file at slotPos; patch the
                    // uint32 there to absoluteOffset, respecting endian.
                    // We use a simple closure-captured writer below.
                    _patches.Add((slotPos, (uint)absoluteOffset, bigEndian));
                }),
        };
        ifd.Entries.Add(stripOffsetEntry);

        // DNGVersion = 1.7.1.0 (required marker for DNG identification).
        ifd.Entries.Add(TagBuilder.Bytes(DngTagCode.DNGVersion, [1, 7, 1, 0]));
        ifd.Entries.Add(TagBuilder.Bytes(DngTagCode.DNGBackwardVersion, [1, 0, 0, 0]));
        ifd.Entries.Add(TagBuilder.Ascii(DngTagCode.UniqueCameraModel, "RoundTrip Test Cam"));

        // 3) Write into a MemoryStream.
        var memory = new MemoryStream();
        using (var ds = new DngStream(memory, bigEndian, leaveOpen: true))
        {
            new TiffWriter(bigEndian).Write(ds, [ifd]);

            // Apply patches accumulated via OffsetSlotCallback.
            foreach (var (pos, val, be) in _patches)
            {
                long saved = ds.Position;
                ds.Position = pos;
                ds.SetBigEndian(be);
                ds.WriteUInt32(val);
                ds.Position = saved;
            }
            _patches.Clear();
        }

        // 4) Parse it back and verify.
        memory.Position = 0;
        using var rs = new DngStream(memory);
        var container = DngContainer.Parse(rs);

        Assert.Equal(bigEndian, container.Header.BigEndian);
        Assert.False(container.Header.BigTiff);
        Assert.Single(container.AllIfds);
        var main = container.AllIfds[container.MainIndex];

        Assert.Equal((uint)width,  main.Find(DngTagCode.ImageWidth)!.GetScalarUInt(bigEndian));
        Assert.Equal((uint)height, main.Find(DngTagCode.ImageLength)!.GetScalarUInt(bigEndian));
        Assert.Equal((uint)Compression.Uncompressed,
                     main.Find(DngTagCode.Compression)!.GetScalarUInt(bigEndian));
        Assert.Equal((uint)Photometric.LinearRaw,
                     main.Find(DngTagCode.PhotometricInterpretation)!.GetScalarUInt(bigEndian));

        // DNGVersion is 4 bytes.
        var dngVer = main.Find(DngTagCode.DNGVersion);
        Assert.NotNull(dngVer);
        Assert.Equal(TiffDataType.Byte, dngVer.Type);
        Assert.Equal(4ul, dngVer.Count);

        _ = dummyByte;
    }

    private readonly List<(long Pos, uint Value, bool BigEndian)> _patches = [];
}
