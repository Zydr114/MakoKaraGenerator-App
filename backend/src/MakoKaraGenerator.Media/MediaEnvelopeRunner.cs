using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Processes.MediaEnvelope;

namespace MakoKaraGenerator.Media;

/// <summary>One source-retained native invocation under a single caller-owned atomic media reservation.</summary>
public interface IMediaPresentationNativeRunner
{
    /// <summary>Fixed native hard limit chosen by immutable internal tool configuration.</summary>
    long NativeLimitBytes { get; }
    /// <summary>Runs one producer, verifies source bytes before/after, and returns only after all native users drained.</summary>
    Task RunAsync(IMediaRetainableFileLease source, IMediaWorkingSetBudget ownerBudget, EnvelopeCommand command,
        long managedPeakBytes, Func<IEnvelopeSession, DecodeOperationBudget, CancellationToken, Task> consume,
        CancellationToken cancellationToken);
}

/// <summary>Adapts the media profile to the generic hard-envelope mechanism without duplicating its owner ledger.</summary>
/// <remarks>The full plan is atomically reserved once. The Processes budget is a subledger of that reservation.</remarks>
public sealed class MediaEnvelopeRunner : IMediaPresentationNativeRunner
{
    private const int HashScratchBytes = 64 * 1024;
    private static readonly ConditionalWeakTable<IMediaWorkingSetBudget, NativeSlots> Slots = new();
    private readonly OwnedNativeEnvelope _owner;
    private readonly List<RetainedReleaseFailure> _failedReleases = [];
    private readonly object _retentionGate = new();

    /// <summary>Creates an internally configured runner; tools, limits and backend are never author-controlled.</summary>
    public MediaEnvelopeRunner(IEnvelopeBackend backend, IEnvelopeQuarantine quarantine, long nativeLimitBytes,
        IEnvelopeClock? clock = null)
    {
        if (nativeLimitBytes <= 0 || nativeLimitBytes % 4096 != 0
            || nativeLimitBytes >= MediaAbCapacityV1.MaximumDecodeWorkingBytes)
            throw new MediaAbException(MediaAbFailure.CapacityExceeded, "native-partition-invalid");
        NativeLimitBytes = nativeLimitBytes;
        _owner = new(backend, quarantine, new(TimeSpan.FromSeconds(5), MediaAbCapacityV1.PipeProgressTimeout,
            TimeSpan.FromSeconds(2), MediaAbCapacityV1.OwnedDrainTimeout, TimeSpan.FromMilliseconds(10)), clock);
    }

    /// <inheritdoc />
    public long NativeLimitBytes { get; }

    /// <summary>Count of failed source releases whose owner reservation is deliberately retained for supervisor inspection.</summary>
    public int RetainedOperationCount { get { lock (_retentionGate) return _failedReleases.Count; } }

