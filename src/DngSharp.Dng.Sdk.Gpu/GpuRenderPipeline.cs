using DngSharp.Dng.Sdk.Imaging;
using DngSharp.Dng.Sdk.Imaging.Raw;
using DngSharp.Dng.Sdk.Math;
using DngSharp.Dng.Sdk.Pixels;
using DngSharp.Dng.Sdk.Primitives;
using DngSharp.Dng.Sdk.Render;
using DngSharp.Dng.Sdk.Tiff;
using ILGPU;
using ILGPU.Runtime;

namespace DngSharp.Dng.Sdk.Gpu;

/// <summary>
/// Colour-pipeline inputs for <see cref="GpuRenderPipeline"/>. Same
/// quantities the CPU path hands to <c>Stage3Renderer.Render</c> +
/// <c>HdrToneMapper.Apply</c>.
/// </summary>
/// <param name="CameraToXyzD50">From <c>Stage3Renderer.ResolveCameraToXyzD50</c>.</param>
/// <param name="BaselineExposure">Stops; applied as <c>2^x</c>.</param>
/// <param name="ColorSpace">Output primaries.</param>
/// <param name="HueSatMap">Optional profile HSV table, already CCT-interpolated.</param>
/// <param name="ToneCurve">Optional <c>ProfileToneCurve</c> control points. Applied in
/// the render step and — mirroring the CPU pipeline — again by the SDR tone-map step.</param>
/// <param name="ToneMapToSdr">Run the HDR→SDR tone map (false = HDR/linear output).</param>
public sealed record GpuRenderParams(
    DngMatrix CameraToXyzD50,
    double BaselineExposure = 0.0,
    OutputColorSpace ColorSpace = OutputColorSpace.Srgb,
    HueSatMap? HueSatMap = null,
    (double Input, double Output)[]? ToneCurve = null,
    bool ToneMapToSdr = true);

/// <summary>
/// Device-resident Stage 1 → SDR RGB8 render. The raw image is uploaded
/// once; linearize, demosaic, colour transform, tone map and gamma/quantize
/// run as successive kernels on device buffers; only the final 8-bit
/// interleaved RGB (or, for the per-stage helpers, that stage's planar
/// float image) is copied back.
///
/// <para>Opcode lists are <b>not</b> executed here. Callers must check that
/// OpcodeList1–3 are empty (or run them on the CPU between the per-stage
/// helpers) — the resident path exists for the common no-opcode case.</para>
///
/// <para>Supported input: <see cref="PixelType.UInt16"/> Stage 1 with one
/// plane (2×2 Bayer CFA) or three planes (LinearRaw/RGB passthrough).
/// Instances are not thread-safe; kernels are compiled once per instance.</para>
/// </summary>
public sealed class GpuRenderPipeline : IDisposable
{
    private readonly Accelerator _acc;

    private readonly Action<Index1D, ArrayView<ushort>, ArrayView<float>, ArrayView<float>, ArrayView<float>,
        ArrayView<float>, ArrayView<float>, ArrayView<float>, LinearizeParams> _linearize;
    private readonly Action<Index1D, ArrayView<float>, ArrayView<float>, DemosaicParams> _demosaic;
    private readonly Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>,
        ArrayView<float>, RenderParams> _render;
    private readonly Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, PlanarParams> _toneMap;
    private readonly Action<Index1D, ArrayView<float>, ArrayView<byte>, PlanarParams> _gamma;

    public GpuDevice Device { get; }

    public GpuRenderPipeline(GpuDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        Device = device;
        _acc = device.Accelerator;

        _linearize = _acc.LoadAutoGroupedStreamKernel<Index1D, ArrayView<ushort>, ArrayView<float>, ArrayView<float>,
            ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, LinearizeParams>(GpuKernels.Linearize);
        _demosaic = _acc.LoadAutoGroupedStreamKernel<Index1D, ArrayView<float>, ArrayView<float>, DemosaicParams>(GpuKernels.Demosaic);
        _render = _acc.LoadAutoGroupedStreamKernel<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>,
            ArrayView<float>, ArrayView<float>, RenderParams>(GpuKernels.Render);
        _toneMap = _acc.LoadAutoGroupedStreamKernel<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, PlanarParams>(GpuKernels.ToneMap);
        _gamma = _acc.LoadAutoGroupedStreamKernel<Index1D, ArrayView<float>, ArrayView<byte>, PlanarParams>(GpuKernels.GammaQuantize);
    }

