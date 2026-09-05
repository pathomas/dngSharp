using DngSharp.Dng.Sdk;
using DngSharp.Dng.Sdk.Codecs;
using DngSharp.Dng.Sdk.Container;
using DngSharp.Dng.Sdk.Errors;
using DngSharp.Dng.Sdk.Imaging.Profile;
using DngSharp.Dng.Sdk.IO;
using DngSharp.Dng.Sdk.Jxl;
using DngSharp.Dng.Sdk.Metadata.Xmp;
using DngSharp.Dng.Sdk.Pipeline;
using DngSharp.Dng.Sdk.Preview;
using DngSharp.Dng.Sdk.Render;
using DngSharp.Dng.Sdk.Tiff;
using DngSharp.Dng.Sdk.Writer;

return Cli.Run(args);

static class Cli
{
    public static int Run(string[] args)
    {
        // Brackets XMP lifecycle around the command (mirrors dng_validate.cpp's
        // dng_xmp_sdk::InitializeSDK / TerminateSDK pattern). The NullXmpSdk
        // is a no-op; hosts wire a real IXmpSdk by replacing this construction.
        using var xmp = new NullXmpSdk();
        xmp.Initialize();

        try
        {
            return RunCore(args);
        }
        catch (DngException ex)
        {
            Console.Error.WriteLine($"error: {ex.ErrorCode}: {ex.Message}");
            // Mirror native dng_validate's exit-code mapping: (code - 100000 + 100).
            int rawCode = (int)ex.ErrorCode;
            if (rawCode is >= 100_000 and < 100_200)
                return rawCode - 100_000 + 100;
            return 1;
        }
        catch (FileNotFoundException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"unexpected error: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static int RunCore(string[] args)
    {
        if (args.Length == 0 || args.Any(a => a is "-h" or "--help"))
        {
            PrintHelp();
            return args.Length == 0 ? 1 : 0;
        }

        var opts = ParseOptions(args);
        if (opts.Inputs.Count == 0)
        {
            Console.Error.WriteLine("error: no input files");
            return 2;
        }

        foreach (var path in opts.Inputs)
        {
            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"error: file not found: {path}");
                return 2;
            }

            using var stream = DngFileStream.OpenRead(path);
            var container = DngContainer.Parse(stream);

            if (opts.Verbose)
                PrintVerbose(stream, container, path);
            else
                PrintSummary(container, path);

            if (opts.DngOutput is { } outPath)
                RoundTripDng(stream, container, outPath);

            if (opts.JpegOutput is not null || opts.WebPOutput is not null)
                RenderAndSave(stream, container, opts);

            if (opts.Stage1Output is not null || opts.Stage2Output is not null || opts.Stage3Output is not null)
                DumpStages(stream, container, opts);
        }
        return 0;
    }

    // -------- Modes ----------------------------------------------------------

