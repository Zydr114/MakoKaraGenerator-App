using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class BackendContentCommandsTests
{
    private static readonly LyricTimingTargetDto Line = new("primary", "one");
    private static readonly LyricTimingTargetDto Token = new("primary", "one", "first");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };

    [Fact]
    public async Task 正文Unicode重切保留未改元素时间身份且每个HTTP请求独立历史()
    {
        using Fixture f = await Fixture.Create();
        Project original = f.Workspace.Project;
        LyricLine source = original.Lyrics.Lines[0];
        ProjectCommandResponse first = Accept(await f.Command("0", new SetLineTextCommandDto(Line, "甲新乙")));
        Assert.Equal("lyrics.set-line-text", first.UndoName);
        Assert.Equal(2, first.Feedback!.PreservedTokenCount); Assert.Equal(1, first.Feedback.ResetTokenCount);
        Assert.Null(first.Feedback.Selection); Assert.True(first.ChangeSet.RequiresFullRefresh);
        Assert.Equal(["lyricsContent", "lyricsStructure", "timeline", "renderInputs"], first.ChangeSet.Areas);
        Assert.Equal(["primary"], first.ChangeSet.BranchIds); Assert.Equal(["one"], first.ChangeSet.LineIds);
        Assert.Empty(first.ChangeSet.TokenIds);
        LyricLine changed = f.Workspace.Project.Lyrics.Lines[0];
        Assert.Same(source.Tokens[0], changed.Tokens[0]); Assert.Same(source.Tokens[1], changed.Tokens[2]);
        Assert.Null(changed.Tokens[1].Time); Assert.Equal(TimingStatus.Missing, changed.Tokens[1].TimingStatus);
        Assert.Equal(source.RubyAnnotations, changed.RubyAnnotations);
        Accept(await f.Command("1", new SetLineTextCommandDto(Line, "甲新👨‍👩‍👧‍👦乙")));
        Assert.Equal(2, (await f.History()).UndoCount);
        Accept(await f.Command("2", new("history.undo")));
        Assert.Same(changed, f.Workspace.Project.Lyrics.Lines[0]);
        ProjectHistoryResponse history = await f.History();
        ProjectCommandResponse noOp = Accept(await f.Command("3", new SetLineTextCommandDto(Line, "甲新乙")));
        Assert.False(noOp.Changed); Assert.Equal(0, noOp.Feedback!.PreservedTokenCount); Assert.Equal(0, noOp.Feedback.ResetTokenCount);
        Assert.Equal(history, await f.History());
        Accept(await f.Command("3", new("history.undo"))); Assert.Same(original, f.Workspace.Project);
        Accept(await f.Command("4", new("history.redo"))); Assert.Equal("甲新乙", f.Workspace.Project.Lyrics.Lines[0].Text);
    }

    [Fact]
    public async Task 注音文字保留原属性并允许删除且正文清空同步清理关联()
    {
        using Fixture f = await Fixture.Create();
        RubyAnnotation ruby = f.Workspace.Project.Lyrics.Lines[0].RubyAnnotations[0];
        ProjectCommandResponse response = Accept(await f.Command("0", new SetRubyTextCommandDto(Token, "新音")));
        Assert.Equal("line.ruby", response.UndoName); Assert.Null(response.Feedback!.Selection);
        Assert.Equal(ruby with { Text = "新音" }, f.Workspace.Project.Lyrics.Lines[0].RubyAnnotations[0]);
        Accept(await f.Command("1", new SetRubyTextCommandDto(Token, "")));
        Assert.Empty(f.Workspace.Project.Lyrics.Lines[0].RubyAnnotations);
        Accept(await f.Command("2", new("history.undo")));
        Accept(await f.Command("3", new SetLineTextCommandDto(Line, "")));
        LyricLine empty = f.Workspace.Project.Lyrics.Lines[0];
        Assert.Empty(empty.Text); Assert.Empty(empty.Tokens); Assert.Empty(empty.RubyAnnotations);
        ProjectLyricsWindowResponse window = (await f.Registry.ReadLyricsWindowAsync("client", f.Session.ProjectSessionId, "primary", "4", limit: 1)).Response!;
        Assert.Empty(window.Lines[0].RubyAnnotations);
        Accept(await f.Command("4", new("history.undo"))); Assert.Equal("新音", Assert.Single(f.Workspace.Project.Lyrics.Lines[0].RubyAnnotations).Text);
    }

    [Fact]
    public async Task 新增复制删除返回小反馈准确选择新ID且复制不套音频上界()
    {
        using Fixture f = await Fixture.Create();
        Project original = f.Workspace.Project;
        ProjectCommandResponse copy = Accept(await f.Command("0", new DuplicateLineCommandDto(Line)));
        string copyId = copy.Feedback!.CreatedLineId!;
        Assert.Equal(1, copy.Feedback.InsertedIndex); Assert.True(copy.Feedback.OverlapsNext);
        Assert.Equal(new LyricContentSelectionDto("primary", true, copyId), copy.Feedback.Selection);
        LyricLine duplicate = f.Workspace.Project.Lyrics.Lines[1];
        Assert.Equal(TimeRange.Create(2000000, 4000000), duplicate.Time);
        Assert.Equal(3000000, f.Session.AudioDurationUs);
        Assert.NotEqual("first", duplicate.Tokens[0].Id);
        Assert.Equal(duplicate.Tokens[0].Id, duplicate.RubyAnnotations[0].BaseTokenId);
        Assert.Equal(original.Lyrics.Lines[0].RubyAnnotations[0].RelativeScale, duplicate.RubyAnnotations[0].RelativeScale);
        ProjectCommandResponse added = Accept(await f.Command("1", new AddLineCommandDto("primary", copyId)));
        Assert.Equal(2, added.Feedback!.InsertedIndex);
        string addedId = added.Feedback.CreatedLineId!;
        Assert.Equal(new LyricContentSelectionDto("primary", true, addedId), added.Feedback.Selection);
        LyricLine empty = f.Workspace.Project.Lyrics.Lines[2]; Assert.Empty(empty.Text); Assert.Null(empty.Time); Assert.Empty(empty.Tokens);
        ProjectCommandResponse deleted = Accept(await f.Command("2", new RemoveLineCommandDto(new("primary", addedId))));
        Assert.Equal(addedId, deleted.Feedback!.DeletedLineId); Assert.Equal("two", deleted.Feedback.Selection!.LineId);
        Assert.True(deleted.Feedback.Selection.HasSelection);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(deleted, Json).Length < 4096);
        Accept(await f.Command("3", new("history.undo"))); Assert.Equal(addedId, f.Workspace.Project.Lyrics.Lines[2].Id);
        Accept(await f.Command("4", new("history.redo"))); Assert.DoesNotContain(f.Workspace.Project.Lyrics.Lines, row => row.Id == addedId);
    }

    [Fact]
    public async Task 删除最后行显式清空选择且空来源可追加()
    {
        using Fixture f = await Fixture.Create();
        Accept(await f.Command("0", new RemoveLineCommandDto(new("primary", "two"))));
        ProjectCommandResponse last = Accept(await f.Command("1", new RemoveLineCommandDto(Line)));
        Assert.Equal(new LyricContentSelectionDto("primary", false), last.Feedback!.Selection);
        string wire = JsonSerializer.Serialize(last, Json); Assert.DoesNotContain("\"lineId\"", wire, StringComparison.Ordinal);
        ProjectCommandResponse appended = Accept(await f.Command("2", new AddLineCommandDto("primary")));
        Assert.Equal(0, appended.Feedback!.InsertedIndex); Assert.True(appended.Feedback.Selection!.HasSelection);
    }

    [Fact]
    public async Task 六个标量命令精确提交撤销重做且设置与正文可保存重开()
    {
        using Fixture f = await Fixture.Create();
        ProjectCommandDto[] inputs = [new SetCanvasSizeCommandDto(641, 361), new SetFrameRateCommandDto(60000, 2002),
            new SetBackgroundColorCommandDto(1, 2, 3, 4), new SetOutputDurationCommandDto("4000007"),
            new UseLyricsEndCommandDto(), new UseAudioDurationCommandDto()];
        string[] names = ["composition.canvas", "composition.frame-rate", "composition.background", "composition.duration", "composition.duration", "composition.duration"];
        for (int index = 0; index < inputs.Length; index++)
        {
            ProjectCommandResponse result = Accept(await f.Command(index.ToString(CultureInfo.InvariantCulture), inputs[index]));
            Assert.True(result.Changed); Assert.Equal(names[index], result.UndoName); Assert.Null(result.Feedback);
            Assert.True(result.ChangeSet.RequiresFullRefresh); Assert.Contains("composition", result.ChangeSet.Areas);
        }
        Composition settings = f.Workspace.Project.Composition;
        Assert.Equal(641, settings.CanvasWidth); Assert.Equal(361, settings.CanvasHeight);
        Assert.Equal(new FrameRate(30000, 1001), settings.FrameRate); Assert.Equal(new RgbaColor(1, 2, 3, 4), settings.BackgroundColor);
        Assert.Equal(3000000, settings.OutputDurationUs);
        Accept(await f.Command("6", new("history.undo"))); Assert.Equal(2500000, f.Workspace.Project.Composition.OutputDurationUs);
        Accept(await f.Command("7", new("history.redo"))); Assert.Equal(settings, f.Workspace.Project.Composition);
        Accept(await f.Command("8", new SetLineTextCommandDto(Line, "甲新乙")));
        string path = Path.Combine(f.DirectoryPath, "saved.mkgproj");
        f.Workspace.SaveAs(path);
        ProjectWorkspace reopened = ProjectWorkspace.CreateNew("other");
        Assert.True(reopened.Open(path).Loaded);
        Assert.Equal(settings, reopened.Project.Composition);
        Assert.Equal("甲新乙", reopened.Project.Lyrics.Lines[0].Text);
        Assert.Equal(f.Workspace.Project.Lyrics.Lines[0].Tokens.Select(token => token.Id), reopened.Project.Lyrics.Lines[0].Tokens.Select(token => token.Id));
    }

    [Fact]
    public async Task 时长显式空值及同值设置noop不损失重做且无歌词末点不猜时长()
    {
        using Fixture f = await Fixture.Create();
        Accept(await f.Command("0", new SetOutputDurationCommandDto("4000007")));
        Accept(await f.Command("1", new SetOutputDurationCommandDto(null)));
        Assert.Null(f.Workspace.Project.Composition.OutputDurationUs);
        Accept(await f.Command("2", new SetCanvasSizeCommandDto(123, 345)));
        Accept(await f.Command("3", new("history.undo")));
        ProjectHistoryResponse history = await f.History();
        Assert.False(Accept(await f.Command("4", new SetOutputDurationCommandDto(null))).Changed);
        Assert.Equal(history, await f.History());
        Project current = f.Workspace.Project;
        f.Workspace.Replace(current with { Lyrics = LyricsDocument.Create([LyricLine.Create("one", "未定时")]) });
        Assert.False(Accept(await f.Command("4", new UseLyricsEndCommandDto())).Changed);
        Assert.Null(f.Workspace.Project.Composition.OutputDurationUs);
    }

    [Fact]
    public async Task 单行最终投影预算与原文字段必要条件均在提交前413且保历史重做()
    {
        using Fixture f = await Fixture.Create();
        Accept(await f.Command("0", new SetCanvasSizeCommandDto(10, 20)));
        Accept(await f.Command("1", new("history.undo")));
        Project before = f.Workspace.Project; ProjectHistoryResponse history = await f.History(); bool dirty = f.Workspace.IsDirty;
        ProjectCommandDto[] oversized = [new SetLineTextCommandDto(Line, new string('字', 20001)),
            new SetLineTextCommandDto(Line, new string('x', ProjectReadModelLimits.MaxResponseBytes + 1)),
            new SetRubyTextCommandDto(Token, new string('x', ProjectReadModelLimits.MaxResponseBytes + 1)),
            new SetRubyTextCommandDto(Token, new string('字', 200000)), new SetLineTextCommandDto(Line, new string('x', 19000))];
        foreach (ProjectCommandDto input in oversized)
        {
            Assert.Equal(BackendErrorCode.ResourceTooLarge, (await f.Command("2", input)).Error!.Code);
            Assert.Same(before, f.Workspace.Project); Assert.Equal(history, await f.History()); Assert.Equal(dirty, f.Workspace.IsDirty);
        }
    }

    [Fact]
    public async Task 大合并元素追加不按grapheme个数误拒且不能复制超预算旧行但可删除修复()
    {
        using Fixture f = await Fixture.Create();
        string text = new('字', 30000);
        LyricLine large = LyricLine.Create("one", text, tokens: [LyricToken.Create("large", text)]);
        f.Workspace.Replace(f.Workspace.Project with { Lyrics = LyricsDocument.Create([large]) });
        ProjectCommandResponse accepted = Accept(await f.Command("0", new SetLineTextCommandDto(Line, text + "新")));
        Assert.Equal(1, accepted.Feedback!.PreservedTokenCount); Assert.Equal(1, accepted.Feedback.ResetTokenCount);
        Assert.True((await f.Registry.ReadLyricsWindowAsync("client", f.Session.ProjectSessionId, "primary", "1", limit: 1)).Accepted);
        LyricLine tooLarge = LyricLine.Create("one", "oversized", tokens: Enumerable.Range(0, 25000).Select(index =>
            LyricToken.Create("huge-" + index.ToString(CultureInfo.InvariantCulture), "字")));
        f.Workspace.Replace(f.Workspace.Project with { Lyrics = LyricsDocument.Create([tooLarge]) });
        Assert.Equal(BackendErrorCode.ResourceTooLarge, (await f.Command("1", new DuplicateLineCommandDto(Line))).Error!.Code);
        ProjectCommandResponse deleted = Accept(await f.Command("1", new RemoveLineCommandDto(Line)));
        Assert.False(deleted.Feedback!.Selection!.HasSelection); Assert.Empty(f.Workspace.Project.Lyrics.Lines);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(deleted, Json).Length < 4096);
    }

    [Fact]
    public async Task 九到十修订位数增长按成功后完整响应预算拒绝临界候选()
    {
        using Fixture f = await Fixture.Create();
        for (int revision = 0; revision < 9; revision++)
            Accept(await f.Command(revision.ToString(CultureInfo.InvariantCulture), new SetCanvasSizeCommandDto(100 + revision, 100)));
        ProjectLyricsWindowResponse window = (await f.Registry.ReadLyricsWindowAsync("client", f.Session.ProjectSessionId, "primary", "9", limit: 1)).Response!;
        ProjectLyricLineDto line = window.Lines[0];
        ProjectRubyAnnotationDto ruby = line.RubyAnnotations[0];
        ProjectLyricsWindowResponse emptyRuby = window with { Lines = [line with { RubyAnnotations = [ruby with { Text = "" }] }] };
        int padding = ProjectReadModelLimits.MaxResponseBytes - JsonSerializer.SerializeToUtf8Bytes(emptyRuby, Json).Length;
        string text = new('x', padding);
        ProjectLyricsWindowResponse atNine = emptyRuby with { Lines = [line with { RubyAnnotations = [ruby with { Text = text }] }] };
        Assert.Equal(ProjectReadModelLimits.MaxResponseBytes, JsonSerializer.SerializeToUtf8Bytes(atNine, Json).Length);
        Assert.Equal(ProjectReadModelLimits.MaxResponseBytes + 1, JsonSerializer.SerializeToUtf8Bytes(atNine with { ProjectRevision = "10" }, Json).Length);
        Project source = f.Workspace.Project; ProjectHistoryResponse history = await f.History();
        Assert.Equal(BackendErrorCode.ResourceTooLarge, (await f.Command("9", new SetRubyTextCommandDto(Token, text))).Error!.Code);
        Assert.Same(source, f.Workspace.Project); Assert.Equal(history, await f.History());
    }

    [Fact]
    public async Task 非主来源正文编辑保持主分支跟随策略和关联()
    {
        using Fixture f = await Fixture.Create();
        Project original = f.Workspace.Project;
        LyricLine translated = LyricLine.Create("translated", "译") with { PrimaryLineId = "one" };
        LyricBranch translation = LyricBranch.Create("translation", "翻译", LyricBranchRole.Translation, [translated], timingMode: LyricBranchTimingMode.FollowPrimary);
        f.Workspace.Replace(original with { Lyrics = original.Lyrics with { Branches = original.Lyrics.Branches.Add(translation) } });
        Accept(await f.Command("0", new SetLineTextCommandDto(new("translation", "translated"), "译文")));
        Assert.Same(original.Lyrics.PrimaryBranch, f.Workspace.Project.Lyrics.PrimaryBranch);
        Assert.Equal("primary", f.Workspace.Project.Lyrics.PrimaryBranchId);
        Assert.Equal(LyricBranchTimingMode.FollowPrimary, f.Workspace.Project.Lyrics.Branches[1].TimingMode);
        Assert.Equal("one", f.Workspace.Project.Lyrics.Branches[1].Lines[0].PrimaryLineId);
    }

    [Fact]
    public async Task 字段缺失目标失效和非法设置均明确拒绝且不改变历史()
    {
        using Fixture f = await Fixture.Create();
        var inputs = new (ProjectCommandDto Command, BackendErrorCode Code, string? Failure)[]
        {
            (new SetLineTextCommandDto(Line, null), BackendErrorCode.InvalidRequest, null),
            (new SetRubyTextCommandDto(Line, "音"), BackendErrorCode.InvalidRequest, null),
            (new SetLineTextCommandDto(Line with { LineId = "missing" }, "x"), BackendErrorCode.CommandRejected, "TargetStale"),
            (new SetCanvasSizeCommandDto(0, 100), BackendErrorCode.CommandRejected, "InvalidCanvasSize"),
            (new SetFrameRateCommandDto(-1, 1001), BackendErrorCode.CommandRejected, "InvalidFrameRate"),
            (new SetOutputDurationCommandDto("0"), BackendErrorCode.CommandRejected, "InvalidOutputDuration"),
            (new SetOutputDurationCommandDto("1.5"), BackendErrorCode.InvalidRequest, null),
            (new SetBackgroundColorCommandDto(1, 2, 3, null), BackendErrorCode.InvalidRequest, null),
            (new AddLineCommandDto("primary", "missing"), BackendErrorCode.CommandRejected, "TargetStale"),
            (new AddLineCommandDto("missing", "one"), BackendErrorCode.CommandRejected, "TargetStale"),
            (new DuplicateLineCommandDto(Token), BackendErrorCode.InvalidRequest, null),
            (new SetLineTextCommandDto(Line, "x") with { Type = "history.undo" }, BackendErrorCode.InvalidRequest, null),
        };
        Project original = f.Workspace.Project;
        foreach ((ProjectCommandDto command, BackendErrorCode code, string? failure) in inputs)
        {
            BackendErrorResponse error = (await f.Command("0", command)).Error!;
            Assert.Equal(code, error.Code); Assert.Equal(failure, error.FailureCode);
            if (failure is not null) Assert.StartsWith("command", error.Field);
        }
        Assert.Same(original, f.Workspace.Project); Assert.Equal(0, (await f.History()).UndoCount);
    }

    [Fact]
    public async Task 新正文及新增行ID耗尽拒绝不丢原工程与历史()
    {
        using Fixture f = await Fixture.Create();
        Project source = f.Workspace.Project;
        LyricLine exhausted = source.Lyrics.Lines[1] with { Id = "line-2147483647",
            Tokens = [LyricToken.Create("token-2147483647", "丙")] };
        f.Workspace.Replace(source with { Lyrics = source.Lyrics with { Lines = source.Lyrics.Lines.SetItem(1, exhausted) } });
        Project before = f.Workspace.Project;
        foreach (ProjectCommandDto command in new ProjectCommandDto[] { new SetLineTextCommandDto(Line, "甲新乙"), new AddLineCommandDto("primary") })
        {
            BackendErrorResponse error = (await f.Command("0", command)).Error!;
            Assert.Equal(BackendErrorCode.CommandRejected, error.Code); Assert.Equal("IdentifierExhausted", error.FailureCode);
            Assert.Same(before, f.Workspace.Project); Assert.Equal(0, (await f.History()).UndoCount);
        }
    }

    [Fact]
    public async Task 新反馈完整保存回执且正文设置字段都进入语义指纹()
    {
        using Fixture f = await Fixture.Create();
        ProjectCommandRequest add = f.Request("0", new AddLineCommandDto("primary", "one"));
        ProjectCommandResponse original = Accept(await f.Execute(add));
        Assert.Equal(JsonSerializer.Serialize(original, Json), JsonSerializer.Serialize(Accept(await f.Execute(add)), Json));
        Assert.Equal(BackendErrorCode.RequestIdReuse, (await f.Execute(add with { Command = new AddLineCommandDto("primary", "two") })).Error!.Code);
        ProjectCommandRequest text = f.Request("1", new SetLineTextCommandDto(Line, "甲新乙"));
        Accept(await f.Execute(text));
        Assert.Equal(BackendErrorCode.RequestIdReuse, (await f.Execute(text with { Command = new SetLineTextCommandDto(Line, "甲另乙") })).Error!.Code);
        ProjectCommandRequest rate = f.Request("2", new SetFrameRateCommandDto(50, 2)); Accept(await f.Execute(rate));
        Assert.True((await f.Execute(rate with { Command = new SetFrameRateCommandDto(25, 1) })).Accepted);
        Assert.Equal(BackendErrorCode.RequestIdReuse, (await f.Execute(rate with { Command = new SetFrameRateCommandDto(24, 1) })).Error!.Code);
        ProjectCommandRequest duration = f.Request("3", new SetOutputDurationCommandDto("4000007")); Accept(await f.Execute(duration));
        Assert.True((await f.Execute(duration with { Command = new SetOutputDurationCommandDto("+04000007") })).Accepted);
        Assert.Equal(BackendErrorCode.RequestIdReuse, (await f.Execute(duration with { Command = new SetOutputDurationCommandDto(null) })).Error!.Code);
    }

    [Fact]
    public async Task 并发相同修订内容操作只有一个提交且相同意图重试不再分配ID()
    {
        using Fixture f = await Fixture.Create();
        ProjectCommandExecutionResult[] results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => f.Command("0", new AddLineCommandDto("primary"))));
        Assert.Single(results, result => result.Accepted); Assert.Equal(7, results.Count(result => result.Error?.Code == BackendErrorCode.RevisionConflict));
        ProjectCommandRequest same = f.Request("1", new DuplicateLineCommandDto(Line));
        results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => f.Execute(same)));
        string id = Accept(results[0]).Feedback!.CreatedLineId!;
        Assert.All(results, result => Assert.Equal(id, Accept(result).Feedback!.CreatedLineId));
        Assert.Equal(2, (await f.History()).UndoCount);
    }

    [Theory]
    [InlineData("{\"type\":\"composition.setOutputDuration\"}")]
    [InlineData("{\"type\":\"composition.setFrameRate\",\"numerator\":\"30\",\"denominator\":1}")]
    [InlineData("{\"type\":\"composition.setBackgroundColor\",\"red\":256,\"green\":2,\"blue\":3,\"alpha\":4}")]
    [InlineData("{\"type\":\"lyrics.setLineText\",\"text\":1}")]
    [InlineData("{\"type\":\"lyrics.addLine\",\"branchId\":\"primary\",\"target\":null}")]
    [InlineData("{\"type\":\"composition.useLyricsEnd\",\"durationUs\":null}")]
    [InlineData("{\"type\":\"lyrics.setRubyText\",\"type\":\"lyrics.setRubyText\"}")]
    public void 新JSON命令拒绝缺失可空必填字段错型与跨type字段(string body) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ProjectCommandDto>(body, Json));

    [Fact]
    public void 时长显式null在忽略普通null的序列化选项下仍写出且所有派生字段往返()
    {
        ProjectCommandDto[] inputs = [new SetLineTextCommandDto(Line, "x"), new SetRubyTextCommandDto(Token, "音"), new AddLineCommandDto("primary"),
            new DuplicateLineCommandDto(Line), new RemoveLineCommandDto(Line), new SetCanvasSizeCommandDto(641, 361), new SetFrameRateCommandDto(30000, 1001),
            new SetBackgroundColorCommandDto(1, 2, 3, 4), new SetOutputDurationCommandDto(null), new UseLyricsEndCommandDto(), new UseAudioDurationCommandDto()];
        foreach (ProjectCommandDto input in inputs)
        {
            string body = JsonSerializer.Serialize(input, Json);
            Assert.Equal(input, JsonSerializer.Deserialize<ProjectCommandDto>(body, Json));
            Assert.Equal(body, JsonSerializer.Serialize(input, input.GetType(), Json));
        }
        Assert.Contains("\"durationUs\":null", JsonSerializer.Serialize<ProjectCommandDto>(new SetOutputDurationCommandDto(null), Json), StringComparison.Ordinal);
    }

    private static ProjectCommandResponse Accept(ProjectCommandExecutionResult result)
    { Assert.True(result.Accepted, $"{result.Error?.Code} {result.Error?.FailureCode}: {result.Error?.Message}"); return result.Response!; }

    private sealed class Fixture : IDisposable
    {
        private readonly TemporaryDirectory _directory = new();
        internal string DirectoryPath => _directory.Path;
        internal ProjectSessionRegistry Registry = null!; internal ProjectSession Session = null!; internal ProjectWorkspace Workspace = null!;
        private static readonly DateTimeOffset Now = new(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);
        internal static async Task<Fixture> Create()
        {
            Fixture f = new(); ResourceStore store = new(new() { ResourceRoot = Path.Combine(f._directory.Path, "resources") });
            StoredAsset lyrics = await store.UploadAsync("client", "song.lrc", "text/plain", new MemoryStream("[00:00.00]甲乙\n"u8.ToArray()));
            StoredAsset audio = await store.UploadAsync("client", "song.wav", "audio/wav", new MemoryStream([1, 2, 3]));
            WaveformPeakBuilder builder = new(); builder.Append(new float[24000]);
            LoadedWaveform waveform = new(new AudioStreamInfo(3000000, 8000, 1, "pcm", "wav"), builder.Build(8000));
            f.Registry = new("host", store, new((_, _) => Task.FromResult<LoadedWaveform?>(waveform)), new FixedClock());
            f.Session = (await f.Registry.CreateAsync("client", new("content", audio.AssetId, lyrics.AssetId))).Session!;
            f.Workspace = f.Session.Workspace;
            LyricLine line = LyricLine.Create("one", "甲乙", TimeRange.Create(0, 2000000), TimingStatus.Imported,
                [LyricToken.Create("first", "甲", TimeRange.Create(100000, 200000), TimingStatus.Imported),
                 LyricToken.Create("second", "乙", TimeRange.Create(500000, 700000), TimingStatus.Imported)]) with
                { RubyAnnotations = [RubyAnnotation.Create("ruby", "first", "こう", "ja", .7, 3, RubyAlignment.Start)] };
            f.Workspace.Replace(f.Workspace.Project with { Lyrics = LyricsDocument.Create([line,
                LyricLine.Create("two", "丙", TimeRange.Create(2000000, 2500000), TimingStatus.Imported)]) });
            return f;
        }
        internal async Task<ProjectHistoryResponse> History() => (await Registry.ReadHistoryAsync("client", Session.ProjectSessionId))!;
        internal ProjectCommandRequest Request(string revision, ProjectCommandDto command) => new(revision, command, Guid.NewGuid().ToString("N"), Session.HostInstanceId, Now.AddMinutes(5));
        internal Task<ProjectCommandExecutionResult> Command(string revision, ProjectCommandDto command) => Execute(Request(revision, command));
        internal Task<ProjectCommandExecutionResult> Execute(ProjectCommandRequest request) => Registry.ExecuteCommandAsync("client", Session.ProjectSessionId, request);
        public void Dispose() { Registry.Dispose(); _directory.Dispose(); }
        private sealed class FixedClock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    }
}
