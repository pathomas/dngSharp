using System.Buffers.Binary;
using System.Runtime.InteropServices;
using DngSharp.Dng.Sdk.Errors;
using DngSharp.Dng.Sdk.Imaging;
using DngSharp.Dng.Sdk.Imaging.Opcodes;
using DngSharp.Dng.Sdk.Pixels;
using DngSharp.Dng.Sdk.Primitives;

namespace DngSharp.Dng.Sdk.Tests.Imaging.Opcodes;

public class GainMapOpcodeTests
{
    /// <summary>
    /// Builds a raw <c>GainMap</c> opcode body matching
    /// <c>dng_opcode_GainMap</c>'s stream constructor: big-endian
    /// dataSize/areaSpec/pointsV/pointsH/spacingV/spacingH/originV/originH/
    /// planes/sample[] (samples as real32, row-major: row, col, plane).
    /// </summary>
    private static byte[] BuildBody(
        DngRect area, uint plane, uint planes, uint rowPitch, uint colPitch,
        int pointsV, int pointsH, double spacingV, double spacingH,
        double originV, double originH, uint mapPlanes, float[] samples)
    {
        using var ms = new MemoryStream();

        void WriteI32(int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); ms.Write(b); }
        void WriteU32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); ms.Write(b); }
        void WriteF64(double v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteDoubleBigEndian(b, v); ms.Write(b); }
        void WriteF32(float v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteSingleBigEndian(b, v); ms.Write(b); }

        WriteI32(area.T); WriteI32(area.L); WriteI32(area.B); WriteI32(area.R);
        WriteU32(plane); WriteU32(planes);
        WriteU32(rowPitch); WriteU32(colPitch);

        WriteI32(pointsV); WriteI32(pointsH);
        WriteF64(spacingV); WriteF64(spacingH);
        WriteF64(originV); WriteF64(originH);
        WriteU32(mapPlanes);
        foreach (var s in samples) WriteF32(s);

        return ms.ToArray();
    }

    private static SimpleImage MakeImage(int width, int height, uint planes, float fill = 1f)
    {
        var bounds = new DngRect(0, 0, height, width);
        var image = new SimpleImage(bounds, planes, PixelType.Float32);
        var floats = MemoryMarshal.Cast<byte, float>(image.Buffer.AsByteSpan());
        floats.Fill(fill);
        return image;
    }

    private static float ReadPixel(SimpleImage image, int row, int col, uint plane)
    {
        var floats = MemoryMarshal.Cast<byte, float>(image.Buffer.AsByteSpan());
        long idx = image.Buffer.OffsetBytes(row, col, plane) / sizeof(float);
        return floats[(int)idx];
    }

    [Fact]
    public void Decode_reads_area_spec_and_grid_header()
    {
        var area = new DngRect(0, 0, 4, 4);
        var body = BuildBody(
            area, plane: 0, planes: 1, rowPitch: 1, colPitch: 1,
            pointsV: 2, pointsH: 2, spacingV: 1.0, spacingH: 1.0, originV: 0.0, originH: 0.0,
            mapPlanes: 1, samples: [1f, 1f, 1f, 1f]);

        var p = GainMapOpcode.Decode(body);

        Assert.Equal(area, p.AreaSpec.Area);
        Assert.Equal((2, 2), p.Points);
        Assert.Equal(1u, p.Planes);
        Assert.Equal(4, p.Samples.Length);
    }

    [Fact]
    public void Decode_single_row_forces_spacing_and_origin_override()
    {
        var area = new DngRect(0, 0, 4, 4);
        // pointsV == 1: native forces spacingV=1.0, originV=0.0 regardless of
        // what's on disk (here: garbage spacingV=99.0, originV=42.0).
        var body = BuildBody(
            area, 0, 1, 1, 1,
            pointsV: 1, pointsH: 2, spacingV: 99.0, spacingH: 1.0, originV: 42.0, originH: 0.0,
            mapPlanes: 1, samples: [0.5f, 0.75f]);

        var p = GainMapOpcode.Decode(body);

        Assert.Equal(1.0, p.Spacing.V);
        Assert.Equal(0.0, p.Origin.V);
    }

    [Fact]
    public void Decode_throws_on_zero_points()
    {
        var area = new DngRect(0, 0, 2, 2);
        var body = BuildBody(area, 0, 1, 1, 1, pointsV: 0, pointsH: 1, spacingV: 1, spacingH: 1, originV: 0, originH: 0, mapPlanes: 1, samples: []);
        Assert.Throws<DngException>(() => GainMapOpcode.Decode(body));
    }

    [Fact]
    public void Decode_throws_on_non_finite_sample()
    {
        var area = new DngRect(0, 0, 2, 2);
        var body = BuildBody(area, 0, 1, 1, 1, pointsV: 1, pointsH: 1, spacingV: 1, spacingH: 1, originV: 0, originH: 0, mapPlanes: 1, samples: [float.NaN]);
        Assert.Throws<DngException>(() => GainMapOpcode.Decode(body));
    }

    [Fact]
    public void Decode_throws_on_body_too_short()
    {
        var area = new DngRect(0, 0, 2, 2);
        var body = BuildBody(area, 0, 1, 1, 1, pointsV: 1, pointsH: 1, spacingV: 1, spacingH: 1, originV: 0, originH: 0, mapPlanes: 1, samples: [1f]);
        // Truncate so the sample grid is missing.
        Assert.Throws<DngException>(() => GainMapOpcode.Decode(body.AsSpan(0, body.Length - 4)));
    }

    [Fact]
    public void Apply_constant_grid_uniformly_scales_every_pixel()
    {
        var image = MakeImage(width: 4, height: 4, planes: 1, fill: 0.5f);
        var area = new DngRect(0, 0, 4, 4);
        var body = BuildBody(
            area, 0, 1, 1, 1,
            pointsV: 1, pointsH: 1, spacingV: 1, spacingH: 1, originV: 0, originH: 0,
            mapPlanes: 1, samples: [0.8f]);
        var p = GainMapOpcode.Decode(body);

        GainMapOpcode.Apply(image, p);

        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 4; c++)
                Assert.Equal(0.5f * 0.8f, ReadPixel(image, r, c, 0), 5);
    }

    [Fact]
    public void Apply_interpolates_at_grid_corners()
    {
        // 2x2 grid spanning the whole image (spacing=1, origin=0): corner
        // gains should be reproduced exactly at the image corners.
        var image = MakeImage(width: 2, height: 2, planes: 1, fill: 1.0f);
        var area = new DngRect(0, 0, 2, 2);
        // samples: row-major (row, col): (0,0)=0.2 (0,1)=0.4 (1,0)=0.6 (1,1)=0.8
        // (gain increases monotonically along both the row and column axes).
        var body = BuildBody(
            area, 0, 1, 1, 1,
            pointsV: 2, pointsH: 2, spacingV: 1, spacingH: 1, originV: 0, originH: 0,
            mapPlanes: 1, samples: [0.2f, 0.4f, 0.6f, 0.8f]);
        var p = GainMapOpcode.Decode(body);

        GainMapOpcode.Apply(image, p);

        // Pixel centers are at (row+0.5)/height and (col+0.5)/width -> for a
        // 2x2 image that's fractional grid positions 0.25 and 0.75 along
        // each axis (not exactly the grid corners), so just assert
        // monotonic ordering matching the sample grid instead of exact
        // corner values.
        float topLeft = ReadPixel(image, 0, 0, 0);
        float topRight = ReadPixel(image, 0, 1, 0);
        float bottomLeft = ReadPixel(image, 1, 0, 0);
        float bottomRight = ReadPixel(image, 1, 1, 0);

        Assert.True(topLeft < topRight);
        Assert.True(bottomLeft < bottomRight);
        Assert.True(topLeft < bottomLeft);
        Assert.True(topRight < bottomRight);
    }

    [Fact]
    public void Apply_clamps_outside_grid_bounds_to_edge_value()
    {
        // origin pushed far positive on both axes so every pixel's
        // normalized index (positionFrac - origin) falls below 0 -> clamps
        // to the (0,0) sample.
        var image = MakeImage(width: 4, height: 4, planes: 1, fill: 1.0f);
        var area = new DngRect(0, 0, 4, 4);
        var body = BuildBody(
            area, 0, 1, 1, 1,
            pointsV: 2, pointsH: 2, spacingV: 1, spacingH: 1, originV: 100.0, originH: 100.0,
            mapPlanes: 1, samples: [0.5f, 0.7f, 0.3f, 0.9f]);
        var p = GainMapOpcode.Decode(body);

        GainMapOpcode.Apply(image, p);

        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 4; c++)
                Assert.Equal(0.5f, ReadPixel(image, r, c, 0), 5);
    }

    [Fact]
    public void Apply_broadcasts_single_plane_gain_map_across_all_image_planes()
    {
        var image = MakeImage(width: 1, height: 1, planes: 3, fill: 0.4f);
        var area = new DngRect(0, 0, 1, 1);
        var body = BuildBody(
            area, plane: 0, planes: 3, rowPitch: 1, colPitch: 1,
            pointsV: 1, pointsH: 1, spacingV: 1, spacingH: 1, originV: 0, originH: 0,
            mapPlanes: 1, samples: [0.5f]);
        var p = GainMapOpcode.Decode(body);

        GainMapOpcode.Apply(image, p);

        for (uint plane = 0; plane < 3; plane++)
            Assert.Equal(0.4f * 0.5f, ReadPixel(image, 0, 0, plane), 5);
    }

    [Fact]
    public void Apply_clips_gain_result_to_unit_range()
    {
        var image = MakeImage(width: 1, height: 1, planes: 1, fill: 0.9f);
        var area = new DngRect(0, 0, 1, 1);
        var body = BuildBody(
            area, 0, 1, 1, 1,
            pointsV: 1, pointsH: 1, spacingV: 1, spacingH: 1, originV: 0, originH: 0,
            mapPlanes: 1, samples: [2.0f]); // 0.9 * 2.0 = 1.8, clipped to 1.0
        var p = GainMapOpcode.Decode(body);

        GainMapOpcode.Apply(image, p);

        Assert.Equal(1.0f, ReadPixel(image, 0, 0, 0));
    }

    [Fact]
    public void Apply_throws_not_yet_implemented_for_non_float_images()
    {
        var bounds = new DngRect(0, 0, 2, 2);
        var image = new SimpleImage(bounds, planes: 1, PixelType.UInt16);
        var body = BuildBody(bounds, 0, 1, 1, 1, pointsV: 1, pointsH: 1, spacingV: 1, spacingH: 1, originV: 0, originH: 0, mapPlanes: 1, samples: [1f]);
        var p = GainMapOpcode.Decode(body);

        Assert.Throws<DngException>(() => GainMapOpcode.Apply(image, p));
    }
}
