namespace DngSharp.Dng.Sdk.Tests.TestImages;

/// <summary>
/// Pure pixel-value generators shared by <see cref="SyntheticDngBuilder"/> and
/// <see cref="SyntheticTiffBuilder"/>, so a DNG built directly and a DNG
/// produced by converting the companion plain TIFF start from byte-identical
/// pixel data. All patterns are neutral (R=G=B at every pixel) so a correct
/// render stays grayscale; any hue shift signals a color-pipeline bug, not a
/// pattern-generation one.
/// </summary>
internal static class SyntheticPixelPatterns
{
    /// <summary>
    /// A horizontal 0→<paramref name="maxValue"/> ramp: column 0 = 0, column
    /// <paramref name="width"/>-1 = <paramref name="maxValue"/>, linear in
    /// between, identical in every row. Returns a row-major interleaved
    /// UInt16 buffer with 3 samples per pixel.
    /// </summary>
    public static ushort[] GradientLeftToRight(int width, int height, ushort maxValue)
    {
        var pixels = new ushort[(long)width * height * 3];
        for (int col = 0; col < width; col++)
        {
            ushort v = (ushort)System.Math.Round(
                width > 1 ? maxValue * (double)col / (width - 1) : 0.0);
            for (int row = 0; row < height; row++)
            {
                long baseIdx = ((long)row * width + col) * 3;
                pixels[baseIdx + 0] = v;
                pixels[baseIdx + 1] = v;
                pixels[baseIdx + 2] = v;
            }
        }
        return pixels;
    }

    /// <summary>
    /// A white (<paramref name="maxValue"/>) background with a solid black
    /// (0) circle centered in the image. The circle's diameter is
    /// <paramref name="diameterFraction"/> of <paramref name="size"/> (e.g.
    /// 0.5 → the circle covers the center 50% of both width and height), so
    /// on a square canvas the authored circle is a true circle — any render
    /// pipeline that introduces anisotropic (non-uniform horizontal vs.
    /// vertical) scaling will turn it into a visible ellipse.
    /// </summary>
    public static ushort[] CenteredCircle(int size, double diameterFraction, ushort maxValue)
    {
        var pixels = new ushort[(long)size * size * 3];
        double radius = size * diameterFraction / 2.0;
        double center = size / 2.0;
        double radiusSq = radius * radius;

        for (int row = 0; row < size; row++)
        {
            double dy = row + 0.5 - center;
            for (int col = 0; col < size; col++)
            {
                double dx = col + 0.5 - center;
                bool insideCircle = (dx * dx) + (dy * dy) <= radiusSq;
                ushort v = insideCircle ? (ushort)0 : maxValue;

                long baseIdx = ((long)row * size + col) * 3;
                pixels[baseIdx + 0] = v;
                pixels[baseIdx + 1] = v;
                pixels[baseIdx + 2] = v;
            }
        }
        return pixels;
    }

    /// <summary>
    /// An alternating black/white checkerboard: <paramref name="squarePx"/>-sized
    /// squares, cell (row/squarePx, col/squarePx) parity decides black (0) vs.
    /// white (<paramref name="maxValue"/>). Detects tile/strip-boundary
    /// misalignment and duplicate-column/row decode bugs — any bug that
    /// shifts, drops, or duplicates columns/rows shows up as an irregular
    /// (non-<paramref name="squarePx"/>-periodic) transition spacing.
    /// </summary>
    public static ushort[] Checkerboard(int width, int height, int squarePx, ushort maxValue)
    {
        var pixels = new ushort[(long)width * height * 3];
        for (int row = 0; row < height; row++)
        {
            int cellRow = row / squarePx;
            for (int col = 0; col < width; col++)
            {
                int cellCol = col / squarePx;
                bool isWhite = ((cellRow + cellCol) & 1) == 0;
                ushort v = isWhite ? maxValue : (ushort)0;

                long baseIdx = ((long)row * width + col) * 3;
                pixels[baseIdx + 0] = v;
                pixels[baseIdx + 1] = v;
                pixels[baseIdx + 2] = v;
            }
        }
        return pixels;
    }

    /// <summary>
    /// A raw sensor-sized buffer of <paramref name="rawSize"/>×<paramref name="rawSize"/>
    /// used to test <c>ActiveArea</c>/<c>DefaultCropArea</c> off-by-one bugs:
    /// <list type="bullet">
    ///   <item>Pixels outside <c>[margin, margin + innerSize)</c> on both axes
    ///     — i.e. the sensor padding that a correct crop must exclude entirely
    ///     — are filled with a "poison" mid-gray value that is neither the
    ///     border's white nor the interior's black, so any leak past the crop
    ///     boundary is trivially distinguishable from the fixture's own
    ///     content.</item>
    ///   <item>Inside the crop rect: solid black with a <paramref name="borderPx"/>-wide
    ///     white border at the exact edges of the crop rect.</item>
    /// </list>
    /// A correct render crops to exactly <paramref name="innerSize"/>×<paramref name="innerSize"/>
    /// with a uniform <paramref name="borderPx"/> white border and no poison
    /// gray visible anywhere.
    /// </summary>
    public static ushort[] BorderedActiveArea(
        int rawSize, int margin, int innerSize, int borderPx, ushort maxValue)
    {
        var pixels = new ushort[(long)rawSize * rawSize * 3];

        // A low-but-nonzero "poison" fraction, deliberately chosen away from
        // both 0 (black interior) and maxValue (white border): the default
        // tone-curve fallback (see HdrToneMapper.SCurve) compresses highlights
        // strongly, so a naive mid-gray (0.5) fraction would render nearly as
        // bright as pure white and be indistinguishable from the border. 0.15
        // renders to a clearly separated mid-brightness band instead.
        ushort poison = (ushort)System.Math.Round(maxValue * 0.15);

        for (int row = 0; row < rawSize; row++)
        {
            int innerRow = row - margin;
            for (int col = 0; col < rawSize; col++)
            {
                int innerCol = col - margin;
                ushort v;
                if (innerRow < 0 || innerRow >= innerSize || innerCol < 0 || innerCol >= innerSize)
                {
                    v = poison;
                }
                else
                {
                    bool onBorder = innerRow < borderPx || innerRow >= innerSize - borderPx
                                  || innerCol < borderPx || innerCol >= innerSize - borderPx;
                    v = onBorder ? maxValue : (ushort)0;
                }

                long baseIdx = ((long)row * rawSize + col) * 3;
                pixels[baseIdx + 0] = v;
                pixels[baseIdx + 1] = v;
                pixels[baseIdx + 2] = v;
            }
        }
        return pixels;
    }
}
