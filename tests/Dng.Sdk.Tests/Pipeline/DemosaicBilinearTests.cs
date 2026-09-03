using Dng.Sdk.Imaging;
using Dng.Sdk.Imaging.Raw;
using Dng.Sdk.Pipeline;
using Dng.Sdk.Pixels;
using Dng.Sdk.Primitives;

namespace Dng.Sdk.Tests.Pipeline;

/// <summary>
/// Unit tests for <see cref="DemosaicBilinear"/> and
/// <see cref="Stage3Builder"/> CFA dispatch.
/// </summary>
public class DemosaicBilinearTests
{
    // RGGB 2×2 CFA pattern.
    private static MosaicInfo RggbMosaic => new()
    {
        Pattern = (2, 2),
        CfaPlaneColor = [0, 1, 1, 2], // R, Gr, Gb, B
    };

    // GRBG 2×2 pattern.
    private static MosaicInfo GrbgMosaic => new()
    {
        Pattern = (2, 2),
        CfaPlaneColor = [1, 0, 2, 1], // Gr, R, B, Gb
    };

    private static SimpleImage MakeRggbBayer(int w, int h, float r, float g, float b)
    {
        // Fill a synthetic 1-plane Float32 Bayer image.
        // Pixel (row, col) carries the Bayer channel's value:
        //   (even, even)=R, (even, odd)=Gr, (odd, even)=Gb, (odd, odd)=B
        var img = new SimpleImage(new DngRect(0, 0, h, w), 1, PixelType.Float32);
        var tile = img.GetTile(img.Bounds);
        for (int row = 0; row < h; row++)
            for (int col = 0; col < w; col++)
            {
                float v = (row % 2 == 0 && col % 2 == 0) ? r :
                          (row % 2 == 0 && col % 2 == 1) ? g :
                          (row % 2 == 1 && col % 2 == 0) ? g : b;
                long off = tile.OffsetBytes(row, col, 0);
                System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(
                    tile.Memory.Span.Slice((int)off, 4), v);
            }
        img.WriteTile(tile);
        return img;
    }

    private static float ReadPlane(SimpleImage img, int row, int col, uint plane)
    {
        var tile = img.GetTile(img.Bounds);
        long off = tile.OffsetBytes(row, col, plane);
        return System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(
            tile.Memory.Span.Slice((int)off, 4));
    }

    [Fact]
    public void Demosaic_uniform_grey_produces_equal_planes()
    {
        // When R=G=B=0.5 everywhere the demosaiced output must be (0.5, 0.5, 0.5).
        var bayer = MakeRggbBayer(8, 8, 0.5f, 0.5f, 0.5f);
        var result = DemosaicBilinear.Build(bayer, RggbMosaic);

        Assert.Equal(3u, result.Planes);
        Assert.Equal(PixelType.Float32, result.PixelType);

        // Check a centre pixel (not affected by border clamping).
        for (uint p = 0u; p < 3u; p++)
            Assert.InRange(ReadPlane(result, 4, 4, p), 0.499f, 0.501f);
    }

    [Fact]
    public void Demosaic_pure_red_channel_propagates_to_red_plane()
    {
        // All R=1.0, G=0.0, B=0.0.
        var bayer = MakeRggbBayer(8, 8, r: 1.0f, g: 0.0f, b: 0.0f);
        var result = DemosaicBilinear.Build(bayer, RggbMosaic);

        // The R plane should be approximately 1 at centre; G and B should be ~0.
        Assert.InRange(ReadPlane(result, 4, 4, 0), 0.9f, 1.01f);
        Assert.InRange(ReadPlane(result, 4, 4, 1), 0.0f, 0.1f);
        Assert.InRange(ReadPlane(result, 4, 4, 2), 0.0f, 0.1f);
    }

    [Fact]
    public void Demosaic_grbg_pattern_handles_plane_offsets()
    {
        // GRBG: (0,0)=G, (0,1)=R, (1,0)=B, (1,1)=G.
        // Build Bayer with R=1, G=0, B=0.
        var img = new SimpleImage(new DngRect(0, 0, 8, 8), 1, PixelType.Float32);
        var tile = img.GetTile(img.Bounds);
        for (int row = 0; row < 8; row++)
            for (int col = 0; col < 8; col++)
            {
                // GRBG at (0,1): R pixel
                float v = (row % 2 == 0 && col % 2 == 1) ? 1.0f : 0.0f;
                long off = tile.OffsetBytes(row, col, 0);
                System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(
                    tile.Memory.Span.Slice((int)off, 4), v);
            }
        img.WriteTile(tile);

        var result = DemosaicBilinear.Build(img, GrbgMosaic);

        // R plane should be near 1; G and B near 0 in the interior.
        Assert.InRange(ReadPlane(result, 4, 4, 0), 0.9f, 1.01f);
    }

    [Fact]
    public void Demosaic_output_bounds_match_input_bounds()
    {
        var bayer = MakeRggbBayer(10, 8, 0.3f, 0.3f, 0.3f);
        var result = DemosaicBilinear.Build(bayer, RggbMosaic);

        Assert.Equal(bayer.Bounds, result.Bounds);
        Assert.Equal(3u, result.Planes);
    }

    [Fact]
    public void Stage3Builder_dispatches_cfa_to_demosaic()
    {
        var bayer = MakeRggbBayer(4, 4, 0.5f, 0.5f, 0.5f);
        var result = Stage3Builder.Build(bayer, Dng.Sdk.Tiff.Photometric.Cfa, RggbMosaic);

        Assert.Equal(3u, result.Planes);
        Assert.NotSame(bayer, result); // demosaic creates a new image
    }

    [Fact]
    public void Stage3Builder_cfa_without_mosaic_throws()
    {
        var bayer = MakeRggbBayer(4, 4, 0.5f, 0.5f, 0.5f);
        Assert.Throws<NotSupportedException>(() =>
            Stage3Builder.Build(bayer, Dng.Sdk.Tiff.Photometric.Cfa, mosaic: null));
    }

    [Fact]
    public void Stage3Builder_CanBuild_true_for_cfa_with_mosaic()
    {
        Assert.True(Stage3Builder.CanBuild(Dng.Sdk.Tiff.Photometric.Cfa, RggbMosaic));
    }

    [Fact]
    public void Stage3Builder_CanBuild_false_for_cfa_without_mosaic()
    {
        Assert.False(Stage3Builder.CanBuild(Dng.Sdk.Tiff.Photometric.Cfa, null));
    }
}
