using Dng.Sdk.Errors;
using Dng.Sdk.IO;
using Dng.Sdk.Primitives;

namespace Dng.Sdk.Tests.IO;

public class DngStreamTests
{
    [Fact]
    public void Little_endian_round_trip_scalars()
    {
        var ms = new MemoryStream();
        using (var w = new DngStream(ms, bigEndian: false, leaveOpen: true))
        {
            w.WriteUInt8(0x12);
            w.WriteUInt16(0x1234);
            w.WriteUInt32(0xDEADBEEF);
            w.WriteUInt64(0x0123456789ABCDEF);
            w.WriteSingle(3.14f);
            w.WriteDouble(System.Math.PI);
            w.WriteInt32(-42);
        }
        ms.Position = 0;
        using var r = new DngStream(ms, bigEndian: false);
        Assert.Equal((byte)0x12, r.ReadUInt8());
        Assert.Equal((ushort)0x1234, r.ReadUInt16());
        Assert.Equal(0xDEADBEEFu, r.ReadUInt32());
        Assert.Equal(0x0123456789ABCDEFul, r.ReadUInt64());
        Assert.Equal(3.14f, r.ReadSingle());
        Assert.Equal(System.Math.PI, r.ReadDouble(), 12);
        Assert.Equal(-42, r.ReadInt32());
    }

    [Fact]
    public void Big_endian_differs_from_little()
    {
        var ms = new MemoryStream();
        using (var w = new DngStream(ms, bigEndian: true, leaveOpen: true))
        {
            w.WriteUInt32(0x11223344);
        }
        // Big-endian 0x11223344 == bytes 11 22 33 44
        Assert.Equal([0x11, 0x22, 0x33, 0x44], ms.ToArray());
    }

    [Fact]
    public void Endian_can_flip_mid_stream()
    {
        // Models DNG's behavior: opcode lists are always big-endian regardless
        // of the host TIFF's byte order.
        var ms = new MemoryStream([
            0x12, 0x34,             // LE uint16: 0x3412
            0xDE, 0xAD, 0xBE, 0xEF, // BE uint32: 0xDEADBEEF
        ]);
        using var r = new DngStream(ms, bigEndian: false);
        Assert.Equal((ushort)0x3412, r.ReadUInt16());
        r.SetBigEndian();
        Assert.Equal(0xDEADBEEFu, r.ReadUInt32());
    }

    [Fact]
    public void Eof_throws_dng_exception()
    {
        var ms = new MemoryStream([0x01]);
        using var r = new DngStream(ms);
        Assert.Equal((byte)1, r.ReadUInt8());
        var ex = Assert.Throws<DngException>(() => r.ReadUInt8());
        Assert.Equal(DngError.EndOfFile, ex.ErrorCode);
    }

    [Fact]
    public void Rational_reads_consume_eight_bytes()
    {
        // 8-byte URational: numerator=1, denominator=300 LE.
        var ms = new MemoryStream([
            0x01, 0x00, 0x00, 0x00,
            0x2C, 0x01, 0x00, 0x00, // 300 LE
        ]);
        using var r = new DngStream(ms);
        var rat = r.ReadURational();
        Assert.Equal(new DngURational(1u, 300u), rat);
        Assert.Equal(8, r.Position);
    }
}
