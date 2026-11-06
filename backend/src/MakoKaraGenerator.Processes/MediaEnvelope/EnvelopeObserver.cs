using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MakoKaraGenerator.Processes.MediaEnvelope;

// This internal boundary makes controlled tests execute the real managed ownership
// state machine, without loading a DSO, starting a service, or obtaining a bus.
internal interface IEnvelopeObserverNativeContext
{
    EnvelopeSnapshot Attach();
    EnvelopeSnapshot Observe();
    void KillVerifiedCgroup();
    void UnrefOwnReference();
    void Close();
}

internal sealed class EnvelopeObserver(EnvelopeIdentity expectedIdentity,
    ulong expectedLimit, IEnvelopeObserverNativeContext context) : IEnvelopeObserver
{
    private readonly SemaphoreSlim _control = new(1, 1);
    private bool _disposed;
    private bool _attachVerified;
    public EnvelopeIdentity ExpectedIdentity { get; } = expectedIdentity;
    public bool AttachVerified => Volatile.Read(ref _attachVerified);

    internal async ValueTask<EnvelopeObserverAttachResult> AttachOwnedAsync(CancellationToken token)
    {
        bool entered = false;
        try
        {
            await _control.WaitAsync(token).ConfigureAwait(false);
            entered = true;
            token.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Join the real call. WaitAsync(token) on this Task would lose an
            // accepted late Ref and its only managed ownership handle.
            EnvelopeSnapshot value = await Task.Run(context.Attach).ConfigureAwait(false);
            EnvelopeSnapshotValidator.VerifyIdentity(value, ExpectedIdentity);
            EnvelopeSnapshotValidator.ValidateControls(value, expectedLimit);
            if (value.ActiveState != "active")
                throw new EnvelopeException(EnvelopeFailure.CapacityUnsupported, "native-observer-gate-not-held");
            Volatile.Write(ref _attachVerified, true);
            token.ThrowIfCancellationRequested();
            return new(this, null);
        }
        catch (Exception error)
        {
            // The exact observer survives cancellation, marshalling failure,
            // safe native errors, and post-Ref validation failure alike.
            return new(this, SafeAttachFailure(error));
        }
        finally { if (entered) _control.Release(); }
    }

    private async ValueTask<T> CallAsync<T>(Func<T> action, CancellationToken token)
    {
        await _control.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await Task.Run(action).ConfigureAwait(false);
        }
        finally { _control.Release(); }
    }

    public ValueTask<EnvelopeSnapshot> ObserveAsync(CancellationToken cancellationToken)
        => CallAsync(() =>
        {
            EnvelopeSnapshot value = context.Observe();
            EnvelopeSnapshotValidator.VerifyIdentity(value, ExpectedIdentity);
            return value;
        }, cancellationToken);

    public async ValueTask KillVerifiedCgroupAsync(CancellationToken cancellationToken)
    {
        await CallAsync(() =>
        {
            EnvelopeSnapshot value = context.Observe();
            EnvelopeSnapshotValidator.VerifyIdentity(value, ExpectedIdentity);
            context.KillVerifiedCgroup();
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask UnrefOwnReferenceAsync(CancellationToken cancellationToken)
    {
        await CallAsync(() =>
        {
            EnvelopeSnapshot value = context.Observe();
            EnvelopeSnapshotValidator.VerifyIdentity(value, ExpectedIdentity);
            EnvelopeSnapshotValidator.RequireDrained(value);
            context.UnrefOwnReference();
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _control.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            // Close itself refuses unresolved own Ref. If it fails, neither
            // _disposed nor the native context changes: this same object remains
            // usable and must stay in its supervisor's retained ownership set.
            await Task.Run(context.Close).ConfigureAwait(false);
            _disposed = true;
        }
        finally { _control.Release(); }
    }

    private static EnvelopeException SafeAttachFailure(Exception error)
        => error is EnvelopeException envelope ? envelope
            : EnvelopeException.WithRetainedCause(
                error is OperationCanceledException ? EnvelopeFailure.DrainFailed : EnvelopeFailure.CapacityUnsupported,
                error is OperationCanceledException ? "native-observer-attach-canceled"
                    : error is IOException or UnauthorizedAccessException ? "native-observer-transport-unavailable"
                    : "native-observer-attach-failed", error);
}
