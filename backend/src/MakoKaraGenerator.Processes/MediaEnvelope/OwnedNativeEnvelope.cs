using System;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace MakoKaraGenerator.Processes.MediaEnvelope;

/// <summary>Successful retained native result, available only after proven owned drain.</summary>
/// <param name="Result">Retained native execution result.</param>
/// <param name="ExitCode">Successful tool exit status.</param>
/// <param name="Oom">Whether the kernel or manager reported OOM.</param>
public sealed record EnvelopeCompletion(string Result, int ExitCode, bool Oom)
{
    /// <summary>Path-free diagnostics from a drained Windows Job Object.</summary>
    public WindowsJobMemoryDiagnostics? WindowsJobMemory { get; init; }
}

/// <summary>Runs a gate-held native tool and retains its exact owner through terminal state and pipe EOF.</summary>
/// <remarks>Systemd services are manager children. No broker PID, process ancestry or sampled RSS is an ownership proof.</remarks>
public sealed class OwnedNativeEnvelope
{
    private readonly IEnvelopeBackend _backend;
    private readonly IEnvelopeClock _clock;
    private readonly IEnvelopeQuarantine _quarantine;
    private readonly EnvelopeExecutionPolicy _policy;

    /// <summary>Creates an owner without opening a manager connection or starting a process.</summary>
    public OwnedNativeEnvelope(IEnvelopeBackend backend, IEnvelopeQuarantine quarantine,
        EnvelopeExecutionPolicy policy, IEnvelopeClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(quarantine);
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();
        _backend = backend; _quarantine = quarantine; _policy = policy; _clock = clock ?? new EnvelopeClock();
    }

