using System.Buffers.Binary;
using System.Runtime.InteropServices;
using DngSharp.Dng.Sdk.Errors;
using DngSharp.Dng.Sdk.Imaging;
using DngSharp.Dng.Sdk.Imaging.Opcodes;
using DngSharp.Dng.Sdk.Pixels;
using DngSharp.Dng.Sdk.Primitives;

namespace DngSharp.Dng.Sdk.Tests.Imaging.Opcodes;

public class DeltaPerRowOpcodeTests
{
    /// <summary>
    /// Builds a raw <c>DeltaPerRow</c> opcode body matching
    /// <c>dng_opcode_DeltaPerRow::PutData</c>: big-endian
    /// dataSize/areaSpec/count/delta[].
    /// </summary>
    private static byte[] BuildBody(DngRect area, uint plane, uint planes, uint rowPitch, uint colPitch, float[] deltas)
    {
        using var ms = new MemoryStream();

        void WriteI32(int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); ms.Write(b); }
        void WriteU32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); ms.Write(b); }
        void WriteF32(float v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteSingleBigEndian(b, v); ms.Write(b); }

        WriteI32(area.T); WriteI32(area.L); WriteI32(area.B); WriteI32(area.R);
        WriteU32(plane); WriteU32(planes);
        WriteU32(rowPitch); WriteU32(colPitch);

        WriteU32((uint)deltas.Length);
        foreach (var d in deltas) WriteF32(d);

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
    public void Decode_reads_area_spec_and_delta_table()
    {
        var area = new DngRect(0, 0, 4, 2); // 4 rows, rowPitch=1 -> 4 deltas
        var body = BuildBody(area, plane: 0, planes: 1, rowPitch: 1, colPitch: 1,
            deltas: [0.1f, 0.2f, 0.3f, 0.4f]);

        var p = DeltaPerRowOpcode.Decode(body);

        Assert.Equal(area, p.AreaSpec.Area);
        Assert.Equal(0u, p.AreaSpec.Plane);
        Assert.Equal(1u, p.AreaSpec.Planes);
        Assert.Equal(4, p.Deltas.Length);
        Assert.Equal(0.3f, p.Deltas[2]);
    }

    [Fact]
    public void Decode_throws_on_count_mismatch()
    {
        var area = new DngRect(0, 0, 4, 2);
        var body = BuildBody(area, 0, 1, 1, 1, deltas: [0.1f, 0.2f]); // should be 4, not 2

        // Corrupt the count field to disagree with delta table length by
        // building a body that claims 2 deltas for a 4-tall area — decode
        // must reject this as a framing mismatch.
        Assert.Throws<DngException>(() => DeltaPerRowOpcode.Decode(body));
    }

    [Fact]
    public void Decode_throws_on_non_finite_delta()
    {
        var area = new DngRect(0, 0, 2, 1);
        var body = BuildBody(area, 0, 1, 1, 1, deltas: [float.NaN, 0.1f]);
        Assert.Throws<DngException>(() => DeltaPerRowOpcode.Decode(body));
    }

    [Fact]
    public void Apply_adds_per_row_delta_and_clips_to_unit_range()
    {
        var image = MakeImage(width: 2, height: 3, planes: 1, fill: 0.5f);
        var area = new DngRect(0, 0, 3, 2);
        var body = BuildBody(area, 0, 1, 1, 1, deltas: [-2.0f, 0.1f, 2.0f]);
        var p = DeltaPerRowOpcode.Decode(body);

        DeltaPerRowOpcode.Apply(image, p);

        // row 0: 0.5 + (-2.0) = -1.5 -> clip to -1.0
        Assert.Equal(-1.0f, ReadPixel(image, 0, 0, 0));
        Assert.Equal(-1.0f, ReadPixel(image, 0, 1, 0));
        // row 1: 0.5 + 0.1 = 0.6
        Assert.Equal(0.6f, ReadPixel(image, 1, 0, 0), 5);
        // row 2: 0.5 + 2.0 = 2.5 -> clip to 1.0
        Assert.Equal(1.0f, ReadPixel(image, 2, 0, 0));
    }

    [Fact]
    public void Apply_respects_row_pitch_stride()
    {
        // height=4, rowPitch=2 -> deltas apply to rows 0 and 2 only.
        var image = MakeImage(width: 1, height: 4, planes: 1, fill: 0f);
        var area = new DngRect(0, 0, 4, 1);
        var body = BuildBody(area, 0, 1, 2, 1, deltas: [0.1f, 0.2f]);
        var p = DeltaPerRowOpcode.Decode(body);

        DeltaPerRowOpcode.Apply(image, p);

        Assert.Equal(0.1f, ReadPixel(image, 0, 0, 0), 5);
        Assert.Equal(0f, ReadPixel(image, 1, 0, 0), 5); // untouched (skipped by pitch)
        Assert.Equal(0.2f, ReadPixel(image, 2, 0, 0), 5);
        Assert.Equal(0f, ReadPixel(image, 3, 0, 0), 5); // untouched (skipped by pitch)
    }

    [Fact]
    public void Apply_empty_delta_table_is_noop()
    {
        // Empty area -> rowPitch/colPitch must be 1, and expected delta
        // count derived from Area.H() (0 for an empty rect) is 0 too.
        var image = MakeImage(width: 2, height: 2, planes: 1, fill: 0.42f);
        var body = BuildBody(DngRect.Empty, 0, 1, 1, 1, deltas: []);
        var p = DeltaPerRowOpcode.Decode(body);

        DeltaPerRowOpcode.Apply(image, p);

        Assert.Equal(0.42f, ReadPixel(image, 0, 0, 0));
        Assert.Equal(0.42f, ReadPixel(image, 1, 1, 0));
    }

    [Fact]
    public void Apply_throws_not_yet_implemented_for_non_float_images()
    {
        var bounds = new DngRect(0, 0, 2, 2);
        var image = new SimpleImage(bounds, planes: 1, PixelType.UInt16);
        var body = BuildBody(bounds, 0, 1, 1, 1, deltas: [0.1f, 0.2f]);
        var p = DeltaPerRowOpcode.Decode(body);

        Assert.Throws<DngException>(() => DeltaPerRowOpcode.Apply(image, p));
    }
}
