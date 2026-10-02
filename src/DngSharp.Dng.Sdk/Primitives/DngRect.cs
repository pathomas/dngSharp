using DngSharp.Dng.Sdk.Errors;
using DngSharp.Dng.Sdk.Math;

namespace DngSharp.Dng.Sdk.Primitives;

/// <summary>
/// Integer rectangle. Mirrors <c>dng_rect</c>. Edges are <c>(t, l, b, r)</c>
/// (top, left, bottom, right) — pixel-coordinate convention from TIFF/DNG.
/// Top-left inclusive, bottom-right exclusive: <c>W = r - l</c>, <c>H = b - t</c>.
/// </summary>
public struct DngRect : IEquatable<DngRect>
{
    public int T;
    public int L;
    public int B;
    public int R;

    public DngRect(int t, int l, int b, int r)
    {
        T = t; L = l; B = b; R = r;

        // Mirror C++ ctor: reject inputs whose size would overflow int32.
        if (!SafeArith.TrySub(r, l, out _) || !SafeArith.TrySub(b, t, out _))
            DngThrow.ProgramError("Overflow in DngRect constructor");
    }

    public DngRect(uint h, uint w)
    {
        T = 0; L = 0;
        if (!SafeArith.TryConvertUInt32ToInt32(h, out B) ||
            !SafeArith.TryConvertUInt32ToInt32(w, out R))
            DngThrow.ProgramError("Overflow in DngRect constructor");
    }

    public DngRect(DngPoint size) : this(0, 0, size.V, size.H) { }

    public static DngRect Empty => default;

    public readonly bool IsZero => T == 0 && L == 0 && B == 0 && R == 0;
    public readonly bool IsEmpty => T >= B || L >= R;
    public readonly bool NotEmpty => !IsEmpty;

    public readonly uint W
    {
        get
        {
            if (R < L) return 0;
            if (!SafeArith.TrySub(R, L, out var w)) DngThrow.Overflow("DngRect.W");
            return (uint)w;
        }
    }

    public readonly uint H
    {
        get
        {
            if (B < T) return 0;
            if (!SafeArith.TrySub(B, T, out var h)) DngThrow.Overflow("DngRect.H");
            return (uint)h;
        }
    }

    public readonly DngPoint TL => new(T, L);
    public readonly DngPoint TR => new(T, R);
    public readonly DngPoint BL => new(B, L);
    public readonly DngPoint BR => new(B, R);
    public readonly DngPoint Size => new((int)H, (int)W);

    public readonly uint LongSide => System.Math.Max(W, H);
    public readonly uint ShortSide => System.Math.Min(W, H);

    public readonly double Diagonal => System.Math.Sqrt((double)W * W + (double)H * H);

    public readonly bool Contains(DngRect other) =>
        other.IsEmpty || (T <= other.T && L <= other.L && B >= other.B && R >= other.R);

    public readonly bool Contains(DngPoint pt) =>
        T <= pt.V && L <= pt.H && B > pt.V && R > pt.H;

    public readonly bool Equals(DngRect other) =>
        T == other.T && L == other.L && B == other.B && R == other.R;

    public override readonly bool Equals(object? obj) => obj is DngRect r && Equals(r);
    public override readonly int GetHashCode() => HashCode.Combine(T, L, B, R);
    public static bool operator ==(DngRect a, DngRect b) => a.Equals(b);
    public static bool operator !=(DngRect a, DngRect b) => !a.Equals(b);

    public static DngRect operator +(DngRect a, DngPoint b) => new(
        SafeArith.Add(a.T, b.V), SafeArith.Add(a.L, b.H),
        SafeArith.Add(a.B, b.V), SafeArith.Add(a.R, b.H));

    public static DngRect operator -(DngRect a, DngPoint b) => new(
        SafeArith.Sub(a.T, b.V), SafeArith.Sub(a.L, b.H),
        SafeArith.Sub(a.B, b.V), SafeArith.Sub(a.R, b.H));

    /// <summary>Set intersection. Mirrors C++ <c>operator&amp;</c>.</summary>
    public static DngRect Intersect(DngRect a, DngRect b)
    {
        var r = new DngRect
        {
            T = System.Math.Max(a.T, b.T),
            L = System.Math.Max(a.L, b.L),
            B = System.Math.Min(a.B, b.B),
            R = System.Math.Min(a.R, b.R),
        };
        if (r.IsEmpty) r = default;
        return r;
    }

    /// <summary>Bounding union. Mirrors C++ <c>operator|</c>.</summary>
    public static DngRect Union(DngRect a, DngRect b)
    {
        if (a.IsEmpty) return b;
        if (b.IsEmpty) return a;
        return new DngRect
        {
            T = System.Math.Min(a.T, b.T),
            L = System.Math.Min(a.L, b.L),
            B = System.Math.Max(a.B, b.B),
            R = System.Math.Max(a.R, b.R),
        };
    }

    public static DngRect Transpose(DngRect a) => new(a.L, a.T, a.R, a.B);

    public override readonly string ToString() => $"[t={T}, l={L}, b={B}, r={R}]";
}

/// <summary>Double-precision rectangle. Mirrors <c>dng_rect_real64</c>.</summary>
public readonly record struct DngRectF(double T, double L, double B, double R)
{
    public DngRectF(DngRect r) : this(r.T, r.L, r.B, r.R) { }

    public DngRectF(DngPointF p1, DngPointF p2)
        : this(System.Math.Min(p1.V, p2.V), System.Math.Min(p1.H, p2.H),
               System.Math.Max(p1.V, p2.V), System.Math.Max(p1.H, p2.H)) { }

    public bool IsEmpty => T >= B || L >= R;

    public double W => System.Math.Max(R - L, 0.0);
    public double H => System.Math.Max(B - T, 0.0);

    public DngPointF TL => new(T, L);
    public DngPointF TR => new(T, R);
    public DngPointF BL => new(B, L);
    public DngPointF BR => new(B, R);
    public DngPointF Center => new((T + B) * 0.5, (L + R) * 0.5);

    public DngRect Round() => new(
        DngMath.RoundToInt32(T), DngMath.RoundToInt32(L),
        DngMath.RoundToInt32(B), DngMath.RoundToInt32(R));
}
