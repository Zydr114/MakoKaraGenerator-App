using System;
using System.Threading.Tasks;
using MakoKaraGenerator.Core.Media;

namespace MakoKaraGenerator.Media;

/// <summary>A caller-owned source lease that can lend an independent lifetime hold to a native operation.</summary>
public interface IMediaRetainableFileLease : IMediaFileLease
{
    /// <summary>Acquires a real source lifetime hold; the hold must survive native quarantine.</summary>
    IAsyncDisposable AcquireHold();
}

/// <summary>Reference-counts a transferred real source lease without reconstructing ownership from a path.</summary>
/// <remarks>C transfers its authorized lease here once. Inspector/decoder APIs then borrow this wrapper.</remarks>
public sealed class MediaEnvelopeFileLease : IMediaRetainableFileLease
{
    private readonly object _gate = new();
    private readonly IMediaFileLease _source;
    private int _references = 1;
    private bool _ownerReleased;
    private Task? _finalRelease;

    /// <summary>Consumes the real authorized source lease after validating its passive identity.</summary>
    public MediaEnvelopeFileLease(IMediaFileLease transferredSource)
    {
        ArgumentNullException.ThrowIfNull(transferredSource);
        _source = transferredSource;
        Content = transferredSource.Content;
        TrustedLocalPath = transferredSource.TrustedLocalPath;
    }

    /// <summary>Immutable source byte identity supplied by the transferred lease.</summary>
    public MediaFileContent Content { get; }
    /// <summary>Host-private authorized path. Never serialize it to authors, HTTP or diagnostics.</summary>
    public string TrustedLocalPath { get; }

    /// <inheritdoc />
    public IAsyncDisposable AcquireHold()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_ownerReleased, this);
            _references = checked(_references + 1);
            return new Hold(this);
        }
    }

    /// <summary>Releases the caller reference; outstanding native holds keep the real source lease alive.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_ownerReleased) return _finalRelease is null ? ValueTask.CompletedTask : new(_finalRelease);
            _ownerReleased = true;
            return DropLocked();
        }
    }

    private ValueTask DropLocked()
    {
        _references--;
        if (_references != 0) return ValueTask.CompletedTask;
        _finalRelease ??= ReleaseRealSourceAsync();
        return new(_finalRelease);
    }

    private async Task ReleaseRealSourceAsync()
    {
        try { await _source.DisposeAsync().ConfigureAwait(false); }
        catch { throw new MediaAbException(MediaAbFailure.DecodeFailed, "source-lease-release-failed"); }
    }

    private sealed class Hold(MediaEnvelopeFileLease owner) : IAsyncDisposable
    {
        private bool _released;
        public ValueTask DisposeAsync()
        {
            lock (owner._gate)
            {
                if (_released) return owner._finalRelease is null ? ValueTask.CompletedTask : new(owner._finalRelease);
                _released = true;
                return owner.DropLocked();
            }
        }
    }
}
