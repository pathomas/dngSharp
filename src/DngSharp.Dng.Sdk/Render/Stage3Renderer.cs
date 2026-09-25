using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DngSharp.Dng.Sdk.Color;
using DngSharp.Dng.Sdk.Imaging;
using DngSharp.Dng.Sdk.Imaging.Profile;
using DngSharp.Dng.Sdk.Math;
using DngSharp.Dng.Sdk.Metadata;
using DngSharp.Dng.Sdk.Pipeline;
using DngSharp.Dng.Sdk.Pixels;
using DngSharp.Dng.Sdk.Primitives;
using DngSharp.Dng.Sdk.Tasks;

namespace DngSharp.Dng.Sdk.Render;

/// <summary>Output color space for the rendered image.</summary>
public enum OutputColorSpace
{
    Srgb = 1,
    AdobeRgb = 2,
    ProPhotoRgb = 3,
    DisplayP3 = 4,
    Rec2020 = 5,
}

/// <summary>
/// Stage 3 → linear RGB color transform. Mirrors the per-pixel color
/// pipeline in <c>dng_render.cpp</c>.
///
/// <para>Input: a Stage-3 <see cref="DngImage"/> with <see cref="PixelType.Float32"/>
/// values in camera space (three interleaved planes). Values are in the range
/// [0, 1] for SDR; may exceed 1.0 for HDR profiles before tone mapping.</para>
///
/// <para>Output: a <see cref="SimpleImage"/> with <see cref="PixelType.Float32"/>
/// in a selectable <b>linear RGB output space</b> (sRGB by default). The caller
/// must apply gamma encoding and clamp/quantize for display output (see
/// <see cref="SrgbGamma"/> and <see cref="QuantizeToUInt8"/>).</para>
///
/// <para>Pipeline steps:
/// <list type="number">
///   <item><b>Camera → XYZ_D50.</b> Multiply each RGB camera-space triple by
///         <paramref name="cameraToXyzD50"/> (3×3 forward matrix).</item>
///   <item><b>Baseline exposure.</b> Scale by <c>2^baselineExposure</c>.</item>
///   <item><b>Optional tone curve.</b> Apply per-channel 1-D lookup (monotone
///         function). If <paramref name="toneCurve"/> is null, skip.</item>
///   <item><b>XYZ_D50 → linear output RGB.</b> Bradford D50→D65 CAT is applied
///         for D65 output spaces; ProPhoto remains in D50.</item>
/// </list>
/// </para>
///
/// <para>This class deliberately omits: <c>LookTable</c>/<c>RGBTables</c>,
/// <c>ProfileGainTableMap2</c>, and the HDR encode/decode brackets around
/// HueSatMap value-channel lookups. <c>ProfileHueSatMapData</c> (HSV
/// hue/saturation/value correction) IS implemented — see <see cref="HueSatMap"/>
/// and the <c>hueSatMap</c> parameter of <see cref="Render"/>. Those
/// remaining gaps land as follow-up work in the color-pipeline hardening
/// task.</para>
/// </summary>
public static class Stage3Renderer
{
    // ── Standard color-science constants ──────────────────────────────────────

    /// <summary>
    /// XYZ_D65 → linear-sRGB (IEC 61966-2-1 / Rec.709 primaries, D65 white).
    /// </summary>
    public static readonly DngMatrix XyzD65ToLinearSrgb = DngMatrix.Matrix3x3(
         3.2404542, -1.5371385, -0.4985314,
        -0.9692660,  1.8760108,  0.0415560,
         0.0556434, -0.2040259,  1.0572252);

    /// <summary>
    /// XYZ_D65 → linear Adobe RGB (1998) (D65 white).
    /// </summary>
    public static readonly DngMatrix XyzD65ToLinearAdobeRgb = DngMatrix.Matrix3x3(
         2.0413690, -0.5649464, -0.3446944,
        -0.9692660,  1.8760108,  0.0415560,
         0.0134474, -0.1183897,  1.0154096);

    /// <summary>
    /// XYZ_D50 → linear ProPhoto RGB / ROMM RGB (D50 white).
    /// </summary>
    public static readonly DngMatrix XyzD50ToLinearProPhoto = DngMatrix.Matrix3x3(
         1.3459433, -0.2556080, -0.0511118,
        -0.5445989,  1.5081673,  0.0205351,
         0.0000000,  0.0000000,  1.2118128);

