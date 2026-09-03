using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Dng.Sdk.Imaging;
using Dng.Sdk.Imaging.Raw;
using Dng.Sdk.Pixels;
using Dng.Sdk.Primitives;
using Dng.Sdk.Tasks;

namespace Dng.Sdk.Pipeline;

/// <summary>
/// Bilinear Bayer demosaic. Converts a one-plane Float32 Stage-2 CFA image
/// into a three-plane Float32 Stage-3 image (R, G, B interleaved).
///
/// <para>Matches the <c>dng_mosaic_info::InterpolateGeneric</c> bilinear path
/// in <c>dng_mosaic_info.cpp</c>. Covers CFA layout 1 (rectangular 2×2 Bayer)
/// which is universal for RGGB / GRBG / BGGR / GBRG patterns. For layouts
/// 2–5 (staggered) and 6–9 (rotated/reflected) the method throws
/// <see cref="NotSupportedException"/>; those are extremely rare in practice.</para>
///
/// <para>Border pixels are clamped to the nearest in-image sample (no wrap).
/// The output image has the same bounds as the input Stage-2 image. ActiveArea
/// crop is the caller's responsibility and is deferred to the pipeline
/// orchestrator.</para>
/// </summary>
public static class DemosaicBilinear
{
    // CFA color identifiers (0=R, 1=G, 2=B)
    private const byte Red   = 0;
    private const byte Green = 1;
    private const byte Blue  = 2;

    /// <summary>
    /// Demosaic <paramref name="stage2"/> using the pattern described in
    /// <paramref name="mosaic"/>. Returns a new 3-plane Float32 image.
    /// </summary>
    public static SimpleImage Build(DngImage stage2, MosaicInfo mosaic)
    {
        ArgumentNullException.ThrowIfNull(stage2);
        ArgumentNullException.ThrowIfNull(mosaic);

        uint patRows = mosaic.Pattern.Rows;
        uint patCols = mosaic.Pattern.Cols;

        if (patRows != 2 || patCols != 2)
            throw new NotSupportedException(
                $"DemosaicBilinear: only 2×2 Bayer patterns supported; got {patRows}×{patCols}.");

        if (mosaic.CfaPlaneColor.Length < 4)
            throw new ArgumentException("MosaicInfo.CfaPlaneColor must have at least 4 entries for a 2×2 pattern.");

        var output = new SimpleImage(stage2.Bounds, 3, PixelType.Float32);
        var task = new BilinearTask(stage2, output, mosaic.CfaPlaneColor);
        AreaTaskRunner.Run(task, stage2.Bounds, sniffer: null);
        return output;
    }

    // ── IAreaTask ──────────────────────────────────────────────────────────────

    private sealed class BilinearTask(DngImage src, SimpleImage dst, byte[] pattern) : IAreaTask
    {
        // Cache the full-image source tile once to safely read border neighbours
        // across tile boundaries. This is safe because SimpleImage stores the
        // whole image in contiguous memory and GetTile just returns a view.
        private readonly PixelBuffer _srcFull = src.GetTile(src.Bounds);
        private readonly ReadOnlyMemory<byte> _srcFullBytes = src.GetTile(src.Bounds).Memory;

        // Tile size: 64 rows keeps scheduling overhead low while staying cache-friendly.
        public DngPoint MaxTileSize(DngPoint imageSize) => new(64, imageSize.H);

