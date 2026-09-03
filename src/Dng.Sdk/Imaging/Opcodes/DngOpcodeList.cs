using Dng.Sdk.Errors;
using Dng.Sdk.IO;
using Dng.Sdk.Metadata;

namespace Dng.Sdk.Imaging.Opcodes;

/// <summary>
/// One opcode entry: id + min DNG version + flags + opaque body bytes.
/// Mirrors <c>dng_opcode</c>. Concrete decoders for each opcode body live
/// in Phase 6 where the render pipeline consumes them; this layer captures
/// the framing exactly so a parsed opcode list can be round-tripped
/// byte-for-byte.
/// </summary>
public sealed class DngOpcode
{
    public required OpcodeId Id { get; init; }
    public required DngVersion MinVersion { get; init; }
    public required OpcodeFlags Flags { get; init; }
    public required ReadOnlyMemory<byte> BodyBytes { get; init; }

    public bool IsOptional => (Flags & OpcodeFlags.Optional) != 0;
    public bool IsOptionalForPreview => (Flags & OpcodeFlags.OptionalForPreview) != 0;
}

/// <summary>
/// Parsed opcode list, tied to a pipeline stage. Mirrors
/// <c>dng_opcode_list</c>. The on-disk format is:
///
/// <code>
///   uint32 count
///   for each:
///     uint32 opcodeId
///     uint32 minVersion (packed; major.minor.patch.build → 1 byte each in 1.5+)
///     uint32 flags
///     uint32 bodySize
///     byte[bodySize] body
/// </code>
///
/// <para><b>The entire stream is big-endian on disk, regardless of the host
/// TIFF byte order.</b> This is by design — it lets opcode lists be copied
/// between files without byte-swapping. (Spec ch. 8.) <see cref="Parse"/>
/// always flips the stream's endian to big.</para>
/// </summary>
public sealed class DngOpcodeList
{
    /// <summary>Pipeline stage this list runs at: 1 (raw), 2 (linear), 3 (demosaiced).</summary>
    public int Stage { get; }

    public List<DngOpcode> Entries { get; } = [];

    public DngOpcodeList(int stage)
    {
        if (stage is < 1 or > 3) DngThrow.ProgramError($"Opcode-list stage must be 1, 2, or 3 — got {stage}");
        Stage = stage;
    }

    public bool IsEmpty => Entries.Count == 0;
    public int Count => Entries.Count;

    /// <summary>
    /// Minimum DNG version that can read every opcode in this list. If
    /// <paramref name="includeOptional"/> is false, optional opcodes that a
    /// reader may skip are excluded from the calculation.
    /// </summary>
    public DngVersion MinVersion(bool includeOptional)
    {
        var min = new DngVersion(1, 3, 0, 0);  // opcode lists themselves require >= 1.3
        foreach (var op in Entries)
        {
            if (!includeOptional && op.IsOptional) continue;
            if (op.MinVersion > min) min = op.MinVersion;
        }
        return min;
    }

    /// <summary>
    /// Parse an opcode list from <paramref name="stream"/>. The stream is
    /// flipped to big-endian (per spec) for the duration of the call and
    /// restored on exit.
    ///
    /// <para>Throws <see cref="DngError.BadFormat"/> if the framing claims
    /// more bytes than <paramref name="byteCount"/> permits or if a body's
    /// declared size would overflow <see cref="int.MaxValue"/>.</para>
    /// </summary>
    public static DngOpcodeList Parse(DngStream stream, int stage, int byteCount, long streamOffset)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (byteCount < 0) DngThrow.BadFormat("Opcode list byteCount must be non-negative");

        var list = new DngOpcodeList(stage);
        if (byteCount == 0) return list;

        long startPos = streamOffset;
        long endPos = checked(streamOffset + byteCount);
        long savedPos = stream.Position;
        bool savedEndian = stream.BigEndian;

        try
        {
            stream.Position = startPos;
            stream.SetBigEndian(true);

            uint count = stream.ReadUInt32();
            // Sanity: a malicious file could claim a billion opcodes. Cap.
            if (count > 1_000_000)
                throw new DngException(DngError.BadFormat, $"Opcode list claims {count} entries");

            for (uint i = 0; i < count; i++)
            {
                uint opcodeId = stream.ReadUInt32();
                uint minVerPacked = stream.ReadUInt32();
                uint flags = stream.ReadUInt32();
                uint bodySize = stream.ReadUInt32();

                if (bodySize > int.MaxValue)
                    throw new DngException(DngError.BadFormat, $"Opcode {opcodeId} body size {bodySize} > int.MaxValue");
                if (stream.Position + bodySize > endPos)
                    throw new DngException(DngError.BadFormat,
                        $"Opcode {opcodeId} body extends past list end (need {bodySize} bytes)");

                var body = new byte[(int)bodySize];
                stream.ReadExactly(body);

                list.Entries.Add(new DngOpcode
                {
                    Id = (OpcodeId)opcodeId,
                    MinVersion = UnpackMinVersion(minVerPacked),
                    Flags = (OpcodeFlags)flags,
                    BodyBytes = body,
                });
            }

            if (stream.Position != endPos)
                throw new DngException(DngError.BadFormat,
                    $"Opcode list length mismatch: parsed {stream.Position - startPos} of {byteCount} bytes");
        }
        finally
        {
            stream.SetBigEndian(savedEndian);
            stream.Position = savedPos;
        }

        return list;
    }

    /// <summary>
    /// Minimum version is packed as 4 bytes big-endian:
    /// <c>(major &lt;&lt; 24) | (minor &lt;&lt; 16) | (patch &lt;&lt; 8) | build</c>.
    /// </summary>
    private static DngVersion UnpackMinVersion(uint packed) =>
        new((byte)(packed >> 24), (byte)(packed >> 16), (byte)(packed >> 8), (byte)packed);
}
