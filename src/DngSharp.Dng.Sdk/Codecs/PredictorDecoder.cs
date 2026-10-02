using System.Buffers;
using System.Runtime.InteropServices;
using DngSharp.Dng.Sdk.Errors;
using DngSharp.Dng.Sdk.Pixels;
using DngSharp.Dng.Sdk.Tiff;

namespace DngSharp.Dng.Sdk.Codecs;

/// <summary>
/// Undoes the TIFF <c>Predictor</c> (tag 317) transform on decoded
/// strip/tile data. Mirrors <c>DecodeDelta8/16/32</c> and
/// <c>DecodeFPDelta</c> in <c>dng_read_image.cpp</c>.
/// </summary>
public static class PredictorDecoder
{
    /// <summary>
    /// True when <paramref name="predictor"/> is one of the floating-point
    /// variants. Those operate on a raw byte stream (MSB plane first) whose
    /// order is independent of the file byte order, so the codec must be
    /// told <c>bigEndian: false</c> and the host order is restored here.
    /// </summary>
    public static bool IsFloatingPoint(Predictor predictor) =>
        predictor is Predictor.FloatingPoint or Predictor.FloatingPointX2 or Predictor.FloatingPointX4;

    /// <summary>
    /// Reverse <paramref name="predictor"/> in place on an interleaved
    /// row-major <paramref name="buffer"/>. Integer predictors expect
    /// host-order samples; floating-point predictors expect the untouched
    /// on-disk byte stream.
    /// </summary>
    public static void Undo(PixelBuffer buffer, Predictor predictor)
    {
        if (predictor == Predictor.None) return;
        if (!buffer.IsInterleaved && !(buffer.Planes == 1 && buffer.ColStep == 1))
            DngThrow.ProgramError("PredictorDecoder: buffer must be interleaved");

        int width  = (int)buffer.Area.W;
        int planes = (int)buffer.Planes;
        int size   = buffer.PixelSize;
        var mem    = buffer.Memory.Span;
        int rowBytes = width * planes * size;

        switch (predictor)
        {
            case Predictor.HorizontalDifference:
            case Predictor.HorizontalDifferenceX2:
            case Predictor.HorizontalDifferenceX4:
            {
                int scale = predictor == Predictor.HorizontalDifferenceX4 ? 4
                          : predictor == Predictor.HorizontalDifferenceX2 ? 2 : 1;
                int step = planes * scale;
                for (int row = 0; row < buffer.Area.H; row++)
                {
                    var r = mem.Slice((int)buffer.OffsetBytes(buffer.Area.T + row, buffer.Area.L), rowBytes);
                    switch (size)
                    {
                        case 1: Accumulate(r, step); break;
                        case 2: Accumulate(MemoryMarshal.Cast<byte, ushort>(r), step); break;
                        case 4: Accumulate(MemoryMarshal.Cast<byte, uint>(r), step); break;
                        default:
                            throw new DngException(DngError.NotYetImplemented,
                                $"Predictor {predictor} with {size}-byte samples");
                    }
                }
                break;
            }

            case Predictor.FloatingPoint:
            case Predictor.FloatingPointX2:
            case Predictor.FloatingPointX4:
            {
                if (size is not (2 or 4))
                    throw new DngException(DngError.NotYetImplemented,
                        $"Predictor {predictor} with {size}-byte samples");

                // Byte differencing runs with stride = channels (× X2/X4 factor),
                // as in Adobe DecodeFPDelta / libtiff horAcc8.
                int fpScale = predictor == Predictor.FloatingPointX4 ? 4
                            : predictor == Predictor.FloatingPointX2 ? 2 : 1;
                int fpStep = planes * fpScale;

                byte[] tmp = ArrayPool<byte>.Shared.Rent(rowBytes);
                try
                {
                    int samples = width * planes;
                    for (int row = 0; row < buffer.Area.H; row++)
                    {
                        var r = mem.Slice((int)buffer.OffsetBytes(buffer.Area.T + row, buffer.Area.L), rowBytes);
                        Accumulate(r, fpStep);
                        r.CopyTo(tmp);

                        // Bytes are stored plane-by-byte, most significant first;
                        // re-interleave into host order.
                        for (int i = 0; i < samples; i++)
                        {
                            int o = i * size;
                            for (int b = 0; b < size; b++)
                            {
                                int srcPlane = BitConverter.IsLittleEndian ? size - 1 - b : b;
                                r[o + b] = tmp[srcPlane * samples + i];
                            }
                        }
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(tmp);
                }
                break;
            }

            default:
                throw new DngException(DngError.NotYetImplemented, $"Predictor {predictor}");
        }
    }

    private static void Accumulate(Span<byte> row, int step)
    {
        for (int i = step; i < row.Length; i++)
            row[i] = (byte)(row[i] + row[i - step]);
    }

    private static void Accumulate(Span<ushort> row, int step)
    {
        for (int i = step; i < row.Length; i++)
            row[i] = (ushort)(row[i] + row[i - step]);
    }

    private static void Accumulate(Span<uint> row, int step)
    {
        for (int i = step; i < row.Length; i++)
            row[i] = unchecked(row[i] + row[i - step]);
    }
}
