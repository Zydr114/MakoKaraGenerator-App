namespace MakoKaraGenerator.Core.Media;

/// <summary>SDR linear-light premultiplied color without implicit clamping.</summary>
public readonly record struct LinearPremulRgba
{
    /// <summary>Creates a read-only LinearPremulRgba value. Accepted boundaries must validate its complete contents.</summary>
    /// <param name="r">Premultiplied red in the closed interval from zero to alpha.</param>
    /// <param name="g">Premultiplied green in the closed interval from zero to alpha.</param>
    /// <param name="b">Premultiplied blue in the closed interval from zero to alpha.</param>
    /// <param name="a">Alpha in the closed interval from zero to one.</param>
    public LinearPremulRgba(
        double r,
        double g,
        double b,
        double a)
    {
        R = r;
        G = g;
        B = b;
        A = a;
    }

    /// <summary>Premultiplied red in the closed interval from zero to alpha.</summary>
    public double R { get; }

    /// <summary>Premultiplied green in the closed interval from zero to alpha.</summary>
    public double G { get; }

    /// <summary>Premultiplied blue in the closed interval from zero to alpha.</summary>
    public double B { get; }

    /// <summary>Alpha in the closed interval from zero to one.</summary>
    public double A { get; }

}
