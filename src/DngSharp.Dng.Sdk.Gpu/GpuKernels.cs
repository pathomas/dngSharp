using ILGPU;
using ILGPU.Algorithms;

namespace DngSharp.Dng.Sdk.Gpu;

/// <summary>Per-launch parameters for <see cref="GpuKernels.Linearize"/>.</summary>
public struct LinearizeParams
{
    public int Width;
    public int Height;
    public int Planes;
    public int RepeatRows;
    public int RepeatCols;
    /// <summary>1 when <see cref="RepeatRows"/>/<see cref="RepeatCols"/> tile a black-level pattern; 0 → per-plane black.</summary>
    public int HasRepeat;
}

/// <summary>Per-launch parameters for <see cref="GpuKernels.Demosaic"/>.</summary>
public struct DemosaicParams
{
    public int Width;
    public int Height;
    /// <summary>CFA colour (0=R, 1=G, 2=B) at pattern cell (row&amp;1, col&amp;1).</summary>
    public int P00, P01, P10, P11;
}

/// <summary>Per-launch parameters for <see cref="GpuKernels.Render"/>.</summary>
public struct RenderParams
{
    public int SrcWidth;
    public int SrcHeight;
    public int CropLeft;
    public int CropTop;
    public int CropWidth;
    public int CropHeight;

    /// <summary>Camera → reference (ProPhoto when a HueSatMap is present; otherwise the full camera → output matrix).</summary>
    public float A00, A01, A02, A10, A11, A12, A20, A21, A22;
    /// <summary>Reference → output (only applied when <see cref="HasHueSatMap"/> is 1).</summary>
    public float B00, B01, B02, B10, B11, B12, B20, B21, B22;

    public float ExposureScale;

    public int HasHueSatMap;
    public int HueDivisions;
    public int SatDivisions;
    public int ValDivisions;
}

/// <summary>Per-launch parameters for <see cref="GpuKernels.ToneMap"/> and <see cref="GpuKernels.GammaQuantize"/>.</summary>
public struct PlanarParams
{
    /// <summary>Pixels per plane.</summary>
    public int PixelCount;
}

/// <summary>
/// ILGPU kernel bodies. Each is a straight float32 transliteration of the
/// corresponding CPU kernel (<c>Stage2Builder</c>, <c>DemosaicBilinear</c>,
/// <c>Stage3Renderer</c>, <c>HueSatMap</c>, <c>HdrToneMapper</c>) so results
/// agree to within float rounding; see <c>docs/perf/phase10-gpu.md</c> for
/// the measured tolerance. All buffers are planar (SoA) — the same layout
/// <c>SimpleImage</c> uses on the host, so uploads/downloads are memcpys.
/// </summary>
public static class GpuKernels
{
    // ── Stage 1 → Stage 2 ─────────────────────────────────────────────────────

    /// <summary>
    /// <c>((lut[sample]) - (black + deltaV[row] + deltaH[col])) * scale</c>,
    /// clipped above 1.0 (sub-zero preserved). One thread per sample over all planes.
    /// </summary>
    public static void Linearize(
        Index1D i,
        ArrayView<ushort> src,
        ArrayView<float> dst,
        ArrayView<float> lut,       // length 0 → none
        ArrayView<float> blacks,    // per pattern cell (HasRepeat) or per plane
        ArrayView<float> scales,    // per plane
        ArrayView<float> deltaV,    // length 0 → none
        ArrayView<float> deltaH,    // length 0 → none
        LinearizeParams p)
    {
        int planeSize = p.Width * p.Height;
        int plane = i / planeSize;
        int rem = i - plane * planeSize;
        int row = rem / p.Width;
        int col = rem - row * p.Width;

        float sample = src[i];
        if (lut.Length > 0)
        {
            int idx = XMath.Clamp((int)sample, 0, (int)lut.Length - 1);
            sample = lut[idx];
        }

        float black;
        if (p.HasRepeat != 0)
        {
            int idx = (row % p.RepeatRows) * p.RepeatCols + (col % p.RepeatCols);
            black = blacks[XMath.Min(idx, (int)blacks.Length - 1)];
        }
        else
        {
            black = blacks[XMath.Min(plane, (int)blacks.Length - 1)];
        }

        if (deltaV.Length > 0 && row < deltaV.Length) black += deltaV[row];
        if (deltaH.Length > 0 && col < deltaH.Length) black += deltaH[col];

        float linear = (sample - black) * scales[plane];
        dst[i] = linear > 1f ? 1f : linear;
    }

