using System.Runtime.CompilerServices;
using DngSharp.Dng.Sdk.Errors;

namespace DngSharp.Dng.Sdk.Math;

/// <summary>
/// Up-to-4×4 dense matrix of <see cref="double"/>. Mirrors <c>dng_matrix</c>.
/// Storage is a flat <c>double[Rows*Cols]</c> in row-major order. The C++
/// version uses a fixed <c>real64[kMaxColorPlanes][kMaxColorPlanes]</c>
/// buffer and a logical <c>(rows,cols)</c> shape; we use a tight array so
/// we can later swap in <see cref="System.Numerics.Tensors"/>-backed kernels
/// without rewriting call sites.
/// </summary>
public sealed class DngMatrix : IEquatable<DngMatrix>
{
    private readonly int _rows;
    private readonly int _cols;
    private readonly double[] _data;

    public DngMatrix()
    {
        _rows = 0;
        _cols = 0;
        _data = [];
    }

    public DngMatrix(int rows, int cols)
    {
        if (rows < 0 || cols < 0) DngThrow.MatrixMath("negative size");
        if (rows > DngLimits.MaxColorPlanes || cols > DngLimits.MaxColorPlanes)
            DngThrow.MatrixMath($"size {rows}×{cols} exceeds {DngLimits.MaxColorPlanes}");
        _rows = rows;
        _cols = cols;
        _data = new double[rows * cols];
    }

    public DngMatrix(DngMatrix m) : this(m._rows, m._cols)
    {
        System.Array.Copy(m._data, _data, _data.Length);
    }

    public int Rows => _rows;
    public int Cols => _cols;
    public bool IsEmpty => _rows == 0 || _cols == 0;
    public bool NotEmpty => !IsEmpty;

