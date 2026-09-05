using System.Numerics;
using System.Runtime.InteropServices;
using DngSharp.Dng.Sdk.Errors;
using DngSharp.Dng.Sdk.Imaging;
using DngSharp.Dng.Sdk.Imaging.Raw;
using DngSharp.Dng.Sdk.Pixels;
using DngSharp.Dng.Sdk.Primitives;
using DngSharp.Dng.Sdk.Tasks;

namespace DngSharp.Dng.Sdk.Pipeline;

/// <summary>
/// Stage 1 → Stage 2 linearization. Mirrors the relevant body of
/// <c>dng_negative::BuildStage2Image</c>.
///
/// <para>Steps (spec ch. 5):
/// <list type="number">
///   <item>Apply <c>LinearizationTable</c> LUT if present (look up each sample).</item>
///   <item>Subtract per-plane black level (with optional per-row/column deltas).</item>
///   <item>Rescale to [0, 1] using <c>WhiteLevel - BlackLevel</c>.</item>
///   <item>Clip values above 1.0 (sub-zero values pass through — preserved
///         for later pipeline stages per spec).</item>
/// </list>
/// </para>
///
/// <para>OpcodeList1 (run before linearization) and OpcodeList2 (run after)
/// are dispatched separately by the orchestrator; this kernel does only the
/// numeric transform.</para>
/// </summary>
public static class Stage2Builder
{
    /// <summary>
    /// Build a stage-2 image from a stage-1 image and the negative's
    /// linearization parameters. The result is always
    /// <see cref="PixelType.Float32"/> (since the post-rescale value range is
    /// floating-point [0, 1]).
    /// </summary>
    public static SimpleImage Build(
        DngImage stage1,
        LinearizationInfo lin,
        DngHost? host = null)
    {
        ArgumentNullException.ThrowIfNull(stage1);
        ArgumentNullException.ThrowIfNull(lin);

        if (lin.BlackLevel.Length == 0)
            DngThrow.ProgramError("Stage2Builder: BlackLevel is empty");
        if (lin.WhiteLevel.Length == 0)
            DngThrow.ProgramError("Stage2Builder: WhiteLevel is empty");

        var stage2 = new SimpleImage(stage1.Bounds, stage1.Planes, PixelType.Float32);

        // Pre-compute per-plane scale = 1 / (WhiteLevel - EffectiveBlack).
        // When BlackLevelRepeatDim > (1,1), multiple black entries cover the
        // 2×2 pattern (e.g. RGGB). Use the minimum black across all pattern
        // cells to determine the scale — the per-pixel lookup in the task
        // then subtracts the exact per-pixel black. This ensures scale is
        // computed from the correct (white - minBlack) span.
        var planeScale = new double[stage1.Planes];
        bool hasRepeat = lin.BlackLevelRepeatDim.Rows > 1 || lin.BlackLevelRepeatDim.Cols > 1;
        for (uint p = 0; p < stage1.Planes; p++)
        {
            double white = lin.WhiteLevel[(int)System.Math.Min(p, (uint)lin.WhiteLevel.Length - 1)];
            double minBlack;
            if (hasRepeat)
            {
                minBlack = lin.BlackLevel[0];
                for (int i = 1; i < lin.BlackLevel.Length; i++)
                    if (lin.BlackLevel[i] < minBlack) minBlack = lin.BlackLevel[i];
            }
            else
            {
                minBlack = lin.BlackLevel[(int)System.Math.Min(p, (uint)lin.BlackLevel.Length - 1)];
            }

            double span = white - minBlack;
            if (span <= 0)
                DngThrow.BadFormat($"Linearization: white ({white}) <= black ({minBlack}) on plane {p}");
            planeScale[p] = 1.0 / span;
        }

        var task = new LinearizationTask(stage1, stage2, lin, planeScale, host?.MaxTileEdgePixels ?? 256);
        AreaTaskRunner.Run(task, stage1.Bounds, host?.Sniffer);

        return stage2;
    }