    // ── Stage 2 → Stage 3 ─────────────────────────────────────────────────────

    /// <summary>
    /// Bilinear 2×2 Bayer demosaic with the phase-preserving period-2 edge
    /// wrap of <c>DemosaicBilinear</c>. One thread per output pixel; writes
    /// three planes.
    /// </summary>
    public static void Demosaic(Index1D i, ArrayView<float> src, ArrayView<float> dst, DemosaicParams p)
    {
        int row = i / p.Width;
        int col = i - row * p.Width;
        int planeSize = p.Width * p.Height;

        dst[i]                 = BilinearSample(src, row, col, 0, p);
        dst[i + planeSize]     = BilinearSample(src, row, col, 1, p);
        dst[i + 2 * planeSize] = BilinearSample(src, row, col, 2, p);
    }

    private static int PatternAt(DemosaicParams p, int r, int c) =>
        r == 0 ? (c == 0 ? p.P00 : p.P01) : (c == 0 ? p.P10 : p.P11);

    private static int WrapEdge(int v, int size)
    {
        // period = 2 (only 2×2 patterns supported)
        if (v < 0) return ((v % 2) + 2) % 2;
        if (v >= size) return (size - 2) + (((v - size) % 2 + 2) % 2);
        return v;
    }

    private static float BilinearSample(ArrayView<float> src, int row, int col, int target, DemosaicParams p)
    {
        if (PatternAt(p, row & 1, col & 1) == target)
            return src[row * p.Width + col];

        float sum = 0f, weight = 0f;
        for (int dr = -1; dr <= 1; dr++)
        {
            for (int dc = -1; dc <= 1; dc++)
            {
                if (dr == 0 && dc == 0) continue;
                int nr = row + dr, nc = col + dc;
                if (PatternAt(p, nr & 1, nc & 1) != target) continue;

                int cr = WrapEdge(nr, p.Height);
                int cc = WrapEdge(nc, p.Width);
                float w = (dr != 0 && dc != 0) ? 0.25f : 0.5f;
                sum += src[cr * p.Width + cc] * w;
                weight += w;
            }
        }
        return weight > 0f ? sum / weight : 0f;
    }

    // ── Stage 3 → linear output RGB ───────────────────────────────────────────

    /// <summary>
    /// Camera → (ProPhoto → HueSatMap →) output matrix → baseline exposure →
    /// optional profile tone curve. Reads the crop window of a full-size
    /// planar Stage-3 image and writes a crop-sized planar float image.
    /// </summary>
    public static void Render(
        Index1D i,
        ArrayView<float> src,
        ArrayView<float> dst,
        ArrayView<float> hsm,        // 3 floats per entry; length 0 → none
        ArrayView<float> curveIn,    // tone-curve control-point inputs; length 0 → none
        ArrayView<float> curveOut,
        RenderParams p)
    {
        int row = i / p.CropWidth;
        int col = i - row * p.CropWidth;
        int srcPlane = p.SrcWidth * p.SrcHeight;
        int s = (row + p.CropTop) * p.SrcWidth + (col + p.CropLeft);

        float r = src[s], g = src[s + srcPlane], b = src[s + 2 * srcPlane];

        float or = p.A00 * r + p.A01 * g + p.A02 * b;
        float og = p.A10 * r + p.A11 * g + p.A12 * b;
        float ob = p.A20 * r + p.A21 * g + p.A22 * b;

        if (p.HasHueSatMap != 0)
        {
            ApplyHueSatMap(hsm, p, ref or, ref og, ref ob);
            float pr = or, pg = og, pb = ob;
            or = p.B00 * pr + p.B01 * pg + p.B02 * pb;
            og = p.B10 * pr + p.B11 * pg + p.B12 * pb;
            ob = p.B20 * pr + p.B21 * pg + p.B22 * pb;
        }

        or *= p.ExposureScale;
        og *= p.ExposureScale;
        ob *= p.ExposureScale;

        if (curveIn.Length > 0)
        {
            or = EvaluateCurve(or, curveIn, curveOut);
            og = EvaluateCurve(og, curveIn, curveOut);
            ob = EvaluateCurve(ob, curveIn, curveOut);
        }

        int dstPlane = p.CropWidth * p.CropHeight;
        dst[i] = or;
        dst[i + dstPlane] = og;
        dst[i + 2 * dstPlane] = ob;
    }

