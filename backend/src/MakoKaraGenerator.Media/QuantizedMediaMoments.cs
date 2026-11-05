namespace MakoKaraGenerator.Media;

/// <summary>Seven raw means quantized by unsigned 16-bit half-up rounding; wire padding belongs to the codec.</summary>
public readonly record struct QuantizedMediaMoments
{
    /// <summary>Creates an immutable numerical value; accepted boundaries validate all fields.</summary>
    /// <param name="rp">Mean linear premultiplied red.</param>
    /// <param name="gp">Mean linear premultiplied green.</param>
    /// <param name="bp">Mean linear premultiplied blue.</param>
    /// <param name="alpha">Mean alpha.</param>
    /// <param name="ypSquared">Mean of squared premultiplied luminance, computed before averaging.</param>
    /// <param name="alphaYp">Mean of alpha times premultiplied luminance, computed before averaging.</param>
    /// <param name="alphaSquared">Mean of squared alpha, computed before averaging.</param>
    public QuantizedMediaMoments(
        ushort rp,
        ushort gp,
        ushort bp,
        ushort alpha,
        ushort ypSquared,
        ushort alphaYp,
        ushort alphaSquared)
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
    public ushort Rp { get; }

    /// <summary>Mean linear premultiplied green.</summary>
    public ushort Gp { get; }

    /// <summary>Mean linear premultiplied blue.</summary>
    public ushort Bp { get; }

    /// <summary>Mean alpha.</summary>
    public ushort Alpha { get; }

    /// <summary>Mean of squared premultiplied luminance, computed before averaging.</summary>
    public ushort YpSquared { get; }

    /// <summary>Mean of alpha times premultiplied luminance, computed before averaging.</summary>
    public ushort AlphaYp { get; }

    /// <summary>Mean of squared alpha, computed before averaging.</summary>
    public ushort AlphaSquared { get; }

}
