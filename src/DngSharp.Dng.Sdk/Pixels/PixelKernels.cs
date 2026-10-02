using System.Runtime.InteropServices;
using DngSharp.Dng.Sdk.Errors;
using DngSharp.Dng.Sdk.Primitives;

namespace DngSharp.Dng.Sdk.Pixels;

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
    /// Areas, planes, and pixel types must match; layouts may differ (this is
    /// the interleaved ⇄ planar conversion point). Per plane, per row: a
    /// single <c>CopyTo</c> when both rows are contiguous, otherwise a
    /// strided typed copy.
    /// </summary>
    public static void Copy(PixelBuffer source, PixelBuffer destination)
    {
        if (source.PixelType != destination.PixelType)
            DngThrow.ProgramError($"Copy: type mismatch ({source.PixelType} -> {destination.PixelType})");
        if (source.Planes != destination.Planes)
            DngThrow.ProgramError($"Copy: plane count mismatch ({source.Planes} -> {destination.Planes})");
        if (source.Area != destination.Area)
            DngThrow.ProgramError($"Copy: area mismatch ({source.Area} -> {destination.Area})");

        if (source.Area.IsEmpty) return;

        // Same view (e.g. WriteTile(GetTile(...))) — nothing to do.
        if (source.Memory.Equals(destination.Memory)
            && source.RowStep == destination.RowStep
            && source.ColStep == destination.ColStep
            && source.PlaneStep == destination.PlaneStep
            && source.Plane == destination.Plane)
            return;

        switch (source.PixelSize)
        {
            case 1: CopyTyped<byte>(source, destination); break;
            case 2: CopyTyped<ushort>(source, destination); break;
            case 4: CopyTyped<uint>(source, destination); break;
            case 8: CopyTyped<ulong>(source, destination); break;
            default: DngThrow.ProgramError($"Copy: unsupported pixel size {source.PixelSize}"); break;
        }
    }

    private static void CopyTyped<T>(PixelBuffer source, PixelBuffer destination) where T : unmanaged
    {
        var src = MemoryMarshal.Cast<byte, T>(source.AsByteSpan());
        var dst = MemoryMarshal.Cast<byte, T>(destination.AsByteSpan());

        int w = (int)source.Area.W;
        int h = (int)source.Area.H;
        int sCol = (int)source.ColStep;
        int dCol = (int)destination.ColStep;

        for (uint p = 0; p < source.Planes; p++)
        {
            for (int r = 0; r < h; r++)
            {
                int row = source.Area.T + r;
                int sOff = (int)(source.OffsetBytes(row, source.Area.L, p) / source.PixelSize);
                int dOff = (int)(destination.OffsetBytes(row, destination.Area.L, p) / destination.PixelSize);

                if (sCol == 1 && dCol == 1)
                {
                    src.Slice(sOff, w).CopyTo(dst.Slice(dOff, w));
                    continue;
                }

                var s = src.Slice(sOff, (w - 1) * sCol + 1);
                var d = dst.Slice(dOff, (w - 1) * dCol + 1);
                for (int c = 0; c < w; c++)
                    d[c * dCol] = s[c * sCol];
            }
        }
    }

    /// <summary>
    /// Pack <paramref name="source"/> into a freshly allocated, tightly
    /// packed <b>interleaved</b> byte array (row-major, planes adjacent) —
    /// the on-disk "chunky" order expected by TIFF <c>PlanarConfiguration=1</c>
    /// and by 8-bit RGB encoders. Works for any source layout.
    /// </summary>
    public static byte[] ToInterleavedBytes(PixelBuffer source)
    {
        long total = (long)source.Area.W * source.Area.H * source.Planes * source.PixelSize;
        if (total > int.MaxValue)
            DngThrow.Overflow($"ToInterleavedBytes: {total} bytes > int.MaxValue");
        var bytes = new byte[total];
        Copy(source, PixelBuffer.Interleaved(source.Area, source.Planes, source.PixelType, bytes));
        return bytes;
    }

    /// <summary>
    /// Fill every sample with the given <typeparamref name="T"/>-typed value.
    /// </summary>
    public static void Fill<T>(PixelBuffer buffer, T value) where T : unmanaged
    {
        var span = buffer.AsTypedSpan<T>();

        // Whole-buffer views: one fill covers exactly the logical area.
        long logical = (long)buffer.Area.W * buffer.Area.H * buffer.Planes;
        if (span.Length == logical)
        {
            span.Fill(value);
            return;
        }

        // Sub-views share the parent's memory: fill only the samples inside
        // the view's area so neighbouring pixels aren't clobbered.
        int w = (int)buffer.Area.W;
        int colStep = (int)buffer.ColStep;
        for (uint p = 0; p < buffer.Planes; p++)
            for (int row = buffer.Area.T; row < buffer.Area.B; row++)
            {
                int off = (int)(buffer.OffsetBytes(row, buffer.Area.L, p) / buffer.PixelSize);
                if (colStep == 1)
                    span.Slice(off, w).Fill(value);
                else
                    for (int c = 0; c < w; c++)
                        span[off + c * colStep] = value;
            }
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
