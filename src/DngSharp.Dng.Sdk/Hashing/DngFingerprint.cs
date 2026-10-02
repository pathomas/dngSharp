using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace DngSharp.Dng.Sdk.Hashing;

/// <summary>
/// 128-bit (MD5) image-data fingerprint. Mirrors <c>dng_fingerprint</c>.
///
/// <para>MD5 is used for DNG's <c>OriginalRawFileDigest</c>, <c>RawDataUniqueID</c>,
/// preview match keys, etc. It is not used for security; we use it for
/// compatibility with the on-disk format only.</para>
/// </summary>
public struct DngFingerprint : IEquatable<DngFingerprint>
{
    public const int Size = 16;

    [InlineArray(Size)]
    private struct Storage { private byte _e0; }

    private Storage _data;

    public bool IsNull
    {
        get
        {
            for (int i = 0; i < Size; i++) if (_data[i] != 0) return false;
            return true;
        }
    }

    public bool IsValid => !IsNull;

    public Span<byte> AsSpan() => MemoryMarshal.CreateSpan(ref _data[0], Size);
    public readonly ReadOnlySpan<byte> AsReadOnlySpan() =>
        MemoryMarshal.CreateReadOnlySpan(
            ref System.Runtime.CompilerServices.Unsafe.AsRef(in _data[0]), Size);

    public void Clear() => AsSpan().Clear();

    /// <summary>
    /// Collapse to a 32-bit hash for hashtable use. Matches C++ <c>Collapse32</c>:
    /// reads the 16 bytes as four big-endian uint32s and XORs them.
    /// </summary>
    public readonly uint Collapse32()
    {
        var s = AsReadOnlySpan();
        return BinaryPrimitives.ReadUInt32BigEndian(s)
             ^ BinaryPrimitives.ReadUInt32BigEndian(s[4..])
             ^ BinaryPrimitives.ReadUInt32BigEndian(s[8..])
             ^ BinaryPrimitives.ReadUInt32BigEndian(s[12..]);
    }

    public readonly string ToUtf8HexString()
    {
        Span<char> buf = stackalloc char[Size * 2];
        var bytes = AsReadOnlySpan();
        for (int i = 0; i < Size; i++)
        {
            buf[i * 2] = HexLower(bytes[i] >> 4);
            buf[i * 2 + 1] = HexLower(bytes[i] & 0xF);
        }
        return new string(buf);
    }

    /// <summary>
    /// Parse a 32-char lowercase or uppercase hex string. Returns false on
    /// any non-hex character or wrong length.
    /// </summary>
    public bool TryParseHex(ReadOnlySpan<char> hex)
    {
        if (hex.Length != Size * 2) return false;
        var dst = AsSpan();
        for (int i = 0; i < Size; i++)
        {
            int hi = HexCharToNum(hex[i * 2]);
            int lo = HexCharToNum(hex[i * 2 + 1]);
            if (hi < 0 || lo < 0) return false;
            dst[i] = (byte)((hi << 4) | lo);
        }
        return true;
    }

    public static int HexCharToNum(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    private static char HexLower(int n) => n < 10 ? (char)('0' + n) : (char)('a' + n - 10);

    public bool Equals(DngFingerprint other) =>
        AsReadOnlySpan().SequenceEqual(other.AsReadOnlySpan());

    public override readonly bool Equals(object? obj) =>
        obj is DngFingerprint f &&
        AsReadOnlySpan().SequenceEqual(f.AsReadOnlySpan());

    public override readonly int GetHashCode() => (int)Collapse32();

    public static bool operator ==(DngFingerprint a, DngFingerprint b) => a.Equals(b);
    public static bool operator !=(DngFingerprint a, DngFingerprint b) => !a.Equals(b);

    public override readonly string ToString() => ToUtf8HexString();

    /// <summary>
    /// One-shot MD5 of a byte span. Mirrors the streaming behavior of
    /// <c>dng_md5_printer</c> for callers that already have the data in memory.
    /// </summary>
    public static DngFingerprint MD5(ReadOnlySpan<byte> data)
    {
        var fp = default(DngFingerprint);
        MD5Hasher.HashData(data, fp.AsSpan());
        return fp;
    }
}

/// <summary>Thin alias so call sites can swap implementations.</summary>
internal static class MD5Hasher
{
    public static void HashData(ReadOnlySpan<byte> source, Span<byte> destination) =>
        MD5.HashData(source, destination);
}
