using System.Runtime.CompilerServices;

namespace Dng.Sdk.Math;

/// <summary>
/// Scalar math helpers used throughout the DNG SDK. Mirrors the inline helpers
/// in <c>dng_utils.h</c>.
/// </summary>
public static class DngMath
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int RoundToInt32(double x) =>
        (int)System.Math.Round(x, MidpointRounding.AwayFromZero);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int RoundToInt32(float x) =>
        (int)MathF.Round(x, MidpointRounding.AwayFromZero);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint RoundToUInt32(double x) =>
        (uint)System.Math.Round(System.Math.Max(0.0, x), MidpointRounding.AwayFromZero);

    /// <summary>
    /// Hardware-friendly linear interpolation. Matches C++'s
    /// <c>Lerp_real64(a,b,t) = a + t*(b-a)</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Lerp(double a, double b, double t) => a + t * (b - a);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Lerp(float a, float b, float t) => a + t * (b - a);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Pin(double low, double x, double high) =>
        x < low ? low : (x > high ? high : x);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Pin(int low, int x, int high) =>
        x < low ? low : (x > high ? high : x);

    /// <summary>Returns ceil(a/b) for unsigned values.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint RoundUp(uint a, uint b) => (a + b - 1) / b;

    /// <summary>Returns the smallest multiple of <paramref name="b"/> >= <paramref name="a"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint RoundUpToMultiple(uint a, uint b) => RoundUp(a, b) * b;
}
