using Dng.Sdk.Math;

namespace Dng.Sdk.Render;

/// <summary>
/// Per-camera hue/saturation/value correction table (DNG spec 6.3.7,
/// <c>ProfileHueSatMapData1/2/3</c>). Mirrors <c>dng_hue_sat_map</c> /
/// <c>RefBaselineHueSatMap</c> in <c>dng_reference.cpp</c>.
///
/// <para>Stores a 3-D table (hue × saturation × value divisions) of
/// <see cref="HsbModify"/> deltas, in value-hue-saturation storage order
/// (matching the native layout: <c>index = (val*HueDivisions + hue)*SatDivisions + sat</c>).
/// Most camera profiles use the "2.5D" optimization (<c>ValDivisions &lt; 2</c>),
/// where only hue and saturation are looked up and the value channel is
/// scaled uniformly.</para>
/// </summary>
public sealed class HueSatMap
{
    /// <summary>One (hueShift, satScale, valScale) delta entry.</summary>
    public readonly record struct HsbModify(float HueShift, float SatScale, float ValScale);

    public int HueDivisions { get; }
    public int SatDivisions { get; }
    public int ValDivisions { get; }

    private readonly HsbModify[] _deltas;

    public HueSatMap(int hueDivisions, int satDivisions, int valDivisions, ReadOnlySpan<float> data)
    {
        if (hueDivisions <= 0 || satDivisions <= 0)
            throw new ArgumentException("HueDivisions and SatDivisions must be positive.");

        HueDivisions = hueDivisions;
        SatDivisions = satDivisions;
        ValDivisions = System.Math.Max(1, valDivisions);

        int entryCount = HueDivisions * SatDivisions * ValDivisions;
        if (data.Length < entryCount * 3)
            throw new ArgumentException(
                $"HueSatMap data too short: expected {entryCount * 3} floats, got {data.Length}.");

        _deltas = new HsbModify[entryCount];
        for (int i = 0; i < entryCount; i++)
            _deltas[i] = new HsbModify(data[i * 3], data[i * 3 + 1], data[i * 3 + 2]);
    }

    private HueSatMap(int hueDivisions, int satDivisions, int valDivisions, HsbModify[] deltas)
    {
        HueDivisions = hueDivisions;
        SatDivisions = satDivisions;
        ValDivisions = valDivisions;
        _deltas = deltas;
    }

    private HsbModify this[int hueIndex, int satIndex] => _deltas[hueIndex * SatDivisions + satIndex];

    /// <summary>
    /// Blend two tables at the same dimensions, matching
    /// <c>dng_hue_sat_map::Interpolate</c> (linear blend of every delta by
    /// <paramref name="weight1"/> for <paramref name="map1"/> vs. <c>1-weight1</c>
    /// for <paramref name="map2"/>).
    /// </summary>
    public static HueSatMap Interpolate(HueSatMap map1, HueSatMap map2, double weight1)
    {
        ArgumentNullException.ThrowIfNull(map1);
        ArgumentNullException.ThrowIfNull(map2);

        if (map1.HueDivisions != map2.HueDivisions ||
            map1.SatDivisions != map2.SatDivisions ||
            map1.ValDivisions != map2.ValDivisions)
        {
            // Mismatched dimensions (rare/invalid profile) — fall back to
            // whichever table is closer in weight rather than throwing.
            return weight1 >= 0.5 ? map1 : map2;
        }

        double w1 = System.Math.Clamp(weight1, 0.0, 1.0);
        double w2 = 1.0 - w1;

        var blended = new HsbModify[map1._deltas.Length];
        for (int i = 0; i < blended.Length; i++)
        {
            var a = map1._deltas[i];
            var b = map2._deltas[i];
            blended[i] = new HsbModify(
                (float)(w1 * a.HueShift + w2 * b.HueShift),
                (float)(w1 * a.SatScale + w2 * b.SatScale),
                (float)(w1 * a.ValScale + w2 * b.ValScale));
        }

        return new HueSatMap(map1.HueDivisions, map1.SatDivisions, map1.ValDivisions, blended);
    }

