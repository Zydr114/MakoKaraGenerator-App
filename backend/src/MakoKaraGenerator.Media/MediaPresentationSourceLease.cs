using MakoKaraGenerator.Core.Media;

namespace MakoKaraGenerator.Media;

/// <summary>Binds an accepted source descriptor to a real independent hold of its authorized file lease.</summary>
/// <remarks>The importing owner still controls authorization and publication. This wrapper only preserves lifetime and immutable cross-binding for native operations.</remarks>
public sealed class MediaPresentationSourceLease : IMediaSourceLease, IMediaRetainableFileLease
{
    private readonly MediaEnvelopeFileLease _lifetime;

    /// <summary>Acquires a real file hold after validating the complete accepted source and content identity.</summary>
    /// <param name="file">Caller-owned retainable authorized source, left caller-owned.</param>
    /// <param name="source">Accepted immutable inspector result for these exact bytes.</param>
    public MediaPresentationSourceLease(IMediaRetainableFileLease file, MediaFeatureFileSource source)
    {
        ArgumentNullException.ThrowIfNull(file); new MediaAbValueValidator().ValidateSource(source);
        if (file.Content != source.Content) throw new MediaAbException(MediaAbFailure.PresentationMismatch, "Accepted source content does not bind the authorized retained file.");
        Source = source;
        _lifetime = new MediaEnvelopeFileLease(new HeldFile(file.Content, file.TrustedLocalPath, file.AcquireHold()));
    }

    /// <inheritdoc />
    public MediaFeatureFileSource Source { get; }
    /// <inheritdoc />
    public MediaFileContent Content => Source.Content;
    /// <inheritdoc />
    public string TrustedLocalPath => _lifetime.TrustedLocalPath;
    /// <inheritdoc />
    public IAsyncDisposable AcquireHold() => _lifetime.AcquireHold();
    /// <inheritdoc />
    public ValueTask DisposeAsync() => _lifetime.DisposeAsync();

    private sealed class HeldFile(MediaFileContent content, string path, IAsyncDisposable hold) : IMediaFileLease
    {
        public MediaFileContent Content => content;
        public string TrustedLocalPath => path;
        public ValueTask DisposeAsync() => hold.DisposeAsync();
    }
}
