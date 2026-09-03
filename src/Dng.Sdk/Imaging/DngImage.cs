using Dng.Sdk.Errors;
using Dng.Sdk.Pixels;
using Dng.Sdk.Primitives;

namespace Dng.Sdk.Imaging;

/// <summary>
/// Abstract base for tile-addressable images. Mirrors <c>dng_image</c>.
///
/// <para>Concrete implementations decide how pixels are stored (a single
/// contiguous buffer, an LRU tile cache backed by disk, lazy-decoded from a
/// JXL bitstream, etc.). All access goes through tile-shaped reads/writes
/// against <see cref="GetTile"/>/<see cref="WriteTile"/> so that callers can
/// be parallelized via the task model in <see cref="Tasks"/>.</para>
/// </summary>
public abstract class DngImage
{
    public DngRect Bounds { get; }
    public uint Planes { get; }
    public PixelType PixelType { get; }

    protected DngImage(DngRect bounds, uint planes, PixelType pixelType)
    {
        ArgumentOutOfRangeException.ThrowIfZero(planes);
        Bounds = bounds;
        Planes = planes;
        PixelType = pixelType;
    }

    /// <summary>Natural tile size for I/O — implementations may suggest a tiling.</summary>
    public virtual DngPoint TileSize => new((int)Bounds.H, (int)Bounds.W);

    /// <summary>Read a tile-shaped region into a new <see cref="PixelBuffer"/>.</summary>
    public abstract PixelBuffer GetTile(DngRect tile);

    /// <summary>Write a tile-shaped region from the given pixel buffer.</summary>
    public abstract void WriteTile(PixelBuffer source);
}

/// <summary>
/// Simplest <see cref="DngImage"/>: a single contiguous interleaved buffer
/// big enough to hold the whole image. Mirrors <c>dng_simple_image</c>.
/// </summary>
public sealed class SimpleImage : DngImage
{
    private readonly byte[] _data;
    private readonly PixelBuffer _whole;

    public SimpleImage(DngRect bounds, uint planes, PixelType pixelType)
        : base(bounds, planes, pixelType)
    {
        int size = pixelType.SizeBytes();
        if (size == 0) DngThrow.ProgramError($"Unsupported PixelType {pixelType}");

        long required = checked((long)bounds.W * bounds.H * planes * size);
        if (required > int.MaxValue)
            DngThrow.Overflow($"SimpleImage size {required} > int.MaxValue; use a tiled image instead");

        _data = new byte[required];
        _whole = PixelBuffer.Interleaved(bounds, planes, pixelType, _data);
    }

    /// <summary>The full-image pixel buffer. Cheaper than <see cref="GetTile"/> for whole-image ops.</summary>
    public PixelBuffer Buffer => _whole;

    public override PixelBuffer GetTile(DngRect tile)
    {
        if (!Bounds.Contains(tile))
            DngThrow.ProgramError($"Tile {tile} not contained in image bounds {Bounds}");

        // Sub-view that shares the underlying storage but reports the tile's
        // own (Area, RowStep, ColStep, PlaneStep). The byte offset is from the
        // image origin to the tile's top-left corner.
        long topLeftOffset = _whole.OffsetBytes(tile.T, tile.L);
        return new PixelBuffer
        {
            Area = tile,
            Plane = _whole.Plane,
            Planes = _whole.Planes,
            RowStep = _whole.RowStep,
            ColStep = _whole.ColStep,
            PlaneStep = _whole.PlaneStep,
            PixelType = _whole.PixelType,
            PixelSize = _whole.PixelSize,
            // Slice the backing memory so OffsetBytes(tile.T, tile.L) on the
            // sub-buffer returns 0 (because Area starts at tile.T/tile.L).
            Memory = _data.AsMemory((int)topLeftOffset),
        };
    }

    public override void WriteTile(PixelBuffer source)
    {
        if (!Bounds.Contains(source.Area))
            DngThrow.ProgramError($"Source tile {source.Area} not contained in image bounds {Bounds}");
        if (source.PixelType != PixelType)
            DngThrow.ProgramError($"PixelType mismatch: source={source.PixelType}, image={PixelType}");
        if (source.Planes != Planes)
            DngThrow.ProgramError($"Planes mismatch: source={source.Planes}, image={Planes}");

        var dst = GetTile(source.Area);
        // Both buffers are interleaved with identical layout; copy row-by-row.
        int rowBytes = (int)source.Area.W * source.PixelSize * (int)source.Planes;
        int srcRowStride = (int)(source.RowStep * source.PixelSize);
        int dstRowStride = (int)(dst.RowStep * dst.PixelSize);
        var srcSpan = source.AsByteSpan();
        var dstSpan = dst.AsByteSpan();
        for (int r = 0; r < source.Area.H; r++)
        {
            srcSpan.Slice(r * srcRowStride, rowBytes)
                   .CopyTo(dstSpan.Slice(r * dstRowStride, rowBytes));
        }
    }
}
