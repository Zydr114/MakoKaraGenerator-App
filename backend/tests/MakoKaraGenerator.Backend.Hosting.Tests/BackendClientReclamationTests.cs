using System.Collections;
using System.Reflection;
using System.Text;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class BackendClientReclamationTests
{
    private const string Secret = "0123456789012345678901234567890123456789";

    [Fact]
    public async Task 同一注册表连续四十次真实新登记可回收过期空闲身份且旧凭证永久拒绝()
    {
        Clock clock = new();
        ClientRegistry clients = Registry(clock, 16);
        List<ClientRegistrationResponse> retired = [];
        int reclaimed = 0;
        for (int index = 0; index < 40; index++)
        {
            ClientRegistrationResponse added = (await clients.RegisterAsync(Secret, new(), _ =>
            { reclaimed++; return Task.FromResult(true); })).Response!;
            Assert.NotNull(added);
            Assert.DoesNotContain(retired, item => item.ClientId == added.ClientId);
            retired.Add(added);
            clock.Advance(TimeSpan.FromSeconds(3));
        }
        Assert.Equal(24, reclaimed);
        ClientRegistrationResponse[] removed = [.. retired.Where(item => !clients.TryReadLease(item.ClientId, out _, out _))];
        Assert.Equal(24, removed.Length);
        foreach (ClientRegistrationResponse old in removed)
        {
            Assert.False(clients.TryReadLease(old.ClientId, out _, out _));
            Assert.Equal(BackendErrorCode.UnauthorizedClient, clients.Authenticate(old.ClientId, old.AccessToken)!.Code);
            Assert.Equal(BackendErrorCode.UnauthorizedClient, (await clients.RegisterAsync(Secret,
                new(ResumeClientId: old.ClientId, ResumeToken: old.ResumeToken), _ => Task.FromResult(true))).Error!.Code);
        }
    }

    [Fact]
    public async Task 未满配额不回收休眠身份且二十次刷新恢复仍是同一身份()
    {
        Clock clock = new();
        ClientRegistry clients = Registry(clock, 2);
        ClientRegistrationResponse original = clients.Register(Secret, new()).Response!;
        clock.Advance(TimeSpan.FromSeconds(3));
        int calls = 0;
        Assert.True((await clients.RegisterAsync(Secret, new(), _ =>
        { calls++; return Task.FromResult(true); })).Accepted);
        Assert.Equal(0, calls);
        ClientRegistrationResponse current = original;
        for (int index = 0; index < 20; index++)
        {
            current = (await clients.RegisterAsync(Secret,
                new(ResumeClientId: current.ClientId, ResumeToken: current.ResumeToken), _ =>
                { calls++; return Task.FromResult(true); })).Response!;
            Assert.Equal(original.ClientId, current.ClientId);
        }
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task 有效租约和已认证在途pin均阻止回收释放幂等后才允许回收()
    {
        Clock clock = new();
        ClientRegistry clients = Registry(clock);
        ClientRegistrationResponse original = clients.Register(Secret, new()).Response!;
        int claims = 0;
        Task<bool> Reclaim(string _) { claims++; return Task.FromResult(true); }
        Assert.Equal(BackendErrorCode.ClientQuotaExceeded, (await clients.RegisterAsync(Secret, new(), Reclaim)).Error!.Code);
        Assert.Equal(0, claims);
        Assert.Null(clients.TryAcquireOperation(original.ClientId, original.AccessToken, out IDisposable? pin));
        Assert.NotNull(pin);
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(BackendErrorCode.ClientQuotaExceeded, (await clients.RegisterAsync(Secret, new(), Reclaim)).Error!.Code);
        Assert.Equal(0, claims);
        pin.Dispose();
        pin.Dispose();
        Assert.True((await clients.RegisterAsync(Secret, new(), Reclaim)).Accepted);
        Assert.Equal(1, claims);
    }

    [Fact]
    public async Task claim期间原子拒绝恢复认证和新pin且检查失败后恢复原身份()
    {
        Clock clock = new();
        ClientRegistry clients = Registry(clock);
        ClientRegistrationResponse original = clients.Register(Secret, new()).Response!;
        clock.Advance(TimeSpan.FromSeconds(3));
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<BackendOperationResult<ClientRegistrationResponse>> registration = clients.RegisterAsync(Secret, new(), async _ =>
        { entered.SetResult(); await release.Task; return false; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.Equal(BackendErrorCode.UnauthorizedClient, clients.Authenticate(original.ClientId, original.AccessToken)!.Code);
            Assert.NotNull(clients.TryAcquireOperation(original.ClientId, original.AccessToken, out IDisposable? pin));
            Assert.Null(pin);
            Assert.False((await clients.RegisterAsync(Secret,
                new(ResumeClientId: original.ClientId, ResumeToken: original.ResumeToken), _ => Task.FromResult(true))).Accepted);
        }
        finally { release.SetResult(); }
        Assert.Equal(BackendErrorCode.ClientQuotaExceeded, (await registration).Error!.Code);
        ClientRegistrationResponse resumed = (await clients.RegisterAsync(Secret,
            new(ResumeClientId: original.ClientId, ResumeToken: original.ResumeToken), _ => Task.FromResult(true))).Response!;
        Assert.Equal(original.ClientId, resumed.ClientId);
        Assert.Null(clients.Authenticate(resumed.ClientId, resumed.AccessToken));
    }

    [Fact]
    public async Task 回收检查异常保留原恢复身份且恢复先赢时不进入回收检查()
    {
        Clock clock = new();
        ClientRegistry clients = Registry(clock);
        ClientRegistrationResponse original = clients.Register(Secret, new()).Response!;
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.False((await clients.RegisterAsync(Secret, new(), _ => throw new IOException("controlled"))).Accepted);
        ClientRegistrationResponse resumed = (await clients.RegisterAsync(Secret,
            new(ResumeClientId: original.ClientId, ResumeToken: original.ResumeToken), _ => Task.FromResult(true))).Response!;
        int calls = 0;
        Assert.False((await clients.RegisterAsync(Secret, new(), _ => { calls++; return Task.FromResult(true); })).Accepted);
        Assert.Equal(0, calls);
        Assert.Null(clients.Authenticate(resumed.ClientId, resumed.AccessToken));
    }

    [Fact]
    public async Task 有效已完成回执及过期但未完成回执均保护身份()
    {
        using TemporaryDirectory directory = new();
        Clock clock = new();
        ClientRegistry clients = Registry(clock);
        ClientRegistrationResponse owner = clients.Register(Secret, new()).Response!;
        using ProjectSessionRegistry projects = Projects(directory.Path, clients, clock);
        RequestMetadataDto metadata = new("done", "host", clock.GetUtcNow().AddSeconds(10));
        await projects.Receipts.ExecuteAsync(owner.ClientId, metadata, "test", null,
            () => Task.FromResult(BackendOperationResult<string>.Success("done")));
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.False(await projects.TryReclaimClientAsync(owner.ClientId));
        Assert.False((await clients.RegisterAsync(Secret, new(), id => projects.TryReclaimClientAsync(id))).Accepted);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<BackendOperationResult<string>> pending = projects.Receipts.ExecuteAsync(owner.ClientId,
            metadata with { RequestId = "pending" }, "test", null, async () =>
            { entered.SetResult(); await release.Task; return BackendOperationResult<string>.Success("done"); });
        await entered.Task;
        clock.Advance(TimeSpan.FromSeconds(10));
        try
        {
            Assert.False(await projects.TryReclaimClientAsync(owner.ClientId));
            Assert.False((await clients.RegisterAsync(Secret, new(), id => projects.TryReclaimClientAsync(id))).Accepted);
        }
        finally { release.SetResult(); }
        Assert.True((await pending).Accepted);
        Assert.True((await clients.RegisterAsync(Secret, new(), id => projects.TryReclaimClientAsync(id))).Accepted);
        Assert.False(clients.TryReadLease(owner.ClientId, out _, out _));
    }

    [Fact]
    public async Task 过期客户端仍拥有未关闭工程时禁止回收且恢复保留工程()
    {
        using TemporaryDirectory directory = new();
        Clock clock = new();
        ClientRegistry clients = Registry(clock);
        ClientRegistrationResponse owner = clients.Register(Secret, new()).Response!;
        ResourceStore resources = new(new() { ResourceRoot = directory.Path }, clock);
        StoredAsset lyrics = await resources.UploadAsync(owner.ClientId, "song.lrc", "text/plain", new MemoryStream(Encoding.UTF8.GetBytes("[00:00.00]hello\n")));
        StoredAsset audio = await resources.UploadAsync(owner.ClientId, "song.wav", "audio/wav", new MemoryStream([1]));
        using ProjectSessionRegistry projects = new("host", resources, Preparation(), clock, clients: clients);
        ProjectSession session = (await projects.CreateAsync(owner.ClientId, new("protected", audio.AssetId, lyrics.AssetId))).Session!;
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.False((await clients.RegisterAsync(Secret, new(), id => projects.TryReclaimClientAsync(id))).Accepted);
        Assert.True(projects.TryGet(owner.ClientId, session.ProjectSessionId, out _));
        Assert.True(clients.Register(Secret, new(ResumeClientId: owner.ClientId, ResumeToken: owner.ResumeToken)).Accepted);
    }

    [Fact]
    public void 回收后晚到事件与读取均不会重建事件字典()
    {
        bool active = true;
        ClientEventJournal journal = new("host", isClientActive: _ => active);
        string cursor = journal.Append("retired", "preview.updated");
        Assert.Single(journal.ReadAfter("retired", null).Events);
        active = false;
        journal.RemoveClient("retired");
        journal.Append("retired", "preview.updated");
        Assert.Empty(journal.ReadAfter("retired", cursor).Events);
        journal.Cursor("retired");
        IDictionary streams = Assert.IsAssignableFrom<IDictionary>(typeof(ClientEventJournal)
            .GetField("_streams", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(journal));
        Assert.Empty(streams.Keys.Cast<string>());
    }

    [Fact]
    public async Task 资源流式读取引用保护身份释放后回收仍保留上传文件()
    {
        using TemporaryDirectory directory = new();
        Clock clock = new();
        ClientRegistry clients = Registry(clock);
        ClientRegistrationResponse owner = clients.Register(Secret, new()).Response!;
        ResourceStore resources = new(new() { ResourceRoot = directory.Path }, clock);
        StoredAsset asset = await resources.UploadAsync(owner.ClientId, "song.lrc", "text/plain", new MemoryStream("lyrics"u8.ToArray()));
        using ProjectSessionRegistry projects = new("host", resources, Preparation(), clock, clients: clients);
        ResourceReadLease read = resources.AcquireRead(owner.ClientId, asset.AssetId);
        clock.Advance(TimeSpan.FromSeconds(3));
        try
        {
            Assert.False((await clients.RegisterAsync(Secret, new(), id => projects.TryReclaimClientAsync(id))).Accepted);
            Assert.Equal((byte)'l', read.Stream.ReadByte());
        }
        finally { read.Dispose(); }
        Assert.True((await clients.RegisterAsync(Secret, new(), id => projects.TryReclaimClientAsync(id))).Accepted);
        Assert.True(File.Exists(asset.StoragePath));
    }

    [Fact]
    public async Task 回收唤醒现有事件等待且已回收client新等待立即要求重同步()
    {
        bool active = true;
        ClientEventJournal journal = new("host", isClientActive: _ => active);
        string cursor = journal.Cursor("retired");
        Task<ClientEventsResponse> existing = journal.WaitForEventsAsync("retired", cursor);
        Assert.False(existing.IsCompleted);
        active = false;
        journal.RemoveClient("retired");
        Assert.True((await existing.WaitAsync(TimeSpan.FromSeconds(5))).ResyncRequired);
        Task<ClientEventsResponse> after = journal.WaitForEventsAsync("retired", cursor);
        Assert.True((await after.WaitAsync(TimeSpan.FromSeconds(5))).ResyncRequired);
    }

    [Fact]
    public async Task 无已发布工程的open运行与等待音频槽位任务在回执过期后仍先于预览清理阻止回收()
    {
        using TemporaryDirectory directory = new();
        Clock clock = new();
        ResourceStore resources = new(new() { ResourceRoot = directory.FilePath("uploads") }, clock);
        using ProjectSessionRegistry projects = new("host", resources, Preparation(), clock);
        string root = directory.FilePath("projects"); Directory.CreateDirectory(root);
        projects.ConfigurePersistence(new() { StateRoot = directory.FilePath("state"), AllowedProjectRoots = new Dictionary<string, string> { ["projects"] = root } });
        ControlledPreparation preparation = new();
        projects.ConfigureAudioPreparation(preparation, new(true, true));
        StoredAsset lyrics = await resources.UploadAsync("seed", "song.lrc", "text/plain", new MemoryStream("[00:00.00]hello\n"u8.ToArray()));
        StoredAsset audio = await resources.UploadAsync("seed", "song.wav", "audio/wav", new MemoryStream([1]));
        ProjectSession session = (await projects.CreateAsync("seed", new("seed", audio.AssetId, lyrics.AssetId))).Session!;
        RequestMetadataDto Meta() => new(Guid.NewGuid().ToString("N"), "host", clock.GetUtcNow().AddSeconds(10));
        string target = (await projects.CreateFileHandleAsync("seed", new("projects", "saved.mkgproj", Metadata: Meta()))).Response!.HandleId;
        JobResponse save = (await projects.StartSaveAsync("seed", session.ProjectSessionId, new("0", target, Meta()))).Response!;
        Assert.Equal("succeeded", (await Terminal(save.JobId, "seed")).State);
        // The terminal response can precede the save action's final cleanup/Completion.
        // This fixture needs a closed seed, so explicitly drain any accepted save task.
        var closing = await projects.StartCloseAsync("seed", session.ProjectSessionId, new("0", ActiveJobs: "wait", Metadata: Meta()));
        Assert.True(closing.Accepted, closing.Error?.ToString());
        Assert.Equal("succeeded", (await Terminal(closing.Response!.JobId, "seed")).State);
        preparation.Block = true;
        string handle = (await projects.CreateFileHandleAsync("opening", new("projects", "saved.mkgproj", Metadata: Meta()))).Response!.HandleId;
        JobResponse first = (await projects.StartOpenAsync("opening", new(handle, "read", Meta()))).Response!;
        await preparation.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        JobResponse second = (await projects.StartOpenAsync("opening", new(handle, "read", Meta()))).Response!;
        clock.Advance(TimeSpan.FromSeconds(20));
        int previewCleanup = 0;
        Task Cleanup(string _) { previewCleanup++; return Task.CompletedTask; }
        try
        {
            Assert.Empty((await projects.ReadClientStateAsync("opening")).Sessions);
            Assert.Equal("running", projects.ReadJob("opening", first.JobId).Response!.State);
            Assert.True(projects.ReadJob("opening", second.JobId).Response!.State is "queued" or "running");
            Assert.Equal(1, preparation.BlockedCalls);
            Assert.False(await projects.TryReclaimClientAsync("opening", Cleanup));
            Assert.Equal(0, previewCleanup);
        }
        finally { preparation.Release.TrySetResult(); }
        Assert.Equal("failed", (await Terminal(first.JobId, "opening")).State);
        Assert.Equal("failed", (await Terminal(second.JobId, "opening")).State);
        Assert.False(await projects.TryReclaimClientAsync("opening", Cleanup));
        Assert.Equal(0, previewCleanup);
        clock.Advance(TimeSpan.FromHours(2));
        Assert.True(await projects.TryReclaimClientAsync("opening", Cleanup));
        Assert.Equal(1, previewCleanup);

        async Task<JobResponse> Terminal(string id, string client)
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(8));
            while (true)
            {
                var read = projects.ReadJob(client, id);
                Assert.True(read.Accepted, read.Error?.ToString());
                JobResponse job = read.Response!;
                if (job.State is "succeeded" or "failed" or "cancelled") return job;
                await Task.Delay(10, timeout.Token);
            }
        }
    }

    [Fact]
    public async Task 任务终态在执行收尾完成前可见且Completion必须等待收尾()
    {
        Clock clock = new();
        using BackendJob job = new("seed", "save", null, clock.GetUtcNow().AddHours(1),
            _ => { }, clock, TimeSpan.FromHours(1), new object(), _ => { });
        TaskCompletionSource terminal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseCleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        job.Start(async current =>
        {
            current.Finish(null);
            terminal.TrySetResult();
            await releaseCleanup.Task;
        });
        try
        {
            await terminal.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("succeeded", job.Read().State);
            Assert.False(job.Completion.IsCompleted);
        }
        finally
        {
            releaseCleanup.TrySetResult();
            await job.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.True(job.Completion.IsCompletedSuccessfully);
    }

    private static ClientRegistry Registry(Clock clock, int maxClients = 1) => new("host", Secret,
        new() { MaxClients = maxClients, TokenLifetimeSeconds = 30, LeaseLifetimeSeconds = 2 }, clock);
    private static ProjectSessionRegistry Projects(string path, ClientRegistry clients, Clock clock) =>
        new("host", new(new() { ResourceRoot = path }, clock), Preparation(), clock, clients: clients);
    private static NewProjectPreparationService Preparation() => new((_, _) => Task.FromResult<LoadedWaveform?>(Waveform()));
    private static LoadedWaveform Waveform()
    {
        WaveformPeakBuilder peaks = new(); peaks.Append(new float[80]);
        return new(new AudioStreamInfo(10_000, 8_000, 1, "pcm", "wav"), peaks.Build(8_000));
    }
    private sealed class ControlledPreparation : IBackendAudioPreparationExecutor
    {
        internal bool Block;
        internal int BlockedCalls;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ValidateInput(BackendAudioPreparationGrant grant) { }
        public async Task<LoadedWaveform?> ExecuteAsync(BackendAudioPreparationGrant grant, CancellationToken cancellationToken = default)
        {
            if (!Block) return Waveform();
            Interlocked.Increment(ref BlockedCalls);
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            throw new IOException("controlled open failure before session publication");
        }
    }
    private sealed class Clock : TimeProvider
    {
        private long _ticks = new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero).UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        public void Advance(TimeSpan duration) => Interlocked.Add(ref _ticks, duration.Ticks);
    }
}
