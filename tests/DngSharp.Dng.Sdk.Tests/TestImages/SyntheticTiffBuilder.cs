using DngSharp.Dng.Sdk.Codecs;
using DngSharp.Dng.Sdk.Pixels;
using DngSharp.Dng.Sdk.Primitives;
using DngSharp.Dng.Sdk.Tiff;
using DngSharp.Dng.Sdk.Writer;

namespace DngSharp.Dng.Sdk.Tests.TestImages;

/// <summary>
/// Builds small, fully-synthetic, analytically-known plain-TIFF fixtures —
/// ordinary <c>PhotometricInterpretation=RGB</c> TIFFs (not DNGs) meant to be
/// fed through an external TIFF-to-DNG conversion step, so the resulting DNG
/// can be rendered and compared against a directly-authored DNG built from
/// the same pixel data (see <see cref="SyntheticDngBuilder"/>).
///
/// <para>These use only baseline TIFF 6.0 tags (no DNG-specific tags) so any
/// standards-compliant converter can ingest them: <c>ImageWidth</c>/
/// <c>ImageLength</c>, <c>BitsPerSample</c> (one value per sample),
/// <c>Compression=Uncompressed</c>, <c>PhotometricInterpretation=RGB</c>,
/// <c>SamplesPerPixel</c>, <c>PlanarConfiguration=1</c> (chunky/interleaved),
/// a single strip, and <c>XResolution</c>/<c>YResolution</c>/
/// <c>ResolutionUnit</c>/<c>Orientation</c> for broad compatibility.</para>
/// </summary>
public static class SyntheticTiffBuilder
{
    // Baseline TIFF tags with no DNG-specific meaning — not present in
    // DngTagCode, so referenced here by their raw TIFF tag numbers.
    private const DngTagCode TagOrientation = (DngTagCode)0x0112;
    private const DngTagCode TagXResolution = (DngTagCode)0x011A;
    private const DngTagCode TagYResolution = (DngTagCode)0x011B;
    private const DngTagCode TagResolutionUnit = (DngTagCode)0x0128;

    /// <summary>
    /// Builds a <paramref name="width"/>×<paramref name="height"/> plain RGB
    /// TIFF whose value is a horizontal ramp: column 0 = 0, column
    /// <paramref name="width"/>-1 = <paramref name="maxValue"/>, linear in
    /// between, identical in every row and replicated across all 3 channels
    /// (R=G=B) — the same pixel data as
    /// <see cref="SyntheticDngBuilder.BuildGradientLeftToRightDng"/>, so a
    /// DNG produced by converting this TIFF can be directly compared against
    /// the DNG built straight from the DNG builder.
    /// </summary>
    public static byte[] BuildGradientLeftToRightTiff(
        int width,
        int height,
        ushort maxValue = 65535,
        bool bigEndian = false)
    {
        var pixels = SyntheticPixelPatterns.GradientLeftToRight(width, height, maxValue);
        return BuildRgbTiff(pixels, width, height, bigEndian);
    }

    /// <summary>
    /// Plain-TIFF counterpart to
    /// <see cref="SyntheticDngBuilder.BuildCenteredCircleDng"/> — identical
    /// pixel data, wrapped as a baseline RGB TIFF instead of a DNG.
    /// </summary>
    public static byte[] BuildCenteredCircleTiff(
        int size,
        double diameterFraction = 0.5,
        ushort maxValue = 65535,
        bool bigEndian = false)
    {
        var pixels = SyntheticPixelPatterns.CenteredCircle(size, diameterFraction, maxValue);
        return BuildRgbTiff(pixels, size, size, bigEndian);
    }

    /// <summary>
    /// Wraps a pre-computed interleaved UInt16 RGB pixel buffer (row-major,
    /// 3 samples per pixel) as a minimal valid uncompressed baseline TIFF.
    /// </summary>
    public static byte[] BuildRgbTiff(
        ushort[] interleavedPixels,
        int width,
        int height,
        bool bigEndian = false)
    {
        const uint planes = 3;
        var bounds = new DngRect(0, 0, height, width);
        var srcBytes = new byte[interleavedPixels.Length * 2];
        Buffer.BlockCopy(interleavedPixels, 0, srcBytes, 0, srcBytes.Length);
        var src = PixelBuffer.Interleaved(bounds, planes, PixelType.UInt16, srcBytes);

        var encoded = new UncompressedEncoder().Encode(src, bigEndian);

        var ifd = new TiffIfdToWrite();
        var stripBlob = new DeferredBlob { Bytes = encoded };
        ifd.Blobs.Add(stripBlob);

        ifd.Entries.Add(TagBuilder.UInt32(DngTagCode.ImageWidth, (uint)width, bigEndian));
        ifd.Entries.Add(TagBuilder.UInt32(DngTagCode.ImageLength, (uint)height, bigEndian));
        ifd.Entries.Add(TagBuilder.UInt16Array(DngTagCode.BitsPerSample, [16, 16, 16], bigEndian));
        ifd.Entries.Add(TagBuilder.UInt16(DngTagCode.Compression, (ushort)Compression.Uncompressed, bigEndian));
        ifd.Entries.Add(TagBuilder.UInt16(DngTagCode.PhotometricInterpretation,
            (ushort)Photometric.Rgb, bigEndian));
        ifd.Entries.Add(TagBuilder.UInt16(TagOrientation, 1, bigEndian)); // top-left
        ifd.Entries.Add(TagBuilder.UInt16(DngTagCode.SamplesPerPixel, (ushort)planes, bigEndian));
        ifd.Entries.Add(TagBuilder.UInt32(DngTagCode.RowsPerStrip, (uint)height, bigEndian));
        ifd.Entries.Add(TagBuilder.UInt32(DngTagCode.StripByteCounts, (uint)encoded.Length, bigEndian));
        ifd.Entries.Add(TagBuilder.URational(TagXResolution, new DngURational(72, 1), bigEndian));
        ifd.Entries.Add(TagBuilder.URational(TagYResolution, new DngURational(72, 1), bigEndian));
        ifd.Entries.Add(TagBuilder.UInt16(TagResolutionUnit, 2, bigEndian)); // inches
        ifd.Entries.Add(TagBuilder.UInt16(DngTagCode.PlanarConfiguration, 1, bigEndian)); // chunky/interleaved

        var stripOffsetEntry = new TiffEntryToWrite
        {
            Tag = DngTagCode.StripOffsets,
            Type = TiffDataType.Long,
            Count = 1,
            Payload = new byte[4],
            OffsetSlotCallback = slotPos =>
                stripBlob.OffsetWriters.Add(absoluteOffset =>
                    Patches.Add((slotPos, (uint)absoluteOffset, bigEndian))),
        };
        ifd.Entries.Add(stripOffsetEntry);

        var memory = new MemoryStream();
        using (var ds = new DngSharp.Dng.Sdk.IO.DngStream(memory, bigEndian, leaveOpen: true))
        {
            new TiffWriter(bigEndian).Write(ds, [ifd]);
            foreach (var (pos, val, be) in Patches)
            {
                long saved = ds.Position;
                ds.Position = pos;
                ds.SetBigEndian(be);
                ds.WriteUInt32(val);
                ds.Position = saved;
            }
            Patches.Clear();
        }

        return memory.ToArray();
    }

    // TiffWriter's deferred-blob offset patching happens after Write()
    // returns (mirrors the pattern in SyntheticDngBuilder /
    // TiffWriterRoundTripTests); collected here and applied immediately
    // after each Write() call above.
    private static readonly List<(long Pos, uint Value, bool BigEndian)> Patches = [];
}
