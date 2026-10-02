using DngSharp.Dng.Sdk.Errors;
using DngSharp.Dng.Sdk.Primitives;

namespace DngSharp.Dng.Sdk.Tests.Primitives;

public class DngRectTests
{
    [Fact]
    public void Default_is_empty_and_zero()
    {
        var r = default(DngRect);
        Assert.True(r.IsZero);
        Assert.True(r.IsEmpty);
    }

    [Fact]
    public void Size_constructors()
    {
        var byHW = new DngRect(10u, 20u);
        Assert.Equal((uint)20, byHW.W);
        Assert.Equal((uint)10, byHW.H);

        var byPoint = new DngRect(new DngPoint(10, 20));
        Assert.Equal(byHW, byPoint);
    }

    [Fact]
    public void W_H_throw_when_size_overflows_int32()
    {
        // Can't construct via TLBR if it would overflow, but mutate directly:
        var r = new DngRect { L = int.MinValue, R = int.MaxValue, T = 0, B = 1 };
        Assert.Throws<DngException>(() => _ = r.W);
    }

    [Fact]
    public void Contains_rect_and_point()
    {
        var outer = new DngRect(0, 0, 100, 100);
        Assert.True(outer.Contains(new DngRect(10, 10, 20, 20)));
        Assert.False(outer.Contains(new DngRect(-1, 0, 1, 1)));
        Assert.True(outer.Contains(new DngPoint(50, 50)));
        Assert.False(outer.Contains(new DngPoint(100, 100))); // bottom/right exclusive
    }

    [Fact]
    public void Intersect_and_union()
    {
        var a = new DngRect(0, 0, 10, 10);
        var b = new DngRect(5, 5, 15, 15);
        Assert.Equal(new DngRect(5, 5, 10, 10), DngRect.Intersect(a, b));
        Assert.Equal(new DngRect(0, 0, 15, 15), DngRect.Union(a, b));
    }

    [Fact]
    public void Disjoint_intersect_is_default()
    {
        var a = new DngRect(0, 0, 10, 10);
        var b = new DngRect(20, 20, 30, 30);
        Assert.Equal(default, DngRect.Intersect(a, b));
    }

    [Fact]
    public void Translate_by_point()
    {
        var r = new DngRect(0, 0, 10, 10);
        var t = r + new DngPoint(5, 7);
        Assert.Equal(new DngRect(5, 7, 15, 17), t);
        Assert.Equal(r, t - new DngPoint(5, 7));
    }

    [Fact]
    public void Long_short_side()
    {
        var r = new DngRect(0, 0, 30, 50);
        Assert.Equal((uint)50, r.LongSide);
        Assert.Equal((uint)30, r.ShortSide);
    }
}
