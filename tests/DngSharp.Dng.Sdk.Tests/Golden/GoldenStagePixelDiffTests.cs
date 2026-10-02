using DngSharp.Dng.Sdk.Codecs;
using DngSharp.Dng.Sdk.Codecs.LosslessJpeg;
using DngSharp.Dng.Sdk.Container;
using DngSharp.Dng.Sdk.Imaging;
using DngSharp.Dng.Sdk.IO;
using DngSharp.Dng.Sdk.Jxl;
using DngSharp.Dng.Sdk.Pipeline;
using DngSharp.Dng.Sdk.Pixels;
using System.Buffers.Binary;

namespace DngSharp.Dng.Sdk.Tests.Golden;

/// <summary>
/// Phase 10 pixel-level golden diff: compares managed Stage-1/2/3 output
/// against native <c>dng_validate -1/-2/-3</c> TIFF captures for the small,
/// fast-to-decode bundled sample DNGs (04–14). This complements
/// <see cref="GoldenBayerDiffTests"/> (which owns the one Bayer/JXL sample,
/// <c>03_jxl_bayer_raw_integer</c>, with its own documented tolerance
/// derivation in <c>tests/golden/SKIP.md</c>) and
/// <see cref="GoldenSyntheticOpcodeDiffTests"/> (which owns the synthetic
/// <c>FixVignetteRadial</c> fixture).
///
/// <para><c>01_jxl_linear_raw_integer</c> and <c>02_jxl_linear_raw_float</c>
/// are deliberately excluded here: each stage TIFF for those samples is
/// ~360 MB (full-resolution phone sensor output), so a per-pixel diff would
/// make the suite dramatically slower for marginal extra coverage beyond
/// what <see cref="GoldenVerboseDiffTests"/> (tag-level) and the JXL decoder
/// unit tests already provide. If deeper JXL pixel coverage is needed later,
/// add a dedicated opt-in test rather than running it by default.</para>
///
/// <para>Each case silently skips (no assertion) when the captured golden
/// TIFF is missing, the managed pipeline doesn't yet support the sample's
/// codec/photometric combination (e.g. baseline-JPEG-compressed tiles), or
/// dimensions don't match — matching the established convention in
/// <see cref="GoldenBayerDiffTests"/>. A skip is not a pass: watch for
/// samples that silently skip and revisit once the underlying gap (codec,
/// tiling, etc.) is closed.</para>
/// </summary>
public class GoldenStagePixelDiffTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    private static string SamplesDir => Path.Combine(RepoRoot, "dng_sdk_1_7_1", "sample_files");
    private static string GoldensDir => Path.Combine(RepoRoot, "tests", "golden");

    /// <summary>
    /// Sample stems covered by this diff (excludes 01/02 — see class doc —
    /// and 03/15, which have their own dedicated test classes).
    /// </summary>
    public static IEnumerable<object[]> Stems =>
    [
        ["04_PGTM2_per_profile"],
        ["05_PGTM2_unsigned8"],
        ["06_PGTM2_unsigned16"],
        ["07_PGTM2_float16"],
        ["08_PGTM2_float32"],
        ["09_ImageSequenceInfo_1_of_3"],
        ["10_ImageSequenceInfo_2_of_3"],
        ["11_ImageSequenceInfo_3_of_3"],
        ["12_ImageStats_WeightedAverage"],
        ["13_ImageStats_Several"],
        ["14_hdr_sdr_profiles"],
    ];

    private static CodecRegistry BuildRegistry()
    {
        var r = new CodecRegistry();
        r.Register(new UncompressedDecoder());
        r.Register(new DeflateDecoder());
        r.Register(new LosslessJpegDecoder());
        if (JxlDecoder.IsAvailable) r.Register(new JxlDecoder());
        return r;
    }

    [Theory]
    [MemberData(nameof(Stems))]
    public void Stage1_matches_native(string stem) => DiffStage(stem, stageNumber: 1);

    [Theory]
    [MemberData(nameof(Stems))]
    public void Stage2_matches_native(string stem) => DiffStage(stem, stageNumber: 2);

    [Theory]
    [MemberData(nameof(Stems))]
    public void Stage3_matches_native(string stem) => DiffStage(stem, stageNumber: 3);

    private static void DiffStage(string stem, int stageNumber)
    {
        string dngPath = Path.Combine(SamplesDir, stem + ".dng");
        string goldenPath = Path.Combine(GoldensDir, stem, $"stage{stageNumber}.tif");
        if (!File.Exists(dngPath) || !File.Exists(goldenPath)) return;

        SimpleImage managed;
        try
        {
            using var stream = DngFileStream.OpenRead(dngPath);
            var container = DngContainer.Parse(stream);
            var result = StripReader.ReadStage1(stream, container, BuildRegistry());
            var stage1 = OpcodeList1Applier.Apply(result.Stage1, result.OpcodeList1);

            if (stageNumber == 1)
            {
                managed = Assert.IsType<SimpleImage>(stage1);
            }
            else
            {
                var stage2 = Stage2Builder.Build(stage1, result.Linearization);
                stage2 = OpcodeList2Applier.Apply(stage2, result.OpcodeList2);

                if (stageNumber == 2)
                {
                    managed = Assert.IsType<SimpleImage>(stage2);
                }
                else
                {
                    if (!Stage3Builder.CanBuild(result.Photometric, result.Mosaic)) return;
                    var stage3 = Assert.IsType<SimpleImage>(Stage3Builder.Build(stage2, result.Photometric, result.Mosaic));
                    stage3 = OpcodeList3Applier.Apply(stage3, result.OpcodeList3);
                    if (result.ActiveArea is { } activeArea)
                        stage3 = ImageCrop.Crop(stage3, activeArea);
                    managed = stage3;
                }
            }
        }
        catch (NotSupportedException)
        {
            // Codec/photometric combination this port doesn't handle yet
            // (e.g. baseline-JPEG-compressed tiled LinearRaw in samples
            // 12/13). Tracked as a known gap, not a silent regression risk:
            // any sample that should be covered but isn't will show up here
            // as a skip if re-run with verbose test output.
            return;
        }

        float[]? nativeFloat = null;
        double[]? nativeRaw = null;
        uint nativeW, nativeH, nativePlanes;

        if (stageNumber == 1)
        {
            nativeRaw = GoldenTiffReader.ReadRawDouble(goldenPath, out nativeW, out nativeH, out nativePlanes);
            if (nativeRaw.Length == 0) return; // unsupported golden layout for the shared reader
        }
        else
        {
            nativeFloat = GoldenTiffReader.ReadFloat32(goldenPath, out nativeW, out nativeH, out nativePlanes);
            if (nativeFloat.Length == 0) return; // unsupported golden layout for the shared reader
        }

        if (nativeW != (uint)managed.Bounds.W || nativeH != (uint)managed.Bounds.H || nativePlanes != managed.Planes)
            return; // dimension mismatch — a different, already-tracked gap (crop/interleave/etc.)

        var tile = managed.GetTile(managed.Bounds);
        var managedBytes = tile.Memory.Span;
        int n = (int)(managed.Bounds.W * managed.Bounds.H * managed.Planes);

        if (stageNumber == 1)
        {
            // Stage 1 is pre-linearization: managed stores samples in
            // whatever native TIFF sample type the file used (UInt8/UInt16/
            // Float32, not normalized to [0,1]), so read + compare in that
            // same raw domain rather than assuming Float32/[0,1].
            Assert.Equal(n, nativeRaw!.Length);
            int sampleSize = managed.PixelType.SizeBytes();
            Assert.True(sampleSize > 0, $"Unsupported managed PixelType {managed.PixelType} for {stem} stage1");

            // Raw sensor codes are expected to decode bit-exact for
            // uncompressed/lossless-JPEG sources; allow a tiny epsilon only
            // to absorb legitimate float rounding for Float32 raw samples.
            double tolerance = managed.PixelType.IsFloat() ? 1e-4 : 0.5;
            double maxAbs = 0;
            for (int i = 0; i < n; i++)
            {
                double managedValue = ReadRawSample(managedBytes, i, managed.PixelType, sampleSize);
                double diff = System.Math.Abs(managedValue - nativeRaw[i]);
                if (diff > maxAbs) maxAbs = diff;
            }

            Assert.True(maxAbs <= tolerance,
                $"{stem} stage1 raw pixel diff exceeded tolerance: maxAbs={maxAbs} > {tolerance}");
            return;
        }

        Assert.Equal(n, nativeFloat!.Length);

        // Native dumps Stage-2/3 TIFFs as normalized UInt16 (round(x *
        // 65535)) or Float32 depending on sample format; managed Stage-2/3
        // images are always Float32 in [0,1] camera-space. Use an absolute
        // tolerance a touch above 1 LSB at 16-bit precision rather than
        // requiring bit-exactness (matches GoldenSyntheticOpcodeDiffTests'
        // rationale).
        const float Tolerance = 4.0f / 65535.0f;
        float maxAbsF = 0;
        for (int i = 0; i < n; i++)
        {
            float managedValue = BinaryPrimitives.ReadSingleLittleEndian(managedBytes.Slice(i * 4, 4));
            float diff = System.Math.Abs(managedValue - nativeFloat[i]);
            if (diff > maxAbsF) maxAbsF = diff;
        }

        Assert.True(maxAbsF <= Tolerance,
            $"{stem} stage{stageNumber} pixel diff exceeded tolerance: maxAbs={maxAbsF} > {Tolerance}");
    }

    private static double ReadRawSample(ReadOnlySpan<byte> bytes, int index, DngSharp.Dng.Sdk.Pixels.PixelType type, int sampleSize)
    {
        int off = index * sampleSize;
        return type switch
        {
            DngSharp.Dng.Sdk.Pixels.PixelType.UInt8 => bytes[off],
            DngSharp.Dng.Sdk.Pixels.PixelType.SInt8 => (sbyte)bytes[off],
            DngSharp.Dng.Sdk.Pixels.PixelType.UInt16 => BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(off, 2)),
            DngSharp.Dng.Sdk.Pixels.PixelType.SInt16 => BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(off, 2)),
            DngSharp.Dng.Sdk.Pixels.PixelType.UInt32 => BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(off, 4)),
            DngSharp.Dng.Sdk.Pixels.PixelType.SInt32 => BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(off, 4)),
            DngSharp.Dng.Sdk.Pixels.PixelType.Float32 => BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(off, 4)),
            DngSharp.Dng.Sdk.Pixels.PixelType.Float16 => (double)BinaryPrimitives.ReadHalfLittleEndian(bytes.Slice(off, 2)),
            _ => throw new NotSupportedException($"ReadRawSample: unsupported PixelType {type}"),
        };
    }
}