    private static void PrintSummary(DngContainer container, string path)
    {
        Console.WriteLine($"== {Path.GetFileName(path)} ==");
        Console.WriteLine($"  byte order   : {(container.Header.BigEndian ? "big-endian (MM)" : "little-endian (II)")}");
        Console.WriteLine($"  format       : {(container.Header.BigTiff ? "BigTIFF (64-bit)" : "TIFF (32-bit)")}");
        Console.WriteLine($"  IFDs (total) : {container.AllIfds.Count} ({container.TopLevelIfds.Count} top-level)");
        Console.WriteLine($"  main         : #{container.MainIndex}");
        if (container.PreviewIndices.Count > 0)
            Console.WriteLine($"  previews     : {string.Join(", ", container.PreviewIndices.Select(i => $"#{i}"))}");
        if (container.MaskIndex >= 0)     Console.WriteLine($"  trans. mask  : #{container.MaskIndex}");
        if (container.DepthIndex >= 0)    Console.WriteLine($"  depth        : #{container.DepthIndex}");
        if (container.EnhancedIndex >= 0) Console.WriteLine($"  enhanced     : #{container.EnhancedIndex}");
        if (container.GainMapIndex >= 0)  Console.WriteLine($"  gain map     : #{container.GainMapIndex}");
        if (container.SemanticMaskIndices.Count > 0)
            Console.WriteLine($"  sem. masks   : {string.Join(", ", container.SemanticMaskIndices.Select(i => $"#{i}"))}");

        var main = container.AllIfds[container.MainIndex];
        var width  = main.Find(DngTagCode.ImageWidth )?.GetScalarUInt(container.Header.BigEndian);
        var height = main.Find(DngTagCode.ImageLength)?.GetScalarUInt(container.Header.BigEndian);
        var comp   = main.Find(DngTagCode.Compression);
        var photo  = main.Find(DngTagCode.PhotometricInterpretation);
        Console.WriteLine($"  main image   : {width}x{height}, "
                        + $"compression={(comp  is null ? "?" : (Compression )comp .GetScalarUInt(container.Header.BigEndian))}, "
                        + $"photometric={(photo is null ? "?" : (Photometric)photo.GetScalarUInt(container.Header.BigEndian))}");
        Console.WriteLine($"  entries (main IFD) : {main.Entries.Count}");
        Console.WriteLine();
    }

    private static void PrintVerbose(DngStream stream, DngContainer container, string path)
    {
        Console.WriteLine($"# {Path.GetFileName(path)}");
        Console.WriteLine($"#   byte order : {(container.Header.BigEndian ? "MM" : "II")}");
        Console.WriteLine($"#   bigtiff    : {container.Header.BigTiff}");
        Console.WriteLine();
        for (int i = 0; i < container.AllIfds.Count; i++)
        {
            var ifd = container.AllIfds[i];
            string role = ClassifyRole(container, i);
            Console.WriteLine($"## IFD #{i} @ 0x{ifd.Offset:X} {role} — {ifd.Entries.Count} entries");
            foreach (var e in ifd.Entries.OrderBy(x => (uint)x.Tag))
            {
                string value = TagValueFormatter.Format(e, stream, container.Header.BigEndian, maxElements: 8);
                string tagName = Enum.IsDefined(e.Tag) ? e.Tag.ToString() : $"Tag0x{(uint)e.Tag:X4}";
                Console.WriteLine($"  {tagName,-32} 0x{(uint)e.Tag:X4}  {e.Type,-9} count={e.Count,-6} = {value}");
            }
            Console.WriteLine();
        }
    }

    private static string ClassifyRole(DngContainer c, int i)
    {
        if (i == c.MainIndex) return "[main]";
        if (i == c.MaskIndex) return "[transparency mask]";
        if (i == c.DepthIndex) return "[depth]";
        if (i == c.EnhancedIndex) return "[enhanced]";
        if (i == c.GainMapIndex) return "[gain map]";
        if (c.PreviewIndices.Contains(i)) return "[preview]";
        if (c.SemanticMaskIndices.Contains(i)) return "[semantic mask]";
        return "";
    }

    /// <summary>
    /// Minimal -dng round-trip: read every IFD entry's payload and re-emit it
    /// verbatim into a fresh TIFF. Demonstrates the writer end-to-end; full
    /// fidelity (re-encoded strips, regenerated profiles, etc.) lands in
    /// Phase 10 with the full render path.
    /// </summary>
    private static void RoundTripDng(DngStream stream, DngContainer container, string outPath)
    {
        var ifdsToWrite = new List<TiffIfdToWrite>(container.TopLevelIfds.Count);
        foreach (var srcIfd in container.TopLevelIfds)
            ifdsToWrite.Add(BuildIfdToWrite(stream, srcIfd));

        using var outFs = DngFileStream.Create(outPath);
        new TiffWriter(container.Header.BigEndian).Write(outFs, ifdsToWrite);
        Console.WriteLine($"  → wrote {ifdsToWrite.Count} IFD(s) to {outPath}");
    }

    /// <summary>
    /// Recreates one IFD (and, recursively, any SubIFDs it references) for
    /// the minimal round-trip writer: every entry's payload is re-emitted
    /// verbatim except strip/tile offsets (the writer doesn't relocate pixel
    /// data yet) and the SubIFDs link itself, which is rebuilt from the
    /// recursively-converted nested IFDs so the reconstituted file preserves
    /// the original SubIFD structure (main image, raw-to-raw sequences, etc.).
    /// </summary>
    private static TiffIfdToWrite BuildIfdToWrite(DngStream stream, TiffIfd srcIfd)
    {
        var dst = new TiffIfdToWrite();
        foreach (var e in srcIfd.Entries.OrderBy(x => (uint)x.Tag))
        {
            // Strip/tile offsets are dropped: the writer would have to
            // relocate the referenced pixel data, which means re-emitting it.
            if (e.Tag is DngTagCode.StripOffsets or DngTagCode.TileOffsets)
                continue;

            if (e.Tag == DngTagCode.SubIFDs)
            {
                var nestedIfds = srcIfd.SubIfds.Select(sub => BuildIfdToWrite(stream, sub)).ToList();
                if (nestedIfds.Count == 0) continue; // nothing to link to

                dst.Entries.Add(new TiffEntryToWrite
                {
                    Tag = DngTagCode.SubIFDs,
                    Type = TiffDataType.Long,
                    Count = (uint)nestedIfds.Count,
                    Payload = ReadOnlyMemory<byte>.Empty, // ignored — see SubIfds
                    SubIfds = nestedIfds,
                });
                continue;
            }

            var bytes = e.IsInline
                ? e.InlineValue.ToArray()
                : ReadOutOfLineBytes(stream, e);

            dst.Entries.Add(new TiffEntryToWrite
            {
                Tag = e.Tag,
                Type = e.Type,
                Count = (uint)System.Math.Min(e.Count, uint.MaxValue),
                Payload = bytes,
            });
        }
        return dst;
    }

    private static byte[] ReadOutOfLineBytes(DngStream stream, TiffIfdEntry e)
    {
        if (e.PayloadSize > int.MaxValue) return [];
        var buf = new byte[(int)e.PayloadSize];
        long saved = stream.Position;
        try
        {
            stream.Position = e.ValueOffset;
            stream.ReadExactly(buf);
        }
        finally { stream.Position = saved; }
        return buf;
    }

    // -------- Argument parsing -----------------------------------------------

    internal sealed class Options
    {
        public List<string> Inputs { get; } = [];
        public bool Verbose { get; set; }
        public string? DngOutput { get; set; }
        public string? JpegOutput { get; set; }
        public string? WebPOutput { get; set; }
        public OutputColorSpace ColorSpace { get; set; } = OutputColorSpace.Srgb;
        public bool HdrMode { get; set; }
        public string? Stage1Output { get; set; }
        public string? Stage2Output { get; set; }
        public string? Stage3Output { get; set; }
    }

    internal static Options ParseOptions(string[] args)
    {
        var opts = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "-v":
                    opts.Verbose = true;
                    break;
                case "-dng":
                    if (i + 1 >= args.Length)
                        throw new DngException(DngError.Unknown, "-dng requires an output path");
                    opts.DngOutput = args[++i];
                    break;
                case "-jpeg":
                    if (i + 1 >= args.Length)
                        throw new DngException(DngError.Unknown, "-jpeg requires an output path");
                    opts.JpegOutput = args[++i];
                    break;
                case "-webp":
                    if (i + 1 >= args.Length)
                        throw new DngException(DngError.Unknown, "-webp requires an output path");
                    opts.WebPOutput = args[++i];
                    break;
                case "-cs1":
                    opts.ColorSpace = OutputColorSpace.Srgb;
                    break;
                case "-cs2":
                    opts.ColorSpace = OutputColorSpace.AdobeRgb;
                    break;
                case "-cs3":
                    opts.ColorSpace = OutputColorSpace.ProPhotoRgb;
                    break;
                case "-csP3":
                    opts.ColorSpace = OutputColorSpace.DisplayP3;
                    break;
                case "-cs2020":
                    opts.ColorSpace = OutputColorSpace.Rec2020;
                    break;
                case "-hdr":
                    opts.HdrMode = true;
                    break;
                case "-1":
                    if (i + 1 >= args.Length)
                        throw new DngException(DngError.Unknown, "-1 requires an output path");
                    opts.Stage1Output = args[++i];
                    break;
                case "-2":
                    if (i + 1 >= args.Length)
                        throw new DngException(DngError.Unknown, "-2 requires an output path");
                    opts.Stage2Output = args[++i];
                    break;
                case "-3":
                    if (i + 1 >= args.Length)
                        throw new DngException(DngError.Unknown, "-3 requires an output path");
                    opts.Stage3Output = args[++i];
                    break;
                default:
                    if (a.StartsWith('-'))
                    {
                        throw new DngException(DngError.NotYetImplemented,
                            $"unknown or unsupported flag '{a}'");
                    }
                    opts.Inputs.Add(a);
                    break;
            }
        }
        return opts;
    }

