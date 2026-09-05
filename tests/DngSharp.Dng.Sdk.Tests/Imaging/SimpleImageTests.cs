using DngSharp.Dng.Sdk.Imaging;
using DngSharp.Dng.Sdk.Pixels;
using DngSharp.Dng.Sdk.Primitives;

namespace DngSharp.Dng.Sdk.Tests.Imaging;

public class SimpleImageTests
{
    [Fact]
    public void Construction_initializes_to_zero()
    {
        var img = new SimpleImage(new DngRect(0, 0, 8, 16), planes: 3, PixelType.UInt8);
        var sum = PixelKernels.Sum<byte>(img.Buffer);
        Assert.Equal(0.0, sum);
    }

    [Fact]
    public void Write_then_read_round_trips_via_tile_api()
    {
        var img = new SimpleImage(new DngRect(0, 0, 32, 32), planes: 1, PixelType.UInt16);

        // Build a tile-sized source buffer (8x8 in the middle), set all samples to 0x1234.
        var tileRect = new DngRect(8, 8, 16, 16);
        var srcBytes = new byte[8 * 8 * 2];
        var src = PixelBuffer.Interleaved(tileRect, 1, PixelType.UInt16, srcBytes);
        PixelKernels.Fill<ushort>(src, 0x1234);

        img.WriteTile(src);

        // Now read back via GetTile.
        var read = img.GetTile(tileRect);
        var samples = read.AsTypedSpan<ushort>();
        // The tile-view's memory is sliced so first ushort sits at offset 0.
        Assert.Equal(0x1234, samples[0]);
        // And the rest of the image is still zero.
        var corner = img.GetTile(new DngRect(0, 0, 4, 4));
        Assert.Equal(0, PixelKernels.Sum<ushort>(corner));
    }

    [Fact]
    public void Get_tile_outside_bounds_throws()
    {
        var img = new SimpleImage(new DngRect(0, 0, 16, 16), 1, PixelType.UInt8);
        Assert.Throws<Errors.DngException>(() => img.GetTile(new DngRect(0, 0, 32, 32)));
    }

    [Fact]
    public void Write_tile_pixeltype_mismatch_throws()
    {
        var img = new SimpleImage(new DngRect(0, 0, 16, 16), 1, PixelType.UInt16);
        var srcBytes = new byte[16 * 16];
        var src = PixelBuffer.Interleaved(new DngRect(0, 0, 16, 16), 1, PixelType.UInt8, srcBytes);
        Assert.Throws<Errors.DngException>(() => img.WriteTile(src));
    }
}
