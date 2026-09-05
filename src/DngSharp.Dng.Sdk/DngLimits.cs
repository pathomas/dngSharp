namespace DngSharp.Dng.Sdk;

/// <summary>
/// SDK-wide capacity constants. Mirrors <c>dng_sdk_limits.h</c>.
/// </summary>
public static class DngLimits
{
    public const uint MaxDNGPreviews = 20;
    public const uint MaxSemanticMasks = 100;
    public const uint MaxSubIFDs = MaxDNGPreviews + MaxSemanticMasks + 5;
    public const uint MaxChainedIFDs = 10;
    public const uint MaxSamplesPerPixel = 5;
    public const uint MaxColorPlanes = 4;
}
