using Dng.Sdk.Codecs;
using Dng.Sdk.Pixels;
using Dng.Sdk.Primitives;
using Dng.Sdk.Tiff;
using Dng.Sdk.Writer;

namespace Dng.Sdk.Tests.TestImages;

/// <summary>
/// Builds small, fully-synthetic, analytically-known DNG (and companion
/// plain-TIFF) fixtures for regression tests. Each generator writes a
/// minimal-but-valid DNG (uncompressed, 3-plane <c>LinearRaw</c>, identity
/// camera matrix, no embedded tone curve — the same shape as real-world
/// "no <c>ProfileToneCurve</c>" files like Apple ProRAW captures) so the
/// full pipeline (<c>DngContainer</c> → <c>StripReader</c> →
/// <c>Stage2Builder</c>/<c>Stage3Builder</c> → <c>Stage3Renderer</c> →
/// <c>HdrToneMapper</c> → JPEG/WebP encode) is exercised exactly as a real
/// file would be, with an expected pixel value known ahead of time at every
/// column so regressions (streaking, duplicate columns, crop/tone-curve
/// bugs) are trivially detectable.
///
/// <para>These are intentionally uncompressed (no JXL/JPEG encode step) so
/// fixture generation has no native-codec dependency and stays fast and
/// portable; they still exercise every managed pipeline stage that produced
/// the left-edge streak bug (see the "DNG left-edge render divergence"
/// investigation), short of the JXL tile-decode step itself.</para>
/// </summary>
public static class SyntheticDngBuilder
{
    /// <summary>
    /// Builds a <paramref name="width"/>×<paramref name="height"/> DNG whose
    /// raw camera-space value is a horizontal ramp: column 0 = 0, column
    /// <paramref name="width"/>-1 = <paramref name="maxValue"/>, linear in
    /// between, identical in every row and replicated across all 3 planes
    /// (R=G=B), so the rendered image should be a neutral-gray gradient that
    /// is monotonically non-decreasing left→right with no flat "stuck"
    /// regions and no reversals.
    /// </summary>
    public static byte[] BuildGradientLeftToRightDng(
        int width,
        int height,
        ushort maxValue = 65535,
        bool bigEndian = false)
    {
        var pixels = SyntheticPixelPatterns.GradientLeftToRight(width, height, maxValue);
        return BuildLinearRawDng(pixels, width, height, planes: 3, bigEndian);
    }

    /// <summary>
    /// Builds a <paramref name="size"/>×<paramref name="size"/> DNG: a white
    /// background with a black circle centered in the image, covering
    /// <paramref name="diameterFraction"/> (default 50%) of both width and
    /// height. Used to detect anisotropic (aspect-distorting) stretching
    /// bugs in the render pipeline — a true circle rendered with unequal
    /// horizontal/vertical scaling comes out as a visibly non-circular
    /// ellipse.
    /// </summary>
    public static byte[] BuildCenteredCircleDng(
        int size,
        double diameterFraction = 0.5,
        ushort maxValue = 65535,
        bool bigEndian = false)
    {
        var pixels = SyntheticPixelPatterns.CenteredCircle(size, diameterFraction, maxValue);
        return BuildLinearRawDng(pixels, size, size, planes: 3, bigEndian);
    }

    /// <summary>
    /// Builds a <paramref name="width"/>×<paramref name="height"/> DNG with an
    /// alternating black/white <paramref name="squarePx"/>-sized checkerboard.
    /// Used to detect tile/strip-boundary misalignment and duplicate/missing
    /// column or row decode bugs: a correct render shows transitions spaced
    /// exactly <paramref name="squarePx"/> pixels apart along both axes.
    /// </summary>
    public static byte[] BuildCheckerboardDng(
        int width,
        int height,
        int squarePx,
        ushort maxValue = 65535,
        bool bigEndian = false)
    {
        var pixels = SyntheticPixelPatterns.Checkerboard(width, height, squarePx, maxValue);
        return BuildLinearRawDng(pixels, width, height, planes: 3, bigEndian);
    }

