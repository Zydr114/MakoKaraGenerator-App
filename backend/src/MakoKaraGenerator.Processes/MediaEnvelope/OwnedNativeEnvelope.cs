using System;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace MakoKaraGenerator.Processes.MediaEnvelope;

/// <summary>Successful retained native result, available only after proven owned drain.</summary>
/// <param name="Result">Manager service result.</param>
/// <param name="ExitCode">Successful tool exit status.</param>
/// <param name="Oom">Whether the kernel or manager reported OOM.</param>
public sealed record EnvelopeCompletion(string Result, int ExitCode, bool Oom);

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

    /// <summary>Runs one native partition, then proves terminal state, empty/removed cgroup and all three output EOFs.</summary>
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
        EnvelopeIdentity? identity = null;
        EnvelopeSnapshot? last = null;
        DecodeOperationBudget.ManagedLease? transport = null;
        bool released = false;
        Exception? primary = null;
        bool consumerFailed = false;
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
            startDeadline.CancelAfter(_policy.StartTimeout);
            await session.StartHeldAsync(new(nonce, unit, checked((ulong)reservation.Bytes), command), startDeadline.Token).ConfigureAwait(false);
            last = await AwaitHeldAsync(session, startDeadline.Token).ConfigureAwait(false);
            // Capture ownership before validating controls: rejected controls must still drain the exact held gate.
            identity = CaptureOwner(last, nonce, unit);
            ValidateControls(last, checked((ulong)reservation.Bytes));
            await session.ReleaseExecGateAsync(cancellationToken).ConfigureAwait(false);
            released = true;

            using var consumerAbort = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using var stopSupervision = new CancellationTokenSource();
            Task<EnvelopeException?> supervision = SuperviseAsync(session, identity, consumerAbort,
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
                if (identity is null && session.StartAttempted)
                {
                    // Keep the gate input live until identity capture. GetUnit on
                    // the same ordered connection reconciles an ambiguous reply.
                    if (!await session.ConfirmStartAbsentAsync(deadline.Token).ConfigureAwait(false))
                    {
                        last = await session.ObserveAsync(deadline.Token).ConfigureAwait(false);
                        identity = CaptureOwner(last, nonce, unit);
                    }
                }
                session.Input.Dispose();
                if (identity is not null)
                {
                    // Callback has joined every reader and borrowed consumer.
                    // Start all tail drains before natural exit, so bounded kernel
                    // pipe capacity cannot deadlock a normally finishing tool.
                    tails = Task.WhenAll(DiscardAsync(session.Output, deadline.Token),
                        DiscardAsync(session.Metadata, deadline.Token), DiscardAsync(session.Diagnostics, deadline.Token));
                    last = await session.ObserveAsync(deadline.Token).ConfigureAwait(false);
                    VerifyIdentity(last, identity);
                    if (primary is not null)
                    {
                        // A rejected held gate exits on input EOF without decoder
                        // exec, even if the platform cannot support cgroup.kill.
                        if (!released) last = await AwaitNaturalExitAsync(session, identity, deadline.Token).ConfigureAwait(false);
                        if (last.CgroupPopulated || !last.Terminal)
                            await session.KillVerifiedCgroupAsync(identity, deadline.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        last = await AwaitNaturalExitAsync(session, identity, deadline.Token).ConfigureAwait(false);
                        if (!last.Terminal || last.CgroupPopulated)
                        {
                            primary = new EnvelopeException(EnvelopeFailure.DecodeFailed, "native-natural-exit-timeout");
                            await session.KillVerifiedCgroupAsync(identity, deadline.Token).ConfigureAwait(false);
                        }
                    }
                    last = await AwaitDrainedAsync(session, identity, deadline.Token).ConfigureAwait(false);
                    await tails.ConfigureAwait(false);
                    await session.UnrefVerifiedAsync(identity, deadline.Token).ConfigureAwait(false);
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
                // Their owned nonblocking I/O polls at most 50ms per native call.
                deadline.Cancel();
                if (tails is not null)
                {
                    try { await tails.ConfigureAwait(false); } catch { }
                }
                if (transport is null)
                    throw new EnvelopeException(EnvelopeFailure.DrainFailed, "native-transport-reservation-missing");
                _quarantine.Retain(new("native-envelope-drain-failed", identity, session, reservation, transport));
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

        if (primary is OperationCanceledException) ExceptionDispatchInfo.Capture(primary).Throw();
        cancellationToken.ThrowIfCancellationRequested();
        if (last?.Oom == true)
            throw new EnvelopeException(EnvelopeFailure.CapacityExceeded, "native-memory-envelope-exhausted");
        if (consumerFailed && primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
        if (primary is EnvelopeException known) throw known;
        if (primary is not null)
            throw new EnvelopeException(EnvelopeFailure.DecodeFailed, "native-consumer-or-transport-failed");
        if (!released || last is null || last.Result != "success" || last.ExecMainCode != 1 || last.ExecMainStatus != 0)
            throw new EnvelopeException(EnvelopeFailure.DecodeFailed, "native-decoder-unsuccessful");
        return new(last.Result, last.ExecMainStatus, last.Oom);
    }

    private async Task<EnvelopeException?> SuperviseAsync(IEnvelopeSession session, EnvelopeIdentity identity,
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
                EnvelopeSnapshot observed = await session.ObserveAsync(controlDeadline.Token).ConfigureAwait(false);
                VerifyIdentity(observed, identity);
                if (canceled || idle)
                {
                    if (!observed.Terminal || observed.CgroupPopulated)
                        await session.KillVerifiedCgroupAsync(identity, controlDeadline.Token).ConfigureAwait(false);
                    consumerAbort.Cancel();
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

    private async Task<EnvelopeSnapshot> AwaitNaturalExitAsync(IEnvelopeSession session, EnvelopeIdentity identity, CancellationToken token)
    {
        long started = _clock.Timestamp;
        while (true)
        {
            EnvelopeSnapshot observed = await session.ObserveAsync(token).ConfigureAwait(false);
            VerifyIdentity(observed, identity);
            if (observed.Terminal && !observed.CgroupPopulated || _clock.Elapsed(started) >= _policy.ExitTimeout) return observed;
            await _clock.DelayAsync(_policy.PollInterval, token).ConfigureAwait(false);
        }
    }

    private async Task<EnvelopeSnapshot> AwaitDrainedAsync(IEnvelopeSession session, EnvelopeIdentity identity, CancellationToken token)
    {
        long started = _clock.Timestamp;
        while (true)
        {
            EnvelopeSnapshot observed = await session.ObserveAsync(token).ConfigureAwait(false);
            VerifyIdentity(observed, identity);
            if (observed.Terminal && !observed.CgroupPopulated && (observed.CgroupExists || observed.CgroupRemoved)) return observed;
            if (_clock.Elapsed(started) >= _policy.DrainTimeout)
                throw new EnvelopeException(EnvelopeFailure.DrainFailed, "owned-unit-drain-timeout");
            await _clock.DelayAsync(_policy.PollInterval, token).ConfigureAwait(false);
        }
    }

    private static void ValidateControls(EnvelopeSnapshot value, ulong limit)
    {
        if (!value.ControlsComplete || !value.KernelKillAvailable || value.MainPid == 0 || value.MainStartTicks == 0 || !value.MainInControlGroup
            || !value.CgroupExists || value.CgroupInode == 0 || value.MemoryMax != limit || value.KernelMemoryMax != limit
            || value.EffectiveMemoryMax == 0 || value.EffectiveMemoryMax > limit || value.MemorySwapMax != 0 || value.KernelSwapMax != 0
            || !value.KernelOomGroup || !value.MemoryAccounting || value.Delegate || value.Type != "exec"
            || value.KillMode != "control-group" || value.Restart != "no" || value.OomPolicy != "kill")
            throw new EnvelopeException(EnvelopeFailure.CapacityUnsupported, "native-envelope-controls-unverified");
    }

    private static EnvelopeIdentity CaptureOwner(EnvelopeSnapshot value, string nonce, string unit)
    {
        if (!IsHex(nonce) || value.UnitName != unit || value.UnitPath.Length == 0
            || value.Description != "Mako media envelope " + nonce || !IsHex(value.InvocationId)
            || value.InvocationId.All(c => c == '0')
            || value.ControlGroup.Length == 0 && !(value.Terminal && value.MainPid == 0 && value.CgroupRemoved)
            || value.ControlGroup.Length != 0 && (!value.ControlGroup.EndsWith('/' + unit, StringComparison.Ordinal) || value.CgroupInode == 0))
            throw new EnvelopeException(EnvelopeFailure.OwnershipLost, "unconfirmed-native-start-owner");
        return new(nonce, unit, value.UnitPath, value.InvocationId, value.ControlGroup, value.MainPid,
            value.MainStartTicks, value.CgroupDevice, value.CgroupInode);
    }

    internal static void VerifyIdentity(EnvelopeSnapshot value, EnvelopeIdentity identity)
    {
        if (value.UnitName != identity.UnitName || value.UnitPath != identity.UnitPath || value.InvocationId != identity.InvocationId
            || value.Description != "Mako media envelope " + identity.Nonce || value.ControlGroup != identity.ControlGroup
            || value.CgroupDevice != identity.CgroupDevice || value.CgroupInode != identity.CgroupInode
            || value.MainPid != 0 && (value.MainPid != identity.MainPid || value.MainStartTicks != identity.StartTicks || !value.MainInControlGroup)
            || value.MainPid == 0 && !value.Terminal && !value.CgroupRemoved)
            throw new EnvelopeException(EnvelopeFailure.OwnershipLost, "native-envelope-owner-changed");
    }

    private static bool IsHex(string value) => value.Length == 32 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static async Task DiscardAsync(Stream stream, CancellationToken token)
    {
        byte[] scratch = new byte[4096];
        while (await stream.ReadAsync(scratch, token).ConfigureAwait(false) != 0) { }
    }
}
