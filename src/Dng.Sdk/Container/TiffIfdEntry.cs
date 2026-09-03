using System.Buffers.Binary;
using Dng.Sdk.IO;
using Dng.Sdk.Tiff;

namespace Dng.Sdk.Container;

/// <summary>
/// One IFD entry: a (tag, type, count, value-or-offset) tuple. Mirrors a row
/// of <c>dng_ifd</c> entries before they're dispatched to typed metadata
/// holders.
/// </summary>
public sealed class TiffIfdEntry
{
    public required DngTagCode Tag { get; init; }
    public required TiffDataType Type { get; init; }

    /// <summary>
    /// Number of elements (not bytes). Widened to <see cref="ulong"/> for
    /// BigTIFF compatibility — BigTIFF entries carry 8-byte counts and can
    /// validly describe payloads > 4 GiB (image strips, raw data blocks).
    /// </summary>
    public required ulong Count { get; init; }

    /// <summary>
    /// Total payload size in bytes (<c>Type.Size() * Count</c>). When this fits
    /// in the value-or-offset slot (4 bytes for TIFF, 8 bytes for BigTIFF) the
    /// payload is stored inline in <see cref="InlineValue"/>; otherwise
    /// <see cref="ValueOffset"/> points at it.
    /// </summary>
    public required ulong PayloadSize { get; init; }

    /// <summary>
    /// Inline payload bytes when <see cref="IsInline"/> is true. Length is
    /// <see cref="PayloadSize"/> (≤ 4 for TIFF / ≤ 8 for BigTIFF). May be
    /// empty when the payload lives at <see cref="ValueOffset"/>.
    /// </summary>
    public required ReadOnlyMemory<byte> InlineValue { get; init; }

    /// <summary>Absolute file offset of the payload when out-of-line.</summary>
    public required long ValueOffset { get; init; }

    public bool IsInline => InlineValue.Length > 0;

    /// <summary>
    /// Interpret the inline payload as a single <c>uint</c>. Convenience for
    /// the common case of <c>NewSubFileType</c>, <c>Compression</c>,
    /// <c>Photometric</c>, image width/height, etc.
    /// </summary>
    public uint GetScalarUInt(bool bigEndian)
    {
        if (!IsInline || Count != 1)
            throw new InvalidOperationException($"Tag {Tag}: not a single inline scalar");
        var s = InlineValue.Span;
        return Type switch
        {
            TiffDataType.Byte or TiffDataType.SByte or TiffDataType.Undefined or TiffDataType.Ascii => s[0],
            TiffDataType.Short or TiffDataType.SShort or TiffDataType.HalfFloat => bigEndian
                ? BinaryPrimitives.ReadUInt16BigEndian(s) : BinaryPrimitives.ReadUInt16LittleEndian(s),
            TiffDataType.Long or TiffDataType.SLong or TiffDataType.Ifd => bigEndian
                ? BinaryPrimitives.ReadUInt32BigEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s),
            _ => throw new InvalidOperationException($"Tag {Tag}: type {Type} is not a scalar uint"),
        };
    }
}