    private sealed class LinearizationTask(
        DngImage src,
        SimpleImage dst,
        LinearizationInfo lin,
        double[] planeScale,
        int tileEdge) : IAreaTask
    {
        public DngPoint MaxTileSize(DngPoint imageSize) => new(tileEdge, tileEdge);

        public void Process(int threadIndex, DngRect tile)
        {
            var srcTile = src.GetTile(tile);
            var dstTile = dst.GetTile(tile);
            var srcBytes = srcTile.Memory.Span;
            var dstBytes = dstTile.Memory.Span;
            var lut = lin.LinearizationTable;

            uint repeatRows = lin.BlackLevelRepeatDim.Rows;
            uint repeatCols = lin.BlackLevelRepeatDim.Cols;
            bool hasRepeat  = repeatRows > 1 || repeatCols > 1;

            // Fast path: single-plane mosaic data (the common Bayer CFA case)
            // with no LUT, no repeating black-level pattern, and no per-row/
            // column deltas — i.e. a single constant (black, scale) pair for
            // the whole tile. Under those conditions the per-plane samples
            // are contiguous in memory (interleaved layout with Planes==1 is
            // just a flat row), so we can batch the multiply-add + top-clip
            // across a full row with <see cref="Vector{Single}"/> instead of
            // one scalar op per pixel. Anything else (LUT lookup, repeating
            // black pattern, per-row/col deltas, multi-plane images) falls
            // back to the general scalar path below.
            if (Vector.IsHardwareAccelerated
                && src.Planes == 1
                && lut is null
                && !hasRepeat
                && lin.BlackLevelDeltaH is null
                && lin.BlackLevelDeltaV is null
                && src.PixelType is PixelType.UInt8 or PixelType.SInt8 or PixelType.UInt16
                    or PixelType.SInt16 or PixelType.UInt32 or PixelType.Float32)
            {
                float black = (float)lin.BlackLevel[0];
                float scale = (float)planeScale[0];
                int count = (int)(tile.R - tile.L);

                for (int row = tile.T; row < tile.B; row++)
                {
                    long srcRowOff = srcTile.OffsetBytes(row, tile.L, 0);
                    long dstRowOff = dstTile.OffsetBytes(row, tile.L, 0);
                    ProcessRowSimd(srcBytes, srcRowOff, dstBytes, dstRowOff, count, src.PixelType, black, scale);
                }

                return;
            }

            for (uint p = 0; p < src.Planes; p++)
            {
                // Per-plane scale (never changes within a plane).
                double scale = planeScale[p];

                for (int row = tile.T; row < tile.B; row++)
                {
                    double rowDelta = lin.BlackLevelDeltaV is { } dv && row < dv.Length ? dv[row] : 0;
                    for (int col = tile.L; col < tile.R; col++)
                    {
                        double colDelta = lin.BlackLevelDeltaH is { } dh && col < dh.Length ? dh[col] : 0;

                        // Black level: when BlackLevelRepeatDim > (1,1) the black-level
                        // table tiles across the sensor (e.g. 2×2 for RGGB Bayer, giving
                        // separate biases for R, G1, G2, B). Index = repeatRow * repeatCols + repeatCol.
                        double black;
                        if (hasRepeat)
                        {
                            uint rr  = (uint)(row - src.Bounds.T) % repeatRows;
                            uint rc  = (uint)(col - src.Bounds.L) % repeatCols;
                            uint idx = rr * repeatCols + rc;
                            black = lin.BlackLevel[System.Math.Min((int)idx, lin.BlackLevel.Length - 1)];
                        }
                        else
                        {
                            black = lin.BlackLevel[(int)System.Math.Min(p, (uint)lin.BlackLevel.Length - 1)];
                        }

                        long srcOff = srcTile.OffsetBytes(row, col, p);
                        long dstOff = dstTile.OffsetBytes(row, col, p);

                        // 1. Read raw sample.
                        double sample = ReadSample(srcBytes, srcOff, src.PixelType);

                        // 2. Optional LUT (applies before black subtract, per spec ch. 5).
                        if (lut is not null)
                        {
                            int idx = (int)System.Math.Clamp(sample, 0, lut.Length - 1);
                            sample = lut[idx];
                        }

                        // 3. Black subtract (rowDelta + colDelta are additive per-pixel corrections).
                        double linear = sample - (black + rowDelta + colDelta);

                        // 4. Rescale to [0, 1].
                        linear *= scale;

                        // 5. Clip top (preserve sub-zero per spec).
                        if (linear > 1.0) linear = 1.0;

                        // Write Float32.
                        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(
                            dstBytes.Slice((int)dstOff, 4), (float)linear);
                    }
                }
            }
        }

        // Bounded block size for the stack-allocated de-widen buffer — keeps
        // stack usage small (1 KiB) regardless of image row width.
        private const int SimdBlockSize = 256;

        /// <summary>
        /// Vectorized (sample - black) * scale, clipped above 1.0, for a
        /// contiguous run of <paramref name="count"/> single-plane samples.
        /// Processes <see cref="Vector{Single}"/>-wide batches (which the JIT
        /// maps to the widest available SIMD ISA — SSE/AVX2/AVX-512 on x86,
        /// NEON/SVE on Arm) with a scalar remainder for the tail.
        /// </summary>
        private static void ProcessRowSimd(
            ReadOnlySpan<byte> srcBytes, long srcRowOffsetBytes,
            Span<byte> dstBytes, long dstRowOffsetBytes,
            int count, PixelType srcType, float black, float scale)
        {
            Span<float> buf = stackalloc float[SimdBlockSize];
            int srcSize = srcType.SizeBytes();
            int vw = Vector<float>.Count;
            var blackVec = new Vector<float>(black);
            var scaleVec = new Vector<float>(scale);
            var oneVec = Vector<float>.One;

            int done = 0;
            while (done < count)
            {
                int chunk = System.Math.Min(SimdBlockSize, count - done);
                var srcChunk = srcBytes.Slice((int)srcRowOffsetBytes + done * srcSize, chunk * srcSize);
                ReadChunkToFloat(srcChunk, buf[..chunk], srcType);

                var dstChunk = MemoryMarshal.Cast<byte, float>(
                    dstBytes.Slice((int)(dstRowOffsetBytes + done * 4L), chunk * 4));

                int k = 0;
                for (; k + vw <= chunk; k += vw)
                {
                    var v = new Vector<float>(buf.Slice(k, vw));
                    var result = Vector.Min((v - blackVec) * scaleVec, oneVec);
                    result.CopyTo(dstChunk.Slice(k, vw));
                }

                for (; k < chunk; k++)
                {
                    float r = (buf[k] - black) * scale;
                    dstChunk[k] = r > 1f ? 1f : r;
                }

                done += chunk;
            }
        }

        /// <summary>Widen a contiguous chunk of raw samples to <see cref="float"/>.</summary>
        private static void ReadChunkToFloat(ReadOnlySpan<byte> src, Span<float> dst, PixelType type)
        {
            switch (type)
            {
                case PixelType.UInt8:
                    for (int i = 0; i < dst.Length; i++) dst[i] = src[i];
                    break;
                case PixelType.SInt8:
                    for (int i = 0; i < dst.Length; i++) dst[i] = (sbyte)src[i];
                    break;
                case PixelType.UInt16:
                {
                    var s = MemoryMarshal.Cast<byte, ushort>(src);
                    for (int i = 0; i < dst.Length; i++) dst[i] = s[i];
                    break;
                }
                case PixelType.SInt16:
                {
                    var s = MemoryMarshal.Cast<byte, short>(src);
                    for (int i = 0; i < dst.Length; i++) dst[i] = s[i];
                    break;
                }
                case PixelType.UInt32:
                {
                    var s = MemoryMarshal.Cast<byte, uint>(src);
                    for (int i = 0; i < dst.Length; i++) dst[i] = s[i];
                    break;
                }
                case PixelType.Float32:
                    MemoryMarshal.Cast<byte, float>(src).CopyTo(dst);
                    break;
                default:
                    DngThrow.ProgramError($"Stage2 SIMD path: unsupported PixelType {type}");
                    break;
            }
        }

        private static double ReadSample(ReadOnlySpan<byte> bytes, long off, PixelType type) => type switch
        {
            PixelType.UInt8   => bytes[(int)off],
            PixelType.SInt8   => (sbyte)bytes[(int)off],
            PixelType.UInt16  => System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice((int)off, 2)),
            PixelType.SInt16  => System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice((int)off, 2)),
            PixelType.UInt32  => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice((int)off, 4)),
            PixelType.Float32 => System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice((int)off, 4)),
            PixelType.Float16 => (double)(Half)System.Buffers.Binary.BinaryPrimitives.ReadHalfLittleEndian(bytes.Slice((int)off, 2)),
            _ => throw new DngException(DngError.NotYetImplemented, $"Stage2: unsupported PixelType {type}"),
        };
    }
}
