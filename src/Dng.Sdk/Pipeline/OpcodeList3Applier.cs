using Dng.Sdk.Imaging;
using Dng.Sdk.Imaging.Opcodes;

namespace Dng.Sdk.Pipeline;

/// <summary>
/// Applies the subset of OpcodeList3 opcodes this port supports to a
/// Stage-3 image. Mirrors <c>host.ApplyOpcodeList(fOpcodeList3, ...)</c>
/// in <c>dng_negative::BuildStage3Image</c>, which runs strictly after
/// demosaic (<c>DoBuildStage3</c>) — see native trace timers
/// ("Interpolate time" wraps demosaic + opcode-list-3 application; the
/// nested "BaseWarpRectilinear time" is the opcode itself).
///
/// <para>Today <see cref="OpcodeId.WarpRectilinear"/> (via
/// <see cref="LensWarpFilter"/>), <see cref="OpcodeId.WarpRectilinear2"/>
/// (via <see cref="WarpRectilinear2Params"/> + <see cref="LensWarpFilter"/>),
/// <see cref="OpcodeId.WarpFisheye"/> (via <see cref="WarpFisheyeParams"/> +
/// <see cref="LensWarpFilter"/>), <see cref="OpcodeId.FixVignetteRadial"/> (via
/// <see cref="FixVignetteRadialOpcode"/>), <see cref="OpcodeId.DeltaPerColumn"/> (via
/// <see cref="DeltaPerColumnOpcode"/>), <see cref="OpcodeId.DeltaPerRow"/>
/// (via <see cref="DeltaPerRowOpcode"/>), <see cref="OpcodeId.ScalePerRow"/>
/// (via <see cref="ScalePerRowOpcode"/>), <see cref="OpcodeId.ScalePerColumn"/>
/// (via <see cref="ScalePerColumnOpcode"/>), <see cref="OpcodeId.TrimBounds"/>
/// (via <see cref="TrimBoundsOpcode"/>), <see cref="OpcodeId.MapPolynomial"/>
/// (via <see cref="MapPolynomialOpcode"/>), and <see cref="OpcodeId.GainMap"/>
/// (via <see cref="GainMapOpcode"/>) are implemented. Other OpcodeList3
/// opcodes are silently skipped — they don't affect the golden Bayer sample
/// this was built for, but will need real implementations before this can be
/// considered general-purpose.</para>
/// </summary>
public static class OpcodeList3Applier
{
    /// <summary>
    /// Apply every supported opcode in <paramref name="opcodeList"/>, in
    /// order, to <paramref name="stage3"/>. Returns the (possibly replaced)
    /// image; unsupported opcodes are left unapplied.
    /// </summary>
    public static SimpleImage Apply(SimpleImage stage3, DngOpcodeList? opcodeList)
    {
        ArgumentNullException.ThrowIfNull(stage3);
        if (opcodeList is null || opcodeList.IsEmpty) return stage3;

        var image = stage3;

        foreach (var opcode in opcodeList.Entries)
        {
            switch (opcode.Id)
            {
                case OpcodeId.WarpRectilinear:
                {
                    var warpParams = WarpRectilinearParams.Decode(opcode.BodyBytes.Span);
                    warpParams.PropagateToAllPlanes((int)image.Planes);
                    if (!warpParams.IsNopAll())
                        image = LensWarpFilter.Apply(image, warpParams);
                    break;
                }
                case OpcodeId.WarpRectilinear2:
                {
                    var warpParams = WarpRectilinear2Params.Decode(opcode.BodyBytes.Span);
                    warpParams.PropagateToAllPlanes((int)image.Planes);
                    if (!warpParams.IsNopAll())
                        image = LensWarpFilter.Apply(image, warpParams);
                    break;
                }
                case OpcodeId.WarpFisheye:
                {
                    var warpParams = WarpFisheyeParams.Decode(opcode.BodyBytes.Span);
                    warpParams.PropagateToAllPlanes((int)image.Planes);
                    if (!warpParams.IsNopAll())
                        image = LensWarpFilter.Apply(image, warpParams);
                    break;
                }
                case OpcodeId.FixVignetteRadial:
                {
                    var p = FixVignetteRadialOpcode.Decode(opcode.BodyBytes.Span);
                    FixVignetteRadialOpcode.Apply(image, p);
                    break;
                }
                case OpcodeId.DeltaPerColumn:
                {
                    var p = DeltaPerColumnOpcode.Decode(opcode.BodyBytes.Span);
                    DeltaPerColumnOpcode.Apply(image, p);
                    break;
                }
                case OpcodeId.DeltaPerRow:
                {
                    var p = DeltaPerRowOpcode.Decode(opcode.BodyBytes.Span);
                    DeltaPerRowOpcode.Apply(image, p);
                    break;
                }
                case OpcodeId.ScalePerRow:
                {
                    var p = ScalePerRowOpcode.Decode(opcode.BodyBytes.Span);
                    ScalePerRowOpcode.Apply(image, p);
                    break;
                }
                case OpcodeId.ScalePerColumn:
                {
                    var p = ScalePerColumnOpcode.Decode(opcode.BodyBytes.Span);
                    ScalePerColumnOpcode.Apply(image, p);
                    break;
                }
                case OpcodeId.TrimBounds:
                {
                    var bounds = TrimBoundsOpcode.Decode(opcode.BodyBytes.Span);
                    image = TrimBoundsOpcode.Apply(image, bounds);
                    break;
                }
                case OpcodeId.MapPolynomial:
                {
                    var p = MapPolynomialOpcode.Decode(opcode.BodyBytes.Span);
                    MapPolynomialOpcode.Apply(image, p);
                    break;
                }
                case OpcodeId.GainMap:
                {
                    var p = GainMapOpcode.Decode(opcode.BodyBytes.Span);
                    GainMapOpcode.Apply(image, p);
                    break;
                }
                default:
                    // Other OpcodeList3 opcodes are not implemented yet —
                    // they don't affect the golden Bayer sample this was
                    // built for, but will need real implementations before
                    // this can be considered general-purpose.
                    break;
            }
        }

        return image;
    }
}
