using DngSharp.Dng.Sdk.Color.Cct;
using DngSharp.Dng.Sdk.Imaging.Profile;
using DngSharp.Dng.Sdk.Math;
using DngSharp.Dng.Sdk.Metadata;
using DngSharp.Dng.Sdk.Pipeline;

namespace DngSharp.Dng.Sdk.Tests.Pipeline;

public class AsShotNeutralTests
{
    [Fact]
    public void Unity_neutral_converges_near_single_illuminant_cct()
    {
        var negative = CreateNegativeWithIdentityProfile();
        negative.Shared.SetAsShotNeutral(DngVector.Of(1.0, 1.0, 1.0));

        var kelvin = negative.EstimateAsShotKelvin();
        Assert.NotNull(kelvin);

        Assert.InRange(kelvin.Value, 5200.0, 5700.0);
    }

    [Fact]
    public void Warm_neutral_converges_to_low_cct()
    {
        var negative = CreateNegativeWithIdentityProfile();
        negative.Shared.SetAsShotNeutral(DngVector.Of(1.0, 0.8, 0.6));

        var kelvin = negative.EstimateAsShotKelvin();
        Assert.NotNull(kelvin);

        Assert.InRange(kelvin.Value, 2400.0, 3200.0);
    }

    [Fact]
    public void Cool_neutral_converges_to_high_cct()
    {
        var negative = CreateNegativeWithIdentityProfile();
        negative.Shared.SetAsShotNeutral(DngVector.Of(0.6, 0.8, 1.0));

        var kelvin = negative.EstimateAsShotKelvin();
        Assert.NotNull(kelvin);

        Assert.InRange(kelvin.Value, 8000.0, 13000.0);
    }

    [Fact]
    public void Estimate_as_shot_kelvin_returns_non_null_for_asshot_neutral()
    {
        var negative = CreateNegativeWithIdentityProfile();
        negative.Shared.SetAsShotNeutral(DngVector.Of(0.8, 1.0, 0.9));

        Assert.NotNull(negative.EstimateAsShotKelvin());
    }

    [Fact]
    public void Estimate_as_shot_kelvin_falls_back_to_daylight_without_profile()
    {
        var negative = new DngNegative(new DngHost())
        {
            Shared = new DngShared(),
        };
        negative.Shared.SetAsShotNeutral(DngVector.Of(1.0, 1.0, 1.0));

        var kelvin = negative.EstimateAsShotKelvin();
        Assert.NotNull(kelvin);
        Assert.Equal(6500.0, kelvin.Value);
    }

    private static DngNegative CreateNegativeWithIdentityProfile()
    {
        var negative = new DngNegative(new DngHost())
        {
            Shared = new DngShared(),
        };
        negative.Profiles.Add(CreateIdentityProfile());
        return negative;
    }

    private static DngCameraProfile CreateIdentityProfile(double kelvin = 5500.0)
    {
        var profile = new DngCameraProfile();
        profile.Illuminants.Add(new CalibrationIlluminant
        {
            Kelvin = kelvin,
            WhitePoint = CctRobertson.TemperatureTintToXy(kelvin, 0.0),
            ColorMatrix = DngMatrix.Identity3x3(),
            ForwardMatrix = DngMatrix.Identity3x3(),
        });
        return profile;
    }
}
