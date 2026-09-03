using System.Runtime.CompilerServices;
using Dng.Sdk.Errors;
using Dng.Sdk.Math;
using Dng.Sdk.Primitives;
using Dng.Sdk.Tiff;

namespace Dng.Sdk.Pixels;

/// <summary>
/// A view over a region of pixel data. Mirrors <c>dng_pixel_buffer</c>.
///
/// <para>A pixel buffer carries:
///   <list type="bullet">
///     <item><see cref="Area"/> — the pixel rectangle the buffer covers</item>
///     <item><see cref="Plane"/>/<see cref="Planes"/> — the contiguous range of planes</item>
///     <item><see cref="RowStep"/>/<see cref="ColStep"/>/<see cref="PlaneStep"/> —
///         step counts (<b>in samples, not bytes</b>) between rows/cols/planes</item>
///     <item><see cref="PixelType"/> + <see cref="PixelSize"/> — sample storage</item>
///     <item><see cref="Memory"/> — the backing bytes, owned by the caller</item>
///   </list>
/// </para>
///
/// <para>Step counts are stored as sample counts (not bytes) to match the C++
/// helper API; multiply by <see cref="PixelSize"/> when computing a byte
/// offset. <see cref="OffsetBytes"/> hides that conversion.</para>
///
/// <para>This is a struct so it can be passed by value across kernel APIs
/// without allocating; copies are cheap. Mutability is the caller's
/// responsibility — pixel buffers are typically immutable from the consumer's
/// point of view but writable by the producer that owns the backing memory.</para>
/// </summary>
public readonly struct PixelBuffer
{
    public DngRect Area { get; init; }
    public uint Plane { get; init; }
    public uint Planes { get; init; }

    /// <summary>Step from one row to the next, in <b>samples</b>.</summary>
    public long RowStep { get; init; }
    /// <summary>Step from one column to the next, in <b>samples</b> (1 for interleaved, planes for packed planar).</summary>
    public long ColStep { get; init; }
    /// <summary>Step from one plane to the next, in <b>samples</b>.</summary>
    public long PlaneStep { get; init; }

    public PixelType PixelType { get; init; }
    public int PixelSize { get; init; }

    /// <summary>Backing storage. The buffer's first sample is at byte offset 0 of this memory.</summary>
    public Memory<byte> Memory { get; init; }

    public bool Dirty { get; init; }

    /// <summary>
    /// Build a tightly-packed interleaved buffer for the given area/planes.
    /// Throws <see cref="DngException"/>(<see cref="DngError.Overflow"/>) if
    /// the required size would exceed <see cref="int.MaxValue"/> bytes.
    /// </summary>
    public static PixelBuffer Interleaved(DngRect area, uint planes, PixelType type, Memory<byte> memory)
    {
        ArgumentOutOfRangeException.ThrowIfZero(planes);
        int size = type.SizeBytes();
        if (size == 0) DngThrow.ProgramError($"Unsupported PixelType {type}");

        // Interleaved: col-step = planes (samples), row-step = w * planes.
        long w = area.W;
        long h = area.H;
        long required = checked(w * h * planes * size);
        if (required > int.MaxValue)
            DngThrow.Overflow($"Pixel buffer size {required} exceeds int.MaxValue");
        if (memory.Length < required)
            DngThrow.ProgramError($"Backing memory too small ({memory.Length} < {required})");

        return new PixelBuffer
        {
            Area = area,
            Plane = 0,
            Planes = planes,
            RowStep = w * planes,
            ColStep = planes,
            PlaneStep = 1,
            PixelType = type,
            PixelSize = size,
            Memory = memory,
        };
    }

    /// <summary>
    /// Build a planar buffer (each plane is a contiguous region; planes are
    /// stacked one after another in memory).
    /// </summary>
    public static PixelBuffer Planar(DngRect area, uint planes, PixelType type, Memory<byte> memory)
    {
        ArgumentOutOfRangeException.ThrowIfZero(planes);
        int size = type.SizeBytes();
        if (size == 0) DngThrow.ProgramError($"Unsupported PixelType {type}");

        long w = area.W;
        long h = area.H;
        long required = checked(w * h * planes * size);
        if (required > int.MaxValue)
            DngThrow.Overflow($"Pixel buffer size {required} exceeds int.MaxValue");
        if (memory.Length < required)
            DngThrow.ProgramError($"Backing memory too small ({memory.Length} < {required})");

        return new PixelBuffer
        {
            Area = area,
            Plane = 0,
            Planes = planes,
            RowStep = w,
            ColStep = 1,
            PlaneStep = w * h,
            PixelType = type,
            PixelSize = size,
            Memory = memory,
        };
    }

    /// <summary>
    /// Byte offset into <see cref="Memory"/> for pixel at
    /// (<paramref name="row"/>, <paramref name="col"/>) on
    /// <paramref name="plane"/>. Mirrors the disabled bounds-check path in
    /// C++ <c>dng_pixel_buffer::InternalPixel</c> — callers may pass coords
    /// outside <see cref="Area"/> (edge padding patterns).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long OffsetBytes(int row, int col, uint plane = 0) =>
        (long)PixelSize *
        (RowStep * (row - (long)Area.T) +
         ColStep * (col - (long)Area.L) +
         PlaneStep * ((long)plane - Plane));

    /// <summary>
    /// Raw backing memory. <b>This is the entire underlying byte array, not
    /// just the buffer's logical extent.</b> Tile sub-views share memory with
    /// their parent image, so for them this span runs from the tile origin
    /// to the end of the parent. Iterate samples via
    /// <see cref="OffsetBytes"/> when correctness across sub-views matters.
    /// </summary>
    public Span<byte> AsByteSpan() => Memory.Span;

    /// <summary>
    /// Typed view over <see cref="AsByteSpan"/>; throws if the pixel type
    /// doesn't match <typeparamref name="T"/>'s size. Same sub-view caveat as
    /// <see cref="AsByteSpan"/> — safe for tightly-packed root buffers, but
    /// for tile sub-views you should iterate via <see cref="OffsetBytes"/>.
    /// </summary>
    public unsafe Span<T> AsTypedSpan<T>() where T : unmanaged
    {
        if (sizeof(T) != PixelSize)
            DngThrow.ProgramError($"Type {typeof(T).Name} size {sizeof(T)} doesn't match PixelSize {PixelSize}");
        var bytes = AsByteSpan();
        return System.Runtime.InteropServices.MemoryMarshal.Cast<byte, T>(bytes);
    }
}
