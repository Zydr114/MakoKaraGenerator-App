using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using MakoKaraGenerator.Processes.MediaEnvelope;

namespace M10.NativeEnvelope.Probe;

internal sealed record HeldReady(EnvelopeIdentity Identity, EnvelopeSnapshot Snapshot);
internal sealed record OwnerFinal(bool Closed, EnvelopeIdentity? Identity, EnvelopeSnapshot? Snapshot,
    bool UnrefAttempted, bool Unreferenced, bool NativePipesEof);
internal sealed record EmptyPayload;

internal sealed class ObserverOwnerState(EvidenceJournal journal, string evidence, string scenario)
{
    private AuditedEnvelopeBackend.Session? _session;
    private ProbePumps? _pumps;
    private bool _released, _authorized, _unrefAttempted, _unreferenced, _closed;
    private EnvelopeIdentity? _identity;
    private EnvelopeSnapshot? _last;
    // Keep every issued native control task until completion, including failure.
    private Task? _nativeCall;
    internal bool Closed => _closed;
    internal OwnerFinal State => new(_closed, _identity, _last, _unrefAttempted, _unreferenced,
        _pumps is { MetadataEof: true, StdoutEof: true, StderrEof: true });

    private async Task CallAsync(Func<ValueTask> call)
    { _nativeCall = call().AsTask(); await _nativeCall.ConfigureAwait(false); }

    internal async Task<object> CommandAsync(string kind, CancellationToken token)
    {
        if (kind == "AbortOwned")
        {
            await CleanupAsync(finishKnown: true, token).ConfigureAwait(false);
            return State;
        }
        if (kind == "StartHeld")
        {
            ObserverSupport.Require(_session is null, "observer-owner-start-repeated");
            var backend = new AuditedEnvelopeBackend(new LinuxSystemdEnvelopeBackend(), journal, scenario,
                ObserverSupport.NativeBytes);
            // Retain the backend/task before a successful Open result is logged.
            Task<IEnvelopeSession> open = backend.OpenAsync(token).AsTask();
            _nativeCall = open;
            try { _session = (AuditedEnvelopeBackend.Session)await open.ConfigureAwait(false); }
            finally { _session ??= backend.Opened; }
            AuditedEnvelopeBackend.Session opened = _session ?? throw new ProbeFailure("observer-owner-open-result-missing");
            string nonce = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
            string tools = Path.Combine(AppContext.BaseDirectory, "media-tools");
            await CallAsync(() => opened.StartHeldAsync(new(nonce, "mako-media-" + nonce + ".service",
                ObserverSupport.NativeBytes, new(Path.Combine(tools, "mkg-media-exec-gate"),
                    Path.Combine(tools, "mkg-envelope-probe-helper"), [scenario == "owner-disconnect" ? "wait" : "echo"])), token))
                .ConfigureAwait(false);
            await CallAsync(() => opened.CaptureHeldProofAsync(token)).ConfigureAwait(false);
            _identity = opened.Identity ?? throw new ProbeFailure("observer-owner-held-identity-missing");
            _last = opened.Held ?? throw new ProbeFailure("observer-owner-held-snapshot-missing");
            return new HeldReady(_identity, _last);
        }
        AuditedEnvelopeBackend.Session session = _session ?? throw new ProbeFailure("observer-owner-session-missing");
        EnvelopeIdentity identity = _identity ?? throw new ProbeFailure("observer-owner-identity-missing");
        if (kind == "ReleaseGate")
        {
            ObserverSupport.Require(!_released && !_closed, "observer-owner-gate-repeated");
            await CallAsync(() => session.ReleaseExecGateAsync(token)).ConfigureAwait(false);
            _released = true;
            _pumps = new(journal, scenario);
            _pumps.Start(session, CancellationToken.None);
            HelperHello hello = await _pumps.Hello.Task.WaitAsync(token).ConfigureAwait(false);
            ObserverSupport.Hello(hello, identity, scenario == "owner-disconnect" ? "wait" : "echo");
            return hello;
        }
        if (kind == "AuthorizeHelper")
        {
            ObserverSupport.Require(_released && !_authorized && !_closed, "observer-owner-go-order-invalid");
            await session.Input.WriteAsync(Encoding.ASCII.GetBytes("GO\n"), token).ConfigureAwait(false);
            await session.Input.FlushAsync(token).ConfigureAwait(false);
            _authorized = true;
            if (scenario == "owner-disconnect")
                await _pumps!.Waiting.Task.WaitAsync(token).ConfigureAwait(false);
            else
            {
                await _pumps!.All.WaitAsync(token).ConfigureAwait(false);
                ObserverSupport.Require(Encoding.ASCII.GetString(_pumps.Stdout) == "MKGPROBE-OUT\n"
                    && Encoding.ASCII.GetString(_pumps.Stderr) == "probe-diagnostic\n"
                    && State.NativePipesEof && _pumps.CompleteSeen && _pumps.Hellos.Count == 1
                    && _pumps.ProcessIdentities.Count == 1, "observer-owner-echo-or-eof-mismatch");
                _last = await ObserverSupport.DrainAsync(session.ObserveAsync, identity, token).ConfigureAwait(false);
                ObserverSupport.Require(!_last.Oom && _last.ExecMainCode == 1 && _last.ExecMainStatus == 0,
                    "observer-owner-echo-not-clean-natural-exit");
            }
            return State;
        }
        if (kind == "ObserveOwner")
        {
            ObserverSupport.Require(!_closed, "observer-owner-observe-after-close");
            _last = await session.ObserveAsync(token).ConfigureAwait(false);
            ObserverSupport.Identity(_last, identity);
            return _last;
        }
        if (kind == "UnrefOwner")
        {
            ObserverSupport.Require(!_closed && !_unrefAttempted && State.NativePipesEof,
                "observer-owner-unref-order-invalid");
            _last = await session.ObserveAsync(token).ConfigureAwait(false);
            ObserverSupport.Identity(_last, identity);
            ObserverSupport.Require(ObserverSupport.Drained(_last), "observer-owner-unref-not-drained");
            _unrefAttempted = true;
            await CallAsync(() => session.UnrefVerifiedAsync(identity, token)).ConfigureAwait(false);
            _unreferenced = true;
            return State;
        }
        if (kind == "DisposeOwner")
        {
            ObserverSupport.Require(_unreferenced && !_closed && State.NativePipesEof,
                "observer-owner-dispose-order-invalid");
            await CallAsync(session.DisposeAsync).ConfigureAwait(false);
            _closed = true;
            return State;
        }
        throw new ProbeFailure("observer-owner-command-invalid");
    }

