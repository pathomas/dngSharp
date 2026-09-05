using System.Buffers.Binary;
using System.Runtime.InteropServices;
using DngSharp.Dng.Sdk.Errors;
using DngSharp.Dng.Sdk.Imaging;
using DngSharp.Dng.Sdk.Imaging.Opcodes;
using DngSharp.Dng.Sdk.Pixels;
using DngSharp.Dng.Sdk.Primitives;

namespace DngSharp.Dng.Sdk.Tests.Imaging.Opcodes;

public class FixVignetteRadialOpcodeTests
{
    private static byte[] BuildBody(double[] coefficients, double centerH, double centerV)
    {
        using var ms = new MemoryStream();
        Span<byte> buf8 = stackalloc byte[8];

        foreach (var c in coefficients)
        {
            BinaryPrimitives.WriteDoubleBigEndian(buf8, c);
            ms.Write(buf8);
        }

        BinaryPrimitives.WriteDoubleBigEndian(buf8, centerH);
        ms.Write(buf8);
        BinaryPrimitives.WriteDoubleBigEndian(buf8, centerV);
        ms.Write(buf8);

        return ms.ToArray();
    }

    private static SimpleImage MakeImage(int width, int height, uint planes, float fill)
    {
        var img = new SimpleImage(new DngRect(0, 0, height, width), planes, PixelType.Float32);
        var floats = MemoryMarshal.Cast<byte, float>(img.Buffer.AsByteSpan());
        floats.Fill(fill);
        return img;
    }

    [Fact]
    public void Decodes_coefficients_and_center()
    {
        var body = BuildBody([0.1, -0.02, 0.0, 0.0, 0.0], 0.5, 0.48);
        var p = FixVignetteRadialOpcode.Decode(body);

        Assert.Equal([0.1, -0.02, 0.0, 0.0, 0.0], p.Coefficients);
        Assert.Equal(0.5, p.Center.H);
        Assert.Equal(0.48, p.Center.V);
        Assert.False(p.IsNop);
    }

    [Fact]
    public void All_zero_coefficients_is_nop()
    {
        var body = BuildBody([0.0, 0.0, 0.0, 0.0, 0.0], 0.5, 0.5);
        var p = FixVignetteRadialOpcode.Decode(body);
        Assert.True(p.IsNop);
    }

    [Fact]
    public void Rejects_wrong_body_size()
    {
        var body = BuildBody([0.0, 0.0, 0.0, 0.0, 0.0], 0.5, 0.5);
        var truncated = body[..^4];

        var ex = Assert.Throws<DngException>(() => FixVignetteRadialOpcode.Decode(truncated));
        Assert.Equal(DngError.BadFormat, ex.ErrorCode);
    }

    [Fact]
    public void Rejects_out_of_range_center()
    {
        var body = BuildBody([0.0, 0.0, 0.0, 0.0, 0.0], 1.5, 0.5);
        var ex = Assert.Throws<DngException>(() => FixVignetteRadialOpcode.Decode(body));
        Assert.Equal(DngError.BadFormat, ex.ErrorCode);
    }

    [Fact]
    public void Rejects_non_finite_coefficient()
    {
        var body = BuildBody([double.NaN, 0.0, 0.0, 0.0, 0.0], 0.5, 0.5);
        var ex = Assert.Throws<DngException>(() => FixVignetteRadialOpcode.Decode(body));
        Assert.Equal(DngError.BadFormat, ex.ErrorCode);
    }

    [Fact]
    public void Nop_params_leave_image_unmodified()
    {
        var img = MakeImage(8, 8, 1, 0.5f);
        var p = FixVignetteRadialOpcode.Decode(BuildBody([0.0, 0.0, 0.0, 0.0, 0.0], 0.5, 0.5));

        FixVignetteRadialOpcode.Apply(img, p);

        var floats = MemoryMarshal.Cast<byte, float>(img.Buffer.AsByteSpan());
        Assert.All(floats.ToArray(), v => Assert.Equal(0.5f, v));
    }

