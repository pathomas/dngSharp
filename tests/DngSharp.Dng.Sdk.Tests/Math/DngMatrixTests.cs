using DngSharp.Dng.Sdk.Errors;
using DngSharp.Dng.Sdk.Math;

namespace DngSharp.Dng.Sdk.Tests.Math;

public class DngMatrixTests
{
    [Fact]
    public void Identity_3x3_is_identity()
    {
        var m = DngMatrix.Identity3x3();
        Assert.True(m.IsIdentity());
        Assert.True(m.IsDiagonal());
    }

    [Fact]
    public void Matrix_vector_multiply()
    {
        var m = DngMatrix.Matrix3x3(
            1, 0, 0,
            0, 2, 0,
            0, 0, 3);
        var v = DngVector.Of(4, 5, 6);
        var r = m * v;
        Assert.Equal(4.0, r[0], 12);
        Assert.Equal(10.0, r[1], 12);
        Assert.Equal(18.0, r[2], 12);
    }

    [Fact]
    public void Matrix_multiply_then_invert_round_trips()
    {
        var a = DngMatrix.Matrix3x3(
            0.6097559,  0.2052641, 0.1492240,
            0.3111242,  0.6256656, 0.0632102,
            0.0194811,  0.0608902, 0.7448387);
        var inv = DngMatrix.Invert(a);
        var product = a * inv;
        Assert.True(product.AlmostIdentity(1e-9), $"Got:\n{Dump(product)}");
    }

    [Fact]
    public void Singular_matrix_throws()
    {
        var m = DngMatrix.Matrix3x3(
            1, 2, 3,
            2, 4, 6,
            3, 6, 9);
        Assert.Throws<DngException>(() => DngMatrix.Invert(m));
    }

    [Fact]
    public void Transpose_swaps_dims()
    {
        var m = DngMatrix.Matrix4x3(
            1, 2, 3,
            4, 5, 6,
            7, 8, 9,
            10, 11, 12);
        var t = DngMatrix.Transpose(m);
        Assert.Equal(3, t.Rows);
        Assert.Equal(4, t.Cols);
        Assert.Equal(10.0, t[0, 3], 12);
        Assert.Equal(12.0, t[2, 3], 12);
    }

    [Fact]
    public void Shape_mismatch_throws()
    {
        var a = new DngMatrix(2, 3);
        var b = new DngMatrix(4, 2);
        Assert.Throws<DngException>(() => _ = a * b);
    }

    [Fact]
    public void Matrix_oversized_rejected()
    {
        Assert.Throws<DngException>(() => new DngMatrix(5, 5));
    }

    private static string Dump(DngMatrix m)
    {
        var lines = new string[m.Rows];
        for (int r = 0; r < m.Rows; r++)
        {
            var cells = new string[m.Cols];
            for (int c = 0; c < m.Cols; c++) cells[c] = m[r, c].ToString("F6");
            lines[r] = string.Join(", ", cells);
        }
        return string.Join("\n", lines);
    }
}
