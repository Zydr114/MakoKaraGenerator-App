using System.Buffers.Binary;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Media;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class BackendWaveformTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] UnavailableProperties = ["projectSessionId", "available", "reason", "protocolVersion"];
    [Fact]
    public async Task 原prepare全部层位模式保留且元数据读取不解码不改历史与歌词修订无关()
    {
        using Fixture f = new();
        ProjectSession session = await f.Create();
        ProjectSessionSummaryResponse before = (await f.Registry.ReadSummaryAsync("a", session.ProjectSessionId))!;
        ProjectWaveformResponse metadata = await f.Metadata(session);
        Assert.True(metadata.Available); Assert.Null(metadata.Reason);
        Assert.Equal(8000, metadata.SampleRate); Assert.Equal(2, metadata.SourceChannels);
        Assert.Equal(1, metadata.WaveformChannels); Assert.Equal(WaveformLimits.Format, metadata.Format);
        Assert.Equal(WorkerAudioPreparationContract.Profile, metadata.Profile);
        Assert.Equal(f.Loaded.Peaks.SampleCount.ToString(CultureInfo.InvariantCulture), metadata.SampleCount);
        Assert.Equal(f.Loaded.Peaks.DurationUs.ToString(CultureInfo.InvariantCulture), metadata.DurationUs);
        WaveformLevelDto[] levels = metadata.Levels!;
        Assert.Equal(f.Cache.RetainedBytes, levels.Sum(level => (long)level.BucketCount * 8));
        foreach (WaveformLevelDto level in levels)
        {
            Assert.Equal(f.Loaded.Peaks.BucketCount(level.Level), level.BucketCount);
            Assert.Equal(f.Loaded.Peaks.BucketSamples(level.Level).ToString(CultureInfo.InvariantCulture), level.BucketSamples);
            WaveformWindow read = (await f.Window(session, metadata.WaveformId!, level.Level, 0, level.BucketCount)).Response!;
            Assert.Equal(level.BucketCount * 8, read.Bytes.Length);
            for (int i = 0; i < level.BucketCount; i++)
            {
                PeakRange expected = f.Loaded.Peaks.GetBucketPeak(level.Level, i);
                Assert.Equal(BitConverter.SingleToInt32Bits(expected.Min), BinaryPrimitives.ReadInt32LittleEndian(read.Bytes.Span.Slice(i * 8, 4)));
                Assert.Equal(BitConverter.SingleToInt32Bits(expected.Max), BinaryPrimitives.ReadInt32LittleEndian(read.Bytes.Span.Slice(i * 8 + 4, 4)));
            }
        }
        Assert.Equal(before, await f.Registry.ReadSummaryAsync("a", session.ProjectSessionId));
        Assert.Equal(1, f.Decodes);
        ProjectCommandExecutionResult edited = await f.Registry.ExecuteCommandAsync("a", session.ProjectSessionId,
            new("0", new SetCanvasSizeCommandDto(321, 181), f.Meta().RequestId, "host", f.Meta().ValidUntilUtc));
        Assert.True(edited.Accepted);
        Assert.Equal(metadata.WaveformId, (await f.Metadata(session)).WaveformId);
        Assert.Equal(1, f.Decodes);
    }

    [Fact]
    public async Task 同源跨客户端去重仍按会话授权且满缓存允许同身份复用新身份缺省不可用()
    {
        using Fixture f = new(maximumBytes: 160);
        ProjectSession first = await f.Create();
        ProjectSession other = await f.Create("b");
        ProjectWaveformResponse metadata = await f.Metadata(first), shared = await f.Metadata(other, "b");
        Assert.Equal(metadata.WaveformId, shared.WaveformId);
        Assert.Equal(160, f.Cache.RetainedBytes); Assert.Equal(1, f.Cache.EntryCount);
        Assert.Equal(BackendErrorCode.SessionNotFound, (await f.Registry.ReadWaveformAsync("b", first.ProjectSessionId)).Error!.Code);
        Assert.Equal(BackendErrorCode.SessionNotFound, (await f.Registry.ReadWaveformWindowAsync("b", first.ProjectSessionId, metadata.WaveformId!, 0, 0, 1)).Error!.Code);
        f.Loaded = Waveform(0.25f);
        ProjectSession unavailable = await f.Create();
        ProjectWaveformResponse empty = await f.Metadata(unavailable);
        Assert.False(empty.Available); Assert.Equal("cacheBudgetExceeded", empty.Reason);
        Assert.Equal(UnavailableProperties, JsonSerializer.SerializeToElement(empty, Json).EnumerateObject().Select(p => p.Name));
        await f.Close(first); Assert.Equal(160, f.Cache.RetainedBytes);
        await f.Close(other, "b"); Assert.Equal(0, f.Cache.RetainedBytes);
        Assert.Equal("cacheBudgetExceeded", (await f.Metadata(unavailable)).Reason);
        Assert.Equal(3, f.Decodes);
        Assert.Equal(BackendErrorCode.ResourceNotFound, (await f.Window(unavailable, metadata.WaveformId!, 0, 0, 1)).Error!.Code);
        ProjectSession fresh = await f.Create();
        Assert.True((await f.Metadata(fresh)).Available);
        Assert.NotEqual(metadata.WaveformId, (await f.Metadata(fresh)).WaveformId);
    }

    [Fact]
    public async Task 关闭及Host释放删除lastref但已生成独立窗口仍完整可读且旧身份新读404()
    {
        using Fixture f = new(); ProjectSession session = await f.Create();
        string id = (await f.Metadata(session)).WaveformId!;
        WaveformWindow inFlight = (await f.Window(session, id, 0, 0, 2)).Response!;
        byte[] before = inFlight.Bytes.ToArray();
        await f.Close(session);
        Assert.Equal(0, f.Cache.RetainedBytes); Assert.Equal(0, f.Cache.EntryCount);
        Assert.Equal(before, inFlight.Bytes.ToArray());
        Assert.Equal(BackendErrorCode.SessionNotFound, (await f.Window(session, id, 0, 0, 2)).Error!.Code);
        _ = await f.Create(); Assert.True(f.Cache.RetainedBytes > 0);
        f.Registry.Dispose(); Assert.Equal(0, f.Cache.RetainedBytes);
    }

    [Fact]
    public async Task 缓存身份包含所有峰值位模式和源采样元信息且窗口ETag绑定完整查询()
    {
        using Fixture f = new(); ProjectSession first = await f.Create();
        string id = (await f.Metadata(first)).WaveformId!;
        WaveformWindow read = (await f.Window(first, id, 0, 0, 1)).Response!;
        Assert.Equal(read.ETag, (await f.Window(first, id, 0, 0, 1)).Response!.ETag);
        Assert.NotEqual(read.ETag, (await f.Window(first, id, 0, 1, 1)).Response!.ETag);
        f.Loaded = Waveform(0.25f); ProjectSession changed = await f.Create();
        Assert.NotEqual(id, (await f.Metadata(changed)).WaveformId);
        Assert.Equal(BackendErrorCode.ResourceNotFound, (await f.Window(changed, id, 0, 0, 1)).Error!.Code);
        f.Loaded = f.Loaded with { Info = f.Loaded.Info with { Channels = 3 } }; ProjectSession channels = await f.Create();
        Assert.NotEqual((await f.Metadata(changed)).WaveformId, (await f.Metadata(channels)).WaveformId);
        Assert.Equal(3, f.Cache.EntryCount);
    }

    [Theory]
    [InlineData(-1, 0, 1, "level")]
    [InlineData(100, 0, 1, "level")]
    [InlineData(0, -1, 1, "start")]
    [InlineData(0, 0, 0, "count")]
    [InlineData(0, 0, 8193, "count")]
    [InlineData(0, 9, 1, "start")]
    [InlineData(0, 8, 2, "start")]
    public async Task 桶窗口超界不夹紧不返回伪静音(int level, int start, int count, string field)
    {
        using Fixture f = new(); ProjectSession session = await f.Create();
        BackendErrorResponse error = (await f.Window(session, (await f.Metadata(session)).WaveformId!, level, start, count)).Error!;
        Assert.Equal(BackendErrorCode.InvalidRequest, error.Code); Assert.Equal(field, error.Field);
    }

    [Fact]
    public async Task 打开重新prepare满缓存仍成功且legacy无声明可读身份变更则不暴露旧payload()
    {
        using Fixture f = new(maximumBytes: 160); ProjectSession first = await f.Create();
        string handle = (await f.Registry.CreateFileHandleAsync("a", new("projects", "saved", Metadata: f.Meta()))).Response!.HandleId;
        Assert.Equal("succeeded", (await f.Terminal((await f.Registry.StartSaveAsync("a", first.ProjectSessionId, new("0", handle, f.Meta()))).Response!)).State);
        f.Loaded = Waveform(.25f);
        JobResponse opened = await f.Terminal((await f.Registry.StartOpenAsync("a", new(handle, "read", f.Meta()))).Response!);
        Assert.Equal("succeeded", opened.State);
        ProjectWaveformResponse unavailable = (await f.Registry.ReadWaveformAsync("a", opened.ProjectSessionId!)).Response!;
        Assert.Equal("cacheBudgetExceeded", unavailable.Reason); Assert.Equal(2, f.Decodes);
        ProjectWorkspace workspace = (ProjectWorkspace)typeof(ProjectSession).GetProperty("Workspace", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(first)!;
        Project old = workspace.Project;
        workspace.Replace(old with { AudioMedia = null });
        Assert.True((await f.Metadata(first)).Available);
        workspace.Replace(old with { AudioPath = old.AudioPath + ".changed" });
        ProjectWaveformResponse changed = await f.Metadata(first);
        Assert.Equal("audioIdentityChanged", changed.Reason); Assert.Null(changed.SourceSha256); Assert.Null(changed.Levels);
        Assert.Equal(BackendErrorCode.ResourceNotFound, (await f.Window(first, "old", 0, 0, 1)).Error!.Code);
    }

    [Fact]
    public async Task 零预算创建成功且Host重启不复用旧缓存()
    {
        using Fixture f = new(maximumBytes: 0); ProjectSession first = await f.Create();
        Assert.Equal("cacheBudgetExceeded", (await f.Metadata(first)).Reason); Assert.Equal(0, f.Cache.RetainedBytes);
        Assert.Single((await f.Registry.ReadClientStateAsync("a")).Sessions);
    }

    [Fact]
    public async Task 未发布候选注册失败释放缓存且不把内部异常伪装为缓存不足()
    {
        using Fixture f = new();
        f.OnDecode = () => typeof(ProjectSessionRegistry).GetField("_stopping", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(f.Registry, 1);
        ProjectSessionCreateResult failed = await f.CreateResult();
        Assert.Equal(BackendErrorCode.SessionConflict, failed.Error!.Code);
        Assert.Equal(0, f.Cache.RetainedBytes); Assert.Equal(0, f.Cache.EntryCount);
        Assert.Empty((await f.Registry.ReadClientStateAsync("a")).Sessions);
    }

    [Fact]
    public async Task 无音轨打开只返回noAudio且已准备缓存不受后续Worker禁用影响()
    {
        using Fixture f = new(); ProjectSession session = await f.Create();
        f.Registry.ConfigureAudioPreparation(null);
        Assert.True((await f.Metadata(session)).Available); Assert.Equal(1, f.Decodes);
        string directory = f.ProjectPath("without-audio");
        ProjectStore.Save(ProjectWorkspace.CreateNew("without-audio").Project, directory);
        string handle = (await f.Registry.CreateFileHandleAsync("a", new("projects", "without-audio", Metadata: f.Meta()))).Response!.HandleId;
        JobResponse opened = await f.Terminal((await f.Registry.StartOpenAsync("a", new(handle, Metadata: f.Meta()))).Response!);
        Assert.Equal("succeeded", opened.State);
        ProjectWaveformResponse noAudio = (await f.Registry.ReadWaveformAsync("a", opened.ProjectSessionId!)).Response!;
        Assert.False(noAudio.Available); Assert.Equal("noAudio", noAudio.Reason);
        Assert.Null(noAudio.WaveformId); Assert.Null(noAudio.SourceSha256); Assert.Null(noAudio.Levels);
        Assert.Equal(1, f.Decodes);
    }

    [Fact]
    public async Task 恢复只用本次prepare满缓存仍成功且不持久化峰值()
    {
        using Fixture f = new(); _ = await f.Create();
        Assert.True(f.Cache.RetainedBytes > 0);
        await f.Registry.ShutdownAsync(); Assert.Equal(0, f.Cache.RetainedBytes);
        f.Restart(maximumBytes: 0);
        RecoveryCandidateResponse candidate = Assert.Single(f.Registry.ReadRecoveryCandidates("a"));
        JobResponse recovered = await f.Terminal((await f.Registry.StartRecoverAsync("a", new(candidate.RecoveryId, f.Meta()))).Response!);
        Assert.Equal("succeeded", recovered.State);
        Assert.Equal("cacheBudgetExceeded", (await f.Registry.ReadWaveformAsync("a", recovered.ProjectSessionId!)).Response!.Reason);
        Assert.Equal(2, f.Decodes); Assert.Equal(0, f.Cache.RetainedBytes);
    }

    private static LoadedWaveform Waveform(float variant = 0)
    {
        float[] minima = [0f, -0f, -2.5f, -.25f, -.5f, -.75f, -.125f, -.3f, -0f];
        float[] maxima = [0f, -0f, 2.25f, .5f, .75f, variant, .2f, .4f, 0f];
        return new(new(64125, 8000, 2, "pcm_s16le", "wav"), WaveformPeakPyramid.FromBaseBuckets(8000, 513, 64, minima, maxima));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TemporaryDirectory _directory = new();
        internal readonly ResourceStore Resources;
        internal ProjectSessionRegistry Registry = null!;
        internal WaveformCache Cache = null!;
        internal LoadedWaveform Loaded = Waveform();
        internal int Decodes;
        internal Action? OnDecode;
        internal Fixture(long maximumBytes = WaveformLimits.MaxRetainedBytes)
        {
            Resources = new(new() { ResourceRoot = _directory.FilePath("resources") });
            Restart(maximumBytes);
        }
        internal void Restart(long maximumBytes)
        {
            Cache = new(maximumBytes);
            Registry = new("host", Resources, new(Decode), waveformCache: Cache);
            Registry.ConfigurePersistence(new() { StateRoot = _directory.FilePath("state"),
                AutoSaveInterval = TimeSpan.FromHours(1),
                AllowedProjectRoots = new Dictionary<string, string> { ["projects"] = _directory.FilePath("projects") } }, loadAudio: Decode);
        }
        private Task<LoadedWaveform?> Decode(string _, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Decodes++; OnDecode?.Invoke(); return Task.FromResult<LoadedWaveform?>(Loaded); }
        internal string ProjectPath(string relative) => Path.Combine(_directory.FilePath("projects"), relative);
        internal RequestMetadataDto Meta() => new(Guid.NewGuid().ToString("N"), Registry.HostInstanceId, DateTimeOffset.UtcNow.AddMinutes(1));
        internal async Task<ProjectSession> Create(string client = "a")
        {
            ProjectSessionCreateResult result = await CreateResult(client);
            Assert.True(result.Accepted, result.Error?.Message); return result.Session!;
        }
        internal async Task<ProjectSessionCreateResult> CreateResult(string client = "a")
        {
            StoredAsset audio = await Resources.UploadAsync(client, "audio.wav", "audio/wav", new MemoryStream([1, 2, 3]));
            StoredAsset lyrics = await Resources.UploadAsync(client, "lyrics.lrc", "text/plain", new MemoryStream("[00:00.00]歌词\n"u8.ToArray()));
            return await Registry.CreateAsync(client, new("waveform", audio.AssetId, lyrics.AssetId));
        }
        internal async Task<ProjectWaveformResponse> Metadata(ProjectSession session, string client = "a") =>
            (await Registry.ReadWaveformAsync(client, session.ProjectSessionId)).Response!;
        internal Task<BackendOperationResult<WaveformWindow>> Window(ProjectSession session, string id, int level, int start, int count) =>
            Registry.ReadWaveformWindowAsync("a", session.ProjectSessionId, id, level, start, count);
        internal async Task Close(ProjectSession session, string client = "a")
        {
            ProjectSessionSummaryResponse summary = (await Registry.ReadSummaryAsync(client, session.ProjectSessionId))!;
            JobResponse accepted = (await Registry.StartCloseAsync(client, session.ProjectSessionId,
                new(summary.ProjectRevision, "discard", ActiveJobs: "wait", Metadata: Meta()))).Response!;
            Assert.Equal("succeeded", (await Terminal(accepted, client)).State);
        }
        internal async Task<JobResponse> Terminal(JobResponse accepted, string client = "a")
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
            while (true)
            {
                JobResponse value = Registry.ReadJob(client, accepted.JobId).Response!;
                if (value.State is "succeeded" or "failed" or "cancelled") return value;
                await Task.Delay(10, timeout.Token);
            }
        }
        public void Dispose() { Registry.Dispose(); _directory.Dispose(); }
    }
}
