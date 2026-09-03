using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Dng.Sdk.Imaging;
using Dng.Sdk.Pipeline;
using Dng.Sdk.Pixels;
using Dng.Sdk.Primitives;
using Dng.Sdk.Tasks;

namespace Dng.Sdk.Render;

/// <summary>
/// HDR → SDR tone mapper. Used when the source DNG has
/// <c>ProfileDynamicRange = 1</c> (HDR) and the output is a standard-dynamic-
/// range format (JPEG, 8-bit WebP).
///
/// <para>Strategy (in priority order):
/// <list type="number">
///   <item>If the active camera profile provides a <c>ProfileToneCurve</c>,
///         apply it. The tone curve is a 1-D piecewise-linear function that
///         maps [0, max-in] → [0, 1] in linear-sRGB space. Applied
///         per-channel, matching the camera profile's exact specification.</item>
///   <item>Otherwise, apply a photographic <see cref="SCurve"/>: shadows are
///         lifted, mid-tones get a contrast boost, and highlights roll off
///         smoothly. It is evaluated on luminance (Rec. 709 Y) and the
///         resulting scale factor is applied uniformly to all channels so hue
///         is preserved (unlike a per-channel Reinhard operator, which can
///         shift saturated colors).</item>
/// </list>
/// </para>
///
/// <para>Both paths operate on the linear-sRGB output of
/// <see cref="Stage3Renderer"/> — apply tone mapping <b>after</b> the color
/// matrix transform and <b>before</b> gamma encoding and quantization.</para>
///
/// <para>HDR WebP output (<c>-webp -hdr</c> flag path) bypasses this mapper
/// entirely and keeps the linear-sRGB values unbounded. The mapper is only
/// engaged on the SDR output path.</para>
/// </summary>
public static class HdrToneMapper
{
    // S-curve shape parameters. See SCurve() for details.
    private const double ShadowLift = 0.03;
    private const double ToeStrength = 0.08;

    /// <summary>
    /// Simple Reinhard tone-mapping operator: <c>t(x) = x / (1 + x)</c>.
    /// Compresses [0, ∞) to [0, 1) with a pleasing roll-off. Exact at 0;
    /// asymptotically approaches 1. Preserves relative mid-tone brightness.
    ///
    /// <para>Retained as a simple, allocation-free building block and for
    /// direct unit testing. <see cref="Apply"/> uses <see cref="SCurve"/>
    /// (not this operator) as its no-curve default — see the class remarks.</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Reinhard(double linear)
    {
        if (linear <= 0.0) return 0.0;
        return linear / (1.0 + linear);
    }

    /// <summary>
    /// Photographic S-curve tone-mapping operator, used as the default when no
    /// <c>ProfileToneCurve</c> is embedded in the DNG. Approximates the
    /// "punchy" look of ACR/Lightroom's default rendering:
    /// <list type="bullet">
    ///   <item><b>Shadow lift</b> — <c>SCurve(0) = ShadowLift &gt; 0</c>, so
    ///         blacks aren't crushed to a hard zero.</item>
    ///   <item><b>Mid-tone contrast boost</b> — the curve's slope exceeds 1
    ///         around the 0.2–0.5 input range, adding "pop" akin to a
    ///         Clarity/contrast slider.</item>
    ///   <item><b>Highlight roll-off</b> — slope smoothly drops below 1 above
    ///         ~0.6 and the curve asymptotes to 1 as input → ∞, so
    ///         over-exposed HDR highlights compress instead of clipping hard.</item>
    /// </list>
    /// Implemented as a Hill/Michaelis-Menten-style function:
    /// <c>t(x) = lift + (1 - lift) / (1 + toe / x²)</c>, which is numerically
    /// stable (no NaN) even for very large or very small positive <c>x</c>.
    ///
    /// <para><see cref="ToeStrength"/> sets the identity crossover (the input
    /// value where <c>SCurve(x) ≈ x</c>) to roughly 0.35–0.4 — i.e. typical
    /// scene-average linear luminance (~0.12–0.2, near mid-gray) is lifted
    /// slightly rather than crushed. A larger toe value moves that crossover
    /// higher and systematically darkens ordinary well-exposed scenes (this
    /// was previously mis-tuned to ~0.55, which read as "underexposed" on
    /// real-world DNGs).</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double SCurve(double linear)
    {
        if (linear <= 0.0) return ShadowLift;

        double x2 = linear * linear;
        double t = 1.0 / (1.0 + ToeStrength / x2);
        return ShadowLift + (1.0 - ShadowLift) * t;
    }