    internal async Task CleanupAsync(bool finishKnown, CancellationToken token)
    {
        if (_nativeCall is not null)
        { try { await _nativeCall.ConfigureAwait(false); } catch (Exception) { /* joined and retained */ } }
        if (_closed) return;
        if (_session is null) { _closed = true; return; }
        AuditedEnvelopeBackend.Session session = _session;
        if (_identity is null)
        {
            if (session.StartAttempted)
            {
                // This is the only owner with ordered-bus absence authority.
                bool absent = await session.ConfirmStartAbsentAsync(token).ConfigureAwait(false);
                ObserverSupport.Require(absent, "observer-owner-start-remains-unresolved");
            }
            await CallAsync(session.DisposeAsync).ConfigureAwait(false);
            _closed = true;
            return;
        }
        EnvelopeIdentity identity = _identity;
        if (_unreferenced)
        {
            if (finishKnown) { await CallAsync(session.DisposeAsync).ConfigureAwait(false); _closed = true; }
            return;
        }
        _last = await session.ObserveAsync(token).ConfigureAwait(false);
        ObserverSupport.Identity(_last, identity);
        if (!ObserverSupport.Drained(_last))
            await CallAsync(() => session.KillVerifiedCgroupAsync(identity, token)).ConfigureAwait(false);
        _last = await ObserverSupport.DrainAsync(session.ObserveAsync, identity, token).ConfigureAwait(false);
        if (_pumps is null)
        {
            _pumps = new(journal, scenario);
            _pumps.Start(session, CancellationToken.None);
        }
        try { await _pumps.All.WaitAsync(token).ConfigureAwait(false); }
        catch (ProbeFailure error) when (!_released && error.Message == "probe-fixture-hello-missing") { }
        ObserverSupport.Require(State.NativePipesEof, "observer-owner-cleanup-eof-missing");
        if (!finishKnown) return;
        ObserverSupport.Require(!_unrefAttempted, "observer-owner-unref-uncertain-retained");
        _unrefAttempted = true;
        await CallAsync(() => session.UnrefVerifiedAsync(identity, token)).ConfigureAwait(false);
        _unreferenced = true;
        await CallAsync(session.DisposeAsync).ConfigureAwait(false);
        _closed = true;
    }

