using System.Numerics;
using MakoKaraGenerator.Core.Media;

namespace MakoKaraGenerator.Media.Tests;

internal static class MediaMomentOracle
{
    internal static QuantizedMediaMoments[] Build(float[] pixels, int width, int height)
    {
        // Deliberately independent: normalized rational pixel/cell boxes, exact decimal luminance,
        // exact rational summation and integer half-up, without production integer-numerator math.
        var xOverlap = new ExactRational[MediaTrackLayoutV1.Columns, width];
        var yOverlap = new ExactRational[MediaTrackLayoutV1.Rows, height];
        for (int cell = 0; cell < MediaTrackLayoutV1.Columns; cell++)
        {
            for (int x = 0; x < width; x++)
            {
                xOverlap[cell, x] = Overlap(new(x, width), new(x + 1, width),
                    new(cell, MediaTrackLayoutV1.Columns), new(cell + 1, MediaTrackLayoutV1.Columns));
            }
        }

        for (int cell = 0; cell < MediaTrackLayoutV1.Rows; cell++)
        {
            for (int y = 0; y < height; y++)
            {
                yOverlap[cell, y] = Overlap(new(y, height), new(y + 1, height),
                    new(cell, MediaTrackLayoutV1.Rows), new(cell + 1, MediaTrackLayoutV1.Rows));
            }
        }

        var sums = new ExactRational[MediaTrackLayoutV1.Cells, MediaTrackLayoutV1.Moments];
        for (int cell = 0; cell < MediaTrackLayoutV1.Cells; cell++)
        {
            for (int moment = 0; moment < MediaTrackLayoutV1.Moments; moment++)
            {
                sums[cell, moment] = ExactRational.Zero;
            }
        }

        // Pixel-major traversal differs from the production cell-major scan.
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = 4 * (y * width + x);
                ExactRational[] moments = Moments(pixels[offset], pixels[offset + 1], pixels[offset + 2], pixels[offset + 3]);
                for (int cellY = 0; cellY < MediaTrackLayoutV1.Rows; cellY++)
                {
                    if (yOverlap[cellY, y].IsZero)
                    {
                        continue;
                    }

                    for (int cellX = 0; cellX < MediaTrackLayoutV1.Columns; cellX++)
                    {
                        if (xOverlap[cellX, x].IsZero)
                        {
                            continue;
                        }

                        ExactRational area = xOverlap[cellX, x] * yOverlap[cellY, y];
                        int cell = cellY * MediaTrackLayoutV1.Columns + cellX;
                        for (int moment = 0; moment < MediaTrackLayoutV1.Moments; moment++)
                        {
                            sums[cell, moment] += area * moments[moment];
                        }
                    }
                }
            }
        }

        var result = new QuantizedMediaMoments[MediaTrackLayoutV1.Cells];
        for (int cell = 0; cell < result.Length; cell++)
        {
            ushort[] quantized = new ushort[MediaTrackLayoutV1.Moments];
            for (int moment = 0; moment < quantized.Length; moment++)
            {
                ExactRational mean = sums[cell, moment] * new ExactRational(MediaTrackLayoutV1.Cells, 1);
                quantized[moment] = mean.Quantize();
            }

            result[cell] = new(quantized[0], quantized[1], quantized[2], quantized[3], quantized[4], quantized[5], quantized[6]);
        }

        return result;
    }

    internal static MediaMomentValues Integrate(MediaMomentValues[] grid, MediaSourceRect rect)
    {
        ExactRational left = ExactRational.FromFloat(rect.Left);
        ExactRational right = ExactRational.FromFloat(rect.Right);
        ExactRational top = ExactRational.FromFloat(rect.Top);
        ExactRational bottom = ExactRational.FromFloat(rect.Bottom);
        ExactRational area = (right - left) * (bottom - top);
        ExactRational[] sums = Enumerable.Repeat(ExactRational.Zero, MediaTrackLayoutV1.Moments).ToArray();
        for (int cellY = 0; cellY < MediaTrackLayoutV1.Rows; cellY++)
        {
            ExactRational overlapY = Overlap(top, bottom, new(cellY, MediaTrackLayoutV1.Rows), new(cellY + 1, MediaTrackLayoutV1.Rows));
            for (int cellX = 0; cellX < MediaTrackLayoutV1.Columns; cellX++)
            {
                ExactRational overlapX = Overlap(left, right, new(cellX, MediaTrackLayoutV1.Columns), new(cellX + 1, MediaTrackLayoutV1.Columns));
                ExactRational weight = overlapX * overlapY;
                double[] values = Components(grid[cellY * MediaTrackLayoutV1.Columns + cellX]);
                for (int moment = 0; moment < values.Length; moment++)
                {
                    sums[moment] += ExactRational.FromDouble(values[moment]) * weight;
                }
            }
        }

        double[] means = sums.Select(sum => (sum / area).ToDouble()).ToArray();
        return new(means[0], means[1], means[2], means[3], means[4], means[5], means[6]);
    }

    internal static double[] Components(MediaMomentValues value) =>
        [value.Rp, value.Gp, value.Bp, value.Alpha, value.YpSquared, value.AlphaYp, value.AlphaSquared];

    private static ExactRational[] Moments(float red, float green, float blue, float alpha)
    {
        ExactRational r = ExactRational.FromFloat(red);
        ExactRational g = ExactRational.FromFloat(green);
        ExactRational b = ExactRational.FromFloat(blue);
        ExactRational a = ExactRational.FromFloat(alpha);
        ExactRational luminance = r * new ExactRational(2126, 10000) +
            g * new ExactRational(7152, 10000) + b * new ExactRational(722, 10000);
        return [r, g, b, a, luminance * luminance, a * luminance, a * a];
    }

    private static ExactRational Overlap(ExactRational firstStart, ExactRational firstEnd,
        ExactRational secondStart, ExactRational secondEnd)
    {
        ExactRational start = firstStart.CompareTo(secondStart) > 0 ? firstStart : secondStart;
        ExactRational end = firstEnd.CompareTo(secondEnd) < 0 ? firstEnd : secondEnd;
        return end.CompareTo(start) > 0 ? end - start : ExactRational.Zero;
    }

    private readonly struct ExactRational
    {
        private readonly BigInteger _numerator;
        private readonly BigInteger _denominator;

        internal ExactRational(BigInteger numerator, BigInteger denominator)
        {
            if (denominator.Sign <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(denominator));
            }

            BigInteger divisor = BigInteger.GreatestCommonDivisor(BigInteger.Abs(numerator), denominator);
            _numerator = numerator / divisor;
            _denominator = denominator / divisor;
        }

        internal static ExactRational Zero => new(0, 1);
        internal bool IsZero => _numerator.IsZero;
        internal int CompareTo(ExactRational other) => (_numerator * other._denominator).CompareTo(other._numerator * _denominator);
        internal double ToDouble() => (double)_numerator / (double)_denominator;
        internal ushort Quantize() => checked((ushort)((2 * _numerator * MediaTrackLayoutV1.QuantMax + _denominator) / (2 * _denominator)));

        internal static ExactRational FromFloat(float value) => FromDouble(value);

        internal static ExactRational FromDouble(double value)
        {
            ulong bits = BitConverter.DoubleToUInt64Bits(value);
            int exponent = (int)((bits >> 52) & 0x7FF);
            BigInteger mantissa = bits & 0xF_FFFF_FFFF_FFFF;
            if (exponent != 0)
            {
                mantissa += BigInteger.One << 52;
            }

            int shift = (exponent == 0 ? 1 - 1023 : exponent - 1023) - 52;
            if ((bits >> 63) != 0)
            {
                mantissa = -mantissa;
            }

            return shift >= 0 ? new(mantissa << shift, BigInteger.One) : new(mantissa, BigInteger.One << -shift);
        }

        /// <summary>Adds two exact rational oracle values.</summary>
        public static ExactRational operator +(ExactRational left, ExactRational right) =>
            new(left._numerator * right._denominator + right._numerator * left._denominator, left._denominator * right._denominator);
        /// <summary>Subtracts two exact rational oracle values.</summary>
        public static ExactRational operator -(ExactRational left, ExactRational right) =>
            new(left._numerator * right._denominator - right._numerator * left._denominator, left._denominator * right._denominator);
        /// <summary>Multiplies two exact rational oracle values.</summary>
        public static ExactRational operator *(ExactRational left, ExactRational right) =>
            new(left._numerator * right._numerator, left._denominator * right._denominator);
        /// <summary>Divides two exact rational oracle values.</summary>
        public static ExactRational operator /(ExactRational left, ExactRational right) =>
            new(left._numerator * right._denominator, left._denominator * right._numerator);
    }
}
