using DngSharp.Dng.Sdk.Math;

namespace DngSharp.Dng.Sdk.Color;

/// <summary>
/// Bradford chromatic adaptation. Mirrors the matrix and inverse used by
/// <c>dng_color_spec.cpp</c> for CAT (chromatic-adaptation transform)
/// between two white points.
///
/// <para>The DNG spec (section 6.2.5) calls for chromatic adaptation when a
/// camera profile's calibration illuminant differs from the PCS white (D50).
/// The Bradford matrix is the spectrally sharpened cone-response basis used
/// by most modern CMS, including ICCv4 and DNG.</para>
/// </summary>
public static class Bradford
{
    /// <summary>The Bradford response matrix B (3×3).</summary>
    public static DngMatrix Matrix { get; } = DngMatrix.Matrix3x3(
         0.8951,  0.2664, -0.1614,
        -0.7502,  1.7135,  0.0367,
         0.0389, -0.0685,  1.0296);

    /// <summary>The inverse of <see cref="Matrix"/>.</summary>
    public static DngMatrix InverseMatrix { get; } = DngMatrix.Invert(Matrix);

    /// <summary>
    /// Build a 3×3 matrix that adapts XYZ values measured under <paramref name="srcWhite"/>
    /// (as xy) to equivalents under <paramref name="dstWhite"/>.
    /// </summary>
    public static DngMatrix MakeAdaptationMatrix(XyCoord srcWhite, XyCoord dstWhite)
    {
        var srcXyz = srcWhite.ToXyz();
        var dstXyz = dstWhite.ToXyz();

        // Cone responses for src/dst whites.
        var srcCone = Matrix * srcXyz;
        var dstCone = Matrix * dstXyz;

        // Per-channel cone scale.
        var ratio = DngMatrix.Diagonal3x3(
            dstCone[0] / srcCone[0],
            dstCone[1] / srcCone[1],
            dstCone[2] / srcCone[2]);

        // CAT = B^-1 · diag(ratio) · B
        return InverseMatrix * ratio * Matrix;
    }
}