    public double this[int row, int col]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _data[row * _cols + col];
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => _data[row * _cols + col] = value;
    }

    /// <summary>Returns a writable view of one row. Mirrors C++ <c>operator[]</c>.</summary>
    public Span<double> Row(int row) => _data.AsSpan(row * _cols, _cols);

    public void SetIdentity(int count)
    {
        if (count <= 0 || count > DngLimits.MaxColorPlanes)
            DngThrow.MatrixMath($"identity size {count}");
        // Identity has the same row+col count.
        if (count != _rows || count != _cols)
            DngThrow.MatrixMath("identity called on non-square matrix");
        System.Array.Clear(_data);
        for (int i = 0; i < count; i++) this[i, i] = 1.0;
    }

    public bool IsDiagonal()
    {
        if (IsEmpty || _rows != _cols) return false;
        for (int r = 0; r < _rows; r++)
            for (int c = 0; c < _cols; c++)
                if (r != c && this[r, c] != 0.0) return false;
        return true;
    }

    public bool IsIdentity()
    {
        if (!IsDiagonal()) return false;
        for (int i = 0; i < _rows; i++)
            if (this[i, i] != 1.0) return false;
        return true;
    }

    public double MaxEntry()
    {
        if (IsEmpty) DngThrow.MatrixMath("MaxEntry on empty");
        double m = _data[0];
        for (int i = 1; i < _data.Length; i++) if (_data[i] > m) m = _data[i];
        return m;
    }

    public double MinEntry()
    {
        if (IsEmpty) DngThrow.MatrixMath("MinEntry on empty");
        double m = _data[0];
        for (int i = 1; i < _data.Length; i++) if (_data[i] < m) m = _data[i];
        return m;
    }

    public void Scale(double factor)
    {
        for (int i = 0; i < _data.Length; i++) _data[i] *= factor;
    }

    public void Round(double factor)
    {
        for (int i = 0; i < _data.Length; i++)
            _data[i] = System.Math.Round(_data[i] * factor) / factor;
    }

    public bool AlmostEqual(DngMatrix other, double slop = 1e-8)
    {
        if (_rows != other._rows || _cols != other._cols) return false;
        for (int i = 0; i < _data.Length; i++)
            if (System.Math.Abs(_data[i] - other._data[i]) > slop) return false;
        return true;
    }

    public bool AlmostIdentity(double slop = 1e-8)
    {
        if (_rows != _cols || IsEmpty) return false;
        for (int r = 0; r < _rows; r++)
            for (int c = 0; c < _cols; c++)
            {
                double want = r == c ? 1.0 : 0.0;
                if (System.Math.Abs(this[r, c] - want) > slop) return false;
            }
        return true;
    }

    public bool Equals(DngMatrix? other)
    {
        if (other is null) return false;
        if (_rows != other._rows || _cols != other._cols) return false;
        return _data.AsSpan().SequenceEqual(other._data);
    }

    public override bool Equals(object? obj) => Equals(obj as DngMatrix);
    public override int GetHashCode()
    {
        // Hash element-by-element via double.GetHashCode so the hash is
        // consistent with double.Equals (which treats NaN.Equals(NaN) as true).
        // Hashing raw bit patterns would break the equals/hash contract for
        // NaN values with different payloads.
        var h = new HashCode();
        h.Add(_rows);
        h.Add(_cols);
        foreach (var x in _data) h.Add(x);
        return h.ToHashCode();
    }

    public static bool operator ==(DngMatrix? a, DngMatrix? b) =>
        ReferenceEquals(a, b) || (a is not null && a.Equals(b));

    public static bool operator !=(DngMatrix? a, DngMatrix? b) => !(a == b);

    // ---- Arithmetic ---------------------------------------------------------

    public static DngMatrix operator *(DngMatrix a, DngMatrix b)
    {
        if (a._cols != b._rows)
            DngThrow.MatrixMath($"shape mismatch: ({a._rows}×{a._cols}) * ({b._rows}×{b._cols})");
        var r = new DngMatrix(a._rows, b._cols);
        for (int i = 0; i < a._rows; i++)
            for (int j = 0; j < b._cols; j++)
            {
                double s = 0;
                for (int k = 0; k < a._cols; k++) s += a[i, k] * b[k, j];
                r[i, j] = s;
            }
        return r;
    }

    public static DngVector operator *(DngMatrix a, DngVector b)
    {
        if (a._cols != b.Count)
            DngThrow.MatrixMath($"shape mismatch: ({a._rows}×{a._cols}) * vector({b.Count})");
        var r = new DngVector(a._rows);
        for (int i = 0; i < a._rows; i++)
        {
            double s = 0;
            for (int k = 0; k < a._cols; k++) s += a[i, k] * b[k];
            r[i] = s;
        }
        return r;
    }

    public static DngMatrix operator *(double k, DngMatrix a)
    {
        var r = new DngMatrix(a);
        r.Scale(k);
        return r;
    }

    public static DngMatrix operator +(DngMatrix a, DngMatrix b)
    {
        if (a._rows != b._rows || a._cols != b._cols)
            DngThrow.MatrixMath("shape mismatch for +");
        var r = new DngMatrix(a._rows, a._cols);
        for (int i = 0; i < a._data.Length; i++) r._data[i] = a._data[i] + b._data[i];
        return r;
    }

    public static DngMatrix Transpose(DngMatrix a)
    {
        var r = new DngMatrix(a._cols, a._rows);
        for (int i = 0; i < a._rows; i++)
            for (int j = 0; j < a._cols; j++)
                r[j, i] = a[i, j];
        return r;
    }

    /// <summary>
    /// Matrix inverse via Gauss-Jordan elimination with partial pivoting.
    /// Throws <see cref="DngException"/> with <see cref="DngError.MatrixMath"/>
    /// if the matrix is singular.
    /// </summary>
    public static DngMatrix Invert(DngMatrix a)
    {
        if (a._rows != a._cols) DngThrow.MatrixMath("Invert: not square");
        int n = a._rows;
        // Build augmented [a|I] into a 2n-wide buffer.
        Span<double> buf = stackalloc double[n * 2 * n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++) buf[i * 2 * n + j] = a[i, j];
            buf[i * 2 * n + n + i] = 1.0;
        }
        int w = 2 * n;
        for (int i = 0; i < n; i++)
        {
            // Partial pivot.
            int piv = i;
            double pivAbs = System.Math.Abs(buf[i * w + i]);
            for (int k = i + 1; k < n; k++)
            {
                double v = System.Math.Abs(buf[k * w + i]);
                if (v > pivAbs) { pivAbs = v; piv = k; }
            }
            if (pivAbs < 1e-20) DngThrow.MatrixMath("Invert: singular");
            if (piv != i)
                for (int j = 0; j < w; j++)
                    (buf[i * w + j], buf[piv * w + j]) = (buf[piv * w + j], buf[i * w + j]);

            double diag = buf[i * w + i];
            double inv = 1.0 / diag;
            for (int j = 0; j < w; j++) buf[i * w + j] *= inv;

            for (int k = 0; k < n; k++)
            {
                if (k == i) continue;
                double f = buf[k * w + i];
                if (f == 0) continue;
                for (int j = 0; j < w; j++) buf[k * w + j] -= f * buf[i * w + j];
            }
        }
        var r = new DngMatrix(n, n);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                r[i, j] = buf[i * w + n + j];
        return r;
    }

    // ---- Specialized factories ---------------------------------------------

    public static DngMatrix Matrix3x3(
        double a00, double a01, double a02,
        double a10, double a11, double a12,
        double a20, double a21, double a22)
    {
        var m = new DngMatrix(3, 3);
        m[0, 0] = a00; m[0, 1] = a01; m[0, 2] = a02;
        m[1, 0] = a10; m[1, 1] = a11; m[1, 2] = a12;
        m[2, 0] = a20; m[2, 1] = a21; m[2, 2] = a22;
        return m;
    }

    public static DngMatrix Diagonal3x3(double a, double b, double c) =>
        Matrix3x3(a, 0, 0, 0, b, 0, 0, 0, c);

    public static DngMatrix Identity3x3() => Diagonal3x3(1, 1, 1);

    public static DngMatrix Matrix4x3(
        double a00, double a01, double a02,
        double a10, double a11, double a12,
        double a20, double a21, double a22,
        double a30, double a31, double a32)
    {
        var m = new DngMatrix(4, 3);
        m[0, 0] = a00; m[0, 1] = a01; m[0, 2] = a02;
        m[1, 0] = a10; m[1, 1] = a11; m[1, 2] = a12;
        m[2, 0] = a20; m[2, 1] = a21; m[2, 2] = a22;
        m[3, 0] = a30; m[3, 1] = a31; m[3, 2] = a32;
        return m;
    }

    public static DngMatrix Matrix4x4(
        double a00, double a01, double a02, double a03,
        double a10, double a11, double a12, double a13,
        double a20, double a21, double a22, double a23,
        double a30, double a31, double a32, double a33)
    {
        var m = new DngMatrix(4, 4);
        m[0, 0] = a00; m[0, 1] = a01; m[0, 2] = a02; m[0, 3] = a03;
        m[1, 0] = a10; m[1, 1] = a11; m[1, 2] = a12; m[1, 3] = a13;
        m[2, 0] = a20; m[2, 1] = a21; m[2, 2] = a22; m[2, 3] = a23;
        m[3, 0] = a30; m[3, 1] = a31; m[3, 2] = a32; m[3, 3] = a33;
        return m;
    }
}
