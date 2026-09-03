using System.Buffers.Binary;
using Dng.Sdk.Errors;
using Dng.Sdk.Imaging.Opcodes;

namespace Dng.Sdk.Tests.Imaging.Opcodes;

public class WarpRectilinear2ParamsTests
{
    private static byte[] BuildBody(
        int planes, double[][] radial, double[][] tangential, double[][] validRange,
        double centerH, double centerV, bool useReciprocal)
    {
        using var ms = new MemoryStream();
        Span<byte> buf4 = stackalloc byte[4];
        Span<byte> buf8 = stackalloc byte[8];

        BinaryPrimitives.WriteUInt32BigEndian(buf4, (uint)planes);
        ms.Write(buf4);

        for (int p = 0; p < planes; p++)
        {
            foreach (double v in radial[p])
            {
                BinaryPrimitives.WriteDoubleBigEndian(buf8, v);
                ms.Write(buf8);
            }
            foreach (double v in tangential[p])
            {
                BinaryPrimitives.WriteDoubleBigEndian(buf8, v);
                ms.Write(buf8);
            }
            foreach (double v in validRange[p])
            {
                BinaryPrimitives.WriteDoubleBigEndian(buf8, v);
                ms.Write(buf8);
            }
        }

        BinaryPrimitives.WriteDoubleBigEndian(buf8, centerH);
        ms.Write(buf8);
        BinaryPrimitives.WriteDoubleBigEndian(buf8, centerV);
        ms.Write(buf8);

        BinaryPrimitives.WriteUInt32BigEndian(buf4, useReciprocal ? 1u : 0u);
        ms.Write(buf4);

        return ms.ToArray();
    }

    private static double[] Kr(params double[] leading)
    {
        var kr = new double[WarpRectilinear2Params.RadialTerms];
        leading.CopyTo(kr, 0);
        return kr;
    }

    [Fact]
    public void Decodes_single_plane_body()
    {
        var body = BuildBody(
            1, [Kr(1.0, 0.1, -0.02)], [[0.001, -0.002]], [[0.0, 1.0]],
            0.5, 0.48, useReciprocal: false);

        var p = WarpRectilinear2Params.Decode(body);

        Assert.Equal(1, p.Planes);
        Assert.Equal(15, p.Radial[0].Length);
        Assert.Equal(1.0, p.Radial[0][0]);
        Assert.Equal(0.1, p.Radial[0][1]);
        Assert.Equal(-0.02, p.Radial[0][2]);
        Assert.Equal([0.001, -0.002], p.Tangential[0]);
        Assert.Equal([0.0, 1.0], p.ValidRange[0]);
        Assert.Equal(0.5, p.Center.H);
        Assert.Equal(0.48, p.Center.V);
        Assert.False(p.UseReciprocal);
    }

    [Fact]
    public void Decodes_use_reciprocal_flag()
    {
        var body = BuildBody(1, [Kr(1.0)], [[0.0, 0.0]], [[0.0, 1.0]], 0.5, 0.5, useReciprocal: true);
        var p = WarpRectilinear2Params.Decode(body);
        Assert.True(p.UseReciprocal);
    }

    [Fact]
    public void Nop_params_are_detected()
    {
        var body = BuildBody(1, [Kr(1.0)], [[0.0, 0.0]], [[0.0, 1.0]], 0.5, 0.5, useReciprocal: false);
        var p = WarpRectilinear2Params.Decode(body);

        Assert.True(p.IsRadNop(0));
        Assert.True(p.IsTanNop(0));
        Assert.True(p.IsNopAll());
    }

    [Fact]
    public void Non_identity_radial_is_not_nop()
    {
        var body = BuildBody(1, [Kr(1.0, 0.05)], [[0.0, 0.0]], [[0.0, 1.0]], 0.5, 0.5, useReciprocal: false);
        var p = WarpRectilinear2Params.Decode(body);

        Assert.False(p.IsRadNop(0));
        Assert.True(p.IsTanNop(0));
        Assert.False(p.IsNopAll());
    }

    [Fact]
    public void Propagates_plane_zero_params_to_additional_planes()
    {
        var body = BuildBody(1, [Kr(1.0, 0.1)], [[0.01, 0.02]], [[0.0, 1.0]], 0.5, 0.5, useReciprocal: false);
        var p = WarpRectilinear2Params.Decode(body);

        p.PropagateToAllPlanes(3);

        Assert.Equal(3, p.Planes);
        for (int plane = 0; plane < 3; plane++)
        {
            Assert.Equal(p.Radial[0], p.Radial[plane]);
            Assert.Equal(p.Tangential[0], p.Tangential[plane]);
            Assert.Equal(p.ValidRange[0], p.ValidRange[plane]);
        }
    }

