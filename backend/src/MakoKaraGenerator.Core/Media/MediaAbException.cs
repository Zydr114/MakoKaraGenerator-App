namespace MakoKaraGenerator.Core.Media;

/// <summary>Named failures shared by media preparation and artifact validation.</summary>
/// <remarks>Cancellation is always <see cref="OperationCanceledException"/> and is never translated into this enum.</remarks>
public enum MediaAbFailure
{
    /// <summary>The authorized source bytes changed.</summary>
    SourceChanged = 0,
    /// <summary>The requested profile or controlled tool closure is unsupported.</summary>
    ProfileUnsupported = 1,
    /// <summary>The declared color interpretation is unsupported or contradictory.</summary>
    ColorUnsupported = 2,
    /// <summary>Presentation time, raster geometry or stream metadata is invalid.</summary>
    PresentationInvalid = 3,
    /// <summary>Decoded presentation observations disagree with the accepted index.</summary>
    PresentationMismatch = 4,
    /// <summary>The controlled decoder failed.</summary>
    DecodeFailed = 5,
    /// <summary>A supported operation exceeds a fixed capacity ceiling.</summary>
    CapacityExceeded = 6,
    /// <summary>The implementation cannot prove or enforce its required resource envelope.</summary>
    CapacityUnsupported = 7,
    /// <summary>Canonical artifact bytes or identity metadata is invalid.</summary>
    ArtifactInvalid = 8,
    /// <summary>Canonical pixel statistics are invalid.</summary>
    StatisticsInvalid = 9,
}

/// <summary>A named media failure carrying bounded host-independent diagnostic text.</summary>
public sealed class MediaAbException : Exception
{
    /// <summary>Creates a named failure with a safe static detail.</summary>
    /// <param name="failure">A defined shared failure category.</param>
    /// <param name="safeDetail">Nonblank text of at most 1024 characters without control characters or path delimiters.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="failure"/> is undefined.</exception>
    /// <exception cref="ArgumentException"><paramref name="safeDetail"/> is not safe bounded diagnostic text.</exception>
    public MediaAbException(MediaAbFailure failure, string safeDetail)
        : base(ValidateDetail(failure, safeDetail))
    {
        Failure = failure;
        SafeDetail = safeDetail;
    }

    /// <summary>The stable shared failure category.</summary>
    public MediaAbFailure Failure { get; }

    /// <summary>The bounded detail, which contains no host path or original tool exception message.</summary>
    public string SafeDetail { get; }

    private static string ValidateDetail(MediaAbFailure failure, string safeDetail)
    {
        if (!Enum.IsDefined(failure))
        {
            throw new ArgumentOutOfRangeException(nameof(failure));
        }

        if (string.IsNullOrWhiteSpace(safeDetail) || safeDetail.Length > 1024 ||
            safeDetail.Any(char.IsControl) || safeDetail.Contains('/') || safeDetail.Contains('\\') ||
            safeDetail.Contains(':'))
        {
            throw new ArgumentException("Media failure detail must be bounded and contain no path delimiters or controls.", nameof(safeDetail));
        }

        return safeDetail;
    }
}