    // ── Resident end-to-end path ──────────────────────────────────────────────

    /// <summary>
    /// Stage 1 → interleaved SDR RGB8 entirely on device. Returns
    /// <c>crop.W × crop.H × 3</c> bytes (crop defaults to the full image).
    /// </summary>
    public byte[] RenderToRgb8(
        SimpleImage stage1,
        LinearizationInfo lin,
        Photometric photometric,
        MosaicInfo? mosaic,
        GpuRenderParams render,
        DngRect? crop = null)
    {
        ArgumentNullException.ThrowIfNull(stage1);
        ArgumentNullException.ThrowIfNull(render);

        var cropRect = crop ?? stage1.Bounds;
        if (!stage1.Bounds.Contains(cropRect))
            throw new ArgumentException($"Crop {cropRect} lies outside image bounds {stage1.Bounds}.", nameof(crop));

        int w = (int)stage1.Bounds.W, h = (int)stage1.Bounds.H;
        int cw = (int)cropRect.W, ch = (int)cropRect.H;

        using var stage2 = LinearizeToDevice(stage1, lin);
        using var stage3 = DemosaicToDevice(stage2.View, w, h, (int)stage1.Planes, photometric, mosaic);
        using var rgb = _acc.Allocate1D<float>((long)cw * ch * 3);
        using var rgb8 = _acc.Allocate1D<byte>((long)cw * ch * 3);

        RenderOnDevice(stage3.View, w, h, cropRect.L - stage1.Bounds.L, cropRect.T - stage1.Bounds.T, cw, ch, render, rgb.View);
        if (render.ToneMapToSdr) ToneMapOnDevice(rgb.View, cw * ch, render.ToneCurve);
        _gamma(cw * ch, rgb.View, rgb8.View, new PlanarParams { PixelCount = cw * ch });
        _acc.Synchronize();

        var result = new byte[cw * ch * 3];
        rgb8.View.CopyToCPU(result);
        return result;
    }

    // ── Per-stage helpers (host round-trip each) ──────────────────────────────

    /// <summary>GPU equivalent of <c>Stage2Builder.Build</c>.</summary>
    public SimpleImage Linearize(SimpleImage stage1, LinearizationInfo lin)
    {
        ArgumentNullException.ThrowIfNull(stage1);
        using var dev = LinearizeToDevice(stage1, lin);
        _acc.Synchronize();
        return Download(dev.View, stage1.Bounds, stage1.Planes);
    }

    /// <summary>GPU equivalent of <c>DemosaicBilinear.Build</c>.</summary>
    public SimpleImage Demosaic(SimpleImage stage2, MosaicInfo mosaic)
    {
        ArgumentNullException.ThrowIfNull(stage2);
        RequireFloat32(stage2, nameof(stage2));
        using var src = Upload<float>(stage2);
        using var dst = DemosaicToDevice(src.View, (int)stage2.Bounds.W, (int)stage2.Bounds.H, (int)stage2.Planes, Photometric.Cfa, mosaic);
        _acc.Synchronize();
        return Download(dst.View, stage2.Bounds, 3);
    }

    /// <summary>
    /// GPU equivalent of <c>Stage3Renderer.Render</c> (matrix, HueSatMap,
    /// exposure, tone curve — no tone map) over <paramref name="crop"/>.
    /// </summary>
    public SimpleImage Render(SimpleImage stage3, GpuRenderParams render, DngRect? crop = null)
    {
        ArgumentNullException.ThrowIfNull(stage3);
        ArgumentNullException.ThrowIfNull(render);
        RequireFloat32(stage3, nameof(stage3));
        if (stage3.Planes != 3)
            throw new ArgumentException("Render expects a 3-plane Stage-3 image.", nameof(stage3));

        var cropRect = crop ?? stage3.Bounds;
        int cw = (int)cropRect.W, ch = (int)cropRect.H;
        using var src = Upload<float>(stage3);
        using var dst = _acc.Allocate1D<float>((long)cw * ch * 3);
        RenderOnDevice(src.View, (int)stage3.Bounds.W, (int)stage3.Bounds.H,
            cropRect.L - stage3.Bounds.L, cropRect.T - stage3.Bounds.T, cw, ch, render, dst.View);
        _acc.Synchronize();
        // Match ImageCrop.Crop: the cropped result is rebased to origin (0,0).
        return Download(dst.View, new DngRect(0, 0, ch, cw), 3);
    }