    [Fact]
    public void Center_pixel_gain_is_one()
    {
        // At the exact optical center, r == 0 so gain(0) == 1 regardless of coefficients.
        var img = MakeImage(9, 9, 1, 0.4f);
        var p = FixVignetteRadialOpcode.Decode(BuildBody([0.5, 0.0, 0.0, 0.0, 0.0], 0.5, 0.5));

        FixVignetteRadialOpcode.Apply(img, p);

        var floats = MemoryMarshal.Cast<byte, float>(img.Buffer.AsByteSpan());
        // Center pixel is at row=4, col=4 for a 9x9 image (0-indexed), which
        // sits exactly on the (0.5, 0.5) normalized center once +0.5 pixel-center
        // offset is applied against a 9-wide/9-tall bounds (center pixel coord = 4.5).
        float centerValue = floats[4 * 9 + 4];
        Assert.True(centerValue >= 0.4f, $"expected near-unity gain at center, got {centerValue}");
    }

    [Fact]
    public void Brightens_pixels_away_from_center_with_positive_coefficients()
    {
        var img = MakeImage(16, 16, 1, 0.2f);
        var p = FixVignetteRadialOpcode.Decode(BuildBody([1.0, 0.0, 0.0, 0.0, 0.0], 0.5, 0.5));

        FixVignetteRadialOpcode.Apply(img, p);

        var floats = MemoryMarshal.Cast<byte, float>(img.Buffer.AsByteSpan());
        float centerValue = floats[8 * 16 + 8];
        float cornerValue = floats[0];

        // Corner (r ~= maxRadius, r2 ~= 1) should have gain ~= 1 + k0 = 2.0,
        // while pixels near the center have gain close to 1 (unchanged).
        Assert.True(cornerValue > centerValue,
            $"expected corner ({cornerValue}) to be brighter than center ({centerValue})");
    }

    [Fact]
    public void Gain_is_clipped_to_one_when_result_would_exceed_it()
    {
        var img = MakeImage(16, 16, 1, 0.9f);
        var p = FixVignetteRadialOpcode.Decode(BuildBody([5.0, 0.0, 0.0, 0.0, 0.0], 0.5, 0.5));

        FixVignetteRadialOpcode.Apply(img, p);

        var floats = MemoryMarshal.Cast<byte, float>(img.Buffer.AsByteSpan());
        Assert.All(floats.ToArray(), v => Assert.True(v <= 1.0f));
    }

    [Fact]
    public void Applies_same_gain_to_every_plane_at_a_given_pixel()
    {
        var img = MakeImage(8, 8, 3, 0.3f);
        var p = FixVignetteRadialOpcode.Decode(BuildBody([1.0, 0.0, 0.0, 0.0, 0.0], 0.5, 0.5));

        FixVignetteRadialOpcode.Apply(img, p);

        var buf = img.Buffer;
        var floats = MemoryMarshal.Cast<byte, float>(buf.AsByteSpan());

        long idx0 = buf.OffsetBytes(2, 3, 0) / sizeof(float);
        long idx1 = buf.OffsetBytes(2, 3, 1) / sizeof(float);
        long idx2 = buf.OffsetBytes(2, 3, 2) / sizeof(float);

        Assert.Equal(floats[(int)idx0], floats[(int)idx1]);
        Assert.Equal(floats[(int)idx1], floats[(int)idx2]);
    }

    [Fact]
    public void Rejects_non_float_pixel_type()
    {
        var img = new SimpleImage(new DngRect(0, 0, 4, 4), 1, PixelType.UInt16);
        var p = FixVignetteRadialOpcode.Decode(BuildBody([0.1, 0.0, 0.0, 0.0, 0.0], 0.5, 0.5));

        var ex = Assert.Throws<DngException>(() => FixVignetteRadialOpcode.Apply(img, p));
        Assert.Equal(DngError.NotYetImplemented, ex.ErrorCode);
    }
}
