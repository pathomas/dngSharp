using DngSharp.Dng.Sdk.Imaging;
using DngSharp.Dng.Sdk.Pixels;
using DngSharp.Dng.Sdk.Primitives;

namespace DngSharp.Dng.Sdk.Tests.Pixels;

/// <summary>
/// Layout-conversion tests for the SoA (planar) SimpleImage storage and the
/// interleaved ⇄ planar copy boundary in <see cref="PixelKernels.Copy"/>.
/// </summary>
public class PixelLayoutTests
{
    [Fact]
    public void SimpleImage_storage_is_planar()
    {
        var img = new SimpleImage(new DngRect(0, 0, 4, 6), planes: 3, PixelType.UInt16);
        var b = img.Buffer;
        Assert.True(b.HasContiguousRows);
        Assert.False(b.IsInterleaved);
        Assert.Equal(1, b.ColStep);
        Assert.Equal(6, b.RowStep);
        Assert.Equal(24, b.PlaneStep);
    }

    [Theory]
    [InlineData(PixelType.UInt8)]
    [InlineData(PixelType.UInt16)]
    [InlineData(PixelType.Float32)]
    public void Copy_interleaved_to_planar_and_back_preserves_samples(PixelType type)
    {
        var area = new DngRect(3, 5, 3 + 7, 5 + 11);
        const uint planes = 3;
        int size = type.SizeBytes();
        int n = (int)(area.W * area.H * planes);

        // Interleaved source with a distinct per-sample value.
        var srcBytes = new byte[n * size];
        var src = PixelBuffer.Interleaved(area, planes, type, srcBytes);
        var srcSpan = src.AsByteSpan();
        for (int i = 0; i < n; i++)
            srcSpan[i * size] = (byte)(i * 7 + 3);

        var img = new SimpleImage(area, planes, type);
        img.WriteTile(src);

        // Planar view must hold the value at OffsetBytes(row,col,plane).
        var planar = img.Buffer;
        var pSpan = planar.AsByteSpan();
        for (int r = 0; r < area.H; r++)
            for (int c = 0; c < area.W; c++)
                for (uint p = 0; p < planes; p++)
                {
                    int i = (int)((r * area.W + c) * planes + p);
                    long off = planar.OffsetBytes(area.T + r, area.L + c, p);
                    Assert.Equal((byte)(i * 7 + 3), pSpan[(int)off]);
                }

        // Round trip back to interleaved bytes.
        Assert.Equal(srcBytes, PixelKernels.ToInterleavedBytes(planar));
    }

    [Fact]
    public void Copy_from_subview_of_interleaved_scratch_into_edge_tile()
    {
        // Codec-style: full 4×4 coded tile, only 3×2 lands inside the image.
        var coded = new DngRect(0, 0, 4, 4);
        var dst = new DngRect(0, 0, 2, 3);
        var scratchBytes = new byte[4 * 4 * 2];
        var scratch = PixelBuffer.Interleaved(coded, 2, PixelType.UInt8, scratchBytes);
        var sb = scratch.AsByteSpan();
        for (int i = 0; i < sb.Length; i++) sb[i] = (byte)i;

        var img = new SimpleImage(dst, 2, PixelType.UInt8);
        PixelKernels.Copy(scratch.SubView(dst), img.GetTile(dst));

        var ib = img.Buffer.AsByteSpan();
        for (int r = 0; r < dst.H; r++)
            for (int c = 0; c < dst.W; c++)
                for (uint p = 0; p < 2; p++)
                {
                    byte expected = (byte)((r * 4 + c) * 2 + p);
                    Assert.Equal(expected, ib[(int)img.Buffer.OffsetBytes(r, c, p)]);
                }
    }

    [Fact]
    public void WriteTile_of_own_GetTile_view_is_a_noop()
    {
        var img = new SimpleImage(new DngRect(0, 0, 4, 4), 3, PixelType.Float32);
        var tile = img.GetTile(new DngRect(1, 1, 3, 3));
        PixelKernels.Fill<float>(tile, 2.5f);
        img.WriteTile(tile);
        Assert.Equal(2.5f * 2 * 2 * 3, PixelKernels.Sum<float>(img.Buffer), 5);
    }
}
