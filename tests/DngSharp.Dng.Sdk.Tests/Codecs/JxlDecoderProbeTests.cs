using DngSharp.Dng.Sdk.Jxl;

namespace DngSharp.Dng.Sdk.Tests.Codecs;

public class JxlDecoderProbeTests
{
    [Fact]
    public void Sniff_empty_returns_not_enough_bytes_when_libjxl_available()
    {
        // This test doesn't require libjxl to be present — IsAvailable returns
        // false when the native library can't be loaded, and Sniff handles
        // that path by returning Invalid.
        var result = JxlProbe.Sniff([]);
        Assert.True(result is JxlSignatureKind.NotEnoughBytes or JxlSignatureKind.Invalid);
    }

    [Fact]
    public void IsAvailable_does_not_throw()
    {
        // Regardless of whether libjxl is installed on the test runner, this
        // probe must complete without throwing.
        _ = JxlDecoder.IsAvailable;
    }

    [Fact]
    public void Decode_without_libjxl_surfaces_clear_error()
    {
        // When libjxl isn't present, we want a domain-specific exception that
        // names the missing dependency — not an obscure DllNotFoundException.
        if (JxlDecoder.IsAvailable) return;  // skip when present

        var decoder = new JxlDecoder();
        var buf = DngSharp.Dng.Sdk.Pixels.PixelBuffer.Interleaved(
            new DngSharp.Dng.Sdk.Primitives.DngRect(0, 0, 2, 2), 1,
            DngSharp.Dng.Sdk.Pixels.PixelType.UInt8, new byte[4]);
        var ex = Assert.Throws<DngSharp.Dng.Sdk.Errors.DngException>(() =>
            decoder.Decode([], buf, bigEndian: false));
        Assert.Equal(DngSharp.Dng.Sdk.Errors.DngError.JxlDecoder, ex.ErrorCode);
        Assert.Contains("libjxl", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