    /// <summary>Runs one native partition, then proves terminal state, empty native containment and all three output EOFs.</summary>
    /// <param name="reservation">Single-use native partition of an already frozen complete operation plan.</param>
    /// <param name="command">Internal structured command.</param>
    /// <param name="consume">Single-flight callback. It must join its pipe pumps and awaited borrows even after cancellation.</param>
    /// <param name="cancellationToken">Cancels consumption; independent owned cleanup still runs.</param>
    /// <remarks>Consumers must cooperate with cancellation. Never abandon a callback while it holds borrowed memory.</remarks>
    public async Task<EnvelopeCompletion> RunAsync(DecodeOperationBudget.NativeLease reservation,
        EnvelopeCommand command, Func<IEnvelopeSession, CancellationToken, Task> consume,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(consume);
        reservation.ClaimRun();
        IEnvelopeSession? session = null;
        EnvelopeRunOwner? owner = null;
        EnvelopeRunState last = default;
        DecodeOperationBudget.ManagedLease? transport = null;
        bool released = false;
        Exception? primary = null;
        bool consumerFailed = false; CancellationToken actualConsumerToken = default, actualStartToken = default;
        string nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        string unit = "mako-media-" + nonce + ".service";
        try
        {
            command.Validate();
            cancellationToken.ThrowIfCancellationRequested();
            transport = reservation.Operation.ReserveManaged(DecodeOperationBudget.TransportReservationPerProducer);
            session = await _backend.OpenAsync(cancellationToken).ConfigureAwait(false);
            if (session.PipeBackingBytes != DecodeOperationBudget.RequiredPipeBackingPerProducer)
                throw new EnvelopeException(EnvelopeFailure.CapacityUnsupported, "pipe-capacity-profile-unavailable");
            using var startDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            actualStartToken = startDeadline.Token;
            startDeadline.CancelAfter(_policy.StartTimeout);
            await session.StartHeldAsync(new(nonce, unit, checked((ulong)reservation.Bytes), command), startDeadline.Token).ConfigureAwait(false);
            owner = session is IHandleEnvelopeSession handles ? handles.CaptureOwner(startDeadline.Token)
                : CaptureSystemdOwner(session, await AwaitHeldAsync(session, startDeadline.Token).ConfigureAwait(false), nonce, unit);
            await owner.ValidateControlsAsync(checked((ulong)reservation.Bytes), startDeadline.Token).ConfigureAwait(false);
            await session.ReleaseExecGateAsync(owner.WaitForHeldInputEof ? cancellationToken : startDeadline.Token).ConfigureAwait(false);
            released = true;

            using var consumerAbort = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            actualConsumerToken = consumerAbort.Token;
            using var stopSupervision = new CancellationTokenSource();
            Task<EnvelopeException?> supervision = SuperviseAsync(session, owner, consumerAbort,
                cancellationToken, stopSupervision.Token);
            try { await consume(session, consumerAbort.Token).ConfigureAwait(false); }
            catch (Exception error) { primary = error; consumerFailed = true; }
            finally { stopSupervision.Cancel(); }
            EnvelopeException? supervisionFailure = await supervision.ConfigureAwait(false);
            if (supervisionFailure is not null && (primary is null or OperationCanceledException))
            { primary = supervisionFailure; consumerFailed = false; }
            if (primary is null && cancellationToken.IsCancellationRequested) primary = new OperationCanceledException(cancellationToken);
        }
        catch (Exception error) { primary ??= error; }

        if (session is not null)
        {
            using var deadline = new CancellationTokenSource(_policy.DrainTimeout);
            Task? tails = null;
            try
            {
                if (owner is null && session.StartAttempted)
                {
                    // Keep the gate input live until identity capture. GetUnit on
                    // the same ordered connection reconciles an ambiguous reply.
                    if (!await session.ConfirmStartAbsentAsync(deadline.Token).ConfigureAwait(false))
                    {
                        owner = session is IHandleEnvelopeSession handles ? handles.CaptureOwner(deadline.Token)
                            : CaptureSystemdOwner(session, await session.ObserveAsync(deadline.Token).ConfigureAwait(false), nonce, unit);
                    }
                }
                session.Input.Dispose();
                if (owner is not null)
                {
                    // Callback has joined every reader and borrowed consumer.
                    // Start all tail drains before natural exit, so bounded kernel
                    // pipe capacity cannot deadlock a normally finishing tool.
                    tails = Task.WhenAll(DiscardAsync(session.Output, deadline.Token),
                        DiscardAsync(session.Metadata, deadline.Token), DiscardAsync(session.Diagnostics, deadline.Token));
                    last = await owner.ObserveOwnedAsync(deadline.Token).ConfigureAwait(false);
                    if (primary is not null)
                    {
                        // A rejected held gate exits on input EOF without decoder
                        // exec, even if the platform cannot support cgroup.kill.
                        if (!released && owner.WaitForHeldInputEof)
                            last = await AwaitNaturalExitAsync(owner, deadline.Token).ConfigureAwait(false);
                        if (last.Populated || !last.Terminal)
                            await owner.KillOwnedAsync(deadline.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        last = await AwaitNaturalExitAsync(owner, deadline.Token).ConfigureAwait(false);
                        if (!last.Terminal || last.Populated)
                        {
                            primary = new EnvelopeException(EnvelopeFailure.DecodeFailed, "native-natural-exit-timeout");
                            await owner.KillOwnedAsync(deadline.Token).ConfigureAwait(false);
                        }
                    }
                    last = await AwaitDrainedAsync(owner, deadline.Token).ConfigureAwait(false);
                    await tails.ConfigureAwait(false);
                    last = await owner.ReleaseOwnedAsync(last, deadline.Token).ConfigureAwait(false);
                }
                // With no accepted unit, closing also releases unused child pipe
                // endpoints. There are no native references for which to await EOF.
                await session.DisposeAsync().ConfigureAwait(false);
                reservation.MarkDrained();
                transport?.Dispose();
            }
            catch (Exception cleanup)
            {
                // Join canceled tail readers before handing handles to quarantine.
                // Both platforms join their bounded native I/O before returning.
                deadline.Cancel();
                if (tails is not null)
                {
                    try { await tails.ConfigureAwait(false); } catch { }
                }
                if (transport is null)
                    throw new EnvelopeException(EnvelopeFailure.DrainFailed, "native-transport-reservation-missing");
                _quarantine.Retain(new("native-envelope-drain-failed", owner?.LinuxIdentity, session, reservation, transport));
                throw EnvelopeException.WithRetainedCause(EnvelopeFailure.DrainFailed,
                    "owned-unit-or-pipes-not-drained", primary ?? cleanup);
            }
        }
        else
        {
            // Open never starts a native unit. Its backend must close partial resources on failure.
            reservation.MarkDrained();
            transport?.Dispose();
        }

        if (primary is OperationCanceledException canceled)
        {
            if (actualStartToken.CanBeCanceled && canceled.CancellationToken == actualStartToken && actualStartToken.IsCancellationRequested)
            {
                // The start deadline is our own linked token. Classify it only
                // after the exact held owner and all pipes drained successfully.
                cancellationToken.ThrowIfCancellationRequested();
                throw new EnvelopeException(EnvelopeFailure.CapacityUnsupported, "native-gate-start-timeout");
            }
            // This direct consumer token is linked to our caller. Only normalize
            // that known chain, and only after owned drain succeeded above.
            if (cancellationToken.IsCancellationRequested && actualConsumerToken.CanBeCanceled &&
                canceled.CancellationToken == actualConsumerToken)
                throw new OperationCanceledException(cancellationToken);
            ExceptionDispatchInfo.Capture(canceled).Throw();
        }
        // A late caller cancellation cannot replace an already established
        // supervision/ownership/timeout or named consumer failure.
        if (primary is null) cancellationToken.ThrowIfCancellationRequested();
        if (last.MemoryLimitHit)
            throw new EnvelopeException(EnvelopeFailure.CapacityExceeded, "native-memory-envelope-exhausted")
            { WindowsJobMemory = last.WindowsJobMemory };
        if (consumerFailed && primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
        if (primary is EnvelopeException known) throw known;
        if (primary is not null)
            throw new EnvelopeException(EnvelopeFailure.DecodeFailed, "native-consumer-or-transport-failed");
        if (!released || last.Result != "success" || !last.ExitedNormally || last.ExitCode != 0)
            throw new EnvelopeException(EnvelopeFailure.DecodeFailed, "native-decoder-unsuccessful")
            { WindowsJobMemory = last.WindowsJobMemory };
        return new(last.Result, last.ExitCode, last.MemoryLimitHit) { WindowsJobMemory = last.WindowsJobMemory };
    }

    private async Task<EnvelopeException?> SuperviseAsync(IEnvelopeSession session, EnvelopeRunOwner owner,
        CancellationTokenSource consumerAbort, CancellationToken callerToken, CancellationToken stopToken)
    {
        long version = session.ProgressVersion, progressAt = _clock.Timestamp;
        try
        {
            while (!stopToken.IsCancellationRequested)
            {
                if (session.ProgressVersion != version) { version = session.ProgressVersion; progressAt = _clock.Timestamp; }
                bool canceled = callerToken.IsCancellationRequested;
                bool idle = _clock.Elapsed(progressAt) >= _policy.IdleTimeout;
                using var controlDeadline = new CancellationTokenSource(_policy.DrainTimeout);
                EnvelopeRunState observed = await owner.ObserveOwnedAsync(controlDeadline.Token).ConfigureAwait(false);
                bool jobLimit = observed.WindowsJobMemory is not null && observed.MemoryLimitHit;
                if (canceled || idle || jobLimit)
                {
                    if (!observed.Terminal || observed.Populated)
                        await owner.KillOwnedAsync(controlDeadline.Token).ConfigureAwait(false);
                    consumerAbort.Cancel();
                    if (jobLimit && !canceled)
                        return new(EnvelopeFailure.CapacityExceeded, "native-memory-envelope-exhausted");
                    return idle && !canceled ? new(EnvelopeFailure.DecodeFailed, "native-pipe-progress-timeout") : null;
                }
                await _clock.DelayAsync(_policy.PollInterval, stopToken).ConfigureAwait(false);
            }
            return null;
        }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested) { return null; }
        catch (EnvelopeException error)
        {
            consumerAbort.Cancel();
            return error;
        }
        catch
        {
            consumerAbort.Cancel();
            return new(EnvelopeFailure.OwnershipLost, "native-supervision-unavailable");
        }
    }

    private async Task<EnvelopeSnapshot> AwaitHeldAsync(IEnvelopeSession session, CancellationToken token)
    {
        long started = _clock.Timestamp;
        do
        {
            EnvelopeSnapshot value = await session.ObserveAsync(token).ConfigureAwait(false);
            // Type=exec may expose the manager's pre-exec child while still activating.
            // Only active proves that exec of our held gate has completed.
            if (value.ActiveState == "active" && value.MainPid != 0 && value.MainStartTicks != 0 || value.Terminal) return value;
            await _clock.DelayAsync(_policy.PollInterval, token).ConfigureAwait(false);
        } while (_clock.Elapsed(started) < _policy.StartTimeout);
        throw new EnvelopeException(EnvelopeFailure.CapacityUnsupported, "native-gate-start-timeout");
    }

    private async Task<EnvelopeRunState> AwaitNaturalExitAsync(EnvelopeRunOwner owner, CancellationToken token)
    {
        long started = _clock.Timestamp;
        while (true)
        {
            EnvelopeRunState observed = await owner.ObserveOwnedAsync(token).ConfigureAwait(false);
            if (observed.Terminal && !observed.Populated || _clock.Elapsed(started) >= _policy.ExitTimeout
                || observed.WindowsJobMemory is not null && observed.MemoryLimitHit) return observed;
            await _clock.DelayAsync(_policy.PollInterval, token).ConfigureAwait(false);
        }
    }

    private async Task<EnvelopeRunState> AwaitDrainedAsync(EnvelopeRunOwner owner, CancellationToken token)
    {
        long started = _clock.Timestamp;
        while (true)
        {
            EnvelopeRunState observed = await owner.ObserveOwnedAsync(token).ConfigureAwait(false);
            if (observed.Terminal && !observed.Populated && observed.Drainable) return observed;
            if (_clock.Elapsed(started) >= _policy.DrainTimeout)
                throw new EnvelopeException(EnvelopeFailure.DrainFailed, "owned-unit-drain-timeout");
            await _clock.DelayAsync(_policy.PollInterval, token).ConfigureAwait(false);
        }
    }

    private static SystemdEnvelopeRunOwner CaptureSystemdOwner(IEnvelopeSession session, EnvelopeSnapshot value, string nonce, string unit)
        => new(session, value, EnvelopeSnapshotValidator.CaptureOwner(value, nonce, unit));

    internal static void VerifyIdentity(EnvelopeSnapshot value, EnvelopeIdentity identity)
        => EnvelopeSnapshotValidator.VerifyIdentity(value, identity);

    private static async Task DiscardAsync(Stream stream, CancellationToken token)
    {
        byte[] scratch = new byte[4096];
        while (await stream.ReadAsync(scratch, token).ConfigureAwait(false) != 0) { }
    }
}
