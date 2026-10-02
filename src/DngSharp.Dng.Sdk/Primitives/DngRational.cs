using DngSharp.Dng.Sdk.Math;

namespace DngSharp.Dng.Sdk.Primitives;

/// <summary>
/// Signed rational, as stored in TIFF SRATIONAL fields. Mirrors
/// <c>dng_srational</c>.
/// </summary>
public struct DngSRational : IEquatable<DngSRational>
{
    public int N;
    public int D;

    public DngSRational(int n, int d) { N = n; D = d; }

    public readonly bool IsValid => D != 0;
    public readonly double AsDouble => D != 0 ? (double)N / D : 0.0;

    /// <summary>
    /// Convert a real value to a rational with a sensible denominator. Matches
    /// the C++ <c>Set_real64</c>: 0 → (0,1); 1≤|x|&lt;32768 → /32768;
    /// |x|≥32768 → /1; otherwise → /(32768*32768).
    /// </summary>
    public void SetDouble(double x, int dd = 0)
    {
        if (x == 0.0) { N = 0; D = 1; return; }
        if (dd == 0)
        {
            double y = System.Math.Abs(x);
            dd = y >= 32768.0 ? 1 : (y >= 1.0 ? 32768 : 32768 * 32768);
        }
        N = DngMath.RoundToInt32(x * dd);
        D = dd;
    }

    public void ReduceByFactor(int factor)
    {
        while (N % factor == 0 && D % factor == 0 && D >= factor)
        {
            N /= factor;
            D /= factor;
        }
    }

    public readonly bool Equals(DngSRational other) => N == other.N && D == other.D;
    public override readonly bool Equals(object? obj) => obj is DngSRational r && Equals(r);
    public override readonly int GetHashCode() => HashCode.Combine(N, D);
    public static bool operator ==(DngSRational a, DngSRational b) => a.Equals(b);
    public static bool operator !=(DngSRational a, DngSRational b) => !a.Equals(b);

    public override readonly string ToString() => $"{N}/{D}";
}

/// <summary>
/// Unsigned rational, as stored in TIFF RATIONAL fields. Mirrors
/// <c>dng_urational</c>.
/// </summary>
public struct DngURational : IEquatable<DngURational>
{
    public uint N;
    public uint D;

    public DngURational(uint n, uint d) { N = n; D = d; }

    public readonly bool IsValid => D != 0;
    public readonly double AsDouble => D != 0 ? (double)N / D : 0.0;

    public void SetDouble(double x, uint dd = 0)
    {
        if (x <= 0.0) { N = 0; D = 1; return; }
        if (dd == 0)
        {
            dd = x >= 32768.0 ? 1u : (x >= 1.0 ? 32768u : 32768u * 32768u);
        }
        N = DngMath.RoundToUInt32(x * dd);
        D = dd;
    }

    public void ReduceByFactor(uint factor)
    {
        while (N % factor == 0 && D % factor == 0 && D >= factor)
        {
            N /= factor;
            D /= factor;
        }
    }

    public readonly bool Equals(DngURational other) => N == other.N && D == other.D;
    public override readonly bool Equals(object? obj) => obj is DngURational r && Equals(r);
    public override readonly int GetHashCode() => HashCode.Combine(N, D);
    public static bool operator ==(DngURational a, DngURational b) => a.Equals(b);
    public static bool operator !=(DngURational a, DngURational b) => !a.Equals(b);

    public override readonly string ToString() => $"{N}/{D}";
}
