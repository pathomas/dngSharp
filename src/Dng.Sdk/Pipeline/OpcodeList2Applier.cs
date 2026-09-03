using Dng.Sdk.Imaging;
using Dng.Sdk.Imaging.Opcodes;

namespace Dng.Sdk.Pipeline;

/// <summary>
/// Applies the subset of OpcodeList2 opcodes this port supports to a
/// linearized Stage-2 image. Mirrors <c>host.ApplyOpcodeList(fOpcodeList2,
/// ...)</c> in <c>dng_negative::BuildStage2Image</c>, which runs after
/// linearization (LUT → black subtract → white rescale → clip) but before
/// Stage 3 (demosaic).
///
/// <para>OpcodeList2 is where native commonly carries
/// <c>FixVignetteRadial</c> (lens-shading correction on linear-reference
/// values, before demosaic spreads any error across neighboring CFA samples)
/// and <c>GainMap</c> — both implemented, see
/// <see cref="FixVignetteRadialOpcode"/>/<see cref="GainMapOpcode"/>.
/// <see cref="OpcodeId.DeltaPerColumn"/>,
/// <see cref="OpcodeId.DeltaPerRow"/>, <see cref="OpcodeId.ScalePerRow"/>,
/// <see cref="OpcodeId.ScalePerColumn"/>, and <see cref="OpcodeId.TrimBounds"/>
/// are implemented (see <see cref="DeltaPerColumnOpcode"/>/
/// <see cref="DeltaPerRowOpcode"/>/<see cref="ScalePerRowOpcode"/>/
/// <see cref="ScalePerColumnOpcode"/>/<see cref="TrimBoundsOpcode"/>/
/// <see cref="MapPolynomialOpcode"/>/<see cref="GainMapOpcode"/>); the
/// rest are tracked as separate follow-up work and every such entry is
/// currently skipped — this class exists as the wiring point so those
/// opcodes have somewhere to run once implemented, and so OpcodeList2-bearing
/// files don't silently drop data readers expect to be applied.</para>
/// </summary>
public static class OpcodeList2Applier
{
    /// <summary>
    /// Apply every supported opcode in <paramref name="opcodeList"/>, in
    /// order, to <paramref name="stage2"/>. Returns the (possibly replaced)
    /// image; unsupported opcodes are left unapplied.
    /// </summary>
    public static SimpleImage Apply(SimpleImage stage2, DngOpcodeList? opcodeList)
    {
        ArgumentNullException.ThrowIfNull(stage2);
        if (opcodeList is null || opcodeList.IsEmpty) return stage2;

        var image = stage2;

        foreach (var opcode in opcodeList.Entries)
        {
            switch (opcode.Id)
            {
                case OpcodeId.DeltaPerRow:
                {
                    var p = DeltaPerRowOpcode.Decode(opcode.BodyBytes.Span);
                    DeltaPerRowOpcode.Apply(image, p);
                    break;
                }
                case OpcodeId.DeltaPerColumn:
                {
                    var p = DeltaPerColumnOpcode.Decode(opcode.BodyBytes.Span);
                    DeltaPerColumnOpcode.Apply(image, p);
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
                case OpcodeId.FixVignetteRadial:
                {
                    var p = FixVignetteRadialOpcode.Decode(opcode.BodyBytes.Span);
                    FixVignetteRadialOpcode.Apply(image, p);
                    break;
                }
                default:
                    // No other OpcodeList2 opcode is implemented yet. When
                    // one is added (MapTable), dispatch on opcode.Id here
                    // exactly as OpcodeList3Applier does for WarpRectilinear.
                    break;
            }
        }

        return image;
    }
}
