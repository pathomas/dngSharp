using Dng.Sdk.Imaging;
using Dng.Sdk.Primitives;

namespace Dng.Sdk.Tests.Imaging;

public class TileIteratorTests
{
    [Fact]
    public void Enumerates_in_scanline_order_with_remainders()
    {
        // 5x5 area, 2x2 tiles. Expect (0,0)(0,2)(0,4)(2,0)(2,2)(2,4)(4,0)(4,2)(4,4)
        // where the right and bottom edges produce 1-wide / 1-tall remainders.
        var tiles = TileIterator.Enumerate(new DngPoint(2, 2), new DngRect(0, 0, 5, 5));
        Assert.Equal(9, tiles.Count);

        // First tile covers (0,0)-(2,2).
        Assert.Equal(new DngRect(0, 0, 2, 2), tiles[0]);

        // Last tile is the bottom-right 1x1 remainder at (4,4).
        Assert.Equal(new DngRect(4, 4, 5, 5), tiles[^1]);

        // Tiles cover the area exactly (no overlap, no gap).
        var covered = new bool[5, 5];
        foreach (var t in tiles)
            for (int r = t.T; r < t.B; r++)
                for (int c = t.L; c < t.R; c++)
                {
                    Assert.False(covered[r, c], $"({r},{c}) covered twice");
                    covered[r, c] = true;
                }
        for (int r = 0; r < 5; r++)
            for (int c = 0; c < 5; c++)
                Assert.True(covered[r, c], $"({r},{c}) not covered");
    }

    [Fact]
    public void Empty_area_yields_nothing()
    {
        var tiles = TileIterator.Enumerate(new DngPoint(4, 4), default);
        Assert.Empty(tiles);
    }

    [Fact]
    public void Tile_larger_than_area_yields_single_clipped_tile()
    {
        var tiles = TileIterator.Enumerate(new DngPoint(100, 100), new DngRect(0, 0, 10, 10));
        Assert.Single(tiles);
        Assert.Equal(new DngRect(0, 0, 10, 10), tiles[0]);
    }

    [Fact]
    public void Non_origin_area_iterates_correctly()
    {
        var tiles = TileIterator.Enumerate(new DngPoint(4, 4), new DngRect(10, 10, 14, 18));
        Assert.Equal(2, tiles.Count);
        Assert.Equal(new DngRect(10, 10, 14, 14), tiles[0]);
        Assert.Equal(new DngRect(10, 14, 14, 18), tiles[1]);
    }
}
