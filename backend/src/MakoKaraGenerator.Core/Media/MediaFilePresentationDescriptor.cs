namespace MakoKaraGenerator.Core.Media;

/// <summary>A bounded immutable description of one selected file stream.</summary>
/// <remarks>SAR changes display geometry without resampling or adding a rounded display width to identity. No first PTS or complete frame table is guessed here.</remarks>
public sealed record MediaFilePresentationDescriptor
{
    /// <summary>Creates a read-only MediaFilePresentationDescriptor value. Accepted boundaries must validate its complete contents.</summary>
    /// <param name="streamIndex">The nonnegative selected stream index.</param>
    /// <param name="encodedWidth">The encoded raster width in pixels.</param>
    /// <param name="encodedHeight">The encoded raster height in pixels.</param>
    /// <param name="canonicalWidth">The exact raster width after mirror and quarter turn.</param>
    /// <param name="canonicalHeight">The exact raster height after mirror and quarter turn.</param>
    /// <param name="streamTimeBase">The reduced video time base; absent for a timeless still.</param>
    /// <param name="sarNumerator">The positive reduced sample aspect ratio numerator.</param>
    /// <param name="sarDenominator">The positive reduced sample aspect ratio denominator.</param>
    /// <param name="rotation">The clockwise quarter turn after optional horizontal mirroring.</param>
    /// <param name="mirrorHorizontallyBeforeRotation">Whether to mirror the encoded raster horizontally before rotation.</param>
    /// <param name="color">The fixed native color interpretation.</param>
    /// <param name="digest">The recomputable descriptor digest; excluded from its preimage.</param>
    public MediaFilePresentationDescriptor(
        int streamIndex,
        int encodedWidth,
        int encodedHeight,
        int canonicalWidth,
        int canonicalHeight,
        RationalTimeBase? streamTimeBase,
        int sarNumerator,
        int sarDenominator,
        MediaQuarterTurn rotation,
        bool mirrorHorizontallyBeforeRotation,
        MediaColorInterpretation color,
        MediaDigest digest)
    {
        StreamIndex = streamIndex;
        EncodedWidth = encodedWidth;
        EncodedHeight = encodedHeight;
        CanonicalWidth = canonicalWidth;
        CanonicalHeight = canonicalHeight;
        StreamTimeBase = streamTimeBase;
        SarNumerator = sarNumerator;
        SarDenominator = sarDenominator;
        Rotation = rotation;
        MirrorHorizontallyBeforeRotation = mirrorHorizontallyBeforeRotation;
        Color = color;
        Digest = digest;
    }

    /// <summary>The nonnegative selected stream index.</summary>
    public int StreamIndex { get; }

    /// <summary>The encoded raster width in pixels.</summary>
    public int EncodedWidth { get; }

    /// <summary>The encoded raster height in pixels.</summary>
    public int EncodedHeight { get; }

    /// <summary>The exact raster width after mirror and quarter turn.</summary>
    public int CanonicalWidth { get; }

    /// <summary>The exact raster height after mirror and quarter turn.</summary>
    public int CanonicalHeight { get; }

    /// <summary>The reduced video time base; absent for a timeless still.</summary>
    public RationalTimeBase? StreamTimeBase { get; }

    /// <summary>The positive reduced sample aspect ratio numerator.</summary>
    public int SarNumerator { get; }

    /// <summary>The positive reduced sample aspect ratio denominator.</summary>
    public int SarDenominator { get; }

    /// <summary>The clockwise quarter turn after optional horizontal mirroring.</summary>
    public MediaQuarterTurn Rotation { get; }

    /// <summary>Whether to mirror the encoded raster horizontally before rotation.</summary>
    public bool MirrorHorizontallyBeforeRotation { get; }

    /// <summary>The fixed native color interpretation.</summary>
    public MediaColorInterpretation Color { get; }

    /// <summary>The recomputable descriptor digest; excluded from its preimage.</summary>
    public MediaDigest Digest { get; }

}
