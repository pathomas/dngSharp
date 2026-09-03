using Dng.Sdk.Imaging.Profile;

namespace Dng.Sdk.Tests.Imaging;

public class CameraProfilePrecedenceTests
{
    [Fact]
    public void Higher_precedence_source_displaces_lower()
    {
        var p = new DngCameraProfile();
        p.SetGainTable(GainTableSource.RawIfdLegacy, [0xAA]);
        Assert.Equal(GainTableSource.RawIfdLegacy, p.GainTableSource);

        // IFD 0 > RawIfdLegacy → should replace.
        p.SetGainTable(GainTableSource.Ifd0, [0xBB]);
        Assert.Equal(GainTableSource.Ifd0, p.GainTableSource);
        Assert.Equal(0xBB, p.GainTable[0]);

        // CameraProfileIfd > Ifd0 → should replace.
        p.SetGainTable(GainTableSource.CameraProfileIfd, [0xCC]);
        Assert.Equal(GainTableSource.CameraProfileIfd, p.GainTableSource);
        Assert.Equal(0xCC, p.GainTable[0]);
    }

    [Fact]
    public void Lower_precedence_source_is_dropped()
    {
        var p = new DngCameraProfile();
        p.SetGainTable(GainTableSource.CameraProfileIfd, [0xCC]);

        // Trying to set a lower-priority source should NOT replace.
        p.SetGainTable(GainTableSource.Ifd0, [0xBB]);
        p.SetGainTable(GainTableSource.RawIfdLegacy, [0xAA]);

        Assert.Equal(GainTableSource.CameraProfileIfd, p.GainTableSource);
        Assert.Equal(0xCC, p.GainTable[0]);
    }

    [Fact]
    public void None_source_clears_table()
    {
        var p = new DngCameraProfile();
        p.SetGainTable(GainTableSource.Ifd0, [0xBB]);
        p.SetGainTable(GainTableSource.None, []);
        Assert.Equal(GainTableSource.None, p.GainTableSource);
        Assert.True(p.GainTable.IsEmpty);
    }
}