    /// <summary>
    /// Builds a <paramref name="rawSize"/>×<paramref name="rawSize"/> raw-sensor
    /// DNG whose <c>ActiveArea</c> and <c>DefaultCropOrigin</c>/<c>DefaultCropSize</c>
    /// both select the centered <paramref name="innerSize"/>×<paramref name="innerSize"/>
    /// sub-rect (offset <paramref name="margin"/> from each edge). Everything
    /// outside that sub-rect is filled with a mid-gray "poison" value; inside
    /// it, solid black with a <paramref name="borderPx"/>-wide white border at
    /// the crop rect's exact edges. Detects off-by-one crop bugs: a correct
    /// render is exactly <paramref name="innerSize"/>×<paramref name="innerSize"/>
    /// with a uniform border and no poison gray visible.
    /// </summary>
    public static byte[] BuildBorderCropDng(
        int rawSize,
        int margin,
        int innerSize,
        int borderPx,
        ushort maxValue = 65535,
        bool bigEndian = false)
    {
        var pixels = SyntheticPixelPatterns.BorderedActiveArea(rawSize, margin, innerSize, borderPx, maxValue);
        return BuildLinearRawDng(
            pixels, rawSize, rawSize, planes: 3, bigEndian,
            configureIfd: (ifd, be) =>
            {
                uint top = (uint)margin, left = (uint)margin;
                uint bottom = (uint)(margin + innerSize), right = (uint)(margin + innerSize);

                ifd.Entries.Add(TagBuilder.UInt32Array(
                    DngTagCode.ActiveArea, [top, left, bottom, right], be));

                // DefaultCropOrigin/Size are [H, V] (x, y) order, unlike
                // ActiveArea's [top, left, bottom, right] order.
                ifd.Entries.Add(TagBuilder.UInt32Array(
                    DngTagCode.DefaultCropOrigin, [(uint)margin, (uint)margin], be));
                ifd.Entries.Add(TagBuilder.UInt32Array(
                    DngTagCode.DefaultCropSize, [(uint)innerSize, (uint)innerSize], be));
            });
    }

    /// <summary>
    /// Builds a <paramref name="width"/>×<paramref name="height"/> left-to-right
    /// gradient DNG (same fixture as <see cref="BuildGradientLeftToRightDng"/>)
    /// tagged with the given TIFF <c>Orientation</c> value (1-9, per EXIF/TIFF
    /// convention — see <see cref="Dng.Sdk.Primitives.DngOrientation.FromTiff"/>).
    /// Used to check that the renderer's handling of the <c>Orientation</c> tag
    /// (currently: not applied — see the 'Geometry' synthetic-fixture todos) is
    /// at least *consistent* across all 8 defined values, i.e. it does not
    /// crash and does not partially/inconsistently apply the tag.
    /// </summary>
    public static byte[] BuildOrientedGradientDng(
        int width,
        int height,
        ushort tiffOrientation,
        ushort maxValue = 65535,
        bool bigEndian = false)
    {
        var pixels = SyntheticPixelPatterns.GradientLeftToRight(width, height, maxValue);
        return BuildLinearRawDng(
            pixels, width, height, planes: 3, bigEndian,
            configureIfd: (ifd, be) =>
                ifd.Entries.Add(TagBuilder.UInt16(DngTagCode.Orientation, tiffOrientation, be)));
    }

