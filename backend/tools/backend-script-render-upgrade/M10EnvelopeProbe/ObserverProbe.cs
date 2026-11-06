using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using MakoKaraGenerator.Processes.MediaEnvelope;

namespace M10.NativeEnvelope.Probe;

internal sealed class ObserverLease(EvidenceJournal journal, string scenario, EnvelopeIdentity expected,
    IEnvelopeObserverBackend? backend = null)
{
    private Task? _nativeCall;
    private Task<EnvelopeObserverAttachResult>? _attachCall;
    internal IEnvelopeObserver? Observer { get; private set; }
    internal EnvelopeObserverAttachResult? Result { get; private set; }
    internal EnvelopeSnapshot? Last { get; private set; }
    internal bool UnrefAttempted { get; private set; }
    internal bool Unreferenced { get; private set; }
    internal bool Closed { get; private set; }
    internal object State => new { expected, attachCompleted = _attachCall?.IsCompleted ?? false,
        actualObserverRetained = Observer is not null, attached = Result?.Attached ?? false,
        failure = Result?.Failure is { } error ? ObserverSupport.Safe(error) : null,
        UnrefAttempted, Unreferenced, Closed, Last, callInFlight = _nativeCall is { IsCompleted: false } };

    internal async Task AttachAsync(CancellationToken token)
    {
        _attachCall = (backend ?? new LinuxSystemdEnvelopeBackend())
            .AttachAsync(expected, ObserverSupport.NativeBytes, token).AsTask();
        _nativeCall = _attachCall;
        try
        {
            Result = await _attachCall.ConfigureAwait(false);
            // Store the real observer BEFORE any logging or branch can throw.
            Observer = Result.Observer;
        }
        catch { Closed = true; throw; } // API only throws before a native observer context exists.
        journal.Write(scenario, "observer-attach-returned", State);
    }

    private IEnvelopeObserver Actual => Observer ?? throw new ProbeFailure("observer-probe-real-observer-missing");

    internal async ValueTask<EnvelopeSnapshot> ObserveAsync(CancellationToken token)
    {
        Task<EnvelopeSnapshot> call = Actual.ObserveAsync(token).AsTask();
        _nativeCall = call;
        Last = await call.ConfigureAwait(false);
        ObserverSupport.Identity(Last, expected);
        journal.Write(scenario, "host-original-snapshot", Last);
        return Last;
    }

    internal async Task KillAsync(CancellationToken token)
    {
        _nativeCall = Actual.KillVerifiedCgroupAsync(token).AsTask();
        await _nativeCall.ConfigureAwait(false);
        journal.Write(scenario, "host-original-pin-kill-complete", expected);
    }

    internal async Task ReleaseAsync(CancellationToken token)
    {
        ObserverSupport.Require(!UnrefAttempted && !Closed, "observer-probe-own-unref-already-attempted");
        // Conservative even for pre-send cancellation. Never retry an ambiguous request.
        UnrefAttempted = true;
        _nativeCall = Actual.UnrefOwnReferenceAsync(token).AsTask();
        await _nativeCall.ConfigureAwait(false);
        Unreferenced = true;
        journal.Write(scenario, "host-own-unref-complete", expected);
    }

    internal async Task CloseAsync()
    {
        _nativeCall = Actual.DisposeAsync().AsTask();
        await _nativeCall.ConfigureAwait(false);
        Closed = true;
        journal.Write(scenario, "host-observer-close-complete", expected);
    }

    internal async Task VerifyLiveRefusalsAsync(CancellationToken token)
    {
        // A known pre-native drain rejection: this is NOT a sent Unref attempt.
        UnrefAttempted = true;
        try
        {
            _nativeCall = Actual.UnrefOwnReferenceAsync(token).AsTask();
            await _nativeCall.ConfigureAwait(false);
            Unreferenced = true;
        }
        catch (EnvelopeException error) when (error.Failure == EnvelopeFailure.DrainFailed
            && error.SafeCode == "native-unit-not-terminal")
        {
            UnrefAttempted = false; // Exact managed RequireDrained rejection happened before native Unref.
            journal.Write(scenario, "host-live-unref-rejected", ObserverSupport.Safe(error));
            try
            {
                _nativeCall = Actual.DisposeAsync().AsTask();
                await _nativeCall.ConfigureAwait(false);
                Closed = true;
            }
            catch (EnvelopeException close) when (close.Failure == EnvelopeFailure.DrainFailed
                && close.SafeCode == "native-observer-reference-unresolved")
            {
                journal.Write(scenario, "host-live-close-rejected-same-object-retained", ObserverSupport.Safe(close));
                _ = await ObserveAsync(token).ConfigureAwait(false);
                return;
            }
            throw new ProbeFailure("observer-probe-live-close-not-rejected");
        }
        // If the supposedly live call unexpectedly succeeded, do not send it again.
        UnrefAttempted = true;
        throw new ProbeFailure("observer-probe-live-unref-not-rejected");
    }

    internal async Task CleanupAsync(bool finishKnown, CancellationToken token)
    {
        if (_nativeCall is not null)
        { try { await _nativeCall.ConfigureAwait(false); } catch (Exception) { /* joined */ } }
        if (Closed) return;
        if (Observer is null)
        {
            ObserverSupport.Require(_attachCall is null or { IsCompleted: true }, "observer-probe-attach-still-in-flight");
            Closed = true;
            return;
        }
        // A failed admission may have no own Ref at all. Close is the native
        // authority that can confirm this without trying an Unref.
        if (Result?.Attached != true && !UnrefAttempted)
        {
            try { await CloseAsync().ConfigureAwait(false); return; }
            catch (EnvelopeException error) when (error.SafeCode == "native-observer-reference-unresolved") { }
        }
        if (!Unreferenced)
        {
            EnvelopeSnapshot snapshot = await ObserveAsync(token).ConfigureAwait(false);
            if (!ObserverSupport.Drained(snapshot)) await KillAsync(token).ConfigureAwait(false);
            Last = await ObserverSupport.DrainAsync(ObserveAsync, expected, token).ConfigureAwait(false);
            if (!finishKnown) return;
            ObserverSupport.Require(!UnrefAttempted, "observer-probe-unref-uncertain-retained");
            await ReleaseAsync(token).ConfigureAwait(false);
        }
        if (finishKnown) await CloseAsync().ConfigureAwait(false);
    }
}

