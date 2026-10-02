using DngSharp.Dng.Sdk.Errors;
using DngSharp.Dng.Sdk.Pixels;
using DngSharp.Dng.Sdk.Primitives;

namespace DngSharp.Dng.Sdk.Tests.Pixels;

public class PixelKernelsTests
{
    [Fact]
    public void Clear_zeroes_every_byte()
    {
        var bytes = new byte[64];
        Array.Fill(bytes, (byte)0xCC);
        var buf = PixelBuffer.Interleaved(new DngRect(0, 0, 8, 8), 1, PixelType.UInt8, bytes);
        PixelKernels.Clear(buf);
        Assert.All(bytes, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Fill_and_sum_round_trip()
    {
        var bytes = new byte[64 * 2];
        var buf = PixelBuffer.Interleaved(new DngRect(0, 0, 8, 8), 1, PixelType.UInt16, bytes);
        PixelKernels.Fill<ushort>(buf, 10);
        Assert.Equal(640.0, PixelKernels.Sum<ushort>(buf));
    }

    [Fact]
    public void Copy_interleaved_fast_path()
    {
        var srcBytes = new byte[16 * 16 * 3];
        var dstBytes = new byte[16 * 16 * 3];
        var src = PixelBuffer.Interleaved(new DngRect(0, 0, 16, 16), 3, PixelType.UInt8, srcBytes);
        var dst = PixelBuffer.Interleaved(new DngRect(0, 0, 16, 16), 3, PixelType.UInt8, dstBytes);
        for (int i = 0; i < srcBytes.Length; i++) srcBytes[i] = (byte)(i & 0xFF);
        PixelKernels.Copy(src, dst);
        Assert.Equal(srcBytes, dstBytes);
    }

    [Fact]
    public void Copy_pixeltype_mismatch_throws()
    {
        var area = new DngRect(0, 0, 8, 8);
        var src = PixelBuffer.Interleaved(area, 1, PixelType.UInt8, new byte[64]);
        var dst = PixelBuffer.Interleaved(area, 1, PixelType.UInt16, new byte[128]);
        Assert.Throws<DngException>(() => PixelKernels.Copy(src, dst));
    }
}
