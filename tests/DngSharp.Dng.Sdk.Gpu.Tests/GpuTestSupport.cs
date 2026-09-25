using DngSharp.Dng.Sdk.Imaging;
using DngSharp.Dng.Sdk.Imaging.Raw;
using DngSharp.Dng.Sdk.Math;
using DngSharp.Dng.Sdk.Pixels;
using DngSharp.Dng.Sdk.Primitives;
using DngSharp.Dng.Sdk.Render;

namespace DngSharp.Dng.Sdk.Gpu.Tests;

/// <summary>
/// One ILGPU device per test class. Defaults to the CPU accelerator so the
/// suite is deterministic on CI; set <c>DNGSHARP_GPU_BACKEND=cuda|opencl</c>
/// to exercise real hardware.
/// </summary>
public sealed class GpuFixture : IDisposable
{
    public GpuDevice Device { get; }
    public GpuRenderPipeline Pipeline { get; }

    public GpuFixture()
    {
        var backend = Environment.GetEnvironmentVariable("DNGSHARP_GPU_BACKEND")?.ToLowerInvariant() switch
        {
            "cuda" => GpuBackend.Cuda,
            "opencl" => GpuBackend.OpenCL,
            "auto" => GpuBackend.Auto,
            _ => GpuBackend.Cpu,
        };
        Device = GpuDevice.Create(backend);
        Pipeline = new GpuRenderPipeline(Device);
    }

    public void Dispose()
    {
        Pipeline.Dispose();
        Device.Dispose();
    }
}

/// <summary>Deterministic synthetic inputs shared by the equivalence tests.</summary>
internal static class Synthetic
{
    public const int Width = 256;
    public const int Height = 192;

    /// <summary>
    /// RGGB Bayer UInt16 mosaic: a smooth two-axis gradient with a colour
    /// cast per CFA cell and a little hash noise, plus a hard edge so the
    /// demosaic border/edge code paths get exercised.
    /// </summary>
    public static SimpleImage BayerStage1(int width = Width, int height = Height)
    {
        var img = new SimpleImage(new DngRect(0, 0, height, width), 1, PixelType.UInt16);
        var px = img.Buffer.AsTypedSpan<ushort>();
        for (int r = 0; r < height; r++)
        {
            for (int c = 0; c < width; c++)
            {
                double gx = c / (double)(width - 1), gy = r / (double)(height - 1);
                double gain = (r & 1, c & 1) switch
                {
                    (0, 0) => 0.55,   // R
                    (1, 1) => 0.35,   // B
                    _ => 0.9,         // G
                };
                double v = 100 + 3800 * gain * (0.15 + 0.85 * gx * (1 - 0.4 * gy));
                if (c > width * 3 / 4 && r < height / 3) v *= 0.3;
                v += ((r * 7 + c * 13) & 31) - 16;
                px[r * width + c] = (ushort)System.Math.Clamp(v, 0, 4095);
            }
        }
        return img;
    }

    /// <summary>Three-plane UInt16 LinearRaw image (planar) for the passthrough path.</summary>
    public static SimpleImage LinearRawStage1(int width = Width, int height = Height)
    {
        var img = new SimpleImage(new DngRect(0, 0, height, width), 3, PixelType.UInt16);
        var px = img.Buffer.AsTypedSpan<ushort>();
        int n = width * height;
        for (int r = 0; r < height; r++)
        {
            for (int c = 0; c < width; c++)
            {
                int i = r * width + c;
                px[i] = (ushort)(200 + 3000 * c / width);
                px[i + n] = (ushort)(200 + 3000 * r / height);
                px[i + 2 * n] = (ushort)(200 + ((r * c) & 2047));
            }
        }
        return img;
    }