    /// <summary>
    /// Decode and dump one or more pipeline stage images as TIFF files.
    /// -1 = raw Stage-1 (unlinearized), -2 = linearized Stage-2 (Float32),
    /// -3 = demosaiced Stage-3 (Float32). CFA (-3) requires Bayer demosaic.
    /// </summary>
    private static void DumpStages(DngStream stream, DngContainer container, Options opts)
    {
        var registry = new CodecRegistry();
        registry.Register(new DngSharp.Dng.Sdk.Codecs.UncompressedDecoder());
        registry.Register(new DngSharp.Dng.Sdk.Codecs.DeflateDecoder());
        registry.Register(new DngSharp.Dng.Sdk.Codecs.LosslessJpeg.LosslessJpegDecoder());
        if (JxlDecoder.IsAvailable) registry.Register(new JxlDecoder());

        Console.WriteLine("  Decoding Stage 1 ...");
        var r = StripReader.ReadStage1(stream, container, registry);

        // OpcodeList1 runs on the raw, unlinearized Stage-1 image, per
        // dng_negative::ReadStage1Image.
        var stage1Image = OpcodeList1Applier.Apply(r.Stage1, r.OpcodeList1);

        if (opts.Stage1Output is { } s1path)
        {
            StageImageWriter.Write(stage1Image, s1path);
            Console.WriteLine($"  → Stage 1 written to {s1path}");
        }

        if (opts.Stage2Output is not null || opts.Stage3Output is not null)
        {
            Console.WriteLine("  Linearizing Stage 2 ...");
            var stage2 = Stage2Builder.Build(stage1Image, r.Linearization);

            // OpcodeList2 runs on the linearized Stage-2 image, per
            // dng_negative::BuildStage2Image.
            stage2 = OpcodeList2Applier.Apply(stage2, r.OpcodeList2);

            if (opts.Stage2Output is { } s2path)
            {
                StageImageWriter.Write(stage2, s2path);
                Console.WriteLine($"  → Stage 2 written to {s2path}");
            }

            if (opts.Stage3Output is { } s3path)
            {
                if (!Stage3Builder.CanBuild(r.Photometric, r.Mosaic))
                    throw new DngException(DngError.NotYetImplemented,
                        $"-3 not yet supported for photometric={r.Photometric} without mosaic info. "
                        + "Supported: LinearRaw, RGB, Bayer CFA.");

                var stage3Image = Stage3Builder.Build(stage2, r.Photometric, r.Mosaic);
                if (stage3Image is not DngSharp.Dng.Sdk.Imaging.SimpleImage s3simple)
                    throw new DngException(DngError.Unknown, "Stage3 is not a SimpleImage");

                // OpcodeList3 (e.g. WarpRectilinear lens-CA correction) runs on
                // the demosaiced Stage-3 image, per dng_negative::BuildStage3Image.
                s3simple = OpcodeList3Applier.Apply(s3simple, r.OpcodeList3);

                // ActiveArea crop — matches what dng_validate -3 dumps.
                if (r.ActiveArea is { } activeArea)
                    s3simple = ImageCrop.Crop(s3simple, activeArea);

                StageImageWriter.Write(s3simple, s3path);
                Console.WriteLine($"  → Stage 3 written to {s3path}");
            }
        }
    }

