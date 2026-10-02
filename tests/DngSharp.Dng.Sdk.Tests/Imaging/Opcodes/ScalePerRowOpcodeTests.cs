using System.Buffers.Binary;
using System.Runtime.InteropServices;
using DngSharp.Dng.Sdk.Errors;
using DngSharp.Dng.Sdk.Imaging;
using DngSharp.Dng.Sdk.Imaging.Opcodes;
using DngSharp.Dng.Sdk.Pixels;
using DngSharp.Dng.Sdk.Primitives;

namespace DngSharp.Dng.Sdk.Tests.Imaging.Opcodes;

public class ScalePerRowOpcodeTests
{
    /// <summary>
    /// Builds a raw <c>ScalePerRow</c> opcode body matching
    /// <c>dng_opcode_ScalePerRow::PutData</c>: big-endian
    /// dataSize/areaSpec/count/scale[].
    /// </summary>
    private static byte[] BuildBody(DngRect area, uint plane, uint planes, uint rowPitch, uint colPitch, float[] scales)
    {
        using var ms = new MemoryStream();

        void WriteI32(int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); ms.Write(b); }
        void WriteU32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); ms.Write(b); }
        void WriteF32(float v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteSingleBigEndian(b, v); ms.Write(b); }

        WriteI32(area.T); WriteI32(area.L); WriteI32(area.B); WriteI32(area.R);
        WriteU32(plane); WriteU32(planes);
        WriteU32(rowPitch); WriteU32(colPitch);

        WriteU32((uint)scales.Length);
        foreach (var s in scales) WriteF32(s);

        return ms.ToArray();
    }

    private static SimpleImage MakeImage(int width, int height, uint planes, float fill = 0f)
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
    public void Decode_reads_area_spec_and_scale_table()
    {
        var area = new DngRect(0, 0, 4, 2); // 4 rows, rowPitch=1 -> 4 scales
        var body = BuildBody(area, plane: 0, planes: 1, rowPitch: 1, colPitch: 1,
            scales: [0.5f, 1.0f, 1.5f, 2.0f]);

        var p = ScalePerRowOpcode.Decode(body);

        Assert.Equal(area, p.AreaSpec.Area);
        Assert.Equal(4, p.Scales.Length);
        Assert.Equal(1.5f, p.Scales[2]);
    }

    [Fact]
    public void Decode_throws_on_count_mismatch()
    {
        var area = new DngRect(0, 0, 4, 2);
        var body = BuildBody(area, 0, 1, 1, 1, scales: [0.5f, 1.0f]); // should be 4, not 2
        Assert.Throws<DngException>(() => ScalePerRowOpcode.Decode(body));
    }

    [Fact]
    public void Decode_throws_on_non_finite_scale()
    {
        var area = new DngRect(0, 0, 2, 1);
        var body = BuildBody(area, 0, 1, 1, 1, scales: [float.NaN, 1.0f]);
        Assert.Throws<DngException>(() => ScalePerRowOpcode.Decode(body));
    }

    [Fact]
    public void Apply_multiplies_per_row_scale_and_clips_to_unit_range()
    {
        var image = MakeImage(width: 2, height: 3, planes: 1, fill: 0.5f);
        var area = new DngRect(0, 0, 3, 2);
        var body = BuildBody(area, 0, 1, 1, 1, scales: [-4.0f, 2.0f, 4.0f]);
        var p = ScalePerRowOpcode.Decode(body);

        ScalePerRowOpcode.Apply(image, p);

        // row 0: 0.5 * -4.0 = -2.0 -> clip to -1.0
        Assert.Equal(-1.0f, ReadPixel(image, 0, 0, 0));
        Assert.Equal(-1.0f, ReadPixel(image, 0, 1, 0));
        // row 1: 0.5 * 2.0 = 1.0
        Assert.Equal(1.0f, ReadPixel(image, 1, 0, 0), 5);
        // row 2: 0.5 * 4.0 = 2.0 -> clip to 1.0
        Assert.Equal(1.0f, ReadPixel(image, 2, 0, 0));
    }

    [Fact]
    public void Apply_respects_row_pitch_stride()
    {
        // height=4, rowPitch=2 -> scales apply to rows 0 and 2 only.
        var image = MakeImage(width: 1, height: 4, planes: 1, fill: 0.5f);
        var area = new DngRect(0, 0, 4, 1);
        var body = BuildBody(area, 0, 1, 2, 1, scales: [2.0f, 0.5f]);
        var p = ScalePerRowOpcode.Decode(body);

        ScalePerRowOpcode.Apply(image, p);

        Assert.Equal(1.0f, ReadPixel(image, 0, 0, 0), 5);
        Assert.Equal(0.5f, ReadPixel(image, 1, 0, 0), 5); // untouched (skipped by pitch)
        Assert.Equal(0.25f, ReadPixel(image, 2, 0, 0), 5);
        Assert.Equal(0.5f, ReadPixel(image, 3, 0, 0), 5); // untouched (skipped by pitch)
    }

    [Fact]
    public void Apply_empty_scale_table_is_noop()
    {
        var image = MakeImage(width: 2, height: 2, planes: 1, fill: 0.42f);
        var body = BuildBody(DngRect.Empty, 0, 1, 1, 1, scales: []);
        var p = ScalePerRowOpcode.Decode(body);

        ScalePerRowOpcode.Apply(image, p);

        Assert.Equal(0.42f, ReadPixel(image, 0, 0, 0));
        Assert.Equal(0.42f, ReadPixel(image, 1, 1, 0));
    }

    [Fact]
    public void Apply_throws_not_yet_implemented_for_non_float_images()
    {
        var bounds = new DngRect(0, 0, 2, 2);
        var image = new SimpleImage(bounds, planes: 1, PixelType.UInt16);
        var body = BuildBody(bounds, 0, 1, 1, 1, scales: [1.0f, 1.0f]);
        var p = ScalePerRowOpcode.Decode(body);

        Assert.Throws<DngException>(() => ScalePerRowOpcode.Apply(image, p));
    }
}
