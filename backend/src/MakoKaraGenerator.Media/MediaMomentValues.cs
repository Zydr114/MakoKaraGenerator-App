namespace MakoKaraGenerator.Media;

/// <summary>Seven unquantized raw means; no variance or unpremultiplication is implicit.</summary>
public readonly record struct MediaMomentValues
{
    /// <summary>Creates an immutable numerical value; accepted boundaries validate all fields.</summary>
    /// <param name="rp">Mean linear premultiplied red.</param>
    /// <param name="gp">Mean linear premultiplied green.</param>
    /// <param name="bp">Mean linear premultiplied blue.</param>
    /// <param name="alpha">Mean alpha.</param>
    /// <param name="ypSquared">Mean of squared premultiplied luminance, computed before averaging.</param>
    /// <param name="alphaYp">Mean of alpha times premultiplied luminance, computed before averaging.</param>
    /// <param name="alphaSquared">Mean of squared alpha, computed before averaging.</param>
    public MediaMomentValues(
        double rp,
        double gp,
        double bp,
        double alpha,
        double ypSquared,
        double alphaYp,
        double alphaSquared)
    {
        Rp = rp;
        Gp = gp;
        Bp = bp;
        Alpha = alpha;
        YpSquared = ypSquared;
        AlphaYp = alphaYp;
        AlphaSquared = alphaSquared;
    }

    /// <summary>Mean linear premultiplied red.</summary>
    public double Rp { get; }

    /// <summary>Mean linear premultiplied green.</summary>
    public double Gp { get; }

    /// <summary>Mean linear premultiplied blue.</summary>
    public double Bp { get; }

    /// <summary>Mean alpha.</summary>
    public double Alpha { get; }

    /// <summary>Mean of squared premultiplied luminance, computed before averaging.</summary>
    public double YpSquared { get; }

    /// <summary>Mean of alpha times premultiplied luminance, computed before averaging.</summary>
    public double AlphaYp { get; }

    /// <summary>Mean of squared alpha, computed before averaging.</summary>
    public double AlphaSquared { get; }

}
