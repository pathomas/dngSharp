namespace DngSharp.Dng.Sdk.Color.Cct;

/// <summary>
/// Robertson-method CCT ↔ xy conversion. Ports the "legacy" path of
/// <c>dng_temperature.cpp</c>'s <c>LegacyGetXY</c>/<c>LegacySetXY</c>.
///
/// <para>Uses the 31-row Robertson table from Wyszecki &amp; Stiles
/// <i>Color Science</i>, 2nd ed., p. 228. Each row holds:
///   <list type="bullet">
///     <item><c>r</c> — reciprocal CCT in mired (10⁶/K)</item>
///     <item><c>u</c>, <c>v</c> — CIE 1960 UCS chromaticity of the
///       isothermal locus point</item>
///     <item><c>t</c> — slope of the isotherm in (u,v)</item>
///   </list>
/// </para>
///
/// <para>The "extended" Planckian-integration path
/// (<c>PlanckReferenceXYForTemp</c>) is intentionally not ported here —
/// adds ~700 lines of CIE 1931 observer math for the very narrow case of
/// CCT &lt; 1667 K or &gt; 25000 K. Add when a caller actually needs it.</para>
/// </summary>
public static class CctRobertson
{
    private const double TintScale = -3000.0;

    private readonly struct Row
    {
        public readonly double R;   // reciprocal mired = 10^6 / K
        public readonly double U;
        public readonly double V;
        public readonly double T;
        public Row(double r, double u, double v, double t) { R = r; U = u; V = v; T = t; }
    }

    private static readonly Row[] Table =
    [
        new(  0, 0.18006, 0.26352, -0.24341),
        new( 10, 0.18066, 0.26589, -0.25479),
        new( 20, 0.18133, 0.26846, -0.26876),
        new( 30, 0.18208, 0.27119, -0.28539),
        new( 40, 0.18293, 0.27407, -0.30470),
        new( 50, 0.18388, 0.27709, -0.32675),
        new( 60, 0.18494, 0.28021, -0.35156),
        new( 70, 0.18611, 0.28342, -0.37915),
        new( 80, 0.18740, 0.28668, -0.40955),
        new( 90, 0.18880, 0.28997, -0.44278),
        new(100, 0.19032, 0.29326, -0.47888),
        new(125, 0.19462, 0.30141, -0.58204),
        new(150, 0.19962, 0.30921, -0.70471),
        new(175, 0.20525, 0.31647, -0.84901),
        new(200, 0.21142, 0.32312, -1.0182),
        new(225, 0.21807, 0.32909, -1.2168),
        new(250, 0.22511, 0.33439, -1.4512),
        new(275, 0.23247, 0.33904, -1.7298),
        new(300, 0.24010, 0.34308, -2.0637),
        new(325, 0.24702, 0.34655, -2.4681),
        new(350, 0.25591, 0.34951, -2.9641),
        new(375, 0.26400, 0.35200, -3.5814),
        new(400, 0.27218, 0.35407, -4.3633),
        new(425, 0.28039, 0.35577, -5.3762),
        new(450, 0.28863, 0.35714, -6.7262),
        new(475, 0.29685, 0.35823, -8.5955),
        new(500, 0.30505, 0.35907, -11.324),
        new(525, 0.31320, 0.35968, -15.628),
        new(550, 0.32129, 0.36011, -23.325),
        new(575, 0.32931, 0.36038, -40.770),
        new(600, 0.33724, 0.36051, -116.45),
    ];

    /// <summary>
    /// Convert (CCT in kelvin, tint) → CIE xy. Mirrors <c>LegacyGetXY</c>.
    /// </summary>
    public static XyCoord TemperatureTintToXy(double kelvin, double tint)
    {
        // r is the input CCT in mired space (10^6 / K).
        double r = 1.0e6 / kelvin;
        double offset = tint * (1.0 / TintScale);

        for (int index = 0; index <= 29; index++)
        {
            if (r < Table[index + 1].R || index == 29)
            {
                double f = (Table[index + 1].R - r) / (Table[index + 1].R - Table[index].R);

                double u = Table[index].U * f + Table[index + 1].U * (1.0 - f);
                double v = Table[index].V * f + Table[index + 1].V * (1.0 - f);

                // Isotherm slopes — normalize to unit vectors before tint offset.
                double uu1 = 1.0, vv1 = Table[index].T;
                double uu2 = 1.0, vv2 = Table[index + 1].T;
                double len1 = System.Math.Sqrt(1.0 + vv1 * vv1);
                double len2 = System.Math.Sqrt(1.0 + vv2 * vv2);
                uu1 /= len1; vv1 /= len1;
                uu2 /= len2; vv2 /= len2;
                double uu3 = uu1 * f + uu2 * (1.0 - f);
                double vv3 = vv1 * f + vv2 * (1.0 - f);
                double len3 = System.Math.Sqrt(uu3 * uu3 + vv3 * vv3);
                uu3 /= len3; vv3 /= len3;

                u += uu3 * offset;
                v += vv3 * offset;

                // CIE 1960 UCS (u,v) → xy.
                return new XyCoord(
                    1.5 * u / (u - 4.0 * v + 2.0),
                          v / (u - 4.0 * v + 2.0));
            }
        }
        // Unreachable — the index == 29 branch covers the tail.
        return XyCoord.D50;
    }

    /// <summary>
    /// Convert CIE xy → (CCT in kelvin, tint). Mirrors <c>LegacySetXY</c>.
    /// </summary>
    public static (double Kelvin, double Tint) XyToTemperatureTint(XyCoord xy)
    {
        double u = 2.0 * xy.X / (1.5 - xy.X + 6.0 * xy.Y);
        double v = 3.0 * xy.Y / (1.5 - xy.X + 6.0 * xy.Y);

        double lastDt = 0.0, lastDu = 0.0, lastDv = 0.0;
        double kelvin = 0.0, tint = 0.0;

        for (int index = 1; index <= 30; index++)
        {
            double du = 1.0;
            double dv = Table[index].T;
            double len = System.Math.Sqrt(1.0 + dv * dv);
            du /= len;
            dv /= len;

            double uu = u - Table[index].U;
            double vv = v - Table[index].V;
            double dt = -uu * dv + vv * du;

            if (dt <= 0.0 || index == 30)
            {
                if (dt > 0.0) dt = 0.0;
                dt = -dt;

                double f = index == 1 ? 0.0 : dt / (lastDt + dt);
                kelvin = 1.0e6 / (Table[index - 1].R * f + Table[index].R * (1.0 - f));

                uu = u - (Table[index - 1].U * f + Table[index].U * (1.0 - f));
                vv = v - (Table[index - 1].V * f + Table[index].V * (1.0 - f));

                du = du * (1.0 - f) + lastDu * f;
                dv = dv * (1.0 - f) + lastDv * f;
                len = System.Math.Sqrt(du * du + dv * dv);
                du /= len;
                dv /= len;
                tint = (uu * du + vv * dv) * TintScale;
                break;
            }

            lastDt = dt;
            lastDu = du;
            lastDv = dv;
        }

        return (kelvin, tint);
    }
}