    /// <summary>
    /// Apply tone mapping in-place to a Float32 linear-sRGB
    /// <see cref="SimpleImage"/>. Values above 1.0 are compressed; negative
    /// values are clamped to 0.
    ///
    /// <para>Pass a <paramref name="profileToneCurve"/> to use the DNG
    /// camera-profile tone curve (piecewise-linear 1-D function). Pass null
    /// to fall back to <see cref="Reinhard"/>.</para>
    /// </summary>
    /// <param name="image">Float32 linear-sRGB image, modified in place.</param>
    /// <param name="profileToneCurve">Optional tone curve from the camera profile
    /// (DNG <c>ProfileToneCurve</c> tag). Each element is a control-point pair
    /// (input, output) where inputs and outputs are in [0, 1]. At least two
    /// points required (identity = [(0,0), (1,1)]). Pass null to use the
    /// default <see cref="SCurve"/> operator.</param>
    /// <param name="host">Optional host for tile size and cancellation.</param>
    public static void Apply(
        SimpleImage image,
        (double Input, double Output)[]? profileToneCurve = null,
        DngHost? host = null)
    {
        ArgumentNullException.ThrowIfNull(image);

        if (profileToneCurve is not null && profileToneCurve.Length < 2)
            profileToneCurve = null; // degenerate curve → fall back to Reinhard

        var task = new ToneMapTask(image, profileToneCurve, host?.MaxTileEdgePixels ?? 256);
        AreaTaskRunner.Run(task, image.Bounds, host?.Sniffer);
    }

    // ── Piecewise-linear tone curve lookup ────────────────────────────────────

    /// <summary>
    /// Evaluate a piecewise-linear tone curve at input <paramref name="x"/>.
    /// The curve is expressed as an ordered list of (input, output) control
    /// points. Values outside the control-point range are linearly extrapolated
    /// from the nearest segment.
    /// </summary>
    public static double EvaluateCurve(double x, (double Input, double Output)[] curve)
    {
        if (curve.Length == 0) return x;
        if (x <= curve[0].Input) return curve[0].Output;
        if (x >= curve[^1].Input) return curve[^1].Output;

        // Binary search for the segment that brackets x.
        int lo = 0, hi = curve.Length - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) >> 1;
            if (curve[mid].Input <= x) lo = mid; else hi = mid;
        }

        double t = (x - curve[lo].Input) / (curve[hi].Input - curve[lo].Input);
        return curve[lo].Output + t * (curve[hi].Output - curve[lo].Output);
    }

    // ── Private task ──────────────────────────────────────────────────────────

    private sealed class ToneMapTask(
        SimpleImage image,
        (double Input, double Output)[]? curve,
        int tileEdge) : IAreaTask
    {
        public DngPoint MaxTileSize(DngPoint imageSize) => new(tileEdge, tileEdge);

        // Rec. 709 luma weights — used to compute a hue-preserving scale factor
        // for the S-curve default path.
        private const double LumaR = 0.2126;
        private const double LumaG = 0.7152;
        private const double LumaB = 0.0722;

        public void Process(int threadIndex, DngRect tile)
        {
            var buf = image.GetTile(tile);
            var bytes = buf.Memory.Span;

            for (int row = tile.T; row < tile.B; row++)
            {
                for (int col = tile.L; col < tile.R; col++)
                {
                    if (curve is not null)
                    {
                        // ProfileToneCurve is applied per-channel, matching the
                        // camera profile's exact specification.
                        for (uint p = 0; p < image.Planes; p++)
                        {
                            long off = buf.OffsetBytes(row, col, p);
                            float v = BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice((int)off, 4));
                            double mapped = EvaluateCurve(v, curve);
                            BinaryPrimitives.WriteSingleLittleEndian(bytes.Slice((int)off, 4), (float)mapped);
                        }

                        continue;
                    }

                    if (image.Planes < 3)
                    {
                        // No luminance to compute (e.g. a single-plane mask) —
                        // apply the S-curve directly per-channel.
                        for (uint p = 0; p < image.Planes; p++)
                        {
                            long off = buf.OffsetBytes(row, col, p);
                            float v = BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice((int)off, 4));
                            double mapped = SCurve(v);
                            BinaryPrimitives.WriteSingleLittleEndian(bytes.Slice((int)off, 4), (float)mapped);
                        }

                        continue;
                    }

                    // S-curve default: evaluate on luminance and apply the
                    // resulting scale uniformly to R/G/B so hue is preserved
                    // (per-channel Reinhard/S-curve would desaturate highlights).
                    long offR = buf.OffsetBytes(row, col, 0);
                    long offG = buf.OffsetBytes(row, col, 1);
                    long offB = buf.OffsetBytes(row, col, 2);

                    double r = BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice((int)offR, 4));
                    double g = BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice((int)offG, 4));
                    double b = BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice((int)offB, 4));

                    double y = LumaR * r + LumaG * g + LumaB * b;
                    double mappedY = SCurve(y);
                    double scale = y > 1e-8 ? mappedY / y : 0.0;

                    double or, og, ob;
                    if (y > 1e-8)
                    {
                        or = System.Math.Max(0.0, r * scale);
                        og = System.Math.Max(0.0, g * scale);
                        ob = System.Math.Max(0.0, b * scale);
                    }
                    else
                    {
                        // Near-black, achromatic pixel: no ratio to preserve —
                        // lift all channels to the S-curve's shadow floor.
                        or = og = ob = mappedY;
                    }

                    BinaryPrimitives.WriteSingleLittleEndian(bytes.Slice((int)offR, 4), (float)or);
                    BinaryPrimitives.WriteSingleLittleEndian(bytes.Slice((int)offG, 4), (float)og);
                    BinaryPrimitives.WriteSingleLittleEndian(bytes.Slice((int)offB, 4), (float)ob);

                    // Additional planes beyond RGB (e.g. alpha) pass through
                    // unmodified — nothing further to do for p >= 3.
                }
            }

            image.WriteTile(buf);
        }
    }
}
