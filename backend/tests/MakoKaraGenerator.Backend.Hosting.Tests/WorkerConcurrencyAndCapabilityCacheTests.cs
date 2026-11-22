using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class WorkerConcurrencyAndCapabilityCacheTests
{
    [Fact]
    public async Task WithRenderBackend共享能力探测缓存且未探测时同步属性非阻塞返回false()
    {
        using CapabilityProbe probe = await CapabilityProbe.CreateAsync();
        WorkerBackendValidationExecutor executor = new(probe.Options(), TimeSpan.FromSeconds(10));

        Assert.False(executor.PackageValidationAvailable);
        Assert.False(executor.ImageValidationAvailable);
        Assert.False(executor.RuntimeValidationAvailable);
        Assert.Empty(probe.ReadLaunchedPids());

        WorkerBackendValidationExecutor cpuBeforeProbe = Assert.IsType<WorkerBackendValidationExecutor>(executor.WithRenderBackend("cpu"));
        WorkerBackendValidationExecutor gpuBeforeProbe = Assert.IsType<WorkerBackendValidationExecutor>(executor.WithRenderBackend("gpu"));
        Assert.False(cpuBeforeProbe.PackageValidationAvailable);
        Assert.False(gpuBeforeProbe.RuntimeValidationAvailable);
        Assert.Empty(probe.ReadLaunchedPids());

        WorkerBackendValidationCapabilities[] probed = await Task.WhenAll(
            executor.ProbeCapabilitiesAsync(),
            cpuBeforeProbe.ProbeCapabilitiesAsync(),
            gpuBeforeProbe.ProbeCapabilitiesAsync());

        foreach (WorkerBackendValidationCapabilities capabilities in probed)
        {
            Assert.True(capabilities.PackageValidationAvailable);
            Assert.True(capabilities.ImageValidationAvailable);
            Assert.True(capabilities.RuntimeValidationAvailable);
        }

        int[] launchesAfterFirstProbe = probe.ReadLaunchedPids();
        Assert.Single(launchesAfterFirstProbe);
        await AssertDeadAsync(launchesAfterFirstProbe[0]);

        WorkerBackendValidationExecutor cpuAfterProbe = Assert.IsType<WorkerBackendValidationExecutor>(executor.WithRenderBackend("cpu"));
        WorkerBackendValidationExecutor gpuAfterProbe = Assert.IsType<WorkerBackendValidationExecutor>(executor.WithRenderBackend("gpu"));
        foreach (WorkerBackendValidationExecutor instance in new[] { executor, cpuBeforeProbe, gpuBeforeProbe, cpuAfterProbe, gpuAfterProbe })
        {
            Assert.True(instance.PackageValidationAvailable);
            Assert.True(instance.ImageValidationAvailable);
            Assert.True(instance.RuntimeValidationAvailable);
            WorkerBackendValidationCapabilities cached = await instance.ProbeCapabilitiesAsync();
            Assert.Equal(probed[0], cached);
        }

        Assert.Equal(launchesAfterFirstProbe, probe.ReadLaunchedPids());
    }

    [Fact]
    public async Task WorkerProcessProxy并发Start与Dispose无竞态且不泄漏子进程()
    {
        using LifecycleProbe probe = await LifecycleProbe.CreateAsync();
        for (int round = 0; round < 12; round++)
        {
            WorkerProcessProxy proxy = new(probe.Options(round));
            Task startTask = Task.Run(async () =>
            {
                try { await proxy.StartAsync(); }
                catch (ObjectDisposedException) { }
                catch (OperationCanceledException) { }
            });
            Task disposeFirst = Task.Run(async () => await proxy.DisposeAsync());
            Task disposeSecond = Task.Run(async () => await proxy.DisposeAsync());

            await Task.WhenAll(startTask, disposeFirst, disposeSecond).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Null(proxy.ProcessId);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => proxy.StartAsync());
            await Assert.ThrowsAsync<ObjectDisposedException>(() => proxy.InvokeAsync(new("echo", [1, 2, 3])));
        }

        foreach (int pid in probe.ReadLaunchedPids())
            await AssertDeadAsync(pid);
    }

    [Fact]
    public async Task WorkerProcessProxy在途Invoke期间并发Dispose立即取消并回收进程()
    {
        using LifecycleProbe probe = await LifecycleProbe.CreateAsync();
        WorkerProcessProxy proxy = new(probe.Options(0));
        await proxy.StartAsync();
        int pid = Assert.IsType<int>(proxy.ProcessId);

        Task<WorkerInvocationResult> invokeTask = proxy.InvokeAsync(new("echo", [4, 5, 6], DelayMilliseconds: 10_000));
        await probe.WaitForRequestAsync(TimeSpan.FromSeconds(5));

        Task disposeFirst = proxy.DisposeAsync().AsTask();
        Task disposeSecond = proxy.DisposeAsync().AsTask();

        await Assert.ThrowsAnyAsync<Exception>(() => invokeTask.WaitAsync(TimeSpan.FromSeconds(10)));
        await Task.WhenAll(disposeFirst, disposeSecond).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(proxy.ProcessId);
        await AssertDeadAsync(pid);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => proxy.InvokeAsync(new("echo", [7])));
    }

    [Fact]
    public async Task 作者草稿异步读取遵守CancellationToken且取消不破坏草稿锁()
    {
        using TemporaryDirectory directory = new();
        ResourceStore resources = new(new() { ResourceRoot = directory.FilePath("resources") });
        await using ProjectSessionRegistry registry = new("author-async-read", resources, NewProjectPreparationService.CreateDefault());
        registry.ConfigurePersistence(new() { StateRoot = directory.FilePath("state") });
        registry.ConfigureAuthorWorkflows(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));

        string archive = directory.FilePath("package.mkfx");
        await EffectPackageTransfer.WriteArchiveAsync(archive, EffectPackageFixture.Contents());
        await using FileStream stream = File.OpenRead(archive);
        StoredAsset asset = await resources.UploadAsync("client", "package.mkfx", "application/octet-stream", stream);

        BackendOperationResult<AuthorDraftResponse> created = await registry.CreateAuthorDraftAsync("client",
            new(asset.AssetId, "scale", "async-read", new(Guid.NewGuid().ToString("N"), registry.HostInstanceId, registry.ServerTimeUtc.AddMinutes(1))));
        Assert.True(created.Accepted, created.Error?.Message);
        AuthorDraftResponse draft = created.Response!;

        ConcurrentDictionary<string, SemaphoreSlim> locks = (ConcurrentDictionary<string, SemaphoreSlim>)typeof(ProjectSessionRegistry)
            .GetField("_authorDraftLocks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(registry)!;
        SemaphoreSlim gate = locks.GetOrAdd(draft.DraftId, static _ => new(1, 1));
        await gate.WaitAsync();
        try
        {
            using CancellationTokenSource cancellation = new();
            Task<BackendOperationResult<AuthorPackageFilesResponse>> pending = registry.ReadAuthorPackageFilesAsync(
                "client", draft.DraftId, draft.DraftRevision, draft.CurrentSourceRevision, cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { gate.Release(); }

        Assert.True((await registry.ReadAuthorPackageFilesAsync("client", draft.DraftId, draft.DraftRevision, draft.CurrentSourceRevision)).Accepted);
        Assert.True((await registry.ReadAuthorDraftAsync("client", draft.DraftId)).Accepted);
        Assert.True((await registry.ReadAuthorDraftsAsync("client")).Accepted);
        Assert.True((await registry.ReadAuthorSourcesAsync("client", draft.DraftId, draft.DraftRevision, draft.CurrentSourceRevision)).Accepted);
    }

    private static bool Alive(int pid)
    {
        try { using Process process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static async Task AssertDeadAsync(int pid)
    {
        for (int i = 0; i < 150 && Alive(pid); i++) await Task.Delay(20);
        Assert.False(Alive(pid), $"Worker PID {pid} was not reaped.");
    }

    private sealed class CapabilityProbe : IDisposable
    {
        private readonly TemporaryDirectory _temp = new();

        public static async Task<CapabilityProbe> CreateAsync()
        {
            CapabilityProbe probe = new();
            string refsDir = Path.Combine(probe._temp.Path, "script-refs", "net10.0");
            Directory.CreateDirectory(refsDir);
            await File.WriteAllBytesAsync(Path.Combine(refsDir, "System.Runtime.dll"), [0x4D, 0x5A, 0x90, 0x00]);
            ScriptReferenceIdentity refs = ScriptReferenceIdentity.Read(refsDir);

            EffectPackageProfile profile = ProjectEffectsJson.PackageProfile;
            WorkerPackageProfileDto[] supported = [new(EffectPackageProfiles.V2.PackageVersion, EffectPackageProfiles.V2.SdkVersion, EffectPackageProfiles.V2.ShaderProfile)];
            PackageRenderWorkerCapabilities packageCapabilities = new(
                StaticProjectSnapshotCodec.EngineIdentity,
                PackageProjectSnapshotCodec.Profile,
                profile.PackageVersion,
                profile.SdkVersion,
                profile.ShaderProfile,
                refs.Identity,
                refs.Count,
                WorkerPackageDiagnosticsContract.Version,
                ValidationProfiles: [PackageRenderWorkerContract.ValidationProfile],
                SupportedPackageProfiles: supported);
            ProjectImageValidationWorkerCapabilities imageCapabilities = new(
                StaticProjectSnapshotCodec.EngineIdentity,
                ProjectImageValidationWorkerContract.ValidationProfile,
                [BackendSnapshotProfiles.StaticLyricsScene, BackendSnapshotProfiles.StaticLyricsSceneAnalysis, BackendSnapshotProfiles.PackageLyricsScene],
                ProjectImageValidationWorkerContract.ImageDecoderProfile,
                ProjectImageValidationWorkerContract.MaximumInputBytes,
                ProjectImageValidationWorkerContract.MaximumImageBytes,
                ProjectImageValidationWorkerContract.MaximumDimension,
                ProjectImageValidationWorkerContract.MaximumDecodedBytes,
                ProjectImageValidationWorkerContract.MaximumBaseSnapshotBytes,
                ProjectImageValidationWorkerContract.MaximumSourceValues,
                WorkerValidationContract.MaximumReportBytes,
                WorkerPackageDiagnosticsContract.Version,
                packageCapabilities);
            RuntimeValidationWorkerCapabilities runtimeCapabilities = new(
                RuntimeValidationWorkerContract.Profile,
                RuntimeValidationWorkerContract.MaximumInputBytes,
                WorkerValidationContract.MaximumReportBytes,
                packageCapabilities);

            Dictionary<string, string> payloads = new()
            {
                [PackageRenderWorkerContract.ValidationCapabilitiesKind] = Convert.ToBase64String(PackageRenderWorkerContract.EncodeCapabilities(packageCapabilities)),
                [ProjectImageValidationWorkerContract.CapabilitiesKind] = Convert.ToBase64String(ProjectImageValidationWorkerContract.EncodeCapabilities(imageCapabilities)),
                [RuntimeValidationWorkerContract.CapabilitiesKind] = Convert.ToBase64String(RuntimeValidationWorkerContract.EncodeCapabilities(runtimeCapabilities)),
            };
            await File.WriteAllTextAsync(probe._temp.FilePath("payloads.json"), JsonSerializer.Serialize(payloads));
            await File.WriteAllTextAsync(probe._temp.FilePath("probe.py"), Source);
            return probe;
        }

        public WorkerProcessOptions Options() => HostingTestPlatformHelpers.PythonOptions(
            _temp.FilePath("probe.py"),
            [_temp.FilePath("launches.log"), _temp.FilePath("payloads.json")],
            TimeSpan.FromMilliseconds(200));

        public int[] ReadLaunchedPids()
        {
            string path = _temp.FilePath("launches.log");
            if (!File.Exists(path)) return [];
            return File.ReadAllLines(path)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => int.Parse(line.Trim(), CultureInfo.InvariantCulture))
                .ToArray();
        }

        public void Dispose() => _temp.Dispose();

        private const string Source = """
            import base64, hashlib, json, os, struct, sys
            launch_log, payloads_path = sys.argv[1:]
            if hasattr(os, 'setsid'):
                try: os.setsid()
                except OSError: pass
            with open(launch_log, 'a') as log:
                log.write(f'{os.getpid()}\n')
                log.flush()
            with open(payloads_path, 'r') as f:
                payloads = json.load(f)
            result_kinds = {
                'package-validation-capabilities-v1': 'package-validation-capabilities-result-v1',
                'project-image-validation-capabilities-v1': 'project-image-validation-capabilities-result-v1',
                'runtime-validation-capabilities-v1': 'runtime-validation-capabilities-result-v1',
            }
            def send(value):
                body = json.dumps(dict(protocolVersion='worker-1', delayMilliseconds=0, cancelled=False, **value)).encode()
                sys.stdout.buffer.write(struct.pack('<i', len(body)) + body)
                sys.stdout.buffer.flush()
            while True:
                prefix = sys.stdin.buffer.read(4)
                if not prefix: break
                request = json.loads(sys.stdin.buffer.read(struct.unpack('<i', prefix)[0]))
                kind = request['kind']
                if kind == 'hello':
                    hello = dict(kind='hello', requestId=request['requestId'])
                    hello['processGroupId'] = os.getpgrp() if hasattr(os, 'getpgrp') else os.getpid()
                    send(hello)
                    continue
                if kind == 'shutdown':
                    break
                if kind in result_kinds:
                    raw = base64.b64decode(payloads[kind])
                    digest = hashlib.sha256(raw).hexdigest()
                    send(dict(kind=result_kinds[kind], requestId=request['requestId'],
                              inputSha256=request['inputSha256'], payload=payloads[kind], outputSha256=digest))
                    continue
                send(dict(kind='error', requestId=request['requestId'], inputSha256=request.get('inputSha256'),
                          error='unsupported', errorCode='Unsupported'))
            """;
    }

    private sealed class LifecycleProbe : IDisposable
    {
        private readonly TemporaryDirectory _temp = new();

        public static async Task<LifecycleProbe> CreateAsync()
        {
            LifecycleProbe probe = new();
            await File.WriteAllTextAsync(probe._temp.FilePath("lifecycle.py"), Source);
            return probe;
        }

        public WorkerProcessOptions Options(int round) => HostingTestPlatformHelpers.PythonOptions(
            _temp.FilePath("lifecycle.py"),
            [_temp.FilePath("launches.log"), _temp.FilePath("request.marker"), (round % 3 * 15).ToString(CultureInfo.InvariantCulture)],
            TimeSpan.FromMilliseconds(200));

        public int[] ReadLaunchedPids()
        {
            string path = _temp.FilePath("launches.log");
            if (!File.Exists(path)) return [];
            return File.ReadAllLines(path)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => int.Parse(line.Trim(), CultureInfo.InvariantCulture))
                .ToArray();
        }

        public async Task WaitForRequestAsync(TimeSpan timeout)
        {
            string marker = _temp.FilePath("request.marker");
            Stopwatch sw = Stopwatch.StartNew();
            while (!File.Exists(marker))
            {
                Assert.True(sw.Elapsed < timeout, "Probe did not receive request in time.");
                await Task.Delay(10);
            }
        }

        public void Dispose() => _temp.Dispose();

        private const string Source = """
            import json, os, struct, sys, time
            launch_log, request_marker, hello_delay_ms = sys.argv[1:]
            with open(launch_log, 'a') as log:
                log.write(f'{os.getpid()}\n')
                log.flush()
            def send(value):
                body = json.dumps(dict(protocolVersion='worker-1', delayMilliseconds=0, cancelled=False, **value)).encode()
                sys.stdout.buffer.write(struct.pack('<i', len(body)) + body)
                sys.stdout.buffer.flush()
            while True:
                prefix = sys.stdin.buffer.read(4)
                if not prefix: break
                request = json.loads(sys.stdin.buffer.read(struct.unpack('<i', prefix)[0]))
                kind = request['kind']
                if kind == 'hello':
                    delay = int(hello_delay_ms)
                    if delay > 0: time.sleep(delay / 1000.0)
                    send(dict(kind='hello', requestId=request['requestId']))
                    continue
                if kind == 'shutdown':
                    break
                if kind == 'cancel':
                    continue
                with open(request_marker, 'w') as m:
                    m.write(request['requestId'])
                delay = request.get('delayMilliseconds', 0)
                if delay > 0: time.sleep(delay / 1000.0)
                send(dict(kind='result', requestId=request['requestId'], inputSha256=request['inputSha256'], payload=request.get('payload', '')))
            """;
    }
}
