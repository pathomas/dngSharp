using System.Buffers.Binary;
using Dng.Sdk.Codecs;
using Dng.Sdk.Container;
using Dng.Sdk.Errors;
using Dng.Sdk.Imaging;
using Dng.Sdk.Imaging.Profile;
using Dng.Sdk.Imaging.Raw;
using Dng.Sdk.IO;
using Dng.Sdk.Metadata;
using Dng.Sdk.Pixels;
using Dng.Sdk.Primitives;
using Dng.Sdk.Tasks;
using Dng.Sdk.Tiff;

namespace Dng.Sdk.Pipeline;

/// <summary>
/// Reads the main IFD's strip or tile data from a DNG stream, dispatches each
/// strip/tile through the codec registry, and assembles a <see cref="SimpleImage"/>
/// (Stage 1). Mirrors the strip-read body of <c>dng_read_image.cpp</c>.
///
/// <para>Layout rules handled:
/// <list type="bullet">
///   <item>Strip layout (<c>StripOffsets</c> / <c>StripByteCounts</c>) — single or multi-strip.</item>
///   <item>Tile layout (<c>TileOffsets</c> / <c>TileByteCounts</c> + <c>TileWidth</c> /
///         <c>TileLength</c>) — single or multi-tile.</item>
///   <item>Planar configuration: interleaved only (<c>PlanarConfiguration = 1</c>);
///         planar DNG is documented but extremely rare in practice — throws if seen.</item>
/// </list>
/// </para>
///
/// <para>The caller must register an <see cref="IRawDecoder"/> in
/// <paramref name="registry"/> for the IFD's <c>Compression</c> tag before calling
/// <see cref="ReadStage1"/>. For JXL files, register
/// <c>Dng.Sdk.Jxl.JxlDecoder</c> from the <c>Dng.Sdk.Jxl</c> project.</para>
/// </summary>
public static class StripReader
{
    /// <summary>
    /// Decode the main IFD of <paramref name="container"/> into a
    /// <see cref="StripReaderResult"/> which bundles the Stage-1 image with
    /// the IFD-derived <see cref="LinearizationInfo"/> and optional
    /// <see cref="MosaicInfo"/>.
    ///
    /// <para>The image's pixel type is derived from the IFD's
    /// <c>BitsPerSample</c> and <c>SampleFormat</c> tags.</para>
    /// </summary>
    /// <param name="stream">Open DNG stream positioned at the start of the file.</param>
    /// <param name="container">Parsed container (supplies the IFD metadata).</param>
    /// <param name="registry">Codec registry — must contain a decoder for the
    /// IFD's <c>Compression</c> code.</param>
    /// <param name="host">Optional host for tile-size hints and cancellation.</param>
    public static StripReaderResult ReadStage1(
        DngStream stream,
        DngContainer container,
        CodecRegistry registry,
        DngHost? host = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(registry);

        var ifd = container.AllIfds[container.MainIndex];
        bool be = container.Header.BigEndian;

        var (image, photometric, pixelType, isFloat) = ReadRawIfdImage(stream, ifd, be, registry, host);
        // Real DNGs commonly store render metadata there even when the raw main
        // image lives in a SubIFD.
        var sharedIfd = container.TopLevelIfds.Count > 0 ? container.TopLevelIfds[0] : ifd;
        var shared = new DngShared();
        var profile = CameraProfileReader.Read(stream, sharedIfd, be, shared);

        // Read linearization and mosaic metadata from the raw main IFD.
        var lin   = LinearizationReader.Read(stream, ifd, be, isFloat);
        var mosaic = photometric == Photometric.Cfa
                     ? MosaicInfoReader.Read(stream, ifd, be) : null;

        var activeArea  = CropAreaReader.ReadActiveArea(stream, ifd, be);
        var defaultCropArea = DefaultCropAreaReader.ReadDefaultCropArea(stream, ifd, be, image.Bounds);
        var opcodeList1 = OpcodeListTagReader.Read(stream, ifd, 1);
        var opcodeList2 = OpcodeListTagReader.Read(stream, ifd, 2);
        var opcodeList3 = OpcodeListTagReader.Read(stream, ifd, 3);

        return new StripReaderResult
        {
            Stage1          = image,
            Linearization   = lin,
            Mosaic          = mosaic,
            Photometric     = photometric,
            CameraProfile   = profile,
            Shared          = shared,
            ActiveArea      = activeArea,
            DefaultCropArea = defaultCropArea,
            OpcodeList1     = opcodeList1,
            OpcodeList2     = opcodeList2,
            OpcodeList3     = opcodeList3,
        };
    }