    /// <summary>Piecewise-linear lookup; mirrors <c>HdrToneMapper.EvaluateCurve</c>.</summary>
    private static float EvaluateCurve(float x, ArrayView<float> cin, ArrayView<float> cout)
    {
        int n = (int)cin.Length;
        if (x <= cin[0]) return cout[0];
        if (x >= cin[n - 1]) return cout[n - 1];

        int lo = 0, hi = n - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) >> 1;
            if (cin[mid] <= x) lo = mid; else hi = mid;
        }

        float t = (x - cin[lo]) / (cin[hi] - cin[lo]);
        return cout[lo] + t * (cout[hi] - cout[lo]);
    }

    /// <summary>Mirrors <c>HueSatMap.Apply</c> (2.5-D and full 3-D paths).</summary>
    private static void ApplyHueSatMap(ArrayView<float> tbl, RenderParams p, ref float r, ref float g, ref float b)
    {
        float rr = XMath.Max(r, 0f), gg = XMath.Max(g, 0f), bb = XMath.Max(b, 0f);

        // RGB → HSV (hue 0..6)
        float v = XMath.Max(rr, XMath.Max(gg, bb));
        float gap = v - XMath.Min(rr, XMath.Min(gg, bb));
        float h, s;
        if (gap > 0f)
        {
            if (rr == v) { h = (gg - bb) / gap; if (h < 0f) h += 6f; }
            else if (gg == v) h = 2f + (bb - rr) / gap;
            else h = 4f + (rr - gg) / gap;
            s = gap / v;
        }
        else
        {
            h = 0f;
            s = 0f;
        }

        int hueDiv = p.HueDivisions, satDiv = p.SatDivisions, valDiv = p.ValDivisions;
        float hScale = hueDiv < 2 ? 0f : hueDiv * (1f / 6f);
        float sScale = satDiv - 1;
        int maxHueIndex0 = hueDiv - 1;
        int maxSatIndex0 = satDiv - 2;

        float hScaled = h * hScale;
        float sScaled = s * sScale;
        int hIndex0 = XMath.Clamp((int)hScaled, 0, maxHueIndex0);
        int sIndex0 = XMath.Clamp((int)sScaled, 0, XMath.Max(0, maxSatIndex0));
        int hIndex1 = hIndex0 + 1;
        if (hIndex0 >= maxHueIndex0) { hIndex0 = maxHueIndex0; hIndex1 = 0; }
        float hFract1 = hScaled - hIndex0;
        float sFract1 = sScaled - sIndex0;
        float hFract0 = 1f - hFract1;
        float sFract0 = 1f - sFract1;
        int sIndex1 = XMath.Min(sIndex0 + 1, satDiv - 1);

        float hueShift, satScale, valScale;

        if (valDiv < 2)
        {
            int e00 = (hIndex0 * satDiv + sIndex0) * 3;
            int e01 = (hIndex1 * satDiv + sIndex0) * 3;
            int e00b = (hIndex0 * satDiv + sIndex1) * 3;
            int e01b = (hIndex1 * satDiv + sIndex1) * 3;

            float hueShift0 = hFract0 * tbl[e00] + hFract1 * tbl[e01];
            float satScale0 = hFract0 * tbl[e00 + 1] + hFract1 * tbl[e01 + 1];
            float valScale0 = hFract0 * tbl[e00 + 2] + hFract1 * tbl[e01 + 2];
            float hueShift1 = hFract0 * tbl[e00b] + hFract1 * tbl[e01b];
            float satScale1 = hFract0 * tbl[e00b + 1] + hFract1 * tbl[e01b + 1];
            float valScale1 = hFract0 * tbl[e00b + 2] + hFract1 * tbl[e01b + 2];

            hueShift = sFract0 * hueShift0 + sFract1 * hueShift1;
            satScale = sFract0 * satScale0 + sFract1 * satScale1;
            valScale = sFract0 * valScale0 + sFract1 * valScale1;
        }
        else
        {
            float vScale = valDiv - 1;
            int maxValIndex0 = valDiv - 2;
            float vScaled = v * vScale;
            int vIndex0 = XMath.Clamp((int)vScaled, 0, XMath.Max(0, maxValIndex0));
            float vFract1 = vScaled - vIndex0;
            float vFract0 = 1f - vFract1;
            int vIndex1 = XMath.Min(vIndex0 + 1, valDiv - 1);

            int e000 = ((vIndex0 * hueDiv + hIndex0) * satDiv + sIndex0) * 3;
            int e010 = ((vIndex0 * hueDiv + hIndex1) * satDiv + sIndex0) * 3;
            int e001 = ((vIndex0 * hueDiv + hIndex0) * satDiv + sIndex1) * 3;
            int e011 = ((vIndex0 * hueDiv + hIndex1) * satDiv + sIndex1) * 3;
            int e100 = ((vIndex1 * hueDiv + hIndex0) * satDiv + sIndex0) * 3;
            int e110 = ((vIndex1 * hueDiv + hIndex1) * satDiv + sIndex0) * 3;
            int e101 = ((vIndex1 * hueDiv + hIndex0) * satDiv + sIndex1) * 3;
            int e111 = ((vIndex1 * hueDiv + hIndex1) * satDiv + sIndex1) * 3;

            float hueShift0 = vFract0 * (hFract0 * tbl[e000] + hFract1 * tbl[e010]) + vFract1 * (hFract0 * tbl[e100] + hFract1 * tbl[e110]);
            float satScale0 = vFract0 * (hFract0 * tbl[e000 + 1] + hFract1 * tbl[e010 + 1]) + vFract1 * (hFract0 * tbl[e100 + 1] + hFract1 * tbl[e110 + 1]);
            float valScale0 = vFract0 * (hFract0 * tbl[e000 + 2] + hFract1 * tbl[e010 + 2]) + vFract1 * (hFract0 * tbl[e100 + 2] + hFract1 * tbl[e110 + 2]);
            float hueShift1 = vFract0 * (hFract0 * tbl[e001] + hFract1 * tbl[e011]) + vFract1 * (hFract0 * tbl[e101] + hFract1 * tbl[e111]);
            float satScale1 = vFract0 * (hFract0 * tbl[e001 + 1] + hFract1 * tbl[e011 + 1]) + vFract1 * (hFract0 * tbl[e101 + 1] + hFract1 * tbl[e111 + 1]);
            float valScale1 = vFract0 * (hFract0 * tbl[e001 + 2] + hFract1 * tbl[e011 + 2]) + vFract1 * (hFract0 * tbl[e101 + 2] + hFract1 * tbl[e111 + 2]);

            hueShift = sFract0 * hueShift0 + sFract1 * hueShift1;
            satScale = sFract0 * satScale0 + sFract1 * satScale1;
            valScale = sFract0 * valScale0 + sFract1 * valScale1;
        }

        h += hueShift * (6f / 360f);
        s = XMath.Min(s * satScale, 1f);
        v = XMath.Max(v * valScale, 0f);

        // HSV → RGB
        if (s > 0f)
        {
            h -= 6f * XMath.Floor(h / 6f);   // fmod into [0, 6)
            int sector = (int)h;
            float f = h - sector;
            float pp = v * (1f - s);
            float q = v * (1f - s * f);
            float t = v * (1f - s * (1f - f));
            switch (sector)
            {
                case 0: r = v; g = t; b = pp; break;
                case 1: r = q; g = v; b = pp; break;
                case 2: r = pp; g = v; b = t; break;
                case 3: r = pp; g = q; b = v; break;
                case 4: r = t; g = pp; b = v; break;
                case 5: r = v; g = pp; b = q; break;
                default: r = v; g = t; b = pp; break;
            }
        }
        else
        {
            r = v; g = v; b = v;
        }
    }

    // ── HDR → SDR tone map (in place) ─────────────────────────────────────────

    private const float ShadowLift = 0.03f;
    private const float ToeStrength = 0.08f;

    private static float SCurve(float x)
    {
        if (x <= 0f) return ShadowLift;
        float x2 = x * x;
        float t = 1f / (1f + ToeStrength / x2);
        return ShadowLift + (1f - ShadowLift) * t;
    }

    /// <summary>
    /// Mirrors <c>HdrToneMapper.Apply</c>: per-channel profile tone curve when
    /// one is supplied, else the hue-preserving luminance S-curve.
    /// </summary>
    public static void ToneMap(Index1D i, ArrayView<float> rgb, ArrayView<float> curveIn, ArrayView<float> curveOut, PlanarParams p)
    {
        int n = p.PixelCount;
        if (curveIn.Length > 0)
        {
            rgb[i] = EvaluateCurve(rgb[i], curveIn, curveOut);
            rgb[i + n] = EvaluateCurve(rgb[i + n], curveIn, curveOut);
            rgb[i + 2 * n] = EvaluateCurve(rgb[i + 2 * n], curveIn, curveOut);
            return;
        }

        float r = rgb[i], g = rgb[i + n], b = rgb[i + 2 * n];
        float y = 0.2126f * r + 0.7152f * g + 0.0722f * b;
        float mappedY = SCurve(y);
        if (y > 1e-8f)
        {
            float scale = mappedY / y;
            rgb[i] = XMath.Max(0f, r * scale);
            rgb[i + n] = XMath.Max(0f, g * scale);
            rgb[i + 2 * n] = XMath.Max(0f, b * scale);
        }
        else
        {
            rgb[i] = mappedY;
            rgb[i + n] = mappedY;
            rgb[i + 2 * n] = mappedY;
        }
    }

    // ── sRGB gamma + 8-bit quantize (planar float → interleaved RGB8) ────────

    private static float SrgbGamma(float linear) =>
        linear <= 0.0031308f ? 12.92f * linear : 1.055f * XMath.Pow(linear, 1f / 2.4f) - 0.055f;

    /// <summary>Mirrors <c>Stage3Renderer.GammaAndQuantize</c>.</summary>
    public static void GammaQuantize(Index1D i, ArrayView<float> rgb, ArrayView<byte> dst, PlanarParams p)
    {
        int n = p.PixelCount;
        int d = i * 3;
        dst[d]     = (byte)(SrgbGamma(XMath.Clamp(rgb[i], 0f, 1f)) * 255f + 0.5f);
        dst[d + 1] = (byte)(SrgbGamma(XMath.Clamp(rgb[i + n], 0f, 1f)) * 255f + 0.5f);
        dst[d + 2] = (byte)(SrgbGamma(XMath.Clamp(rgb[i + 2 * n], 0f, 1f)) * 255f + 0.5f);
    }
}
