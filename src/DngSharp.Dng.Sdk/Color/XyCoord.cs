using DngSharp.Dng.Sdk.Math;

namespace DngSharp.Dng.Sdk.Color;

/// <summary>
/// CIE xy chromaticity coordinate. Mirrors <c>dng_xy_coord</c>.
/// </summary>
public readonly record struct XyCoord(double X, double Y)
{
    public bool IsValid => X > 0.0 && Y > 0.0;

    public static XyCoord operator +(XyCoord a, XyCoord b) => new(a.X + b.X, a.Y + b.Y);
    public static XyCoord operator -(XyCoord a, XyCoord b) => new(a.X - b.X, a.Y - b.Y);
    public static XyCoord operator *(double k, XyCoord a) => new(a.X * k, a.Y * k);
    public static double operator *(XyCoord a, XyCoord b) => a.X * b.X + a.Y * b.Y;

    // Standard illuminants (spec section 5.1).
    public static XyCoord StdA  => new(0.4476, 0.4074);
    public static XyCoord D50   => new(0.3457, 0.3585);
    public static XyCoord D55   => new(0.3324, 0.3474);
    public static XyCoord D65   => new(0.3127, 0.3290);
    public static XyCoord D75   => new(0.2990, 0.3149);

    /// <summary>
    /// Convert XYZ tristimulus to xy chromaticity. Mirrors <c>XYZtoXY</c>.
    /// </summary>
    public static XyCoord FromXyz(DngVector xyz)
    {
        if (xyz.Count != 3)
            Errors.DngThrow.MatrixMath("FromXyz: expected 3-vector");

        double total = xyz[0] + xyz[1] + xyz[2];
        if (total <= 0.0) return D50;
        return new XyCoord(xyz[0] / total, xyz[1] / total);
    }

    /// <summary>
    /// Convert xy chromaticity to unit-luminance XYZ. Mirrors <c>XYtoXYZ</c>.
    /// </summary>
    public DngVector ToXyz()
    {
        var c = this;
        if (!c.IsValid) c = D50;
        // Clamp away from degenerate boundary (matches Adobe's pin).
        double cx = System.Math.Max(c.X, 0.000001);
        double cy = System.Math.Max(c.Y, 0.000001);
        if (cx + cy > 0.999999)
        {
            double scale = 0.999999 / (cx + cy);
            cx *= scale;
            cy *= scale;
        }
        return DngVector.Of(cx / cy, 1.0, (1.0 - cx - cy) / cy);
    }
}

/// <summary>
/// XYZ profile-connection-space white point. Spec uses D50.
/// </summary>
public static class Pcs
{
    public static XyCoord WhiteXy => XyCoord.D50;
    public static DngVector WhiteXyz => XyCoord.D50.ToXyz();
}
