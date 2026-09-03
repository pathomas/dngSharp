using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Dng.Sdk.Errors;
using Dng.Sdk.Imaging;
using Dng.Sdk.Imaging.Opcodes;
using Dng.Sdk.Pixels;
using Dng.Sdk.Primitives;

namespace Dng.Sdk.Tests.Imaging.Opcodes;

public class MapPolynomialOpcodeTests
{
    /// <summary>
    /// Builds a raw <c>MapPolynomial</c> opcode body matching
    /// <c>dng_opcode_MapPolynomial::PutData</c>: big-endian
    /// dataSize/areaSpec/degree/coefficient[] (coefficients as real64).
    /// </summary>
    private static byte[] BuildBody(DngRect area, uint plane, uint planes, uint rowPitch, uint colPitch, double[] coefficients)
    {
        using var ms = new MemoryStream();

        void WriteI32(int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); ms.Write(b); }
        void WriteU32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); ms.Write(b); }
        void WriteF64(double v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteDoubleBigEndian(b, v); ms.Write(b); }

        uint degree = (uint)(coefficients.Length - 1);

        WriteI32(area.T); WriteI32(area.L); WriteI32(area.B); WriteI32(area.R);
        WriteU32(plane); WriteU32(planes);
        WriteU32(rowPitch); WriteU32(colPitch);

        WriteU32(degree);
        foreach (var c in coefficients) WriteF64(c);

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
    public void Decode_reads_area_spec_and_coefficients()
    {
        var area = new DngRect(0, 0, 2, 2);
        var body = BuildBody(area, plane: 0, planes: 1, rowPitch: 1, colPitch: 1, coefficients: [0.1, 0.2, 0.3]);

        var p = MapPolynomialOpcode.Decode(body);

        Assert.Equal(area, p.AreaSpec.Area);
        Assert.Equal(2u, p.Degree);
        Assert.Equal(3, p.Coefficients.Length);
        Assert.Equal(0.2f, p.Coefficients[1]);
    }

    [Fact]
    public void Decode_throws_on_degree_too_large()
    {
        var area = new DngRect(0, 0, 2, 2);
        // 10 coefficients -> degree 9 > kMaxDegree (8).
        var body = BuildBody(area, 0, 1, 1, 1, coefficients: new double[10]);
        Assert.Throws<DngException>(() => MapPolynomialOpcode.Decode(body));
    }

    [Fact]
    public void Decode_throws_on_non_finite_coefficient()
    {
        var area = new DngRect(0, 0, 2, 2);
        var body = BuildBody(area, 0, 1, 1, 1, coefficients: [double.NaN]);
        Assert.Throws<DngException>(() => MapPolynomialOpcode.Decode(body));
    }

    [Fact]
    public void Apply_degree_zero_sets_constant_value()
    {
        var image = MakeImage(width: 2, height: 1, planes: 1, fill: 0.5f);
        var area = new DngRect(0, 0, 1, 2);
        var body = BuildBody(area, 0, 1, 1, 1, coefficients: [0.25]);
        var p = MapPolynomialOpcode.Decode(body);

        MapPolynomialOpcode.Apply(image, p);

        Assert.Equal(0.25f, ReadPixel(image, 0, 0, 0));
        Assert.Equal(0.25f, ReadPixel(image, 0, 1, 0));
    }

    [Fact]
    public void Apply_degree_one_is_linear_and_odd_symmetric()
    {
        // y = 0.1 + 2*x, verified at both a positive and negative x.
        var image = MakeImage(width: 1, height: 1, planes: 1, fill: 0f);
        var area = new DngRect(0, 0, 1, 1);
        var body = BuildBody(area, 0, 1, 1, 1, coefficients: [0.1, 2.0]);
        var p = MapPolynomialOpcode.Decode(body);

        var pixels = MemoryMarshal.Cast<byte, float>(image.Buffer.AsByteSpan());
        pixels[0] = 0.3f;
        MapPolynomialOpcode.Apply(image, p);
        Assert.Equal(0.1f + 2.0f * 0.3f, ReadPixel(image, 0, 0, 0), 5);

        pixels[0] = -0.3f;
        MapPolynomialOpcode.Apply(image, p);
        Assert.Equal(0.1f + 2.0f * -0.3f, ReadPixel(image, 0, 0, 0), 5);
    }

    [Fact]
    public void Apply_higher_degree_is_odd_symmetric_about_zero_except_constant_term()
    {
        // y(x) - c0 must be an odd function of x for degree >= 1:
        // (y(x) - c0) == -(y(-x) - c0).
        var image = MakeImage(width: 1, height: 1, planes: 1, fill: 0f);
        var area = new DngRect(0, 0, 1, 1);
        var body = BuildBody(area, 0, 1, 1, 1, coefficients: [0.05, 1.0, 0.5, 0.25]);
        var p = MapPolynomialOpcode.Decode(body);

        var pixels = MemoryMarshal.Cast<byte, float>(image.Buffer.AsByteSpan());

        pixels[0] = 0.4f;
        MapPolynomialOpcode.Apply(image, p);
        float yPos = ReadPixel(image, 0, 0, 0);

        pixels[0] = -0.4f;
        MapPolynomialOpcode.Apply(image, p);
        float yNeg = ReadPixel(image, 0, 0, 0);

        Assert.Equal(yPos - 0.05f, -(yNeg - 0.05f), 4);
    }

    [Fact]
    public void Apply_clips_result_to_unit_range()
    {
        var image = MakeImage(width: 1, height: 1, planes: 1, fill: 0f);
        var area = new DngRect(0, 0, 1, 1);
        var body = BuildBody(area, 0, 1, 1, 1, coefficients: [5.0]); // constant, out of [-1,1]
        var p = MapPolynomialOpcode.Decode(body);

        MapPolynomialOpcode.Apply(image, p);

        Assert.Equal(1.0f, ReadPixel(image, 0, 0, 0));
    }

    [Fact]
    public void Apply_throws_not_yet_implemented_for_non_float_images()
    {
        var bounds = new DngRect(0, 0, 2, 2);
        var image = new SimpleImage(bounds, planes: 1, PixelType.UInt16);
        var body = BuildBody(bounds, 0, 1, 1, 1, coefficients: [0.1, 0.2]);
        var p = MapPolynomialOpcode.Decode(body);

        Assert.Throws<DngException>(() => MapPolynomialOpcode.Apply(image, p));
    }
}
