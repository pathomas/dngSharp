using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Dng.Sdk.Errors;
using Dng.Sdk.Primitives;

namespace Dng.Sdk.IO;

/// <summary>
/// Endian-aware DNG/TIFF stream wrapper. Mirrors <c>dng_stream</c>.
///
/// <para>Wraps a <see cref="Stream"/> and tracks whether reads/writes should
/// honor little- or big-endian byte order. TIFF files declare their byte
/// order in the header (<c>II</c> = little, <c>MM</c> = big); DNG opcode
/// lists are <b>always</b> big-endian regardless of the host TIFF order — use
/// <see cref="SetBigEndian"/> to switch when parsing opcode bodies.</para>
///
/// <para>This class owns its position cursor and supports random-access seeking
/// against the underlying stream. Disposal of a writable stream is not flushed
/// implicitly — callers must <see cref="Flush"/> before letting the SDK
/// reclaim the instance, matching the C++ contract.</para>
/// </summary>
public class DngStream : IDisposable
{
    private readonly Stream _inner;
    private readonly bool _leaveOpen;
    private bool _bigEndian;
    private bool _disposed;

    /// <summary>
    /// Optional offset of this stream within its source file. Mirrors
    /// <c>fOffsetInOriginalFile</c>. Used to translate <see cref="Position"/>
    /// back to absolute file positions when sub-streams are spawned.
    /// </summary>
    public long OffsetInOriginalFile { get; }

    public DngStream(Stream inner, bool bigEndian = false, long offsetInOriginalFile = 0, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (!inner.CanRead && !inner.CanWrite)
            DngThrow.ProgramError("DngStream requires a readable or writable inner stream");
        _inner = inner;
        _leaveOpen = leaveOpen;
        _bigEndian = bigEndian;
        OffsetInOriginalFile = offsetInOriginalFile;
    }

    public bool BigEndian => _bigEndian;
    public bool LittleEndian => !_bigEndian;

    public void SetBigEndian(bool bigEndian = true) => _bigEndian = bigEndian;
    public void SetLittleEndian(bool littleEndian = true) => _bigEndian = !littleEndian;

    public long Length => _inner.Length;

    public long Position
    {
        get => _inner.Position;
        set
        {
            if (value < 0 || value > _inner.Length)
                throw new DngException(DngError.EndOfFile, $"Seek out of range: {value}");
            _inner.Position = value;
        }
    }

    public long PositionInOriginalFile => OffsetInOriginalFile + Position;

    public void Skip(long delta) => Position = checked(Position + delta);

    public void Flush() => _inner.Flush();

