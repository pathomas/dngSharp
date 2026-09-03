using Dng.Sdk.Render;

namespace Dng.Sdk.Tests.Render;

public class HdrEncodingTests
{
    [Fact]
    public void Zero_is_fixed_and_endpoint_compresses()
    {
        // f(0) = 0 — true fixed point.
        Assert.Equal(0.0, HdrEncoding.Encode(0.0), 12);
        Assert.Equal(0.0, HdrEncoding.Decode(0.0), 12);
        // f(1) = 1 · 257 / (256 · 2) = 257/512 ≈ 0.502.
        // The HDR function compresses the SDR range — it is NOT identity at 1.
        Assert.Equal(257.0 / 512.0, HdrEncoding.Encode(1.0), 12);
    }

    [Theory]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(0.99)]
    [InlineData(2.0)]      // HDR range > 1
    [InlineData(10.0)]
    [InlineData(100.0)]
    public void Encode_decode_round_trips(double x)
    {
        var encoded = HdrEncoding.Encode(x);
        var decoded = HdrEncoding.Decode(encoded);
        Assert.Equal(x, decoded, 8);
    }

    [Fact]
    public void Negative_inputs_pass_through_unchanged()
    {
        // Sub-zero (sensor noise) values must NOT be clipped or remapped.
        Assert.Equal(-0.05, HdrEncoding.Encode(-0.05));
        Assert.Equal(-0.05, HdrEncoding.Decode(-0.05));
    }

    [Fact]
    public void Encode_at_x100_lies_in_expected_band()
    {
        // At x = 100: f = 100 · 356 / (256 · 101) ≈ 1.377.
        // The output exceeds 1.0 in the HDR region — that's the whole point.
        var encoded = HdrEncoding.Encode(100.0);
        Assert.InRange(encoded, 1.3, 1.5);

        var huge = HdrEncoding.Encode(1e6);
        Assert.True(double.IsFinite(huge));
    }

    [Fact]
    public void Wrap_lookup_sdr_bypasses_encoding()
    {
        static double identity(double v) => v;

        // SDR: lookup is called directly with x — identity returns x.
        Assert.Equal(0.5, HdrEncoding.WrapLookup(0.5, identity, useHdr: false), 12);
        // HDR: lookup is called with encode(x) and result is decoded —
        // for an identity lookup the round-trip yields x exactly.
        Assert.Equal(0.5, HdrEncoding.WrapLookup(0.5, identity, useHdr: true), 8);

        // Non-trivial lookup: squaring.
        static double sq(double v) => v * v;
        var sdr = HdrEncoding.WrapLookup(2.0, sq, useHdr: false);
        var hdr = HdrEncoding.WrapLookup(2.0, sq, useHdr: true);
        Assert.Equal(4.0, sdr, 12);
        Assert.NotEqual(4.0, hdr); // HDR-wrapped sq differs from plain sq
    }
}
