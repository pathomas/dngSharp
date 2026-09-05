using DngSharp.Dng.Sdk.Metadata;

namespace DngSharp.Dng.Sdk.Tests.Metadata;

public class DngDateTimeTests
{
    [Fact]
    public void Default_is_invalid()
    {
        Assert.False(default(DngDateTime).IsValid);
    }

    [Fact]
    public void Constructed_value_is_valid()
    {
        var dt = new DngDateTime(2024, 7, 15, 13, 30, 45);
        Assert.True(dt.IsValid);
        Assert.Equal("2024:07:15 13:30:45", dt.ToExifString());
        Assert.Equal("2024-07-15T13:30:45", dt.ToIso8601());
    }

    [Theory]
    [InlineData("2024:07:15 13:30:45")]   // standard EXIF
    [InlineData("2024-07-15T13:30:45")]   // ISO 8601 variant
    public void Parse_accepts_supported_formats(string input)
    {
        var dt = default(DngDateTime);
        Assert.True(dt.TryParseExif(input));
        Assert.Equal(2024u, dt.Year);
        Assert.Equal(7u, dt.Month);
        Assert.Equal(45u, dt.Second);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a date")]
    [InlineData("2024:13:01 00:00:00")]  // month 13
    [InlineData("0000:01:01 00:00:00")]  // year 0
    [InlineData("2024:07")]              // too short
    public void Parse_rejects_bad_input(string input)
    {
        var dt = default(DngDateTime);
        Assert.False(dt.TryParseExif(input));
    }

    [Fact]
    public void NUL_padded_strings_parse()
    {
        var dt = default(DngDateTime);
        Assert.True(dt.TryParseExif("2024:07:15 13:30:45\0\0"));
        Assert.Equal(2024u, dt.Year);
    }
}
