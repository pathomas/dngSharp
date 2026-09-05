using System.Buffers.Binary;
using System.Runtime.InteropServices;
using DngSharp.Dng.Sdk.Errors;
using DngSharp.Dng.Sdk.Imaging;
using DngSharp.Dng.Sdk.Imaging.Opcodes;
using DngSharp.Dng.Sdk.Pixels;
using DngSharp.Dng.Sdk.Primitives;

namespace DngSharp.Dng.Sdk.Tests.Imaging.Opcodes;

public class FixBadPixelsListOpcodeTests
{
    private static byte[] BuildBody(uint bayerPhase, DngPoint[] points, DngRect[] rects)
    {
        using var ms = new MemoryStream();
        void WriteU32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); ms.Write(b); }
        void WriteI32(int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); ms.Write(b); }

        WriteU32(bayerPhase);
        WriteU32((uint)points.Length);
        WriteU32((uint)rects.Length);

        foreach (var pt in points) { WriteI32(pt.V); WriteI32(pt.H); }
        foreach (var r in rects) { WriteI32(r.T); WriteI32(r.L); WriteI32(r.B); WriteI32(r.R); }

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
    public void Decode_reads_points_and_rects()
    {
        var body = BuildBody(0, [new DngPoint(1, 2)], [new DngRect(3, 4, 5, 6)]);
        var p = FixBadPixelsListOpcode.Decode(body);

        Assert.Single(p.Points);
        Assert.Equal(new DngPoint(1, 2), p.Points[0]);
        Assert.Single(p.Rects);
        Assert.Equal(new DngRect(3, 4, 5, 6), p.Rects[0]);
    }

    [Fact]
    public void Decode_throws_on_size_mismatch()
    {
        var body = BuildBody(0, [new DngPoint(1, 2)], []);
        // Corrupt pointCount field to claim more points than the body has room for.
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(4, 4), 999);
        Assert.Throws<DngException>(() => FixBadPixelsListOpcode.Decode(body));
    }

    [Fact]
    public void Apply_fixes_single_bad_point_using_same_color_neighbors()
    {
        int w = 5, h = 5;
        var values = new ushort[w * h];
        for (int i = 0; i < values.Length; i++) values[i] = 100;
        values[2 * w + 2] = 9999; // bad point at (2,2), green under phase 0

        var image = MakeImage(w, h, values);
        var p = FixBadPixelsListOpcode.Decode(BuildBody(0, [new DngPoint(2, 2)], []));

        FixBadPixelsListOpcode.Apply(image, p);

        Assert.Equal((ushort)100, ReadPixel(image, 2, 2));
    }

    [Fact]
    public void Apply_fixes_bad_rect_covering_single_column()
    {
        // 5x5 grid, bad column at col=2 (a single-column rect).
        int w = 5, h = 5;
        var values = new ushort[w * h];
        for (int i = 0; i < values.Length; i++) values[i] = 50;
        for (int row = 0; row < h; row++) values[row * w + 2] = 9999;

        var image = MakeImage(w, h, values);
        var rect = new DngRect(0, 2, h, 3);
        var p = FixBadPixelsListOpcode.Decode(BuildBody(0, [], [rect]));

        FixBadPixelsListOpcode.Apply(image, p);

        // Middle rows should be fixed via same-color neighbors outside the
        // bad column (this port's simplified same-color-neighbor rule).
        Assert.Equal((ushort)50, ReadPixel(image, 2, 2));
    }

    [Fact]
    public void Apply_ignores_flagged_bad_neighbors_when_averaging()
    {
        // Two adjacent bad green points diagonally isolated from each other
        // should each still resolve using the remaining good neighbors.
        int w = 5, h = 5;
        var values = new ushort[w * h];
        for (int i = 0; i < values.Length; i++) values[i] = 80;
        values[2 * w + 2] = 9999; // (2,2) bad, green
        values[1 * w + 1] = 9999; // (1,1) also bad -> not counted as a neighbor of (2,2)

        var image = MakeImage(w, h, values);
        var p = FixBadPixelsListOpcode.Decode(BuildBody(0, [new DngPoint(2, 2), new DngPoint(1, 1)], []));

        FixBadPixelsListOpcode.Apply(image, p);

        Assert.Equal((ushort)80, ReadPixel(image, 2, 2));
    }

    [Fact]
    public void Apply_throws_not_yet_implemented_for_non_uint16_images()
    {
        var bounds = new DngRect(0, 0, 2, 2);
        var image = new SimpleImage(bounds, planes: 1, PixelType.Float32);
        var p = FixBadPixelsListOpcode.Decode(BuildBody(0, [], []));

        Assert.Throws<DngException>(() => FixBadPixelsListOpcode.Apply(image, p));
    }
}
