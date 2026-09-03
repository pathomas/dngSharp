using Dng.Sdk.Errors;
using Dng.Sdk.Metadata.Iptc;

namespace Dng.Sdk.Tests.Metadata;

public class IptcReaderTests
{
    /// <summary>
    /// Build one IPTC IIM record: 0x1C, record, dataset, length(BE u16), payload.
    /// </summary>
    private static byte[] Record(byte record, byte dataset, ReadOnlySpan<byte> payload)
    {
        var buf = new byte[5 + payload.Length];
        buf[0] = 0x1C;
        buf[1] = record;
        buf[2] = dataset;
        buf[3] = (byte)((payload.Length >> 8) & 0xFF);
        buf[4] = (byte)(payload.Length & 0xFF);
        payload.CopyTo(buf.AsSpan(5));
        return buf;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        int total = parts.Sum(p => p.Length);
        var buf = new byte[total];
        int o = 0;
        foreach (var p in parts) { p.CopyTo(buf, o); o += p.Length; }
        return buf;
    }

    [Fact]
    public void Parses_title_keywords_and_copyright()
    {
        var block = Concat(
            Record(2, 5, "A Sunset"u8),
            Record(2, 25, "sunset"u8),
            Record(2, 25, "horizon"u8),
            Record(2, 116, "(c) 2024 Test Photographer"u8));
        var iptc = IptcReader.Read(block);
        Assert.Equal("A Sunset", iptc.Title);
        Assert.Equal(["sunset", "horizon"], iptc.Keywords);
        Assert.Equal("(c) 2024 Test Photographer", iptc.CopyrightNotice);
        Assert.False(iptc.IsEmpty);
    }

    [Fact]
    public void Parses_date_then_time_into_single_datetime()
    {
        var block = Concat(
            Record(2, 55, "20240715"u8),
            Record(2, 60, "133045+0000"u8));
        var iptc = IptcReader.Read(block);
        Assert.Equal(2024u, iptc.DateCreated.Year);
        Assert.Equal(7u, iptc.DateCreated.Month);
        Assert.Equal(15u, iptc.DateCreated.Day);
        Assert.Equal(13u, iptc.DateCreated.Hour);
        Assert.Equal(30u, iptc.DateCreated.Minute);
        Assert.Equal(45u, iptc.DateCreated.Second);
    }

    [Fact]
    public void Truncated_payload_throws_bad_format()
    {
        var block = new byte[] { 0x1C, 2, 5, 0, 100 /* len 100 but no payload */ };
        var ex = Assert.Throws<DngException>(() => IptcReader.Read(block));
        Assert.Equal(DngError.BadFormat, ex.ErrorCode);
    }

    [Fact]
    public void Empty_block_yields_empty_iptc()
    {
        Assert.True(IptcReader.Read([]).IsEmpty);
    }

    [Fact]
    public void Unknown_datasets_are_ignored()
    {
        var block = Concat(
            Record(2, 5, "Title"u8),
            Record(2, 200, "vendor-specific junk"u8),  // unknown dataset
            Record(2, 120, "Description text"u8));
        var iptc = IptcReader.Read(block);
        Assert.Equal("Title", iptc.Title);
        Assert.Equal("Description text", iptc.Description);
    }
}