    /// <summary>
    /// Decode <paramref name="ifd"/>'s strip/tile pixel data into a
    /// <see cref="SimpleImage"/>, applying the JXL Float16→Float32 precision
    /// promotion and row/column de-interleaving. Shared by <see cref="ReadStage1"/>
    /// (main IFD) and <see cref="ReadMaskImage"/> (transparency-mask IFD) — any
    /// raster sub-image IFD in the container can be decoded this way.
    /// </summary>
    private static (SimpleImage Image, Photometric Photometric, PixelType PixelType, bool IsFloat) ReadRawIfdImage(
        DngStream stream, TiffIfd ifd, bool be, CodecRegistry registry, DngHost? host)
    {
        uint imageWidth  = RequireScalar(ifd, DngTagCode.ImageWidth,  be);
        uint imageHeight = RequireScalar(ifd, DngTagCode.ImageLength, be);
        uint planes      = ifd.Find(DngTagCode.SamplesPerPixel) is { } spp
                           ? spp.GetScalarUInt(be) : 1u;

        var compression  = (Compression)RequireScalar(ifd, DngTagCode.Compression, be);
        var photometric  = ifd.Find(DngTagCode.PhotometricInterpretation) is { } pe
                           ? (Photometric)pe.GetScalarUInt(be) : Photometric.Rgb;
        var decoder      = registry.GetDecoder(compression);
        var pixelType    = DerivePixelType(stream, ifd, be);
        bool isFloat     = pixelType is PixelType.Float16 or PixelType.Float32;

        // JPEG XL is a lossy transform codec: its reconstructed sample values
        // are inherently full-precision floats, independent of the file's
        // declared BitsPerSample/SampleFormat (those tags describe the
        // *nominal* dynamic range of the original capture, not a hard
        // precision ceiling on what the codec can reconstruct). Requesting a
        // Float16 output buffer from libjxl for a Float16-tagged JXL stream
        // forces it to round its float32 reconstruction down to half
        // precision before we ever see it — introducing quantization
        // "stair-stepping" that's small in absolute terms but, after
        // gamma/tone-curve encoding of dark image regions, appears as a
        // visible banding/streaking artifact (see the "DNG left-edge render
        // divergence" investigation — verified bit-exact vs. native
        // dng_validate's Stage 1 output once decoded at Float32 instead).
        // Uncompressed strips are unaffected: their on-disk bytes genuinely
        // are Float16, so there's no extra precision to recover.
        if (compression == Compression.Jxl && pixelType == PixelType.Float16)
            pixelType = PixelType.Float32;

        var image = new SimpleImage(
            new DngRect(0, 0, (int)imageHeight, (int)imageWidth),
            planes, pixelType);

        // Check planar configuration — only interleaved supported today.
        if (ifd.Find(DngTagCode.PlanarConfiguration) is { } pcEntry)
        {
            var pc = (PlanarConfiguration)pcEntry.GetScalarUInt(be);
            if (pc == PlanarConfiguration.Planar)
                DngThrow.NotYetImplemented("StripReader: PlanarConfiguration=2 (planar) is not yet supported");
        }

        // Detect strip vs tile layout.
        bool isTiled = ifd.Find(DngTagCode.TileOffsets) is not null;

        if (isTiled)
            ReadTiles(stream, ifd, be, image, planes, pixelType, decoder, host);
        else
            ReadStrips(stream, ifd, be, image, imageWidth, imageHeight, planes, pixelType, decoder, host);

        // Reverse row/column interleaving (DNG 1.7.1 RowInterleaveFactor /
        // ColumnInterleaveFactor tags), when present. The raw strip/tile data
        // above was decoded in its on-disk "field" layout (e.g. four
        // quadrant sub-images for a 2×2-interleaved Bayer JXL stream); this
        // reassembles it into normal raster order before any other stage
        // sees it. Mirrors dng_read_image::Read's early Interleave2D call.
        uint rowInterleave = ifd.Find(DngTagCode.RowInterleaveFactor) is { } rifEntry
                             ? rifEntry.GetScalarUInt(be) : 1u;
        uint colInterleave = ifd.Find(DngTagCode.ColumnInterleaveFactor) is { } cifEntry
                             ? cifEntry.GetScalarUInt(be) : 1u;
        if (rowInterleave > 1 || colInterleave > 1)
            image = RowColumnInterleave.Decode(image, (int)rowInterleave, (int)colInterleave);

        return (image, photometric, pixelType, isFloat);
    }

