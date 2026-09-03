using System.Runtime.InteropServices;
using Dng.Sdk.Errors;
using Dng.Sdk.Primitives;

namespace Dng.Sdk.Pixels;

/// <summary>
/// Reference scalar pixel-buffer kernels. Mirrors a small subset of
/// <c>dng_reference.cpp</c>. Performance-oriented SIMD specializations land
/// in later phases at point of use; this layer exists so the rest of the
/// pipeline can be wired and tested today.
///
/// <para>The intent is byte-correct, branch-clear reference behavior. Hot
/// paths (linearization, demosaic, color transform) will get vectorized
/// equivalents in Phase 5/6.</para>
/// </summary>
public static class PixelKernels
{
    /// <summary>Fill every sample in <paramref name="buffer"/> with zero.</summary>
    public static void Clear(PixelBuffer buffer)
    {
        buffer.AsByteSpan().Clear();
    }

    /// <summary>
    /// Copy from <paramref name="source"/> to <paramref name="destination"/>.
    /// Areas, planes, and pixel types must match. Falls through to per-row
    /// <c>Memory.CopyTo</c> when both buffers share an interleaved layout —
    /// otherwise per-pixel copy via offsets.
    /// </summary>
    public static void Copy(PixelBuffer source, PixelBuffer destination)
    {
        if (source.PixelType != destination.PixelType)
            DngThrow.ProgramError($"Copy: type mismatch ({source.PixelType} -> {destination.PixelType})");
        if (source.Planes != destination.Planes)
            DngThrow.ProgramError($"Copy: plane count mismatch ({source.Planes} -> {destination.Planes})");
        if (source.Area != destination.Area)
            DngThrow.ProgramError($"Copy: area mismatch ({source.Area} -> {destination.Area})");

        // Fast path: tightly packed interleaved buffers with matching layout.
        bool fast = source.ColStep == destination.ColStep
                 && source.PlaneStep == destination.PlaneStep
                 && source.ColStep == source.Planes
                 && source.PlaneStep == 1;
        if (fast)
        {
            int rowBytes = (int)source.Area.W * source.PixelSize * (int)source.Planes;
            int srcStride = (int)(source.RowStep * source.PixelSize);
            int dstStride = (int)(destination.RowStep * destination.PixelSize);
            var srcSpan = source.AsByteSpan();
            var dstSpan = destination.AsByteSpan();
            for (int r = 0; r < source.Area.H; r++)
                srcSpan.Slice(r * srcStride, rowBytes).CopyTo(dstSpan.Slice(r * dstStride, rowBytes));
            return;
        }

        // Generic path: per-sample copy.
        int pixelSize = source.PixelSize;
        for (uint p = 0; p < source.Planes; p++)
            for (int row = source.Area.T; row < source.Area.B; row++)
                for (int col = source.Area.L; col < source.Area.R; col++)
                {
                    long sOff = source.OffsetBytes(row, col, p);
                    long dOff = destination.OffsetBytes(row, col, p);
                    source.AsByteSpan().Slice((int)sOff, pixelSize)
                          .CopyTo(destination.AsByteSpan().Slice((int)dOff, pixelSize));
                }
    }

    /// <summary>
    /// Fill every sample with the given <typeparamref name="T"/>-typed value.
    /// </summary>
    public static void Fill<T>(PixelBuffer buffer, T value) where T : unmanaged
    {
        var span = buffer.AsTypedSpan<T>();
        span.Fill(value);
    }

    /// <summary>
    /// Sum all samples (interpreted as <typeparamref name="T"/>) within the
    /// buffer's logical area × planes. Walks the buffer via
    /// <see cref="PixelBuffer.OffsetBytes"/> so it works correctly for tile
    /// sub-views (which share their parent's backing memory) and for planar
    /// or arbitrary-step layouts.
    /// </summary>
    public static double Sum<T>(PixelBuffer buffer) where T : unmanaged
    {
        unsafe
        {
            if (sizeof(T) != buffer.PixelSize)
                DngThrow.ProgramError($"Sum<T>: type size {sizeof(T)} != pixel size {buffer.PixelSize}");
        }

        var bytes = buffer.Memory.Span;
        double sum = 0;
        for (uint p = 0; p < buffer.Planes; p++)
            for (int row = buffer.Area.T; row < buffer.Area.B; row++)
                for (int col = buffer.Area.L; col < buffer.Area.R; col++)
                {
                    long off = buffer.OffsetBytes(row, col, p);
                    if (typeof(T) == typeof(byte)) sum += bytes[(int)off];
                    else if (typeof(T) == typeof(ushort))
                        sum += System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice((int)off, 2));
                    else if (typeof(T) == typeof(uint))
                        sum += System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice((int)off, 4));
                    else if (typeof(T) == typeof(float))
                        sum += System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice((int)off, 4));
                    else if (typeof(T) == typeof(double))
                        sum += System.Buffers.Binary.BinaryPrimitives.ReadDoubleLittleEndian(bytes.Slice((int)off, 8));
                    else DngThrow.ProgramError($"Sum<T> not implemented for {typeof(T).Name}");
                }
        return sum;
    }
}