    /// <summary>
    /// Wraps a pre-computed interleaved UInt16 pixel buffer (row-major,
    /// <paramref name="planes"/> samples per pixel) as a minimal valid
    /// uncompressed <c>LinearRaw</c> DNG: <c>DNGVersion</c>/<c>DNGBackwardVersion</c>,
    /// <c>UniqueCameraModel</c>, an identity <c>ColorMatrix1</c> (D65 illuminant),
    /// <c>BlackLevel</c>=0 / <c>WhiteLevel</c>=65535, and no
    /// <c>ProfileToneCurve</c> — deliberately, so tests exercise the
    /// default-tone-curve fallback path.
    /// </summary>
    public static byte[] BuildLinearRawDng(
        ushort[] interleavedPixels,
        int width,
        int height,
        uint planes,
        bool bigEndian = false,
        Action<TiffIfdToWrite, bool>? configureIfd = null)
    {
        var bounds = new DngRect(0, 0, height, width);
        var srcBytes = new byte[interleavedPixels.Length * 2];
        Buffer.BlockCopy(interleavedPixels, 0, srcBytes, 0, srcBytes.Length);
        var src = PixelBuffer.Interleaved(bounds, planes, PixelType.UInt16, srcBytes);

        var encoded = new UncompressedEncoder().Encode(src, bigEndian);

        var ifd = new TiffIfdToWrite();
        var stripBlob = new DeferredBlob { Bytes = encoded };
        ifd.Blobs.Add(stripBlob);

        ifd.Entries.Add(TagBuilder.UInt32(DngTagCode.NewSubFileType, 0, bigEndian));
        ifd.Entries.Add(TagBuilder.UInt32(DngTagCode.ImageWidth, (uint)width, bigEndian));
        ifd.Entries.Add(TagBuilder.UInt32(DngTagCode.ImageLength, (uint)height, bigEndian));
        ifd.Entries.Add(TagBuilder.UInt16Array(DngTagCode.BitsPerSample, Enumerable.Repeat((ushort)16, (int)planes).ToArray(), bigEndian));
        ifd.Entries.Add(TagBuilder.UInt16(DngTagCode.Compression, (ushort)Compression.Uncompressed, bigEndian));
        ifd.Entries.Add(TagBuilder.UInt16(DngTagCode.PhotometricInterpretation,
            (ushort)Photometric.LinearRaw, bigEndian));
        ifd.Entries.Add(TagBuilder.UInt16(DngTagCode.SamplesPerPixel, (ushort)planes, bigEndian));
        ifd.Entries.Add(TagBuilder.UInt16(DngTagCode.PlanarConfiguration, 1, bigEndian)); // chunky/interleaved
        ifd.Entries.Add(TagBuilder.UInt32(DngTagCode.RowsPerStrip, (uint)height, bigEndian));
        ifd.Entries.Add(TagBuilder.UInt32(DngTagCode.StripByteCounts, (uint)encoded.Length, bigEndian));

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

        // DNG identification + minimal camera-profile tags. ColorMatrix1 is
        // the identity matrix (camera space == XYZ) so the rendered gradient
        // stays neutral gray. Deliberately no ProfileToneCurve tag — this
        // exercises the default (no-embedded-curve) fallback path.
        ifd.Entries.Add(TagBuilder.Bytes(DngTagCode.DNGVersion, [1, 4, 0, 0]));
        ifd.Entries.Add(TagBuilder.Bytes(DngTagCode.DNGBackwardVersion, [1, 0, 0, 0]));
        ifd.Entries.Add(TagBuilder.Ascii(DngTagCode.UniqueCameraModel, "Dng.Sdk Synthetic Test Camera"));
        ifd.Entries.Add(TagBuilder.Ascii(DngTagCode.Make, "Dng.Sdk"));
        ifd.Entries.Add(TagBuilder.Ascii(DngTagCode.Model, "Synthetic"));
        ifd.Entries.Add(TagBuilder.UInt16(DngTagCode.CalibrationIlluminant1, 21, bigEndian)); // 21 = D65
        ifd.Entries.Add(SRationalMatrix3X3Identity(DngTagCode.ColorMatrix1, bigEndian));

        configureIfd?.Invoke(ifd, bigEndian);

        var memory = new MemoryStream();
        using (var ds = new Dng.Sdk.IO.DngStream(memory, bigEndian, leaveOpen: true))
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
    // returns (mirrors the pattern in TiffWriterRoundTripTests); collected
    // here and applied immediately after each Write() call above.
    private static readonly List<(long Pos, uint Value, bool BigEndian)> Patches = [];

    private static TiffEntryToWrite SRationalMatrix3X3Identity(DngTagCode tag, bool bigEndian)
    {
        // 9 SRationals: identity 3x3 matrix (1/1, 0/1, 0/1, 0/1, 1/1, 0/1, 0/1, 0/1, 1/1).
        Span<int> num = [1, 0, 0, 0, 1, 0, 0, 0, 1];
        var bytes = new byte[9 * 8];
        for (int i = 0; i < 9; i++)
        {
            int n = num[i];
            const int d = 1;
            if (bigEndian)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(i * 8), n);
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(i * 8 + 4), d);
            }
            else
            {
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * 8), n);
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * 8 + 4), d);
            }
        }
        return new TiffEntryToWrite
        {
            Tag = tag,
            Type = TiffDataType.SRational,
            Count = 9,
            Payload = bytes,
        };
    }
}
