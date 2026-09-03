namespace Dng.Sdk.Imaging.Raw;

/// <summary>
/// Linearization and per-pixel offset state. Mirrors a subset of
/// <c>dng_linearization_info</c>. Used by stage-1 → stage-2 to:
/// <list type="number">
///   <item>Apply <see cref="LinearizationTable"/> (optional)</item>
///   <item>Subtract per-pixel black level (<see cref="BlackLevel"/> +
///         <see cref="BlackLevelDeltaH"/>/<see cref="BlackLevelDeltaV"/>)</item>
///   <item>Rescale to [0, 1] via <see cref="WhiteLevel"/></item>
///   <item>Clip (sub-zero values <i>not</i> clipped at this stage — that's a
///         later stage's job)</item>
/// </list>
/// </summary>
public sealed class LinearizationInfo
{
    /// <summary>
    /// Per-sample LUT for stage-1 linearization. Length matches the bit
    /// depth (e.g. 1024 entries for 10-bit, 65536 for 16-bit). Null = no LUT.
    /// </summary>
    public ushort[]? LinearizationTable { get; set; }

    /// <summary>
    /// Per-plane black-level offset, in the original (unlinearized) value
    /// space. Length = number of color planes (1, 3, or 4).
    /// </summary>
    public double[] BlackLevel { get; set; } = [];

    /// <summary>
    /// Optional per-row black-level adjustment (BlackLevelDeltaV — tag
    /// 50714). Length = height in pixels.
    /// </summary>
    public double[]? BlackLevelDeltaV { get; set; }

    /// <summary>
    /// Optional per-column black-level adjustment (BlackLevelDeltaH — tag
    /// 50715). Length = width in pixels.
    /// </summary>
    public double[]? BlackLevelDeltaH { get; set; }

    /// <summary>
    /// Per-plane white level. The post-linearization, post-black-subtract
    /// value that maps to 1.0 in stage 2.
    /// </summary>
    public double[] WhiteLevel { get; set; } = [];

    /// <summary>
    /// Black-level repeat pattern (BlackLevelRepeatDim — tag 50713). When
    /// (rows, cols) != (1, 1), <see cref="BlackLevel"/> tiles across the
    /// sensor (typical for CFA Bayer where R/G1/G2/B have different bias).
    /// </summary>
    public (uint Rows, uint Cols) BlackLevelRepeatDim { get; set; } = (1, 1);
}