        public void Process(int threadIndex, DngRect tile)
        {
            // Use the full-image source buffer so border neighbours (outside the
            // current tile but inside the image) can be read without out-of-bounds.
            var srcBytes = _srcFullBytes.Span;
            var dstTile  = dst.GetTile(tile);
            var dstBytes = dstTile.Memory.Span;

            int minRow = src.Bounds.T;
            int minCol = src.Bounds.L;
            int maxRow = src.Bounds.B - 1;
            int maxCol = src.Bounds.R - 1;

            byte p00 = pattern[0], p01 = pattern[1], p10 = pattern[2], p11 = pattern[3];

            for (int row = tile.T; row < tile.B; row++)
            {
                for (int col = tile.L; col < tile.R; col++)
                {
                    float r = BilinearSample(srcBytes, row, col, Red,   p00, p01, p10, p11, minRow, minCol, maxRow, maxCol);
                    float g = BilinearSample(srcBytes, row, col, Green, p00, p01, p10, p11, minRow, minCol, maxRow, maxCol);
                    float b = BilinearSample(srcBytes, row, col, Blue,  p00, p01, p10, p11, minRow, minCol, maxRow, maxCol);

                    WritePlane(dstBytes, dstTile, row, col, 0, r);
                    WritePlane(dstBytes, dstTile, row, col, 1, g);
                    WritePlane(dstBytes, dstTile, row, col, 2, b);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private float BilinearSample(
            ReadOnlySpan<byte> srcBytes,
            int row, int col, byte targetColor,
            byte p00, byte p01, byte p10, byte p11,
            int minRow, int minCol, int maxRow, int maxCol)
        {
            int br = row & 1, bc = col & 1;
            if (pattern_at(p00, p01, p10, p11, br, bc) == targetColor)
                return ReadPixel(srcBytes, row, col);

            int h = maxRow - minRow + 1;
            int w = maxCol - minCol + 1;

            float sum = 0f, weight = 0f;
            for (int dr = -1; dr <= 1; dr++)
            {
                for (int dc = -1; dc <= 1; dc++)
                {
                    if (dr == 0 && dc == 0) continue;
                    int nr = row + dr, nc = col + dc;
                    int nbr = nr & 1, nbc = nc & 1;
                    if (pattern_at(p00, p01, p10, p11, nbr, nbc) != targetColor) continue;

                    // Border pixels: native replicates by periodically tiling the
                    // first/last CFAPatternSize (2) rows/cols of the real image
                    // (dng_image::Get with edge_repeat, phase-aligned via
                    // dng_pixel_buffer::RepeatPhase — see dng_image.cpp GetRepeat),
                    // NOT a plain single-row/col clamp. A naive clamp would reuse
                    // the wrong CFA color at the border (e.g. row -1 clamped to row
                    // 0 keeps row 0's own color, when it should read row 1's values
                    // to preserve the alternating-color parity one step further
                    // out). Wrap using period 2 (this class only supports 2×2
                    // patterns) relative to the image bounds instead.
                    int cr = minRow + WrapEdge(nr - minRow, h, 2);
                    int cc = minCol + WrapEdge(nc - minCol, w, 2);
                    float wgt = (dr != 0 && dc != 0) ? 0.25f : 0.5f;
                    sum    += ReadPixel(srcBytes, cr, cc) * wgt;
                    weight += wgt;
                }
            }
            return weight > 0f ? sum / weight : 0f;
        }

        // Periodic, phase-preserving edge wrap: for v outside [0, size), tile
        // the first/last `period` positions of the real range cyclically
        // (mirrors dng_image::Get's edge_repeat behavior for a repeating
        // CFAPatternSize block), rather than clamping to the nearest edge.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int WrapEdge(int v, int size, int period)
        {
            if (v < 0)
                return ((v % period) + period) % period;
            if (v >= size)
                return (size - period) + (((v - size) % period + period) % period);
            return v;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte pattern_at(byte p00, byte p01, byte p10, byte p11, int r, int c) =>
            (r == 0) ? (c == 0 ? p00 : p01) : (c == 0 ? p10 : p11);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private float ReadPixel(ReadOnlySpan<byte> bytes, int row, int col)
        {
            long off = _srcFull.OffsetBytes(row, col, 0);
            return BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice((int)off, 4));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WritePlane(Span<byte> bytes, PixelBuffer tile, int row, int col, uint plane, float v)
        {
            long off = tile.OffsetBytes(row, col, plane);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.Slice((int)off, 4), v);
        }
    }
}
