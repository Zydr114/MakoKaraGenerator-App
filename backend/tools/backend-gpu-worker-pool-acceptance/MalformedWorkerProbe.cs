using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Processes;

// Trusted tool-only raw-protocol probe. The production proxy still creates,
// warms, owns and drains the original process; no production behavior is patched.
internal static class MalformedWorkerProbe
{
    internal static async Task RunAsync(string[] args)
    {
        string Option(string key)
        {
            int index = Array.IndexOf(args, key);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : throw new ArgumentException(key + " required");
        }
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Linux original-process proof required.");
        string output = Path.GetFullPath(Option("--output")), workerPath = Path.GetFullPath(Option("--worker"));
        if (!output.Contains("/.local/", StringComparison.Ordinal)
            || !workerPath.StartsWith(Path.Combine(output, "dist") + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("Use a fresh prepared App .local output/dist Worker.");
        string log = Path.Combine(output, "malformed-worker-events.jsonl");
        if (File.Exists(log)) throw new IOException("Use a fresh evidence directory.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var observed = new Dictionary<string, ProcessIdentity>(StringComparer.Ordinal);
        WorkerProcessProxy? proxy = null;
        SemaphoreSlim? callGate = null;
        bool gateHeld = false;
        Exception? failure = null;
        ProcessIdentity? original = null;
        void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        void Record(string type, object data) => File.AppendAllText(log, JsonSerializer.Serialize(new
        { type, monotonicTicks = Stopwatch.GetTimestamp(), stopwatchFrequency = Stopwatch.Frequency, utc = DateTimeOffset.UtcNow, data }) + "\n");
        string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        try
        {
            Record("manifest", new
            {
                runClass = "direct-worker-malformed-envelope-correctness-not-performance",
                workerSha256 = Hash(workerPath),
                deploymentSha256 = Hash(Path.Combine(output, "dist/deployment.json")),
                boundary = "Real production proxy warmup; original private pipes borrowed under original call gate only to observe consecutive raw errors before normal proxy fail-stop reaping. No consumer, pool, author code, or product modification.",
            });
            proxy = new(new WorkerProcessOptions("dotnet", [workerPath]) { RenderBackend = "gpu", EnableGpuPreview = true });
            GpuWorkerWarmupResult warmed = await proxy.WarmGpuAsync(deadline.Token);
            ProcessIdentity identity = proxy.GpuWarmupProcessIdentity ?? throw new InvalidOperationException("Warmup original identity missing.");
            original = identity;
            WorkerProcessProxy owner = proxy;
            Require(warmed.WorkerPid == identity.ProcessId && warmed.DeviceInitializations == 1
                && warmed.ContextInitializations == 1, "Real warmup original initialization proof required.");
            ObserveOriginalGroup();
            Record("real-proxy-warmed", new { original, warmed });

            // InvokeMessageAsync intentionally reaps any error. A trusted direct
            // caller must retain its pipes to test Worker state after that error.
            callGate = Field<SemaphoreSlim>(proxy, "_callGate");
            await callGate.WaitAsync(deadline.Token); gateHeld = true;
            Process originalProcess = Field<Process>(owner, "_process");
            Stream originalInput = Field<Stream>(owner, "_input");
            Stream originalOutput = Field<Stream>(owner, "_output");
            await RequireOriginalAsync();

            byte[] warmBytes = GpuWorkerWarmupContract.EncodeRequest(warmed.Identity);
            var beforeProof = await ReadOriginalDeviceAsync("before-malformed");
            Require(SameProof(beforeProof, warmed), "Original device before malformed input differs from proxy warmup.");

            // Valid Worker framing + SHA + correlation metadata; only the inner
            // KSO1 envelope is truncated. DecodeOpen deterministically throws
            // WorkerSnapshotBudgetExceeded after GpuWorkerPreparationState.Bind.
            Guid firstSession = Guid.NewGuid();
            byte[] malformed = [1, 2, 3];
            var first = new WorkerMessage(ProjectRuntimeInputWorkerContract.OpenKind, Guid.NewGuid().ToString("N"),
                WorkerProtocol.ComputeSha256(malformed), malformed, sessionId: firstSession.ToString("N"),
                sourceSha256: WorkerProtocol.ComputeSha256("{}"u8), frameSequence: 0);
            await ExpectRawErrorAsync(first, "WorkerSnapshotBudgetExceeded", "first-open-inner-envelope-rejected");

            // Exact valid original warmup identity and valid payload SHA: no new
            // preparation may occur in the same process after the business Bind.
            var warmAgain = new WorkerMessage(GpuWorkerWarmupContract.RequestKind, Guid.NewGuid().ToString("N"),
                WorkerProtocol.ComputeSha256(warmBytes), warmBytes);
            await ExpectRawErrorAsync(warmAgain, GpuWorkerWarmupContract.FailureCode, "same-process-rewarm-rejected");

            // Valid KSO1 header and length. A fresh Worker would get beyond Bind
            // and reject {} later; the already consumed Worker must reject Bind
            // with WorkerGpuWarmupInvalid before decoding this business source.
            Guid secondSession = Guid.NewGuid();
            byte[] validEnvelope = PackageRenderWorkerContract.EncodeOpen(secondSession, "{}"u8);
            var checkedEnvelope = PackageRenderWorkerContract.DecodeOpen(validEnvelope);
            Require(checkedEnvelope.SessionId == secondSession && checkedEnvelope.SnapshotBytes.AsSpan().SequenceEqual("{}"u8)
                && checkedEnvelope.SourceSha256 == WorkerProtocol.ComputeSha256("{}"u8),
                "Second business envelope must be complete and structurally valid.");
            var second = new WorkerMessage(ProjectRuntimeInputWorkerContract.OpenKind, Guid.NewGuid().ToString("N"),
                WorkerProtocol.ComputeSha256(validEnvelope), validEnvelope, sessionId: secondSession.ToString("N"),
                sourceSha256: WorkerProtocol.ComputeSha256("{}"u8), frameSequence: 0);
            await ExpectRawErrorAsync(second, GpuWorkerWarmupContract.FailureCode, "same-process-second-business-bind-rejected");
            var afterProof = await ReadOriginalDeviceAsync("after-malformed-and-rejections");
            Require(SameProof(afterProof, warmed), "Rejecting malformed business input recreated or replaced the original device.");
            await RequireOriginalAsync();
            Record("assertions-passed", new { original, firstError = "WorkerSnapshotBudgetExceeded",
                rewarmError = GpuWorkerWarmupContract.FailureCode, secondBusinessError = GpuWorkerWarmupContract.FailureCode,
                sameOriginalProcessAndPipes = true, samePreparationAndInitializationProof = true,
                boundary = "First malformed inner envelope reaches real Worker and consumes business Bind before DecodeOpen; subsequent valid warmup identity and business envelope cannot reuse it. No frame/consumer/performance claim." });

            async Task RequireOriginalAsync()
            {
                var lifecycle = Field<SemaphoreSlim>(owner, "_lifecycleGate");
                await lifecycle.WaitAsync(deadline.Token);
                try
                {
                    Require(ReferenceEquals(originalProcess, Field<Process>(owner, "_process"))
                        && ReferenceEquals(originalInput, Field<Stream>(owner, "_input"))
                        && ReferenceEquals(originalOutput, Field<Stream>(owner, "_output"))
                        && owner.ProcessId == identity.ProcessId && Alive(identity)
                        && !originalProcess.HasExited && owner.GpuWarmupProcessIdentity == identity,
                        "Original Worker PID/startTicks/process/pipe identity changed.");
                    ObserveOriginalGroup();
                }
                finally { lifecycle.Release(); }
            }

            async Task<WorkerMessage> ExchangeAsync(WorkerMessage request)
            {
                WorkerProtocol.Validate(request);
                Require(request.InputSha256 == WorkerProtocol.ComputeSha256(request.Payload), "Request payload SHA must be exact.");
                await RequireOriginalAsync();
                using var perCall = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                perCall.CancelAfter(TimeSpan.FromSeconds(20));
                await WorkerProtocol.WriteAsync(originalInput!, request, perCall.Token);
                WorkerMessage response = await WorkerProtocol.ReadAsync(originalOutput!, perCall.Token)
                    ?? throw new EndOfStreamException("Original Worker exited instead of returning a correlated response.");
                Require(response.RequestId == request.RequestId && response.InputSha256 == request.InputSha256
                    && response.SessionId == request.SessionId && response.SourceSha256 == request.SourceSha256
                    && response.FrameSequence == request.FrameSequence && !response.Cancelled,
                    "Raw Worker response correlation differs from exact request.");
                await RequireOriginalAsync();
                return response;
            }

            async Task ExpectRawErrorAsync(WorkerMessage request, string expectedCode, string phase)
            {
                WorkerMessage response = await ExchangeAsync(request);
                Require(response.Kind == "error" && response.ErrorCode == expectedCode
                    && !string.IsNullOrWhiteSpace(response.Error), "Unexpected Worker rejection code: " + phase);
                var expected = new WorkerMessage("error", request.RequestId, request.InputSha256,
                    error: response.Error, errorCode: expectedCode, sessionId: request.SessionId,
                    sourceSha256: request.SourceSha256, frameSequence: request.FrameSequence);
                Require(JsonSerializer.Serialize(response) == JsonSerializer.Serialize(expected),
                    "Raw error contains unnegotiated payload/diagnostic/result fields: " + phase);
                Record(phase, new { request.Kind, request.RequestId, request.InputSha256, request.SessionId,
                    request.SourceSha256, request.FrameSequence, payloadBytes = request.Payload.Length,
                    responseKind = response.Kind, response.ErrorCode, original });
            }

            async Task<GpuWorkerWarmupResult> ReadOriginalDeviceAsync(string phase)
            {
                var request = new WorkerMessage(GpuWorkerWarmupContract.DeviceStateKind, Guid.NewGuid().ToString("N"),
                    WorkerProtocol.ComputeSha256(warmBytes), warmBytes);
                var response = await ExchangeAsync(request);
                Require(response.Kind == GpuWorkerWarmupContract.DeviceStateResultKind
                    && response.OutputSha256 == WorkerProtocol.ComputeSha256(response.Payload)
                    && response.Error is null && response.ErrorCode is null,
                    "Original device-state proof unavailable: " + phase);
                var proof = GpuWorkerWarmupContract.DecodeResult(response.Payload, warmed.Identity);
                Require(proof.WorkerPid == identity.ProcessId, "Device proof PID changed.");
                Record("original-device-state", new { phase, original, proof });
                return proof;
            }
        }
        catch (Exception error)
        {
            failure = error;
            Record("failed", new { type = error.GetType().Name, error.Message });
        }
        finally
        {
            if (gateHeld) callGate!.Release();
            if (proxy is not null)
            {
                try { ObserveOriginalGroup(); await proxy.StopAsync(CancellationToken.None); await proxy.DisposeAsync(); }
                catch (Exception error) { failure ??= error; Record("owned-drain-failed", new { type = error.GetType().Name }); }
            }
            var remaining = observed.Values.Where(Alive).ToArray();
            if (remaining.Length != 0) failure ??= new IOException("Original owned identities remain after proxy Stop/Dispose.");
            if (proxy?.ProcessId is not null) failure ??= new IOException("Proxy process is not cleared after owned drain.");
            Record("final-owned-drain", new { passed = failure is null, observed = observed.Values.ToArray(), remaining,
                originalGone = original is not null && !Alive(original),
                proxyProcessCleared = proxy?.ProcessId is null,
                boundary = "Production proxy Stop/Dispose owns drain; stable PID/startTicks checked independently, group is diagnostic." });
        }
        if (failure is not null) throw new InvalidOperationException("Direct Worker malformed-envelope probe failed; inspect private events.", failure);

        void ObserveOriginalGroup()
        {
            if (original is null) return;
            observed[Key(original)] = original;
            foreach (string directory in Directory.EnumerateDirectories("/proc"))
                if (int.TryParse(Path.GetFileName(directory), out int pid) && Identity(pid) is { } member
                    && member.ProcessGroupId == original.ProcessGroupId && member.StartTicks >= original.StartTicks)
                    observed[Key(member)] = member;
        }
    }

    private static T Field<T>(object instance, string name) where T : class =>
        typeof(WorkerProcessProxy).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance) as T
        ?? throw new InvalidOperationException("Original proxy field changed: " + name);
    private static bool SameProof(GpuWorkerWarmupResult first, GpuWorkerWarmupResult second) =>
        JsonSerializer.Serialize(first) == JsonSerializer.Serialize(second);
    private static string Key(ProcessIdentity identity) => identity.ProcessId + ":" + identity.StartTicks;
    private static bool Alive(ProcessIdentity identity) => Identity(identity.ProcessId) is { } current
        && current.StartTicks == identity.StartTicks;
    private static ProcessIdentity? Identity(int pid)
    {
        try
        {
            string stat = File.ReadAllText($"/proc/{pid}/stat");
            string[] fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            return new(pid, ulong.Parse(fields[19], CultureInfo.InvariantCulture), int.Parse(fields[2], CultureInfo.InvariantCulture));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or IndexOutOfRangeException)
        { return null; }
    }
}
