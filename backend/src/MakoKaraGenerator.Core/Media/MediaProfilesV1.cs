namespace MakoKaraGenerator.Core.Media;

/// <summary>Versioned normative identifiers; changing their bytes or mathematics requires a new profile.</summary>
public static class MediaProfilesV1
{
    /// <summary>Fixed admission ceilings, including per-manifest entry and UTF-8 text capacities.</summary>
    public const string Capacity = "media-ab-capacity-v1";

    /// <summary>Exact PTS normalization and hold intervals.</summary>
    public const string Presentation = "media-presentation-pts-ceil-us-hold-v1";

    /// <summary>Controlled SDR decoder pipeline.</summary>
    public const string DecoderPipeline = "media-decoder-sdr-rgba32f-v1";

    /// <summary>Linear-light SDR premultiplied RGBA.</summary>
    public const string Color = "media-linear-srgb709-premul-v1";

    /// <summary>Explicit JFIF sRGB and BT.601 JPEG interpretation.</summary>
    public const string JpegInterpretation = "media-jfif-srgb-bt601-full-v1";

    /// <summary>Exact raster quarter turns and rational sample aspect display geometry.</summary>
    public const string Geometry = "media-display-rational-sar-quarterturn-v1";

    /// <summary>Canonical linear premultiplied source-over background composition.</summary>
    public const string BackgroundComposite = "media-background-linear-source-over-v1";

    /// <summary>Seven moments on a 32 by 18 grid.</summary>
    public const string Statistics = "media-moments7-grid32x18-v1";

    /// <summary>Unsigned 16-bit half-up moment quantization.</summary>
    public const string Quantization = "media-moments-u16-half-up-v1";

    /// <summary>Canonical MFT1 artifact format.</summary>
    public const string Codec = "media-feature-track-mft1";

    /// <summary>Source-grid area queries using binary32 coordinates.</summary>
    public const string Query = "media-grid-area-f32-coordinates-v1";

    /// <summary>RGBA32F GPU prefix sampling.</summary>
    public const string Gpu = "media-moments-prefix-rgba32f-v1";

}
