using DngSharp.Dng.Sdk.Imaging;
using DngSharp.Dng.Sdk.Imaging.Opcodes;

namespace DngSharp.Dng.Sdk.Pipeline;

/// <summary>
/// Applies the subset of OpcodeList1 opcodes this port supports to a raw
/// Stage-1 image. Mirrors <c>host.ApplyOpcodeList(fOpcodeList1, ...)</c> in
/// <c>dng_negative::ReadStage1Image</c>, which runs immediately after strip
/// decode and before any linearization — i.e. straight on the camera-native
/// (unlinearized) sample values.
///
/// <para>OpcodeList1 is the stage where native typically carries
/// <c>FixBadPixelsConstant</c>/<c>FixBadPixelsList</c> (defective-pixel
/// patching, which must happen before white/black rescale distorts the
/// sentinel values) and occasionally <c>MapPolynomial</c>/<c>MapTable</c> for
/// per-camera raw-value remapping. <see cref="OpcodeId.TrimBounds"/> (see
/// <see cref="TrimBoundsOpcode"/>), <see cref="OpcodeId.FixBadPixelsConstant"/>
/// (see <see cref="FixBadPixelsConstantOpcode"/>), and
/// <see cref="OpcodeId.FixBadPixelsList"/> (see
/// <see cref="FixBadPixelsListOpcode"/>), and <see cref="OpcodeId.MapTable"/>
/// (see <see cref="MapTableOpcode"/>) are implemented; the rest are
/// tracked as separate follow-up work and every such entry is currently
/// skipped — this class exists as the wiring point so those opcodes have
/// somewhere to run once implemented, and so OpcodeList1-bearing files don't
/// silently drop data readers expect to be applied.</para>
/// </summary>
public static class OpcodeList1Applier
{
    /// <summary>
    /// Apply every supported opcode in <paramref name="opcodeList"/>, in
    /// order, to <paramref name="stage1"/>. Returns the (possibly replaced)
    /// image; unsupported opcodes are left unapplied.
    /// </summary>
    public static SimpleImage Apply(SimpleImage stage1, DngOpcodeList? opcodeList)
    {
        ArgumentNullException.ThrowIfNull(stage1);
        if (opcodeList is null || opcodeList.IsEmpty) return stage1;

        var image = stage1;

        foreach (var opcode in opcodeList.Entries)
        {
            switch (opcode.Id)
            {
                case OpcodeId.TrimBounds:
                {
                    var bounds = TrimBoundsOpcode.Decode(opcode.BodyBytes.Span);
                    image = TrimBoundsOpcode.Apply(image, bounds);
                    break;
                }
                case OpcodeId.FixBadPixelsConstant:
                {
                    var p = FixBadPixelsConstantOpcode.Decode(opcode.BodyBytes.Span);
                    FixBadPixelsConstantOpcode.Apply(image, p);
                    break;
                }
                case OpcodeId.FixBadPixelsList:
                {
                    var p = FixBadPixelsListOpcode.Decode(opcode.BodyBytes.Span);
                    FixBadPixelsListOpcode.Apply(image, p);
                    break;
                }
                case OpcodeId.MapTable:
                {
                    var p = MapTableOpcode.Decode(opcode.BodyBytes.Span);
                    MapTableOpcode.Apply(image, p);
                    break;
                }
                default:
                    // No other OpcodeList1 opcode is implemented yet. Stage 1
                    // is raw and typically integer-typed (not Float32), and
                    // none of the implemented in-place opcodes (see
                    // DeltaPerColumnOpcode etc.) support integer buffers yet
                    // — dispatching them here would risk throwing on
                    // real-world raw DNGs where this port previously
                    // silently (if incompletely) skipped the opcode. When an
                    // integer-buffer-capable implementation lands, dispatch
                    // on opcode.Id here exactly as OpcodeList3Applier does
                    // for WarpRectilinear.
                    break;
            }
        }

        return image;
    }
}