    /// <summary>
    /// XYZ_D65 → linear Display P3 (D65 white).
    /// </summary>
    public static readonly DngMatrix XyzD65ToLinearDisplayP3 = DngMatrix.Matrix3x3(
         2.4934969, -0.9313836, -0.4027107,
        -0.8294890,  1.7626641,  0.0236247,
         0.0358458, -0.0761724,  0.9568845);

    /// <summary>
    /// XYZ_D65 → linear Rec.2020 / BT.2020 (D65 white).
    /// </summary>
    public static readonly DngMatrix XyzD65ToLinearRec2020 = DngMatrix.Matrix3x3(
         1.7166511, -0.3556708, -0.2533662,
        -0.6666843,  1.6164812,  0.0157685,
         0.0176399, -0.0427706,  0.9421031);

    /// <summary>
    /// Bradford CAT D50 → D65. Pre-computed once since it's a constant.
    /// </summary>
    public static readonly DngMatrix D50ToD65Cat = Bradford.MakeAdaptationMatrix(XyCoord.D50, XyCoord.D65);

    /// <summary>
    /// Combined matrix: Camera-space → XYZ_D50 → (D50→D65 Bradford) → linear sRGB.
    /// Multiplied lazily given a specific <c>cameraToXyzD50</c>.
    /// </summary>
    public static DngMatrix CameraToLinearSrgb(DngMatrix cameraToXyzD50) =>
        XyzD65ToLinearSrgb * D50ToD65Cat * cameraToXyzD50;

    /// <summary>
    /// Combined matrix: Camera-space → XYZ_D50 → selected output RGB space.
    /// </summary>
    public static DngMatrix CameraToOutputSpace(DngMatrix cameraToXyzD50, OutputColorSpace cs)
    {
        ArgumentNullException.ThrowIfNull(cameraToXyzD50);

        return cs switch
        {
            OutputColorSpace.Srgb => XyzD65ToLinearSrgb * D50ToD65Cat * cameraToXyzD50,
            OutputColorSpace.AdobeRgb => XyzD65ToLinearAdobeRgb * D50ToD65Cat * cameraToXyzD50,
            OutputColorSpace.ProPhotoRgb => XyzD50ToLinearProPhoto * cameraToXyzD50,
            OutputColorSpace.DisplayP3 => XyzD65ToLinearDisplayP3 * D50ToD65Cat * cameraToXyzD50,
            OutputColorSpace.Rec2020 => XyzD65ToLinearRec2020 * D50ToD65Cat * cameraToXyzD50,
            _ => XyzD65ToLinearSrgb * D50ToD65Cat * cameraToXyzD50,
        };
    }

    /// <summary>
    /// Fixed matrix: linear ProPhoto RGB (D50) → selected output RGB space.
    /// Independent of the camera; the inverse of <see cref="XyzD50ToLinearProPhoto"/>
    /// composed with the same D50→D65 CAT + primaries used by
    /// <see cref="CameraToOutputSpace"/>.
    /// </summary>
    public static DngMatrix ProPhotoToOutputSpace(OutputColorSpace cs)
    {
        var proPhotoToXyzD50 = DngMatrix.Invert(XyzD50ToLinearProPhoto);
        return cs switch
        {
            OutputColorSpace.Srgb => XyzD65ToLinearSrgb * D50ToD65Cat * proPhotoToXyzD50,
            OutputColorSpace.AdobeRgb => XyzD65ToLinearAdobeRgb * D50ToD65Cat * proPhotoToXyzD50,
            OutputColorSpace.ProPhotoRgb => DngMatrix.Identity3x3(),
            OutputColorSpace.DisplayP3 => XyzD65ToLinearDisplayP3 * D50ToD65Cat * proPhotoToXyzD50,
            OutputColorSpace.Rec2020 => XyzD65ToLinearRec2020 * D50ToD65Cat * proPhotoToXyzD50,
            _ => XyzD65ToLinearSrgb * D50ToD65Cat * proPhotoToXyzD50,
        };
    }

