using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Threading.Channels;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class BackendPreviewSessionRegistryTests
{
    private static BackendPreviewPinnedInput Input(Factory factory, string revision = "source-1") =>
        new(new("project", "project-1", revision), ProjectSnapshot.FromProject(Project.Create("preview",
            Composition.Create(2, 2, FrameRate.Fps30, outputDurationUs: 1_000_000), LyricsDocument.Create([]))), factory);

    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly PreviewFrameHandleStore Frames = new(maxFrames: 4, maxBytes: 4096);
        internal readonly PreviewDiagnosticsStore Diagnostics = new();
        internal readonly Encoder Encoder;
        internal readonly BackendPreviewSessionRegistry Registry;

        internal Fixture(Encoder? encoder = null, int maxSessions = 4, int perClient = 2, int pageSize = 2)
        {
            Encoder = encoder ?? new();
            Registry = new(Frames, Diagnostics, Encoder, new()
            {
                MaxSessions = maxSessions, MaxSessionsPerClient = perClient,
                Limits = new(2, 2, 1, 2, 1024, 30, pageSize),
            });
        }

        internal PreviewSessionResponse Create(BackendPreviewPinnedInput input, string client = "client")
        {
            BackendPreviewSessionResult result = Registry.Create(client, new(input.Source, new(1, 1)), input);
            Assert.Null(result.Error);
            return result.Status!.Session;
        }

        internal BackendPreviewFrameRequestResult Request(PreviewSessionResponse session, long sequence, long time, string intent = "playback") =>
            Registry.RequestFrame("client", session.PreviewId, session.Source.SourceRevision,
                new(session.PreviewGeneration, sequence.ToString(CultureInfo.InvariantCulture), time.ToString(CultureInfo.InvariantCulture), intent));

        internal async Task<BackendPreviewSessionStatus> Wait(string id, Func<BackendPreviewSessionStatus, bool> predicate)
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
            while (true)
            {
                BackendPreviewSessionStatus? status = Registry.Read("client", id).Status;
                if (status is not null && predicate(status)) return status;
                await Task.Delay(10, timeout.Token);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Registry.DisposeAsync();
            Encoder.Dispose();
        }
    }

    [Fact]
    public async Task 模板试样来源可复用固定帧生命周期且不可提交()
    {
        await using Fixture fixture = new();
        Factory factory = new();
        ProjectSnapshot snapshot = ProjectSnapshot.FromProject(Project.Create("template-sample",
            Composition.Create(2, 2, FrameRate.Fps30, outputDurationUs: 1_000_000), LyricsDocument.Create([])));
        BackendPreviewPinnedInput input = new(new("template", "builtin:line-classic", "revision-1"), snapshot, factory);
        BackendPreviewSessionResult created = fixture.Registry.Create("client",
            new(input.Source, new(1, 1), PreviewAlphaMode.Opaque, "template-sample", null, "template", false), input);
        Assert.True(created.Accepted, created.Error?.Message);
        Assert.Equal("template", created.Status!.Session.PreviewKind);
        Assert.False(created.Status.Session.Committable);
        BackendPreviewSessionResult update = fixture.Registry.Update("client", created.Status.Session.PreviewId,
            created.Status.Session.Source.SourceRevision,
            new(created.Status.Session.PreviewGeneration, new("project", "project-1", "source-2"), new(1, 1), PreviewAlphaMode.Opaque),
            input with { Source = new("project", "project-1", "source-2") });
        Assert.Equal(BackendErrorCode.CommandNotSupported, update.Error!.Code);
        Assert.True(fixture.Request(created.Status.Session, 1, 0).Accepted);
        (await factory.Next()).Release.SetResult();
        BackendPreviewSessionStatus ready = await fixture.Wait(created.Status.Session.PreviewId,
            status => status.Session.LatestFrame is not null);
        Assert.Equal("revision-1", ready.Session.LatestFrame!.SourceRevision);
    }

    [Fact]
    public async Task 作者草稿来源固定为不可提交并限制换源草稿身份()
    {
        await using Fixture fixture = new();
        Factory factory = new();
        ProjectSnapshot snapshot = ProjectSnapshot.FromProject(Project.Create("author-sample",
            Composition.Create(2, 2, FrameRate.Fps30, outputDurationUs: 1_000_000), LyricsDocument.Create([])));
        PreviewSourceDto source = new("authorDraft", "draft-1", "draft-rev-1",
            new(TemplatePreviewLimits.SingleLineSampleId, "token"), "source-rev-1");
        BackendPreviewPinnedInput input = new(source, snapshot, factory);
        BackendPreviewSessionResult created = fixture.Registry.Create("client",
            new(source, new(1, 1), PreviewAlphaMode.Opaque, "author-sample", null, "authorDraft", false), input);
        Assert.True(created.Accepted, created.Error?.Message);
        Assert.Equal("authorDraft", created.Status!.Session.PreviewKind);
        Assert.False(created.Status.Session.Committable);
        BackendPreviewSessionResult wrongDraft = fixture.Registry.Update("client", created.Status.Session.PreviewId,
            created.Status.Session.Source.SourceRevision,
            new(created.Status.Session.PreviewGeneration,
                source with { SourceId = "draft-2" }, new(1, 1), PreviewAlphaMode.Opaque),
            input with { Source = source with { SourceId = "draft-2" } });
        Assert.True(wrongDraft.Accepted, wrongDraft.Error?.Message);
        Assert.Equal("draft-2", wrongDraft.Status!.Session.Source.SourceId);
    }

    [Fact]
    public async Task 创建固定来源且按客户端及总会话配额限制并拒绝旧基线()
    {
        await using Fixture fixture = new(maxSessions: 2, perClient: 1);
        Factory factory = new();
        BackendPreviewPinnedInput input = Input(factory);
        PreviewSessionResponse a = fixture.Create(input);
        Assert.Equal(BackendErrorCode.SessionQuotaExceeded,
            fixture.Registry.Create("client", new(input.Source, new(1, 1)), input).Error!.Code);
        PreviewSessionResponse b = fixture.Create(input, "other");
        Assert.True(long.Parse(b.PreviewGeneration, CultureInfo.InvariantCulture) > long.Parse(a.PreviewGeneration, CultureInfo.InvariantCulture));
        Assert.Equal(BackendErrorCode.SessionQuotaExceeded,
            fixture.Registry.Create("third", new(input.Source, new(1, 1)), input).Error!.Code);
        Assert.Equal(BackendErrorCode.PreviewSessionNotFound, fixture.Registry.Read("other", a.PreviewId).Error!.Code);
        Assert.Equal(BackendErrorCode.PreviewGenerationConflict,
            fixture.Registry.RequestFrame("client", a.PreviewId, "stale", new(a.PreviewGeneration, "1", "0", "playback")).Error!.Code);
        Assert.Equal(BackendErrorCode.PreviewGenerationConflict,
            (await fixture.Registry.CloseAsync("client", a.PreviewId, "stale", a.Source.SourceRevision)).Error!.Code);
        Assert.Equal(2, fixture.Registry.Count);
        Assert.Equal(0, factory.Created);
    }

    [Fact]
    public async Task 向前播放不取消在途帧并只运行最新待处理时间()
    {
        await using Fixture fixture = new();
        Factory factory = new();
        PreviewSessionResponse session = fixture.Create(Input(factory));
        Assert.True(fixture.Request(session, 1, 1).Accepted);
        Call first = await factory.Next();
        for (int value = 2; value <= 100; value++) Assert.True(fixture.Request(session, value, value).Accepted);
        Assert.False(first.Token.IsCancellationRequested);
        first.Release.SetResult();
        Call latest = await factory.Next();
        Assert.Equal(100, latest.Request.TimeUs);
        BackendPreviewSessionStatus intermediate = await fixture.Wait(session.PreviewId, status => status.Session.LatestFrame?.RequestSeq == "1");
        Assert.NotNull(intermediate.Session.LatestFrame);
        latest.Release.SetResult();
        BackendPreviewSessionStatus ready = await fixture.Wait(session.PreviewId, status => status.Session.LatestFrame?.RequestSeq == "100");
        Assert.Equal("ready", ready.Session.State);
        Assert.Equal("100", ready.Session.LatestFrame!.TimeUs);
        Assert.True(fixture.Frames.TryRead("client", ready.Session.LatestFrame.FrameId, DateTimeOffset.UtcNow, out PreviewFrameReadResult? read, out _));
        Assert.Equal(PreviewPngFixture.Create(), read!.Content);
        Assert.Equal(new long[] { 1, 100 }, factory.Times.ToArray());
        Assert.Equal(1, factory.Created);
        Assert.Equal(BackendErrorCode.PreviewRequestOutOfOrder, fixture.Request(session, 100, 100).Error!.Code);
        Assert.Equal(BackendErrorCode.PreviewRequestOutOfOrder, fixture.Request(session, 101, 99).Error!.Code);
    }

    [Fact]
    public async Task seek由服务换代次并取消旧输入且拒绝迟到失败和旧generation请求()
    {
        await using Fixture fixture = new();
        Factory factory = new(ignoreCancellation: true);
        PreviewSessionResponse session = fixture.Create(Input(factory));
        Assert.True(fixture.Request(session, 1, 90).Accepted);
        Call old = await factory.Next();
        BackendPreviewFrameRequestResult seek = fixture.Request(session, 2, 10, "seek");
        Assert.True(seek.Accepted);
        Assert.NotEqual(session.PreviewGeneration, seek.Response!.PreviewGeneration);
        Assert.True(old.Token.IsCancellationRequested);
        Assert.Equal(BackendErrorCode.PreviewGenerationConflict, fixture.Request(session, 3, 11).Error!.Code);
        old.Release.SetException(new InvalidOperationException("obsolete"));
        Call current = await factory.Next();
        current.Release.SetResult();
        BackendPreviewSessionStatus ready = await fixture.Wait(session.PreviewId, status => status.Session.State == "ready");
        Assert.Equal(seek.Response.PreviewGeneration, ready.Session.PreviewGeneration);
        Assert.Equal("2", ready.Session.LatestFrame!.RequestSeq);
        Assert.Null(ready.Failure);
        Assert.Equal(2, factory.Created);
        Assert.Equal(1, factory.Disposed);
    }

    [Fact]
    public async Task 更新即取消旧工作且无需新请求也会释放旧会话()
    {
        await using Fixture fixture = new();
        Factory factory = new(ignoreCancellation: true);
        PreviewSessionResponse session = fixture.Create(Input(factory));
        Assert.True(fixture.Request(session, 1, 90).Accepted);
        Call old = await factory.Next();
        BackendPreviewPinnedInput next = Input(factory, "source-2");
        BackendPreviewSessionResult updated = fixture.Registry.Update("client", session.PreviewId, session.Source.SourceRevision,
            new(session.PreviewGeneration, next.Source, new(1, 1), PreviewAlphaMode.Opaque), next);
        Assert.True(updated.Accepted);
        Assert.True(old.Token.IsCancellationRequested);
        old.Release.SetResult();
        await WaitUntil(() => factory.Disposed == 1);
        Assert.Equal("idle", fixture.Registry.Read("client", session.PreviewId).Status!.Session.State);
        Assert.Equal(0, fixture.Frames.Count);
        Assert.Equal(BackendErrorCode.PreviewGenerationConflict, fixture.Request(session, 2, 100).Error!.Code);
        Assert.True(fixture.Request(updated.Status!.Session, 2, 100).Accepted);
        Call current = await factory.Next();
        Assert.Same(next.Snapshot, current.Request.Project);
        current.Release.SetResult();
        _ = await fixture.Wait(session.PreviewId, status => status.Session.State == "ready");
    }

    [Fact]
    public async Task 编码在锁外且更新后旧编码结果不能发布()
    {
        Encoder encoder = new() { Block = true };
        await using Fixture fixture = new(encoder);
        Factory factory = new();
        PreviewSessionResponse session = fixture.Create(Input(factory));
        Assert.True(fixture.Request(session, 1, 1).Accepted);
        (await factory.Next()).Release.SetResult();
        await encoder.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        BackendPreviewPinnedInput next = Input(factory, "new-source");
        BackendPreviewSessionResult updated = fixture.Registry.Update("client", session.PreviewId, session.Source.SourceRevision,
            new(session.PreviewGeneration, next.Source, new(1, 1), PreviewAlphaMode.Opaque), next);
        Assert.True(updated.Accepted);
        Assert.Equal("idle", fixture.Registry.Read("client", session.PreviewId).Status!.Session.State);
        encoder.Unblock.Set();
        await WaitUntil(() => factory.Disposed == 1);
        Assert.Equal(0, fixture.Frames.Count);
        Assert.Equal("idle", fixture.Registry.Read("client", session.PreviewId).Status!.Session.State);
    }

    [Theory]
    [InlineData("throw")]
    [InlineData("null")]
    [InlineData("invalid")]
    public async Task 编码失败或无效PNG进入失败状态并发布诊断而非假ready(string mode)
    {
        await using Fixture fixture = new(new Encoder { Mode = mode });
        Factory factory = new();
        PreviewSessionResponse session = fixture.Create(Input(factory));
        Assert.True(fixture.Request(session, 1, 1).Accepted);
        (await factory.Next()).Release.SetResult();
        BackendPreviewSessionStatus failed = await fixture.Wait(session.PreviewId, status => status.Session.State == "failed");
        Assert.NotNull(failed.Failure);
        Assert.Null(failed.Session.LatestFrame);
        Assert.Equal(0, fixture.Frames.Count);
        Assert.True(fixture.Diagnostics.TryRead("client", session.PreviewId, null, null, out PreviewDiagnosticsPageDto? page, out _));
        Assert.Equal("error", Assert.Single(page!.Diagnostics).Severity);
    }

    [Fact]
    public async Task 关闭立即撤销读取而所有关闭调用等待真实排水且配额排水后才释放()
    {
        await using Fixture fixture = new(maxSessions: 1, perClient: 1);
        Factory factory = new(ignoreCancellation: true);
        BackendPreviewPinnedInput input = Input(factory);
        PreviewSessionResponse session = fixture.Create(input);
        Assert.True(fixture.Request(session, 1, 1).Accepted);
        Call old = await factory.Next();
        Task<BackendPreviewSessionResult> close = fixture.Registry.CloseAsync("client", session.PreviewId,
            session.PreviewGeneration, session.Source.SourceRevision);
        Task<BackendPreviewSessionResult> again = fixture.Registry.CloseAsync("client", session.PreviewId,
            session.PreviewGeneration, session.Source.SourceRevision);
        Assert.False(close.IsCompleted);
        Assert.False(again.IsCompleted);
        Assert.True(old.Token.IsCancellationRequested);
        Assert.Equal("closing", fixture.Registry.Read("client", session.PreviewId).Status!.Session.State);
        Assert.Equal(BackendErrorCode.SessionClosing, fixture.Request(session, 2, 2).Error!.Code);
        Assert.Equal(BackendErrorCode.SessionQuotaExceeded,
            fixture.Registry.Create("client", new(input.Source, new(1, 1)), input).Error!.Code);
        old.Release.SetResult();
        Assert.Equal("closed", (await close).Status!.Session.State);
        Assert.Equal("closed", (await again).Status!.Session.State);
        Assert.Equal(1, factory.Disposed);
        Assert.Equal(0, fixture.Frames.Count);
        Assert.Equal(0, fixture.Diagnostics.Count);
        Assert.Equal(0, fixture.Registry.Count);
        _ = fixture.Create(input);
    }

    [Fact]
    public async Task 并发Dispose均等待排水且客户端清理不影响其他客户端()
    {
        Fixture fixture = new();
        Factory factory = new(ignoreCancellation: true);
        PreviewSessionResponse session = fixture.Create(Input(factory));
        PreviewSessionResponse other = fixture.Create(Input(factory), "other");
        Assert.True(fixture.Request(session, 1, 1).Accepted);
        Call call = await factory.Next();
        Task clientDrain = fixture.Registry.DrainClientAsync("client");
        Assert.NotNull(fixture.Registry.Read("other", other.PreviewId).Status);
        Task dispose = fixture.Registry.DisposeAsync().AsTask();
        Task disposeAgain = fixture.Registry.DisposeAsync().AsTask();
        Assert.False(dispose.IsCompleted);
        Assert.False(disposeAgain.IsCompleted);
        call.Release.SetResult();
        await Task.WhenAll(clientDrain, dispose, disposeAgain).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, fixture.Registry.Count);
        Assert.Equal(1, factory.Disposed);
        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task 未隔离效果不能通过清空指纹绕过拒绝且来源必须匹配固定输入()
    {
        await using Fixture fixture = new();
        Factory factory = new();
        BackendPreviewPinnedInput input = Input(factory);
        BackendPreviewPinnedInput unsafeInput = input with
        {
            Snapshot = input.Snapshot with { EffectInputsFingerprint = "unsafe" },
        };
        Assert.Equal(BackendErrorCode.CommandNotSupported,
            fixture.Registry.Create("client", new(input.Source, new(1, 1)), unsafeInput).Error!.Code);
        unsafeInput = input with
        {
            Snapshot = input.Snapshot with
            {
                Effects = new(instances: [new ProjectEffectInstance
                { Id = "i", Name = "i", PackageId = "p", Revision = "r", ExportId = "e" }]),
            },
        };
        Assert.Equal(BackendErrorCode.CommandNotSupported,
            fixture.Registry.Create("client", new(input.Source, new(1, 1)), unsafeInput).Error!.Code);
        Assert.Equal(BackendErrorCode.InvalidRequest,
            fixture.Registry.Create("client", new(input.Source with { SourceRevision = "other" }, new(1, 1)), input).Error!.Code);
        Assert.Equal(0, factory.Created);
    }

    [Fact]
    public async Task 诊断分页完整绑定当前帧且新帧替换整套旧游标()
    {
        await using Fixture fixture = new(pageSize: 2);
        Factory factory = new() { DiagnosticCount = 5 };
        PreviewSessionResponse session = fixture.Create(Input(factory));
        Assert.True(fixture.Request(session, 1, 1).Accepted);
        (await factory.Next()).Release.SetResult();
        _ = await fixture.Wait(session.PreviewId, state => state.Session.State == "ready");
        Assert.True(fixture.Diagnostics.TryRead("client", session.PreviewId, null, null, out PreviewDiagnosticsPageDto? first, out _));
        Assert.Equal(2, first!.Diagnostics.Length);
        string oldCursor = first.NextCursor!;
        Assert.True(fixture.Diagnostics.TryRead("client", session.PreviewId, oldCursor, null, out PreviewDiagnosticsPageDto? second, out _));
        Assert.Equal(2, second!.Diagnostics.Length);
        Assert.True(fixture.Diagnostics.TryRead("client", session.PreviewId, second.NextCursor, null, out PreviewDiagnosticsPageDto? third, out _));
        Assert.Single(third!.Diagnostics);
        Assert.False(third.HasMore);
        Assert.True(fixture.Request(session, 2, 2).Accepted);
        (await factory.Next()).Release.SetResult();
        _ = await fixture.Wait(session.PreviewId, state => state.Session.LatestFrame?.RequestSeq == "2");
        Assert.False(fixture.Diagnostics.TryRead("client", session.PreviewId, oldCursor, null, out _, out BackendErrorResponse? error));
        Assert.Equal(BackendErrorCode.DiagnosticCursorExpired, error!.Code);
    }

    [Fact]
    public async Task 已发布帧关闭后不能再取得但已有读取租约可以读完()
    {
        await using Fixture fixture = new();
        Factory factory = new();
        PreviewSessionResponse session = fixture.Create(Input(factory));
        Assert.True(fixture.Request(session, 1, 1).Accepted);
        (await factory.Next()).Release.SetResult();
        BackendPreviewSessionStatus ready = await fixture.Wait(session.PreviewId, status => status.Session.State == "ready");
        string frameId = ready.Session.LatestFrame!.FrameId;
        Assert.True(fixture.Frames.TryAcquireRead("client", frameId, DateTimeOffset.UtcNow, out PreviewFrameReadLease? lease, out _));
        using (lease)
        {
            Assert.Equal("closed", (await fixture.Registry.CloseAsync("client", session.PreviewId,
                session.PreviewGeneration, session.Source.SourceRevision)).Status!.Session.State);
            Assert.False(fixture.Frames.TryRead("client", frameId, DateTimeOffset.UtcNow, out _, out _));
            Assert.Equal(1, fixture.Frames.ActiveReaders);
            Assert.True(fixture.Frames.ActiveReadBytes > 0);
            using MemoryStream content = new();
            await lease!.Stream.CopyToAsync(content);
            Assert.Equal(PreviewPngFixture.Create(), content.ToArray());
        }
        Assert.Equal(0, fixture.Frames.ActiveReaders);
        Assert.Equal(0, fixture.Frames.RetainedBytes);
    }

    [Fact]
    public async Task 工程快照捕获在单写者内验证客户端和精确修订且后续编辑不改变固定输入()
    {
        using TemporaryDirectory directory = new();
        ResourceStore store = new(new() { ResourceRoot = directory.Path });
        StoredAsset lyrics = await store.UploadAsync("client", "song.lrc", "text/plain",
            new MemoryStream(Encoding.UTF8.GetBytes("[00:00.00]hello\n")));
        StoredAsset audio = await store.UploadAsync("client", "song.wav", "audio/wav", new MemoryStream([1, 2, 3]));
        WaveformPeakBuilder peaks = new();
        peaks.Append(new float[24_000]);
        LoadedWaveform waveform = new(new AudioStreamInfo(3_000_000, 8_000, 1, "pcm", "wav"), peaks.Build(8_000));
        await using ProjectSessionRegistry projects = new("host", store,
            new((_, _) => Task.FromResult<LoadedWaveform?>(waveform)));
        ProjectSession session = (await projects.CreateAsync("client", new("capture", audio.AssetId, lyrics.AssetId))).Session!;
        Factory factory = new();
        Assert.Equal(BackendErrorCode.SessionNotFound, (await session.CapturePreviewInputAsync("other", "0", factory)).Error!.Code);
        Assert.Equal(BackendErrorCode.RevisionConflict, (await session.CapturePreviewInputAsync("client", "1", factory)).Error!.Code);
        BackendPreviewInputCaptureResult captured = await session.CapturePreviewInputAsync("client", "0", factory);
        Assert.Null(captured.Error);
        Assert.Equal("0", captured.Input!.Source.SourceRevision);
        Assert.Equal(session.ProjectSessionId, captured.Input.Source.SourceId);
        LyricLineDto line = Assert.Single((await projects.ReadLyricsAsync("client", session.ProjectSessionId, "primary"))!.Lines);
        ProjectCommandExecutionResult edited = await projects.ExecuteCommandAsync("client", session.ProjectSessionId,
            new("0", new("lyrics.setTimeRange", new("primary", line.Id), "1000", "2000"), "edit", "host", DateTimeOffset.UtcNow.AddMinutes(1)));
        Assert.True(edited.Accepted);
        Assert.Equal(BackendErrorCode.RevisionConflict, (await session.CapturePreviewInputAsync("client", "0", factory)).Error!.Code);
        BackendPreviewInputCaptureResult current = await session.CapturePreviewInputAsync("client", "1", factory);
        Assert.Null(current.Error);
        Assert.NotSame(captured.Input.Snapshot, current.Input!.Snapshot);
        Assert.Equal(0, captured.Input.Snapshot.Lyrics.Lines[0].Time!.Value.StartUs);
        Assert.Equal(1000, current.Input.Snapshot.Lyrics.Lines[0].Time!.Value.StartUs);
        Assert.Equal(0, factory.Created);
    }

    [Fact]
    public async Task 实际Error诊断保留发生身份位置包来源且缓存失败不产生像素()
    {
        await using Fixture fixture = new(pageSize: 1);
        Factory factory = new();
        PreviewSessionResponse session = fixture.Create(Input(factory));
        Guid epoch = Guid.NewGuid();
        RuntimeDiagnostic report = new(new("actual-instance"), "actual-artifact", "render", 123,
            new(DiagnosticSeverity.Error, "ActualError", "actual report", new("controller.cs", 7, 11)))
        { PackageIdentity = new("diagnostic-test", "sha256:" + new string('a', 64), "controller") };
        RuntimeDiagnosticSnapshot snapshot = RuntimeDiagnosticSnapshot.FromOccurrences([
            new(epoch, 1, report), new(epoch, 2, report)]);
        Assert.True(fixture.Request(session, 1, 1).Accepted);
        (await factory.Next()).Release.SetException(new RenderSessionFailureException("WorkerRuntimeDiagnosticError", "private internal error", snapshot));
        BackendPreviewSessionStatus failed = await fixture.Wait(session.PreviewId, state => state.Session.State == "failed");
        Assert.Equal("WorkerRuntimeDiagnosticError", failed.RenderFailureCode);
        Assert.Equal(0, fixture.Frames.Count);
        Assert.Equal(0, fixture.Encoder.Calls);
        Assert.True(fixture.Diagnostics.TryRead("client", session.PreviewId, null, null, out PreviewDiagnosticsPageDto? first, out _));
        BackendDiagnosticDto actual = Assert.Single(first!.Diagnostics);
        Assert.Equal("runtime", actual.OriginKind);
        Assert.Equal("ActualError", actual.Code);
        Assert.Equal(epoch, actual.Occurrence!.Epoch);
        Assert.Equal("1", actual.Occurrence.Sequence);
        Assert.Equal("actual-instance", actual.Occurrence.InstanceId);
        Assert.Equal("actual-artifact", actual.Occurrence.ArtifactHash);
        Assert.Equal("render", actual.Occurrence.Stage);
        Assert.Equal("123", actual.Occurrence.TimeUs);
        Assert.Equal(new BackendDiagnosticLocationDto("controller.cs", 7, 11), actual.Occurrence.Location);
        Assert.Equal(new BackendDiagnosticPackageDto("diagnostic-test", "sha256:" + new string('a', 64), "controller"), actual.Occurrence.Package);
        Assert.True(fixture.Diagnostics.TryRead("client", session.PreviewId, first.NextCursor, null, out PreviewDiagnosticsPageDto? second, out _));
        Assert.Equal("2", Assert.Single(second!.Diagnostics).Occurrence!.Sequence);
        Assert.True(fixture.Request(session, 2, 2).Accepted);
        _ = await fixture.Wait(session.PreviewId, state => state.Session.State == "failed" && state.Session.LatestRequestSeq == "2");
        Assert.Equal(1, factory.Created);
        Assert.Single(factory.Times);
    }

    [Fact]
    public async Task 实际诊断超预算明确失败且不会把截断报告伪装为完整实际快照()
    {
        await using Fixture fixture = new();
        Factory factory = new();
        PreviewSessionResponse session = fixture.Create(Input(factory));
        Guid epoch = Guid.NewGuid();
        RuntimeDiagnostic report = new(new("instance"), "artifact", "render", 1,
            new(DiagnosticSeverity.Error, "ActualError", "actual"));
        RuntimeDiagnosticSnapshot snapshot = RuntimeDiagnosticSnapshot.FromOccurrences(
            Enumerable.Range(1, 257).Select(index => new RuntimeDiagnosticOccurrence(epoch, index, report)));
        Assert.True(fixture.Request(session, 1, 1).Accepted);
        (await factory.Next()).Release.SetException(new RenderSessionFailureException("ActualError", "internal", snapshot));
        BackendPreviewSessionStatus failed = await fixture.Wait(session.PreviewId, state => state.Session.State == "failed");
        Assert.Equal(BackendErrorCode.DiagnosticLimitExceeded, failed.Failure!.Code);
        Assert.Equal("PreviewDiagnosticsBudgetExceeded", failed.RenderFailureCode);
        Assert.True(fixture.Diagnostics.TryRead("client", session.PreviewId, null, null, out PreviewDiagnosticsPageDto? page, out _));
        BackendDiagnosticDto diagnostic = Assert.Single(page!.Diagnostics);
        Assert.Equal("transportFailure", diagnostic.OriginKind);
        Assert.Null(diagnostic.Occurrence);
        Assert.Equal(0, fixture.Frames.Count);
    }

    private static async Task WaitUntil(Func<bool> check)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        while (!check()) await Task.Delay(10, timeout.Token);
    }

    private sealed class Encoder : IBackendPreviewPngEncoder, IDisposable
    {
        internal string? Mode;
        internal bool Block;
        internal int Calls;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ManualResetEventSlim Unblock = new();

        public byte[] Encode(BackendPreviewFrame frame)
        {
            Interlocked.Increment(ref Calls);
            // The fixture is the exact encoded 1x1 red pixel produced by our controlled factory.
            Assert.Equal(new byte[] { 0, 0, 255, 255 }, frame.Pixels);
            Entered.TrySetResult();
            if (Block && !Unblock.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            return Mode switch
            {
                "throw" => throw new InvalidOperationException("encoder failed"),
                "null" => null!,
                "invalid" => [137, 80, 78, 71, 13, 10, 26, 10, 1, 2],
                _ => PreviewPngFixture.Create(),
            };
        }

        public void Dispose() => Unblock.Dispose();
    }

    [Fact]
    public async Task 编译失败完整来源缓存并保持零位置且不制造发生或发布新帧()
    {
        await using Fixture fixture = new();
        Factory factory = new();
        PreviewSessionResponse session = fixture.Create(Input(factory));
        Assert.True(fixture.Request(session, 1, 1).Accepted);
        RenderSourceDiagnostic source = new("package", "sha256:" + new string('a', 64), null, null, "CompileError", "actual compile error", true, null, 0, 0);
        (await factory.Next()).Release.SetException(new RenderSessionFailureException("WorkerPackageCompilationFailed", "private", sourceDiagnostics: [source]));
        _ = await fixture.Wait(session.PreviewId, status => status.Session.State == "failed");
        Assert.Equal(0, fixture.Frames.Count); Assert.Equal(0, fixture.Encoder.Calls);
        Assert.True(fixture.Diagnostics.TryRead("client", session.PreviewId, null, null, out PreviewDiagnosticsPageDto? page, out _));
        var diagnostic = Assert.Single(page!.Diagnostics);
        Assert.Equal("compileSource", diagnostic.OriginKind); Assert.Null(diagnostic.Occurrence);
        Assert.Equal(0, diagnostic.Source!.Line); Assert.Equal(0, diagnostic.Source.Column);
        Assert.Null(diagnostic.Source.InstanceId); Assert.Null(diagnostic.Source.Path);
        Assert.True(fixture.Request(session, 2, 2).Accepted);
        _ = await fixture.Wait(session.PreviewId, status => status.Session.LatestRequestSeq == "2" && status.Session.State == "failed");
        Assert.True(fixture.Diagnostics.TryRead("client", session.PreviewId, null, null, out page, out _));
        Assert.Equal(diagnostic, Assert.Single(page!.Diagnostics));
    }

    [Fact]
    public async Task 新帧诊断预算失败不发布帧且旧成功lease继续有效()
    {
        await using Fixture fixture = new();
        Factory factory = new();
        PreviewSessionResponse session = fixture.Create(Input(factory));
        Assert.True(fixture.Request(session, 1, 1).Accepted);
        (await factory.Next()).Release.SetResult();
        var ready = await fixture.Wait(session.PreviewId, status => status.Session.State == "ready");
        string oldFrame = ready.Session.LatestFrame!.FrameId;
        factory.DiagnosticCount = 65; // Legal private count, exceeds configured 32 pages x 2 items.
        Assert.True(fixture.Request(session, 2, 2).Accepted);
        (await factory.Next()).Release.SetResult();
        var failed = await fixture.Wait(session.PreviewId, status => status.Session.State == "failed");
        Assert.Equal("PreviewDiagnosticsBudgetExceeded", failed.RenderFailureCode);
        Assert.Equal(BackendErrorCode.DiagnosticLimitExceeded, failed.Failure!.Code);
        Assert.Null(failed.Session.LatestFrame);
        Assert.Equal(1, fixture.Frames.Count);
        Assert.True(fixture.Frames.TryRead("client", oldFrame, DateTimeOffset.UtcNow, out _, out _));
        Assert.Equal(1, fixture.Encoder.Calls);
    }

    [Fact]
    public async Task 合法图效果材质旁报完整分页且runtimeError保留旁报不发布像素()
    {
        await using Fixture fixture = new();
        string revision = "sha256:" + new string('a', 64);
        ResourceCatalog resources = new("package", revision, ["material"], ["graph"], []);
        Factory factory = new()
        {
            Image = [new(new("instance"), revision, resources.GetGraph("graph"), "input", "TransparentBackgroundDependency")],
            Material = [new(new PreparedMaterialOwner.Source(new("style.unsungFill", "fill", new MaterialSpec.PackageMaterial("package", revision, "material", EffectParameterOverrides.Empty)), PreparedMaterialSlot.UnsungFill), resources.GetMaterial("material"), "input", "TransparentBackgroundDependency")],
        };
        PreviewSessionResponse session = fixture.Create(Input(factory));
        Assert.True(fixture.Request(session, 1, 1).Accepted); (await factory.Next()).Release.SetResult();
        _ = await fixture.Wait(session.PreviewId, status => status.Session.State == "ready");
        Assert.True(fixture.Diagnostics.TryRead("client", session.PreviewId, null, null, out PreviewDiagnosticsPageDto? page, out _));
        Assert.Equal(2, page!.Diagnostics.Length);
        Assert.Equal("graph", page.Diagnostics[0].ImageEffect!.Graph.ExportId);
        Assert.Equal("style.unsungFill", page.Diagnostics[1].Material!.Owner.Source!.Path);
        factory.Error = true; factory.DiagnosticCount = 1;
        Assert.True(fixture.Request(session, 2, 2).Accepted); (await factory.Next()).Release.SetResult();
        var failed = await fixture.Wait(session.PreviewId, status => status.Session.State == "failed");
        Assert.Equal("PreviewRuntimeDiagnosticError", failed.RenderFailureCode);
        Assert.Null(failed.Session.LatestFrame); Assert.Equal(1, fixture.Frames.Count); Assert.Equal(1, fixture.Encoder.Calls);
        Assert.True(fixture.Diagnostics.TryRead("client", session.PreviewId, null, null, out page, out _));
        Assert.Equal("runtime", page!.Diagnostics[0].OriginKind); Assert.Equal("error", page.Diagnostics[0].Severity);
        Assert.Equal("imageEffect", page.Diagnostics[1].OriginKind);
        Assert.True(fixture.Diagnostics.TryRead("client", session.PreviewId, page.NextCursor, null, out page, out _));
        Assert.Equal("material", Assert.Single(page!.Diagnostics).OriginKind);
    }

    [Fact]
    public async Task 失败异常中的图效果材质旁报在缓存重报时仍保留()
    {
        await using Fixture fixture = new();
        string revision = "sha256:" + new string('a', 64);
        ResourceCatalog resources = new("package", revision, ["material"], ["graph"], []);
        Factory factory = new(); var session = fixture.Create(Input(factory));
        Assert.True(fixture.Request(session, 1, 1).Accepted);
        var failure = new RenderSessionFailureException("RuntimeError", "private")
        {
            ImageEffectDiagnostics = [new(new("instance"), revision, resources.GetGraph("graph"), "input", "TransparentBackgroundDependency")],
            MaterialDiagnostics = [new(new PreparedMaterialOwner.Script(new("instance"), revision) { PackageIdentity = new("package", revision, "controller") }, resources.GetMaterial("material"), "input", "TransparentBackgroundDependency")],
        };
        (await factory.Next()).Release.SetException(failure);
        _ = await fixture.Wait(session.PreviewId, status => status.Session.State == "failed");
        Assert.True(fixture.Request(session, 2, 2).Accepted);
        _ = await fixture.Wait(session.PreviewId, status => status.Session.LatestRequestSeq == "2" && status.Session.State == "failed");
        Assert.True(fixture.Diagnostics.TryRead("client", session.PreviewId, null, null, out PreviewDiagnosticsPageDto? page, out _));
        Assert.Equal("graph", page!.Diagnostics[0].ImageEffect!.Graph.ExportId);
        Assert.Equal("controller", page.Diagnostics[1].Material!.Owner.Script!.PackageIdentity!.ExportId);
        Assert.Null(page.Diagnostics[1].Material!.Owner.Source);
        Assert.Equal(0, fixture.Frames.Count); Assert.Equal(0, fixture.Encoder.Calls);
    }

    [Fact]
    public async Task 非法失败owner报告invalid而非预算且不逃出状态更新()
    {
        await using Fixture fixture = new();
        string revision = "sha256:" + new string('a', 64);
        ResourceCatalog resources = new("package", revision, ["material"], [], []);
        Factory factory = new(); var session = fixture.Create(Input(factory));
        Assert.True(fixture.Request(session, 1, 1).Accepted);
        (await factory.Next()).Release.SetException(new RenderSessionFailureException("RuntimeError", "private")
        { MaterialDiagnostics = [new(null!, resources.GetMaterial("material"), "input", "TransparentBackgroundDependency")] });
        var failed = await fixture.Wait(session.PreviewId, status => status.Session.State == "failed");
        Assert.Equal(BackendErrorCode.InternalError, failed.Failure!.Code);
        Assert.Equal("PreviewDiagnosticsInvalid", failed.RenderFailureCode);
        Assert.True(fixture.Diagnostics.TryRead("client", session.PreviewId, null, null, out PreviewDiagnosticsPageDto? page, out _));
        Assert.Equal("transportFailure", Assert.Single(page!.Diagnostics).OriginKind);
        Assert.Equal(0, fixture.Frames.Count);
    }

    [Fact]
    public async Task 包预览与预设候选必须有可信标记且非法包拒绝()
    {
        await using Fixture fixture = new(); Factory factory = new();
        Project project = Project.Create("package", Composition.Create(2, 2, FrameRate.Fps30, outputDurationUs: 1_000_000), LyricsDocument.Create([])) with
        { Effects = new(packages: [EffectPackageFixture.Contents()]) };
        ProjectSnapshot snapshot = ProjectSnapshot.FromProject(project);
        BackendPreviewPinnedInput basis = Input(factory) with { Snapshot = snapshot };
        Assert.Equal(BackendErrorCode.CommandNotSupported, fixture.Registry.Create("client", new(basis.Source, new(1, 1)), basis).Error!.Code);
        Assert.Equal(BackendErrorCode.CommandNotSupported, fixture.Registry.Create("client", new(basis.Source, new(1, 1)), basis with { VerifiedProfile = "static-lyrics-scene-v1" }).Error!.Code);
        var verified = basis with { VerifiedProfile = "package-lyrics-scene-v1" };
        Assert.True(fixture.Registry.Create("client", new(verified.Source, new(1, 1)), verified).Accepted);
        var candidate = verified with { Source = verified.Source with { Kind = "presetCandidate" } };
        Assert.True(fixture.Registry.Create("client", new(candidate.Source, new(1, 1), PreviewKind: "presetCandidate", Committable: false), candidate).Accepted);
        var invalid = verified with { Snapshot = snapshot with { EffectInputsFingerprint = "invalid" } };
        Assert.Equal(BackendErrorCode.CommandNotSupported, fixture.Registry.Create("client", new(invalid.Source, new(1, 1)), invalid).Error!.Code);
        var budgetOnly = basis with { Snapshot = Input(factory).Snapshot with { Effects = new(budgets: new() { LiveParticles = 999 }) } };
        Assert.Equal(BackendErrorCode.CommandNotSupported, fixture.Registry.Create("client", new(budgetOnly.Source, new(1, 1)), budgetOnly).Error!.Code);
        Assert.Equal(0, factory.Created);
    }

    private sealed record Call(RenderRequest Request, TaskCompletionSource Release, CancellationToken Token);
    private sealed class Factory(bool ignoreCancellation = false) : IRenderFrameSessionFactory
    {
        internal readonly Channel<Call> Calls = Channel.CreateUnbounded<Call>();
        internal readonly ConcurrentQueue<long> Times = new();
        internal int Created;
        internal int Disposed;
        internal int DiagnosticCount;
        internal bool Error;
        internal ImmutableArray<RenderImageEffectDiagnostic> Image = [];
        internal ImmutableArray<RenderMaterialDiagnostic> Material = [];

        internal Task<Call> Next() => Calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        public ValueTask<IRenderFrameSession> CreateAsync(ProjectSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Created);
            return ValueTask.FromResult<IRenderFrameSession>(new Session(this, ignoreCancellation));
        }

        private sealed class Session(Factory owner, bool ignoreCancellation) : IRenderFrameSession
        {
            public async ValueTask<RenderFrame> RenderAsync(RenderRequest request, CancellationToken cancellationToken = default)
            {
                owner.Times.Enqueue(request.TimeUs);
                TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
                owner.Calls.Writer.TryWrite(new(request, release, cancellationToken));
                if (ignoreCancellation) await release.Task.ConfigureAwait(false);
                else await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                ImmutableArray<RuntimeDiagnostic> diagnostics = [.. Enumerable.Range(0, owner.DiagnosticCount).Select(index =>
                    new RuntimeDiagnostic(new EffectInstanceId("test-instance"), "artifact", "render", request.TimeUs,
                        new(owner.Error ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning, "D" + index.ToString(CultureInfo.InvariantCulture), "warning")))];
                return new RenderFrame([0, 0, 255, 255], request.ResolveTarget(), PixelFormat.Bgra8888Premultiplied, request.TimeUs)
                { DiagnosticSnapshot = RuntimeDiagnosticSnapshot.FromArray(diagnostics), ImageEffectDiagnostics = owner.Image, MaterialDiagnostics = owner.Material };
            }

            public ValueTask DisposeAsync()
            {
                Interlocked.Increment(ref owner.Disposed);
                return ValueTask.CompletedTask;
            }
        }
    }
}
