using System.Buffers.Binary;
using System.Runtime.InteropServices;
using DngSharp.Dng.Sdk.Errors;
using DngSharp.Dng.Sdk.Imaging;
using DngSharp.Dng.Sdk.Imaging.Opcodes;
using DngSharp.Dng.Sdk.Pixels;
using DngSharp.Dng.Sdk.Primitives;

namespace DngSharp.Dng.Sdk.Tests.Imaging.Opcodes;

public class FixBadPixelsConstantOpcodeTests
{
    private static byte[] BuildBody(uint constant, uint bayerPhase)
    {
        using var ms = new MemoryStream();
        void WriteU32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); ms.Write(b); }
        WriteU32(constant);
        WriteU32(bayerPhase);
        return ms.ToArray();
    }

    private static SimpleImage MakeImage(int width, int height, ushort[] values)
    {
        var bounds = new DngRect(0, 0, height, width);
        var image = new SimpleImage(bounds, planes: 1, PixelType.UInt16);
        var pixels = MemoryMarshal.Cast<byte, ushort>(image.Buffer.AsByteSpan());
        values.CopyTo(pixels);
        return image;
    }

    private static ushort ReadPixel(SimpleImage image, int row, int col)
    {
        var pixels = MemoryMarshal.Cast<byte, ushort>(image.Buffer.AsByteSpan());
        long idx = image.Buffer.OffsetBytes(row, col, 0) / sizeof(ushort);
        return pixels[(int)idx];
    }

    [Fact]
    public void Decode_reads_constant_and_bayer_phase()
    {
        var body = BuildBody(constant: 0, bayerPhase: 1);
        var p = FixBadPixelsConstantOpcode.Decode(body);
        Assert.Equal(0u, p.Constant);
        Assert.Equal(1u, p.BayerPhase);
    }

    [Fact]
    public void Decode_throws_on_body_too_short()
    {
        var body = new byte[4]; // needs 8 bytes (constant + bayerPhase)
        Assert.Throws<DngException>(() => FixBadPixelsConstantOpcode.Decode(body));
    }

    [Fact]
    public void Apply_fixes_green_bad_pixel_using_diagonal_neighbors()
    {
        // bayerPhase=0: (0,0) is green (row+col even -> green per IsGreen).
        // 5x5 grid, bad pixel at (2,2) which should be green (2+2=4 even).
        int w = 5, h = 5;
        var values = new ushort[w * h];
        for (int i = 0; i < values.Length; i++) values[i] = 100;
        values[2 * w + 2] = 0; // bad pixel marked with constant 0

        // diagonal neighbors of (2,2): (1,1)=100,(1,3)=100,(3,1)=100,(3,3)=100
        var image = MakeImage(w, h, values);
        var p = FixBadPixelsConstantOpcode.Decode(BuildBody(constant: 0, bayerPhase: 0));

        FixBadPixelsConstantOpcode.Apply(image, p);

        Assert.Equal((ushort)100, ReadPixel(image, 2, 2));
    }

    [Fact]
    public void Apply_fixes_red_blue_bad_pixel_using_axis_neighbors()
    {
        // bayerPhase=0: (0,1) is red/blue (row+col odd).
        int w = 5, h = 5;
        var values = new ushort[w * h];
        for (int i = 0; i < values.Length; i++) values[i] = 200;
        values[2 * w + 1] = 0; // bad pixel at (2,1), red/blue since 2+1=3 odd

        var image = MakeImage(w, h, values);
        var p = FixBadPixelsConstantOpcode.Decode(BuildBody(constant: 0, bayerPhase: 0));

        FixBadPixelsConstantOpcode.Apply(image, p);

        Assert.Equal((ushort)200, ReadPixel(image, 2, 1));
    }

    [Fact]
    public void Apply_ignores_non_matching_pixels()
    {
        var image = MakeImage(3, 3, [1, 2, 3, 4, 5, 6, 7, 8, 9]);
        var p = FixBadPixelsConstantOpcode.Decode(BuildBody(constant: 0, bayerPhase: 0));

        FixBadPixelsConstantOpcode.Apply(image, p);

        Assert.Equal((ushort)5, ReadPixel(image, 1, 1));
    }

    [Fact]
    public void Apply_partial_neighbor_average_near_edge()
    {
        // 3x3 grid, bad pixel at corner (0,0) which is green (0+0=0 even).
        // Only one diagonal neighbor (1,1) is in-bounds.
        var image = MakeImage(3, 3, [0, 10, 20, 30, 40, 50, 60, 70, 80]);
        var p = FixBadPixelsConstantOpcode.Decode(BuildBody(constant: 0, bayerPhase: 0));

        FixBadPixelsConstantOpcode.Apply(image, p);

        Assert.Equal((ushort)40, ReadPixel(image, 0, 0));
    }

    [Fact]
    public void Apply_throws_not_yet_implemented_for_non_uint16_images()
    {
        var bounds = new DngRect(0, 0, 2, 2);
        var image = new SimpleImage(bounds, planes: 1, PixelType.Float32);
        var p = FixBadPixelsConstantOpcode.Decode(BuildBody(0, 0));

        Assert.Throws<DngException>(() => FixBadPixelsConstantOpcode.Apply(image, p));
    }

    [Fact]
    public void Apply_throws_not_yet_implemented_for_multi_plane_images()
    {
        var bounds = new DngRect(0, 0, 2, 2);
        var image = new SimpleImage(bounds, planes: 3, PixelType.UInt16);
        var p = FixBadPixelsConstantOpcode.Decode(BuildBody(0, 0));

        Assert.Throws<DngException>(() => FixBadPixelsConstantOpcode.Apply(image, p));
    }
}