    /// <summary>GPU equivalent of <c>HdrToneMapper.Apply</c> (in place).</summary>
    public void ToneMap(SimpleImage rgb, (double Input, double Output)[]? toneCurve)
    {
        ArgumentNullException.ThrowIfNull(rgb);
        RequireFloat32(rgb, nameof(rgb));
        if (rgb.Planes != 3)
            throw new ArgumentException("ToneMap expects a 3-plane image.", nameof(rgb));

        using var dev = Upload<float>(rgb);
        ToneMapOnDevice(dev.View, (int)(rgb.Bounds.W * rgb.Bounds.H), toneCurve);
        _acc.Synchronize();
        dev.View.BaseView.CopyToCPU(rgb.Buffer.AsTypedSpan<float>()[..(int)dev.Length]);
    }

    /// <summary>GPU equivalent of <c>Stage3Renderer.GammaAndQuantize</c>.</summary>
    public byte[] GammaAndQuantize(SimpleImage rgb)
    {
        ArgumentNullException.ThrowIfNull(rgb);
        RequireFloat32(rgb, nameof(rgb));
        if (rgb.Planes != 3)
            throw new ArgumentException("GammaAndQuantize expects a 3-plane image.", nameof(rgb));

        int n = (int)(rgb.Bounds.W * rgb.Bounds.H);
        using var src = Upload<float>(rgb);
        using var dst = _acc.Allocate1D<byte>((long)n * 3);
        _gamma(n, src.View, dst.View, new PlanarParams { PixelCount = n });
        _acc.Synchronize();
        var result = new byte[n * 3];
        dst.View.CopyToCPU(result);
        return result;
    }

    // ── Device-side building blocks ───────────────────────────────────────────

    private MemoryBuffer1D<float, Stride1D.Dense> LinearizeToDevice(SimpleImage stage1, LinearizationInfo lin)
    {
        ArgumentNullException.ThrowIfNull(lin);
        if (stage1.PixelType != PixelType.UInt16)
            throw new NotSupportedException($"GpuRenderPipeline: Stage 1 must be UInt16 (got {stage1.PixelType}).");
        if (lin.BlackLevel.Length == 0 || lin.WhiteLevel.Length == 0)
            throw new ArgumentException("BlackLevel and WhiteLevel must be non-empty.", nameof(lin));

        int w = (int)stage1.Bounds.W, h = (int)stage1.Bounds.H, planes = (int)stage1.Planes;
        bool hasRepeat = lin.BlackLevelRepeatDim.Rows > 1 || lin.BlackLevelRepeatDim.Cols > 1;

        // Per-plane scale, computed exactly as Stage2Builder does (min black
        // across the repeat pattern when one is present).
        var scales = new float[planes];
        for (int p = 0; p < planes; p++)
        {
            double white = lin.WhiteLevel[System.Math.Min(p, lin.WhiteLevel.Length - 1)];
            double minBlack = hasRepeat ? lin.BlackLevel.Min() : lin.BlackLevel[System.Math.Min(p, lin.BlackLevel.Length - 1)];
            double span = white - minBlack;
            if (span <= 0)
                throw new ArgumentException($"Linearization: white ({white}) <= black ({minBlack}) on plane {p}.", nameof(lin));
            scales[p] = (float)(1.0 / span);
        }

        var blacks = Array.ConvertAll(lin.BlackLevel, d => (float)d);
        var lut = lin.LinearizationTable is { } t ? Array.ConvertAll(t, v => (float)v) : [];
        var deltaV = lin.BlackLevelDeltaV is { } dv ? Array.ConvertAll(dv, d => (float)d) : [];
        var deltaH = lin.BlackLevelDeltaH is { } dh ? Array.ConvertAll(dh, d => (float)d) : [];

        long n = (long)w * h * planes;
        using var src = Upload<ushort>(stage1);
        var dst = _acc.Allocate1D<float>(n);
        using var lutBuf = UploadArray(lut);
        using var blackBuf = UploadArray(blacks);
        using var scaleBuf = UploadArray(scales);
        using var dvBuf = UploadArray(deltaV);
        using var dhBuf = UploadArray(deltaH);

        _linearize((int)n, src.View, dst.View, lutBuf.View, blackBuf.View, scaleBuf.View, dvBuf.View, dhBuf.View,
            new LinearizeParams
            {
                Width = w,
                Height = h,
                Planes = planes,
                RepeatRows = (int)lin.BlackLevelRepeatDim.Rows,
                RepeatCols = (int)lin.BlackLevelRepeatDim.Cols,
                HasRepeat = hasRepeat ? 1 : 0,
            });

        // The stream is in-order, so the temporaries can be released as soon
        // as the launch is enqueued; Synchronize() by the caller drains it.
        _acc.Synchronize();
        return dst;
    }

