using System.Buffers.Binary;
using Dng.Sdk.Errors;
using Dng.Sdk.Imaging.Opcodes;

namespace Dng.Sdk.Tests.Imaging.Opcodes;

public class WarpFisheyeParamsTests
{
    private static byte[] BuildBody(int planes, double[][] radial, double centerH, double centerV)
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
        var body = BuildBody(1, [[1.0, 0.1, -0.02, 0.003]], 0.5, 0.48);
        var p = WarpFisheyeParams.Decode(body);

        Assert.Equal(1, p.Planes);
        Assert.Equal([1.0, 0.1, -0.02, 0.003], p.Radial[0]);
        Assert.Equal(0.5, p.Center.H);
        Assert.Equal(0.48, p.Center.V);
    }

    [Fact]
    public void Propagates_plane_zero_params_to_additional_planes()
    {
        var body = BuildBody(1, [[1.0, 0.1, 0.0, 0.0]], 0.5, 0.5);
        var p = WarpFisheyeParams.Decode(body);

        p.PropagateToAllPlanes(3);

        Assert.Equal(3, p.Planes);
        for (int plane = 0; plane < 3; plane++)
            Assert.Equal(p.Radial[0], p.Radial[plane]);
    }

    [Fact]
    public void Rad_nop_is_always_false()
    {
        // Even with all-zero coefficients, the fisheye model is never a NOP
        // (matches native dng_warp_params_fisheye::IsRadNOP, unconditional false).
        var body = BuildBody(1, [[0.0, 0.0, 0.0, 0.0]], 0.5, 0.5);
        var p = WarpFisheyeParams.Decode(body);

        Assert.False(p.IsRadNop(0));
        Assert.True(p.IsTanNop(0));
        Assert.False(p.IsNopAll());
    }

    [Fact]
    public void Radial_ratio_matches_closed_form_atan_polynomial()
    {
        var body = BuildBody(1, [[1.0, 0.1, -0.02, 0.003]], 0.5, 0.5);
        var p = WarpFisheyeParams.Decode(body);

        double r2 = 0.36; // r = 0.6
        double r = 0.6;
        double t = System.Math.Atan(r);
        double expected = (1.0 * t + 0.1 * t * t * t - 0.02 * t * t * t * t * t + 0.003 * t * t * t * t * t * t * t) / r;

        Assert.Equal(expected, p.RadialRatio(0, r2), precision: 10);
    }

    [Fact]
    public void Radial_ratio_returns_one_when_r_is_near_zero()
    {
        var body = BuildBody(1, [[2.0, 0.0, 0.0, 0.0]], 0.5, 0.5);
        var p = WarpFisheyeParams.Decode(body);

        Assert.Equal(1.0, p.RadialRatio(0, 1.0e-14), precision: 12);
    }

    [Fact]
    public void EvaluateTangential_throws_program_error()
    {
        var body = BuildBody(1, [[1.0, 0.0, 0.0, 0.0]], 0.5, 0.5);
        var p = WarpFisheyeParams.Decode(body);

        var ex = Assert.Throws<DngException>(() => p.EvaluateTangential(0, 0.1, 0.1, 0.1, 0.01, 0.01));
        Assert.Equal(DngError.Unknown, ex.ErrorCode);
    }

    [Fact]
    public void Rejects_wrong_body_size()
    {
        var body = BuildBody(1, [[1.0, 0.0, 0.0, 0.0]], 0.5, 0.5);
        var truncated = body[..^4];

        var ex = Assert.Throws<DngException>(() => WarpFisheyeParams.Decode(truncated));
        Assert.Equal(DngError.BadFormat, ex.ErrorCode);
    }

    [Fact]
    public void Rejects_zero_planes()
    {
        var body = new byte[4]; // planes = 0
        var ex = Assert.Throws<DngException>(() => WarpFisheyeParams.Decode(body));
        Assert.Equal(DngError.BadFormat, ex.ErrorCode);
    }

    [Fact]
    public void Rejects_out_of_range_center()
    {
        var body = BuildBody(1, [[1.0, 0.0, 0.0, 0.0]], 1.5, 0.5);
        var ex = Assert.Throws<DngException>(() => WarpFisheyeParams.Decode(body));
        Assert.Equal(DngError.BadFormat, ex.ErrorCode);
    }

    [Fact]
    public void Works_with_lens_warp_filter_via_shared_interface()
    {
        var body = BuildBody(1, [[1.0, 0.05, 0.0, 0.0]], 0.5, 0.5);
        var p = WarpFisheyeParams.Decode(body);

        var src = new Dng.Sdk.Imaging.SimpleImage(
            new Dng.Sdk.Primitives.DngRect(0, 0, 16, 16), planes: 1, Dng.Sdk.Pixels.PixelType.Float32);
        var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(src.Buffer.AsByteSpan());
        for (int i = 0; i < floats.Length; i++) floats[i] = (float)i / floats.Length;

        var dst = LensWarpFilter.Apply(src, p);
        Assert.Equal(src.Bounds, dst.Bounds);
    }
}