    /// <summary>
    /// Estimate the as-shot correlated color temperature from shared
    /// metadata (<see cref="DngShared.AsShotNeutral"/> or
    /// <see cref="DngShared.AsShotWhiteXy"/>), defaulting to 6500K when
    /// neither is present. Exposed so callers can resolve auxiliary
    /// per-illuminant data (e.g. <see cref="CameraColorMatrix.ResolveHueSatMap"/>)
    /// using the same estimate as <see cref="ResolveCameraToXyzD50"/>.
    /// </summary>
    public static double EstimateAsShotKelvin(DngShared shared)
    {
        ArgumentNullException.ThrowIfNull(shared);
        var negative = new DngNegative(new DngHost()) { Shared = shared };
        return negative.EstimateAsShotKelvin() ?? 6500.0;
    }

    /// <summary>
    /// Resolve the active camera→XYZ_D50 matrix from an embedded profile and
    /// file-level shared metadata. Falls back to identity only when the file
    /// has no usable profile data.
    ///
    /// <para>Implements the DNG spec 5.4.2 ForwardMatrix path in full:
    /// <c>cameraToXYZ_D50 = FM × D × inv(AB × CC)</c>
    /// where:
    /// <list type="bullet">
    ///   <item>FM = interpolated ForwardMatrix (from <see cref="CameraColorMatrix.BuildCameraToXyzD50"/>)</item>
    ///   <item>D = diag(1/n_r, 1/n_g, 1/n_b) white balance from <see cref="DngShared.AsShotNeutral"/></item>
    ///   <item>AB = AnalogBalance diagonal (from <see cref="DngShared.AnalogBalance"/>; defaults to identity)</item>
    ///   <item>CC = CameraCalibration (from the closest calibration illuminant; defaults to identity)</item>
    /// </list>
    /// </para>
    /// </summary>
    public static DngMatrix ResolveCameraToXyzD50(DngCameraProfile? profile, DngShared shared)
    {
        ArgumentNullException.ThrowIfNull(shared);

        if (profile is null || profile.Illuminants.Count == 0)
            return DngMatrix.Identity3x3();

        double asShotKelvin = EstimateAsShotKelvin(shared);

        // FM — ForwardMatrix at the as-shot CCT (interpolated between calibration illuminants).
        var fm = CameraColorMatrix.BuildCameraToXyzD50(profile, asShotKelvin);

        // White balance diagonal: D = diag(1/n_r, 1/n_g, 1/n_b).
        // Neutral values in (0, 1], max entry = 1.0; reciprocal = per-channel WB gain.
        //
        // AsShotNeutral and AsShotWhiteXY are mutually exclusive per spec 6.4;
        // when only AsShotWhiteXY is present we must derive an equivalent
        // camera-space neutral by projecting its xy chromaticity back through
        // the (as-yet-unbalanced) forward matrix — mirroring the inverse of
        // DngNegative.SolveAsShotNeutralKelvin's forward projection. Skipping
        // this (returning `fm` unscaled) leaves the raw camera-space channel
        // imbalance in the output, which for most Bayer sensors manifests as
        // a strong green cast plus apparent underexposure (green is usually
        // the strongest raw channel and red/blue are heavily under-scaled).
        DngVector? neutral = shared.AsShotNeutral;
        if (neutral is null && shared.AsShotWhiteXy is { } whiteXy)
        {
            var xyz = whiteXy.ToXyz();
            try
            {
                var invFm = DngMatrix.Invert(fm);
                var derived = invFm * xyz;
                double maxXy = derived.MaxEntry();
                if (maxXy > 0.0)
                {
                    derived.Scale(1.0 / maxXy);
                    neutral = derived;
                }
            }
            catch
            {
                // Degenerate forward matrix — fall through to the "no WB" path below.
            }
        }

        if (neutral is not { } resolvedNeutral || resolvedNeutral.Count < 3
            || resolvedNeutral[0] <= 0 || resolvedNeutral[1] <= 0 || resolvedNeutral[2] <= 0)
        {
            return fm; // No neutral available — FM without WB (rare; most DNGs have one or the other).
        }

        var wbDiag = DngMatrix.Diagonal3x3(
            1.0 / resolvedNeutral[0],
            1.0 / resolvedNeutral[1],
            1.0 / resolvedNeutral[2]);

        // inv(AB × CC): analog balance × camera calibration correction.
        // Use the calibration illuminant closest to the as-shot CCT.
        Span<double> kelvins = stackalloc double[profile.Illuminants.Count];
        for (int i = 0; i < profile.Illuminants.Count; i++)
            kelvins[i] = profile.Illuminants[i].Kelvin;
        var (loIdx, _) = ColorSpec.PickIlluminants(asShotKelvin, kelvins);
        var refIlluminant = profile.Illuminants[loIdx];

        var abCc = DngMatrix.Identity3x3();
        if (refIlluminant.CameraCalibration is { } cc)
        {
            var ab = shared.AnalogBalance is { } abVec && abVec.Count >= 3
                ? DngMatrix.Diagonal3x3(abVec[0], abVec[1], abVec[2])
                : DngMatrix.Identity3x3();
            abCc = ab * cc;
        }

        DngMatrix invAbCc;
        try
        {
            invAbCc = DngMatrix.Invert(abCc);
        }
        catch
        {
            invAbCc = DngMatrix.Identity3x3(); // degenerate CC — use identity
        }

        // Final: FM × D × inv(AB × CC)
        return fm * wbDiag * invAbCc;
    }

