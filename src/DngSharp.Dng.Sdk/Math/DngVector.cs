using System.Runtime.CompilerServices;
using DngSharp.Dng.Sdk.Errors;

namespace DngSharp.Dng.Sdk.Math;

/// <summary>
/// Up-to-4-element <see cref="double"/> vector. Mirrors <c>dng_vector</c>.
/// </summary>
public sealed class DngVector : IEquatable<DngVector>
{
    private readonly int _count;
    private readonly double[] _data;

    public DngVector()
    {
        _count = 0;
        _data = [];
    }

    public DngVector(int count)
    {
        if (count < 0 || count > DngLimits.MaxColorPlanes)
            DngThrow.MatrixMath($"vector size {count}");
        _count = count;
        _data = new double[count];
    }

    public DngVector(DngVector v) : this(v._count)
    {
        System.Array.Copy(v._data, _data, _count);
    }

    public static DngVector Of(params double[] values)
    {
        var v = new DngVector(values.Length);
        System.Array.Copy(values, v._data, values.Length);
        return v;
    }

    public int Count => _count;
    public bool IsEmpty => _count == 0;

    public double this[int i]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _data[i];
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => _data[i] = value;
    }

    public Span<double> AsSpan() => _data;

    public double MaxEntry()
    {
        if (IsEmpty) DngThrow.MatrixMath("MaxEntry on empty");
        double m = _data[0];
        for (int i = 1; i < _count; i++) if (_data[i] > m) m = _data[i];
        return m;
    }

    public double MinEntry()
    {
        if (IsEmpty) DngThrow.MatrixMath("MinEntry on empty");
        double m = _data[0];
        for (int i = 1; i < _count; i++) if (_data[i] < m) m = _data[i];
        return m;
    }

    public void Scale(double factor)
    {
        for (int i = 0; i < _count; i++) _data[i] *= factor;
    }

    /// <summary>Returns this vector as the diagonal of a square matrix.</summary>
    public DngMatrix AsDiagonal()
    {
        var m = new DngMatrix(_count, _count);
        for (int i = 0; i < _count; i++) m[i, i] = _data[i];
        return m;
    }

    /// <summary>Returns this vector as a column matrix (count×1).</summary>
    public DngMatrix AsColumn()
    {
        var m = new DngMatrix(_count, 1);
        for (int i = 0; i < _count; i++) m[i, 0] = _data[i];
        return m;
    }

    public static DngVector operator -(DngVector a, DngVector b)
    {
        if (a._count != b._count) DngThrow.MatrixMath("vector size mismatch for -");
        var r = new DngVector(a._count);
        for (int i = 0; i < a._count; i++) r._data[i] = a._data[i] - b._data[i];
        return r;
    }

    public static DngVector operator *(double k, DngVector a)
    {
        var r = new DngVector(a);
        r.Scale(k);
        return r;
    }

    public static double Dot(DngVector a, DngVector b)
    {
        if (a._count != b._count) DngThrow.MatrixMath("vector size mismatch for Dot");
        double s = 0;
        for (int i = 0; i < a._count; i++) s += a._data[i] * b._data[i];
        return s;
    }

    public static double Distance(DngVector a, DngVector b)
    {
        var d = a - b;
        return System.Math.Sqrt(Dot(d, d));
    }

    public bool Equals(DngVector? other) =>
        other is not null && _count == other._count && _data.AsSpan().SequenceEqual(other._data);

    public override bool Equals(object? obj) => Equals(obj as DngVector);
    public override int GetHashCode()
    {
        // Hash element-by-element so the hash is consistent with double.Equals
        // for NaN values; see DngMatrix.GetHashCode for rationale.
        var h = new HashCode();
        h.Add(_count);
        foreach (var x in _data) h.Add(x);
        return h.ToHashCode();
    }
}
