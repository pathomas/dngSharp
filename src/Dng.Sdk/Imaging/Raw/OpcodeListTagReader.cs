using Dng.Sdk.Container;
using Dng.Sdk.Imaging.Opcodes;
using Dng.Sdk.IO;
using Dng.Sdk.Tiff;

namespace Dng.Sdk.Imaging.Raw;

/// <summary>
/// Locates an <c>OpcodeList1</c>/<c>OpcodeList2</c>/<c>OpcodeList3</c> tag in a
/// main IFD and parses its raw bytes via <see cref="DngOpcodeList.Parse"/>.
/// The tag payload is always big-endian regardless of the host TIFF byte
/// order (spec ch. 8) — <see cref="DngOpcodeList.Parse"/> already accounts
/// for this internally.
/// </summary>
public static class OpcodeListTagReader
{
    /// <summary>
    /// Read the opcode list for <paramref name="stage"/> (1, 2, or 3) from
    /// <paramref name="ifd"/>, or <see langword="null"/> if the corresponding
    /// tag is absent.
    /// </summary>
    public static DngOpcodeList? Read(DngStream stream, TiffIfd ifd, int stage)
    {
        var tag = stage switch
        {
            1 => DngTagCode.OpcodeList1,
            2 => DngTagCode.OpcodeList2,
            3 => DngTagCode.OpcodeList3,
            _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "stage must be 1, 2, or 3"),
        };

        var entry = ifd.Find(tag);
        if (entry is null) return null;

        long offset;
        int byteCount = (int)System.Math.Min((ulong)int.MaxValue, entry.PayloadSize);
        if (byteCount == 0) return new DngOpcodeList(stage);

        if (entry.IsInline)
        {
            // Extremely unlikely for an opcode list (always several bytes),
            // but handle it defensively by round-tripping through a memory
            // stream isn't available here — opcode lists always have a real
            // ValueOffset in practice since they exceed the inline slot size.
            // Fall back to treating InlineValue as if it were at ValueOffset
            // by writing to a scratch location is not supported by DngStream;
            // simplest correct approach: there is no realistic inline case,
            // so just bail out.
            return null;
        }

        offset = entry.ValueOffset;
        return DngOpcodeList.Parse(stream, stage, byteCount, offset);
    }
}
