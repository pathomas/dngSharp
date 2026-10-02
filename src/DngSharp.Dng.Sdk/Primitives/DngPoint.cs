using System.Runtime.CompilerServices;
using DngSharp.Dng.Sdk.Math;

namespace DngSharp.Dng.Sdk.Primitives;

/// <summary>
/// Integer 2D point. Mirrors <c>dng_point</c>. Note the field order: DNG uses
/// (v, h) = (vertical, horizontal), not (x, y) — we preserve that to avoid
/// silent transposes when porting algorithms.
/// </summary>
public readonly record struct DngPoint(int V, int H)
{
    public double Length() => System.Math.Sqrt((double)V * V + (double)H * H);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DngPoint operator +(DngPoint a, DngPoint b) =>
        new(SafeArith.Add(a.V, b.V), SafeArith.Add(a.H, b.H));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DngPoint operator -(DngPoint a, DngPoint b) =>
        new(SafeArith.Sub(a.V, b.V), SafeArith.Sub(a.H, b.H));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DngPoint operator -(DngPoint p) =>
        new(SafeArith.Sub(0, p.V), SafeArith.Sub(0, p.H));

    public static DngPoint Transpose(DngPoint a) => new(a.H, a.V);

    /// <summary>
    /// 90° rotation: <c>(-h, v)</c>. Mirrors C++'s <c>MakePerpendicular</c>,
    /// which routes negation through safe arithmetic so <c>INT_MIN</c> throws
    /// rather than silently overflowing.
    /// </summary>
    public static DngPoint MakePerpendicular(DngPoint p) =>
        new(SafeArith.Sub(0, p.H), p.V);
}

/// <summary>
/// Double-precision 2D point. Mirrors <c>dng_point_real64</c>.
/// </summary>
public readonly record struct DngPointF(double V, double H)
{
    public DngPointF(DngPoint p) : this(p.V, p.H) { }

    public double Length() => System.Math.Sqrt(V * V + H * H);

    public DngPoint Round() =>
        new(DngMath.RoundToInt32(V), DngMath.RoundToInt32(H));

    public DngPointF Scale(double k) => new(V * k, H * k);

    public DngPointF Normalize()
    {
        double len = Length();
        return len == 0 ? this : Scale(1.0 / len);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DngPointF operator +(DngPointF a, DngPointF b) => new(a.V + b.V, a.H + b.H);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DngPointF operator -(DngPointF a, DngPointF b) => new(a.V - b.V, a.H - b.H);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DngPointF operator -(DngPointF p) => new(-p.V, -p.H);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DngPointF operator *(double k, DngPointF p) => new(p.V * k, p.H * k);

    public static double Distance(DngPointF a, DngPointF b) => (a - b).Length();

    public static double DistanceSquared(DngPointF a, DngPointF b)
    {
        var d = a - b;
        return d.V * d.V + d.H * d.H;
    }

    public static double Dot(DngPointF a, DngPointF b) => a.H * b.H + a.V * b.V;

    public static DngPointF Lerp(DngPointF a, DngPointF b, double t) =>
        new(DngMath.Lerp(a.V, b.V, t), DngMath.Lerp(a.H, b.H, t));

    public static DngPointF Transpose(DngPointF a) => new(a.H, a.V);

    public static DngPointF MakePerpendicular(DngPointF p) => new(-p.H, p.V);
}
