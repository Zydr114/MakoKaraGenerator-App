using System.Numerics;

namespace MakoKaraGenerator.Core.Media;

/// <summary>Common exact presentation arithmetic for decoded observations and untrusted canonical records.</summary>
public interface IMediaPresentationTimeMath
{
    /// <summary>Compares exact signed source times without floating-point conversion.</summary>
    /// <param name="left">The first absolute timestamp.</param>
    /// <param name="right">The second absolute timestamp.</param>
    /// <returns>A negative value, zero or a positive value according to the exact ordering.</returns>
    /// <exception cref="MediaAbException">A time base or bounded arithmetic operation is invalid.</exception>
    int Compare(RationalTimestamp left, RationalTimestamp right);

    /// <summary>Computes the mathematical ceiling of the exact delta in microseconds.</summary>
    /// <param name="boundary">The absolute source boundary.</param>
    /// <param name="firstPresentationPts">The first actual source presentation timestamp.</param>
    /// <returns><c>ceil((boundary - firstPresentationPts) * 1000000)</c> as a checked signed 64-bit integer.</returns>
    /// <exception cref="MediaAbException">A time base, intermediate or final result is invalid.</exception>
    long NormalizeBoundaryUs(RationalTimestamp boundary, RationalTimestamp firstPresentationPts);
}

/// <summary>BigInteger implementation with a 192-bit absolute limit after every multiplication and subtraction.</summary>
/// <remarks>Negative deltas retain mathematical ceiling semantics. The interval validator separately refuses negative published boundaries.</remarks>
public sealed class MediaPresentationTimeMath : IMediaPresentationTimeMath
{
    /// <summary>Maximum absolute bit length of each arithmetic intermediate.</summary>
    public const int MaximumIntermediateBits = 192;

    /// <inheritdoc/>
    public int Compare(RationalTimestamp left, RationalTimestamp right)
    {
        Validate(left);
        Validate(right);
        BigInteger leftValue = Multiply(Multiply(left.Ticks, left.TimeBase.Numerator), right.TimeBase.Denominator);
        BigInteger rightValue = Multiply(Multiply(right.Ticks, right.TimeBase.Numerator), left.TimeBase.Denominator);
        return leftValue.CompareTo(rightValue);
    }

    /// <inheritdoc/>
    public long NormalizeBoundaryUs(RationalTimestamp boundary, RationalTimestamp firstPresentationPts)
    {
        Validate(boundary);
        Validate(firstPresentationPts);
        BigInteger left = Multiply(Multiply(boundary.Ticks, boundary.TimeBase.Numerator), firstPresentationPts.TimeBase.Denominator);
        BigInteger right = Multiply(Multiply(firstPresentationPts.Ticks, firstPresentationPts.TimeBase.Numerator), boundary.TimeBase.Denominator);
        BigInteger numerator = Multiply(Check(left - right), 1_000_000);
        BigInteger denominator = Multiply(boundary.TimeBase.Denominator, firstPresentationPts.TimeBase.Denominator);
        BigInteger quotient = BigInteger.DivRem(numerator, denominator, out BigInteger remainder);
        if (remainder.Sign > 0)
        {
            quotient = Check(quotient + BigInteger.One);
        }

        if (quotient < long.MinValue || quotient > long.MaxValue)
        {
            throw new MediaAbException(MediaAbFailure.PresentationInvalid, "Normalized presentation time exceeds the signed microsecond range.");
        }

        return (long)quotient;
    }

    private static void Validate(RationalTimestamp timestamp) =>
        MediaAbRules.ValidateReducedRatio(timestamp.TimeBase.Numerator, timestamp.TimeBase.Denominator, MediaAbFailure.PresentationInvalid);

    private static BigInteger Multiply(BigInteger left, BigInteger right) => Check(left * right);

    private static BigInteger Check(BigInteger value)
    {
        if (BigInteger.Abs(value).GetBitLength() > MaximumIntermediateBits)
        {
            throw new MediaAbException(MediaAbFailure.PresentationInvalid, "Presentation arithmetic exceeds the bounded exact intermediate range.");
        }

        return value;
    }
}
