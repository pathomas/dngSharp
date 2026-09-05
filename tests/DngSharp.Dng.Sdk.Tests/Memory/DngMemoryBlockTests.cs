using DngSharp.Dng.Sdk.Memory;

namespace DngSharp.Dng.Sdk.Tests.Memory;

public class DngMemoryBlockTests
{
    [Fact]
    public void Allocate_returns_writable_span_of_requested_size()
    {
        using var block = PooledMemoryAllocator.Shared.Allocate(64);
        Assert.Equal(64, block.LogicalSize);
        Assert.Equal(64, block.Span.Length);
        block.Span.Fill(0xAB);
        for (int i = 0; i < 64; i++) Assert.Equal(0xAB, block.Span[i]);
    }

    [Fact]
    public void Dispose_releases_buffer_to_pool()
    {
        var block = PooledMemoryAllocator.Shared.Allocate(128);
        block.Dispose();
        // Subsequent access throws.
        Assert.Throws<ObjectDisposedException>(() => _ = block.Span.Length);
    }

    [Fact]
    public void Zero_size_allocation_is_valid()
    {
        using var block = PooledMemoryAllocator.Shared.Allocate(0);
        Assert.Equal(0, block.LogicalSize);
        Assert.Equal(0, block.Span.Length);
    }
}
