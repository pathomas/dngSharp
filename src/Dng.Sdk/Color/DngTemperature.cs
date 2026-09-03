using Dng.Sdk.Color.Cct;

namespace Dng.Sdk.Color;

/// <summary>
/// Color temperature (kelvin) + tint, with conversions to/from xy chromaticity
/// using the Robertson lookup table. Mirrors <c>dng_temperature</c>.
/// </summary>
public struct DngTemperature : IEquatable<DngTemperature>
{
    public double Kelvin;
    public double Tint;

    public DngTemperature(double kelvin, double tint)
    {
        Kelvin = kelvin;
        Tint = tint;
    }

    public readonly bool Equals(DngTemperature other) => Kelvin == other.Kelvin && Tint == other.Tint;
    public override readonly bool Equals(object? obj) => obj is DngTemperature t && Equals(t);
    public override readonly int GetHashCode() => HashCode.Combine(Kelvin, Tint);
    public static bool operator ==(DngTemperature a, DngTemperature b) => a.Equals(b);
    public static bool operator !=(DngTemperature a, DngTemperature b) => !a.Equals(b);

    /// <summary>Set <see cref="Kelvin"/>/<see cref="Tint"/> from a CIE xy point.</summary>
    public void SetXy(XyCoord xy) => (Kelvin, Tint) = CctRobertson.XyToTemperatureTint(xy);

    /// <summary>Convert the current (<see cref="Kelvin"/>, <see cref="Tint"/>) to CIE xy.</summary>
    public readonly XyCoord GetXy() => CctRobertson.TemperatureTintToXy(Kelvin, Tint);
}
