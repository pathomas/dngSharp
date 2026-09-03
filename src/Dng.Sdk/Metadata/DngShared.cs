using Dng.Sdk.Color;
using Dng.Sdk.Errors;
using Dng.Sdk.Hashing;
using Dng.Sdk.Math;
using Dng.Sdk.Primitives;

namespace Dng.Sdk.Metadata;

/// <summary>
/// DNG version quad. Each field is a separate byte stored in tag
/// <c>DNGVersion</c> (50706) or <c>DNGBackwardVersion</c> (50707).
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores",
    Justification = "DNG version constants V1_3_0/V1_4_0/... use underscores to separate version components, matching spec conventions and the Adobe header.")]
public readonly record struct DngVersion(byte Major, byte Minor, byte Patch, byte Build)
    : IComparable<DngVersion>
{
    public static DngVersion V1_0_0 => new(1, 0, 0, 0);
    public static DngVersion V1_3_0 => new(1, 3, 0, 0);
    public static DngVersion V1_4_0 => new(1, 4, 0, 0);
    public static DngVersion V1_5_0 => new(1, 5, 0, 0);
    public static DngVersion V1_6_0 => new(1, 6, 0, 0);
    public static DngVersion V1_7_0 => new(1, 7, 0, 0);
    public static DngVersion V1_7_1 => new(1, 7, 1, 0);

    public int CompareTo(DngVersion other)
    {
        int c = Major.CompareTo(other.Major); if (c != 0) return c;
        c = Minor.CompareTo(other.Minor); if (c != 0) return c;
        c = Patch.CompareTo(other.Patch); if (c != 0) return c;
        return Build.CompareTo(other.Build);
    }

    public static bool operator <(DngVersion a, DngVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(DngVersion a, DngVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(DngVersion a, DngVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(DngVersion a, DngVersion b) => a.CompareTo(b) >= 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}.{Build}";

    public static DngVersion FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4) DngThrow.BadFormat("DNGVersion needs 4 bytes");
        return new DngVersion(bytes[0], bytes[1], bytes[2], bytes[3]);
    }
}

/// <summary>
/// Cross-IFD DNG state. Mirrors <c>dng_shared</c>. These tags live in IFD 0
/// and apply to the whole file (not per-IFD), so they're hoisted into a
/// single struct.
/// </summary>
public sealed class DngShared
{
    /// <summary>Spec version this file uses (tag 50706).</summary>
    public DngVersion Version { get; set; }

    /// <summary>Minimum spec version a reader must understand (tag 50707).</summary>
    public DngVersion BackwardVersion { get; set; }

    public string UniqueCameraModel { get; set; } = string.Empty;
    public string LocalizedCameraModel { get; set; } = string.Empty;

    /// <summary>
    /// White balance as a camera-space neutral. Mutually exclusive with
    /// <see cref="AsShotWhiteXy"/>. Set via <see cref="SetAsShotNeutral"/>
    /// to enforce the spec invariant; direct mutation bypasses the check.
    /// </summary>
    public DngVector? AsShotNeutral { get; private set; }

    /// <summary>
    /// White balance as an xy chromaticity. Mutually exclusive with
    /// <see cref="AsShotNeutral"/>.
    /// </summary>
    public XyCoord? AsShotWhiteXy { get; private set; }

    /// <summary>
    /// Per-channel AnalogBalance from IFD 0. Mirrors the DNG tag of the same
    /// name and applies across the whole file.
    /// </summary>
    public DngVector? AnalogBalance { get; set; }

    public double BaselineExposure { get; set; }
    public double BaselineNoise { get; set; } = 1.0;
    public double BaselineSharpness { get; set; } = 1.0;
    public double LinearResponseLimit { get; set; } = 1.0;

    public DngFingerprint CameraCalibrationSignature { get; set; }
    public DngFingerprint ProfileCalibrationSignature { get; set; }
    public DngFingerprint RawDataUniqueId { get; set; }
    public DngFingerprint OriginalRawFileDigest { get; set; }

    /// <summary>
    /// Set <see cref="AsShotNeutral"/>, clearing any previously set
    /// <see cref="AsShotWhiteXy"/>. Per spec section 6.4: the two tags are
    /// mutually exclusive in the same IFD.
    /// </summary>
    public void SetAsShotNeutral(DngVector neutral)
    {
        ArgumentNullException.ThrowIfNull(neutral);
        AsShotNeutral = neutral;
        AsShotWhiteXy = null;
    }

    /// <summary>
    /// Set <see cref="AsShotWhiteXy"/>, clearing any previously set
    /// <see cref="AsShotNeutral"/>. Per spec section 6.4: mutually exclusive.
    /// </summary>
    public void SetAsShotWhiteXy(XyCoord xy)
    {
        AsShotWhiteXy = xy;
        AsShotNeutral = null;
    }

    /// <summary>
    /// Validate that a reader supporting <paramref name="readerVersion"/>
    /// can read this file. Throws <see cref="DngError.UnsupportedDng"/> when
    /// <see cref="BackwardVersion"/> exceeds the reader's capability.
    /// </summary>
    public void ValidateReadable(DngVersion readerVersion)
    {
        if (BackwardVersion > readerVersion)
            throw new DngException(DngError.UnsupportedDng,
                $"File requires reader >= {BackwardVersion}; have {readerVersion}");
    }
}