    /// <inheritdoc />
    public async Task RunAsync(IMediaRetainableFileLease source, IMediaWorkingSetBudget ownerBudget,
        EnvelopeCommand command, long managedPeakBytes,
        Func<IEnvelopeSession, DecodeOperationBudget, CancellationToken, Task> consume,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(ownerBudget);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(consume);
        long total;
        try { total = checked(managedPeakBytes + NativeLimitBytes); }
        catch (OverflowException) { throw new MediaAbException(MediaAbFailure.CapacityExceeded, "decode-plan-overflow"); }
        long minimumManaged = DecodeOperationBudget.TransportReservationPerProducer
            + MediaAbCapacityV1.MaximumRetainedDiagnosticBytes + 4096 + HashScratchBytes;
        if (managedPeakBytes < minimumManaged || total > MediaAbCapacityV1.MaximumDecodeWorkingBytes
            || total > ownerBudget.LimitBytes)
            throw new MediaAbException(MediaAbFailure.CapacityExceeded, "decode-plan-exceeds-profile");

        // One atomic charge includes ALL simultaneous native limits and managed
        // capacity. Subledger reservations below do not charge ownerBudget again.
        NativeSlot slot = Slots.GetValue(ownerBudget, _ => new NativeSlots()).Acquire();
        IMediaMemoryReservation ownerReservation;
        try { ownerReservation = ownerBudget.Reserve(total, MediaWorkingSetPurpose.DecoderNative); }
        catch { slot.Dispose(); throw; }
        IAsyncDisposable sourceHold;
        try { sourceHold = source.AcquireHold(); }
        catch { ownerReservation.Dispose(); slot.Dispose(); throw; }
        var lifetime = new SourceAndBudgetHold(sourceHold, ownerReservation, slot);
        DecodeOperationBudget operation;
        try
        {
            operation = new(MediaAbCapacityV1.MaximumDecodeWorkingBytes, managedPeakBytes, [NativeLimitBytes],
                MediaAbCapacityV1.MaximumOwnedDecodeProcesses, lifetime);
        }
        catch { await lifetime.DisposeAsync().ConfigureAwait(false); throw; }

        Exception? failure = null; CancellationToken actualConsumerToken = default;
        try
        {
            await VerifySourceAsync(source, operation, cancellationToken).ConfigureAwait(false);
            await _owner.RunAsync(operation.BeginNative(0), command,
                (session, token) => ConsumeWithDiagnosticsAsync(session, operation,
                    (s, t) => { actualConsumerToken = t; return consume(s, operation, t); }, token), cancellationToken).ConfigureAwait(false);
            await VerifySourceAsync(source, operation, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) { failure = error; }
        try { await operation.ReleaseSourceAsync().ConfigureAwait(false); }
        catch (EnvelopeException release)
        {
            // Native quarantine already holds its operation. Retaining here also
            // covers source-release failures after successful native drain.
            var safeFailure = new MediaAbException(MediaAbFailure.DecodeFailed, "owned-drain-failed");
            // Preserve both the operation failure (including its original consumer
            // cause/token) and source-release failure only in host-private state.
            // The public mapping explicitly retains the named drain fact.
            lock (_retentionGate) _failedReleases.Add(new(operation, failure, release, safeFailure));
            throw safeFailure;
        }
        if (failure is OperationCanceledException canceled)
        {
            // Normalize only our actual linked consumer cancellation after every
            // native user and the source hold have successfully drained. An
            // unrelated consumer token remains exactly the caller's exception.
            if (cancellationToken.IsCancellationRequested && actualConsumerToken.CanBeCanceled &&
                canceled.CancellationToken == actualConsumerToken)
                throw new OperationCanceledException(cancellationToken);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(canceled).Throw();
        }
        if (failure is MediaAbException media) throw media;
        if (failure is EnvelopeException envelope) throw Map(envelope);
        if (failure is not null) throw new MediaAbException(MediaAbFailure.DecodeFailed, "native-operation-failed");
    }

    private static MediaAbException Map(EnvelopeException error) => new(error.Failure switch
    {
        EnvelopeFailure.CapacityUnsupported => MediaAbFailure.CapacityUnsupported,
        EnvelopeFailure.CapacityExceeded => MediaAbFailure.CapacityExceeded,
        _ => MediaAbFailure.DecodeFailed,
    }, error.SafeCode);

    private static async Task ConsumeWithDiagnosticsAsync(IEnvelopeSession session, DecodeOperationBudget budget,
        Func<IEnvelopeSession, CancellationToken, Task> consume, CancellationToken token)
    {
        using var diagnosticReservation = budget.ReserveManaged(MediaAbCapacityV1.MaximumRetainedDiagnosticBytes + 4096);
        byte[] retained = new byte[MediaAbCapacityV1.MaximumRetainedDiagnosticBytes];
        byte[] scratch = new byte[4096];
        // Never decode or print arbitrary native stderr. A bounded byte ring may
        // be used by host-private diagnostics later; it is not an accumulated frame table.
        using var stop = new CancellationTokenSource();
        Task diagnostics = PumpDiagnosticsAsync(session.Diagnostics, retained, scratch, stop.Token);
        using var consumeCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task body;
        try { body = consume(session, consumeCancellation.Token); }
        catch (Exception error) { body = Task.FromException(error); }
        try
        {
            Task first = await Task.WhenAny(body, diagnostics).ConfigureAwait(false);
            if (first == diagnostics && diagnostics.IsFaulted)
            {
                consumeCancellation.Cancel();
                try { await body.ConfigureAwait(false); } catch { }
                await diagnostics.ConfigureAwait(false);
            }
            await body.ConfigureAwait(false);
        }
        finally
        {
            // Join this reader even if the callback threw synchronously. Native
            // pipe cancellation is bounded; RunAsync then owns the only tail reader.
            stop.Cancel();
            try { await diagnostics.ConfigureAwait(false); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }

    private static async Task PumpDiagnosticsAsync(Stream stream, byte[] retained, byte[] scratch, CancellationToken token)
    {
        int position = 0;
        while (true)
        {
            int read = await stream.ReadAsync(scratch, token).ConfigureAwait(false);
            if (read == 0) return;
            for (int i = 0; i < read; i++) { retained[position] = scratch[i]; position = (position + 1) % retained.Length; }
        }
    }

    private static async Task VerifySourceAsync(IMediaFileLease source, DecodeOperationBudget operation, CancellationToken token)
    {
        using var reservation = operation.ReserveManaged(HashScratchBytes);
        byte[] scratch = new byte[HashScratchBytes];
        try
        {
            await using var input = new FileStream(source.TrustedLocalPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (input.Length != source.Content.ByteLength) throw new MediaAbException(MediaAbFailure.SourceChanged, "source-length-changed");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long count = 0;
            while (true)
            {
                int read = await input.ReadAsync(scratch, token).ConfigureAwait(false);
                if (read == 0) break;
                count = checked(count + read);
                if (count > source.Content.ByteLength) throw new MediaAbException(MediaAbFailure.SourceChanged, "source-grew-during-read");
                hash.AppendData(scratch, 0, read);
            }
            string actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (count != source.Content.ByteLength || actual != source.Content.Sha256.LowerHex)
                throw new MediaAbException(MediaAbFailure.SourceChanged, "source-hash-changed");
        }
        catch (OperationCanceledException) { throw; }
        catch (MediaAbException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new MediaAbException(MediaAbFailure.SourceChanged, "source-read-unavailable"); }
    }

    private sealed record RetainedReleaseFailure(DecodeOperationBudget Operation, Exception? PrimaryFailure,
        EnvelopeException ReleaseFailure, MediaAbException SafeMappedFailure);

    private sealed class SourceAndBudgetHold(IAsyncDisposable sourceHold, IMediaMemoryReservation budget, NativeSlot slot) : IAsyncDisposable
    {
        private bool _released;
        public async ValueTask DisposeAsync()
        {
            if (_released) return;
            await sourceHold.DisposeAsync().ConfigureAwait(false);
            budget.Dispose(); slot.Dispose(); _released = true;
        }
    }

    private sealed class NativeSlots
    {
        private readonly object _gate = new();
        private int _live;
        internal NativeSlot Acquire()
        {
            lock (_gate)
            {
                if (_live >= MediaAbCapacityV1.MaximumOwnedDecodeProcesses)
                    throw new MediaAbException(MediaAbFailure.CapacityExceeded, "native-producer-count-exceeded");
                _live++;
                return new(this);
            }
        }
        internal void Release() { lock (_gate) _live--; }
    }

    private sealed class NativeSlot(NativeSlots owner) : IDisposable
    {
        private NativeSlots? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }

}
