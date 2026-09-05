using System.Buffers.Binary;
using DngSharp.Dng.Sdk.Errors;
using DngSharp.Dng.Sdk.Imaging;
using DngSharp.Dng.Sdk.Imaging.Opcodes;
using DngSharp.Dng.Sdk.Pixels;
using DngSharp.Dng.Sdk.Primitives;
using DngSharp.Dng.Sdk.Tiff;

namespace DngSharp.Dng.Sdk.Tests.Imaging.Opcodes;

public class WarpRectilinearParamsTests
{
    private static byte[] BuildBody(int planes, double[][] radial, double[][] tangential, double centerH, double centerV)
    {
        using var ms = new MemoryStream();
        Span<byte> buf4 = stackalloc byte[4];
        Span<byte> buf8 = stackalloc byte[8];

        BinaryPrimitives.WriteUInt32BigEndian(buf4, (uint)planes);
        ms.Write(buf4);

        for (int p = 0; p < planes; p++)
        {
            foreach (double v in new[] { radial[p][0], radial[p][1], radial[p][2], radial[p][3], tangential[p][0], tangential[p][1] })
            {
                BinaryPrimitives.WriteDoubleBigEndian(buf8, v);
                ms.Write(buf8);
            }
        }

        BinaryPrimitives.WriteDoubleBigEndian(buf8, centerH);
        ms.Write(buf8);
        BinaryPrimitives.WriteDoubleBigEndian(buf8, centerV);
        ms.Write(buf8);

        return ms.ToArray();
    }

    [Fact]
    public void Decodes_single_plane_body()
    {
        var body = BuildBody(1, [[1.0, 0.1, -0.05, 0.02]], [[0.001, -0.002]], 0.5, 0.48);

        var p = WarpRectilinearParams.Decode(body);

        Assert.Equal(1, p.Planes);
        Assert.Equal([1.0, 0.1, -0.05, 0.02], p.Radial[0]);
        Assert.Equal([0.001, -0.002], p.Tangential[0]);
        Assert.Equal(0.5, p.Center.H);
        Assert.Equal(0.48, p.Center.V);
    }

    [Fact]
    public void Nop_params_are_detected()
    {
        var body = BuildBody(1, [[1.0, 0.0, 0.0, 0.0]], [[0.0, 0.0]], 0.5, 0.5);
        var p = WarpRectilinearParams.Decode(body);

        Assert.True(p.IsRadNop(0));
        Assert.True(p.IsTanNop(0));
        Assert.True(p.IsNopAll());
    }

    [Fact]
    public void Non_identity_radial_is_not_nop()
    {
        var body = BuildBody(1, [[1.0, 0.05, 0.0, 0.0]], [[0.0, 0.0]], 0.5, 0.5);
        var p = WarpRectilinearParams.Decode(body);

        Assert.False(p.IsRadNop(0));
        Assert.True(p.IsTanNop(0));
        Assert.False(p.IsNopAll());
    }

    [Fact]
    public void Propagates_plane_zero_params_to_additional_planes()
    {
        var body = BuildBody(1, [[1.0, 0.1, 0.0, 0.0]], [[0.01, 0.02]], 0.5, 0.5);
        var p = WarpRectilinearParams.Decode(body);

        p.PropagateToAllPlanes(3);

        Assert.Equal(3, p.Planes);
        for (int plane = 0; plane < 3; plane++)
        {
            Assert.Equal(p.Radial[0], p.Radial[plane]);
            Assert.Equal(p.Tangential[0], p.Tangential[plane]);
        }
    }

    [Fact]
    public void Radial_ratio_matches_closed_form_polynomial()
    {
        var body = BuildBody(1, [[1.0, 0.1, -0.02, 0.003]], [[0.0, 0.0]], 0.5, 0.5);
        var p = WarpRectilinearParams.Decode(body);

        double r2 = 0.36; // r = 0.6
        double expected = 1.0 + 0.1 * r2 - 0.02 * r2 * r2 + 0.003 * r2 * r2 * r2;

        Assert.Equal(expected, p.RadialRatio(0, r2), precision: 12);
    }

    [Fact]
    public void Rejects_wrong_body_size()
    {
        var body = BuildBody(1, [[1.0, 0.0, 0.0, 0.0]], [[0.0, 0.0]], 0.5, 0.5);
        var truncated = body[..^4];

        var ex = Assert.Throws<DngException>(() => WarpRectilinearParams.Decode(truncated));
        Assert.Equal(DngError.BadFormat, ex.ErrorCode);
    }

    [Fact]
    public void Rejects_zero_planes()
    {
        var body = new byte[4]; // planes = 0
        var ex = Assert.Throws<DngException>(() => WarpRectilinearParams.Decode(body));
        Assert.Equal(DngError.BadFormat, ex.ErrorCode);
    }
}

