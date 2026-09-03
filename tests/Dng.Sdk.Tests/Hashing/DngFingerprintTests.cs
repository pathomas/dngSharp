using Dng.Sdk.Hashing;

namespace Dng.Sdk.Tests.Hashing;

public class DngFingerprintTests
{
    [Fact]
    public void Default_is_null_and_invalid()
    {
        var fp = default(DngFingerprint);
        Assert.True(fp.IsNull);
        Assert.False(fp.IsValid);
    }

    [Fact]
    public void Md5_of_known_input()
    {
        // RFC 1321 vector.
        var fp = DngFingerprint.MD5("abc"u8);
        Assert.Equal("900150983cd24fb0d6963f7d28e17f72", fp.ToUtf8HexString());
    }

    [Fact]
    public void Hex_round_trip()
    {
        var fp1 = DngFingerprint.MD5("hello world"u8);
        var hex = fp1.ToUtf8HexString();
        var fp2 = default(DngFingerprint);
        Assert.True(fp2.TryParseHex(hex));
        Assert.Equal(fp1, fp2);
    }

    [Fact]
    public void TryParseHex_rejects_bad_input()
    {
        var fp = default(DngFingerprint);
        Assert.False(fp.TryParseHex("nothex"));
        Assert.False(fp.TryParseHex("zzzz0150983cd24fb0d6963f7d28e17f72"));
        Assert.False(fp.TryParseHex(new string('0', 31))); // wrong length
    }

    [Fact]
    public void Collapse32_is_deterministic()
    {
        var fp1 = DngFingerprint.MD5("abc"u8);
        var fp2 = DngFingerprint.MD5("abc"u8);
        Assert.Equal(fp1.Collapse32(), fp2.Collapse32());
    }

    [Fact]
    public void HexCharToNum_covers_all_digits()
    {
        Assert.Equal(0, DngFingerprint.HexCharToNum('0'));
        Assert.Equal(9, DngFingerprint.HexCharToNum('9'));
        Assert.Equal(10, DngFingerprint.HexCharToNum('a'));
        Assert.Equal(15, DngFingerprint.HexCharToNum('f'));
        Assert.Equal(15, DngFingerprint.HexCharToNum('F'));
        Assert.Equal(-1, DngFingerprint.HexCharToNum('g'));
        Assert.Equal(-1, DngFingerprint.HexCharToNum(' '));
    }
}
