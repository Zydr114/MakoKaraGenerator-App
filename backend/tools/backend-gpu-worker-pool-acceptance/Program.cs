using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Processes;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting.Compilation;

if (!args.Contains("--run-real-gpu", StringComparer.Ordinal))
{
    Console.WriteLine("Prepared only. Real device run requires --run-real-gpu --worker <isolated-dist/worker/Worker.dll> --output <App/.local/run>. Optional --snapshot <source-profile-bytes>, --rapid-count 8, --malformed-worker-only. This Host/Proxy probe opens real pools without a consumer; desktop completed-frame acceptance remains separate.");
    return;
}
if (args.Contains("--malformed-worker-only", StringComparer.Ordinal)) await MalformedWorkerProbe.RunAsync(args);
else if (args.Contains("--dual-consumer-driver", StringComparer.Ordinal)) await DualConsumerDriver.RunAsync(args);
else await new Acceptance(args).RunAsync();

sealed class Acceptance
{
    private readonly string[] _args;
    private readonly ConcurrentDictionary<string, ProcessIdentity> _observed = new();
    private readonly List<GpuStreamWorkerSession> _sessions = [];
    private readonly HashSet<string> _boundIdentities = [];
    private readonly object _logGate = new();
    private string _output = "", _worker = "";
    private GpuWorkerPool? _pool;
    private long _generation;
    private readonly string _host = Guid.NewGuid().ToString("N");
    private int _maximumSlots;
    internal Acceptance(string[] args) => _args = args;