    /// <summary>
    /// Decode the DNG's transparency-mask IFD (<c>NewSubFileType</c> 4/5), if
    /// present, into a single-plane <see cref="SimpleImage"/>. Returns
    /// <see langword="null"/> when <paramref name="container"/> has no mask IFD.
    ///
    /// <para>Mirrors <c>dng_negative::ReadTransparencyMask</c>: the mask is just
    /// another raster image (its own strip/tile layout, own compression), not
    /// linearized or demosaiced — callers composite it directly against the
    /// rendered Stage-3/output image (mask value 0 = fully transparent →
    /// background shows through; max = fully opaque → rendered pixel shows
    /// through). Resizing the mask to match Stage-3 dimensions when they differ
    /// (native's <c>ResizeTransparencyToMatchStage3</c>) is not yet implemented —
    /// this port assumes the mask and main image share the same pixel grid,
    /// which holds for the samples exercised so far.</para>
    /// </summary>
    public static SimpleImage? ReadMaskImage(
        DngStream stream,
        DngContainer container,
        CodecRegistry registry,
        DngHost? host = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(registry);

        if (container.MaskIndex < 0) return null;

        var ifd = container.AllIfds[container.MaskIndex];
        bool be = container.Header.BigEndian;

        var (image, _, _, _) = ReadRawIfdImage(stream, ifd, be, registry, host);
        return image;
    }

    // ── Strips ───────────────────────────────────────────────────────────────

    private static void ReadStrips(
        DngStream stream,
        TiffIfd ifd,
        bool be,
        SimpleImage image,
        uint imageWidth,
        uint imageHeight,
        uint planes,
        PixelType pixelType,
        IRawDecoder decoder,
        DngHost? host)
    {
        uint rowsPerStrip = ifd.Find(DngTagCode.RowsPerStrip) is { } rps
                            ? rps.GetScalarUInt(be) : imageHeight;
        if (rowsPerStrip == 0) rowsPerStrip = imageHeight;

        var offsets    = ReadUInt64Array(stream, ifd, DngTagCode.StripOffsets,    be);
        var byteCounts = ReadUInt64Array(stream, ifd, DngTagCode.StripByteCounts, be);

        if (offsets.Length != byteCounts.Length)
            DngThrow.BadFormat($"StripReader: StripOffsets count ({offsets.Length}) ≠ StripByteCounts count ({byteCounts.Length})");

        for (int i = 0; i < offsets.Length; i++)
        {
            uint stripTop    = (uint)i * rowsPerStrip;
            uint stripHeight = (uint)System.Math.Min((long)rowsPerStrip, (long)(imageHeight - stripTop));

            var compressed = ReadBytes(stream, (long)offsets[i], (long)byteCounts[i]);

            var dstArea = new DngRect((int)stripTop, 0, (int)(stripTop + stripHeight), (int)imageWidth);
            var dst = image.GetTile(dstArea);
            decoder.Decode(compressed, dst, be);
            image.WriteTile(dst);
        }
    }

    // ── Tiles ────────────────────────────────────────────────────────────────

