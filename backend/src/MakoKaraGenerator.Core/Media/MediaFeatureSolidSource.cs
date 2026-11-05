namespace MakoKaraGenerator.Core.Media;

/// <summary>A timeless canonical analytic solid with no file, decoder operation or artifact.</summary>
/// <remarks>The complete requested profile is retained; numerical consumers still quantize the seven moments using the fixed u16 profile.</remarks>
public sealed class MediaFeatureSolidSource
{
    private MediaFeatureSolidSource(MediaSourceKey key, MediaProfileBundle profile, LinearPremulRgba color)
    { Key = key; Profile = profile; Color = color; }

    /// <summary>The recomputable solid-domain source identity.</summary>
    public MediaSourceKey Key { get; }
    /// <summary>The complete supported fixed profile.</summary>
    public MediaProfileBundle Profile { get; }
    /// <summary>The exact finite positive-zero canonical premultiplied linear source color.</summary>
    public LinearPremulRgba Color { get; }

    /// <summary>Creates a canonical immutable solid source.</summary>
    /// <param name="profile">The complete supported requested profile, with a valid recomputed digest.</param>
    /// <param name="color">A canonical linear-premultiplied SDR color.</param>
    /// <returns>A validated analytic source with a solid-domain key.</returns>
    public static MediaFeatureSolidSource Create(MediaProfileBundle profile, LinearPremulRgba color)
    {
        var value = new MediaFeatureSolidSource(default, profile, color);
        MediaSourceKey key = MediaFeatureInputIdentityWriter.ComputeSolidSourceKey(value);
        return new(key, profile, color);
    }
}