    private MemoryBuffer1D<float, Stride1D.Dense> DemosaicToDevice(
        ArrayView<float> stage2, int w, int h, int planes, Photometric photometric, MosaicInfo? mosaic)
    {
        switch (photometric)
        {
            case Photometric.LinearRaw:
            case Photometric.Rgb:
                if (planes != 3)
                    throw new NotSupportedException($"GpuRenderPipeline: {photometric} passthrough needs 3 planes (got {planes}).");
                var copy = _acc.Allocate1D<float>(stage2.Length);
                copy.View.CopyFrom(stage2);
                return copy;

            case Photometric.Cfa when mosaic is not null:
                if (planes != 1)
                    throw new NotSupportedException("GpuRenderPipeline: CFA demosaic needs a single-plane Stage 2.");
                if (mosaic.Pattern.Rows != 2 || mosaic.Pattern.Cols != 2)
                    throw new NotSupportedException($"GpuRenderPipeline: only 2×2 Bayer patterns supported; got {mosaic.Pattern.Rows}×{mosaic.Pattern.Cols}.");
                if (mosaic.CfaPlaneColor.Length < 4)
                    throw new ArgumentException("MosaicInfo.CfaPlaneColor must have at least 4 entries for a 2×2 pattern.", nameof(mosaic));

                var dst = _acc.Allocate1D<float>((long)w * h * 3);
                _demosaic(w * h, stage2, dst.View, new DemosaicParams
                {
                    Width = w,
                    Height = h,
                    P00 = mosaic.CfaPlaneColor[0],
                    P01 = mosaic.CfaPlaneColor[1],
                    P10 = mosaic.CfaPlaneColor[2],
                    P11 = mosaic.CfaPlaneColor[3],
                });
                return dst;

            default:
                throw new NotSupportedException($"GpuRenderPipeline: photometric={photometric} is not supported.");
        }
    }

