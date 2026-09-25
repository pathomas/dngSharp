namespace DngSharp.Dng.Sdk.Gpu.Tests;

public class GpuDeviceTests(GpuFixture gpu) : IClassFixture<GpuFixture>
{
    [Fact]
    public void Requested_hardware_backend_is_actually_used()
    {
        var requested = Environment.GetEnvironmentVariable("DNGSHARP_GPU_BACKEND")?.ToLowerInvariant();
        if (requested is "cuda" or "opencl")
        {
            Assert.True(gpu.Device.IsHardwareAccelerator,
                $"Requested {requested} but got {gpu.Device.Name}.");
        }
        else if (requested is null or "")
        {
            Assert.False(gpu.Device.IsHardwareAccelerator);
        }

        Assert.False(string.IsNullOrWhiteSpace(gpu.Device.Name));
        Assert.True(gpu.Device.MemorySize > 0);
    }

    [Fact]
    public void Cpu_backend_is_always_available()
    {
        using var dev = GpuDevice.Create(GpuBackend.Cpu);
        Assert.False(dev.IsHardwareAccelerator);
    }

    [Fact]
    public void HasHardwareDevice_is_consistent_with_Auto()
    {
        using var dev = GpuDevice.Create(GpuBackend.Auto);
        Assert.Equal(GpuDevice.HasHardwareDevice(), dev.IsHardwareAccelerator);
    }
}
