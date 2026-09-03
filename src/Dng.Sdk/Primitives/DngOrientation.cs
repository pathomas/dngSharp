namespace Dng.Sdk.Primitives;

/// <summary>
/// Image orientation. Mirrors <c>dng_orientation</c>.
///
/// <para>Adobe and TIFF use different encodings; we store the Adobe encoding
/// internally because the bit layout (mirror=bit2, transpose=bit0, rotation in
/// bits 0-1) makes composition and inversion easy.</para>
///
/// <para><b>Composition is not commutative.</b> <c>a + b</c> means "apply
/// <c>a</c> then <c>b</c>". To invert from <c>c = a + b</c>: <c>b = -a + c</c>,
/// not <c>c - a</c>.</para>
/// </summary>
public readonly struct DngOrientation : IEquatable<DngOrientation>
{
    public const uint Normal = 0;
    public const uint Rotate90CW = 1;
    public const uint Rotate180 = 2;
    public const uint Rotate90CCW = 3;
    public const uint Mirror = 4;
    public const uint Mirror90CW = 5;
    public const uint Mirror180 = 6;
    public const uint Mirror90CCW = 7;
    public const uint Unknown = 8;

    private readonly uint _adobe;

    private DngOrientation(uint adobe) { _adobe = adobe; }

    public uint Adobe => _adobe;
    public bool IsValid => _adobe < Unknown;

    public static DngOrientation FromAdobe(uint adobe) => new(adobe);

    public static DngOrientation FromTiff(uint tiff) => new(tiff switch
    {
        1 => Normal,
        2 => Mirror,
        3 => Rotate180,
        4 => Mirror180,
        5 => Mirror90CCW,
        6 => Rotate90CW,
        7 => Mirror90CW,
        8 => Rotate90CCW,
        9 => Unknown,
        _ => Normal,
    });

    public uint ToTiff() => _adobe switch
    {
        Normal => 1,
        Mirror => 2,
        Rotate180 => 3,
        Mirror180 => 4,
        Mirror90CCW => 5,
        Rotate90CW => 6,
        Mirror90CW => 7,
        Rotate90CCW => 8,
        Unknown => 9,
        _ => 1,
    };

    public bool FlipD => (_adobe & 1) != 0;

    public bool FlipH => (_adobe & 4) != 0
        ? (_adobe & 2) == 0
        : (_adobe & 2) != 0;

    public bool FlipV => (_adobe & 4) != 0
        ? FlipD == FlipH
        : FlipD != FlipH;

    public bool IsMirrored => (_adobe & 4) != 0;

    /// <summary>Inverse. Mirrors C++ <c>operator-</c>.</summary>
    public static DngOrientation operator -(DngOrientation a)
    {
        uint x = a._adobe;
        if ((x & 5) == 5) x ^= 2;
        return new DngOrientation(((4 - x) & 3) | (x & 4));
    }

    /// <summary>Composition. Mirrors C++ <c>operator+</c>. Not commutative.</summary>
    public static DngOrientation operator +(DngOrientation a, DngOrientation b)
    {
        uint x = a._adobe;
        uint y = b._adobe;
        if ((y & 4) != 0)
            x ^= (x & 1) != 0 ? 6u : 4u;
        return new DngOrientation(((x + y) & 3) | (x & 4));
    }

    public static DngOrientation operator -(DngOrientation a, DngOrientation b) => a + (-b);

    public bool Equals(DngOrientation other) => _adobe == other._adobe;
    public override bool Equals(object? obj) => obj is DngOrientation o && Equals(o);
    public override int GetHashCode() => _adobe.GetHashCode();
    public static bool operator ==(DngOrientation a, DngOrientation b) => a.Equals(b);
    public static bool operator !=(DngOrientation a, DngOrientation b) => !a.Equals(b);
}
