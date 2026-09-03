using System.Buffers.Binary;

namespace Dng.Sdk.Tests.TestImages;

/// <summary>
/// Builds raw big-endian <c>DngOpcodeList</c> byte blobs for embedding
/// <c>OpcodeList1</c>/<c>OpcodeList2</c>/<c>OpcodeList3</c> tags in synthetic
/// test DNGs. Matches the wire format parsed by
/// <see cref="Dng.Sdk.Imaging.Opcodes.DngOpcodeList.Parse"/>:
/// <code>
///   uint32 count
///   per opcode:
///     uint32 id
///     uint32 minVersionPacked   // (major&lt;&lt;24)|(minor&lt;&lt;16)|(patch&lt;&lt;8)|build
///     uint32 flags
///     uint32 bodySize
///     byte[bodySize] body
/// </code>
/// always big-endian regardless of the host TIFF byte order (opcode lists
/// are byte-order-independent per DNG spec ch. 8, so they can be copied
/// between files without byte-swapping).
/// </summary>
public static class OpcodeListTestBuilder
{
    /// <summary>One opcode-list entry to encode via <see cref="Build"/>.</summary>
    public readonly record struct Entry(uint Id, byte Major, byte Minor, byte Patch, byte Build, uint Flags, byte[] Body);

    public static byte[] Build(params Entry[] opcodes)
    {
        using var ms = new MemoryStream();

        void WriteU32(uint v)
        {
            var b = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(b, v);
            ms.Write(b);
        }

        WriteU32((uint)opcodes.Length);
        foreach (var op in opcodes)
        {
            WriteU32(op.Id);
            uint packed = ((uint)op.Major << 24) | ((uint)op.Minor << 16) | ((uint)op.Patch << 8) | op.Build;
            WriteU32(packed);
            WriteU32(op.Flags);
            WriteU32((uint)op.Body.Length);
            ms.Write(op.Body);
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Builds a <c>FixVignetteRadial</c> opcode body: 5 real64 gain-polynomial
    /// coefficients (<c>k0..k4</c>) followed by real64 <c>centerH</c>,
    /// <c>centerV</c> — see <see cref="Dng.Sdk.Imaging.Opcodes.FixVignetteRadialOpcode"/>.
    /// </summary>
    public static byte[] BuildFixVignetteRadialBody(double[] coefficients, double centerH, double centerV)
    {
        using var ms = new MemoryStream();
        Span<byte> b8 = stackalloc byte[8];

        foreach (var c in coefficients)
        {
            BinaryPrimitives.WriteDoubleBigEndian(b8, c);
            ms.Write(b8);
        }

        BinaryPrimitives.WriteDoubleBigEndian(b8, centerH);
        ms.Write(b8);
        BinaryPrimitives.WriteDoubleBigEndian(b8, centerV);
        ms.Write(b8);

        return ms.ToArray();
    }
}
