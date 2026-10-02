using System.Runtime.CompilerServices;

namespace DngSharp.Dng.Sdk.Render;

/// <summary>
/// HDR encode/decode wrap used around table lookups when the active camera
/// profile has <c>ProfileDynamicRange = 1</c> (DNG 1.7+).
///
/// <para>The forward encoding is <c>f(x) = x · (256 + x) / (256 · (1 + x))</c>
/// (spec ch. 6 — HDR encode). Its inverse maps the encoded value back to
/// scene-referred linear. Together they bracket lookups into HSV maps, tone
/// curves, and look tables so that table contents (designed for SDR domain
/// [0, 1]) can drive HDR scene values without clipping.</para>
///
/// <para><b>Don't clip intermediate values to [0, 1] inside HDR sections.</b>
/// The spec explicitly preserves the extended range; clipping there would
/// silently lose scene highlights.</para>
/// </summary>
public static class HdrEncoding
{
    /// <summary>
    /// Forward HDR encode: <c>f(x) = x·(256+x) / (256·(1+x))</c>. Compresses
    /// the non-negative scene-referred range. <b>NOT a fixed-point at x=1</b>
    /// — f(1) = 257/512 ≈ 0.502. The function's purpose is to map an
    /// unbounded HDR range so that table lookups (designed for SDR-style
    /// inputs) cover the full scene-referred range without saturation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Encode(double x)
    {
        if (x <= 0) return x;            // preserve sub-zero and zero
        return x * (256.0 + x) / (256.0 * (1.0 + x));
    }

    /// <summary>
    /// Inverse HDR encode. Numerically: solve <c>x·(256+x)/(256·(1+x)) = y</c>
    /// for <c>x</c> in <c>x ≥ 0</c>.
    ///
    /// <para>Expanding gives the quadratic <c>x² + (256 − 256·y)·x − 256·y = 0</c>,
    /// whose positive root is
    /// <c>x = ((256·y − 256) + √((256 − 256·y)² + 4·256·y)) / 2</c>.</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Decode(double y)
    {
        if (y <= 0) return y;
        double b = 256.0 - 256.0 * y;
        double disc = b * b + 4.0 * 256.0 * y;
        return (-b + System.Math.Sqrt(disc)) * 0.5;
    }

    /// <summary>
    /// Apply <paramref name="lookup"/> with HDR encode/decode wrapping when
    /// <paramref name="useHdr"/> is true; otherwise just call the lookup
    /// directly (SDR path).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double WrapLookup(double x, Func<double, double> lookup, bool useHdr) =>
        useHdr ? Decode(lookup(Encode(x))) : lookup(x);
}
