using System.Buffers;

namespace Dng.Sdk.Memory;

/// <summary>
/// Lease-style allocator used for transient pixel buffers, decoder scratch, etc.
/// Mirrors C++ <c>dng_memory_allocator</c>. The default implementation pulls
/// from <see cref="ArrayPool{T}.Shared"/>; hosts can replace it (e.g. for
/// large-page allocators).
///
/// <para><b>Size limit:</b> backed by <see cref="ArrayPool{T}"/>, which itself
/// uses <see cref="int"/>-sized arrays — so any single allocation is capped at
/// <see cref="int.MaxValue"/> bytes (≈ 2 GiB). For pipeline stages that need
/// larger contiguous buffers (full-frame stage-1 raw data > 2 GiB),
/// use <see cref="PooledMemoryAllocator.AllocateLargePinned"/>, which goes to
/// the pinned object heap and tolerates the same int cap. True >2 GiB
/// allocations need native memory and are deferred to Phase 6.</para>
/// </summary>
public interface IMemoryAllocator
{
    DngMemoryBlock Allocate(int size);
}

public sealed class PooledMemoryAllocator : IMemoryAllocator
{
    public static readonly PooledMemoryAllocator Shared = new();

    public DngMemoryBlock Allocate(int size)
    {
        if (size < 0) Errors.DngThrow.Memory("negative size");
        if (size == 0) return new DngMemoryBlock([], 0, null);
        var buffer = ArrayPool<byte>.Shared.Rent(size);
        return new DngMemoryBlock(buffer, size, ArrayPool<byte>.Shared);
    }

    /// <summary>
    /// Allocate from the pinned object heap (POH) for buffers that need a
    /// stable address across GCs — useful for P/Invoke-bound payloads (e.g.
    /// libjxl strip output) and for pixel buffers used in long-running native
    /// kernels. Not pooled; the buffer is collected when its owning
    /// <see cref="DngMemoryBlock"/> is unreferenced.
    /// </summary>
    public static DngMemoryBlock AllocateLargePinned(int size)
    {
        if (size < 0) Errors.DngThrow.Memory("negative size");
        if (size == 0) return new DngMemoryBlock([], 0, null);
        var buffer = GC.AllocateUninitializedArray<byte>(size, pinned: true);
        return new DngMemoryBlock(buffer, size, pool: null);
    }
}

/// <summary>
/// Owned, disposable byte block returned by <see cref="IMemoryAllocator"/>.
/// Mirrors <c>dng_memory_block</c>. Callers must <c>using</c>-scope the
/// instance; the underlying buffer is returned to the pool on <see cref="Dispose"/>.
///
/// <para>Use <see cref="Buffer"/> for unsafe pointer-based hot paths and
/// <see cref="Span"/>/<see cref="Memory"/> for managed access.</para>
/// </summary>
public sealed class DngMemoryBlock : IDisposable
{
    private byte[] _buffer;
    private ArrayPool<byte>? _pool;
    private bool _disposed;

    public int LogicalSize { get; }

    internal DngMemoryBlock(byte[] buffer, int logicalSize, ArrayPool<byte>? pool)
    {
        _buffer = buffer;
        LogicalSize = logicalSize;
        _pool = pool;
    }

    /// <summary>The pool-backed byte array. May be larger than <see cref="LogicalSize"/>.</summary>
    public byte[] Buffer
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _buffer;
        }
    }

    public Span<byte> Span
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _buffer.AsSpan(0, LogicalSize);
        }
    }

    public Memory<byte> Memory
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _buffer.AsMemory(0, LogicalSize);
        }
    }

    public unsafe ref byte GetPinnableReference()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // BCL convention: pin to a null ref for zero-length buffers so
        // `fixed (byte* p = block)` produces a null pointer instead of
        // throwing. Mirrors Span<T>.GetPinnableReference / byte[].GetPinnableReference.
        return ref _buffer.Length == 0
            ? ref System.Runtime.CompilerServices.Unsafe.NullRef<byte>()
            : ref _buffer[0];
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_pool is not null && _buffer.Length > 0)
        {
            _pool.Return(_buffer);
        }
        _buffer = [];
        _pool = null;
    }
}