internal sealed class ObserverScenario(string directory, string run, string name, EvidenceJournal journal,
    ProbeBusWitness witness)
{
    private readonly List<ObserverLease> _observers = [];
    private ObserverOwnerProcess? _owner;
    private ProbePidFd? _helper;
    private EnvelopeIdentity? _identity;
    private string? _ownerSender, _hostSender;
    private bool _handlesClosed;
    private bool _nativeExitVerified = true;
    internal bool Closed => (_owner is null || _owner.Closed) && _observers.All(x => x.Closed) && _nativeExitVerified;
    internal object State => new { scenario = name, closed = Closed, identity = _identity,
        owner = _owner?.State, observers = _observers.Select(x => x.State).ToArray(),
        helperPidfdCaptured = _helper is not null, handlesClosed = _handlesClosed };

    private RefWitness Refs(string checkpoint)
    {
        RefWitness value = witness.Sample(_identity ?? throw new ProbeFailure("observer-probe-identity-missing"));
        journal.Write(name, "refs-" + checkpoint, value);
        return value;
    }
    private static void Only(RefWitness value, params string[] senders)
        => ObserverSupport.Require(value.Entries.Length == senders.Length
            && value.Entries.Select(x => x.Sender).ToHashSet(StringComparer.Ordinal).SetEquals(senders),
            "observer-probe-reference-set-mismatch");

    private async Task<ObserverLease> AttachAsync(EnvelopeIdentity expected, CancellationToken token)
    {
        var lease = new ObserverLease(journal, name, expected);
        _observers.Add(lease);
        await lease.AttachAsync(token).ConfigureAwait(false);
        return lease;
    }

    internal async Task RunAsync(CancellationToken token)
    {
        _owner = ObserverOwnerProcess.Start(Path.Combine(directory, name), run, name, journal);
        _owner.Capture();
        HeldReady held = await _owner.CommandAsync<HeldReady>("StartHeld", token).ConfigureAwait(false);
        _identity = held.Identity;
        ObserverSupport.Identity(held.Snapshot, held.Identity);
        ObserverSupport.Require(held.Snapshot.ActiveState == "active" && held.Snapshot.MainPid != 0
            && held.Snapshot.CgroupPopulated, "observer-probe-initial-gate-not-live");
        _helper = new(held.Identity.MainPid, held.Identity.StartTicks, ownedChild: false);
        _nativeExitVerified = false;
        RefWitness before = Refs("before-attach");
        ObserverSupport.Require(before.Entries.Length == 1 && before.Entries[0].Pid == _owner.Captured.Pid,
            "observer-probe-owner-addref-not-witnessed");
        _ownerSender = before.Entries[0].Sender;

        if (name == "wrong-observer-tokens")
        {
            EnvelopeIdentity[] wrong =
            [
                held.Identity with { InvocationId = held.Identity.InvocationId[0] == 'a' ? new string('b', 32) : new string('a', 32) },
                held.Identity with { MainPid = held.Identity.MainPid == uint.MaxValue ? held.Identity.MainPid - 1 : held.Identity.MainPid + 1 },
                held.Identity with { StartTicks = held.Identity.StartTicks == ulong.MaxValue ? 1 : held.Identity.StartTicks + 1 },
                held.Identity with { CgroupInode = held.Identity.CgroupInode == ulong.MaxValue ? 1 : held.Identity.CgroupInode + 1 },
            ];
            for (int index = 0; index < wrong.Length; index++)
            {
                ObserverLease rejected = await AttachAsync(wrong[index], token).ConfigureAwait(false);
                ObserverSupport.Require(rejected.Result is { Attached: false, Failure.Failure: EnvelopeFailure.OwnershipLost,
                    Failure.SafeCode: "native-envelope-identity-stale" },
                    "observer-probe-wrong-token-not-ownership-rejected");
                await rejected.CloseAsync().ConfigureAwait(false);
                Only(Refs("wrong-token-" + index.ToString(CultureInfo.InvariantCulture)), _ownerSender);
                EnvelopeSnapshot stillHeld = await _owner.CommandAsync<EnvelopeSnapshot>("ObserveOwner", token).ConfigureAwait(false);
                ObserverSupport.Identity(stillHeld, held.Identity);
                ObserverSupport.Require(stillHeld.MainPid == held.Identity.MainPid && stillHeld.CgroupPopulated,
                    "observer-probe-wrong-token-changed-original-gate");
            }
        }

        ObserverLease host = await AttachAsync(held.Identity, token).ConfigureAwait(false);
        ObserverSupport.Require(host.Result is { Attached: true }, "observer-probe-host-attach-not-admitted");
        RefWitness both = Refs("after-attach");
        RefSender[] added = both.Entries.Where(x => x.Sender != _ownerSender).ToArray();
        ObserverSupport.Require(both.Entries.Length == 2 && both.Entries.Any(x => x.Sender == _ownerSender
            && x.Pid == _owner.Captured.Pid) && added.Length == 1 && added[0].Pid == Environment.ProcessId,
            "observer-probe-independent-host-ref-not-witnessed");
        _hostSender = added[0].Sender;

        if (name == "wrong-observer-tokens")
        {
            await CleanupAsync(finishKnown: true, token).ConfigureAwait(false);
            return;
        }
        var sinceRelease = Stopwatch.StartNew();
        HelperHello hello = await _owner.CommandAsync<HelperHello>("ReleaseGate", token).ConfigureAwait(false);
        ObserverSupport.Hello(hello, held.Identity, name == "owner-disconnect" ? "wait" : "echo");
        if (name == "owner-disconnect") ObserverSupport.Require(sinceRelease.Elapsed < TimeSpan.FromSeconds(2),
            "observer-probe-hello-exceeded-prealarm-budget");
        OwnerFinal action = await _owner.CommandAsync<OwnerFinal>("AuthorizeHelper", token).ConfigureAwait(false);

        if (name == "owner-disconnect")
        {
            ObserverSupport.Require(sinceRelease.Elapsed < TimeSpan.FromSeconds(3), "observer-probe-waiting-exceeded-prealarm-budget");
            EnvelopeSnapshot live = await host.ObserveAsync(token).ConfigureAwait(false);
            RequireLive(live, held.Identity);
            Only(Refs("before-live-refusal"), _ownerSender, _hostSender);
            await host.VerifyLiveRefusalsAsync(token).ConfigureAwait(false);
            Only(Refs("after-live-refusal"), _ownerSender, _hostSender);
            await _owner.KillForDisconnectAsync(token).ConfigureAwait(false);
            RefWitness disconnected;
            while (true)
            {
                try
                {
                    disconnected = Refs("owner-disconnected");
                    if (disconnected.Entries.All(x => x.Sender != _ownerSender)) break;
                }
                catch (EnvelopeException error) when (error.Failure == EnvelopeFailure.CapacityUnsupported)
                { journal.Write(name, "refs-disconnect-sample-retry", ObserverSupport.Safe(error)); }
                ObserverSupport.Require(sinceRelease.Elapsed < TimeSpan.FromSeconds(7),
                    "observer-probe-owner-disconnect-exceeded-prealarm-budget");
                await Task.Delay(25, token).ConfigureAwait(false);
            }
            Only(disconnected, _hostSender);
            live = await host.ObserveAsync(token).ConfigureAwait(false);
            RequireLive(live, held.Identity);
            ObserverSupport.Require(sinceRelease.Elapsed < TimeSpan.FromSeconds(7) && !_helper.Exited,
                "observer-probe-original-helper-not-live-after-owner-disconnect");
            journal.Write(name, "original-native-live-after-owner-disconnect", live);
            ObserverSupport.Require(sinceRelease.Elapsed < TimeSpan.FromSeconds(8), "observer-probe-kill-exceeded-prealarm-budget");
            await host.KillAsync(token).ConfigureAwait(false);
            EnvelopeSnapshot terminal = await ObserverSupport.DrainAsync(host.ObserveAsync, held.Identity, token).ConfigureAwait(false);
            ObserverSupport.Require(_helper.Exited && !terminal.Oom && terminal.ExecMainCode == 2
                && terminal.ExecMainStatus == 9, "observer-probe-terminal-was-not-owned-sigkill");
            _nativeExitVerified = true;
            journal.Write(name, "original-native-drained-no-native-pipe-eof-authority", terminal);
            await host.ReleaseAsync(token).ConfigureAwait(false);
            await host.CloseAsync().ConfigureAwait(false);
        }
        else
        {
            ObserverSupport.Require(action.NativePipesEof && action.Snapshot is { } value && ObserverSupport.Drained(value),
                "observer-probe-owner-natural-eof-or-drain-missing");
            EnvelopeSnapshot terminal = await host.ObserveAsync(token).ConfigureAwait(false);
            ObserverSupport.Require(ObserverSupport.Drained(terminal) && _helper.Exited,
                "observer-probe-host-natural-drain-missing");
            _nativeExitVerified = true;
            Only(Refs("terminal-two-refs"), _ownerSender, _hostSender);
            if (name == "owner-unref-first")
            {
                _ = await _owner.CommandAsync<OwnerFinal>("UnrefOwner", token).ConfigureAwait(false);
                _ = await _owner.CommandAsync<OwnerFinal>("DisposeOwner", token).ConfigureAwait(false);
                Only(Refs("owner-unref-host-retains-terminal"), _hostSender);
                terminal = await host.ObserveAsync(token).ConfigureAwait(false);
                ObserverSupport.Require(ObserverSupport.Drained(terminal), "observer-probe-host-retained-terminal-missing");
                await _owner.ExitCleanAsync(token).ConfigureAwait(false);
                await host.ReleaseAsync(token).ConfigureAwait(false);
                await host.CloseAsync().ConfigureAwait(false);
            }
            else
            {
                await host.ReleaseAsync(token).ConfigureAwait(false);
                await host.CloseAsync().ConfigureAwait(false);
                Only(Refs("host-unref-owner-retains-terminal"), _ownerSender);
                terminal = await _owner.CommandAsync<EnvelopeSnapshot>("ObserveOwner", token).ConfigureAwait(false);
                ObserverSupport.Identity(terminal, held.Identity);
                ObserverSupport.Require(ObserverSupport.Drained(terminal), "observer-probe-owner-retained-terminal-missing");
                _ = await _owner.CommandAsync<OwnerFinal>("UnrefOwner", token).ConfigureAwait(false);
                _ = await _owner.CommandAsync<OwnerFinal>("DisposeOwner", token).ConfigureAwait(false);
                await _owner.ExitCleanAsync(token).ConfigureAwait(false);
            }
        }
        ObserverSupport.Require(Closed, "observer-probe-scenario-owners-remain");
    }

    private static void RequireLive(EnvelopeSnapshot value, EnvelopeIdentity identity)
    {
        ObserverSupport.Identity(value, identity);
        ObserverSupport.Require(value.MainPid == identity.MainPid && value.MainStartTicks == identity.StartTicks
            && value.ActiveState == "active" && value.CgroupPopulated, "observer-probe-original-native-not-live");
    }

    internal async Task CleanupAsync(bool finishKnown, CancellationToken token)
    {
        var errors = new List<Exception>();
        // Always try every retained real owner, even if another cleanup fails.
        if (_owner is not null)
        {
            try { await _owner.CleanupAsync(finishKnown, token).ConfigureAwait(false); }
            catch (Exception error) { errors.Add(error); }
        }
        foreach (ObserverLease observer in _observers)
        {
            try { await observer.CleanupAsync(finishKnown, token).ConfigureAwait(false); }
            catch (Exception error) { errors.Add(error); }
        }
        if (_helper is not null)
        {
            try
            {
                _nativeExitVerified = _helper.Exited;
                ObserverSupport.Require(_nativeExitVerified, "observer-probe-original-pidfd-not-exited");
            }
            catch (Exception error) { errors.Add(error); }
        }
        if (errors.Count != 0) throw new AggregateException(errors);
        if (finishKnown) ObserverSupport.Require(Closed, "observer-probe-cleanup-remains-retained");
    }

    internal async Task<GuardianReply> GuardianAsync(string command)
    {
        try
        {
            if (command == "observe")
                foreach (ObserverLease lease in _observers.Where(x => !x.Closed && !x.Unreferenced))
                    _ = await lease.ObserveAsync(CancellationToken.None).ConfigureAwait(false);
            if (command is "kill-drain" or "finish-known")
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await CleanupAsync(command == "finish-known", cleanup.Token).ConfigureAwait(false);
            }
            return new(State, command == "finish-known" && Closed);
        }
        catch (Exception error) { return new(new { state = State, error = ObserverSupport.Safe(error) }); }
    }

    internal void CloseHandles()
    {
        if (_handlesClosed) return;
        ObserverSupport.Require(Closed, "observer-probe-closing-live-handles");
        _helper?.Close();
        _owner?.CloseHandles();
        _handlesClosed = true;
    }
}