    /// <summary>
    /// Apply the table to a camera-reference RGB triple in place. RGB is
    /// expected to be in linear ProPhoto-RGB reference space, roughly [0, 1]
    /// for SDR content (values above 1 — HDR highlights carried through this
    /// port's tone-mapping pipeline — are preserved rather than hard-clipped,
    /// which is an intentional deviation from the native SDR-only
    /// <c>Pin_real32</c> clip; see class remarks on <see cref="Stage3Renderer"/>).
    /// </summary>
    public void Apply(ref double r, ref double g, ref double b)
    {
        // Native pins RGB >= 0 before RGBtoHSV (DNG_PinnedNonnegativeRGBtoHSV);
        // upper bound is intentionally NOT clamped here to preserve HDR highlights.
        double rr = System.Math.Max(r, 0.0);
        double gg = System.Math.Max(g, 0.0);
        double bb = System.Math.Max(b, 0.0);

        RgbToHsv(rr, gg, bb, out double h, out double s, out double v);

        double hScale = HueDivisions < 2 ? 0.0 : HueDivisions * (1.0 / 6.0);
        double sScale = SatDivisions - 1;

        int maxHueIndex0 = HueDivisions - 1;
        int maxSatIndex0 = SatDivisions - 2;

        double hueShift, satScale, valScale;

        if (ValDivisions < 2)
        {
            // "2.5D" fast path: bilinear interpolation over (hue, sat) only.
            double hScaled = h * hScale;
            double sScaled = s * sScale;

            int hIndex0 = System.Math.Clamp((int)hScaled, 0, maxHueIndex0);
            int sIndex0 = System.Math.Clamp((int)sScaled, 0, System.Math.Max(0, maxSatIndex0));

            int hIndex1 = hIndex0 + 1;
            if (hIndex0 >= maxHueIndex0)
            {
                hIndex0 = maxHueIndex0;
                hIndex1 = 0;
            }

            double hFract1 = hScaled - hIndex0;
            double sFract1 = sScaled - sIndex0;
            double hFract0 = 1.0 - hFract1;
            double sFract0 = 1.0 - sFract1;

            int sIndex1 = System.Math.Min(sIndex0 + 1, SatDivisions - 1);

            var e00 = this[hIndex0, sIndex0];
            var e01 = this[hIndex1, sIndex0];
            var e00b = this[hIndex0, sIndex1];
            var e01b = this[hIndex1, sIndex1];

            double hueShift0 = hFract0 * e00.HueShift + hFract1 * e01.HueShift;
            double satScale0 = hFract0 * e00.SatScale + hFract1 * e01.SatScale;
            double valScale0 = hFract0 * e00.ValScale + hFract1 * e01.ValScale;

            double hueShift1 = hFract0 * e00b.HueShift + hFract1 * e01b.HueShift;
            double satScale1 = hFract0 * e00b.SatScale + hFract1 * e01b.SatScale;
            double valScale1 = hFract0 * e00b.ValScale + hFract1 * e01b.ValScale;

            hueShift = sFract0 * hueShift0 + sFract1 * hueShift1;
            satScale = sFract0 * satScale0 + sFract1 * satScale1;
            valScale = sFract0 * valScale0 + sFract1 * valScale1;
        }
        else
        {
            // Full 3-D trilinear interpolation over (hue, sat, val).
            double vScale = ValDivisions - 1;
            int maxValIndex0 = ValDivisions - 2;

            double hScaled = h * hScale;
            double sScaled = s * sScale;
            double vScaled = v * vScale;

            int hIndex0 = System.Math.Clamp((int)hScaled, 0, maxHueIndex0);
            int sIndex0 = System.Math.Clamp((int)sScaled, 0, System.Math.Max(0, maxSatIndex0));
            int vIndex0 = System.Math.Clamp((int)vScaled, 0, System.Math.Max(0, maxValIndex0));

            int hIndex1 = hIndex0 + 1;
            if (hIndex0 >= maxHueIndex0)
            {
                hIndex0 = maxHueIndex0;
                hIndex1 = 0;
            }

            double hFract1 = hScaled - hIndex0;
            double sFract1 = sScaled - sIndex0;
            double vFract1 = vScaled - vIndex0;
            double hFract0 = 1.0 - hFract1;
            double sFract0 = 1.0 - sFract1;
            double vFract0 = 1.0 - vFract1;

            int sIndex1 = System.Math.Min(sIndex0 + 1, SatDivisions - 1);
            int vIndex1 = System.Math.Min(vIndex0 + 1, ValDivisions - 1);

            HsbModify At(int hi, int si, int vi) => _deltas[(vi * HueDivisions + hi) * SatDivisions + si];

            var e000 = At(hIndex0, sIndex0, vIndex0);
            var e010 = At(hIndex1, sIndex0, vIndex0);
            var e001 = At(hIndex0, sIndex1, vIndex0);
            var e011 = At(hIndex1, sIndex1, vIndex0);
            var e100 = At(hIndex0, sIndex0, vIndex1);
            var e110 = At(hIndex1, sIndex0, vIndex1);
            var e101 = At(hIndex0, sIndex1, vIndex1);
            var e111 = At(hIndex1, sIndex1, vIndex1);

            double hueShift0 = vFract0 * (hFract0 * e000.HueShift + hFract1 * e010.HueShift) +
                                vFract1 * (hFract0 * e100.HueShift + hFract1 * e110.HueShift);
            double satScale0 = vFract0 * (hFract0 * e000.SatScale + hFract1 * e010.SatScale) +
                                vFract1 * (hFract0 * e100.SatScale + hFract1 * e110.SatScale);
            double valScale0 = vFract0 * (hFract0 * e000.ValScale + hFract1 * e010.ValScale) +
                                vFract1 * (hFract0 * e100.ValScale + hFract1 * e110.ValScale);

            double hueShift1 = vFract0 * (hFract0 * e001.HueShift + hFract1 * e011.HueShift) +
                                vFract1 * (hFract0 * e101.HueShift + hFract1 * e111.HueShift);
            double satScale1 = vFract0 * (hFract0 * e001.SatScale + hFract1 * e011.SatScale) +
                                vFract1 * (hFract0 * e101.SatScale + hFract1 * e111.SatScale);
            double valScale1 = vFract0 * (hFract0 * e001.ValScale + hFract1 * e011.ValScale) +
                                vFract1 * (hFract0 * e101.ValScale + hFract1 * e111.ValScale);

            hueShift = sFract0 * hueShift0 + sFract1 * hueShift1;
            satScale = sFract0 * satScale0 + sFract1 * satScale1;
            valScale = sFract0 * valScale0 + sFract1 * valScale1;
        }

        hueShift *= 6.0 / 360.0; // degrees → internal 0-6 hue range
        h += hueShift;
        s = System.Math.Min(s * satScale, 1.0);
        v = System.Math.Max(v * valScale, 0.0); // lower-bound only; see remarks re: HDR headroom

        HsvToRgb(h, s, v, out r, out g, out b);
    }