    /// <summary>
    /// Full render pipeline: decode strips → Stage2 linearize → Stage3 passthrough
    /// (LinearRaw/RGB) or demosaic (CFA) → color transform → HDR tone-map or HDR WebP
    /// → JPEG/WebP encode → write.
    /// </summary>
    private static void RenderAndSave(DngStream stream, DngContainer container, Options opts)
    {
        // Validate HDR flag usage.
        if (opts.HdrMode && opts.JpegOutput is not null)
            throw new DngException(DngError.Unknown,
                "-hdr is not supported with -jpeg: JPEG cannot carry HDR. Use -webp -hdr instead.");

        // Build codec registry — register JXL if available.
        var registry = new CodecRegistry();
        registry.Register(new DngSharp.Dng.Sdk.Codecs.UncompressedDecoder());
        registry.Register(new DngSharp.Dng.Sdk.Codecs.DeflateDecoder());
        registry.Register(new DngSharp.Dng.Sdk.Codecs.LosslessJpeg.LosslessJpegDecoder());
        if (JxlDecoder.IsAvailable) registry.Register(new JxlDecoder());

        // 1. Decode → Stage 1 (also reads linearization + mosaic from IFD).
        Console.WriteLine("  Decoding Stage 1 ...");
        var result = StripReader.ReadStage1(stream, container, registry);
        var photometric = result.Photometric;

        // OpcodeList1 runs on the raw, unlinearized Stage-1 image, per
        // dng_negative::ReadStage1Image.
        var stage1Image = OpcodeList1Applier.Apply(result.Stage1, result.OpcodeList1);

        // Also decode the transparency mask (if the DNG has one), so it can
        // be composited against the rendered output below. Native always
        // reads this alongside the main image (dng_validate.cpp's
        // "Transparency mask read time" step) and blends masked-out pixels
        // to the background rather than showing the raw sensor content
        // there — the true photo boundary is frequently *tighter* than the
        // full raw buffer (e.g. Portrait/Depth-effect captures), and the
        // masked-out fringe can carry noisy/invalid edge pixels that
        // otherwise show up as visible "streaking".
        var maskImage = StripReader.ReadMaskImage(stream, container, registry);

        if (!Stage3Builder.CanBuild(photometric, result.Mosaic))
            throw new DngException(DngError.NotYetImplemented,
                $"Render not yet supported for photometric={photometric}. "
                + "Supported today: LinearRaw, RGB, and Bayer CFA (bilinear demosaic). "
                + "Other CFA types require Milestone B follow-up.");

        // 2. Stage 2: linearize using IFD-read parameters.
        Console.WriteLine("  Linearizing Stage 2 ...");
        var stage2 = Stage2Builder.Build(stage1Image, result.Linearization);

        // OpcodeList2 runs on the linearized Stage-2 image, per
        // dng_negative::BuildStage2Image.
        stage2 = OpcodeList2Applier.Apply(stage2, result.OpcodeList2);

        // 3. Stage 3: passthrough for LinearRaw/RGB; bilinear demosaic for CFA.
        var stage3 = Stage3Builder.Build(stage2, photometric, result.Mosaic);

        if (stage3 is not DngSharp.Dng.Sdk.Imaging.SimpleImage simpleStage3)
            throw new DngException(DngError.Unknown, "Stage3 is not a SimpleImage — unexpected image type");

        // OpcodeList3 (e.g. WarpRectilinear lens-CA correction) runs on the
        // demosaiced Stage-3 image, per dng_negative::BuildStage3Image.
        simpleStage3 = OpcodeList3Applier.Apply(simpleStage3, result.OpcodeList3);

        // Crop to the "clean" rendered-image rect. DefaultCropArea (when
        // present) is what dng_render.cpp actually uses — it's tighter than
        // ActiveArea and excludes the sensor/lens-correction edge pixels
        // (e.g. WarpRectilinear's boundary-clamped resample) that would
        // otherwise show up as streaking at the image edges. Fall back to
        // ActiveArea when DefaultCropOrigin/Size are absent.
        var renderCrop = result.DefaultCropArea ?? result.ActiveArea;
        if (renderCrop is { } cropRect)
        {
            simpleStage3 = ImageCrop.Crop(simpleStage3, cropRect);

            // Keep the mask's pixel grid aligned with Stage 3 by applying the
            // same crop. Only valid when the mask shares the raw image's
            // (uncropped) bounds — true for every sample seen so far; a
            // differently-sized mask would need native's
            // ResizeTransparencyToMatchStage3 resample, which isn't ported
            // yet, so such masks are skipped (logged, not applied) rather
            // than risking a misaligned composite.
            if (maskImage is { } m)
            {
                if (m.Bounds.Equals(result.Stage1.Bounds))
                    maskImage = ImageCrop.Crop(m, cropRect);
                else
                {
                    Console.WriteLine($"  (transparency mask size {m.Bounds.W}x{m.Bounds.H} != "
                        + $"raw {result.Stage1.Bounds.W}x{result.Stage1.Bounds.H} — resize not yet "
                        + "supported, skipping mask compositing)");
                    maskImage = null;
                }
            }
        }

        // Rather than leaving the masked-out fringe in the frame and painting
        // it white (which just replaces a "streak" border with a "white"
        // border), trim it off entirely: find the tightest rect that still
        // contains fully/partially-opaque mask pixels and crop both Stage 3
        // and the mask down to it. Any residual partial-alpha ramp pixels
        // right at the new edge are still handled by CompositeMaskAgainstWhite.
        if (maskImage is { } alignedMask)
        {
            var opaqueRect = FindOpaqueBoundingBox(alignedMask);
            if (opaqueRect is { } box && !box.Equals(alignedMask.Bounds))
            {
                Console.WriteLine($"  Cropping to transparency-mask bounds "
                    + $"({box.W}x{box.H} of {alignedMask.Bounds.W}x{alignedMask.Bounds.H}) ...");
                simpleStage3 = ImageCrop.Crop(simpleStage3, box);
                maskImage = ImageCrop.Crop(alignedMask, box);
            }
        }

        // 4. Color transform: embedded camera profile → XYZ_D50 → selected output RGB space.
        Console.WriteLine("  Applying color transform ...");
        var camToXyz = Stage3Renderer.ResolveCameraToXyzD50(result.CameraProfile, result.Shared);
        double baselineExposure = result.Shared.BaselineExposure;
        var toneCurveFunc = result.CameraProfile?.ToneCurve is { } tc
            ? (Func<double, double>)(x => HdrToneMapper.EvaluateCurve(x, tc))
            : null;

        DngSharp.Dng.Sdk.Render.HueSatMap? hueSatMap = null;
        if (result.CameraProfile is { } camProfile && camProfile.Illuminants.Count > 0)
        {
            double asShotKelvin = Stage3Renderer.EstimateAsShotKelvin(result.Shared);
            hueSatMap = DngSharp.Dng.Sdk.Render.CameraColorMatrix.ResolveHueSatMap(camProfile, asShotKelvin);
        }

        var linearRgb = Stage3Renderer.Render(
            simpleStage3,
            camToXyz,
            baselineExposure,
            toneCurve: toneCurveFunc,
            colorSpace: opts.ColorSpace,
            hueSatMap: hueSatMap);

        // 5. HDR tone-map (SDR output path only).
        if (!opts.HdrMode)
        {
            Console.WriteLine("  Tone-mapping HDR → SDR ...");
            HdrToneMapper.Apply(linearRgb, result.CameraProfile?.ToneCurve);
        }

        int w = (int)linearRgb.Bounds.W, h = (int)linearRgb.Bounds.H;

        if (opts.JpegOutput is { } jpegPath)
        {
            Console.WriteLine("  Encoding JPEG ...");
            var rgbBytes = new byte[w * h * 3];
            Stage3Renderer.GammaAndQuantize(linearRgb, rgbBytes);
            CompositeMaskAgainstWhite(rgbBytes, maskImage, w, h);
            var jpegBytes = JpegEncoder.Encode(rgbBytes, w, h);
            File.WriteAllBytes(jpegPath, jpegBytes);
            Console.WriteLine($"  → wrote {jpegBytes.Length:N0} bytes to {jpegPath}");
        }

        if (opts.WebPOutput is { } webpPath)
        {
            byte[] webpBytes;
            if (opts.HdrMode)
            {
                Console.WriteLine("  Encoding HDR WebP ...");
                var floatBytes = linearRgb.GetTile(linearRgb.Bounds).Memory.ToArray();
                webpBytes = WebPEncoder.EncodeHdr(floatBytes, w, h);
            }
            else
            {
                Console.WriteLine("  Encoding WebP ...");
                var rgbBytes = new byte[w * h * 3];
                Stage3Renderer.GammaAndQuantize(linearRgb, rgbBytes);
                CompositeMaskAgainstWhite(rgbBytes, maskImage, w, h);
                webpBytes = WebPEncoder.EncodeSdr(rgbBytes, w, h);
            }
            File.WriteAllBytes(webpPath, webpBytes);
            Console.WriteLine($"  → wrote {webpBytes.Length:N0} bytes to {webpPath}");
        }
    }