    // ── Rendering ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Transform a Stage-3 camera-space image to a linear output RGB space.
    /// </summary>
    /// <param name="stage3">Stage-3 Float32 camera-space image.</param>
    /// <param name="cameraToXyzD50">Camera→XYZ_D50 forward matrix (3×3).</param>
    /// <param name="baselineExposure">Baseline exposure in stops (0.0 = no change).
    /// Applied as <c>scale = 2^baselineExposure</c> after matrix multiply.</param>
    /// <param name="toneCurve">Optional 1-D tone curve applied in XYZ_D50 space
    /// (per-channel or single-channel; null = identity).</param>
    /// <param name="host">Optional host for tile size and cancellation.</param>
    /// <param name="colorSpace">Output color space. Defaults to sRGB.</param>
    /// <param name="hueSatMap">Optional per-camera HSV hue/saturation/value
    /// correction table (<c>ProfileHueSatMapData</c>), already interpolated
    /// for the as-shot CCT via <see cref="CameraColorMatrix.ResolveHueSatMap"/>.
    /// Applied in linear ProPhoto-RGB reference space, before exposure and
    /// tone curve — matching native pipeline ordering. Forces the scalar
    /// (non-SIMD) path when present.</param>
    /// <returns>Float32 three-plane linear RGB image in the selected output space (unbounded range; caller clips).</returns>
    public static SimpleImage Render(
        DngImage stage3,
        DngMatrix cameraToXyzD50,
        double baselineExposure = 0.0,
        Func<double, double>? toneCurve = null,
        DngHost? host = null,
        OutputColorSpace colorSpace = OutputColorSpace.Srgb,
        HueSatMap? hueSatMap = null)
    {
        ArgumentNullException.ThrowIfNull(stage3);
        ArgumentNullException.ThrowIfNull(cameraToXyzD50);

        double exposureScale = System.Math.Pow(2.0, baselineExposure);
        var output = new SimpleImage(stage3.Bounds, 3, PixelType.Float32);

        if (hueSatMap is null)
        {
            var combined = CameraToOutputSpace(cameraToXyzD50, colorSpace);
            var task = new RenderTask(stage3, output, combined, exposureScale, toneCurve,
                                      host?.MaxTileEdgePixels ?? 256);
            AreaTaskRunner.Run(task, stage3.Bounds, host);
        }
        else
        {
            // Split camera→output into camera→ProPhotoRGB (apply HueSatMap
            // here, matching the native reference-RGB stage) and a fixed
            // ProPhotoRGB→output matrix. Mathematically equivalent to the
            // single combined matrix above when no HueSatMap is present.
            var cameraToProPhoto = CameraToOutputSpace(cameraToXyzD50, OutputColorSpace.ProPhotoRgb);
            var proPhotoToOutput = ProPhotoToOutputSpace(colorSpace);
            var task = new HueSatMapRenderTask(stage3, output, cameraToProPhoto, proPhotoToOutput,
                                                hueSatMap, exposureScale, toneCurve,
                                                host?.MaxTileEdgePixels ?? 256);
            AreaTaskRunner.Run(task, stage3.Bounds, host);
        }

        return output;
    }

    // ── sRGB gamma & quantize helpers ──────────────────────────────────────────

    /// <summary>
    /// Apply the sRGB piecewise gamma transfer function to a linear value.
    /// Input range: [0, 1]; output range: [0, 1].
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double SrgbGamma(double linear)
    {
        if (linear <= 0.0031308)
            return 12.92 * linear;
        return 1.055 * System.Math.Pow(linear, 1.0 / 2.4) - 0.055;
    }

