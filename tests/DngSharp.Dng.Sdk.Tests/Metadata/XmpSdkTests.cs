using DngSharp.Dng.Sdk.Metadata.Xmp;

namespace DngSharp.Dng.Sdk.Tests.Metadata;

public class XmpSdkTests
{
    [Fact]
    public void Null_sdk_initialize_and_dispose()
    {
        using var sdk = new NullXmpSdk();
        Assert.False(sdk.IsInitialized);
        sdk.Initialize();
        Assert.True(sdk.IsInitialized);
        sdk.Dispose();
        Assert.False(sdk.IsInitialized);
    }

    [Fact]
    public void Null_sdk_parse_returns_empty_meta()
    {
        using var sdk = new NullXmpSdk();
        sdk.Initialize();
        using var meta = sdk.Parse(default);
        Assert.Null(meta.GetProperty("http://ns.adobe.com/exif/1.0/", "ExposureTime"));
        meta.SetProperty("http://ns.adobe.com/exif/1.0/", "ExposureTime", "1/250");
        // Null sdk discards writes.
        Assert.Null(meta.GetProperty("http://ns.adobe.com/exif/1.0/", "ExposureTime"));
        Assert.Empty(meta.SerializePacket());
    }

    [Fact]
    public void Throwing_sdk_signals_unwired_dependency()
    {
        using var sdk = new ThrowingXmpSdk();
        sdk.Initialize();
        Assert.Throws<Errors.DngException>(() => sdk.Parse(default));
    }

    [Fact]
    public void Packet_decodes_as_utf8()
    {
        var pkt = new DngXmpPacket("<rdf>hi</rdf>"u8.ToArray());
        Assert.False(pkt.IsEmpty);
        Assert.Equal("<rdf>hi</rdf>", pkt.AsUtf8String());
    }
}
