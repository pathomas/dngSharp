using System.Buffers.Binary;
using DngSharp.Dng.Sdk.Errors;
using DngSharp.Dng.Sdk.Pipeline;
using DngSharp.Dng.Sdk.Primitives;

namespace DngSharp.Dng.Sdk.Imaging.Opcodes;

/// <summary>
/// Decodes and applies the <c>TrimBounds</c> opcode
/// (<see cref="OpcodeId.TrimBounds"/>, id 6). Mirrors
/// <c>dng_opcode_TrimBounds</c> in <c>dng_misc_opcodes.cpp</c>: crops the
/// image to a sub-rectangle, replacing it with a new zero-origin image of
/// the trimmed size (mirroring <c>dng_simple_image::Trim</c>, which resets
/// <c>fBounds</c> to <c>(0,0,H,W)</c> after trimming).
///
/// <para>Wire format (all big-endian; no leading self-describing
/// byte-count field, since <c>DngOpcodeList</c>'s generic <c>bodySize</c>
/// framing already accounts for it — always 16 for this opcode):
/// <code>
///   int32 t, l, b, r  // the trim rectangle, in the image's current
///                     // coordinate space
/// </code>
/// </para>
///
/// <para>Unlike the Delta/Scale opcodes, <c>TrimBounds</c> is pixel-type
/// agnostic — it's a pure crop, not a per-sample math operation — so it can
/// be safely wired into OpcodeList1 (raw/integer Stage-1 images) as well as
/// OpcodeList2/3 (Float32 Stage-2/3 images).</para>
/// </summary>
public static class TrimBoundsOpcode
{
    /// <summary>Decode a <c>TrimBounds</c> opcode body.</summary>
    public static DngRect Decode(ReadOnlySpan<byte> body)
    {
        if (body.Length < 16)
            DngThrow.BadFormat("TrimBounds: body too short for bounds");

        int t = BinaryPrimitives.ReadInt32BigEndian(body.Slice(0, 4));
        int l = BinaryPrimitives.ReadInt32BigEndian(body.Slice(4, 4));
        int b = BinaryPrimitives.ReadInt32BigEndian(body.Slice(8, 4));
        int r = BinaryPrimitives.ReadInt32BigEndian(body.Slice(12, 4));

        var bounds = new DngRect(t, l, b, r);
        if (bounds.IsEmpty)
            DngThrow.BadFormat("TrimBounds: bounds rectangle is empty");

        return bounds;
    }

    /// <summary>
    /// Crop <paramref name="image"/> to <paramref name="bounds"/>, returning
    /// a new zero-origin image of the trimmed size. Throws
    /// <see cref="DngException"/> if <paramref name="bounds"/> is empty or
    /// not fully contained within the image's current bounds (mirroring
    /// native's <c>fBounds.IsEmpty() || (fBounds &amp; image-&gt;Bounds()) != fBounds</c>
    /// check).
    /// </summary>
    public static SimpleImage Apply(SimpleImage image, DngRect bounds)
    {
        ArgumentNullException.ThrowIfNull(image);

        if (bounds.IsEmpty || DngRect.Intersect(bounds, image.Bounds) != bounds)
            DngThrow.BadFormat($"TrimBounds: bounds {bounds} not fully contained within image bounds {image.Bounds}");

        return ImageCrop.Crop(image, bounds);
    }
}