    /// <summary>
    /// Clamp, apply sRGB gamma, and quantize a float pixel buffer to a UInt8
    /// interleaved byte buffer suitable for JPEG / WebP encoding.
    ///
    /// <para>The input buffer is modified in-place (float values are consumed
    /// and the same backing array is reused via a typed span). The returned
    /// <see cref="Span{T}"/> is a view of <paramref name="dest"/>.</para>
    /// </summary>
    /// <param name="src">Float32 linear-sRGB SimpleImage.</param>
    /// <param name="dest">Pre-allocated byte buffer (must be width×height×3 bytes).</param>
    /// <param name="host">Optional host for cancellation and thread count.</param>
    /// <returns>Span over <paramref name="dest"/> filled with gamma-encoded UInt8 pixels.</returns>
    public static Span<byte> GammaAndQuantize(SimpleImage src, byte[] dest, DngHost? host = null)
    {
        ArgumentNullException.ThrowIfNull(src);
        ArgumentNullException.ThrowIfNull(dest);

        int total = (int)(src.Bounds.W * src.Bounds.H) * 3;
        if (dest.Length < total)
            throw new ArgumentException($"dest too small: need {total}, got {dest.Length}");

        var srcTile = src.GetTile(src.Bounds);
        int w = (int)src.Bounds.W;
        int h = (int)src.Bounds.H;
        uint planes = src.Planes;
        if (planes != 3)
            throw new ArgumentException($"GammaAndQuantize expects 3 planes, got {planes}");

        // Source storage is planar; output is interleaved RGB bytes. Walk one
        // row per iteration: for each plane read the contiguous (or strided)
        // float row and scatter to dest[(row*w+col)*3+plane]. Same per-sample
        // math as before, so output bytes are identical.
        ParallelWork.For(0, h, host?.Sniffer, host?.MaxThreads, r =>
        {
            var srcBytes = srcTile.AsByteSpan();
            int colStep = (int)srcTile.ColStep;
            int destRow = r * w * 3;
            for (uint p = 0; p < planes; p++)
            {
                int off = (int)(srcTile.OffsetBytes(src.Bounds.T + r, src.Bounds.L, p) / 4);
                var floats = MemoryMarshal.Cast<byte, float>(srcBytes);
                int d = destRow + (int)p;
                for (int c = 0; c < w; c++, d += 3)
                {
                    double g = SrgbGamma(System.Math.Clamp((double)floats[off + c * colStep], 0.0, 1.0));
                    dest[d] = (byte)(g * 255.0 + 0.5);
                }
            }
        });
        return dest.AsSpan(0, total);
    }

    // ── Private task ───────────────────────────────────────────────────────────

