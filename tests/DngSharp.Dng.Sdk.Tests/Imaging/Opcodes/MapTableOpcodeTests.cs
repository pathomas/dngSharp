using System.Buffers.Binary;
using System.Runtime.InteropServices;
using DngSharp.Dng.Sdk.Errors;
using DngSharp.Dng.Sdk.Imaging;
using DngSharp.Dng.Sdk.Imaging.Opcodes;
using DngSharp.Dng.Sdk.Pixels;
using DngSharp.Dng.Sdk.Primitives;

namespace DngSharp.Dng.Sdk.Tests.Imaging.Opcodes;

public class MapTableOpcodeTests
{
    /// <summary>
    /// Builds a raw <c>MapTable</c> opcode body matching
    /// <c>dng_opcode_MapTable::PutData</c>: big-endian
    /// dataSize/areaSpec/count/table[].
    /// </summary>
    private static byte[] BuildBody(DngRect area, uint plane, uint planes, uint rowPitch, uint colPitch, ushort[] table)
    {
        using var ms = new MemoryStream();

        void WriteI32(int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); ms.Write(b); }
        void WriteU32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); ms.Write(b); }
        void WriteU16(ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, v); ms.Write(b); }

        WriteI32(area.T); WriteI32(area.L); WriteI32(area.B); WriteI32(area.R);
        WriteU32(plane); WriteU32(planes);
        WriteU32(rowPitch); WriteU32(colPitch);

        WriteU32((uint)table.Length);
        foreach (var v in table) WriteU16(v);

        return ms.ToArray();
    }

    private static SimpleImage MakeImage(int width, int height, uint planes, ushort fill = 0)
    {
        var bounds = new DngRect(0, 0, height, width);
        var image = new SimpleImage(bounds, planes, PixelType.UInt16);
        var pixels = MemoryMarshal.Cast<byte, ushort>(image.Buffer.AsByteSpan());
        pixels.Fill(fill);
        return image;
    }

    private static ushort ReadPixel(SimpleImage image, int row, int col, uint plane)
    {
        var pixels = MemoryMarshal.Cast<byte, ushort>(image.Buffer.AsByteSpan());
        long idx = image.Buffer.OffsetBytes(row, col, plane) / sizeof(ushort);
        return pixels[(int)idx];
    }

    [Fact]
    public void Decode_reads_area_spec_and_replicates_last_entry()
    {
        var area = new DngRect(0, 0, 2, 2);
        var body = BuildBody(area, plane: 0, planes: 1, rowPitch: 1, colPitch: 1, table: [10, 20, 30]);

        var p = MapTableOpcode.Decode(body);

        Assert.Equal(area, p.AreaSpec.Area);
        Assert.Equal(65536, p.Table.Length);
        Assert.Equal((ushort)10, p.Table[0]);
        Assert.Equal((ushort)20, p.Table[1]);
        Assert.Equal((ushort)30, p.Table[2]);
        Assert.Equal((ushort)30, p.Table[3]); // replicated from last explicit entry
        Assert.Equal((ushort)30, p.Table[65535]);
    }

    [Fact]
    public void Decode_throws_on_count_zero()
    {
        var area = new DngRect(0, 0, 2, 2);
        var body = BuildBody(area, 0, 1, 1, 1, table: []);
        Assert.Throws<DngException>(() => MapTableOpcode.Decode(body));
    }

    [Fact]
    public void Decode_throws_on_body_too_short()
    {
        var area = new DngRect(0, 0, 2, 2);
        var body = BuildBody(area, 0, 1, 1, 1, table: [1, 2]);
        // Truncate so the table entries are missing.
        Assert.Throws<DngException>(() => MapTableOpcode.Decode(body.AsSpan(0, body.Length - 4)));
    }

    [Fact]
    public void Apply_maps_each_sample_through_the_table()
    {
        var image = MakeImage(width: 2, height: 2, planes: 1);
        var pixels = MemoryMarshal.Cast<byte, ushort>(image.Buffer.AsByteSpan());
        pixels[0] = 0; pixels[1] = 1; pixels[2] = 2; pixels[3] = 5; // (row0: 0,1) (row1: 2,5)

        var area = new DngRect(0, 0, 2, 2);
        var body = BuildBody(area, 0, 1, 1, 1, table: [100, 200, 300]);
        var p = MapTableOpcode.Decode(body);

        MapTableOpcode.Apply(image, p);

        Assert.Equal((ushort)100, ReadPixel(image, 0, 0, 0));
        Assert.Equal((ushort)200, ReadPixel(image, 0, 1, 0));
        Assert.Equal((ushort)300, ReadPixel(image, 1, 0, 0));
        Assert.Equal((ushort)300, ReadPixel(image, 1, 1, 0)); // 5 -> replicated last entry (300)
    }

    [Fact]
    public void Apply_respects_col_pitch_stride()
    {
        var image = MakeImage(width: 4, height: 1, planes: 1, fill: 0);
        var area = new DngRect(0, 0, 1, 4);
        var body = BuildBody(area, 0, 1, 1, 2, table: [7]);
        var p = MapTableOpcode.Decode(body);

        MapTableOpcode.Apply(image, p);

        Assert.Equal((ushort)7, ReadPixel(image, 0, 0, 0));
        Assert.Equal((ushort)0, ReadPixel(image, 0, 1, 0)); // untouched (skipped by pitch)
        Assert.Equal((ushort)7, ReadPixel(image, 0, 2, 0));
        Assert.Equal((ushort)0, ReadPixel(image, 0, 3, 0)); // untouched (skipped by pitch)
    }

    [Fact]
    public void Apply_throws_not_yet_implemented_for_non_uint16_images()
    {
        var bounds = new DngRect(0, 0, 2, 2);
        var image = new SimpleImage(bounds, planes: 1, PixelType.Float32);
        var body = BuildBody(bounds, 0, 1, 1, 1, table: [1]);
        var p = MapTableOpcode.Decode(body);

        Assert.Throws<DngException>(() => MapTableOpcode.Apply(image, p));
    }
}