internal static class ObserverProbe
{
    internal static async Task<int> DispatchAsync(string[] args)
    {
        if (!OperatingSystem.IsLinux()) return 42;
        try
        {
            if (args.Length == 2 && args[0] == "--observer-evidence" && Path.IsPathFullyQualified(args[1]))
                return await RunHostAsync(Path.GetFullPath(args[1])).ConfigureAwait(false);
            if (args.Length == 6 && args[0] == "--observer-owner" && Path.IsPathFullyQualified(args[1])
                && args[2].Length == 32 && args[2].All(char.IsAsciiHexDigitLower)
                && (args[3] is "wrong-observer-tokens" or "owner-unref-first" or "host-unref-first" or "owner-disconnect")
                && uint.TryParse(args[4], CultureInfo.InvariantCulture, out uint parentPid)
                && ulong.TryParse(args[5], CultureInfo.InvariantCulture, out ulong parentTicks))
                return await RunOwnerAsync(args[1], args[2], args[3], parentPid, parentTicks).ConfigureAwait(false);
            Console.Error.WriteLine("observer probe: invalid arguments");
            return 64;
        }
        catch (Exception error)
        {
            // RunHost/RunOwner contain all created-resource failures. This outer
            // boundary is for arguments/bootstrap/filesystem setup only.
            Console.Error.WriteLine(JsonSerializer.Serialize(ObserverSupport.Safe(error), ObserverSupport.Json));
            return 1;
        }
    }

