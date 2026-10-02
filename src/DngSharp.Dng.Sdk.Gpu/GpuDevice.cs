using ILGPU;
using ILGPU.Algorithms;
using ILGPU.Runtime;
using ILGPU.Runtime.CPU;
using ILGPU.Runtime.Cuda;
using ILGPU.Runtime.OpenCL;

namespace DngSharp.Dng.Sdk.Gpu;

/// <summary>Which ILGPU back-end to use.</summary>
public enum GpuBackend
{
    /// <summary>CUDA if a device is present, else OpenCL, else the ILGPU CPU accelerator.</summary>
    Auto = 0,
    Cuda = 1,
    OpenCL = 2,
    /// <summary>ILGPU's multi-threaded CPU accelerator. Runs the same kernel IL on the host; used by tests/CI.</summary>
    Cpu = 3,
}

/// <summary>
/// Owns an ILGPU <see cref="Context"/> + <see cref="Accelerator"/> pair.
/// Create one per process (kernel compilation is cached per accelerator)
/// and share it across <see cref="GpuRenderPipeline"/> instances.
/// </summary>
public sealed class GpuDevice : IDisposable
{
    public Context Context { get; }
    public Accelerator Accelerator { get; }

    /// <summary>Back-end actually selected (may differ from the request when <see cref="GpuBackend.Auto"/>).</summary>
    public GpuBackend Backend { get; }

    public string Name => Accelerator.Name;
    public long MemorySize => Accelerator.MemorySize;
    public bool IsHardwareAccelerator => Backend is GpuBackend.Cuda or GpuBackend.OpenCL;

    private GpuDevice(Context context, Accelerator accelerator, GpuBackend backend)
    {
        Context = context;
        Accelerator = accelerator;
        Backend = backend;
    }

    /// <summary>
    /// Create a device. <see cref="GpuBackend.Auto"/> prefers CUDA, then
    /// OpenCL, then falls back to the CPU accelerator so callers always get
    /// a working instance; an explicit hardware back-end throws when no such
    /// device exists.
    /// </summary>
    public static GpuDevice Create(GpuBackend backend = GpuBackend.Auto)
    {
        var context = Context.Create(b => b.Default().EnableAlgorithms());
        try
        {
            switch (backend)
            {
                case GpuBackend.Cuda:
                {
                    var devices = context.GetCudaDevices();
                    if (devices.Count == 0) throw new InvalidOperationException("No CUDA device is available to ILGPU.");
                    return new GpuDevice(context, devices[0].CreateAccelerator(context), GpuBackend.Cuda);
                }
                case GpuBackend.OpenCL:
                {
                    var devices = context.GetCLDevices();
                    if (devices.Count == 0) throw new InvalidOperationException("No OpenCL device is available to ILGPU.");
                    return new GpuDevice(context, devices[0].CreateAccelerator(context), GpuBackend.OpenCL);
                }
                case GpuBackend.Cpu:
                    return new GpuDevice(context, context.CreateCPUAccelerator(0), GpuBackend.Cpu);
                default:
                    if (context.GetCudaDevices().Count > 0)
                        return new GpuDevice(context, context.GetCudaDevices()[0].CreateAccelerator(context), GpuBackend.Cuda);
                    if (context.GetCLDevices().Count > 0)
                        return new GpuDevice(context, context.GetCLDevices()[0].CreateAccelerator(context), GpuBackend.OpenCL);
                    return new GpuDevice(context, context.CreateCPUAccelerator(0), GpuBackend.Cpu);
            }
        }
        catch
        {
            context.Dispose();
            throw;
        }
    }

    /// <summary>True when at least one CUDA or OpenCL device is visible to ILGPU.</summary>
    public static bool HasHardwareDevice()
    {
        using var context = Context.Create(b => b.Default());
        return context.GetCudaDevices().Count > 0 || context.GetCLDevices().Count > 0;
    }

    public void Dispose()
    {
        Accelerator.Dispose();
        Context.Dispose();
    }
}
