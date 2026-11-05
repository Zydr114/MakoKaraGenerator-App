namespace MakoKaraGenerator.Core.Media;

/// <summary>A positive reduced number of seconds per presentation tick.</summary>
/// <remarks>Construction reduces trusted arithmetic inputs. Canonical wire readers must use <see cref="FromCanonical"/> to reject noncanonical input.</remarks>
public readonly record struct RationalTimeBase
{
    /// <summary>Creates a positive time base and reduces it by its greatest common divisor.</summary>
    /// <param name="numerator">Positive seconds numerator within signed 32-bit range.</param>
    /// <param name="denominator">Positive ticks denominator within signed 32-bit range.</param>
    /// <exception cref="MediaAbException">Either input is not positive.</exception>
    public RationalTimeBase(int numerator, int denominator)
    {
        if (numerator <= 0 || denominator <= 0)
        {
            throw new MediaAbException(MediaAbFailure.PresentationInvalid, "Presentation time base must be positive.");
        }

        int divisor = MediaAbRules.GreatestCommonDivisor(numerator, denominator);
        Numerator = numerator / divisor;
        Denominator = denominator / divisor;
    }

    /// <summary>The positive reduced seconds numerator.</summary>
    public int Numerator { get; }

    /// <summary>The positive reduced ticks denominator.</summary>
    public int Denominator { get; }

    /// <summary>Reads a canonical time base without silently repairing noncanonical wire bytes.</summary>
    /// <param name="numerator">The encoded positive numerator.</param>
    /// <param name="denominator">The encoded positive denominator.</param>
    /// <returns>The exact encoded reduced time base.</returns>
    /// <exception cref="MediaAbException">The encoded base is nonpositive or not reduced.</exception>
    public static RationalTimeBase FromCanonical(int numerator, int denominator)
    {
        MediaAbRules.ValidateReducedRatio(numerator, denominator, MediaAbFailure.PresentationInvalid);
        return new RationalTimeBase(numerator, denominator);
    }
}
