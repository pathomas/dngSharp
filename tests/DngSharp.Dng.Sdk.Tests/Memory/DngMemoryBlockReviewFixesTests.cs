using DngSharp.Dng.Sdk.Memory;

namespace DngSharp.Dng.Sdk.Tests.Memory;

public class DngMemoryBlockReviewFixesTests
{
    [Fact]
    public unsafe void Empty_block_pinnable_reference_is_null()
    {
        // BCL convention: `fixed (byte* p = block)` on a zero-length block
        // must yield a null pointer, not throw IndexOutOfRangeException.
        using var block = PooledMemoryAllocator.Shared.Allocate(0);
        fixed (byte* p = block)
        {
            Assert.True(p == null);
        }
    }

    [Fact]
    public unsafe void Non_empty_block_pinnable_reference_is_first_byte()
    {
        using var block = PooledMemoryAllocator.Shared.Allocate(16);
        block.Span[0] = 0xAB;
        fixed (byte* p = block)
        {
            Assert.False(p == null);
            Assert.Equal((byte)0xAB, *p);
        }
    }
}