    /// <summary>
    /// Finds the smallest rect (in <paramref name="mask"/>'s own zero-origin
    /// coordinate space) that excludes any leading/trailing row or column
    /// that contains no fully-opaque pixel at all. A row/column is trimmed
    /// only while it is entirely below full opacity; as soon as a row or
    /// column contains at least one fully-opaque pixel, trimming on that
    /// edge stops. This removes the sensor-edge margin (mask == 0) *and* the
    /// narrow partial-alpha falloff ring around it (values between 0 and
    /// max) — leaving just enough that <see cref="CompositeMaskAgainstWhite"/>
    /// still has real fully-opaque pixels at the new border, so no white
    /// haze remains. Returns <see langword="null"/> if no pixel is fully
    /// opaque, or the mask type isn't supported.
    /// </summary>
    private static DngSharp.Dng.Sdk.Primitives.DngRect? FindOpaqueBoundingBox(DngSharp.Dng.Sdk.Imaging.SimpleImage mask)
    {
        int w = (int)mask.Bounds.W, h = (int)mask.Bounds.H;
        var tile = mask.GetTile(mask.Bounds);

        double maxValue = mask.PixelType switch
        {
            DngSharp.Dng.Sdk.Pixels.PixelType.UInt8 => 255.0,
            DngSharp.Dng.Sdk.Pixels.PixelType.UInt16 => 65535.0,
            DngSharp.Dng.Sdk.Pixels.PixelType.Float32 => 1.0,
            _ => 255.0,
        };
        // Small tolerance for float masks / rounding — treat "close enough to
        // max" as fully opaque.
        double opaqueThreshold = maxValue - System.Math.Max(maxValue * 1e-6, 0.5);

        bool RowHasOpaquePixel(Func<int, double> valueAt, int row)
        {
            int rowBase = row * w;
            for (int c = 0; c < w; c++)
                if (valueAt(rowBase + c) >= opaqueThreshold) return true;
            return false;
        }

        bool ColHasOpaquePixel(Func<int, double> valueAt, int col)
        {
            for (int r = 0; r < h; r++)
                if (valueAt(r * w + col) >= opaqueThreshold) return true;
            return false;
        }

        Func<int, double>? valueAt = mask.PixelType switch
        {
            DngSharp.Dng.Sdk.Pixels.PixelType.UInt8 => i => tile.AsTypedSpan<byte>()[i],
            DngSharp.Dng.Sdk.Pixels.PixelType.UInt16 => i => tile.AsTypedSpan<ushort>()[i],
            DngSharp.Dng.Sdk.Pixels.PixelType.Float32 => i => tile.AsTypedSpan<float>()[i],
            _ => null,
        };
        if (valueAt is null) return null;

        int top = 0, bottom = h, left = 0, right = w;
        while (top < bottom && !RowHasOpaquePixel(valueAt, top)) top++;
        while (bottom > top && !RowHasOpaquePixel(valueAt, bottom - 1)) bottom--;
        while (left < right && !ColHasOpaquePixel(valueAt, left)) left++;
        while (right > left && !ColHasOpaquePixel(valueAt, right - 1)) right--;

        if (top >= bottom || left >= right) return null; // no fully-opaque pixel anywhere.

        return new DngSharp.Dng.Sdk.Primitives.DngRect(top, left, bottom, right);
    }