    public static LinearizationInfo BayerLinearization(bool withLut = false, bool withDeltas = false)
    {
        var lin = new LinearizationInfo
        {
            BlackLevel = [64, 66, 68, 70],
            BlackLevelRepeatDim = (2, 2),
            WhiteLevel = [4095],
        };
        if (withLut)
        {
            var lut = new ushort[4096];
            for (int i = 0; i < lut.Length; i++) lut[i] = (ushort)System.Math.Min(4095, i + i / 16);
            lin.LinearizationTable = lut;
        }
        if (withDeltas)
        {
            lin.BlackLevelDeltaV = Enumerable.Range(0, Height).Select(r => (r % 5) * 0.5).ToArray();
            lin.BlackLevelDeltaH = Enumerable.Range(0, Width).Select(c => (c % 3) * -0.25).ToArray();
        }
        return lin;
    }

    public static LinearizationInfo LinearRawLinearization() => new()
    {
        BlackLevel = [128, 130, 132],
        WhiteLevel = [4095, 4095, 4095],
    };

    public static MosaicInfo Rggb() => new()
    {
        Pattern = (2, 2),
        CfaPlaneColor = [0, 1, 1, 2],
    };

    /// <summary>A plausible camera→XYZ_D50 matrix (roughly an iPhone-class sensor with WB applied).</summary>
    public static DngMatrix CameraToXyzD50() => DngMatrix.Matrix3x3(
        0.7347, 0.1543, 0.0752,
        0.3117, 0.6270, 0.0613,
        0.0229, 0.1021, 0.7000);

    public static (double, double)[] ToneCurve() =>
    [
        (0.0, 0.0), (0.1, 0.16), (0.25, 0.38), (0.5, 0.66), (0.75, 0.87), (1.0, 1.0),
    ];

    /// <summary>2.5-D table (val divisions = 1) with mild, spatially varying hue/sat/val deltas.</summary>
    public static HueSatMap HueSatMap25D(int hue = 12, int sat = 4) => BuildHueSatMap(hue, sat, 1);

    /// <summary>Full 3-D table.</summary>
    public static HueSatMap HueSatMap3D(int hue = 6, int sat = 3, int val = 3) => BuildHueSatMap(hue, sat, val);

    private static HueSatMap BuildHueSatMap(int hue, int sat, int val)
    {
        var data = new float[hue * sat * val * 3];
        for (int v = 0; v < val; v++)
            for (int h = 0; h < hue; h++)
                for (int s = 0; s < sat; s++)
                {
                    int e = ((v * hue + h) * sat + s) * 3;
                    data[e] = 8f * MathF.Sin(h * 0.7f + v);          // hue shift, degrees
                    data[e + 1] = 1f + 0.15f * MathF.Cos(s * 1.3f);  // sat scale
                    data[e + 2] = 1f + 0.05f * MathF.Sin(s + h);     // val scale
                }
        return new HueSatMap(hue, sat, val, data);
    }
}

/// <summary>Max / mean absolute difference helpers over planar float images and RGB8 buffers.</summary>
internal static class Diff
{
    public static (double Max, double Mean) Floats(SimpleImage a, SimpleImage b)
    {
        Assert.Equal(a.Bounds, b.Bounds);
        Assert.Equal(a.Planes, b.Planes);
        var fa = a.Buffer.AsTypedSpan<float>();
        var fb = b.Buffer.AsTypedSpan<float>();
        int n = (int)(a.Bounds.W * a.Bounds.H * a.Planes);
        double max = 0, sum = 0;
        for (int i = 0; i < n; i++)
        {
            double d = System.Math.Abs((double)fa[i] - fb[i]);
            if (d > max) max = d;
            sum += d;
        }
        return (max, sum / n);
    }

    /// <summary>Returns (max diff, mean diff, fraction of samples differing by more than 1).</summary>
    public static (int Max, double Mean, double FractionOver1) Bytes(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        Assert.Equal(a.Length, b.Length);
        int max = 0; long sum = 0, over1 = 0;
        for (int i = 0; i < a.Length; i++)
        {
            int d = System.Math.Abs(a[i] - b[i]);
            if (d > max) max = d;
            if (d > 1) over1++;
            sum += d;
        }
        return (max, sum / (double)a.Length, over1 / (double)a.Length);
    }
}