    internal async Task<GuardianReply> GuardianAsync(string command)
    {
        if (command == "status") return new(new { state = State });
        try
        {
            if (command == "observe" && _session is not null && !_closed && !_unreferenced)
                _last = await _session.ObserveAsync(CancellationToken.None).ConfigureAwait(false);
            if (command is "kill-drain" or "finish-known")
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await CleanupAsync(command == "finish-known", cleanup.Token).ConfigureAwait(false);
            }
            ObserverSupport.Summary(evidence, State);
            journal.Write(scenario, "guardian-command-complete", new { command, state = State });
            return new(new { state = State }, command == "finish-known" && Closed);
        }
        catch (Exception error)
        {
            journal.Write(scenario, "guardian-command-failed", new { command, error = ObserverSupport.Safe(error), state = State });
            return new(new { state = State, error = ObserverSupport.Safe(error) });
        }
    }
}

internal sealed class ObserverOwnerProcess : IDisposable
{
    internal Process Process { get; }
    internal ProbeProcess Captured { get; private set; } = null!;
    internal ProbePidFd? PidFd { get; private set; }
    private readonly ProbeOwnerChannel _commands;
    private readonly EvidenceJournal _journal;
    private readonly string _scenario;
    private readonly string _directory;
    private readonly Task<byte[]> _stderr;
    private bool _joined, _closed, _killed;
    internal bool Closed => _closed;
    internal object State => new { captured = Captured, joined = _joined, closed = _closed,
        killed = _killed, channelBroken = _commands.Broken, channel = _commands.State, nativeOwnerProcessExited = _joined || Process.HasExited,
        childGuardian = Path.Combine(_directory, "guardian.sock") };

    private ObserverOwnerProcess(Process process, string directory, string run, string scenario, EvidenceJournal journal)
    {
        Process = process;
        _directory = directory;
        _scenario = scenario;
        _journal = journal;
        _commands = new(new ProbeWire(process.StandardOutput.BaseStream, process.StandardInput.BaseStream, run, scenario),
            process.StandardInput.Close, journal, scenario);
        _stderr = ReadStderrAsync(process.StandardError.BaseStream);
    }

    internal static ObserverOwnerProcess Start(string directory, string run, string scenario, EvidenceJournal journal)
    {
        ProbeProcess parent = ProbeProcess.Read((uint)Environment.ProcessId);
        var info = new ProcessStartInfo(Environment.ProcessPath ?? throw new ProbeFailure("observer-probe-process-path-missing"))
        { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(info.FileName) == "dotnet")
            info.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (string arg in new[] { "--observer-owner", directory, run, scenario,
            parent.Pid.ToString(CultureInfo.InvariantCulture), parent.StartTicks.ToString(CultureInfo.InvariantCulture) })
            info.ArgumentList.Add(arg);
        var process = new Process { StartInfo = info };
        ObserverSupport.Require(process.Start(), "observer-probe-owner-process-start-failed");
        // Return the actual child before any capture/log operation can throw.
        return new(process, directory, run, scenario, journal);
    }

    internal void Capture()
    {
        Captured = ProbeProcess.Read((uint)Process.Id);
        ObserverSupport.Require(Captured.Executable == ProbeProcess.Read((uint)Environment.ProcessId).Executable,
            "observer-probe-child-executable-mismatch");
        PidFd = new(Captured.Pid, Captured.StartTicks, ownedChild: true);
        _journal.Write(_scenario, "owner-process-captured", Captured);
    }

    internal Task<T> CommandAsync<T>(string kind, CancellationToken token)
    {
        ObserverSupport.Require(!_joined, "observer-probe-owner-channel-unusable");
        return _commands.CommandAsync<T>(kind, token);
    }

    internal async Task KillForDisconnectAsync(CancellationToken token)
    {
        (PidFd ?? throw new ProbeFailure("observer-probe-owner-pidfd-missing")).KillOwnedChild();
        _killed = true;
        _journal.Write(_scenario, "owned-child-pidfd-sigkill", Captured);
        await JoinAsync(token).ConfigureAwait(false);
        _closed = true; // Process/FD lifetime only; Host must still prove original native drain.
    }

    internal async Task ExitCleanAsync(CancellationToken token)
    {
        _ = await CommandAsync<OwnerFinal>("ExitClean", token).ConfigureAwait(false);
        await JoinAsync(token).ConfigureAwait(false);
        ObserverSupport.Require(Process.ExitCode == 0, "observer-probe-owner-clean-exit-failed");
        LoadClosedProof();
    }

