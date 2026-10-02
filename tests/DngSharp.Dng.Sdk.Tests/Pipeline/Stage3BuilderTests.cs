using DngSharp.Dng.Sdk.Imaging;
using DngSharp.Dng.Sdk.Imaging.Raw;
using DngSharp.Dng.Sdk.Pipeline;
using DngSharp.Dng.Sdk.Pixels;
using DngSharp.Dng.Sdk.Primitives;
using DngSharp.Dng.Sdk.Tiff;

namespace DngSharp.Dng.Sdk.Tests.Pipeline;

public class Stage3BuilderTests
{
    private static SimpleImage MakeStage2(int width = 4, int height = 4, uint planes = 3)
    {
        var lin = new LinearizationInfo
        {
            BlackLevel = [0.0],
            WhiteLevel = [65535.0],
        };
        // Produce a real Stage2 image via Stage2Builder.
        var raw = new SimpleImage(new DngRect(0, 0, height, width), planes, PixelType.UInt16);
        var tile = raw.GetTile(raw.Bounds);
        var samples = tile.AsTypedSpan<ushort>();
        for (int i = 0; i < samples.Length; i++) samples[i] = (ushort)(i * 100);
        raw.WriteTile(tile);
        return (SimpleImage)Stage2Builder.Build(raw, lin);
    }

    [Fact]
    public void LinearRaw_passthrough_returns_same_instance()
    {
        var stage2 = MakeStage2();
        var stage3 = Stage3Builder.Build(stage2, Photometric.LinearRaw);
        Assert.Same(stage2, stage3);
    }

    [Fact]
    public void Rgb_passthrough_returns_same_instance()
    {
        var stage2 = MakeStage2();
        var stage3 = Stage3Builder.Build(stage2, Photometric.Rgb);
        Assert.Same(stage2, stage3);
    }

    [Fact]
    public void CanBuild_returns_true_for_supported_types()
    {
        Assert.True(Stage3Builder.CanBuild(Photometric.LinearRaw));
        Assert.True(Stage3Builder.CanBuild(Photometric.Rgb));
    }

    [Fact]
    public void CanBuild_returns_false_for_bayer_cfa()
    {
        Assert.False(Stage3Builder.CanBuild(Photometric.Cfa));
    }

    [Fact]
    public void Cfa_throws_not_supported()
    {
        var stage2 = MakeStage2();
        Assert.Throws<NotSupportedException>(() =>
            Stage3Builder.Build(stage2, Photometric.Cfa));
    }
}