    /// <summary>Mirrors <c>DNG_RGBtoHSV</c> (hue range 0-6; sat/value 0-1).</summary>
    private static void RgbToHsv(double r, double g, double b, out double h, out double s, out double v)
    {
        v = System.Math.Max(r, System.Math.Max(g, b));
        double gap = v - System.Math.Min(r, System.Math.Min(g, b));

        if (gap > 0.0)
        {
            if (r == v)
            {
                h = (g - b) / gap;
                if (h < 0.0) h += 6.0;
            }
            else if (g == v)
            {
                h = 2.0 + (b - r) / gap;
            }
            else
            {
                h = 4.0 + (r - g) / gap;
            }

            s = gap / v;
        }
        else
        {
            h = 0.0;
            s = 0.0;
        }
    }

    /// <summary>Mirrors <c>DNG_HSVtoRGB</c>, including the rare <c>i==6</c> edge case.</summary>
    private static void HsvToRgb(double h, double s, double v, out double r, out double g, out double b)
    {
        if (s > 0.0)
        {
            h %= 6.0; // C# '%' on doubles matches C's fmod (truncated remainder).
            if (h < 0.0) h += 6.0;

            int i = (int)h;
            double f = h - i;

            double p = v * (1.0 - s);
            double q = v * (1.0 - s * f);
            double t = v * (1.0 - s * (1.0 - f));

            switch (i)
            {
                case 0: r = v; g = t; b = p; break;
                case 1: r = q; g = v; b = p; break;
                case 2: r = p; g = v; b = t; break;
                case 3: r = p; g = q; b = v; break;
                case 4: r = t; g = p; b = v; break;
                case 5: r = v; g = p; b = q; break;
                case 6: r = v; g = t; b = p; break; // fmod edge case (h landed exactly on 6.0)
                default: r = v; g = t; b = p; break;
            }
        }
        else
        {
            r = v;
            g = v;
            b = v;
        }
    }
}