    private async Task JoinAsync(CancellationToken token, bool strict = true)
    {
        // This is ordered after the SAME command/send/read tasks have joined;
        // only then may the tail reader consume control output until real EOF.
        await _commands.JoinAsync().ConfigureAwait(false);
        await Process.WaitForExitAsync(token).ConfigureAwait(false);
        byte[] stderr = await _stderr.WaitAsync(token).ConfigureAwait(false);
        // The channel above joined its actual read, including noncancelable late ACKs.
        byte[] scratch = new byte[4096];
        int trailing = 0;
        while (true)
        {
            int read = await Process.StandardOutput.BaseStream.ReadAsync(scratch, token).ConfigureAwait(false);
            if (read == 0) break;
            trailing += read;
            ObserverSupport.Require(trailing <= 65536, "observer-probe-owner-trailing-output-overflow");
        }
        ObserverSupport.Require(!strict || trailing == 0, "observer-probe-owner-output-after-final-ack");
        _joined = true;
        RecordChildJournal();
        _journal.Write(_scenario, "owner-process-and-control-eof", new { captured = Captured,
            Process.ExitCode, controlStdoutEof = true, controlStderrEof = true, stderrBytes = stderr.Length,
            trailingControlBytes = trailing });
    }

    private void RecordChildJournal()
    {
        string path = Path.Combine(_directory, "events.jsonl");
        ObserverSupport.Require(File.Exists(path) && new FileInfo(path).Length <= 1048576,
            "observer-probe-child-journal-missing-or-overflow");
        byte[] bytes = File.ReadAllBytes(path);
        int complete = Array.LastIndexOf(bytes, (byte)10) + 1;
        int previous = complete > 1 ? Array.LastIndexOf(bytes, (byte)10, complete - 2) : -1;
        JsonElement? lastComplete = null;
        if (complete > 0)
        {
            using JsonDocument last = JsonDocument.Parse(bytes.AsMemory(previous + 1, complete - previous - 2),
                new JsonDocumentOptions { MaxDepth = 32 });
            lastComplete = last.RootElement.Clone();
        }
        _journal.Write(_scenario, "owner-exited-journal-summary", new
        {
            bytes = bytes.Length, completeBytes = complete, incompleteTailBytes = bytes.Length - complete,
            sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), lastComplete,
            killedOwnerMeansNativePipeEofUnknown = _killed,
        });
    }

    private void LoadClosedProof()
    {
        string path = Path.Combine(_directory, "summary.json");
        ObserverSupport.Require(new FileInfo(path).Length <= 32768, "observer-probe-owner-summary-overflow");
        OwnerFinal proof = JsonSerializer.Deserialize<OwnerFinal>(File.ReadAllBytes(path),
            ObserverSupport.Json) ?? throw new ProbeFailure("observer-probe-owner-summary-missing");
        ObserverSupport.Require(proof.Closed, "observer-probe-owner-not-closed");
        _closed = true;
        _journal.Write(_scenario, "owner-closed-proof", proof);
    }

    internal async Task CleanupAsync(bool finishKnown, CancellationToken token)
    {
        if (_closed) return;
        if (_commands.Broken)
        {
            // Never send AbortOwned/Unref on a broken/partially consumed command.
            // Close our real writer, join all original exchange tasks, and allow
            // the child's SAME reader to receive EOF and run its owned cleanup.
            await _commands.BreakAndJoinAsync().ConfigureAwait(false);
            if (!Process.HasExited) await Process.WaitForExitAsync(token).ConfigureAwait(false);
        }
        if (Process.HasExited)
        {
            if (!_joined) await JoinAsync(token, strict: false).ConfigureAwait(false);
            if (_killed) _closed = true;
            else LoadClosedProof();
            return;
        }
        ObserverSupport.Require(finishKnown, "observer-probe-owner-retained-use-child-guardian");
        _ = await CommandAsync<OwnerFinal>("AbortOwned", token).ConfigureAwait(false);
        await ExitCleanAsync(token).ConfigureAwait(false);
    }

    internal void CloseHandles()
    {
        ObserverSupport.Require(_closed && _joined, "observer-probe-owner-handles-still-owned");
        PidFd?.Close();
        Process.Dispose();
    }

    public void Dispose() => CloseHandles();

    private static async Task<byte[]> ReadStderrAsync(Stream stream)
    {
        byte[] buffer = new byte[65536];
        int used = 0;
        while (used < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(used), CancellationToken.None).ConfigureAwait(false);
            if (read == 0) return buffer[..used];
            used += read;
        }
        throw new ProbeFailure("observer-probe-owner-stderr-overflow");
    }
}