    private static void ReadTiles(
        DngStream stream,
        TiffIfd ifd,
        bool be,
        SimpleImage image,
        uint planes,
        PixelType pixelType,
        IRawDecoder decoder,
        DngHost? host)
    {
        uint tileW = RequireScalar(ifd, DngTagCode.TileWidth,  be);
        uint tileH = RequireScalar(ifd, DngTagCode.TileLength, be);

        uint imageW = (uint)image.Bounds.W;
        uint imageH = (uint)image.Bounds.H;

        var offsets    = ReadUInt64Array(stream, ifd, DngTagCode.TileOffsets,    be);
        var byteCounts = ReadUInt64Array(stream, ifd, DngTagCode.TileByteCounts, be);

        if (offsets.Length != byteCounts.Length)
            DngThrow.BadFormat($"StripReader: TileOffsets count ({offsets.Length}) ≠ TileByteCounts count ({byteCounts.Length})");

        uint tilesAcross = (imageW + tileW - 1) / tileW;
        uint tilesDown   = (imageH + tileH - 1) / tileH;

        if ((ulong)(tilesAcross * tilesDown) != (ulong)offsets.Length)
            DngThrow.BadFormat(
                $"StripReader: expected {tilesAcross}×{tilesDown}={tilesAcross * tilesDown} tiles, "
                + $"got {offsets.Length} offset entries");

        for (uint ty = 0; ty < tilesDown; ty++)
        {
            for (uint tx = 0; tx < tilesAcross; tx++)
            {
                int idx = (int)(ty * tilesAcross + tx);
                uint tileTop  = ty * tileH;
                uint tileLeft = tx * tileW;
                uint tileActH = (uint)System.Math.Min((long)tileH, (long)(imageH - tileTop));
                uint tileActW = (uint)System.Math.Min((long)tileW, (long)(imageW - tileLeft));

                var compressed = ReadBytes(stream, (long)offsets[idx], (long)byteCounts[idx]);

                // ALWAYS decode into a compact scratch buffer (origin at 0,0) and
                // then copy row-by-row into the strided image buffer.
                //
                // Root cause: image.GetTile(dstArea).RowStep = imageWidth × planes
                // (strided), but compressed decoders like JXL write compactly
                // (RowStep = tileWidth × planes). Giving a strided buffer to a
                // compact writer causes rows to land at wrong offsets.
                //
                // For boundary (partial) tiles the logical tile size tileH × tileW is
                // always used for the scratch — the codec encodes the full tile
                // including any image-boundary padding.
                var scratchBounds = new DngRect(0, 0, (int)tileH, (int)tileW);
                var scratch = new SimpleImage(scratchBounds, planes, pixelType);
                var scratchBuf = scratch.GetTile(scratchBounds);
                decoder.Decode(compressed, scratchBuf, be);
                scratch.WriteTile(scratchBuf);

                // Row-by-row copy from compact scratch → strided image.
                var dstArea = new DngRect(
                    (int)tileTop, (int)tileLeft,
                    (int)(tileTop + tileActH), (int)(tileLeft + tileActW));
                var dst      = image.GetTile(dstArea);
                var dstBytes = dst.Memory.Span;
                var srcBytes = scratchBuf.Memory.Span;
                int rowBytes = (int)tileActW * (int)planes * (int)pixelType.SizeBytes();
                int srcRowStride = (int)tileW * (int)planes * (int)pixelType.SizeBytes();

                for (int row = 0; row < (int)tileActH; row++)
                {
                    // Source: compact scratch, row-major from (row, 0).
                    int srcOff = row * srcRowStride;
                    // Destination: strided image buffer at (tileTop+row, tileLeft).
                    long dstOff = dst.OffsetBytes((int)tileTop + row, (int)tileLeft, 0);
                    srcBytes.Slice(srcOff, rowBytes).CopyTo(dstBytes.Slice((int)dstOff, rowBytes));
                }
                image.WriteTile(dst);
            }
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static PixelType DerivePixelType(DngStream stream, TiffIfd ifd, bool be)
    {
        // BitsPerSample for the first plane (all planes must match for
        // interleaved DNG; heterogeneous depths are not supported here).
        uint bps = ifd.Find(DngTagCode.BitsPerSample) is { } bpsEntry
                   ? ReadFirstUInt16(stream, bpsEntry, be) : 8u;

        var fmt = ifd.Find(DngTagCode.SampleFormat) is { } sfEntry
                  ? (SampleFormat)ReadFirstUInt16(stream, sfEntry, be) : SampleFormat.UnsignedInteger;

        return (bps, fmt) switch
        {
            (8,  SampleFormat.UnsignedInteger) => PixelType.UInt8,
            (8,  _)                            => PixelType.UInt8,
            (16, SampleFormat.UnsignedInteger) => PixelType.UInt16,
            (16, SampleFormat.SignedInteger)   => PixelType.SInt16,
            (16, SampleFormat.FloatingPoint)   => PixelType.Float16,
            (32, SampleFormat.UnsignedInteger) => PixelType.UInt32,
            (32, SampleFormat.FloatingPoint)   => PixelType.Float32,
            _ => throw new DngException(DngError.NotYetImplemented,
                     $"StripReader: unsupported BitsPerSample={bps} / SampleFormat={fmt}"),
        };
    }

    private static uint RequireScalar(TiffIfd ifd, DngTagCode tag, bool be)
    {
        var entry = ifd.Find(tag);
        if (entry is null)
            DngThrow.BadFormat($"StripReader: required tag {tag} is missing from main IFD");
        return entry!.GetScalarUInt(be);
    }

    /// <summary>
    /// Read the first <c>uint16</c> from a Short/Long-typed entry, handling both
    /// inline single values and out-of-line arrays (e.g. <c>BitsPerSample = 16 16 16</c>).
    /// </summary>
    private static uint ReadFirstUInt16(DngStream stream, TiffIfdEntry entry, bool be)
    {
        if (entry.IsInline)
        {
            var s = entry.InlineValue.Span;
            return entry.Type == TiffDataType.Short || entry.Type == TiffDataType.SShort
                ? (be ? BinaryPrimitives.ReadUInt16BigEndian(s) : BinaryPrimitives.ReadUInt16LittleEndian(s))
                : entry.GetScalarUInt(be);
        }

        // Out-of-line array — read the first element from the stream.
        long saved = stream.Position;
        try
        {
            stream.Position = entry.ValueOffset;
            var buf = new byte[2];
            stream.ReadExactly(buf);
            return be ? BinaryPrimitives.ReadUInt16BigEndian(buf) : BinaryPrimitives.ReadUInt16LittleEndian(buf);
        }
        finally { stream.Position = saved; }
    }

    /// <summary>
    /// Read all values from a Long (uint32) or Long8 (uint64) array entry.
    /// Handles both inline (single scalar) and out-of-line (multiple values) cases.
    /// </summary>
    private static ulong[] ReadUInt64Array(DngStream stream, TiffIfd ifd, DngTagCode tag, bool be)
    {
        var entry = ifd.Find(tag);
        if (entry is null)
            DngThrow.BadFormat($"StripReader: required tag {tag} is missing");

        int count = (int)System.Math.Min((ulong)int.MaxValue, entry!.Count);
        var result = new ulong[count];

        if (entry.IsInline)
        {
            // Inline → exactly one element.
            result[0] = entry.GetScalarUInt(be);
            return result;
        }

        // Out-of-line array — read from stream.
        long savedPos = stream.Position;
        try
        {
            stream.Position = entry.ValueOffset;

            bool isLong8 = entry.Type is TiffDataType.Long8 or TiffDataType.SLong8 or TiffDataType.Ifd8;
            var elemBuf = new byte[isLong8 ? 8 : 4];
            for (int i = 0; i < count; i++)
            {
                stream.ReadExactly(elemBuf);
                result[i] = isLong8
                    ? (be ? BinaryPrimitives.ReadUInt64BigEndian(elemBuf) : BinaryPrimitives.ReadUInt64LittleEndian(elemBuf))
                    : (be ? BinaryPrimitives.ReadUInt32BigEndian(elemBuf) : BinaryPrimitives.ReadUInt32LittleEndian(elemBuf));
            }
        }
        finally
        {
            stream.Position = savedPos;
        }

        return result;
    }

    private static byte[] ReadBytes(DngStream stream, long offset, long length)
    {
        if (length <= 0) return [];
        var buf = new byte[length];
        long saved = stream.Position;
        try
        {
            stream.Position = offset;
            stream.ReadExactly(buf);
        }
        finally { stream.Position = saved; }
        return buf;
    }
}