    internal async Task RunAsync()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Real GPU acceptance requires Linux.");
        _worker = Path.GetFullPath(Option("--worker") ?? throw new ArgumentException("--worker required"));
        _output = Path.GetFullPath(Option("--output") ?? throw new ArgumentException("--output required"));
        if (!_output.Contains("/.local/", StringComparison.Ordinal) ||
            !_worker.StartsWith(Path.Combine(_output, "dist") + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("Use an isolated App .local deployment and output.");
        Directory.CreateDirectory(_output);
        if (File.Exists(Path.Combine(_output, "events.jsonl"))) throw new IOException("Use a fresh output directory.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using var sampleStop = new CancellationTokenSource();
        Task sampling = Task.CompletedTask;
        Exception? failure = null;
        BackendPreparedProjectRuntimeInput? input = null;
        var options = new WorkerProcessOptions("dotnet", [_worker]) { RenderBackend = "gpu", EnableGpuPreview = true,
            ScriptCompilationService = new ScriptCompiler(Path.Combine(Path.GetDirectoryName(_worker)!, "script-refs", "net10.0"),
                Path.Combine(_output, "compiler-artifacts")) };
        Record("manifest", new { runClass = "real-host-proxy-finite-not-performance", worker = Path.GetFileName(_worker),
            workerSha256 = Hash(_worker), deploymentSha256 = OptionalHash(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(_worker))!, "deployment.json")),
            sourceSnapshotSha256 = Option("--snapshot") is { } source ? Hash(source) : null,
            fixture = Option("--snapshot") is null ? "default-color-no-author-code" : "supplied-source-profile",
            consumerBoundary = "Real Vulkan/Skia devices, business sessions and pools; no consumer connects, no frame completion/consumer resource-release claim",
            timingBoundary = "stopwatch observer samples, includes admission/ref preparation/business bind/pool open; not HTTP-to-present or GPU completion latency",
            ttlSeconds = 30, initialBackoffMs = 250, maximumBackoffSeconds = 30, ownPid = Environment.ProcessId });
        try
        {
            ProjectSnapshot snapshot = Option("--snapshot") is { } path
                ? ProjectSnapshotSourceProfile.Decode(await File.ReadAllBytesAsync(path, deadline.Token))
                : ProjectSnapshot.FromProject(ProjectWorkspace.CreateNew("M04 acceptance").Project);
            using var capture = BackendProjectRuntimeInputLease.CreateDetached(snapshot);
            var preparation = await capture.PrepareAsync(deadline.Token);
            Require(preparation.Accepted, "fixture preparation rejected"); input = preparation.Response!;
            Record("fixture-prepared", new { sourceSha256 = WorkerProtocol.ComputeSha256(input.SourceBytes.Span), input.SourceProfile,
                codeBoundary = Option("--snapshot") is null ? "No author code in default fixture" : "Provided snapshot packages preserved" });
            _pool = new(options);
            Record("actual-deployment-closure", new { _pool.DeploymentClosureSize.Files, _pool.DeploymentClosureSize.Bytes,
                identity = _pool.Read().ConfigurationIdentity, boundary = "Actual complete Worker file bytes plus deployment/native manifests hashed at preparation boundaries" });
            sampling = SampleLoopAsync(sampleStop.Token);
            // Admit immediately while the constructor's first real warmup is opening.
            // Admission itself supplies the exact miss classification; do not infer it from elapsed time.
            var initialCold = await OpenAsync(input, "initial-no-ready-spare", deadline.Token);
            Record("initial-admission", new { initialCold.WorkerAdmission });
            await CloseAsync(initialCold, "initial-no-ready-spare", deadline.Token);
            var watch = Stopwatch.StartNew();
            await WaitForAsync(() => _pool.Read().Spare == 1, deadline.Token);
            Record("initial-spare-ready", new { elapsedMs = watch.Elapsed.TotalMilliseconds, workers = _pool.ReadPrivateDiagnostics() });

            var first = await OpenAsync(input, "first-spare-hit", deadline.Token);
            await WaitForAsync(() => _pool.Read().Spare == 1, deadline.Token);
            var second = await OpenAsync(input, "second-active", deadline.Token);
            Require(_pool.Read() is { Bound: 2, Total: 2, Spare: 0 }, "two actual active pools share two slots");
            var before = _pool.ReadPrivateDiagnostics().Select(w => w.ProcessId).Order().ToArray();
            _ = await _pool.VerifyDeviceAsync(deadline.Token);
            Require(before.SequenceEqual(_pool.ReadPrivateDiagnostics().Select(w => w.ProcessId).Order()), "availability must not create a helper");
            Record("two-active-device-query", new { pool = _pool.Read(), workers = _pool.ReadPrivateDiagnostics() });
            await ExpectRejectedAsync(async () => { await using var extra = await _pool.AcquireAsync(deadline.Token); }, "third-admission-denied");

            await CloseAsync(second, "two-to-one", deadline.Token);
            await WaitForAsync(() => _pool.Read() is { Bound: 1, Spare: 1, Total: 2 }, deadline.Token);
            Record("single-active-replenished", new { pool = _pool.Read(), workers = _pool.ReadPrivateDiagnostics() });

            // Invalid snapshot JSON is rejected by the real Host source validator.
            // Its acquired single-use Worker lease must still drain, never return to spare.
            ProcessIdentity malformedOriginal;
            await using (var malformed = await _pool.AcquireAsync(deadline.Token))
            {
                malformedOriginal = malformed.Worker.GpuWarmupProcessIdentity!;
                await ExpectRejectedAsync(async () => { _ = await malformed.Worker.OpenRuntimeInputSessionAsync("{}"u8.ToArray(), TimeSpan.FromSeconds(20), deadline.Token); }, "malformed-input-rejected");
                Record("failed-business-attempt", new { original = malformedOriginal, prepared = malformed.Worker.GpuWarmup,
                    boundary = "Real Host source validation rejects structurally invalid snapshot before Worker request; acquired single-use lease is drained" });
            }
            await WaitForAsync(() => !Alive(malformedOriginal), deadline.Token);
            Require(!Alive(malformedOriginal), "invalid input lease must drain original process");
            await WaitForAsync(() => _pool.Read().Spare == 1, deadline.Token);

            for (int i = 0; i < int.Parse(Option("--rapid-count") ?? "8", CultureInfo.InvariantCulture); i++)
            {
                // Do not wait for replenishment after close: real hit/miss state is recorded by OpenAsync.
                var replacement = await OpenAsync(input, "rapid-" + i, deadline.Token);
                await CloseAsync(replacement, "rapid-" + i, deadline.Token);
            }

            // A stream with no consumer has a 30-second connection deadline.
            // Close it before the separate real spare TTL wait; dual-main proves
            // that a connected remaining consumer continues while spare replenishes.
            await CloseAsync(first, "final-active-close-before-spare-ttl", deadline.Token);

            await WaitForAsync(() => _pool.Read().Spare == 1, deadline.Token);
            var oldSpare = _pool.ReadPrivateDiagnostics().Single(w => w.State == "Spare").OwnedIdentity!;
            Record("ttl-start", new { oldSpare });
            await Task.Delay(TimeSpan.FromSeconds(31), deadline.Token);
            await WaitForAsync(() => _pool.Read().Spare == 1 && _pool.ReadPrivateDiagnostics().Single(w => w.State == "Spare").OwnedIdentity != oldSpare, deadline.Token);
            Require(!Alive(oldSpare), "TTL must drain previous original identity");
            Record("ttl-replaced", new { oldSpare, pool = _pool.Read(), workers = _pool.ReadPrivateDiagnostics() });

            // Only mutate this isolated deployment's existing manifest; restore exact bytes in finally.
            string manifest = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(_worker))!, "deployment.json");
            byte[] originalManifest = await File.ReadAllBytesAsync(manifest, deadline.Token);
            var obsolete = _pool.ReadPrivateDiagnostics().Single(w => w.State == "Spare").OwnedIdentity!;
            try
            {
                await File.WriteAllBytesAsync(manifest, [.. originalManifest, (byte)'\n'], deadline.Token);
                _ = await _pool.VerifyDeviceAsync(deadline.Token);
                await WaitForAsync(() => _pool.Read().Spare == 1, deadline.Token);
                Require(!Alive(obsolete), "changed deployment drains old unused spare");
                Record("deployment-invalidated", new { obsolete, workers = _pool.ReadPrivateDiagnostics() });
            }
            finally { await File.WriteAllBytesAsync(manifest, originalManifest, CancellationToken.None); }
            // Restoration is another configuration transition; query must never use the old active process as proof.
            _ = await _pool.VerifyDeviceAsync(deadline.Token);
            await WaitForAsync(() => _pool.Read() is { Bound: 0, Spare: 1 }, deadline.Token);
        }
        catch (Exception error)
        { failure = error; Record("failure", new { type = error.GetType().Name, message = error.Message }); }
        finally
        {
            foreach (var session in _sessions.ToArray())
                try { await session.DisposeAsync(); } catch (Exception error) { failure ??= error; Record("session-cleanup-failed", new { type = error.GetType().Name }); }
            if (_pool is not null)
                try { await _pool.DisposeAsync(); } catch (Exception error) { failure ??= error; Record("pool-cleanup-failed", new { type = error.GetType().Name }); }
            sampleStop.Cancel();
            try { await sampling; }
            catch (OperationCanceledException) { }
            catch (Exception error) { failure ??= error; Record("sampling-failed", new { type = error.GetType().Name }); }
            input?.Dispose();
            var remaining = _observed.Values.Where(Alive).ToArray();
            Record("shutdown", new { passed = failure is null && remaining.Length == 0 && _maximumSlots <= 2,
                maximumSlots = _maximumSlots, observedIdentities = _observed.Values.ToArray(), remaining,
                terminationBoundary = "Every observed exact PID/startTicks identity gone; group metadata is diagnostic; proxy owned-group/tree StopAsync separately verified on successful dispose" });
            if (remaining.Length != 0) failure ??= new IOException("Owned process drain incomplete");
        }
        if (failure is not null) throw new InvalidOperationException("M04 real acceptance failed; inspect private events.jsonl", failure);
    }

    private async Task<GpuStreamWorkerSession> OpenAsync(BackendPreparedProjectRuntimeInput input, string name, CancellationToken token)
    {
        var before = _pool!.Read(); var workers = _pool.ReadPrivateDiagnostics();
        string generation = Interlocked.Increment(ref _generation).ToString(CultureInfo.InvariantCulture);
        var timer = Stopwatch.StartNew();
        var session = await GpuStreamWorkerSession.OpenAsync(_pool, input, "m04-" + generation, _host,
            generation, 1280, 720, false, Environment.ProcessId, token);
        _sessions.Add(session);
        var current = _pool.ReadPrivateDiagnostics().Single(w => w.ProcessId == session.Opened.WorkerPid);
        var proof = session.Opened.WarmupBinding;
        Require(current.Preparation is { DeviceInitializations: 1, ContextInitializations: 1 } &&
            proof is { DeviceInitializations: 1, ContextInitializations: 1, OriginalDeviceBound: true } &&
            proof.PreparationId == current.Preparation.PreparationId && proof.InitializationId == current.Preparation.InitializationId,
            "business pool open must reuse original once-initialized device/context");
        Require(current.OwnedIdentity is not null && _boundIdentities.Add(Key(current.OwnedIdentity)),
            "each business input generation owns a fresh original process");
        Record("business-open", new { name, elapsedMs = timer.Elapsed.TotalMilliseconds,
            admission = session.WorkerAdmission, classificationBoundary = "pool admission lock records prepared-spare-hit, opening-spare-wait or cold-reservation",
            before, workersBefore = workers, process = current.OwnedIdentity,
            stream = new { session.Opened.StreamId, hostInstanceId = _host, inputGeneration = generation,
                session.Opened.ProducerEpoch, session.Opened.PoolGeneration, consumerEpoch = (string?)null },
            session.Opened, current.Preparation });
        return session;
    }

    private async Task CloseAsync(GpuStreamWorkerSession session, string name, CancellationToken token)
    {
        var process = _pool!.ReadPrivateDiagnostics().Single(w => w.ProcessId == session.Opened.WorkerPid).OwnedIdentity!;
        var timer = Stopwatch.StartNew();
        await session.DisposeAsync(); _sessions.Remove(session);
        await WaitForAsync(() => !Alive(process), token);
        Require(session.TerminationConfirmed, "exact owned worker drain required");
        Record("business-close", new { name, elapsedMs = timer.Elapsed.TotalMilliseconds, process,
            session.Opened.StreamId, session.Opened.ProducerEpoch, session.TerminationConfirmed,
            consumerBoundary = "No consumer connected; no external consumer lease-release claim" });
    }

    private async Task SampleLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var workers = _pool!.ReadPrivateDiagnostics(); var state = _pool.Read();
            _maximumSlots = Math.Max(_maximumSlots, state.Total);
            foreach (var worker in workers)
            {
                var own = worker.OwnedIdentity ?? (worker.ProcessId is { } pid ? ReadIdentity(pid) : null);
                if (own is null) continue;
                _observed.TryAdd(Key(own), own);
                foreach (string directory in Directory.EnumerateDirectories("/proc"))
                    if (int.TryParse(Path.GetFileName(directory), out int child) && ReadIdentity(child) is { } member &&
                        member.ProcessGroupId == own.ProcessGroupId && member.StartTicks >= own.StartTicks)
                        _observed.TryAdd(Key(member), member);
            }
            Record("pool-sample", new { state, workers, observedLive = _observed.Values.Where(Alive).ToArray() });
            Require(state.Total <= 2 && state.Spare <= 1, "hard process slot budget");
            await Task.Delay(100, token);
        }
    }

    private string? Option(string key) { int i = Array.IndexOf(_args, key); return i >= 0 && i + 1 < _args.Length ? _args[i + 1] : null; }
    private void Record(string type, object data)
    { lock (_logGate) File.AppendAllText(Path.Combine(_output, "events.jsonl"), JsonSerializer.Serialize(new { type, monotonicTicks = Stopwatch.GetTimestamp(), stopwatchFrequency = Stopwatch.Frequency, utc = DateTimeOffset.UtcNow, data }) + "\n"); }
    private static async Task WaitForAsync(Func<bool> predicate, CancellationToken token)
    { using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(25)); while (!predicate()) await Task.Delay(20, timeout.Token); }
    private async Task ExpectRejectedAsync(Func<Task> action, string name)
    { try { await action(); } catch (Exception error) when (error is WorkerProtocolException or InvalidOperationException or ArgumentException) { Record(name, new { type = error.GetType().Name }); return; } throw new InvalidOperationException(name + " unexpectedly accepted"); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static string Key(ProcessIdentity identity) => identity.ProcessId + ":" + identity.StartTicks;
    // Opening process observation may precede setsid; group membership is diagnostic,
    // while PID + startTicks remains the stable original-process ownership identity.
    private static bool Alive(ProcessIdentity identity) => ReadIdentity(identity.ProcessId) is { } current
        && current.ProcessId == identity.ProcessId && current.StartTicks == identity.StartTicks;
    private static ProcessIdentity? ReadIdentity(int pid)
    {
        try { string stat = File.ReadAllText($"/proc/{pid}/stat"); string[] fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            return new(pid, ulong.Parse(fields[19], CultureInfo.InvariantCulture), int.Parse(fields[2], CultureInfo.InvariantCulture)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or IndexOutOfRangeException) { return null; }
    }
    private static string Hash(string file) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)));
    private static string? OptionalHash(string file) => File.Exists(file) ? Hash(file) : null;
}
