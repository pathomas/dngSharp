using Dng.Sdk;

namespace Dng.Sdk.Tests;

public class SmokeTests
{
    [Fact]
    public void SdkExposesSpecVersion()
    {
        Assert.Equal("1.7.1", DngSdkInfo.SpecVersion);
    }
}
