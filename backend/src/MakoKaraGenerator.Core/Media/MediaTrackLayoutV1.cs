namespace MakoKaraGenerator.Core.Media;

/// <summary>Canonical MFT1 section sizes shared by capacity admission and the later codec.</summary>
/// <remarks>These are wire constants, never CLR structure sizes. A file implementation must forward to this single source of layout truth.</remarks>
public static class MediaTrackLayoutV1
{
    /// <summary>Complete fixed artifact header size.</summary>
    public const int HeaderBytes = 256;
    /// <summary>One canonical presentation interval record size.</summary>
    public const int IntervalBytes = 64;
    /// <summary>Horizontal statistics cells.</summary>
    public const int Columns = 32;
    /// <summary>Vertical statistics cells.</summary>
    public const int Rows = 18;
    /// <summary>Total statistics cells.</summary>
    public const int Cells = Columns * Rows;
    /// <summary>Number of stored moments per cell.</summary>
    public const int Moments = 7;
    /// <summary>Wire bytes per quantized cell including canonical padding.</summary>
    public const int CellBytes = 16;
    /// <summary>Wire bytes per complete frame grid.</summary>
    public const int FrameGridBytes = Cells * CellBytes;
    /// <summary>Maximum unsigned 16-bit quantized moment value.</summary>
    public const int QuantMax = 65_535;
    /// <summary>Number of frames per temporal prefix block.</summary>
    public const int BlockFrames = 64;
    /// <summary>Wire bytes for a prefix entry including its exact integer cell sums.</summary>
    public const int PrefixEntryBytes = 16 + Cells * Moments * sizeof(ulong);

    /// <summary>Computes the exact prefix count for a canonical track.</summary>
    /// <param name="kind">A file source family.</param>
    /// <param name="frameCount">One for stills, or a bounded positive video frame count.</param>
    /// <returns>Zero for a still; the zero prefix plus one prefix per complete or final partial video block.</returns>
    /// <exception cref="MediaAbException">The family or frame count is invalid.</exception>
    public static long GetPrefixCount(MediaSourceKind kind, long frameCount)
    {
        ValidateCount(kind, frameCount);
        return kind == MediaSourceKind.Still ? 0 : (frameCount + BlockFrames - 1) / BlockFrames + 1;
    }

    /// <summary>Computes and admits the exact complete canonical artifact byte length.</summary>
    /// <param name="kind">A file source family.</param>
    /// <param name="frameCount">The exact presentation count.</param>
    /// <returns>The header plus interval, grid and prefix sections.</returns>
    /// <exception cref="MediaAbException">The input is invalid or exceeds an artifact capacity ceiling.</exception>
    public static long GetArtifactBytes(MediaSourceKind kind, long frameCount)
    {
        long prefixes = GetPrefixCount(kind, frameCount);
        long bytes = checked(HeaderBytes + frameCount * IntervalBytes + frameCount * FrameGridBytes + prefixes * PrefixEntryBytes);
        if (bytes > MediaAbCapacityV1.MaximumArtifactBytes)
        {
            throw new MediaAbException(MediaAbFailure.CapacityExceeded, "Canonical artifact exceeds the fixed byte limit.");
        }

        return bytes;
    }

    /// <summary>Computes and admits simultaneous scratch sections plus the final candidate.</summary>
    /// <param name="kind">A file source family.</param>
    /// <param name="frameCount">The exact presentation count.</param>
    /// <returns>Twice the artifact size minus its single header, since section spools have no header.</returns>
    /// <exception cref="MediaAbException">The input is invalid or exceeds a fixed capacity ceiling.</exception>
    public static long GetScratchAndCandidateBytes(MediaSourceKind kind, long frameCount)
    {
        long bytes = checked(2 * GetArtifactBytes(kind, frameCount) - HeaderBytes);
        if (bytes > MediaAbCapacityV1.MaximumScratchAndCandidateBytes)
        {
            throw new MediaAbException(MediaAbFailure.CapacityExceeded, "Scratch and candidate exceed the fixed combined byte limit.");
        }

        return bytes;
    }

    private static void ValidateCount(MediaSourceKind kind, long frameCount)
    {
        if (kind is not (MediaSourceKind.Still or MediaSourceKind.Video))
        {
            throw new MediaAbException(MediaAbFailure.ArtifactInvalid, "Only still and video sources have canonical file tracks.");
        }

        if (frameCount <= 0 || (kind == MediaSourceKind.Still && frameCount != 1))
        {
            throw new MediaAbException(MediaAbFailure.ArtifactInvalid, "Canonical presentation count is invalid for the source family.");
        }

        if (frameCount > MediaAbCapacityV1.MaximumFrames)
        {
            throw new MediaAbException(MediaAbFailure.CapacityExceeded, "Canonical presentation count exceeds the fixed limit.");
        }
    }
}