    private sealed class RenderTask(
        DngImage src,
        SimpleImage dst,
        DngMatrix combined,    // camera → linear output RGB (3×3)
        double exposureScale,
        Func<double, double>? toneCurve,
        int tileEdge) : IAreaTask
    {
        public DngPoint MaxTileSize(DngPoint imageSize) => new(tileEdge, tileEdge);

        public void Process(int threadIndex, DngRect tile)
        {
            var srcTile = src.GetTile(tile);
            var dstTile = dst.GetTile(tile);
            var srcBytes = srcTile.Memory.Span;
            var dstBytes = dstTile.Memory.Span;

            int count = (int)(tile.R - tile.L);

            // Fast path: no tone curve, and both tiles are planar (ColStep==1)
            // so each plane's row is one contiguous float span — feed R/G/B
            // rows straight into <see cref="Vector{Single}"/> MADs with no
            // de-interleave buffers. Falls back to the scalar per-pixel loop
            // (below) when a tone curve is present (non-vectorizable delegate
            // per sample) or a buffer isn't planar.
            if (toneCurve is null && Vector.IsHardwareAccelerated
                && srcTile.HasContiguousRows && dstTile.HasContiguousRows)
            {
                float m00 = (float)combined[0, 0], m01 = (float)combined[0, 1], m02 = (float)combined[0, 2];
                float m10 = (float)combined[1, 0], m11 = (float)combined[1, 1], m12 = (float)combined[1, 2];
                float m20 = (float)combined[2, 0], m21 = (float)combined[2, 1], m22 = (float)combined[2, 2];
                float expF = (float)exposureScale;

                var srcF = MemoryMarshal.Cast<byte, float>(srcBytes);
                var dstF = MemoryMarshal.Cast<byte, float>(dstBytes);

                for (int row = tile.T; row < tile.B; row++)
                {
                    int sr = (int)(srcTile.OffsetBytes(row, tile.L, 0) / 4);
                    int sg = (int)(srcTile.OffsetBytes(row, tile.L, 1) / 4);
                    int sb = (int)(srcTile.OffsetBytes(row, tile.L, 2) / 4);
                    int dr = (int)(dstTile.OffsetBytes(row, tile.L, 0) / 4);
                    int dg = (int)(dstTile.OffsetBytes(row, tile.L, 1) / 4);
                    int db = (int)(dstTile.OffsetBytes(row, tile.L, 2) / 4);
                    ProcessRowSimd(
                        srcF.Slice(sr, count), srcF.Slice(sg, count), srcF.Slice(sb, count),
                        dstF.Slice(dr, count), dstF.Slice(dg, count), dstF.Slice(db, count),
                        m00, m01, m02, m10, m11, m12, m20, m21, m22, expF);
                }

                return;
            }

            // Hoist matrix cells out of the pixel loop (the indexer is a
            // bounds-checked call per access).
            double c00 = combined[0, 0], c01 = combined[0, 1], c02 = combined[0, 2];
            double c10 = combined[1, 0], c11 = combined[1, 1], c12 = combined[1, 2];
            double c20 = combined[2, 0], c21 = combined[2, 1], c22 = combined[2, 2];

            // Planar scalar path: walk each row as six contiguous float spans
            // (three in, three out) instead of computing six byte offsets per
            // pixel. Same arithmetic as the general path below.
            if (srcTile.HasContiguousRows && dstTile.HasContiguousRows)
            {
                var srcF = MemoryMarshal.Cast<byte, float>(srcBytes);
                var dstF = MemoryMarshal.Cast<byte, float>(dstBytes);

                for (int row = tile.T; row < tile.B; row++)
                {
                    var rIn = srcF.Slice((int)(srcTile.OffsetBytes(row, tile.L, 0) / 4), count);
                    var gIn = srcF.Slice((int)(srcTile.OffsetBytes(row, tile.L, 1) / 4), count);
                    var bIn = srcF.Slice((int)(srcTile.OffsetBytes(row, tile.L, 2) / 4), count);
                    var rOut = dstF.Slice((int)(dstTile.OffsetBytes(row, tile.L, 0) / 4), count);
                    var gOut = dstF.Slice((int)(dstTile.OffsetBytes(row, tile.L, 1) / 4), count);
                    var bOut = dstF.Slice((int)(dstTile.OffsetBytes(row, tile.L, 2) / 4), count);

                    for (int k = 0; k < count; k++)
                    {
                        double r = rIn[k], g = gIn[k], b = bIn[k];
                        double or = (c00 * r + c01 * g + c02 * b) * exposureScale;
                        double og = (c10 * r + c11 * g + c12 * b) * exposureScale;
                        double ob = (c20 * r + c21 * g + c22 * b) * exposureScale;
                        if (toneCurve is not null)
                        {
                            or = toneCurve(or);
                            og = toneCurve(og);
                            ob = toneCurve(ob);
                        }
                        rOut[k] = (float)or;
                        gOut[k] = (float)og;
                        bOut[k] = (float)ob;
                    }
                }

                return;
            }

            for (int row = tile.T; row < tile.B; row++)
            {
                for (int col = tile.L; col < tile.R; col++)
                {
                    // Read 3-channel camera-space pixel.
                    long off0 = srcTile.OffsetBytes(row, col, 0);
                    long off1 = srcTile.OffsetBytes(row, col, 1);
                    long off2 = srcTile.OffsetBytes(row, col, 2);

                    double r = BinaryPrimitives.ReadSingleLittleEndian(srcBytes.Slice((int)off0, 4));
                    double g = BinaryPrimitives.ReadSingleLittleEndian(srcBytes.Slice((int)off1, 4));
                    double b = BinaryPrimitives.ReadSingleLittleEndian(srcBytes.Slice((int)off2, 4));

                    // Matrix multiply: camera → linear output RGB.
                    double or = c00 * r + c01 * g + c02 * b;
                    double og = c10 * r + c11 * g + c12 * b;
                    double ob = c20 * r + c21 * g + c22 * b;

                    // Baseline exposure.
                    or *= exposureScale;
                    og *= exposureScale;
                    ob *= exposureScale;

                    // Optional tone curve (applies in XYZ_D50 space proxy — simplified).
                    if (toneCurve is not null)
                    {
                        or = toneCurve(or);
                        og = toneCurve(og);
                        ob = toneCurve(ob);
                    }

                    // Write Float32 output.
                    long doff0 = dstTile.OffsetBytes(row, col, 0);
                    long doff1 = dstTile.OffsetBytes(row, col, 1);
                    long doff2 = dstTile.OffsetBytes(row, col, 2);
                    BinaryPrimitives.WriteSingleLittleEndian(dstBytes.Slice((int)doff0, 4), (float)or);
                    BinaryPrimitives.WriteSingleLittleEndian(dstBytes.Slice((int)doff1, 4), (float)og);
                    BinaryPrimitives.WriteSingleLittleEndian(dstBytes.Slice((int)doff2, 4), (float)ob);
                }
            }
        }

        /// <summary>
        /// Vectorized 3×3 matrix multiply + exposure scale over one planar row:
        /// three contiguous input planes → three contiguous output planes.
        /// Arithmetic order matches the scalar path's float evaluation
        /// (<c>(r·m0 + g·m1 + b·m2)·exp</c>) so results are bit-identical to
        /// the previous interleaved implementation.
        /// </summary>
        private static void ProcessRowSimd(
            ReadOnlySpan<float> rIn, ReadOnlySpan<float> gIn, ReadOnlySpan<float> bIn,
            Span<float> rOut, Span<float> gOut, Span<float> bOut,
            float m00, float m01, float m02,
            float m10, float m11, float m12,
            float m20, float m21, float m22,
            float expScale)
        {
            int count = rIn.Length;
            int vw = Vector<float>.Count;

            int k = 0;
            for (; k + vw <= count; k += vw)
            {
                var rv = new Vector<float>(rIn.Slice(k, vw));
                var gv = new Vector<float>(gIn.Slice(k, vw));
                var bv = new Vector<float>(bIn.Slice(k, vw));

                ((rv * m00 + gv * m01 + bv * m02) * expScale).CopyTo(rOut.Slice(k, vw));
                ((rv * m10 + gv * m11 + bv * m12) * expScale).CopyTo(gOut.Slice(k, vw));
                ((rv * m20 + gv * m21 + bv * m22) * expScale).CopyTo(bOut.Slice(k, vw));
            }
            for (; k < count; k++)
            {
                float r = rIn[k], g = gIn[k], b = bIn[k];
                rOut[k] = (r * m00 + g * m01 + b * m02) * expScale;
                gOut[k] = (r * m10 + g * m11 + b * m12) * expScale;
                bOut[k] = (r * m20 + g * m21 + b * m22) * expScale;
            }
        }
    }

