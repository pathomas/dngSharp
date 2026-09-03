namespace Dng.Sdk.Imaging.Opcodes;

/// <summary>
/// Opcode identifiers used in DNG opcode lists. Mirrors
/// <c>dng_opcode_id</c>. Each value is the on-disk 32-bit ID written into
/// the opcode-list stream.
/// </summary>
public enum OpcodeId : uint
{
    // Internal use only opcode. Never written to DNGs.
    Private = 0,

    // Warp image to correct distortion and lateral chromatic aberration for
    // rectilinear lenses.
    WarpRectilinear = 1,

    // Warp image to correct distortion for fisheye lenses.
    WarpFisheye = 2,

    // Radial vignette correction.
    FixVignetteRadial = 3,

    // Patch bad Bayer pixels marked with a special value in the image.
    FixBadPixelsConstant = 4,

    // Patch bad Bayer pixels/rectangles at a list of specified coordinates.
    FixBadPixelsList = 5,

    // Trim image to specified bounds.
    TrimBounds = 6,

    // Map an area through a 16-bit LUT.
    MapTable = 7,

    // Map an area using a polynomial function.
    MapPolynomial = 8,

    // Apply a gain map to an area.
    GainMap = 9,

    // Apply a per-row delta to an area.
    DeltaPerRow = 10,

    // Apply a per-column delta to an area.
    DeltaPerColumn = 11,

    // Apply a per-row scale to an area.
    ScalePerRow = 12,

    // Apply a per-column scale to an area.
    ScalePerColumn = 13,

    // DNG 1.6 — extension of WarpRectilinear.
    WarpRectilinear2 = 14,
}

/// <summary>
/// Per-opcode flags. Mirrors <c>kFlag_</c> bits in <c>dng_opcodes.h</c>.
/// </summary>
[Flags]
public enum OpcodeFlags : uint
{
    None = 0,
    /// <summary>Optional — readers may skip if unsupported.</summary>
    Optional = 1,
    /// <summary>Optional for preview workflows but required for full render.</summary>
    OptionalForPreview = 2,
}
