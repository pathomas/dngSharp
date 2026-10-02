using DngSharp.Dng.Sdk.Errors;
using DngSharp.Dng.Sdk.Math;

namespace DngSharp.Dng.Sdk.Tests.Math;

public class SafeArithTests
{
    [Theory]
    [InlineData(int.MaxValue, 1)]
    [InlineData(int.MinValue, -1)]
    public void Add_throws_on_overflow(int a, int b)
    {
        Assert.Throws<DngException>(() => SafeArith.Add(a, b));
    }

    [Theory]
    [InlineData(int.MinValue, 1)]
    [InlineData(int.MaxValue, -1)]
    public void Sub_throws_on_overflow(int a, int b)
    {
        Assert.Throws<DngException>(() => SafeArith.Sub(a, b));
    }

    [Fact]
    public void Sub_zero_minus_intmin_throws()
    {
        // The motivating MakePerpendicular case from dng_point.h.
        Assert.Throws<DngException>(() => SafeArith.Sub(0, int.MinValue));
    }

    [Fact]
    public void TryAdd_reports_overflow_without_throwing()
    {
        Assert.False(SafeArith.TryAdd(int.MaxValue, 1, out _));
        Assert.True(SafeArith.TryAdd(1, 2, out var r));
        Assert.Equal(3, r);
    }

    [Fact]
    public void TryConvertUInt32ToInt32_rejects_out_of_range()
    {
        Assert.True(SafeArith.TryConvertUInt32ToInt32(0, out var z));
        Assert.Equal(0, z);
        Assert.True(SafeArith.TryConvertUInt32ToInt32((uint)int.MaxValue, out var max));
        Assert.Equal(int.MaxValue, max);
        Assert.False(SafeArith.TryConvertUInt32ToInt32((uint)int.MaxValue + 1u, out _));
    }
}
