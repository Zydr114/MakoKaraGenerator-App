using System.Globalization;
using MakoKaraGenerator.Processes.MediaEnvelope;

namespace M10.Presentation.Probe;

internal sealed class AuditedEnvelopeBackend(IEnvelopeBackend backend, EvidenceJournal journal, string scenario,
    ulong expectedLimit, bool exerciseWrongIdentity = false) : IEnvelopeBackend
{
    internal Session? Opened { get; private set; }

    public async ValueTask<IEnvelopeSession> OpenAsync(CancellationToken cancellationToken)
    {
        Session value = new(await backend.OpenAsync(cancellationToken).ConfigureAwait(false), journal, scenario,
            expectedLimit, exerciseWrongIdentity);
        Opened = value;
        journal.Write(scenario, "transport-open", new { value.PipeBackingBytes });
        return value;
    }

    internal sealed class Session(IEnvelopeSession inner, EvidenceJournal journal, string scenario,
        ulong expectedLimit, bool exerciseWrongIdentity) : IEnvelopeSession
    {
        private EnvelopeStart? _start;
        internal EnvelopeSnapshot? Held { get; private set; }
        internal EnvelopeSnapshot? Last { get; private set; }
        internal EnvelopeIdentity? Identity { get; private set; }
        internal bool GateReleased { get; private set; }
        internal bool Unreferenced { get; private set; }
        internal bool Disposed { get; private set; }
        internal int KillRequests { get; private set; }
        internal int RejectedIdentityRequests { get; private set; }
        internal TaskCompletionSource KillCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Stream Input => inner.Input;
        public Stream Output => inner.Output;
        public Stream Metadata => inner.Metadata;
        public Stream Diagnostics => inner.Diagnostics;
        public long PipeBackingBytes => inner.PipeBackingBytes;
        public long ProgressVersion => inner.ProgressVersion;
        public bool StartAttempted => inner.StartAttempted;

        public async ValueTask StartHeldAsync(EnvelopeStart start, CancellationToken cancellationToken)
        {
            _start = start;
            journal.Write(scenario, "start-request", new { start.Nonce, start.UnitName, start.NativeLimitBytes });
            await inner.StartHeldAsync(start, cancellationToken).ConfigureAwait(false);
            journal.Write(scenario, "start-accepted");
        }

        public async ValueTask<EnvelopeSnapshot> ObserveAsync(CancellationToken cancellationToken)
        {
            EnvelopeSnapshot value = await inner.ObserveAsync(cancellationToken).ConfigureAwait(false);
            Last = value;
            journal.Write(scenario, "owner-snapshot", value);
            return value;
        }

        public async ValueTask<bool> ConfirmStartAbsentAsync(CancellationToken cancellationToken)
        {
            bool absent = await inner.ConfirmStartAbsentAsync(cancellationToken).ConfigureAwait(false);
            journal.Write(scenario, "start-reconciled", new { absent });
            return absent;
        }

        public async ValueTask ReleaseExecGateAsync(CancellationToken cancellationToken)
        {
            EnvelopeStart start = _start ?? throw new ProbeFailure("probe-start-missing");
            Held = await ObserveAsync(cancellationToken).ConfigureAwait(false);
            RequireHeldControls(Held, start, expectedLimit);
            Identity = Capture(Held, start.Nonce);
            string executable = new FileInfo("/proc/" + Held.MainPid.ToString(CultureInfo.InvariantCulture) + "/exe")
                .ResolveLinkTarget(returnFinalTarget: false)?.FullName ?? throw new ProbeFailure("probe-gate-executable-unreadable");
            if (Path.GetFullPath(executable) != Path.GetFullPath(start.Command.GateExecutable))
                throw new ProbeFailure("probe-real-tool-execed-before-gate-release");
            journal.Write(scenario, "gate-held-kernel-controls-proven", new { identity = Identity, snapshot = Held,
                process = ReadProc(Held.MainPid) });
            // The gate must not have execed the helper or emitted any of its
            // stdout/metadata/diagnostics. Each timed read must cancel with no byte.
            await Task.WhenAll(ProveNoByteAsync(Output, "stdout", cancellationToken),
                ProveNoByteAsync(Metadata, "metadata", cancellationToken),
                ProveNoByteAsync(Diagnostics, "stderr", cancellationToken)).ConfigureAwait(false);
            if (exerciseWrongIdentity) await RejectWrongIdentitiesAsync(Identity, cancellationToken).ConfigureAwait(false);
            await inner.ReleaseExecGateAsync(cancellationToken).ConfigureAwait(false);
            GateReleased = true;
            journal.Write(scenario, "gate-released", Identity);
        }

        private async Task ProveNoByteAsync(Stream stream, string pipe, CancellationToken caller)
        {
            using var duration = CancellationTokenSource.CreateLinkedTokenSource(caller);
            duration.CancelAfter(TimeSpan.FromMilliseconds(100));
            byte[] one = new byte[1];
            try
            {
                int read = await stream.ReadAsync(one, duration.Token).ConfigureAwait(false);
                journal.Write(scenario, "gate-unexpected-output", new { pipe, read });
                throw new ProbeFailure("probe-output-before-gate-release");
            }
            catch (OperationCanceledException) when (!caller.IsCancellationRequested)
            { journal.Write(scenario, "gate-pipe-quiet", new { pipe }); }
        }

        private async Task RejectWrongIdentitiesAsync(EnvelopeIdentity identity, CancellationToken token)
        {
            // These are deliberately incorrect tokens for the actual held owner.
            // No fake manager result is substituted. The backend must reject each
            // token before opening/writing the pinned cgroup.kill control.
            EnvelopeIdentity[] incorrect =
            [
                identity with { InvocationId = identity.InvocationId[0] == 'a' ? new string('b', 32) : new string('a', 32) },
                identity with { MainPid = identity.MainPid + 1 },
                identity with { StartTicks = identity.StartTicks + 1 },
                identity with { CgroupInode = identity.CgroupInode + 1 },
            ];
            string[] labels = ["invocation", "pid", "start-ticks", "cgroup-inode"];
            for (int index = 0; index < incorrect.Length; index++)
            {
                try { await inner.KillVerifiedCgroupAsync(incorrect[index], token).ConfigureAwait(false); }
                catch (EnvelopeException error) when (error.Failure == EnvelopeFailure.OwnershipLost)
                {
                    RejectedIdentityRequests++;
                    journal.Write(scenario, "wrong-owner-token-rejected", new { field = labels[index], error.SafeCode });
                    EnvelopeSnapshot after = await ObserveAsync(token).ConfigureAwait(false);
                    if (after.MainPid != identity.MainPid || after.MainStartTicks != identity.StartTicks
                        || after.InvocationId != identity.InvocationId || !after.CgroupPopulated)
                        throw new ProbeFailure("probe-held-owner-changed-after-rejection");
                    continue;
                }
                throw new ProbeFailure("probe-wrong-owner-token-was-accepted");
            }
        }

        public async ValueTask KillVerifiedCgroupAsync(EnvelopeIdentity identity, CancellationToken cancellationToken)
        {
            KillRequests++;
            journal.Write(scenario, "owned-kill-request", identity);
            await inner.KillVerifiedCgroupAsync(identity, cancellationToken).ConfigureAwait(false);
            journal.Write(scenario, "owned-kill-completed", identity);
            KillCompleted.TrySetResult();
        }

        public async ValueTask UnrefVerifiedAsync(EnvelopeIdentity identity, CancellationToken cancellationToken)
        {
            journal.Write(scenario, "owned-unref-request", identity);
            await inner.UnrefVerifiedAsync(identity, cancellationToken).ConfigureAwait(false);
            Unreferenced = true;
            journal.Write(scenario, "owned-unref-completed", identity);
        }

        public async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            Disposed = true;
            journal.Write(scenario, "owned-session-disposed");
        }

        internal static EnvelopeIdentity Capture(EnvelopeSnapshot value, string nonce) => new(nonce, value.UnitName,
            value.UnitPath, value.InvocationId, value.ControlGroup, value.MainPid, value.MainStartTicks,
            value.CgroupDevice, value.CgroupInode);

        private static void RequireHeldControls(EnvelopeSnapshot value, EnvelopeStart start, ulong limit)
        {
            if (value.UnitName != start.UnitName || value.Description != "Mako media envelope " + start.Nonce
                || value.InvocationId.Length != 32 || value.MainPid == 0 || value.MainStartTicks == 0
                || !value.MainInControlGroup || !value.CgroupExists || !value.CgroupPopulated || value.Terminal
                || !value.ControlsComplete || !value.KernelKillAvailable || value.MemoryMax != limit
                || value.KernelMemoryMax != limit || value.EffectiveMemoryMax is 0 || value.EffectiveMemoryMax > limit
                || value.MemorySwapMax != 0 || value.KernelSwapMax != 0 || !value.KernelOomGroup
                || !value.MemoryAccounting || value.Delegate || value.Type != "exec" || value.Restart != "no"
                || value.OomPolicy != "kill" || value.KillMode != "control-group")
                throw new ProbeFailure("probe-held-controls-incomplete");
        }

        internal static object ReadProc(uint pid)
        {
            string directory = "/proc/" + pid.ToString(CultureInfo.InvariantCulture);
            string stat = File.ReadAllText(directory + "/stat");
            string group = File.ReadAllText(directory + "/cgroup");
            string executable = new FileInfo(directory + "/exe").ResolveLinkTarget(returnFinalTarget: false)?.FullName ?? "unresolved";
            string[] fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            ulong ticks = ulong.Parse(fields[19], CultureInfo.InvariantCulture);
            return new { pid, startTicks = ticks, cgroup = group, executable };
        }
    }
}
