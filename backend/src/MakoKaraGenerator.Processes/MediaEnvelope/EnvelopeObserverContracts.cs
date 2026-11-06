using System.Threading;
using System.Threading.Tasks;

namespace MakoKaraGenerator.Processes.MediaEnvelope;

/// <summary>Attaches an independent manager observer without pipes or execution authority.</summary>
public interface IEnvelopeObserverBackend
{
    /// <summary>
    /// Verifies a live held owner and its controls using an independent bus reference and cgroup pin.
    /// Before a native context exists, this may throw without retaining resources. After context creation,
    /// every attach failure or cancellation returns that real observer in the result for explicit retention.
    /// The call joins an in-flight native attach even if cancellation is requested meanwhile.
    /// </summary>
    ValueTask<EnvelopeObserverAttachResult> AttachAsync(EnvelopeIdentity expectedIdentity,
        ulong expectedLimitBytes, CancellationToken cancellationToken);
}

/// <summary>
/// Owns the exact observer returned by attach, including failure and cancellation outcomes.
/// A caller must retain it until its own reference is safely released and disposal succeeds.
/// Failure never establishes native absence, drain, pipe EOF, or permission to release source ownership.
/// </summary>
/// <param name="Observer">The real independent bus, immutable expected owner, and original pin.</param>
/// <param name="Failure">Path-free failure; null only for a verified held admission.</param>
public sealed record EnvelopeObserverAttachResult(IEnvelopeObserver Observer, EnvelopeException? Failure)
{
    /// <summary>Whether this attach completed held-owner and controls admission.</summary>
    public bool Attached => Failure is null && Observer.AttachVerified;
}

/// <summary>
/// An independent, identity-bound observer. It owns no native streams and cannot start or release a gate.
/// Dispose does not implicitly Unref: unresolved own Ref or Unref calls keep the context intact and fail safely.
/// The supervisor must keep the same object on every failure until cleanup is proved.
/// </summary>
public interface IEnvelopeObserver : IAsyncDisposable
{
    /// <summary>The complete immutable owner supplied to attach.</summary>
    EnvelopeIdentity ExpectedIdentity { get; }
    /// <summary>Whether the held admission completed; this is historical evidence, not a drain statement.</summary>
    bool AttachVerified { get; }
    /// <summary>Reads manager and the original pinned cgroup, verifying every fixed owner token.</summary>
    ValueTask<EnvelopeSnapshot> ObserveAsync(CancellationToken cancellationToken);
    /// <summary>Rechecks the bound owner and writes only the original pinned cgroup.kill.</summary>
    ValueTask KillVerifiedCgroupAsync(CancellationToken cancellationToken);
    /// <summary>
    /// Rechecks original manager terminal plus original pin empty/removed, then Unrefs this bus's reference.
    /// An ambiguous Ref or Unref reply is retained and is never retried blindly.
    /// </summary>
    ValueTask UnrefOwnReferenceAsync(CancellationToken cancellationToken);
}