    private void RenderOnDevice(
        ArrayView<float> stage3, int srcW, int srcH, int cropL, int cropT, int cropW, int cropH,
        GpuRenderParams render, ArrayView<float> dst)
    {
        var p = new RenderParams
        {
            SrcWidth = srcW,
            SrcHeight = srcH,
            CropLeft = cropL,
            CropTop = cropT,
            CropWidth = cropW,
            CropHeight = cropH,
            ExposureScale = (float)System.Math.Pow(2.0, render.BaselineExposure),
        };

        DngMatrix a, b;
        if (render.HueSatMap is { } hsm)
        {
            a = Stage3Renderer.CameraToOutputSpace(render.CameraToXyzD50, OutputColorSpace.ProPhotoRgb);
            b = Stage3Renderer.ProPhotoToOutputSpace(render.ColorSpace);
            p.HasHueSatMap = 1;
            p.HueDivisions = hsm.HueDivisions;
            p.SatDivisions = hsm.SatDivisions;
            p.ValDivisions = hsm.ValDivisions;
        }
        else
        {
            a = Stage3Renderer.CameraToOutputSpace(render.CameraToXyzD50, render.ColorSpace);
            b = DngMatrix.Identity3x3();
        }

        p.A00 = (float)a[0, 0]; p.A01 = (float)a[0, 1]; p.A02 = (float)a[0, 2];
        p.A10 = (float)a[1, 0]; p.A11 = (float)a[1, 1]; p.A12 = (float)a[1, 2];
        p.A20 = (float)a[2, 0]; p.A21 = (float)a[2, 1]; p.A22 = (float)a[2, 2];
        p.B00 = (float)b[0, 0]; p.B01 = (float)b[0, 1]; p.B02 = (float)b[0, 2];
        p.B10 = (float)b[1, 0]; p.B11 = (float)b[1, 1]; p.B12 = (float)b[1, 2];
        p.B20 = (float)b[2, 0]; p.B21 = (float)b[2, 1]; p.B22 = (float)b[2, 2];

        float[] table = [];
        if (render.HueSatMap is { } map)
        {
            table = new float[map.EntryCount * 3];
            map.CopyTo(table);
        }

        var (cin, cout) = SplitCurve(render.ToneCurve);
        using var tableBuf = UploadArray(table);
        using var cinBuf = UploadArray(cin);
        using var coutBuf = UploadArray(cout);

        _render(cropW * cropH, stage3, dst, tableBuf.View, cinBuf.View, coutBuf.View, p);
        _acc.Synchronize();
    }

    private void ToneMapOnDevice(ArrayView<float> rgb, int pixelCount, (double Input, double Output)[]? toneCurve)
    {
        // HdrToneMapper treats a <2-point curve as absent.
        var (cin, cout) = SplitCurve(toneCurve is { Length: >= 2 } ? toneCurve : null);
        using var cinBuf = UploadArray(cin);
        using var coutBuf = UploadArray(cout);
        _toneMap(pixelCount, rgb, cinBuf.View, coutBuf.View, new PlanarParams { PixelCount = pixelCount });
        _acc.Synchronize();
    }

    private static (float[] In, float[] Out) SplitCurve((double Input, double Output)[]? curve)
    {
        if (curve is null || curve.Length == 0) return ([], []);
        var cin = new float[curve.Length];
        var cout = new float[curve.Length];
        for (int i = 0; i < curve.Length; i++)
        {
            cin[i] = (float)curve[i].Input;
            cout[i] = (float)curve[i].Output;
        }
        return (cin, cout);
    }

    // ── Transfers ─────────────────────────────────────────────────────────────

    private MemoryBuffer1D<T, Stride1D.Dense> Upload<T>(SimpleImage image) where T : unmanaged
    {
        long n = (long)image.Bounds.W * image.Bounds.H * image.Planes;
        var buf = _acc.Allocate1D<T>(n);
        buf.View.BaseView.CopyFromCPU((ReadOnlySpan<T>)image.Buffer.AsTypedSpan<T>()[..(int)n]);
        return buf;
    }

    /// <summary>Upload a host array; an empty array yields a zero-length view the kernels treat as "absent".</summary>
    private DeviceArray<T> UploadArray<T>(T[] data) where T : unmanaged
    {
        if (data.Length == 0) return new DeviceArray<T>(null, ArrayView<T>.Empty);
        var buf = _acc.Allocate1D<T>(data.Length);
        buf.View.CopyFromCPU(data);
        return new DeviceArray<T>(buf, buf.View);
    }

    private readonly struct DeviceArray<T>(MemoryBuffer1D<T, Stride1D.Dense>? buffer, ArrayView<T> view) : IDisposable
        where T : unmanaged
    {
        public ArrayView<T> View => view;
        public void Dispose() => buffer?.Dispose();
    }

    private SimpleImage Download(ArrayView<float> view, DngRect bounds, uint planes)
    {
        var image = new SimpleImage(bounds, planes, PixelType.Float32);
        view.CopyToCPU(image.Buffer.AsTypedSpan<float>()[..(int)view.Length]);
        return image;
    }

    private static void RequireFloat32(SimpleImage image, string paramName)
    {
        if (image.PixelType != PixelType.Float32)
            throw new ArgumentException($"Expected Float32 image, got {image.PixelType}.", paramName);
    }

    public void Dispose()
    {
        // Kernels and buffers are owned by the accelerator / disposed per call.
    }
}
