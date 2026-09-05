using DngSharp.Dng.Sdk.Imaging;
using DngSharp.Dng.Sdk.Pixels;
using DngSharp.Dng.Sdk.Primitives;
using DngSharp.Dng.Sdk.Tiff;

namespace DngSharp.Dng.Sdk.Imaging.Opcodes;

/// <summary>
/// Applies a <see cref="WarpRectilinearParams"/> lens-correction warp to a
/// Float32 image via inverse-mapped bicubic resample. Mirrors
/// <c>dng_filter_warp</c> + <c>dng_resample_bicubic</c> in the native SDK
/// (see <c>dng_lens_correction.cpp</c>/<c>dng_resample.cpp</c>).
///
/// <para>For each destination (corrected) pixel, computes the corresponding
/// uncorrected source position via the warp math in
/// <see cref="WarpRectilinearParams"/>, then samples the source image with a
/// 4×4 (radius 2) separable bicubic kernel (Catmull-Rom-family, A = -0.75),
/// using a precomputed 32×32-subsample weight table for speed (matches the
/// native <c>kResampleSubsampleCount2D</c> = 32 convention).</para>
///
/// <para>Runs the whole image as a single "tile" (no tiled area-task
/// infrastructure in this port yet), so the 4×4 sample window is clamped to
/// stay within the full source image bounds — equivalent to native's
/// per-tile clamp when the tile is the entire image.</para>
/// </summary>
public static class LensWarpFilter
{
    private const int Radius = 2;      // dng_resample_bicubic::Extent() == 2.0
    private const int Width = Radius * 2; // 4-tap separable kernel
    private const int SubsampleBits = 5;
    private const int SubsampleCount = 1 << SubsampleBits; // 32, matches kResampleSubsampleCount2D
    private const double BicubicA = -0.75;

    // weights[fracV, fracH, i, j] flattened as [(fracV*SubsampleCount+fracH)*Width*Width + i*Width + j]
    private static readonly float[] Weights = BuildWeights();

    private static double BicubicKernel(double x)
    {
        x = System.Math.Abs(x);
        if (x >= 2.0) return 0.0;
        if (x >= 1.0) return ((BicubicA * x - 5.0 * BicubicA) * x + 8.0 * BicubicA) * x - 4.0 * BicubicA;
        return ((BicubicA + 2.0) * x - (BicubicA + 3.0)) * x * x + 1.0;
    }

    private static float[] BuildWeights()
    {
        var table = new float[SubsampleCount * SubsampleCount * Width * Width];

        for (int y = 0; y < SubsampleCount; y++)
        {
            double yFract = y * (1.0 / SubsampleCount);

            for (int x = 0; x < SubsampleCount; x++)
            {
                double xFract = x * (1.0 / SubsampleCount);

                int baseIndex = (y * SubsampleCount + x) * Width * Width;
                int index = 0;
                double total = 0.0;

                for (int i = 0; i < Width; i++)
                {
                    int yInt = i - Radius + 1;
                    double yPos = yInt - yFract;

                    for (int j = 0; j < Width; j++)
                    {
                        int xInt = j - Radius + 1;
                        double xPos = xInt - xFract;

                        double w = BicubicKernel(xPos) * BicubicKernel(yPos);
                        table[baseIndex + index] = (float)w;
                        total += w;
                        index++;
                    }
                }

                float scale = (float)(1.0 / total);
                for (int k = 0; k < Width * Width; k++)
                    table[baseIndex + k] *= scale;
            }
        }

        return table;
    }

