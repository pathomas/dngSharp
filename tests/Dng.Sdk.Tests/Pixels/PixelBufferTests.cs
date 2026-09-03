using Dng.Sdk.Errors;
using Dng.Sdk.Pixels;
using Dng.Sdk.Primitives;

namespace Dng.Sdk.Tests.Pixels;

public class PixelBufferTests
{
    [Fact]
    public void Interleaved_layout_steps()
    {
        var area = new DngRect(0, 0, 10, 20);  // h=10, w=20
        var buf = PixelBuffer.Interleaved(area, planes: 3, PixelType.UInt16, new byte[10 * 20 * 3 * 2]);
        Assert.Equal(20L * 3, buf.RowStep);     // w * planes samples per row
        Assert.Equal(3L, buf.ColStep);          // planes per pixel
        Assert.Equal(1L, buf.PlaneStep);        // adjacent planes are interleaved
        Assert.Equal(2, buf.PixelSize);
    }

    [Fact]
    public void Planar_layout_steps()
    {
        var area = new DngRect(0, 0, 10, 20);
        var buf = PixelBuffer.Planar(area, planes: 3, PixelType.UInt16, new byte[10 * 20 * 3 * 2]);
        Assert.Equal(20L, buf.RowStep);         // w samples per row (one plane)
        Assert.Equal(1L, buf.ColStep);
        Assert.Equal(20L * 10, buf.PlaneStep);  // skip a whole plane
    }

    [Fact]
    public void OffsetBytes_interleaved_first_pixel_is_zero()
    {
        var area = new DngRect(0, 0, 10, 20);
        var buf = PixelBuffer.Interleaved(area, planes: 3, PixelType.UInt8, new byte[10 * 20 * 3]);
        Assert.Equal(0L, buf.OffsetBytes(0, 0));
    }

    [Fact]
    public void OffsetBytes_interleaved_row_one_col_one_plane_one()
    {
        // 16x16 RGB uint8: at (row=1, col=1, plane=1), offset = (16*3*1) + (3*1) + (1*1) = 52 bytes.
        var area = new DngRect(0, 0, 16, 16);
        var buf = PixelBuffer.Interleaved(area, planes: 3, PixelType.UInt8, new byte[16 * 16 * 3]);
        Assert.Equal(52L, buf.OffsetBytes(1, 1, 1));
    }

    [Fact]
    public void Buffer_too_small_throws()
    {
        var area = new DngRect(0, 0, 10, 20);
        Assert.Throws<DngException>(() =>
            PixelBuffer.Interleaved(area, planes: 3, PixelType.UInt8, new byte[10]));
    }

    [Fact]
    public void AsTypedSpan_size_mismatch_throws()
    {
        var area = new DngRect(0, 0, 4, 4);
        var buf = PixelBuffer.Interleaved(area, planes: 1, PixelType.UInt8, new byte[16]);
        Assert.Throws<DngException>(() => buf.AsTypedSpan<ushort>());
    }

    [Fact]
    public void AsTypedSpan_correct_type_returns_writable_view()
    {
        var area = new DngRect(0, 0, 4, 4);
        var memory = new byte[16 * 2];
        var buf = PixelBuffer.Interleaved(area, planes: 1, PixelType.UInt16, memory);
        var span = buf.AsTypedSpan<ushort>();
        Assert.Equal(16, span.Length);
        span[0] = 0xBEEF;
        // Verify the underlying bytes (little-endian on x64).
        Assert.Equal((byte)0xEF, memory[0]);
        Assert.Equal((byte)0xBE, memory[1]);
    }
}