    /// <summary>
    /// Scalar render path used when a <see cref="HueSatMap"/> is present:
    /// camera → ProPhotoRGB (apply HSV table) → exposure → tone curve →
    /// output space. See <see cref="Render"/> remarks for pipeline ordering.
    /// </summary>
    private sealed class HueSatMapRenderTask(
        DngImage src,
        SimpleImage dst,
        DngMatrix cameraToProPhoto,
        DngMatrix proPhotoToOutput,
        HueSatMap hueSatMap,
        double exposureScale,
        Func<double, double>? toneCurve,
        int tileEdge) : IAreaTask
    {
        public DngPoint MaxTileSize(DngPoint imageSize) => new(tileEdge, tileEdge);

        public void Process(int threadIndex, DngRect tile)
        {
            var srcTile = src.GetTile(tile);
            var dstTile = dst.GetTile(tile);
            var srcBytes = srcTile.Memory.Span;
            var dstBytes = dstTile.Memory.Span;
            int count = (int)(tile.R - tile.L);

            // Planar fast path — see RenderTask: six contiguous row spans
            // instead of six byte offsets per pixel.
            if (srcTile.HasContiguousRows && dstTile.HasContiguousRows)
            {
                var srcF = MemoryMarshal.Cast<byte, float>(srcBytes);
                var dstF = MemoryMarshal.Cast<byte, float>(dstBytes);

                for (int row = tile.T; row < tile.B; row++)
                {
                    var rIn = srcF.Slice((int)(srcTile.OffsetBytes(row, tile.L, 0) / 4), count);
                    var gIn = srcF.Slice((int)(srcTile.OffsetBytes(row, tile.L, 1) / 4), count);
                    var bIn = srcF.Slice((int)(srcTile.OffsetBytes(row, tile.L, 2) / 4), count);
                    var rOut = dstF.Slice((int)(dstTile.OffsetBytes(row, tile.L, 0) / 4), count);
                    var gOut = dstF.Slice((int)(dstTile.OffsetBytes(row, tile.L, 1) / 4), count);
                    var bOut = dstF.Slice((int)(dstTile.OffsetBytes(row, tile.L, 2) / 4), count);

                    for (int k = 0; k < count; k++)
                    {
                        Transform(rIn[k], gIn[k], bIn[k], out double or, out double og, out double ob);
                        rOut[k] = (float)or;
                        gOut[k] = (float)og;
                        bOut[k] = (float)ob;
                    }
                }

                return;
            }

            for (int row = tile.T; row < tile.B; row++)
            {
                for (int col = tile.L; col < tile.R; col++)
                {
                    long off0 = srcTile.OffsetBytes(row, col, 0);
                    long off1 = srcTile.OffsetBytes(row, col, 1);
                    long off2 = srcTile.OffsetBytes(row, col, 2);

                    double cr = BinaryPrimitives.ReadSingleLittleEndian(srcBytes.Slice((int)off0, 4));
                    double cg = BinaryPrimitives.ReadSingleLittleEndian(srcBytes.Slice((int)off1, 4));
                    double cb = BinaryPrimitives.ReadSingleLittleEndian(srcBytes.Slice((int)off2, 4));

                    Transform(cr, cg, cb, out double or, out double og, out double ob);

                    long doff0 = dstTile.OffsetBytes(row, col, 0);
                    long doff1 = dstTile.OffsetBytes(row, col, 1);
                    long doff2 = dstTile.OffsetBytes(row, col, 2);
                    BinaryPrimitives.WriteSingleLittleEndian(dstBytes.Slice((int)doff0, 4), (float)or);
                    BinaryPrimitives.WriteSingleLittleEndian(dstBytes.Slice((int)doff1, 4), (float)og);
                    BinaryPrimitives.WriteSingleLittleEndian(dstBytes.Slice((int)doff2, 4), (float)ob);
                }
            }
        }

        // Matrix cells hoisted once per task; the DngMatrix indexer is a
        // bounds-checked call that would otherwise run 18× per pixel.
        private readonly double _a00 = cameraToProPhoto[0, 0], _a01 = cameraToProPhoto[0, 1], _a02 = cameraToProPhoto[0, 2];
        private readonly double _a10 = cameraToProPhoto[1, 0], _a11 = cameraToProPhoto[1, 1], _a12 = cameraToProPhoto[1, 2];
        private readonly double _a20 = cameraToProPhoto[2, 0], _a21 = cameraToProPhoto[2, 1], _a22 = cameraToProPhoto[2, 2];
        private readonly double _b00 = proPhotoToOutput[0, 0], _b01 = proPhotoToOutput[0, 1], _b02 = proPhotoToOutput[0, 2];
        private readonly double _b10 = proPhotoToOutput[1, 0], _b11 = proPhotoToOutput[1, 1], _b12 = proPhotoToOutput[1, 2];
        private readonly double _b20 = proPhotoToOutput[2, 0], _b21 = proPhotoToOutput[2, 1], _b22 = proPhotoToOutput[2, 2];

        /// <summary>
        /// One camera-space pixel → output space: camera → linear ProPhoto RGB
        /// (reference space) → HSV hue/sat/value correction (spec 6.3.7) →
        /// output space → baseline exposure → optional tone curve.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Transform(double cr, double cg, double cb, out double or, out double og, out double ob)
        {
            double pr = _a00 * cr + _a01 * cg + _a02 * cb;
            double pg = _a10 * cr + _a11 * cg + _a12 * cb;
            double pb = _a20 * cr + _a21 * cg + _a22 * cb;

            hueSatMap.Apply(ref pr, ref pg, ref pb);

            or = _b00 * pr + _b01 * pg + _b02 * pb;
            og = _b10 * pr + _b11 * pg + _b12 * pb;
            ob = _b20 * pr + _b21 * pg + _b22 * pb;

            or *= exposureScale;
            og *= exposureScale;
            ob *= exposureScale;

            if (toneCurve is not null)
            {
                or = toneCurve(or);
                og = toneCurve(og);
                ob = toneCurve(ob);
            }
        }
    }
}