    /// <summary>
    /// Read exactly <paramref name="buffer"/>.Length bytes; throws
    /// <see cref="DngException"/>(<see cref="DngError.EndOfFile"/>) if the
    /// stream ends first.
    /// </summary>
    public void ReadExactly(Span<byte> buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = _inner.Read(buffer[read..]);
            if (n <= 0)
                throw new DngException(DngError.EndOfFile,
                    $"Unexpected EOF (wanted {buffer.Length} bytes, got {read})");
            read += n;
        }
    }

    public void Write(ReadOnlySpan<byte> buffer) => _inner.Write(buffer);

    // ---- Endian-aware reads -----------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte ReadUInt8()
    {
        int b = _inner.ReadByte();
        if (b < 0) throw new DngException(DngError.EndOfFile, "EOF reading byte");
        return (byte)b;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public sbyte ReadInt8() => (sbyte)ReadUInt8();

    public ushort ReadUInt16()
    {
        Span<byte> b = stackalloc byte[2];
        ReadExactly(b);
        return _bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(b) : BinaryPrimitives.ReadUInt16LittleEndian(b);
    }

    public short ReadInt16()
    {
        Span<byte> b = stackalloc byte[2];
        ReadExactly(b);
        return _bigEndian ? BinaryPrimitives.ReadInt16BigEndian(b) : BinaryPrimitives.ReadInt16LittleEndian(b);
    }

    public uint ReadUInt32()
    {
        Span<byte> b = stackalloc byte[4];
        ReadExactly(b);
        return _bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(b) : BinaryPrimitives.ReadUInt32LittleEndian(b);
    }

    public int ReadInt32()
    {
        Span<byte> b = stackalloc byte[4];
        ReadExactly(b);
        return _bigEndian ? BinaryPrimitives.ReadInt32BigEndian(b) : BinaryPrimitives.ReadInt32LittleEndian(b);
    }

    public ulong ReadUInt64()
    {
        Span<byte> b = stackalloc byte[8];
        ReadExactly(b);
        return _bigEndian ? BinaryPrimitives.ReadUInt64BigEndian(b) : BinaryPrimitives.ReadUInt64LittleEndian(b);
    }

    public long ReadInt64()
    {
        Span<byte> b = stackalloc byte[8];
        ReadExactly(b);
        return _bigEndian ? BinaryPrimitives.ReadInt64BigEndian(b) : BinaryPrimitives.ReadInt64LittleEndian(b);
    }

    public float ReadSingle()
    {
        Span<byte> b = stackalloc byte[4];
        ReadExactly(b);
        return _bigEndian ? BinaryPrimitives.ReadSingleBigEndian(b) : BinaryPrimitives.ReadSingleLittleEndian(b);
    }

    public double ReadDouble()
    {
        Span<byte> b = stackalloc byte[8];
        ReadExactly(b);
        return _bigEndian ? BinaryPrimitives.ReadDoubleBigEndian(b) : BinaryPrimitives.ReadDoubleLittleEndian(b);
    }

    public DngURational ReadURational()
    {
        uint n = ReadUInt32();
        uint d = ReadUInt32();
        return new DngURational(n, d);
    }

    public DngSRational ReadSRational()
    {
        int n = ReadInt32();
        int d = ReadInt32();
        return new DngSRational(n, d);
    }

    /// <summary>
    /// Read a NUL-terminated ASCII string of at most <paramref name="maxBytes"/>
    /// bytes. The terminator is consumed; the returned string excludes it.
    /// </summary>
    public string ReadAscii(int maxBytes)
    {
        if (maxBytes <= 0) return string.Empty;
        Span<byte> buf = maxBytes <= 512 ? stackalloc byte[maxBytes] : new byte[maxBytes];
        ReadExactly(buf);
        int len = buf.IndexOf((byte)0);
        if (len < 0) len = buf.Length;
        return System.Text.Encoding.ASCII.GetString(buf[..len]);
    }

    // ---- Endian-aware writes ----------------------------------------------

    public void WriteUInt8(byte v) => _inner.WriteByte(v);
    public void WriteInt8(sbyte v) => _inner.WriteByte((byte)v);

    public void WriteUInt16(ushort v)
    {
        Span<byte> b = stackalloc byte[2];
        if (_bigEndian) BinaryPrimitives.WriteUInt16BigEndian(b, v);
        else BinaryPrimitives.WriteUInt16LittleEndian(b, v);
        _inner.Write(b);
    }

    public void WriteInt16(short v) => WriteUInt16((ushort)v);

    public void WriteUInt32(uint v)
    {
        Span<byte> b = stackalloc byte[4];
        if (_bigEndian) BinaryPrimitives.WriteUInt32BigEndian(b, v);
        else BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        _inner.Write(b);
    }

    public void WriteInt32(int v) => WriteUInt32((uint)v);

    public void WriteUInt64(ulong v)
    {
        Span<byte> b = stackalloc byte[8];
        if (_bigEndian) BinaryPrimitives.WriteUInt64BigEndian(b, v);
        else BinaryPrimitives.WriteUInt64LittleEndian(b, v);
        _inner.Write(b);
    }

    public void WriteInt64(long v) => WriteUInt64((ulong)v);

    public void WriteSingle(float v)
    {
        Span<byte> b = stackalloc byte[4];
        if (_bigEndian) BinaryPrimitives.WriteSingleBigEndian(b, v);
        else BinaryPrimitives.WriteSingleLittleEndian(b, v);
        _inner.Write(b);
    }

    public void WriteDouble(double v)
    {
        Span<byte> b = stackalloc byte[8];
        if (_bigEndian) BinaryPrimitives.WriteDoubleBigEndian(b, v);
        else BinaryPrimitives.WriteDoubleLittleEndian(b, v);
        _inner.Write(b);
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Standard dispose pattern. Derived stream types (e.g., HTTP-backed,
    /// memory-mapped, sub-stream) should override this to release additional
    /// resources and call <c>base.Dispose(disposing)</c>.
    /// </summary>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;
        if (disposing && !_leaveOpen) _inner.Dispose();
    }
}