    /// <summary>
    /// Alpha-composite an 8-bit interleaved RGB buffer against a solid white
    /// background using a single-plane transparency mask (mask value 0 =
    /// fully transparent/background, max = fully opaque/foreground). Mirrors
    /// how native flattens masked-out edge/background pixels to white rather
    /// than showing the raw (often noisy or invalid) sensor content there —
    /// see the "DNG left-edge/all-edge streak" investigation. No-ops when
    /// <paramref name="mask"/> is <see langword="null"/> or its bounds don't
    /// match <paramref name="w"/>×<paramref name="h"/> (should already be
    /// aligned by the caller's crop, but this is a defensive check).
    /// </summary>
    private static void CompositeMaskAgainstWhite(byte[] rgbBytes, DngSharp.Dng.Sdk.Imaging.SimpleImage? mask, int w, int h)
    {
        if (mask is null) return;
        if ((int)mask.Bounds.W != w || (int)mask.Bounds.H != h) return;

        Console.WriteLine("  Compositing transparency mask ...");

        var maskTile = mask.GetTile(mask.Bounds);
        double maxValue = mask.PixelType switch
        {
            DngSharp.Dng.Sdk.Pixels.PixelType.UInt8  => 255.0,
            DngSharp.Dng.Sdk.Pixels.PixelType.UInt16 => 65535.0,
            DngSharp.Dng.Sdk.Pixels.PixelType.Float16 or DngSharp.Dng.Sdk.Pixels.PixelType.Float32 => 1.0,
            _ => 255.0,
        };

        int pixelCount = w * h;

        switch (mask.PixelType)
        {
            case DngSharp.Dng.Sdk.Pixels.PixelType.UInt8:
            {
                var maskSpan = maskTile.AsTypedSpan<byte>();
                for (int i = 0; i < pixelCount; i++)
                {
                    double alpha = maskSpan[i] / maxValue;
                    BlendPixel(rgbBytes, i, alpha);
                }
                break;
            }
            case DngSharp.Dng.Sdk.Pixels.PixelType.UInt16:
            {
                var maskSpan = maskTile.AsTypedSpan<ushort>();
                for (int i = 0; i < pixelCount; i++)
                {
                    double alpha = maskSpan[i] / maxValue;
                    BlendPixel(rgbBytes, i, alpha);
                }
                break;
            }
            case DngSharp.Dng.Sdk.Pixels.PixelType.Float32:
            {
                var maskSpan = maskTile.AsTypedSpan<float>();
                for (int i = 0; i < pixelCount; i++)
                {
                    double alpha = System.Math.Clamp((double)maskSpan[i], 0.0, 1.0);
                    BlendPixel(rgbBytes, i, alpha);
                }
                break;
            }
            default:
                // Unsupported mask pixel type — skip compositing rather than
                // risk misreading the buffer.
                Console.WriteLine($"  (transparency mask pixel type {mask.PixelType} not supported — skipping)");
                break;
        }

        static void BlendPixel(byte[] rgb, int pixelIndex, double alpha)
        {
            if (alpha >= 1.0) return; // fully opaque — foreground already correct.
            int b = pixelIndex * 3;
            for (int c = 0; c < 3; c++)
                rgb[b + c] = (byte)(alpha * rgb[b + c] + (1.0 - alpha) * 255.0 + 0.5);
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine($"dng_validate (.NET port) — targets DNG spec {DngSdkInfo.SpecVersion}");
        Console.WriteLine();
        Console.WriteLine("Usage: DngSharp.Dng.Validate [options] <file.dng> [...]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  -v                    Verbose tag dump — prints every IFD entry with its decoded value.");
        Console.WriteLine("  -dng <out.dng>        Round-trip the input file through the writer (smoke test).");
        Console.WriteLine("  -1 <out.tif>          Write Stage-1 image (raw, unlinearized) to TIFF.");
        Console.WriteLine("  -2 <out.tif>          Write Stage-2 image (linearized Float32) to TIFF.");
        Console.WriteLine("  -3 <out.tif>          Write Stage-3 image (demosaiced Float32) to TIFF.");
        Console.WriteLine("  -jpeg <out.jpg>       Render to JPEG (8-bit, tone-mapped for HDR sources).");
        Console.WriteLine("  -webp <out.webp>      Render to WebP (SDR 8-bit by default; HDR F16 with -hdr).");
        Console.WriteLine("  -cs1                  Render to sRGB / Rec.709 (default).");
        Console.WriteLine("  -cs2                  Render to Adobe RGB (1998).");
        Console.WriteLine("  -cs3                  Render to ProPhoto / ROMM RGB.");
        Console.WriteLine("  -csP3                 Render to Display P3.");
        Console.WriteLine("  -cs2020               Render to Rec.2020 / BT.2020.");
        Console.WriteLine("  -hdr                  Keep HDR range in -webp output (VP8L F16). Incompatible with -jpeg.");
        Console.WriteLine("  -h, --help            Show this help.");
        Console.WriteLine();
        Console.WriteLine("Default (no flag): one-line summary per file.");
        Console.WriteLine();
        Console.WriteLine("Supported photometrics for -jpeg / -webp:");
        Console.WriteLine("  LinearRaw (e.g. iPhone ProRAW), RGB, Bayer CFA (bilinear demosaic).");
        Console.WriteLine("  Full colour-profile wiring (AsShotNeutral, ForwardMatrix) is Milestone C.");
        Console.WriteLine();
        Console.WriteLine("Not yet implemented:");
        Console.WriteLine("  -tif <file.tif>       Render to TIFF (full colour pipeline; Milestone C).");
        Console.WriteLine("  -proxy <px>           Proxy DNG.");
        Console.WriteLine("  -lossyMosaicJXL / -losslessJXL   JXL re-encode.");
    }
}
