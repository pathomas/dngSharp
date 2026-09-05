using System.Buffers.Binary;
using DngSharp.Dng.Sdk.Imaging;
using DngSharp.Dng.Sdk.Pipeline;
using DngSharp.Dng.Sdk.Pixels;
using DngSharp.Dng.Sdk.Primitives;

namespace DngSharp.Dng.Sdk.Tests.Pipeline;

/// <summary>
/// Unit tests for <see cref="RowColumnInterleave"/>, which reverses the DNG
/// 1.7.1 <c>RowInterleaveFactor</c>/<c>ColumnInterleaveFactor</c> on-disk
/// pixel layout.
/// </summary>
public class RowColumnInterleaveTests
{
    private static void WritePixel(PixelBuffer tile, int row, int col, uint plane, float v)
    {
        long off = tile.OffsetBytes(row, col, plane);
        BinaryPrimitives.WriteSingleLittleEndian(tile.Memory.Span.Slice((int)off, 4), v);
    }

    private static float ReadPixel(PixelBuffer tile, int row, int col, uint plane)
    {
        long off = tile.OffsetBytes(row, col, plane);
        return BinaryPrimitives.ReadSingleLittleEndian(tile.Memory.Span.Slice((int)off, 4));
    }

    private static SimpleImage MakeSingleChannelImage(int w, int h, Func<int, int, float> value)
    {
        var img = new SimpleImage(new DngRect(0, 0, h, w), 1, PixelType.Float32);
        var tile = img.GetTile(img.Bounds);
        for (int row = 0; row < h; row++)
            for (int col = 0; col < w; col++)
                WritePixel(tile, row, col, 0, value(row, col));
        img.WriteTile(tile);
        return img;
    }

    [Fact]
    public void NOP_when_both_factors_are_one()
    {
        var src = MakeSingleChannelImage(4, 4, (r, c) => r * 10 + c);
        var result = RowColumnInterleave.Decode(src, 1, 1);
        Assert.Same(src, result);
    }

    [Fact]
    public void Decodes_2x2_interleaved_field_layout()
    {
        // 4x4 image, rowFactor=2 colFactor=2. On disk the four fields are
        // stored as contiguous 2x2 blocks in field order (0,0),(0,1),(1,0),(1,1):
        //  rows 0-1,cols 0-1 = field(0,0); rows 0-1,cols 2-3 = field(0,1)
        //  rows 2-3,cols 0-1 = field(1,0); rows 2-3,cols 2-3 = field(1,1)
        // Tag each source cell with its field/index so a wrong-quadrant read
        // is obvious, then verify the decoded raster matches the expected
        // mapping computed independently below.
        var src = new SimpleImage(new DngRect(0, 0, 4, 4), 1, PixelType.Float32);
        var srcTile = src.GetTile(src.Bounds);

        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 4; c++)
            {
                int fr = r / 2, fc = c / 2;      // which field block
                int ir = r % 2, ic = c % 2;      // index within the field block
                float v = fr * 200 + fc * 100 + ir * 10 + ic;
                WritePixel(srcTile, r, c, 0, v);
            }
        src.WriteTile(srcTile);

        var decoded = RowColumnInterleave.Decode(src, 2, 2);
        var dstTile = decoded.GetTile(decoded.Bounds);

        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 4; c++)
            {
                int rField = r % 2, cField = c % 2;
                int srcRow = rField * 2 + r / 2;
                int srcCol = cField * 2 + c / 2;
                int fr = srcRow / 2, fc = srcCol / 2, ir = srcRow % 2, ic = srcCol % 2;
                float expected = fr * 200 + fc * 100 + ir * 10 + ic;
                Assert.Equal(expected, ReadPixel(dstTile, r, c, 0));
            }
    }

    [Fact]
    public void Handles_non_power_of_two_dimensions_with_remainder()
    {
        // 5 rows / 2 row-factor: field 0 gets 3 rows (ceil), field 1 gets 2 rows.
        // This exercises the "H % rowFactor" remainder branch of the block-start math.
        const int w = 5, h = 5;
        var src = MakeSingleChannelImage(w, h, (r, c) => r * 100 + c);

        var decoded = RowColumnInterleave.Decode(src, 2, 2);
        var dstTile = decoded.GetTile(decoded.Bounds);

        for (int r = 0; r < h; r++)
        {
            int rField = r % 2;
            int rBlockStart = rField * (h / 2) + System.Math.Min(rField, h % 2);
            int srcRow = rBlockStart + r / 2;
            for (int c = 0; c < w; c++)
            {
                int cField = c % 2;
                int cBlockStart = cField * (w / 2) + System.Math.Min(cField, w % 2);
                int srcCol = cBlockStart + c / 2;
                float expected = srcRow * 100 + srcCol;
                Assert.Equal(expected, ReadPixel(dstTile, r, c, 0));
            }
        }
    }

    [Fact]
    public void Treats_factor_greater_than_or_equal_to_dimension_as_nop()
    {
        var src = MakeSingleChannelImage(3, 3, (r, c) => r * 10 + c);
        var result = RowColumnInterleave.Decode(src, 5, 5);
        Assert.Same(src, result);
    }
}
