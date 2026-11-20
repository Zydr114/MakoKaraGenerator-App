namespace MakoKaraGenerator.Processes.MediaEnvelope;

internal readonly record struct EnvelopeRunState(bool Terminal, bool Populated, bool Drainable,
    bool MemoryLimitHit, string Result, bool ExitedNormally, int ExitCode, WindowsJobMemoryDiagnostics? WindowsJobMemory = null);

internal interface IHandleEnvelopeSession
{
    EnvelopeRunOwner CaptureOwner(CancellationToken token);
}

internal abstract class EnvelopeRunOwner
{
    internal virtual EnvelopeIdentity? LinuxIdentity => null;
    internal virtual bool WaitForHeldInputEof => false;
    internal abstract ValueTask ValidateControlsAsync(ulong limit, CancellationToken token);
    internal abstract ValueTask<EnvelopeRunState> ObserveOwnedAsync(CancellationToken token);
    internal abstract ValueTask KillOwnedAsync(CancellationToken token);
    internal abstract ValueTask<EnvelopeRunState> ReleaseOwnedAsync(EnvelopeRunState last, CancellationToken token);
}

internal sealed class SystemdEnvelopeRunOwner(IEnvelopeSession session, EnvelopeSnapshot captured, EnvelopeIdentity identity)
    : EnvelopeRunOwner
{
    internal override EnvelopeIdentity LinuxIdentity => identity;
    internal override bool WaitForHeldInputEof => true;

    internal override ValueTask ValidateControlsAsync(ulong limit, CancellationToken token)
    {
        EnvelopeSnapshotValidator.ValidateControls(captured, limit);
        return ValueTask.CompletedTask;
    }

    internal override async ValueTask<EnvelopeRunState> ObserveOwnedAsync(CancellationToken token)
    {
        EnvelopeSnapshot value = await session.ObserveAsync(token).ConfigureAwait(false);
        EnvelopeSnapshotValidator.VerifyIdentity(value, identity);
        return new(value.Terminal, value.CgroupPopulated, value.CgroupExists || value.CgroupRemoved,
            value.Oom, value.Result, value.ExecMainCode == 1, value.ExecMainStatus);
    }

    internal override ValueTask KillOwnedAsync(CancellationToken token) => session.KillVerifiedCgroupAsync(identity, token);

    internal override async ValueTask<EnvelopeRunState> ReleaseOwnedAsync(EnvelopeRunState last, CancellationToken token)
    {
        await session.UnrefVerifiedAsync(identity, token).ConfigureAwait(false);
        return last;
    }
}
