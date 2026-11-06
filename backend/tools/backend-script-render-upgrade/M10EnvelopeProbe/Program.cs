using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MakoKaraGenerator.Processes.MediaEnvelope;

namespace M10.NativeEnvelope.Probe;

internal static class Program
{
    private static readonly JsonSerializerOptions SummaryOptions = new() { WriteIndented = true };
    private const long NativeBytes = 64L * 1024 * 1024;
    private const long ManagedBytes = 2L * 1024 * 1024;
    private static readonly List<object> FailedTerminalRoots = [];
    private static readonly EnvelopeExecutionPolicy Policy = new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(25));

    public static async Task<int> Main(string[] arguments)
    {
        if (arguments.Length > 0 && arguments[0] is "--observer-evidence" or "--observer-owner")
            return await ObserverProbe.DispatchAsync(arguments).ConfigureAwait(false);
        if (arguments.Length != 2 || arguments[0] != "--evidence" || !Path.IsPathFullyQualified(arguments[1]))
        {
            Console.Error.WriteLine("usage: M10.NativeEnvelope.Probe --evidence <new-absolute-private-directory>");
            return 64;
        }
        string evidence = Path.GetFullPath(arguments[1]);
        if (Directory.Exists(evidence))
        {
            Console.Error.WriteLine("The evidence directory must be new.");
            return 64;
        }
        using var journal = new EvidenceJournal(evidence);
        using var wholeRun = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        string tools = Path.Combine(AppContext.BaseDirectory, "media-tools");
        string gate = Path.Combine(tools, "mkg-media-exec-gate");
        string helper = Path.Combine(tools, "mkg-envelope-probe-helper");
        journal.Write("run", "probe-start", new { processId = Environment.ProcessId, started = DateTimeOffset.UtcNow,
            scope = "standalone-native-envelope-fixture", orchestratorIdentity = AuditedEnvelopeBackend.Session.ReadProc((uint)Environment.ProcessId), nativeBytes = NativeBytes, managedBytes = ManagedBytes,
            totalBudget = 268435456, toolHashes = new { gate = Hash(gate), helper = Hash(helper),
                bridge = Hash(Path.Combine(tools, "libmkg_media_envelope.so")) } });
        var scenarios = new (string Name, string Mode)[]
        {
            ("four-fd-gate-natural-exit", "echo"), ("native-nonzero", "nonzero"),
            ("caller-cancel", "wait"), ("pipe-stall-timeout", "stall"),
            ("oom-subtree-aggregate", "oom-subtree"), ("cancel-during-borrow", "wait"),
            ("wrong-ownership-tokens", "echo"),
        };
        var summaries = new List<object>();
        foreach ((string name, string mode) in scenarios)
        {
            try
            {
                object summary = await RunScenarioAsync(journal, name, mode, gate, helper, wholeRun.Token).ConfigureAwait(false);
                summaries.Add(summary);
                journal.Write(name, "scenario-pass", summary);
                Console.WriteLine(name + ": pass");
            }
            catch (EnvelopeException error) when (error.Failure == EnvelopeFailure.CapacityUnsupported)
            {
                journal.Write(name, "platform-unsupported", new { error.Failure, error.SafeCode });
                WriteSummary(evidence, "capacity-unsupported", summaries);
                Console.Error.WriteLine("native envelope capability unsupported: " + error.SafeCode);
                return 42;
            }
            catch (Exception error)
            {
                // Error text may contain paths. Persist only known safe code/category.
                journal.Write(name, "scenario-fail", SafeError(error));
                WriteSummary(evidence, "failed", summaries);
                Console.Error.WriteLine(name + ": failed; inspect the private evidence journal");
                return 1;
            }
        }
        journal.Write("run", "all-scenarios-passed", new { count = summaries.Count });
        WriteSummary(evidence, "passed", summaries);
        return 0;
    }

    private static async Task<object> RunScenarioAsync(EvidenceJournal journal, string name, string mode,
        string gate, string helper, CancellationToken wholeRun)
    {
        using var scenario = CancellationTokenSource.CreateLinkedTokenSource(wholeRun);
        scenario.CancelAfter(TimeSpan.FromSeconds(20));
        var source = new ProbeSource(journal, name);
        var budget = new DecodeOperationBudget(268435456, ManagedBytes, [NativeBytes], 2, source);
        var quarantine = new JournalQuarantine(journal, name);
        var backend = new AuditedEnvelopeBackend(new LinuxSystemdEnvelopeBackend(), journal, name,
            NativeBytes, exerciseWrongIdentity: name == "wrong-ownership-tokens");
        var owner = new OwnedNativeEnvelope(backend, quarantine, Policy);
        var pumps = new ProbePumps(journal, name);
        bool borrowKillObserved = false;
        Exception? outcome = null;
        EnvelopeCompletion? completion = null;
        Stopwatch duration = Stopwatch.StartNew();
        try
        {
            completion = await owner.RunAsync(budget.BeginNative(0), new(gate, helper, [mode]), async (session, token) =>
            {
                using var charged = budget.ReserveManaged(256 * 1024); // Actual bounded pump arrays + small records.
                using var pumpsStop = CancellationTokenSource.CreateLinkedTokenSource(token);
                pumps.Start(session, pumpsStop.Token);
                try
                {
                    HelperHello hello = await pumps.Hello.Task.WaitAsync(token).ConfigureAwait(false);
                    AuditedEnvelopeBackend.Session audited = backend.Opened ?? throw new ProbeFailure("probe-session-missing");
                    EnvelopeSnapshot held = audited.Held ?? throw new ProbeFailure("probe-held-proof-missing");
                    ValidateHelperOwner(hello, held);
                    journal.Write(name, "helper-same-pid-exec-proven", new { hello, held.MainPid, held.MainStartTicks });
                    // Memory/fork/stall negative controls are authorized only now,
                    // after both pre-exec and helper-side hard-limit proofs.
                    await session.Input.WriteAsync(Encoding.ASCII.GetBytes("GO\n"), token).ConfigureAwait(false);
                    journal.Write(name, "fixture-negative-or-normal-action-authorized", new { mode, nativeLimit = NativeBytes });
                    if (name == "caller-cancel")
                    {
                        await pumps.Waiting.Task.WaitAsync(token).ConfigureAwait(false);
                        journal.Write(name, "caller-cancellation-requested");
                        scenario.Cancel();
                    }
                    else if (name == "cancel-during-borrow")
                    {
                        await pumps.Waiting.Task.WaitAsync(token).ConfigureAwait(false);
                        using DecodeOperationBudget.ManagedLease pixels = budget.ReserveManaged(4096);
                        float[] canonical = new float[1024];
                        using IDisposable borrow = budget.BeginBorrow();
                        canonical[0] = 0.25f;
                        journal.Write(name, "borrow-enter", new { budget.ActiveBorrows, budget.LiveManagedBytes });
                        scenario.Cancel();
                        using var killWait = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                        await audited.KillCompleted.Task.WaitAsync(killWait.Token).ConfigureAwait(false);
                        if (budget.ActiveBorrows != 1 || source.Released || canonical[0] != 0.25f)
                            throw new ProbeFailure("probe-borrow-released-before-callback");
                        try { await budget.ReleaseSourceAsync().ConfigureAwait(false); }
                        catch (EnvelopeException error) when (error.SafeCode == "decode-lease-still-active")
                        { borrowKillObserved = true; }
                        if (!borrowKillObserved) throw new ProbeFailure("probe-source-released-during-borrow");
                        journal.Write(name, "owned-kill-while-borrow-still-live", new { budget.ActiveBorrows,
                            source.Released, liveManaged = budget.LiveManagedBytes });
                        // Deliberate finite callback residence after native termination.
                        await Task.Delay(150, CancellationToken.None).ConfigureAwait(false);
                        GC.KeepAlive(canonical);
                    }
                    await pumps.All.ConfigureAwait(false);
                }
                catch (OperationCanceledException error) when (error.CancellationToken == pumpsStop.Token && token.IsCancellationRequested)
                {
                    // This probe owns its pump-local link; return the envelope's
                    // consumer token so the public caller attribution is testable.
                    token.ThrowIfCancellationRequested();
                    throw;
                }
                finally
                {
                    pumpsStop.Cancel();
                    try { await pumps.All.ConfigureAwait(false); }
                    catch (OperationCanceledException) when (pumpsStop.IsCancellationRequested) { }
                }
            }, scenario.Token).ConfigureAwait(false);
        }
        catch (Exception error) { outcome = error; }

        AuditedEnvelopeBackend.Session? observed = backend.Opened;
        journal.Write(name, "owner-returned", new { durationMs = duration.ElapsedMilliseconds, completion,
            error = outcome is null ? null : SafeError(outcome), quarantineCount = quarantine.Count,
            cancellationTokenMatches = outcome is OperationCanceledException canceled && canceled.CancellationToken == scenario.Token,
            sourceReleased = source.Released, budget.ActiveBorrows, budget.LiveManagedBytes,
            lastSnapshot = observed?.Last });
        if (outcome is EnvelopeException { Failure: EnvelopeFailure.CapacityUnsupported })
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(outcome).Throw();
        if (quarantine.Count != 0)
        {
            // Do not drop this record and proceed as if native cleanup succeeded.
            // The fixture's own per-process alarm bounds lingering fixture PIDs.
            journal.Write(name, "quarantine-failed-terminal-stop-suite", new { quarantine.Count });
            FailedTerminalRoots.Add(quarantine);
            FailedTerminalRoots.Add(budget);
            throw new ProbeFailure("probe-native-owner-quarantined");
        }
        if (observed is null || !observed.GateReleased || !observed.Unreferenced || !observed.Disposed)
            throw new ProbeFailure("probe-owned-session-not-fully-drained");
        if (observed.Last is not { Terminal: true, CgroupPopulated: false } last
            || !(last.CgroupExists || last.CgroupRemoved))
            throw new ProbeFailure("probe-final-cgroup-not-drained");
        VerifyExpectedOutcome(name, outcome, observed);
        if (name is "caller-cancel" or "cancel-during-borrow")
        {
            if (outcome is not OperationCanceledException callerCancellation || callerCancellation.CancellationToken != scenario.Token)
                throw new ProbeFailure("probe-public-cancellation-token-mismatch");
        }
        if (name == "cancel-during-borrow" && !borrowKillObserved)
            throw new ProbeFailure("probe-borrow-cancel-evidence-missing");
        if (mode is "echo" or "nonzero")
        {
            if (Encoding.ASCII.GetString(pumps.Stdout) != "MKGPROBE-OUT\n"
                || Encoding.ASCII.GetString(pumps.Stderr) != "probe-diagnostic\n"
                || !pumps.MetadataEof || !pumps.StdoutEof || !pumps.StderrEof)
                throw new ProbeFailure("probe-four-fd-content-or-eof-mismatch");
        }
        if (mode == "oom-subtree")
        {
            if (!last.Oom || pumps.Hellos.Count < 2 || pumps.SeenPids.Count < 2 || !pumps.AggregateAllocationAuthorized)
                throw new ProbeFailure("probe-subtree-oom-evidence-incomplete");
            foreach (HelperHello hello in pumps.Hellos) ValidateHelperGroup(hello, observed.Held!);
        }
        await budget.ReleaseSourceAsync().ConfigureAwait(false);
        if (!source.Released || budget.LiveManagedBytes != 0 || budget.ActiveBorrows != 0)
            throw new ProbeFailure("probe-final-source-or-buffer-reservation-live");
        VerifyCapturedProcessesGone(pumps.ProcessIdentities, journal, name);
        return new { scenario = name, elapsedMs = duration.ElapsedMilliseconds, outcome = outcome is null ? "success" : SafeError(outcome),
            observed.KillRequests, observed.RejectedIdentityRequests, sourceReleased = source.Released,
            finalIdentity = observed.Identity, finalState = observed.Last, fixturePids = pumps.SeenPids };
    }

    private static void ValidateHelperOwner(HelperHello hello, EnvelopeSnapshot held)
    {
        ValidateHelperGroup(hello, held);
        if (hello.Pid != held.MainPid || hello.StartTicks != held.MainStartTicks)
            throw new ProbeFailure("probe-gate-exec-changed-pid-or-startticks");
    }

    private static void ValidateHelperGroup(HelperHello hello, EnvelopeSnapshot held)
    {
        if (hello.MemoryMax != NativeBytes || hello.SwapMax != 0 || hello.OomGroup != 1 || hello.ControlGroup != held.ControlGroup)
            throw new ProbeFailure("probe-helper-kernel-envelope-mismatch");
    }

    private static void VerifyExpectedOutcome(string scenario, Exception? error, AuditedEnvelopeBackend.Session session)
    {
        bool correct = scenario switch
        {
            "four-fd-gate-natural-exit" => error is null && session.KillRequests == 0,
            "wrong-ownership-tokens" => error is null && session.KillRequests == 0 && session.RejectedIdentityRequests == 4,
            "native-nonzero" => error is EnvelopeException { Failure: EnvelopeFailure.DecodeFailed } && session.Last?.ExecMainStatus == 17,
            "caller-cancel" or "cancel-during-borrow" => error is OperationCanceledException && session.KillRequests > 0,
            "pipe-stall-timeout" => error is EnvelopeException { Failure: EnvelopeFailure.DecodeFailed, SafeCode: "native-pipe-progress-timeout" }
                && session.KillRequests > 0,
            "oom-subtree-aggregate" => error is EnvelopeException { Failure: EnvelopeFailure.CapacityExceeded } && session.Last?.Oom == true,
            _ => false,
        };
        if (!correct) throw new ProbeFailure("probe-unexpected-outcome");
    }

    private static void VerifyCapturedProcessesGone(IReadOnlyDictionary<uint, ulong> identities,
        EvidenceJournal journal, string scenario)
    {
        foreach ((uint pid, ulong expectedTicks) in identities)
        {
            string directory = "/proc/" + pid.ToString(CultureInfo.InvariantCulture);
            bool sameIdentity = false;
            string state = "absent";
            try
            {
                string stat = File.ReadAllText(directory + "/stat");
                string[] fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                ulong actualTicks = ulong.Parse(fields[19], CultureInfo.InvariantCulture);
                sameIdentity = actualTicks == expectedTicks;
                state = sameIdentity ? fields[0] : "reused-pid";
                journal.Write(scenario, "fixture-pid-after-drain", new { pid, expectedTicks, actualTicks, sameIdentity, state });
            }
            catch (IOException)
            { journal.Write(scenario, "fixture-pid-after-drain", new { pid, expectedTicks, sameIdentity, state }); }
            if (sameIdentity) throw new ProbeFailure("probe-owned-fixture-pid-remains-after-drain");
            // A reused PID is never signalled. Exact start ticks distinguish it
            // from the fixture recorded before its memory/cancellation action.
        }
    }

    private static object SafeError(Exception error) => error switch
    {
        EnvelopeException envelope => new { category = envelope.Failure.ToString(), code = envelope.SafeCode, cause = envelope.SafeCauseCode },
        ProbeFailure probe => new { category = "probe-assertion", code = probe.Message },
        OperationCanceledException => new { category = "canceled", code = "operation-canceled" },
        _ => new { category = "unexpected-managed-failure", code = "inspect-source-and-evidence" },
    };

    private static string Hash(string path)
    { using FileStream input = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(input)); }
    private static void WriteSummary(string directory, string status, List<object> scenarios)
        => File.WriteAllText(Path.Combine(directory, "summary.json"), JsonSerializer.Serialize(new { status, scenarios },
            SummaryOptions));

    private sealed class ProbeSource(EvidenceJournal journal, string scenario) : IAsyncDisposable
    {
        internal bool Released { get; private set; }
        public ValueTask DisposeAsync()
        { Released = true; journal.Write(scenario, "source-lease-released"); return ValueTask.CompletedTask; }
    }

    private sealed class JournalQuarantine(EvidenceJournal journal, string scenario) : IEnvelopeQuarantine
    {
        private readonly List<EnvelopeQuarantine> _records = [];
        internal int Count => _records.Count;
        public void Retain(EnvelopeQuarantine record)
        {
            _records.Add(record);
            journal.Write(scenario, "quarantine-retained", new { record.SafeReason, record.Identity,
                nativeBytes = record.Reservation.Bytes, transportBytes = record.TransportReservation.Bytes,
                managedLiveBytes = record.Reservation.Operation.LiveManagedBytes });
        }
    }
}
