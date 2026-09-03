namespace Dng.Sdk.Imaging.Opcodes;

/// <summary>
/// Common surface shared by <see cref="WarpRectilinearParams"/> (DNG 1.3
/// <c>WarpRectilinear</c>) and <see cref="WarpRectilinear2Params"/> (DNG 1.6
/// <c>WarpRectilinear2</c>) so <see cref="LensWarpFilter"/> can apply either
/// model without duplicating the resample loop. Mirrors the subset of
/// <c>dng_warp_params_rectilinear</c>'s virtual interface both concrete
/// models implement.
/// </summary>
public interface IWarpRectilinearParams
{
    /// <summary>Optical center in normalized [0,1] image-relative coordinates.</summary>
    (double H, double V) Center { get; }

    /// <summary>Copy plane-0 parameters to any additional planes required by the image.</summary>
    void PropagateToAllPlanes(int totalPlanes);

    /// <summary>Radial correction is a no-op for this plane.</summary>
    bool IsRadNop(int plane);

    /// <summary>Tangential correction is a no-op for this plane.</summary>
    bool IsTanNop(int plane);

    /// <summary>Both radial and tangential corrections are a no-op for every plane.</summary>
    bool IsNopAll();

    /// <summary>Evaluate the radial correction ratio f(r) for squared normalized radius <paramref name="r2"/>.</summary>
    double RadialRatio(int plane, double r2);

    /// <summary>Evaluate the 2D tangential warp offset. Returns (tanH, tanV).</summary>
    (double TanH, double TanV) EvaluateTangential(
        int plane, double r2, double diffH, double diffV, double diffH2, double diffV2);
}
