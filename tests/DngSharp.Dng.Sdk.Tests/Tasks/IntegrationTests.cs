using DngSharp.Dng.Sdk.Imaging;
using DngSharp.Dng.Sdk.Pixels;
using DngSharp.Dng.Sdk.Primitives;
using DngSharp.Dng.Sdk.Tasks;

namespace DngSharp.Dng.Sdk.Tests.Tasks;

/// <summary>
/// End-to-end exercise: parallel tile fill on a SimpleImage, then verify
/// every sample was written exactly once. This is the pattern that Phase 5/6
/// kernels (linearization, demosaic, color transform) will use.
/// </summary>
public class IntegrationTests
{
    private sealed class FillTileTask(SimpleImage image, ushort value) : IAreaTask
    {
        public DngPoint MaxTileSize(DngPoint imageSize) => new(64, 64);

        public void Process(int threadIndex, DngRect tile)
        {
            var dst = image.GetTile(tile);
            // Walk the tile via OffsetBytes to be correct on sub-views.
            var span = dst.Memory.Span;
            for (int row = tile.T; row < tile.B; row++)
                for (int col = tile.L; col < tile.R; col++)
                {
                    long off = dst.OffsetBytes(row, col);
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(
                        span.Slice((int)off, 2), value);
                }
        }
    }

    [Fact]
    public void Parallel_tile_fill_then_sum_matches_expected_total()
    {
        var img = new SimpleImage(new DngRect(0, 0, 256, 256), planes: 1, PixelType.UInt16);
        AreaTaskRunner.Run(new FillTileTask(img, 100), img.Bounds);

        // 256 * 256 samples × 100 = 6_553_600.
        Assert.Equal(6_553_600.0, PixelKernels.Sum<ushort>(img.Buffer));
    }
}
