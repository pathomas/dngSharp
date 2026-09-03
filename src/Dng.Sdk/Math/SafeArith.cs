using System.Runtime.CompilerServices;
using Dng.Sdk.Errors;

namespace Dng.Sdk.Math;

/// <summary>
/// Checked / saturating integer math used throughout the SDK. Mirrors
/// <c>dng_safe_arithmetic.h</c>. C# already has <c>checked</c> blocks; these
/// helpers translate the C++ try/return-bool pattern into either throwing
/// (default) or <c>TryXxx</c> overloads.
/// </summary>
public static class SafeArith
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Add(int a, int b)
    {
        try { return checked(a + b); }
        catch (OverflowException) { DngThrow.Overflow("int+int"); return 0; }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Sub(int a, int b)
    {
        try { return checked(a - b); }
        catch (OverflowException) { DngThrow.Overflow("int-int"); return 0; }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Mul(int a, int b)
    {
        try { return checked(a * b); }
        catch (OverflowException) { DngThrow.Overflow("int*int"); return 0; }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint AddU(uint a, uint b)
    {
        try { return checked(a + b); }
        catch (OverflowException) { DngThrow.Overflow("uint+uint"); return 0; }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint SubU(uint a, uint b)
    {
        try { return checked(a - b); }
        catch (OverflowException) { DngThrow.Overflow("uint-uint"); return 0; }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint MulU(uint a, uint b)
    {
        try { return checked(a * b); }
        catch (OverflowException) { DngThrow.Overflow("uint*uint"); return 0; }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryAdd(int a, int b, out int result)
    {
        long sum = (long)a + b;
        result = unchecked((int)sum);
        return sum >= int.MinValue && sum <= int.MaxValue;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TrySub(int a, int b, out int result)
    {
        long diff = (long)a - b;
        result = unchecked((int)diff);
        return diff >= int.MinValue && diff <= int.MaxValue;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryMul(int a, int b, out int result)
    {
        long product = (long)a * b;
        result = unchecked((int)product);
        return product >= int.MinValue && product <= int.MaxValue;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryConvertUInt32ToInt32(uint value, out int result)
    {
        result = unchecked((int)value);
        return value <= int.MaxValue;
    }

    /// <summary>
    /// Multiplies two <see cref="uint"/>s into a <see cref="uint"/>, throwing
    /// on overflow. Useful for tile-size sanity checks.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint MulUSize(uint a, uint b)
    {
        ulong product = (ulong)a * b;
        if (product > uint.MaxValue)
            DngThrow.Overflow("uint*uint");
        return (uint)product;
    }
}
