using System.Globalization;
using System.Reflection;
using System.Text.Json;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class BackendEditingCommandsTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly LyricTimingTargetDto First = new("primary", "one", "first");
    private static readonly LyricTimingTargetDto Second = new("primary", "one", "second");
    private static readonly LyricTimingTargetDto Third = new("primary", "one", "third");

    [Fact]
    public async Task 起点估算与同端点终点确认各一次历史且撤销恢复时间来源状态()
    {
        using Fixture f = await Fixture.Create();
        Project original = f.Workspace.Project;
        ProjectCommandResponse start = Accepted(await f.Command("0", new StampTokenStartCommandDto(Third, "1000001")));
        Assert.Equal("1", start.ProjectRevision);
        Assert.True(start.TimingResult!.EstimatedPlaceholder);
        Assert.Equal(new LyricTimeDto("1000001", "1033334"), (await f.Line()).Tokens[2].Time);
        Assert.Equal(TimingStatus.Estimated, f.Token(2).TimingStatus);
        Assert.Equal(["third"], start.ChangeSet.TokenIds);
        ProjectCommandResponse end = Accepted(await f.Command("1", new StampTokenEndCommandDto(Third, "1033334")));
        Assert.True(end.Changed);
        Assert.False(end.TimingResult!.EstimatedPlaceholder);
        Assert.Equal(TimingStatus.Edited, f.Token(2).TimingStatus);
        Assert.Equal(2, (await f.History()).UndoCount);
        Accepted(await f.Command("2", new("history.undo")));
        Assert.Equal(TimingStatus.Estimated, f.Token(2).TimingStatus);
        Accepted(await f.Command("3", new("history.undo")));
        Assert.Same(original, f.Workspace.Project);
        Accepted(await f.Command("4", new("history.redo")));
        Assert.Equal(TimingStatus.Estimated, f.Token(2).TimingStatus);
        Accepted(await f.Command("5", new("history.redo")));
        Assert.Equal(TimingStatus.Edited, f.Token(2).TimingStatus);
    }

    [Fact]
    public async Task 清除元素后空时间重复清除不增修订事件历史且保留重做()
    {
        using Fixture f = await Fixture.Create();
        Accepted(await f.Command("0", new ClearTokenTimeCommandDto(First)));
        Assert.Null(f.Token(0).Time); Assert.Equal(TimingStatus.Missing, f.Token(0).TimingStatus);
        Accepted(await f.Command("1", new NudgeTokenTimeCommandDto(Second, 1)));
        Accepted(await f.Command("2", new("history.undo")));
        ProjectHistoryResponse history = await f.History();
        ProjectCommandResponse noOp = Accepted(await f.Command("3", new ClearTokenTimeCommandDto(First)));
        Assert.False(noOp.Changed); Assert.Equal("3", noOp.ProjectRevision);
        Assert.Null(noOp.TimingResult);
        Assert.DoesNotContain("timingResult", JsonSerializer.Serialize(noOp, Json), StringComparison.Ordinal);
        Assert.Empty(noOp.ChangeSet.Areas); Assert.True(noOp.CanRedo);
        Assert.Equal(history, await f.History());
        Assert.Equal(noOp.EventCursor, (await f.Registry.ReadSummaryAsync("client", f.Session.ProjectSessionId))!.EventCursor);
        Accepted(await f.Command("3", new("history.redo")));
        Accepted(await f.Command("4", new("history.undo")));
        Accepted(await f.Command("5", new("history.undo")));
        Assert.Equal(TimeRange.Create(100000, 200000), f.Token(0).Time);
    }

    [Fact]
    public async Task 连续HTTP微调和批次不跨请求合并而零帧微调保重做()
    {
        using Fixture f = await Fixture.Create();
        Accepted(await f.Command("0", new NudgeTokenTimeCommandDto(First, 1)));
        Accepted(await f.Command("1", new NudgeTokenTimeCommandDto(First, 1)));
        Assert.Equal(TimeRange.Create(166666, 266666), f.Token(0).Time);
        Assert.Equal(2, (await f.History()).UndoCount);
        Accepted(await f.Command("2", new("history.undo")));
        Assert.Equal(TimeRange.Create(133333, 233333), f.Token(0).Time);
        ProjectHistoryResponse history = await f.History();
        Assert.False(Accepted(await f.Command("3", new NudgeTokenTimeCommandDto(First, 0))).Changed);
        Assert.Equal(history, await f.History());
        Accepted(await f.Command("3", Batch(First, "150001", "250003")));
        Accepted(await f.Command("4", Batch(First, "160005", "260007")));
        Assert.Equal(3, (await f.History()).UndoCount);
        Accepted(await f.Command("5", new("history.undo")));
        Assert.Equal(TimeRange.Create(150001, 250003), f.Token(0).Time);
    }

    [Fact]
    public async Task 整行平移保持未定时元素和微秒并一次撤销重做()
    {
        using Fixture f = await Fixture.Create();
        Project original = f.Workspace.Project;
        ProjectCommandResponse shifted = Accepted(await f.Command("0", new ShiftLineCommandDto(new("primary", "one"), "+123457")));
        Assert.Equal(TimeRange.Create(123457, 2123457), f.Workspace.Project.Lyrics.Lines[0].Time);
        Assert.Equal(TimeRange.Create(223457, 323457), f.Token(0).Time);
        Assert.Null(f.Token(2).Time); Assert.Equal(TimingStatus.Missing, f.Token(2).TimingStatus);
        Assert.Equal(["first", "second"], shifted.ChangeSet.TokenIds);
        Assert.Equal(1, (await f.History()).UndoCount);
        Project result = f.Workspace.Project;
        Accepted(await f.Command("1", new("history.undo"))); Assert.Same(original, f.Workspace.Project);
        Accepted(await f.Command("2", new("history.redo"))); Assert.Same(result, f.Workspace.Project);
    }

    [Fact]
    public async Task 批次第二项非法拒绝整批且合法批次不额外应用媒体上界()
    {
        using Fixture f = await Fixture.Create(lineEndUs: 6000000);
        Project original = f.Workspace.Project;
        Assert.False((await f.Command("0", new SetTokenTimesCommandDto([
            new(First, "100001", "200003"), new(Second, "150000", "700000")]))).Accepted);
        Assert.Same(original, f.Workspace.Project); Assert.Equal(0, (await f.History()).UndoCount);
        ProjectCommandResponse changed = Accepted(await f.Command("0", Batch(Third, "4000001", "4500007")));
        Assert.Equal("1", changed.ProjectRevision);
        Assert.Equal(TimeRange.Create(4000001, 4500007), f.Token(2).Time);
        Assert.Equal(3000000, f.Session.AudioDurationUs);
        Accepted(await f.Command("1", new("history.undo"))); Assert.Same(original, f.Workspace.Project);
        Accepted(await f.Command("2", new("history.redo")));
        Assert.Equal(TimeRange.Create(4000001, 4500007), f.Token(2).Time);
    }

    [Fact]
    public async Task 批次条数重复目标空元素及行目标拒绝且不部分采用()
    {
        using Fixture f = await Fixture.Create();
        Project original = f.Workspace.Project;
        ProjectCommandDto[] inputs = [
            new SetTokenTimesCommandDto(null), new SetTokenTimesCommandDto([]),
            new SetTokenTimesCommandDto(Enumerable.Repeat(new TokenTimeChangeDto(First, "1", "2"), 1001).ToArray()),
            new SetTokenTimesCommandDto([new(First, "1", "2"), new(First, "3", "4")]),
            new SetTokenTimesCommandDto([null!]),
            new SetTokenTimesCommandDto([new(new("primary", "one"), "1", "2")]),
            new SetTokenTimesCommandDto([new(First, "1", "2"), new(Second, "3", "no")]),
        ];
        foreach (ProjectCommandDto input in inputs)
            Assert.Equal(BackendErrorCode.InvalidRequest, (await f.Command("0", input)).Error!.Code);
        Assert.Same(original, f.Workspace.Project); Assert.Equal(0, (await f.History()).UndoCount);
    }

    [Fact]
    public async Task 一千个不同元素批次在上限合法且提交一次历史()
    {
        using Fixture f = await Fixture.Create();
        LyricToken[] tokens = Enumerable.Range(0, 1000).Select(index => new LyricToken
            { Id = "bulk-" + index.ToString(CultureInfo.InvariantCulture), Text = "字" }).ToArray();
        LyricLine line = f.Workspace.Project.Lyrics.Lines[0] with { Text = new string('字', 1000), Tokens = [.. tokens] };
        f.Workspace.Replace(f.Workspace.Project with { Lyrics = f.Workspace.Project.Lyrics with { Lines = [line] } });
        Project original = f.Workspace.Project;
        TokenTimeChangeDto[] changes = tokens.Select((token, index) => new TokenTimeChangeDto(new("primary", "one", token.Id),
            (index * 1000L).ToString(CultureInfo.InvariantCulture), (index * 1000L + 999).ToString(CultureInfo.InvariantCulture))).ToArray();
        ProjectCommandResponse response = Accepted(await f.Command("0", new SetTokenTimesCommandDto(changes)));
        Assert.Equal(1000, response.ChangeSet.TokenIds.Length); Assert.Equal(1, (await f.History()).UndoCount);
        Assert.False(response.ChangeSet.RequiresFullRefresh);
        Assert.Equal(["primary"], response.ChangeSet.BranchIds);
        Assert.Equal(["one"], response.ChangeSet.LineIds);
        Assert.Equal(tokens.Select(token => token.Id), response.ChangeSet.TokenIds);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(response.ChangeSet).Length <= ProjectCommandLimits.MaxChangeSetBytes);
        Assert.Equal(TimeRange.Create(999000, 999999), f.Token(999).Time);
        Accepted(await f.Command("1", new("history.undo"))); Assert.Same(original, f.Workspace.Project);
    }

    [Fact]
    public async Task 长Unicode目标批次完整失效通知保成功回执与一次撤销重做()
    {
        using Fixture f = await Fixture.Create();
        LyricToken[] tokens = Enumerable.Range(0, 100).Select(index => new LyricToken
        {
            Id = new string('字', 124) + index.ToString("D4", CultureInfo.InvariantCulture), Text = "字",
        }).ToArray();
        LyricLine line = f.Workspace.Project.Lyrics.Lines[0] with { Text = new string('字', 100), Tokens = [.. tokens] };
        f.Workspace.Replace(f.Workspace.Project with { Lyrics = f.Workspace.Project.Lyrics with { Lines = [line] } });
        Project original = f.Workspace.Project;
        string[] tokenIds = [.. tokens.Select(token => token.Id)];
        string[] areas = ["lyricsTiming", "timeline", "renderInputs"];
        ProjectChangeSetDto complete = new(areas, ["primary"], ["one"], tokenIds, false);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(complete).Length > f.Registry.Receipts.MaxReceiptBytes);
        TokenTimeChangeDto[] changes = tokens.Select((token, index) => new TokenTimeChangeDto(new("primary", "one", token.Id),
            (index * 1000L).ToString(CultureInfo.InvariantCulture), (index * 1000L + 999).ToString(CultureInfo.InvariantCulture))).ToArray();
        ProjectCommandRequest request = f.Request("0", new SetTokenTimesCommandDto(changes));
        ProjectCommandExecutionResult result = await f.Execute(request);
        ProjectHistoryResponse history = await f.History();
        Assert.True(result.Accepted, $"{result.Error?.Code}; 实际修订={history.ProjectRevision}；历史={history.UndoCount}");
        ProjectCommandResponse response = Accepted(result);
        Assert.Equal("1", response.ProjectRevision);
        Assert.Equal(1, history.UndoCount);
        Assert.Equal(TimeRange.Create(99000, 99999), f.Token(99).Time);
        AssertFullRefresh(response);
        ProjectCommandResponse replay = Accepted(await f.Execute(request));
        Assert.Equal(JsonSerializer.Serialize(response), JsonSerializer.Serialize(replay));
        Assert.Equal(1, (await f.History()).UndoCount);
        ProjectCommandResponse undo = Accepted(await f.Command("1", new("history.undo")));
        AssertFullRefresh(undo);
        Assert.Same(original, f.Workspace.Project);
        Assert.Equal(0, (await f.History()).UndoCount);
        ProjectCommandResponse redo = Accepted(await f.Command("2", new("history.redo")));
        AssertFullRefresh(redo);
        Assert.Equal(TimeRange.Create(99000, 99999), f.Token(99).Time);
        Assert.Equal(1, (await f.History()).UndoCount);

        void AssertFullRefresh(ProjectCommandResponse value)
        {
            Assert.Equal(areas, value.ChangeSet.Areas);
            Assert.True(value.ChangeSet.RequiresFullRefresh);
            Assert.Empty(value.ChangeSet.BranchIds);
            Assert.Empty(value.ChangeSet.LineIds);
            Assert.Empty(value.ChangeSet.TokenIds);
            Assert.True(JsonSerializer.SerializeToUtf8Bytes(BackendOperationResult<ProjectCommandResponse>.Success(value)).Length
                < f.Registry.Receipts.MaxReceiptBytes);
        }
    }

    [Fact]
    public async Task 新领域拒绝原因码稳定且不改旧命令错误wire()
    {
        using Fixture f = await Fixture.Create(lineEndUs: 6000000);
        var cases = new (ProjectCommandDto Command, string FailureCode, string Field)[]
        {
            (new StampTokenEndCommandDto(Third, "1"), "Untimed", "command.target"),
            (new StampTokenStartCommandDto(Third, "3000000"), "BeyondContentDuration", "command.playheadUs"),
            (new StampTokenEndCommandDto(First, "100000"), "InvalidRange", "command.playheadUs"),
            (new NudgeTokenTimeCommandDto(First, -4), "NudgeOutOfBounds", "command.frameDelta"),
            (new NudgeTokenTimeCommandDto(Third, 1), "Untimed", "command.target"),
            (new ShiftLineCommandDto(new("primary", "one"), "-1"), "DomainValidationFailed", "command.deltaUs"),
            (Batch(First, "500001", "600007"), "DomainValidationFailed", "command.changes"),
            (Batch(First with { TokenId = "missing" }, "1", "2"), "TargetStale", "command.changes"),
            (new ClearTokenTimeCommandDto(First with { TokenId = "missing" }), "TargetStale", "command.target"),
        };
        foreach ((ProjectCommandDto command, string failureCode, string field) in cases)
        {
            BackendErrorResponse error = (await f.Command("0", command)).Error!;
            Assert.Equal(BackendErrorCode.CommandRejected, error.Code);
            Assert.Equal(failureCode, error.FailureCode);
            Assert.Equal(field, error.Field);
            Assert.NotEqual(error.FailureCode, error.Field);
        }
        BackendErrorResponse old = (await f.Command("0", new("lyrics.setTimeRange", First, "-1", "10"))).Error!;
        Assert.Null(old.FailureCode);
        Assert.Equal("DomainValidationFailed", old.Field);
        Assert.DoesNotContain("failureCode", JsonSerializer.Serialize(old, Json), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 非法目标数字边界和未打轴拒绝且保持工程历史()
    {
        using Fixture f = await Fixture.Create();
        Project original = f.Workspace.Project;
        ProjectCommandDto[] inputs = [
            new StampTokenStartCommandDto(null, "1"), new StampTokenStartCommandDto(First, "9223372036854775808"),
            new StampTokenStartCommandDto(First, "1.1"), new StampTokenEndCommandDto(Third, "1500000"),
            new NudgeTokenTimeCommandDto(First, null), new NudgeTokenTimeCommandDto(First, int.MinValue),
            new NudgeTokenTimeCommandDto(First, -4), new ClearTokenTimeCommandDto(new("primary", "one")),
            new ClearTokenTimeCommandDto(First with { TokenId = "missing" }),
            new ShiftLineCommandDto(First, "1"), new ShiftLineCommandDto(new("primary", "one"), "-1"),
            new ShiftLineCommandDto(new("primary", "one"), "1000001"),
            new ShiftLineCommandDto(new("primary", "one"), long.MaxValue.ToString(CultureInfo.InvariantCulture)),
            new StampTokenStartCommandDto(Third, "3000000"),
            new StampTokenStartCommandDto(First, "1") with { StartUs = "2" },
            new StampTokenStartCommandDto(First, "1") with { Type = "history.undo" },
            new("lyrics.nudgeTokenTime", First),
        ];
        foreach (ProjectCommandDto input in inputs) Assert.False((await f.Command("0", input)).Accepted);
        Assert.Same(original, f.Workspace.Project); Assert.Equal(0, (await f.History()).UndoCount);
    }

    [Fact]
    public async Task 打点沿Application夹紧播放头且单元素命令继续遵守准备音频边界()
    {
        using Fixture f = await Fixture.Create(lineEndUs: 6000000);
        Accepted(await f.Command("0", new StampTokenStartCommandDto(First, "-999999")));
        Assert.Equal(0, f.Token(0).Time!.Value.StartUs);
        Accepted(await f.Command("1", Batch(Third, "2500001", "2700007")));
        Accepted(await f.Command("2", new StampTokenEndCommandDto(Third, "999999999")));
        Assert.Equal(3000000, f.Token(2).Time!.Value.EndUs);
        Assert.Equal(BackendErrorCode.CommandRejected, (await f.Command("3", new NudgeTokenTimeCommandDto(Third, 1))).Error!.Code);
        Assert.Equal(BackendErrorCode.CommandRejected,
            (await f.Command("3", new("lyrics.setTimeRange", Third, "2500001", "3000001"))).Error!.Code);
    }

    [Fact]
    public async Task 各新字段进入回执身份且等值微秒重试先于修订冲突()
    {
        using Fixture f = await Fixture.Create();
        var pairs = new (ProjectCommandDto First, ProjectCommandDto Same, ProjectCommandDto Different)[]
        {
            (new StampTokenStartCommandDto(First, "100001"), new StampTokenStartCommandDto(First, "+0100001"), new StampTokenStartCommandDto(First, "100002")),
            (new StampTokenEndCommandDto(First, "200001"), new StampTokenEndCommandDto(First, "+0200001"), new StampTokenEndCommandDto(First, "200002")),
            (new NudgeTokenTimeCommandDto(First, 0), new NudgeTokenTimeCommandDto(First, 0), new NudgeTokenTimeCommandDto(First, 1)),
            (new ShiftLineCommandDto(new("primary", "one"), "0"), new ShiftLineCommandDto(new("primary", "one"), "+00"), new ShiftLineCommandDto(new("primary", "one"), "1")),
            (Batch(First, "100003", "200005"), Batch(First, "+0100003", "+0200005"), Batch(First, "100003", "200006")),
        };
        foreach ((ProjectCommandDto first, ProjectCommandDto same, ProjectCommandDto different) in pairs)
        {
            string revision = (await f.History()).ProjectRevision;
            ProjectCommandRequest request = f.Request(revision, first);
            ProjectCommandResponse original = Accepted(await f.Execute(request));
            Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(Accepted(await f.Execute(request with { Command = same }))));
            Assert.Equal(BackendErrorCode.RequestIdReuse, (await f.Execute(request with { Command = different })).Error!.Code);
        }
    }

    [Fact]
    public async Task 批次排队后外部修改原数组不改变已绑定回执的执行载荷()
    {
        using Fixture f = await Fixture.Create();
        TokenTimeChangeDto[] changes = [new(First, "150001", "250003")];
        ProjectCommandRequest request = f.Request("0", new SetTokenTimesCommandDto(changes));
        SemaphoreSlim writer = (SemaphoreSlim)typeof(ProjectSession)
            .GetProperty("Writer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(f.Session)!;
        await writer.WaitAsync();
        Task<ProjectCommandExecutionResult> pending;
        try
        {
            pending = f.Execute(request);
            Assert.False(pending.IsCompleted);
            Assert.True(f.Registry.Receipts.HasUnexpiredReceipt("client", request.RequestId));
            changes[0] = new(First, "160005", "260007");
        }
        finally { writer.Release(); }

        ProjectCommandResponse response = Accepted(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(TimeRange.Create(150001, 250003), f.Token(0).Time);
        ProjectCommandResponse replay = Accepted(await f.Execute(request with
            { Command = Batch(First, "+0150001", "+0250003") }));
        Assert.Equal(JsonSerializer.Serialize(response), JsonSerializer.Serialize(replay));
        Assert.Equal(BackendErrorCode.RequestIdReuse, (await f.Execute(request)).Error!.Code);
        Assert.Equal(1, (await f.History()).UndoCount);
    }

    [Fact]
    public async Task 新命令相同修订并发只有一个成功且同回执并发只执行一次()
    {
        using Fixture f = await Fixture.Create();
        Task<ProjectCommandExecutionResult>[] different = Enumerable.Range(0, 12)
            .Select(_ => f.Command("0", new NudgeTokenTimeCommandDto(First, 1))).ToArray();
        ProjectCommandExecutionResult[] results = await Task.WhenAll(different);
        Assert.Single(results, result => result.Accepted);
        Assert.Equal(11, results.Count(result => result.Error?.Code == BackendErrorCode.RevisionConflict));
        ProjectCommandRequest same = f.Request("1", new NudgeTokenTimeCommandDto(First, 1));
        results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => f.Execute(same)));
        Assert.All(results, result => Assert.Equal("2", Accepted(result).ProjectRevision));
        Assert.Equal(2, (await f.History()).UndoCount);
    }

    [Fact]
    public async Task 明确来源编辑保持主来源与FollowPrimary关联()
    {
        using Fixture f = await Fixture.Create();
        Project source = f.Workspace.Project;
        LyricLine line = source.Lyrics.Lines[0] with { Id = "translation-line", PrimaryLineId = "one",
            Tokens = [new LyricToken { Id = "translation-token", Text = "译", Time = TimeRange.Create(100000, 200000), TimingStatus = TimingStatus.Imported }] };
        LyricBranch branch = LyricBranch.Create("translation", "翻译", LyricBranchRole.Translation, [line], timingMode: LyricBranchTimingMode.FollowPrimary);
        f.Workspace.Replace(source with { Lyrics = source.Lyrics with { Branches = source.Lyrics.Branches.Add(branch) } });
        Accepted(await f.Command("0", new NudgeTokenTimeCommandDto(new("translation", line.Id, "translation-token"), 1)));
        Assert.Equal("primary", f.Workspace.Project.Lyrics.PrimaryBranchId);
        Assert.Same(source.Lyrics.PrimaryBranch, f.Workspace.Project.Lyrics.PrimaryBranch);
        LyricBranch updated = f.Workspace.Project.Lyrics.Branches[1];
        Assert.Equal(LyricBranchTimingMode.FollowPrimary, updated.TimingMode);
        Assert.Equal("one", updated.Lines[0].PrimaryLineId);
        Assert.Equal(TimeRange.Create(133333, 233333), updated.Lines[0].Tokens[0].Time);
    }

    [Theory]
    [InlineData("{\"type\":\"lyrics.stampTokenStart\",\"target\":{\"branchId\":\"p\",\"lineId\":\"l\",\"tokenId\":\"t\"},\"playheadUs\":\"1\"}", typeof(StampTokenStartCommandDto))]
    [InlineData("{\"type\":\"lyrics.stampTokenEnd\",\"target\":{},\"playheadUs\":\"2\"}", typeof(StampTokenEndCommandDto))]
    [InlineData("{\"type\":\"lyrics.clearTokenTime\",\"target\":{}}", typeof(ClearTokenTimeCommandDto))]
    [InlineData("{\"type\":\"lyrics.nudgeTokenTime\",\"target\":{},\"frameDelta\":-2}", typeof(NudgeTokenTimeCommandDto))]
    [InlineData("{\"type\":\"lyrics.shiftLine\",\"target\":{},\"deltaUs\":\"-3\"}", typeof(ShiftLineCommandDto))]
    [InlineData("{\"type\":\"lyrics.setTokenTimes\",\"changes\":[{\"target\":null,\"startUs\":\"4\",\"endUs\":\"5\"}]}", typeof(SetTokenTimesCommandDto))]
    public void JSON按type读取并以声明或运行时类型写出时不丢派生字段(string body, Type expectedType)
    {
        ProjectCommandDto original = JsonSerializer.Deserialize<ProjectCommandDto>(body, Json)!;
        Assert.IsType(expectedType, original);
        foreach (string written in new[] { JsonSerializer.Serialize(original, Json), JsonSerializer.Serialize(original, original.GetType(), Json) })
        {
            ProjectCommandDto copy = JsonSerializer.Deserialize<ProjectCommandDto>(written, Json)!;
            Assert.IsType(expectedType, copy);
            Assert.Equal(JsonSerializer.Serialize(original, Json), JsonSerializer.Serialize(copy, Json));
        }
    }

    [Theory]
    [InlineData("{\"type\":\"lyrics.stampTokenStart\",\"analysisJobId\":null}")]
    [InlineData("{\"type\":\"lyrics.clearTokenTime\",\"startUs\":null}")]
    [InlineData("{\"type\":\"lyrics.nudgeTokenTime\",\"frameDelta\":\"1\"}")]
    [InlineData("{\"type\":\"lyrics.shiftLine\",\"deltaUs\":1}")]
    [InlineData("{\"type\":\"lyrics.stampTokenEnd\",\"playheadUs\":true}")]
    [InlineData("{\"type\":\"lyrics.clearTokenTime\",\"type\":\"lyrics.clearTokenTime\"}")]
    [InlineData("{\"type\":\"lyrics.clearTokenTime\",\"extra\":1}")]
    [InlineData("{\"type\":\"lyrics.setTokenTimes\",\"changes\":[{\"extra\":1}]}")]
    [InlineData("{\"type\":\"lyrics.clearTokenTime\",\"target\":{\"extra\":1}}")]
    public void JSON新命令拒绝跨类型字段错型重复及未知嵌套字段(string body) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ProjectCommandDto>(body, Json));

    [Theory]
    [InlineData("lyrics.setTimeRange")]
    [InlineData("history.undo")]
    [InlineData("history.redo")]
    [InlineData("audio.analysis.apply")]
    [InlineData("audio.analysis.clear")]
    [InlineData("unknown.command")]
    public void 旧五命令和未知type保留已知显式空字段读取兼容(string type)
    {
        string body = $$"""{"type":"{{type}}","target":null,"startUs":null,"endUs":null,"analysisJobId":null}""";
        ProjectCommandDto command = JsonSerializer.Deserialize<ProjectCommandDto>(body, Json)!;
        Assert.IsType<ProjectCommandDto>(command); Assert.Equal(type, command.Type);
        Assert.Equal(command, JsonSerializer.Deserialize<ProjectCommandDto>(JsonSerializer.Serialize(command, Json), Json));
    }

    [Fact]
    public void JSON直接反序列化到错误派生类型返回JsonException() => Assert.Throws<JsonException>(() =>
        JsonSerializer.Deserialize<StampTokenStartCommandDto>("{\"type\":\"history.undo\"}", Json));

    private static SetTokenTimesCommandDto Batch(LyricTimingTargetDto target, string start, string end) => new([new(target, start, end)]);
    private static ProjectCommandResponse Accepted(ProjectCommandExecutionResult result)
    {
        Assert.True(result.Accepted, $"{result.Error?.Code}: {result.Error?.Field}: {result.Error?.Message}");
        return result.Response!;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TemporaryDirectory _directory = new();
        internal ProjectSessionRegistry Registry = null!;
        internal ProjectSession Session = null!;
        internal ProjectWorkspace Workspace = null!;
        private static readonly DateTimeOffset Now = new(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);
        internal static async Task<Fixture> Create(long lineEndUs = 2000000)
        {
            Fixture f = new();
            ResourceStore store = new(new() { ResourceRoot = f._directory.Path });
            StoredAsset lyrics = await store.UploadAsync("client", "song.lrc", "text/plain", new MemoryStream("[00:00.00]甲乙丙\n"u8.ToArray()));
            StoredAsset audio = await store.UploadAsync("client", "song.wav", "audio/wav", new MemoryStream([1, 2, 3]));
            WaveformPeakBuilder builder = new(); builder.Append(new float[24000]);
            LoadedWaveform waveform = new(new AudioStreamInfo(3000000, 8000, 1, "pcm", "wav"), builder.Build(8000));
            f.Registry = new("host", store, new((_, _) => Task.FromResult<LoadedWaveform?>(waveform)), new FixedClock());
            f.Session = (await f.Registry.CreateAsync("client", new("editing", audio.AssetId, lyrics.AssetId))).Session!;
            f.Workspace = (ProjectWorkspace)typeof(ProjectSession).GetProperty("Workspace", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(f.Session)!;
            LyricLine line = LyricLine.Create("one", "甲乙丙", TimeRange.Create(0, lineEndUs), TimingStatus.Imported,
                [new LyricToken { Id = "first", Text = "甲", Time = TimeRange.Create(100000, 200000), TimingStatus = TimingStatus.Imported },
                 new LyricToken { Id = "second", Text = "乙", Time = TimeRange.Create(500000, 700000), TimingStatus = TimingStatus.Imported },
                 new LyricToken { Id = "third", Text = "丙" }]);
            f.Workspace.Replace(f.Workspace.Project with { Lyrics = LyricsDocument.Create([line]) });
            return f;
        }
        internal LyricToken Token(int index) => Workspace.Project.Lyrics.Lines[0].Tokens[index];
        internal async Task<LyricLineDto> Line() => Assert.Single((await Registry.ReadLyricsAsync("client", Session.ProjectSessionId, "primary"))!.Lines);
        internal async Task<ProjectHistoryResponse> History() => (await Registry.ReadHistoryAsync("client", Session.ProjectSessionId))!;
        internal ProjectCommandRequest Request(string revision, ProjectCommandDto command) => new(revision, command, Guid.NewGuid().ToString("N"), Session.HostInstanceId, Now.AddMinutes(5));
        internal Task<ProjectCommandExecutionResult> Command(string revision, ProjectCommandDto command) => Execute(Request(revision, command));
        internal Task<ProjectCommandExecutionResult> Execute(ProjectCommandRequest request) => Registry.ExecuteCommandAsync("client", Session.ProjectSessionId, request);
        public void Dispose() { Registry.Dispose(); _directory.Dispose(); }
        private sealed class FixedClock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    }
}
