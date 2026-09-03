using Dng.Sdk.Errors;
using Dng.Sdk.Primitives;

namespace Dng.Sdk.Tests.Primitives;

public class DngPointTests
{
    [Fact]
    public void Default_is_zero()
    {
        Assert.Equal(new DngPoint(0, 0), default(DngPoint));
    }

    [Fact]
    public void Field_order_is_v_then_h()
    {
        // DNG convention: (vertical, horizontal). Tests guard against accidental (x,y).
        var p = new DngPoint(7, 3);
        Assert.Equal(7, p.V);
        Assert.Equal(3, p.H);
    }

    [Fact]
    public void Length_uses_hypot()
    {
        var p = new DngPoint(3, 4);
        Assert.Equal(5.0, p.Length(), precision: 12);
    }

    [Fact]
    public void Add_subtract_negate()
    {
        var a = new DngPoint(1, 2);
        var b = new DngPoint(10, 20);
        Assert.Equal(new DngPoint(11, 22), a + b);
        Assert.Equal(new DngPoint(-9, -18), a - b);
        Assert.Equal(new DngPoint(-1, -2), -a);
    }

    [Fact]
    public void MakePerpendicular_intmin_throws_via_safe_arith()
    {
        // Matches CR-4208475 N-L15 in dng_point.h.
        var bad = new DngPoint(0, int.MinValue);
        Assert.Throws<DngException>(() => DngPoint.MakePerpendicular(bad));
    }

    [Fact]
    public void Add_overflow_throws()
    {
        var a = new DngPoint(int.MaxValue, 0);
        var b = new DngPoint(1, 0);
        Assert.Throws<DngException>(() => _ = a + b);
    }

    [Fact]
    public void Transpose_swaps_axes()
    {
        var p = new DngPoint(7, 3);
        var t = DngPoint.Transpose(p);
        Assert.Equal(new DngPoint(3, 7), t);
    }
}

public class DngPointFTests
{
    [Fact]
    public void Round_uses_away_from_zero()
    {
        var p = new DngPointF(0.5, -0.5);
        Assert.Equal(new DngPoint(1, -1), p.Round());
    }

    [Fact]
    public void Distance_and_dot_and_lerp()
    {
        var a = new DngPointF(0, 0);
        var b = new DngPointF(3, 4);
        Assert.Equal(5.0, DngPointF.Distance(a, b), 12);
        Assert.Equal(25.0, DngPointF.DistanceSquared(a, b), 12);
        Assert.Equal(0.0, DngPointF.Dot(a, b), 12);
        var mid = DngPointF.Lerp(a, b, 0.5);
        Assert.Equal(new DngPointF(1.5, 2.0), mid);
    }

    [Fact]
    public void Normalize_zero_is_safe()
    {
        var z = new DngPointF(0, 0);
        var n = z.Normalize();
        Assert.Equal(0.0, n.V);
        Assert.Equal(0.0, n.H);
    }
}