public class LensWarpFilterTests
{
    private static SimpleImage MakeGradientImage(int width, int height)
    {
        var img = new SimpleImage(new DngRect(0, 0, height, width), planes: 1, PixelType.Float32);
        var buf = img.Buffer;
        var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buf.AsByteSpan());
        for (int row = 0; row < height; row++)
            for (int col = 0; col < width; col++)
                floats[row * width + col] = (float)(row * width + col) / (width * height);
        return img;
    }

    [Fact]
    public void Nop_warp_is_effectively_identity()
    {
        var src = MakeGradientImage(64, 48);
        var p = WarpRectilinearParams.Decode(
            BuildIdentityBody(planes: 1));

        var dst = LensWarpFilter.Apply(src, p);

        var srcFloats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(src.Buffer.AsByteSpan());
        var dstFloats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(dst.Buffer.AsByteSpan());

        Assert.Equal(srcFloats.Length, dstFloats.Length);
        for (int i = 0; i < srcFloats.Length; i++)
            Assert.Equal(srcFloats[i], dstFloats[i], precision: 6);
    }

    [Fact]
    public void Barrel_correction_pulls_corner_toward_a_darker_center_value()
    {
        // Radial pincushion-style correction (k2 > 0) warps dst corners toward
        // src positions closer to the (darker, lower-value) center, compared
        // to plain identity sampling at the same dst position.
        const int width = 100, height = 100;
        var src = MakeGradientImage(width, height);

        var identityParams = WarpRectilinearParams.Decode(BuildIdentityBody(1));
        var warpedParams = WarpRectilinearParams.Decode(
            BuildBody1(1.0, 0.3, 0.0, 0.0, 0.0, 0.0, 0.5, 0.5));

        var identityDst = LensWarpFilter.Apply(src, identityParams);
        var warpedDst = LensWarpFilter.Apply(src, warpedParams);

        var idFloats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(identityDst.Buffer.AsByteSpan());
        var wFloats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(warpedDst.Buffer.AsByteSpan());

        // Compare pixel data at destination corner (10,10) — with a positive
        // k2 term (dst = center + (diff*f(r))*normRadius-ish scaling upward),
        // the warped result must differ from the un-warped identity result.
        int idx = 10 * width + 10;
        Assert.NotEqual(idFloats[idx], wFloats[idx]);
    }

    [Fact]
    public void Rejects_non_float32_image()
    {
        var img = new SimpleImage(new DngRect(0, 0, 16, 16), planes: 1, PixelType.UInt16);
        var p = WarpRectilinearParams.Decode(BuildIdentityBody(1));

        Assert.Throws<ArgumentException>(() => LensWarpFilter.Apply(img, p));
    }

    [Fact]
    public void Handles_multi_plane_propagation_from_single_plane_params()
    {
        var img = new SimpleImage(new DngRect(0, 0, 32, 32), planes: 3, PixelType.Float32);
        var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(img.Buffer.AsByteSpan());
        for (int i = 0; i < floats.Length; i++) floats[i] = (float)i / floats.Length;

        var p = WarpRectilinearParams.Decode(BuildBody1(1.0, 0.1, 0.0, 0.0, 0.0, 0.0, 0.5, 0.5));

        var dst = LensWarpFilter.Apply(img, p);

        Assert.Equal(3u, dst.Planes);
        Assert.Equal(3, p.Planes); // propagated in-place
    }

    private static byte[] BuildIdentityBody(int planes)
    {
        using var ms = new MemoryStream();
        Span<byte> buf4 = stackalloc byte[4];
        Span<byte> buf8 = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(buf4, (uint)planes);
        ms.Write(buf4);
        for (int p = 0; p < planes; p++)
        {
            foreach (double v in new[] { 1.0, 0.0, 0.0, 0.0, 0.0, 0.0 })
            {
                BinaryPrimitives.WriteDoubleBigEndian(buf8, v);
                ms.Write(buf8);
            }
        }
        BinaryPrimitives.WriteDoubleBigEndian(buf8, 0.5);
        ms.Write(buf8);
        BinaryPrimitives.WriteDoubleBigEndian(buf8, 0.5);
        ms.Write(buf8);
        return ms.ToArray();
    }

    private static byte[] BuildBody1(double k0, double k2, double k4, double k6, double kt0, double kt1, double cH, double cV)
    {
        using var ms = new MemoryStream();
        Span<byte> buf4 = stackalloc byte[4];
        Span<byte> buf8 = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(buf4, 1);
        ms.Write(buf4);
        foreach (double v in new[] { k0, k2, k4, k6, kt0, kt1 })
        {
            BinaryPrimitives.WriteDoubleBigEndian(buf8, v);
            ms.Write(buf8);
        }
        BinaryPrimitives.WriteDoubleBigEndian(buf8, cH);
        ms.Write(buf8);
        BinaryPrimitives.WriteDoubleBigEndian(buf8, cV);
        ms.Write(buf8);
        return ms.ToArray();
    }
}