    /// <summary>
    /// Warp <paramref name="src"/> (a Float32 <see cref="SimpleImage"/>) using
    /// <paramref name="warpParams"/>. Returns a new image of the same bounds
    /// and plane count. <paramref name="pixelAspectRatio"/> defaults to 1.0
    /// (square pixels) — pass <c>negative.PixelAspectRatio</c> when the port
    /// grows that field.
    /// </summary>
    public static SimpleImage Apply(SimpleImage src, IWarpRectilinearParams warpParams, double pixelAspectRatio = 1.0)
    {
        ArgumentNullException.ThrowIfNull(src);
        ArgumentNullException.ThrowIfNull(warpParams);
        if (src.PixelType != PixelType.Float32)
            throw new ArgumentException("LensWarpFilter.Apply requires a Float32 image", nameof(src));

        warpParams.PropagateToAllPlanes((int)src.Planes);

        if (warpParams.IsNopAll())
        {
            // Fast path: nothing to warp. Still return a copy for consistent
            // ownership semantics with the non-NOP path.
            var copy = new SimpleImage(src.Bounds, src.Planes, src.PixelType);
            src.Buffer.AsByteSpan()[..copy.Buffer.AsByteSpan().Length]
                .CopyTo(copy.Buffer.AsByteSpan());
            return copy;
        }

        var bounds = src.Bounds;
        var dst = new SimpleImage(bounds, src.Planes, src.PixelType);

        double pixelScaleV = 1.0 / pixelAspectRatio;
        double pixelScaleVInv = pixelAspectRatio;

        double centerH = Lerp(bounds.L, bounds.R, warpParams.Center.H);
        double centerV = Lerp(bounds.T, bounds.B, warpParams.Center.V);

        // "Squared" bounds account for non-square pixels; with the default
        // pixelAspectRatio == 1.0 this equals the image bounds unchanged.
        double squareBottom = bounds.T + pixelScaleV * (bounds.B - bounds.T);
        double squareCenterV = Lerp(bounds.T, squareBottom, warpParams.Center.V);
        double squareCenterH = centerH;

        double normRadius = MaxDistanceToRectCorners(squareCenterV, squareCenterH, bounds.T, bounds.L, squareBottom, bounds.R);
        double invNormRadius = 1.0 / normRadius;

        int hMin = bounds.L;
        int hMax = bounds.R - Width - 1;
        int vMin = bounds.T;
        int vMax = bounds.B - Width - 1;

        if (hMax < hMin || vMax < vMin)
            throw new InvalidOperationException("LensWarpFilter: image too small for the bicubic resample window");

        var srcBuf = src.Buffer;
        var dstBuf = dst.Buffer;

        for (uint plane = 0; plane < src.Planes; plane++)
        {
            bool isTanNop = warpParams.IsTanNop((int)plane);
            bool isRadNop = warpParams.IsRadNop((int)plane);

            var dstFloats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(dstBuf.AsByteSpan());
            var srcFloats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(srcBuf.AsByteSpan());

            for (int dstRow = bounds.T; dstRow < bounds.B; dstRow++)
            {
                for (int dstCol = bounds.L; dstCol < bounds.R; dstCol++)
                {
                    (double srcV, double srcH) = GetSrcPixelPosition(
                        dstRow, dstCol, plane, warpParams, isRadNop, isTanNop,
                        centerH, centerV, invNormRadius, normRadius,
                        pixelScaleV, pixelScaleVInv);

                    // Clamp to the full source image area (dng_filter_warp's
                    // srcImageArea clamp, applied before int/frac decomposition).
                    srcH = double.Clamp(srcH, bounds.L, bounds.R - 1.0);
                    srcV = double.Clamp(srcV, bounds.T, bounds.B - 1.0);

                    int sIntV = (int)System.Math.Floor(srcV);
                    int sIntH = (int)System.Math.Floor(srcH);

                    int sFracV = (int)((srcV - sIntV) * SubsampleCount);
                    int sFracH = (int)((srcH - sIntH) * SubsampleCount);

                    // Add resample offset (1 - radius).
                    sIntV += 1 - Radius;
                    sIntH += 1 - Radius;

                    if (sIntH < hMin) { sIntH = hMin; sFracH = 0; }
                    else if (sIntH > hMax) { sIntH = hMax; sFracH = 0; }

                    if (sIntV < vMin) { sIntV = vMin; sFracV = 0; }
                    else if (sIntV > vMax) { sIntV = vMax; sFracV = 0; }

                    int weightBase = (sFracV * SubsampleCount + sFracH) * Width * Width;

                    long srcRowStepSamples = srcBuf.RowStep;
                    long srcBase = srcBuf.OffsetBytes(sIntV, sIntH, plane) / sizeof(float);

                    float total = 0f;
                    int wIdx = weightBase;
                    long rowBase = srcBase;

                    for (int i = 0; i < Width; i++)
                    {
                        for (int j = 0; j < Width; j++)
                        {
                            total += Weights[wIdx + j] * srcFloats[(int)(rowBase + (long)j * srcBuf.ColStep)];
                        }
                        wIdx += Width;
                        rowBase += srcRowStepSamples;
                    }

                    long dstOffsetSamples = dstBuf.OffsetBytes(dstRow, dstCol, plane) / sizeof(float);
                    dstFloats[(int)dstOffsetSamples] = float.Clamp(total, float.MinValue, float.MaxValue);
                }
            }
        }

        return dst;
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private static double MaxDistanceToRectCorners(double cv, double ch, double t, double l, double b, double r)
    {
        double d1 = Distance(cv, ch, t, l);
        double d2 = Distance(cv, ch, t, r);
        double d3 = Distance(cv, ch, b, l);
        double d4 = Distance(cv, ch, b, r);
        return System.Math.Max(System.Math.Max(d1, d2), System.Math.Max(d3, d4));
    }

    private static double Distance(double v0, double h0, double v1, double h1)
    {
        double dv = v1 - v0;
        double dh = h1 - h0;
        return System.Math.Sqrt(dv * dv + dh * dh);
    }

    private static (double SrcV, double SrcH) GetSrcPixelPosition(
        int dstRow, int dstCol, uint plane, IWarpRectilinearParams p,
        bool isRadNop, bool isTanNop,
        double centerH, double centerV, double invNormRadius, double normRadius,
        double pixelScaleV, double pixelScaleVInv)
    {
        double diffV = dstRow - centerV;
        double diffH = dstCol - centerH;

        double diffNormV = diffV * invNormRadius;
        double diffNormH = diffH * invNormRadius;

        double diffNormScaledV = diffNormV * pixelScaleV;
        double diffNormScaledH = diffNormH;

        double diffNormSqrV = diffNormScaledV * diffNormScaledV;
        double diffNormSqrH = diffNormScaledH * diffNormScaledH;

        double rr = System.Math.Min(diffNormSqrV + diffNormSqrH, 1.0);

        double dSrcH, dSrcV;

        if (isTanNop)
        {
            double ratio = p.RadialRatio((int)plane, rr);
            dSrcH = diffH * ratio;
            dSrcV = diffV * ratio;
        }
        else if (isRadNop)
        {
            var (tanH, tanV) = p.EvaluateTangential((int)plane, rr, diffNormScaledH, diffNormScaledV, diffNormSqrH, diffNormSqrV);
            dSrcH = diffH + normRadius * tanH;
            dSrcV = diffV + normRadius * tanV * pixelScaleVInv;
        }
        else
        {
            double ratio = p.RadialRatio((int)plane, rr);
            var (tanH, tanV) = p.EvaluateTangential((int)plane, rr, diffNormScaledH, diffNormScaledV, diffNormSqrH, diffNormSqrV);
            dSrcH = normRadius * (diffNormH * ratio + tanH);
            dSrcV = normRadius * (diffNormV * ratio + tanV * pixelScaleVInv);
        }

        return (centerV + dSrcV, centerH + dSrcH);
    }
}
