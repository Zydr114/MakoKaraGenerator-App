namespace MakoKaraGenerator.Core.Media;

/// <summary>The immutable feature-input state; available file and solid values have distinct branches.</summary>
public enum MediaInputState : uint
{
    /// <summary>A fixed preparation attempt is not yet available.</summary>
    Pending = 0,
    /// <summary>A fixed validated file artifact or analytic solid is available.</summary>
    Available = 1,
    /// <summary>A fixed preparation attempt failed.</summary>
    Unavailable = 2,
    /// <summary>The output has no background contribution by an explicit policy.</summary>
    NoMedia = 3,
}

/// <summary>The fixed output background policy, independent of SDK or a renderer implementation.</summary>
public enum MediaOutputMode : uint
{
    /// <summary>Ordinary output includes its fixed background contribution.</summary>
    Normal = 0,
    /// <summary>Transparent output must not prepare hidden background media.</summary>
    Transparent = 1,
}

/// <summary>An explicit reason for absence of background contribution.</summary>
public enum MediaNoMediaReason : uint
{
    /// <summary>Transparent output suppresses background contribution before probing.</summary>
    TransparentOutput = 0,
    /// <summary>A captured normal-output policy explicitly has no background contribution.</summary>
    NoBackgroundContribution = 1,
}

/// <summary>A canonical domain-separated fixed-input digest.</summary>
/// <param name="Digest">The complete fixed-input identity digest.</param>
public readonly record struct MediaInputRevision(MediaDigest Digest);
/// <summary>A canonical domain-separated display mapping digest.</summary>
/// <param name="Digest">The complete mapping identity digest.</param>
public readonly record struct MediaMappingKey(MediaDigest Digest);
/// <summary>An owner-issued immutable preparation-attempt identity; a retry uses a new value.</summary>
/// <param name="Digest">The fixed preparation-attempt digest.</param>
public readonly record struct MediaPreparationRequestId(MediaDigest Digest);
/// <summary>An owner-issued identity of a fixed failure record.</summary>
/// <param name="Digest">The stable failure-record digest.</param>
public readonly record struct MediaFailureId(MediaDigest Digest);

/// <summary>A fixed failure identity and safe diagnostic; human detail is excluded from input identity.</summary>
public sealed class MediaFailure
{
    private MediaFailure(MediaFailureId id, string code, string safeDetail)
    { Id = id; Code = code; SafeDetail = safeDetail; }

    /// <summary>The stable identity of this failure occurrence.</summary>
    public MediaFailureId Id { get; }
    /// <summary>A bounded ASCII failure code without paths or controls.</summary>
    public string Code { get; }
    /// <summary>Bounded safe human detail, never raw process output or a host path.</summary>
    public string SafeDetail { get; }

    /// <summary>Creates a fixed failure record without granting source or resource authority.</summary>
    /// <param name="id">The owner-issued valid failure digest.</param>
    /// <param name="code">One through 64 ASCII letters, digits, dots, underscores or hyphens.</param>
    /// <param name="safeDetail">Nonblank canonical text of at most 1024 characters without controls or path delimiters.</param>
    /// <returns>A validated immutable failure.</returns>
    public static MediaFailure Create(MediaFailureId id, string code, string safeDetail)
    {
        var value = new MediaFailure(id, code, safeDetail);
        MediaFeatureInputRules.ValidateFailure(value);
        return value;
    }
}
