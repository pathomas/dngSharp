using DngSharp.Dng.Sdk.Color;
using DngSharp.Dng.Sdk.Errors;
using DngSharp.Dng.Sdk.Imaging.Profile;
using DngSharp.Dng.Sdk.Math;
using DngSharp.Dng.Sdk.Render;

namespace DngSharp.Dng.Sdk.Tests.Render;

public class CameraColorMatrixTests
{
    /// <summary>
    /// Single-illuminant profile with a unit ForwardMatrix — round-trip
    /// should yield identity.
    /// </summary>
    [Fact]
    public void Single_illuminant_forward_matrix_passes_through()
    {
        var profile = new DngCameraProfile();
        profile.Illuminants.Add(new CalibrationIlluminant
        {
            Kelvin = 5500,
            WhitePoint = XyCoord.D65,
            ForwardMatrix = DngMatrix.Identity3x3(),
        });
        var m = CameraColorMatrix.BuildCameraToXyzD50(profile, 5500);
        Assert.True(m.IsIdentity());
    }

    /// <summary>
    /// Dual-illuminant profile: at exactly the lower CCT the result must
    /// equal the lower-CCT ForwardMatrix; at exactly the higher CCT it must
    /// equal the higher-CCT ForwardMatrix. This validates the inverse-CCT
    /// weight is plumbed correctly through the assembler.
    /// </summary>
    [Fact]
    public void Dual_illuminant_endpoints_return_unblended_matrices()
    {
        var fmLo = DngMatrix.Diagonal3x3(0.8, 0.9, 1.1);
        var fmHi = DngMatrix.Diagonal3x3(1.2, 1.0, 0.7);
        var profile = new DngCameraProfile();
        profile.Illuminants.Add(new CalibrationIlluminant
        {
            Kelvin = 2856,
            WhitePoint = XyCoord.StdA,
            ForwardMatrix = fmLo,
        });
        profile.Illuminants.Add(new CalibrationIlluminant
        {
            Kelvin = 6504,
            WhitePoint = XyCoord.D65,
            ForwardMatrix = fmHi,
        });

        var atLo = CameraColorMatrix.BuildCameraToXyzD50(profile, 2856);
        Assert.True(atLo.AlmostEqual(fmLo, 1e-9));

        var atHi = CameraColorMatrix.BuildCameraToXyzD50(profile, 6504);
        Assert.True(atHi.AlmostEqual(fmHi, 1e-9));
    }

    /// <summary>
    /// Midpoint in inverse-CCT space: weight should be 0.5 → matrix is the
    /// average of the two ForwardMatrices.
    /// </summary>
    [Fact]
    public void Dual_illuminant_mired_midpoint_yields_lerp_midpoint()
    {
        var fmLo = DngMatrix.Diagonal3x3(0.8, 0.9, 1.1);
        var fmHi = DngMatrix.Diagonal3x3(1.2, 1.0, 0.7);
        var profile = new DngCameraProfile();
        profile.Illuminants.Add(new CalibrationIlluminant
        {
            Kelvin = 2856,
            WhitePoint = XyCoord.StdA,
            ForwardMatrix = fmLo,
        });
        profile.Illuminants.Add(new CalibrationIlluminant
        {
            Kelvin = 6504,
            WhitePoint = XyCoord.D65,
            ForwardMatrix = fmHi,
        });

        double miredMid = 2.0 / (1.0 / 2856.0 + 1.0 / 6504.0);
        var atMid = CameraColorMatrix.BuildCameraToXyzD50(profile, miredMid);

        // Expect element-wise mean.
        Assert.Equal(1.0, atMid[0, 0], 9);   // (0.8 + 1.2) / 2
        Assert.Equal(0.95, atMid[1, 1], 9);  // (0.9 + 1.0) / 2
        Assert.Equal(0.9, atMid[2, 2], 9);   // (1.1 + 0.7) / 2
    }

    /// <summary>
    /// Fall back from ForwardMatrix to ColorMatrix when FM is absent —
    /// invert + Bradford adapt should still yield a sensible mapping.
    /// </summary>
    [Fact]
    public void Color_matrix_path_runs_when_forward_matrix_absent()
    {
        // XYZ_D65 → "camera RGB" matrix (deliberately easy — diagonal).
        var profile = new DngCameraProfile();
        profile.Illuminants.Add(new CalibrationIlluminant
        {
            Kelvin = 6504,
            WhitePoint = XyCoord.D65,
            ColorMatrix = DngMatrix.Diagonal3x3(2.0, 2.0, 2.0),
        });

        var m = CameraColorMatrix.BuildCameraToXyzD50(profile, 6504);

        // The result should adapt D65 → D50 then scale by 0.5 (inverse of diag(2)).
        // Bradford D65→D50 amplifies the red channel and reduces blue, so the
        // diagonals end up off-balance (R ≈ 0.376, G ≈ 0.5, B ≈ 0.54). Just
        // verify the matrix is in a sensible range and the assembler didn't crash.
        Assert.InRange(m[0, 0], 0.3, 0.7);
        Assert.InRange(m[1, 1], 0.3, 0.7);
        Assert.InRange(m[2, 2], 0.3, 0.7);
    }

    [Fact]
    public void Empty_profile_throws()
    {
        var profile = new DngCameraProfile();
        Assert.Throws<DngException>(() => CameraColorMatrix.BuildCameraToXyzD50(profile, 5500));
    }
}
