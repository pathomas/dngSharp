using DngSharp.Dng.Sdk.Primitives;

namespace DngSharp.Dng.Sdk.Imaging;

/// <summary>
/// Iterates a larger rectangle as a sequence of tile-sized sub-rectangles.
/// Mirrors <c>dng_tile_iterator</c>.
///
/// <para>Tiles are emitted in scanline order (top-to-bottom, left-to-right),
/// each clipped to the original area's bounds — the last column / row may
/// have a smaller-than-tile-size remainder. The iterator is allocation-free.</para>
/// </summary>
public ref struct TileIterator
{
    private readonly DngRect _area;
    private readonly int _tileWidth;
    private readonly int _tileHeight;
    private int _r;
    private int _c;
    private bool _done;

    public TileIterator(DngPoint tileSize, DngRect area)
    {
        if (tileSize.V <= 0 || tileSize.H <= 0)
            Errors.DngThrow.ProgramError($"TileSize must be positive, got {tileSize}");
        _area = area;
        _tileHeight = tileSize.V;
        _tileWidth = tileSize.H;
        _r = area.T;
        _c = area.L;
        _done = area.IsEmpty;
    }

    /// <summary>
    /// Yields the next tile. Returns true when a tile is produced, false when
    /// iteration completes. Typical usage:
    /// <code>
    /// var it = new TileIterator(image.TileSize, image.Bounds);
    /// while (it.Next(out var tile)) { ... }
    /// </code>
    /// </summary>
    public bool Next(out DngRect tile)
    {
        if (_done)
        {
            tile = default;
            return false;
        }

        int t = _r;
        int l = _c;
        int b = System.Math.Min(t + _tileHeight, _area.B);
        int r = System.Math.Min(l + _tileWidth, _area.R);
        tile = new DngRect(t, l, b, r);

        _c += _tileWidth;
        if (_c >= _area.R)
        {
            _c = _area.L;
            _r += _tileHeight;
            if (_r >= _area.B) _done = true;
        }
        return true;
    }

    /// <summary>
    /// Materializes all tiles into a list. Convenience for tests and the
    /// parallel area-task runner.
    /// </summary>
    public static List<DngRect> Enumerate(DngPoint tileSize, DngRect area)
    {
        var list = new List<DngRect>();
        var it = new TileIterator(tileSize, area);
        while (it.Next(out var t)) list.Add(t);
        return list;
    }
}