    [Fact]
    public void Radial_ratio_matches_closed_form_polynomial_with_odd_powers()
    {
        // f(r) = 1.0 + 0.1*r - 0.02*r^2 (odd power r^1 term exercises the
        // Horner-on-r evaluation that the restricted DNG 1.3 model can't express).
        var body = BuildBody(1, [Kr(1.0, 0.1, -0.02)], [[0.0, 0.0]], [[0.0, 1.0]], 0.5, 0.5, useReciprocal: false);
        var p = WarpRectilinear2Params.Decode(body);

        double r2 = 0.36; // r = 0.6
        double r = 0.6;
        double expected = 1.0 + 0.1 * r - 0.02 * r * r;

        Assert.Equal(expected, p.RadialRatio(0, r2), precision: 12);
    }

    [Fact]
    public void Radial_ratio_uses_reciprocal_when_flag_set()
    {
        var body = BuildBody(1, [Kr(2.0)], [[0.0, 0.0]], [[0.0, 1.0]], 0.5, 0.5, useReciprocal: true);
        var p = WarpRectilinear2Params.Decode(body);

        // f(r) = 2.0 constant -> reciprocal = 0.5.
        Assert.Equal(0.5, p.RadialRatio(0, 0.25), precision: 12);
    }

    [Fact]
    public void Radial_ratio_clamps_to_valid_radius_range()
    {
        // f(r) = r (identity-ish), valid range [0.2, 0.8].
        var body = BuildBody(1, [Kr(0.0, 1.0)], [[0.0, 0.0]], [[0.2, 0.8]], 0.5, 0.5, useReciprocal: false);
        var p = WarpRectilinear2Params.Decode(body);

        // r2 = 0.81 (r=0.9) is above maxValidRadius=0.8, so it's clamped to 0.8 -> f(0.8) = 0.8.
        Assert.Equal(0.8, p.RadialRatio(0, 0.81), precision: 12);

        // r2 = 0.01 (r=0.1) is below minValidRadius=0.2, clamped to 0.2 -> f(0.2) = 0.2.
        Assert.Equal(0.2, p.RadialRatio(0, 0.01), precision: 12);
    }

    [Fact]
    public void Rejects_invalid_valid_radius_range()
    {
        // min >= max is invalid.
        var body = BuildBody(1, [Kr(1.0)], [[0.0, 0.0]], [[0.5, 0.5]], 0.5, 0.5, useReciprocal: false);
        var ex = Assert.Throws<DngException>(() => WarpRectilinear2Params.Decode(body));
        Assert.Equal(DngError.BadFormat, ex.ErrorCode);
    }

    [Fact]
    public void Rejects_wrong_body_size()
    {
        var body = BuildBody(1, [Kr(1.0)], [[0.0, 0.0]], [[0.0, 1.0]], 0.5, 0.5, useReciprocal: false);
        var truncated = body[..^4];

        var ex = Assert.Throws<DngException>(() => WarpRectilinear2Params.Decode(truncated));
        Assert.Equal(DngError.BadFormat, ex.ErrorCode);
    }

    [Fact]
    public void Rejects_zero_planes()
    {
        var body = new byte[4]; // planes = 0
        var ex = Assert.Throws<DngException>(() => WarpRectilinear2Params.Decode(body));
        Assert.Equal(DngError.BadFormat, ex.ErrorCode);
    }

    [Fact]
    public void Works_with_lens_warp_filter_via_shared_interface()
    {
        // Sanity check: WarpRectilinear2Params satisfies IWarpRectilinearParams
        // and LensWarpFilter.Apply accepts it directly (NOP fast path).
        var body = BuildBody(1, [Kr(1.0)], [[0.0, 0.0]], [[0.0, 1.0]], 0.5, 0.5, useReciprocal: false);
        var p = WarpRectilinear2Params.Decode(body);

        var src = new Dng.Sdk.Imaging.SimpleImage(
            new Dng.Sdk.Primitives.DngRect(0, 0, 8, 8), planes: 1, Dng.Sdk.Pixels.PixelType.Float32);

        var dst = LensWarpFilter.Apply(src, p);
        Assert.Equal(src.Bounds, dst.Bounds);
    }
}
