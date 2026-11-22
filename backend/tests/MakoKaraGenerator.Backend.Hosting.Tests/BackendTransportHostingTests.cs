using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class BackendTransportHostingTests
{
    [Fact]
    public async Task 资源上传固定字节并按客户端授权且拒绝路径越界()
    {
        using TemporaryDirectory directory = new();
        ResourceStore store = new(new() { ResourceRoot = directory.Path, MaxAssetBytes = 64, MaxAssetQuotaBytes = 128 });
        byte[] bytes = Encoding.UTF8.GetBytes("[00:00.00]hello\n");

        StoredAsset asset = await store.UploadAsync("client-a", "song.lrc", "text/plain", new MemoryStream(bytes));

        Assert.Equal(bytes.Length, asset.Size);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), asset.Sha256);
        Assert.Equal("text/plain", asset.ContentType);
        Assert.True(File.Exists(asset.StoragePath));
        Assert.True(store.TryGetAuthorized("client-a", asset.AssetId, out StoredAsset authorized));
        Assert.Equal(asset, authorized);
        Assert.False(store.TryGetAuthorized("client-b", asset.AssetId, out _));
        ResourceStoreException unauthorized = Assert.Throws<ResourceStoreException>(() => store.GetAuthorized("client-b", asset.AssetId));
        Assert.Equal(BackendErrorCode.ResourceUnauthorized, unauthorized.Code);

        ResourceStoreException traversal = await Assert.ThrowsAsync<ResourceStoreException>(() =>
            store.UploadAsync("client-a", "../escape.lrc", "text/plain", new MemoryStream(bytes)));
        Assert.Equal(BackendErrorCode.ResourcePathNotAllowed, traversal.Code);
    }

    [Fact]
    public async Task 资源上传执行大小与总配额门禁()
    {
        using TemporaryDirectory directory = new();
        ResourceStore store = new(new() { ResourceRoot = directory.Path, MaxAssetBytes = 4, MaxAssetQuotaBytes = 6 });

        ResourceStoreException tooLarge = await Assert.ThrowsAsync<ResourceStoreException>(() =>
            store.UploadAsync("client", "a.wav", "audio/wav", new MemoryStream(new byte[5])));
        Assert.Equal(BackendErrorCode.ResourceTooLarge, tooLarge.Code);
        await store.UploadAsync("client", "a.wav", "audio/wav", new MemoryStream(new byte[4]));
        ResourceStoreException quota = await Assert.ThrowsAsync<ResourceStoreException>(() =>
            store.UploadAsync("client", "b.wav", "audio/wav", new MemoryStream(new byte[3])));
        Assert.Equal(BackendErrorCode.ResourceQuotaExceeded, quota.Code);
    }

    [Fact]
    public async Task 工程会话创建使用托管资源并保持零修订和单写者摘要()
    {
        using TemporaryDirectory directory = new();
        ResourceStore store = new(new() { ResourceRoot = directory.Path });
        StoredAsset lyrics = await store.UploadAsync("client", "song.lrc", "text/plain", new MemoryStream(Encoding.UTF8.GetBytes("[00:00.00]hello\n")));
        StoredAsset audio = await store.UploadAsync("client", "song.wav", "audio/wav", new MemoryStream([1, 2, 3]));

        NewProjectPreparationService preparation = new((path, _) =>
        {
            Assert.Equal(audio.StoragePath, path);
            return Task.FromResult<LoadedWaveform?>(Sample());
        });
        ProjectSessionRegistry registry = new("host-one", store, preparation);
        ProjectSessionCreateResult result = await registry.CreateAsync("client", new(
            "HTTP project", audio.AssetId, lyrics.AssetId,
            new CompositionDto(640, 360, 30, 1)));

        Assert.True(result.Accepted);
        ProjectSession session = Assert.IsType<ProjectSession>(result.Session);
        ProjectSessionSummaryResponse summary = (await registry.ReadSummaryAsync("client", session.ProjectSessionId))!;
        Assert.Equal("0", summary.ProjectRevision);
        Assert.True(summary.IsDirty);
        Assert.False(summary.HasPath);
        Assert.NotNull(summary.EventCursor);
        Assert.False(summary.CanUndo);
        Assert.False(summary.CanRedo);
        Assert.Equal(1, summary.LyricsLineCount);
        Assert.True(registry.TryGet("client", session.ProjectSessionId, out ProjectSession same));
        Assert.Same(session, same);
        Assert.False(registry.TryGet("other", session.ProjectSessionId, out _));
    }

    [Fact]
    public async Task 工程会话创建按歌词扩展名接受浏览器未识别的ASS二进制类型()
    {
        using TemporaryDirectory directory = new();
        ResourceStore store = new(new() { ResourceRoot = directory.Path });
        const string ass = "[Script Info]\nScriptType: v4.00+\n\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,hello\n";
        StoredAsset lyrics = await store.UploadAsync("client", "song.ass", "application/octet-stream", new MemoryStream(Encoding.UTF8.GetBytes(ass)));
        StoredAsset audio = await store.UploadAsync("client", "song.wav", "audio/wav", new MemoryStream([1, 2, 3]));
        using ProjectSessionRegistry registry = new("host-one", store,
            new((_, _) => Task.FromResult<LoadedWaveform?>(Sample())));

        ProjectSessionCreateResult result = await registry.CreateAsync("client", new(
            "ASS project", audio.AssetId, lyrics.AssetId, new CompositionDto(640, 360, 30, 1)));

        Assert.True(result.Accepted);
        ProjectSession session = Assert.IsType<ProjectSession>(result.Session);
        ProjectSessionSummaryResponse summary = (await registry.ReadSummaryAsync("client", session.ProjectSessionId))!;
        Assert.Equal(1, summary.LyricsLineCount);
    }

    [Theory]
    [InlineData("song.bin", "application/octet-stream")]
    [InlineData("song.ass", "video/mp4")]
    public async Task 工程创建拒绝未知二进制扩展名或视频歌词资源(string fileName, string contentType)
    {
        using TemporaryDirectory directory = new();
        ResourceStore store = new(new() { ResourceRoot = directory.Path });
        StoredAsset lyrics = await store.UploadAsync("client", fileName, contentType, new MemoryStream([1, 2, 3]));
        using ProjectSessionRegistry registry = new("host-one", store,
            new((_, _) => Task.FromResult<LoadedWaveform?>(Sample())));
        ProjectSessionCreateResult result = await registry.CreateAsync("client", new("Invalid lyrics", LyricsAssetId: lyrics.AssetId));
        Assert.False(result.Accepted);
        Assert.Equal(BackendErrorCode.InvalidRequest, result.Error!.Code);
        Assert.Equal(nameof(CreateProjectSessionRequest.LyricsAssetId), result.Error.Field);
    }

    [Fact]
    public async Task 工程会话创建在资源ID为空串时返回结构化拒绝而不是访问资源字典异常()
    {
        using TemporaryDirectory directory = new();
        ResourceStore store = new(new() { ResourceRoot = directory.Path });
        ProjectSessionRegistry registry = new("host-one", store,
            new((_, _) => Task.FromResult<LoadedWaveform?>(Sample())));

        ProjectSessionCreateResult result = await registry.CreateAsync("client", new("invalid", "", ""));

        Assert.False(result.Accepted);
        Assert.Equal(BackendErrorCode.InvalidRequest, result.Error!.Code);
        Assert.Equal(nameof(CreateProjectSessionRequest.AudioAssetId), result.Error.Field);
    }

    [Fact]
    public async Task JSON空请求和空资源字段返回结构化拒绝()
    {
        using TemporaryDirectory directory = new();
        ResourceStore store = new(new() { ResourceRoot = directory.Path });
        using ProjectSessionRegistry registry = new("host-one", store,
            new((_, _) => Task.FromResult<LoadedWaveform?>(Sample())));
        Assert.Equal(BackendErrorCode.InvalidRequest,
            (await registry.CreateAsync("client", JsonSerializer.Deserialize<CreateProjectSessionRequest>("null"))).Error!.Code);
        foreach (string body in new[]
        {
            "{\"Name\":\"invalid\",\"AudioAssetId\":\"\",\"LyricsAssetId\":\"x\"}",
            "{\"Name\":\"invalid\",\"AudioAssetId\":null,\"LyricsAssetId\":\" \"}",
        })
        {
            ProjectSessionCreateResult rejected = await registry.CreateAsync("client", JsonSerializer.Deserialize<CreateProjectSessionRequest>(body));
            Assert.Equal(BackendErrorCode.InvalidRequest, rejected.Error!.Code);
        }
    }

    [Fact]
    public async Task 具名歌词时间命令保持精确微秒并通过同一写者支持实际撤销重做()
    {
        using CommandFixture fixture = await CommandFixture.CreateAsync();
        LyricLineDto original = await fixture.LineAsync();
        ProjectCommandRequest request = fixture.Request("req-1", "0", fixture.SetLine("1250001", "2800007"));
        ProjectCommandResponse changed = Accepted(await fixture.ExecuteAsync(request));
        Assert.True(changed.Changed);
        Assert.Equal("0", changed.PreviousRevision);
        Assert.Equal("1", changed.ProjectRevision);
        Assert.True(changed.IsDirty);
        Assert.True(changed.CanUndo);
        Assert.NotNull(changed.EventCursor);
        Assert.Equal([original.Id], changed.ChangeSet.LineIds);
        Assert.Equal(new LyricTimeDto("1250001", "2800007"), (await fixture.LineAsync()).Time);
        ProjectHistoryResponse history = await fixture.HistoryAsync();
        Assert.Equal(1, history.UndoCount);
        Assert.Equal("line.time", history.UndoName);

        Assert.Equal(BackendErrorCode.RevisionConflict,
            (await fixture.ExecuteAsync(fixture.Request("stale", "0", new("history.undo")))).Error!.Code);
        ProjectCommandResponse undone = Accepted(await fixture.ExecuteAsync(fixture.Request("undo", "1", new("history.undo"))));
        Assert.Equal("2", undone.ProjectRevision);
        Assert.True(undone.Changed);
        Assert.True(undone.CanRedo);
        Assert.True(undone.IsDirty);
        Assert.Equal(original.Time, (await fixture.LineAsync()).Time);
        Assert.Equal(0, (await fixture.HistoryAsync()).UndoCount);
        Assert.Equal(1, (await fixture.HistoryAsync()).RedoCount);

        ProjectCommandResponse redone = Accepted(await fixture.ExecuteAsync(fixture.Request("redo", "2", new("history.redo"))));
        Assert.Equal("3", redone.ProjectRevision);
        Assert.Equal(new LyricTimeDto("1250001", "2800007"), (await fixture.LineAsync()).Time);
        Assert.Equal(1, (await fixture.HistoryAsync()).UndoCount);
        Assert.Equal(0, (await fixture.HistoryAsync()).RedoCount);
        AssertEquivalent(changed, Accepted(await fixture.ExecuteAsync(request)));
        Assert.Equal("3", (await fixture.SummaryAsync()).ProjectRevision);
    }

    [Fact]
    public async Task 元素命令按稳定ID保存精确微秒且撤销重做恢复目标()
    {
        using CommandFixture fixture = await CommandFixture.CreateAsync();
        LyricLineDto original = await fixture.LineAsync();
        LyricTokenDto token = original.Tokens[0];
        ProjectCommandDto command = new("lyrics.setTimeRange", new("primary", original.Id, token.Id), "123457", "234569");
        ProjectCommandResponse changed = Accepted(await fixture.ExecuteAsync(fixture.Request("token", "0", command)));
        Assert.Equal([token.Id], changed.ChangeSet.TokenIds);
        Assert.Equal(new LyricTimeDto("123457", "234569"), (await fixture.LineAsync()).Tokens.Single(item => item.Id == token.Id).Time);
        Assert.Equal("token.time", (await fixture.HistoryAsync()).UndoName);
        Accepted(await fixture.ExecuteAsync(fixture.Request("undo", "1", new("history.undo"))));
        Assert.Equal(token.Time, (await fixture.LineAsync()).Tokens.Single(item => item.Id == token.Id).Time);
        Accepted(await fixture.ExecuteAsync(fixture.Request("redo", "2", new("history.redo"))));
        Assert.Equal(new LyricTimeDto("123457", "234569"), (await fixture.LineAsync()).Tokens.Single(item => item.Id == token.Id).Time);
    }

    [Fact]
    public async Task 同ID同语义并发请求只执行一次且去重先于预期修订()
    {
        using CommandFixture fixture = await CommandFixture.CreateAsync();
        ProjectCommandRequest request = fixture.Request("same", "0", fixture.SetLine("100001", "200003"));
        TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<ProjectCommandExecutionResult>[] operations = Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            return await fixture.ExecuteAsync(request);
        })).ToArray();
        start.SetResult();
        ProjectCommandExecutionResult[] results = await Task.WhenAll(operations);
        Assert.All(results, result => AssertEquivalent(results[0].Response!, Accepted(result)));
        Assert.Equal("1", (await fixture.SummaryAsync()).ProjectRevision);
        Assert.Equal(1, (await fixture.HistoryAsync()).UndoCount);
        Accepted(await fixture.ExecuteAsync(fixture.Request("undo", "1", new("history.undo"))));
        AssertEquivalent(results[0].Response!, Accepted(await fixture.ExecuteAsync(request)));
        Assert.Equal("2", (await fixture.SummaryAsync()).ProjectRevision);
    }

    [Fact]
    public async Task 不同ID相同预期修订的并发命令只有一个写者成功()
    {
        using CommandFixture fixture = await CommandFixture.CreateAsync();
        TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<ProjectCommandExecutionResult>[] operations = Enumerable.Range(0, 16).Select(index => Task.Run(async () =>
        {
            await start.Task;
            return await fixture.ExecuteAsync(fixture.Request("req-" + index, "0", fixture.SetLine("100001", "200003")));
        })).ToArray();
        start.SetResult();
        ProjectCommandExecutionResult[] results = await Task.WhenAll(operations);
        Assert.Single(results, result => result.Accepted);
        Assert.Equal(15, results.Count(result => result.Error?.Code == BackendErrorCode.RevisionConflict));
        Assert.Equal("1", (await fixture.SummaryAsync()).ProjectRevision);
        Assert.Equal(1, (await fixture.HistoryAsync()).UndoCount);
    }

    [Fact]
    public async Task 同ID异语义或异截止时间拒绝且等值数字和时区规范化重试()
    {
        using CommandFixture fixture = await CommandFixture.CreateAsync();
        ProjectCommandRequest request = fixture.Request("reuse", "0", fixture.SetLine("100001", "200003"));
        ProjectCommandResponse original = Accepted(await fixture.ExecuteAsync(request));
        foreach (ProjectCommandRequest changed in new[]
        {
            request with { ExpectedRevision = "1" },
            request with { Command = request.Command with { EndUs = "200004" } },
            request with { ValidUntilUtc = request.ValidUntilUtc!.Value.AddTicks(1) },
        })
            Assert.Equal(BackendErrorCode.RequestIdReuse, (await fixture.ExecuteAsync(changed)).Error!.Code);
        ProjectCommandRequest same = request with
        {
            ExpectedRevision = "00",
            Command = request.Command with { StartUs = "0100001", EndUs = "+200003" },
            ValidUntilUtc = request.ValidUntilUtc!.Value.ToOffset(TimeSpan.FromHours(8)),
        };
        AssertEquivalent(original, Accepted(await fixture.ExecuteAsync(same)));
        Assert.Equal(1, (await fixture.HistoryAsync()).UndoCount);
    }

    [Fact]
    public async Task 宿主和截止时间门禁在Hosting内生效且不会占用有效回执配额()
    {
        using CommandFixture fixture = await CommandFixture.CreateAsync(new() { MaxReceiptsPerSession = 1 });
        ProjectCommandRequest valid = fixture.Request("metadata", "0", fixture.SetLine("100001", "200003"));
        Assert.Equal(BackendErrorCode.HostRestarted, (await fixture.ExecuteAsync(valid with { HostInstanceId = null })).Error!.Code);
        Assert.Equal(BackendErrorCode.HostRestarted, (await fixture.ExecuteAsync(valid with { HostInstanceId = "old-host" })).Error!.Code);
        Assert.Equal(BackendErrorCode.RequestExpired, (await fixture.ExecuteAsync(valid with { ValidUntilUtc = null })).Error!.Code);
        Assert.Equal(BackendErrorCode.RequestExpired, (await fixture.ExecuteAsync(valid with { ValidUntilUtc = fixture.Clock.GetUtcNow() })).Error!.Code);
        Assert.Equal(BackendErrorCode.InvalidRequest, (await fixture.ExecuteAsync(valid with
        {
            ValidUntilUtc = fixture.Clock.GetUtcNow().AddSeconds(600).AddTicks(1),
        })).Error!.Code);
        Accepted(await fixture.ExecuteAsync(valid with { ValidUntilUtc = fixture.Clock.GetUtcNow().AddSeconds(600) }));
        Assert.Equal("1", (await fixture.SummaryAsync()).ProjectRevision);
        Assert.Equal(fixture.Clock.GetUtcNow(), fixture.Registry.ServerTimeUtc);
        Assert.Equal(600, fixture.Registry.MaxCommandRetryWindowSeconds);
        Assert.Equal(1, fixture.Registry.MaxCommandReceiptsPerSession);
    }

    [Fact]
    public async Task 截止前最后一刻可重试而截止时刻及清理后都不能重新执行()
    {
        using CommandFixture fixture = await CommandFixture.CreateAsync(new() { MaxReceiptsPerSession = 1 });
        ProjectCommandRequest request = fixture.Request("expires", "0", fixture.SetLine("100001", "200003"), seconds: 5);
        ProjectCommandResponse response = Accepted(await fixture.ExecuteAsync(request));
        fixture.Clock.Advance(TimeSpan.FromSeconds(5).Subtract(TimeSpan.FromTicks(1)));
        AssertEquivalent(response, Accepted(await fixture.ExecuteAsync(request)));
        fixture.Clock.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(BackendErrorCode.RequestExpired, (await fixture.ExecuteAsync(request)).Error!.Code);
        Accepted(await fixture.ExecuteAsync(fixture.Request("after-cleanup", "1", new("history.undo"))));
        Assert.Equal(BackendErrorCode.RequestExpired, (await fixture.ExecuteAsync(request)).Error!.Code);
        Assert.Equal("2", (await fixture.SummaryAsync()).ProjectRevision);
        Assert.Equal(1, (await fixture.HistoryAsync()).RedoCount);
    }

    [Fact]
    public async Task 服务重启后旧会话不存在仍以旧宿主身份拒绝命令()
    {
        using CommandFixture fixture = await CommandFixture.CreateAsync();
        ProjectCommandRequest request = fixture.Request("before-restart", "0", fixture.SetLine("100001", "200003"));
        Accepted(await fixture.ExecuteAsync(request));
        using TemporaryDirectory directory = new();
        ResourceStore store = new(new() { ResourceRoot = directory.Path });
        using ProjectSessionRegistry restarted = new("host-two", store,
            new((_, _) => Task.FromResult<LoadedWaveform?>(Sample())), fixture.Clock);

        ProjectCommandExecutionResult rejected = await restarted.ExecuteCommandAsync("client", fixture.Session.ProjectSessionId, request);

        Assert.Equal(BackendErrorCode.HostRestarted, rejected.Error!.Code);
        Assert.Equal(BackendErrorCode.SessionNotFound, (await restarted.ExecuteCommandAsync("client", fixture.Session.ProjectSessionId,
            request with { HostInstanceId = "host-two" })).Error!.Code);
        Assert.Equal("1", (await fixture.SummaryAsync()).ProjectRevision);
    }

    [Fact]
    public async Task 配额满时拒绝新请求而有效回执可重试且不被驱逐()
    {
        using CommandFixture fixture = await CommandFixture.CreateAsync(new() { MaxReceiptsPerSession = 2 });
        ProjectCommandRequest first = fixture.Request("first", "0", fixture.SetLine("100001", "200003"), seconds: 5);
        ProjectCommandResponse original = Accepted(await fixture.ExecuteAsync(first));
        ProjectCommandRequest rejected = fixture.Request("stale", "0", new("history.undo"), seconds: 10);
        ProjectCommandExecutionResult stale = await fixture.ExecuteAsync(rejected);
        Assert.Equal(BackendErrorCode.RevisionConflict, stale.Error!.Code);
        ProjectCommandRequest third = fixture.Request("third", "1", new("history.undo"));
        Assert.Equal(BackendErrorCode.RequestQuotaExceeded, (await fixture.ExecuteAsync(third)).Error!.Code);
        AssertEquivalent(original, Accepted(await fixture.ExecuteAsync(first)));
        Assert.Equal(stale.Error, (await fixture.ExecuteAsync(rejected)).Error);
        Assert.Equal(BackendErrorCode.RequestIdReuse,
            (await fixture.ExecuteAsync(first with { Command = new("history.undo") })).Error!.Code);
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        // 只读也回收已到期回执，仍有效的拒绝回执保留。
        Assert.Equal("1", (await fixture.SummaryAsync()).ProjectRevision);
        Accepted(await fixture.ExecuteAsync(third));
        Assert.Equal(stale.Error, (await fixture.ExecuteAsync(rejected)).Error);
        Assert.Equal("2", (await fixture.SummaryAsync()).ProjectRevision);
    }

    [Fact]
    public async Task 无变化命令保留修订历史脏状态和可重做步骤()
    {
        using CommandFixture fixture = await CommandFixture.CreateAsync();
        Accepted(await fixture.ExecuteAsync(fixture.Request("change", "0", fixture.SetLine("100001", "200003"))));
        Accepted(await fixture.ExecuteAsync(fixture.Request("undo", "1", new("history.undo"))));
        ProjectHistoryResponse before = await fixture.HistoryAsync();
        LyricLineDto current = await fixture.LineAsync();
        ProjectCommandResponse noOp = Accepted(await fixture.ExecuteAsync(fixture.Request("noop", "2",
            fixture.SetLine(current.Time.StartUs!, current.Time.EndUs!))));
        Assert.False(noOp.Changed);
        Assert.Equal("2", noOp.PreviousRevision);
        Assert.Equal("2", noOp.ProjectRevision);
        Assert.True(noOp.IsDirty);
        Assert.True(noOp.CanRedo);
        Assert.Empty(noOp.ChangeSet.Areas);
        Assert.Equal(before, await fixture.HistoryAsync());
        Accepted(await fixture.ExecuteAsync(fixture.Request("redo", "2", new("history.redo"))));
        Assert.Equal(new LyricTimeDto("100001", "200003"), (await fixture.LineAsync()).Time);
    }

    [Fact]
    public async Task 非法和未知命令及空JSON命令只返回拒绝并保留工程和历史()
    {
        using CommandFixture fixture = await CommandFixture.CreateAsync();
        ProjectHistoryResponse before = await fixture.HistoryAsync();
        LyricLineDto initial = await fixture.LineAsync();
        ProjectCommandDto?[] commands =
        [
            null, new(null!), new(""), new("project.replace"), new("history.undo"), new("history.redo"),
            new("history.undo", new("primary", initial.Id)),
            new("lyrics.setTimeRange"), new("lyrics.setTimeRange", new(null!, initial.Id), "1", "2"),
            fixture.SetLine("1", null!), fixture.SetLine(" ", "2"), fixture.SetLine("1.5", "2"),
            fixture.SetLine("9223372036854775808", "2"), fixture.SetLine("-1", "2"), fixture.SetLine("2", "1"),
            fixture.SetLine("1", "3000001"), new("lyrics.setTimeRange", new("primary", "missing"), "1", "2"),
            new("lyrics.setTimeRange", new("primary", initial.Id, "missing"), "1", "2"),
        ];
        for (int index = 0; index < commands.Length; index++)
            Assert.False((await fixture.ExecuteAsync(fixture.Request("invalid-" + index, "0", commands[index]!))).Accepted);
        Assert.Equal(BackendErrorCode.InvalidRequest,
            (await fixture.ExecuteAsync(JsonSerializer.Deserialize<ProjectCommandRequest>("null"))).Error!.Code);
        Assert.Equal(BackendErrorCode.InvalidRequest,
            (await fixture.ExecuteAsync(fixture.Request("bad-revision", "-1", new("history.undo")))).Error!.Code);
        Assert.Equal(before, await fixture.HistoryAsync());
        LyricLineDto after = await fixture.LineAsync();
        Assert.Equal(initial.Time, after.Time);
        Assert.Equal(initial.Tokens.Select(token => token.Time), after.Tokens.Select(token => token.Time));
        Assert.Equal("0", (await fixture.SummaryAsync()).ProjectRevision);
    }

    [Fact]
    public void JSON未知字段不能绕过已公布的命令语义()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web);
        foreach (string body in new[]
        {
            "{\"expectedRevision\":\"0\",\"command\":{\"type\":\"history.undo\"},\"extra\":true}",
            "{\"expectedRevision\":\"0\",\"command\":{\"type\":\"history.undo\",\"extra\":true}}",
            "{\"expectedRevision\":\"0\",\"command\":{\"type\":\"lyrics.setTimeRange\",\"target\":{\"branchId\":\"primary\",\"lineId\":\"line\",\"extra\":true}}}",
        })
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ProjectCommandRequest>(body, options));
    }

    [Fact]
    public async Task 读摘要歌词和历史各自绑定同锁捕获的完整修订()
    {
        using CommandFixture fixture = await CommandFixture.CreateAsync();
        TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task writer = Task.Run(async () =>
        {
            await start.Task;
            for (int revision = 0; revision < 30; revision++)
            {
                string startUs = revision % 2 == 0 ? "100001" : "100003";
                Accepted(await fixture.ExecuteAsync(fixture.Request("write-" + revision, revision.ToString(CultureInfo.InvariantCulture), fixture.SetLine(startUs, "200007"))));
                await Task.Yield();
            }
        });
        Task reader = Task.Run(async () =>
        {
            await start.Task;
            for (int index = 0; index < 80; index++)
            {
                ProjectSessionSummaryResponse summary = await fixture.SummaryAsync();
                Assert.Equal(summary.ProjectRevision != "0", summary.CanUndo);
                Assert.False(summary.CanRedo);
                Assert.NotNull(summary.EventCursor);
                ProjectHistoryResponse history = await fixture.HistoryAsync();
                Assert.Equal(int.Parse(history.ProjectRevision, CultureInfo.InvariantCulture), history.UndoCount);
                LyricsLinesResponse lyrics = (await fixture.Registry.ReadLyricsAsync("client", fixture.Session.ProjectSessionId, "primary"))!;
                int revision = int.Parse(lyrics.ProjectRevision, CultureInfo.InvariantCulture);
                if (revision > 0)
                    Assert.Equal(revision % 2 == 1 ? "100001" : "100003", Assert.Single(lyrics.Lines).Time.StartUs);
                await Task.Yield();
            }
        });
        start.SetResult();
        await Task.WhenAll(writer, reader);
        Assert.Equal("30", (await fixture.SummaryAsync()).ProjectRevision);
        Assert.Equal(30, (await fixture.HistoryAsync()).UndoCount);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(601, 1)]
    [InlineData(1, 0)]
    public void 回执配置拒绝无限窗口或非正配额(int seconds, int count) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProjectCommandRetentionOptions
        {
            MaxRetryWindowSeconds = seconds, MaxReceiptsPerSession = count,
        }.Validate());

    [Fact]
    public async Task 输出DTO数组被消费者改写不能污染留存回执或工程()
    {
        using CommandFixture fixture = await CommandFixture.CreateAsync();
        ProjectCommandRequest request = fixture.Request("receipt", "0", fixture.SetLine("100001", "200003"));
        ProjectCommandResponse first = Accepted(await fixture.ExecuteAsync(request));
        string expected = JsonSerializer.Serialize(first);
        first.ChangeSet.Areas[0] = "mutated";
        first.ChangeSet.BranchIds[0] = "mutated";
        first.ChangeSet.LineIds[0] = "mutated";
        ProjectCommandResponse retry = Accepted(await fixture.ExecuteAsync(request));
        Assert.Equal(expected, JsonSerializer.Serialize(retry));
        retry.ChangeSet.Areas[0] = "mutated-again";
        Assert.Equal(expected, JsonSerializer.Serialize(Accepted(await fixture.ExecuteAsync(request))));
        Assert.Equal("1", (await fixture.SummaryAsync()).ProjectRevision);
        Assert.Equal(1, (await fixture.HistoryAsync()).UndoCount);
    }

    [Fact]
    public async Task 上传新建与多工程命令共用client回执且新建重试保留最初摘要()
    {
        using TemporaryDirectory directory = new();
        ManualClock clock = new();
        ResourceStore store = new(new() { ResourceRoot = directory.Path }, clock);
        using ProjectSessionRegistry registry = new("host-one", store,
            new((_, _) => Task.FromResult<LoadedWaveform?>(Sample())), clock);
        RequestMetadataDto Metadata(string id) => new(id, "host-one", clock.GetUtcNow().AddSeconds(30));
        byte[] lyricsBytes = Encoding.UTF8.GetBytes("[00:00.00]hello\n");
        BackendOperationResult<AssetResponse> upload = await registry.UploadAssetAsync("client", Metadata("lyrics"), "song.lrc", "text/plain", lyricsBytes);
        AssetResponse lyrics = upload.Response!;
        Assert.Equal(lyrics, (await registry.UploadAssetAsync("client", Metadata("lyrics"), "song.lrc", "TEXT/PLAIN; charset=utf-8", lyricsBytes)).Response);
        Assert.Single(Directory.GetFiles(directory.Path, "*.lrc", SearchOption.AllDirectories));
        AssetResponse audio = (await registry.UploadAssetAsync("client", Metadata("audio"), "song.wav", "audio/wav", [1, 2, 3])).Response!;
        CreateProjectSessionRequest request = new("one", audio.AssetId, lyrics.AssetId, Metadata: Metadata("create"));
        ProjectSessionCreateResult created = await registry.CreateAsync("client", request);
        Assert.True(created.Accepted);
        ProjectSession first = created.Session!;
        ProjectSessionSummaryResponse initial = created.Summary!;
        Assert.Equal("0", initial.ProjectRevision);
        ProjectSession second = (await registry.CreateAsync("client", request with { Name = "two", Metadata = Metadata("create-two") })).Session!;
        LyricLineDto line = Assert.Single((await registry.ReadLyricsAsync("client", first.ProjectSessionId, "primary"))!.Lines);
        ProjectCommandRequest command = new("0", new("lyrics.setTimeRange", new("primary", line.Id), "100001", "200003"),
            "cross-path", "host-one", clock.GetUtcNow().AddSeconds(30));
        Accepted(await registry.ExecuteCommandAsync("client", first.ProjectSessionId, command));
        Assert.Equal(BackendErrorCode.RequestIdReuse, (await registry.ExecuteCommandAsync("client", second.ProjectSessionId, command)).Error!.Code);
        Assert.Equal(BackendErrorCode.RequestIdReuse, (await registry.CreateAsync("client", request with { Metadata = Metadata("cross-path") })).Error!.Code);
        Assert.Equal(initial, (await registry.CreateAsync("client", request)).Summary);
        ClientStateResponse state = await registry.ReadClientStateAsync("client");
        Assert.Equal(2, state.Sessions.Length);
        Assert.All(state.Sessions, item => Assert.Equal(state.EventCursor, item.EventCursor));
        Assert.Equal("1", state.Sessions.Single(item => item.ProjectSessionId == first.ProjectSessionId).ProjectRevision);
        Assert.Empty(registry.ReadEvents("client", state.EventCursor).Events);
        ProjectCommandResponse undo = Accepted(await registry.ExecuteCommandAsync("client", first.ProjectSessionId,
            new("1", new("history.undo"), "undo", "host-one", clock.GetUtcNow().AddSeconds(30))));
        BackendEventResponse changed = Assert.Single(registry.ReadEvents("client", state.EventCursor).Events);
        Assert.Equal("2", changed.Summary!.ProjectRevision);
        Assert.Equal(undo.EventCursor, changed.EventCursor);
        Assert.Equal("4", changed.EventSeq);
    }

    [Fact]
    public async Task 新建异步准备与已注册会话持有真实音频引用且租约到期不丢工程()
    {
        using TemporaryDirectory directory = new();
        ManualClock clock = new();
        ResourceStore store = new(new() { ResourceRoot = directory.Path, UnreferencedAssetLifetimeSeconds = 2 }, clock);
        StoredAsset lyrics = await store.UploadAsync("client", "song.lrc", "text/plain", new MemoryStream(Encoding.UTF8.GetBytes("[00:00.00]hello\n")));
        StoredAsset audio = await store.UploadAsync("client", "song.wav", "audio/wav", new MemoryStream([1, 2, 3]));
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ProjectSessionRegistry registry = new("host-one", store, new(async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            return Sample();
        }), clock);
        Task<ProjectSessionCreateResult> creating = registry.CreateAsync("client", new("one", audio.AssetId, lyrics.AssetId));
        await entered.Task;
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(0, store.CollectExpired());
        release.TrySetResult();
        ProjectSessionCreateResult created = await creating;
        Assert.True(created.Accepted);
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(1, store.CollectExpired());
        Assert.False(File.Exists(lyrics.StoragePath));
        Assert.True(File.Exists(audio.StoragePath));
        Assert.True((await registry.ReadSummaryAsync("client", created.Session!.ProjectSessionId))!.IsDirty);
        await registry.DisposeAsync();
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(1, store.CollectExpired());
        Assert.False(File.Exists(audio.StoragePath));
    }

    [Fact]
    public async Task 已排队的旧查询进入writer后必须重新检查显式关闭状态()
    {
        using CommandFixture fixture = await CommandFixture.CreateAsync();
        SemaphoreSlim writer = fixture.Session.Writer;
        await writer.WaitAsync();
        Task<ProjectSessionSummaryResponse?> summary = fixture.Registry.ReadSummaryAsync("client", fixture.Session.ProjectSessionId);
        Task<ProjectHistoryResponse?> history = fixture.Registry.ReadHistoryAsync("client", fixture.Session.ProjectSessionId);
        Task<LyricsLinesResponse?> lyrics = fixture.Registry.ReadLyricsAsync("client", fixture.Session.ProjectSessionId, "primary");
        try
        {
            Assert.False(summary.IsCompleted);
            Assert.False(history.IsCompleted);
            Assert.False(lyrics.IsCompleted);
            fixture.Session.IsClosed = true;
        }
        finally { writer.Release(); }
        Assert.Null(await summary);
        Assert.Null(await history);
        Assert.Null(await lyrics);
    }

    [Fact]
    public async Task 多工程state在全部writer内一次捕获摘要与事件游标不跳过并发编辑()
    {
        using TemporaryDirectory directory = new();
        ManualClock clock = new();
        ResourceStore store = new(new() { ResourceRoot = directory.Path }, clock);
        StoredAsset lyrics = await store.UploadAsync("client", "song.lrc", "text/plain", new MemoryStream(Encoding.UTF8.GetBytes("[00:00.00]hello\n")));
        StoredAsset audio = await store.UploadAsync("client", "song.wav", "audio/wav", new MemoryStream([1, 2, 3]));
        using ProjectSessionRegistry registry = new("host-one", store,
            new((_, _) => Task.FromResult<LoadedWaveform?>(Sample())), clock);
        ProjectSession a = (await registry.CreateAsync("client", new("one", audio.AssetId, lyrics.AssetId))).Session!;
        ProjectSession b = (await registry.CreateAsync("client", new("two", audio.AssetId, lyrics.AssetId))).Session!;
        ProjectSession[] ordered = [.. new[] { a, b }.OrderBy(session => session.ProjectSessionId, StringComparer.Ordinal)];
        SemaphoreSlim firstWriter = ordered[0].Writer;
        SemaphoreSlim secondWriter = ordered[1].Writer;
        LyricLineDto line = Assert.Single((await registry.ReadLyricsAsync("client", ordered[0].ProjectSessionId, "primary"))!.Lines);
        await secondWriter.WaitAsync();
        Task<ClientStateResponse> reading = registry.ReadClientStateAsync("client");
        Task<ProjectCommandExecutionResult>? command = null;
        try
        {
            Assert.Equal(0, firstWriter.CurrentCount);
            Assert.False(reading.IsCompleted);
            command = registry.ExecuteCommandAsync("client", ordered[0].ProjectSessionId,
                new("0", new("lyrics.setTimeRange", new("primary", line.Id), "100001", "200003"),
                    "while-reading", "host-one", clock.GetUtcNow().AddSeconds(30)));
            Assert.False(command.IsCompleted);
        }
        finally { secondWriter.Release(); }
        ClientStateResponse state = await reading;
        Assert.All(state.Sessions, summary => Assert.Equal("0", summary.ProjectRevision));
        ProjectCommandResponse changed = Accepted(await command!);
        BackendEventResponse observed = Assert.Single(registry.ReadEvents("client", state.EventCursor).Events);
        Assert.Equal(changed.ProjectRevision, observed.Summary!.ProjectRevision);
        Assert.Equal(changed.EventCursor, observed.EventCursor);
    }

    [Fact]
    public async Task 客户端租约到期state保留脏工程且恢复身份可继续访问并遵守会话数量上限()
    {
        using TemporaryDirectory directory = new();
        ManualClock clock = new();
        const string secret = "0123456789012345678901234567890123456789";
        ClientRegistry clients = new("host-one", secret, new() { MaxSessionsPerClient = 1, TokenLifetimeSeconds = 1, LeaseLifetimeSeconds = 2 }, clock);
        ClientRegistrationResponse original = clients.Register(secret, new()).Response!;
        ResourceStore store = new(new() { ResourceRoot = directory.Path }, clock);
        StoredAsset lyrics = await store.UploadAsync(original.ClientId, "song.lrc", "text/plain", new MemoryStream(Encoding.UTF8.GetBytes("[00:00.00]hello\n")));
        StoredAsset audio = await store.UploadAsync(original.ClientId, "song.wav", "audio/wav", new MemoryStream([1, 2, 3]));
        using ProjectSessionRegistry registry = new("host-one", store,
            new((_, _) => Task.FromResult<LoadedWaveform?>(Sample())), clock, clients: clients);
        ProjectSession session = (await registry.CreateAsync(original.ClientId, new("one", audio.AssetId, lyrics.AssetId))).Session!;
        Assert.Equal(BackendErrorCode.SessionQuotaExceeded, (await registry.CreateAsync(original.ClientId, new("two", audio.AssetId, lyrics.AssetId))).Error!.Code);
        clock.Advance(TimeSpan.FromSeconds(3));
        ClientStateResponse dormant = await registry.ReadClientStateAsync(original.ClientId);
        Assert.True(dormant.Dormant);
        Assert.True(Assert.Single(dormant.Sessions).IsDirty);
        Assert.Equal(session.ProjectSessionId, dormant.Sessions[0].ProjectSessionId);
        ClientRegistrationResponse resumed = clients.Register(secret, new(ResumeClientId: original.ClientId, ResumeToken: original.ResumeToken)).Response!;
        ClientStateResponse active = await registry.ReadClientStateAsync(resumed.ClientId);
        Assert.False(active.Dormant);
        Assert.Equal(dormant.EventCursor, active.EventCursor);
        Assert.Equal(dormant.Sessions[0], Assert.Single(active.Sessions));
        Assert.True(registry.TryGet(resumed.ClientId, session.ProjectSessionId, out _));
    }

    [Fact]
    public async Task 并发停止都等待同一已接受请求且结束前不会释放会话资源()
    {
        using TemporaryDirectory directory = new();
        ManualClock clock = new();
        ResourceStore store = new(new() { ResourceRoot = directory.Path, UnreferencedAssetLifetimeSeconds = 1 }, clock);
        StoredAsset lyrics = await store.UploadAsync("client", "song.lrc", "text/plain", new MemoryStream(Encoding.UTF8.GetBytes("[00:00.00]hello\n")));
        StoredAsset audio = await store.UploadAsync("client", "song.wav", "audio/wav", new MemoryStream([1, 2, 3]));
        ProjectSessionRegistry registry = new("host-one", store, new((_, _) => Task.FromResult<LoadedWaveform?>(Sample())), clock);
        ProjectSession session = (await registry.CreateAsync("client", new("one", audio.AssetId, lyrics.AssetId))).Session!;
        TaskCompletionSource accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<BackendOperationResult<string>> operation = registry.Receipts.ExecuteAsync("client",
            new("draining", "host-one", clock.GetUtcNow().AddSeconds(30)), "test-operation", null, async () =>
            {
                accepted.TrySetResult();
                await release.Task;
                return BackendOperationResult<string>.Success("done");
            });
        await accepted.Task;
        Task first = registry.ShutdownAsync().AsTask();
        Task second = registry.ShutdownAsync().AsTask();
        try
        {
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            clock.Advance(TimeSpan.FromSeconds(2));
            store.CollectExpired();
            Assert.True(File.Exists(audio.StoragePath));
            Assert.True(registry.TryGet("client", session.ProjectSessionId, out _));
        }
        finally { release.TrySetResult(); }
        Assert.Equal("done", (await operation).Response);
        await Task.WhenAll(first, second);
        Assert.False(registry.TryGet("client", session.ProjectSessionId, out _));
        clock.Advance(TimeSpan.FromSeconds(1));
        store.CollectExpired();
        Assert.False(File.Exists(audio.StoragePath));
    }

    private static void AssertEquivalent(ProjectCommandResponse expected, ProjectCommandResponse actual) =>
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));

    private static ProjectCommandResponse Accepted(ProjectCommandExecutionResult result)
    {
        Assert.True(result.Accepted, result.Error is null ? null : $"{result.Error.Code}: {result.Error.Field} {result.Error.Message}");
        return Assert.IsType<ProjectCommandResponse>(result.Response);
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks = new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero).UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        public void Advance(TimeSpan duration) => Interlocked.Add(ref _ticks, duration.Ticks);
    }

    private sealed class CommandFixture : IDisposable
    {
        private readonly TemporaryDirectory _directory = new();
        public ManualClock Clock { get; } = new();
        public ProjectSessionRegistry Registry { get; private set; } = null!;
        public ProjectSession Session { get; private set; } = null!;
        private string _lineId = null!;

        public static async Task<CommandFixture> CreateAsync(ProjectCommandRetentionOptions? retention = null)
        {
            CommandFixture fixture = new();
            ResourceStore store = new(new() { ResourceRoot = fixture._directory.Path });
            StoredAsset lyrics = await store.UploadAsync("client", "song.lrc", "text/plain",
                new MemoryStream(Encoding.UTF8.GetBytes("[00:00.00]hello\n")));
            StoredAsset audio = await store.UploadAsync("client", "song.wav", "audio/wav", new MemoryStream([1, 2, 3]));
            fixture.Registry = new("host-one", store, new((_, _) => Task.FromResult<LoadedWaveform?>(Sample())), fixture.Clock, retention);
            ProjectSessionCreateResult created = await fixture.Registry.CreateAsync("client", new("commands", audio.AssetId, lyrics.AssetId));
            Assert.True(created.Accepted);
            fixture.Session = created.Session!;
            fixture._lineId = (await fixture.LineAsync()).Id;
            return fixture;
        }

        public ProjectCommandDto SetLine(string start, string end) => new("lyrics.setTimeRange", new("primary", _lineId), start, end);
        public ProjectCommandRequest Request(string id, string revision, ProjectCommandDto command, int seconds = 300) =>
            new(revision, command, id, "host-one", Clock.GetUtcNow().AddSeconds(seconds));
        public Task<ProjectCommandExecutionResult> ExecuteAsync(ProjectCommandRequest? request) =>
            Registry.ExecuteCommandAsync("client", Session.ProjectSessionId, request);
        public async Task<LyricLineDto> LineAsync() =>
            Assert.Single((await Registry.ReadLyricsAsync("client", Session.ProjectSessionId, "primary"))!.Lines);
        public async Task<ProjectHistoryResponse> HistoryAsync() =>
            (await Registry.ReadHistoryAsync("client", Session.ProjectSessionId))!;
        public async Task<ProjectSessionSummaryResponse> SummaryAsync() =>
            (await Registry.ReadSummaryAsync("client", Session.ProjectSessionId))!;
        public void Dispose() { Registry.Dispose(); _directory.Dispose(); }
    }

    private static LoadedWaveform Sample()
    {
        WaveformPeakBuilder builder = new();
        builder.Append(new float[24_000]);
        return new(new AudioStreamInfo(3_000_000, 8_000, 1, "pcm", "wav"), builder.Build(8_000));
    }
}
