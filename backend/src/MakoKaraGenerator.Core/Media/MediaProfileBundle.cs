namespace MakoKaraGenerator.Core.Media;

/// <summary>The exact supported profile bundle bound to tool and color identities.</summary>
public sealed record MediaProfileBundle
{
    /// <summary>Creates a read-only MediaProfileBundle value. Accepted boundaries must validate its complete contents.</summary>
    /// <param name="presentation">The presentation-time profile.</param>
    /// <param name="decoderPipeline">The controlled decoder pipeline profile.</param>
    /// <param name="color">The canonical linear premultiplied color profile.</param>
    /// <param name="statistics">The spatial moment statistics profile.</param>
    /// <param name="quantization">The moment quantization profile.</param>
    /// <param name="codec">The canonical feature artifact format.</param>
    /// <param name="query">The source-grid query profile.</param>
    /// <param name="gpu">The GPU prefix sampling profile.</param>
    /// <param name="decoderIdentity">The tool composite digest bound to this profile.</param>
    /// <param name="colorInterpretation">The color interpretation digest bound to this profile.</param>
    /// <param name="digest">The recomputable bundle digest; excluded from its preimage.</param>
    public MediaProfileBundle(
        string presentation,
        string decoderPipeline,
        string color,
        string statistics,
        string quantization,
        string codec,
        string query,
        string gpu,
        MediaDigest decoderIdentity,
        MediaDigest colorInterpretation,
        MediaDigest digest)
    {
        Presentation = presentation;
        DecoderPipeline = decoderPipeline;
        Color = color;
        Statistics = statistics;
        Quantization = quantization;
        Codec = codec;
        Query = query;
        Gpu = gpu;
        DecoderIdentity = decoderIdentity;
        ColorInterpretation = colorInterpretation;
        Digest = digest;
    }

    /// <summary>The presentation-time profile.</summary>
    public string Presentation { get; }

    /// <summary>The controlled decoder pipeline profile.</summary>
    public string DecoderPipeline { get; }

    /// <summary>The canonical linear premultiplied color profile.</summary>
    public string Color { get; }

    /// <summary>The spatial moment statistics profile.</summary>
    public string Statistics { get; }

    /// <summary>The moment quantization profile.</summary>
    public string Quantization { get; }

    /// <summary>The canonical feature artifact format.</summary>
    public string Codec { get; }

    /// <summary>The source-grid query profile.</summary>
    public string Query { get; }

    /// <summary>The GPU prefix sampling profile.</summary>
    public string Gpu { get; }

    /// <summary>The tool composite digest bound to this profile.</summary>
    public MediaDigest DecoderIdentity { get; }

    /// <summary>The color interpretation digest bound to this profile.</summary>
    public MediaDigest ColorInterpretation { get; }

    /// <summary>The recomputable bundle digest; excluded from its preimage.</summary>
    public MediaDigest Digest { get; }

}
