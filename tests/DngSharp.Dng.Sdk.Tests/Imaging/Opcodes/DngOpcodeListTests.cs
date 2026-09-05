using DngSharp.Dng.Sdk.Errors;
using DngSharp.Dng.Sdk.IO;
using DngSharp.Dng.Sdk.Imaging.Opcodes;
using DngSharp.Dng.Sdk.Metadata;

namespace DngSharp.Dng.Sdk.Tests.Imaging.Opcodes;

public class DngOpcodeListTests
{
    private static byte[] BuildList(params (OpcodeId Id, DngVersion MinVer, OpcodeFlags Flags, byte[] Body)[] entries)
    {
        // Spec ch. 8: opcode-list stream is ALWAYS big-endian.
        using var ms = new MemoryStream();
        using var w = new DngStream(ms, bigEndian: true, leaveOpen: true);
        w.WriteUInt32((uint)entries.Length);
        foreach (var (id, ver, flags, body) in entries)
        {
            w.WriteUInt32((uint)id);
            uint packed = ((uint)ver.Major << 24) | ((uint)ver.Minor << 16) | ((uint)ver.Patch << 8) | ver.Build;
            w.WriteUInt32(packed);
            w.WriteUInt32((uint)flags);
            w.WriteUInt32((uint)body.Length);
            w.Write(body);
        }
        return ms.ToArray();
    }

    [Fact]
    public void Roundtrips_through_little_endian_stream()
    {
        var raw = BuildList(
            (OpcodeId.TrimBounds, DngVersion.V1_3_0, OpcodeFlags.None, [1, 2, 3, 4]),
            (OpcodeId.GainMap, DngVersion.V1_4_0, OpcodeFlags.Optional, [9, 9, 9]));

        // Critically: the OUTER stream is little-endian; the opcode parser
        // must temporarily flip to big-endian (spec mandate).
        using var s = DngMemoryStream.WrapNoCopy(raw, bigEndian: false);
        Assert.False(s.BigEndian);

        var list = DngOpcodeList.Parse(s, stage: 1, byteCount: raw.Length, streamOffset: 0);
        Assert.Equal(2, list.Count);
        Assert.Equal(OpcodeId.TrimBounds, list.Entries[0].Id);
        Assert.Equal(DngVersion.V1_3_0, list.Entries[0].MinVersion);
        Assert.Equal(4u, (uint)list.Entries[0].BodyBytes.Length);

        Assert.Equal(OpcodeId.GainMap, list.Entries[1].Id);
        Assert.True(list.Entries[1].IsOptional);

        // Parser restores stream endian after exit.
        Assert.False(s.BigEndian);
    }

    [Fact]
    public void MinVersion_excludes_optional_when_requested()
    {
        var raw = BuildList(
            (OpcodeId.TrimBounds, DngVersion.V1_3_0, OpcodeFlags.None, []),
            (OpcodeId.GainMap, DngVersion.V1_7_0, OpcodeFlags.Optional, []));
        using var s = DngMemoryStream.WrapNoCopy(raw, bigEndian: false);
        var list = DngOpcodeList.Parse(s, stage: 1, byteCount: raw.Length, streamOffset: 0);

        Assert.Equal(DngVersion.V1_7_0, list.MinVersion(includeOptional: true));
        Assert.Equal(DngVersion.V1_3_0, list.MinVersion(includeOptional: false));
    }

    [Fact]
    public void Truncated_body_throws_bad_format()
    {
        // Manually craft a list claiming a 100-byte body when only 4 bytes follow.
        using var ms = new MemoryStream();
        using var w = new DngStream(ms, bigEndian: true, leaveOpen: true);
        w.WriteUInt32(1);                  // count
        w.WriteUInt32((uint)OpcodeId.TrimBounds);
        w.WriteUInt32(0x01030000);         // min version 1.3.0.0
        w.WriteUInt32(0);                  // flags
        w.WriteUInt32(100);                // body size
        w.Write(new byte[] { 0, 0, 0, 0 });

        var raw = ms.ToArray();
        using var s = DngMemoryStream.WrapNoCopy(raw, bigEndian: false);
        var ex = Assert.Throws<DngException>(() =>
            DngOpcodeList.Parse(s, stage: 1, byteCount: raw.Length, streamOffset: 0));
        Assert.Equal(DngError.BadFormat, ex.ErrorCode);
    }

    [Fact]
    public void Empty_list_parses_to_zero_entries()
    {
        // count=0, four bytes total.
        var raw = new byte[] { 0, 0, 0, 0 };
        using var s = DngMemoryStream.WrapNoCopy(raw, bigEndian: false);
        var list = DngOpcodeList.Parse(s, stage: 2, byteCount: 4, streamOffset: 0);
        Assert.True(list.IsEmpty);
        Assert.Equal(2, list.Stage);
    }

    [Fact]
    public void Bogus_stage_rejected()
    {
        Assert.Throws<DngException>(() => new DngOpcodeList(0));
        Assert.Throws<DngException>(() => new DngOpcodeList(4));
    }
}