    private static JsonElement ReadSourceManifest()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "observer-source-manifest.json");
        ObserverSupport.Require(File.Exists(path) && new FileInfo(path).Length is > 0 and <= 1048576,
            "observer-probe-matched-source-manifest-missing");
        using JsonDocument source = JsonDocument.Parse(File.ReadAllBytes(path),
            new JsonDocumentOptions { MaxDepth = 16 });
        JsonElement value = source.RootElement;
        ObserverSupport.Require(value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty("matchedIntegrationReviewed", out JsonElement reviewed)
            && reviewed.ValueKind == JsonValueKind.True
            && value.TryGetProperty("files", out JsonElement files) && files.ValueKind == JsonValueKind.Array
            && files.GetArrayLength() > 0, "observer-probe-matched-source-manifest-invalid");
        // This is root's declared source inventory, not a claim that this process
        // can reconstruct provenance from binaries. Runtime hashes are independent.
        return value.Clone();
    }

    private static void Record(EvidenceJournal journal, string scenario, string kind, object data)
    { try { journal.Write(scenario, kind, data); } catch (Exception) { /* preserve actual ownership on journal failure */ } }

    private static async Task<int> RunHostAsync(string evidence)
    {
        // Reject long child guardian paths before any process/session can exist.
        ObserverSupport.Require(System.Text.Encoding.UTF8.GetByteCount(Path.Combine(evidence,
            "wrong-observer-tokens", "guardian.sock")) <= 100, "observer-probe-guardian-path-too-long");
        ObserverSupport.MakePrivateDirectory(evidence);
        using var journal = new EvidenceJournal(evidence);
        ProbeProcess hostIdentity = ProbeProcess.Read((uint)Environment.ProcessId);
        ObserverScenario? current = null;
        var summaries = new List<object>();
        ProbeBusWitness? witness = null;
        object? artifacts = null;
        var guardian = new ProbeGuardian(evidence, command => current is null
            ? Task.FromResult(new GuardianReply(new { state = "no-operation" })) : current.GuardianAsync(command));
        int exit = 1;
        using var userCancel = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, value) => { value.Cancel = true; userCancel.Cancel(); };
        Console.CancelKeyPress += onCancel;
        try
        {
            witness = new();
            string tools = Path.Combine(AppContext.BaseDirectory, "media-tools");
            string run = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            artifacts = new { scope = "finite-real-observer-fixture", sourceMatch = "root-declared-reviewed-integration-not-runtime-attestation",
                host = hostIdentity, runtime = Environment.Version.ToString(),
                os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                kernel = File.ReadAllText("/proc/sys/kernel/osrelease").Trim(),
                activeBudgetSeconds = 240, scenarioBudgetSeconds = 30, cleanupBudgetSeconds = 10,
                source = ReadSourceManifest(),
                hashes = new[] { typeof(Program).Assembly.Location, typeof(LinuxSystemdEnvelopeBackend).Assembly.Location, Path.Combine(tools, "libmkg_media_envelope.so"),
                    Path.Combine(tools, "mkg-media-exec-gate"), Path.Combine(tools, "mkg-envelope-probe-helper"),
                    Path.Combine(tools, "libmkg_probe_diagnostics.so") }.Select(path => new { path,
                        sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) }).ToArray() };
            ObserverSupport.AtomicJson(evidence, "source-manifest.json", new
            { state = "started", artifacts, managerVersion = witness.ManagerVersion });
            journal.Write("run", "observer-probe-start", artifacts);
            using var whole = CancellationTokenSource.CreateLinkedTokenSource(userCancel.Token);
            whole.CancelAfter(TimeSpan.FromMinutes(4));
            foreach (string name in new[] { "wrong-observer-tokens", "owner-unref-first", "host-unref-first", "owner-disconnect" })
            {
                using var scenario = CancellationTokenSource.CreateLinkedTokenSource(whole.Token);
                scenario.CancelAfter(TimeSpan.FromSeconds(30));
                current = new(evidence, run, name, journal, witness);
                await current.RunAsync(scenario.Token).ConfigureAwait(false);
                object completed = current.State;
                journal.Write(name, "observer-scenario-pass", completed);
                summaries.Add(completed);
                current.CloseHandles();
                current = null;
            }
            ObserverSupport.Summary(evidence, new { state = "passed", scenarios = summaries });
            journal.Write("run", "observer-four-scenarios-passed", new { count = summaries.Count });
            exit = 0;
        }
        catch (Exception error)
        {
            bool unsupported = error is EnvelopeException { Failure: EnvelopeFailure.CapacityUnsupported };
            Record(journal, "run", "observer-probe-failed", ObserverSupport.Safe(error));
            if (current is not null)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await current.CleanupAsync(finishKnown: true, cleanup.Token).ConfigureAwait(false); }
                catch (Exception failure) { Record(journal, "run", "initial-cleanup-failed", ObserverSupport.Safe(failure)); }
                if (!current.Closed)
                {
                    Record(journal, "run", "failed-retained-guardian-alive", new { guardian = Path.Combine(evidence, "guardian.sock"),
                        host = hostIdentity, state = current.State });
                    try { ObserverSupport.Summary(evidence, new { state = "failed-retained", scenarios = summaries, retained = current.State }); }
                    catch (Exception) { /* do not drop objects because evidence storage failed */ }
                    Console.Error.WriteLine("observer probe failed-retained: guardian remains alive; inspect private evidence");
                    await guardian.WaitRetainedAsync().ConfigureAwait(false);
                    ObserverSupport.Require(current.Closed, "observer-probe-guardian-resolution-not-closed");
                }
                current.CloseHandles();
            }
            exit = unsupported ? 42 : 1;
            try { ObserverSupport.Summary(evidence, new { state = unsupported ? "capability-unsupported-cleaned" : "failed-cleaned",
                scenarios = summaries, error = ObserverSupport.Safe(error) }); }
            catch (Exception) { exit = 1; }
        }
        // These are reached only after every created real owner has closed.
        try
        {
            ObserverSupport.AtomicJson(evidence, "source-manifest.json", new
            { state = "owners-closed", exitCode = exit, artifacts, managerVersion = witness?.ManagerVersion });
        }
        catch (Exception) { exit = 1; }
        witness?.Close();
        await guardian.CloseAsync().ConfigureAwait(false);
        Console.CancelKeyPress -= onCancel;
        return exit;
    }

    private static async Task<int> RunOwnerAsync(string evidence, string run, string scenario, uint parentPid, ulong parentTicks)
    {
        ProbeProcess self = ProbeProcess.Read((uint)Environment.ProcessId), parent = ProbeProcess.Read(parentPid);
        ObserverSupport.Require(self.ParentPid == parentPid && parent.StartTicks == parentTicks
            && self.Executable == parent.Executable, "observer-owner-parent-bootstrap-mismatch");
        ObserverSupport.MakePrivateDirectory(evidence);
        using var journal = new EvidenceJournal(evidence);
        var state = new ObserverOwnerState(journal, evidence, scenario);
        var wire = new ProbeWire(Console.OpenStandardInput(), Console.OpenStandardOutput(), run, scenario);
        var guardian = new ProbeGuardian(evidence, state.GuardianAsync);
        long sequence = 0;
        int exit = 1;
        using var userCancel = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, value) => { value.Cancel = true; userCancel.Cancel(); };
        Console.CancelKeyPress += onCancel;
        try
        {
            journal.Write(scenario, "owner-bootstrap", new { self, parent });
            while (true)
            {
                using var control = CancellationTokenSource.CreateLinkedTokenSource(userCancel.Token);
                control.CancelAfter(TimeSpan.FromSeconds(30));
                // Console stdin may not interrupt an entered read on this token.
                // The parent owns its writer and closes it on failed/canceled
                // exchanges; real EOF then sends this SAME reader into cleanup.
                // The 30s control token is cooperative, not an independent read deadline.
                ProbeMessage? command = await wire.ReadAsync(control.Token).ConfigureAwait(false);
                if (command is null)
                {
                    journal.Write(scenario, "owner-control-input-eof", state.State);
                    break;
                }
                ObserverSupport.Require(command.Sequence == ++sequence, "observer-owner-command-sequence");
                _ = command.Payload<EmptyPayload>();
                journal.Write(scenario, "owner-command-received", command);
                if (command.Kind == "ExitClean")
                {
                    ObserverSupport.Require(state.Closed, "observer-owner-exit-with-live-owners");
                    ObserverSupport.Summary(evidence, state.State);
                    journal.Write(scenario, "owner-clean-exit-ack", state.State);
                    await wire.SendAsync(sequence, "ExitCleanAck", state.State, control.Token).ConfigureAwait(false);
                    exit = 0;
                    break;
                }
                using var operation = CancellationTokenSource.CreateLinkedTokenSource(userCancel.Token);
                operation.CancelAfter(TimeSpan.FromSeconds(10));
                object reply = await state.CommandAsync(command.Kind, operation.Token).ConfigureAwait(false);
                journal.Write(scenario, "owner-command-complete", new { sequence, command.Kind, state = state.State });
                await wire.SendAsync(sequence, command.Kind + "Ack", reply, control.Token).ConfigureAwait(false);
            }
        }
        catch (Exception error)
        {
            Record(journal, scenario, "owner-operation-failed", ObserverSupport.Safe(error));
            try
            {
                using var replyDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                if (sequence > 0) await wire.SendAsync(sequence, "Fault", ObserverSupport.Safe(error), replyDeadline.Token).ConfigureAwait(false);
            }
            catch (Exception) { /* pipe failure does not release state */ }
        }
        if (!state.Closed)
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await state.CleanupAsync(finishKnown: true, cleanup.Token).ConfigureAwait(false); }
            catch (Exception error) { Record(journal, scenario, "owner-cleanup-failed", ObserverSupport.Safe(error)); }
        }
        if (!state.Closed)
        {
            Record(journal, scenario, "owner-failed-retained-guardian-alive", state.State);
            try { ObserverSupport.Summary(evidence, state.State); } catch (Exception) { }
            await guardian.WaitRetainedAsync().ConfigureAwait(false);
        }
        // Never exit merely because the parent's transport disconnected.
        ObserverSupport.Require(state.Closed, "observer-owner-cleanup-not-closed");
        ObserverSupport.Summary(evidence, state.State);
        await guardian.CloseAsync().ConfigureAwait(false);
        Console.CancelKeyPress -= onCancel;
        return exit;
    }
}
