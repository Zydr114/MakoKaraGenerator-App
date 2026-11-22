using System.Collections.Immutable;
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

public sealed class BackendTokenCommandsTests
{
    private static readonly LyricTimingTargetDto Line = new("primary", "one");
    private static readonly LyricTimingTargetDto Token = new("primary", "one", "first");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };


    [Fact]
    public async Task 五命令共享正文时间ID选择与单次历史且可保存重开()
    {
        using Fixture f = await Fixture.Create();
        Project source = f.Workspace.Project;
        ProjectCommandResponse merge = Accept(await f.Command("0", new MergeTokenCommandDto(Token)));
        Assert.Equal("line.merge", merge.UndoName); Assert.Equal(new LyricTokenSelectionDto("primary", "one", true, "first"), merge.TokenResult!.Selection);
        Assert.Null(merge.TokenResult.CreatedTokenCount); Assert.Null(merge.Feedback);
        LyricToken joined = Assert.Single(f.Workspace.Project.Lyrics.Lines[0].Tokens);
        Assert.Equal("甲乙", joined.Text); Assert.Equal(TimeRange.Create(100000, 700000), joined.Time);
        ProjectCommandResponse split = Accept(await f.Command("1", new SplitTokenCommandDto(Token)));
        Assert.Equal("line.split", split.UndoName); Assert.Equal(1, split.TokenResult!.PreservedTokenCount);
        Assert.Equal(1, split.TokenResult.CreatedTokenCount); Assert.Equal(2, split.TokenResult.EstimatedTokenCount);
        string right = split.TokenResult.Selection!.TokenId!; Assert.NotEqual("first", right);
        Assert.Equal(new LyricTokenSelectionDto("primary", "one", true, right), split.TokenResult.Selection);
        Assert.Equal(TimeRange.Create(100000, 400000), f.Workspace.Project.Lyrics.Lines[0].Tokens[0].Time);
        ProjectCommandResponse reorder = Accept(await f.Command("2", new ReorderTokenCommandDto(Token, 1, true)));
        Assert.Equal("line.reorder", reorder.UndoName); Assert.Null(reorder.TokenResult!.Selection); Assert.Null(reorder.TokenResult.CreatedTokenCount);
        Assert.Equal("乙甲", f.Workspace.Project.Lyrics.Lines[0].Text);
        Assert.All(f.Workspace.Project.Lyrics.Lines[0].Tokens, value => Assert.Equal(TimingStatus.Edited, value.TimingStatus));
        ProjectCommandResponse manual = Accept(await f.Command("3", new ResplitLineCommandDto(Line, ["乙甲"], false)));
        Assert.Equal("line.split-rule", manual.UndoName); Assert.Equal(new LyricTokenSelectionDto("primary", "one", false), manual.TokenResult!.Selection);
        ProjectCommandResponse branch = Accept(await f.Command("4", new ResplitBranchCommandDto("primary", ProjectTokenSplitRule.PerTextElement, true)));
        Assert.Equal("lyrics.split-rule", branch.UndoName); Assert.Null(branch.TokenResult!.Selection); Assert.Equal(1, branch.TokenResult.ChangedLineCount);
        Assert.Equal(["lyricsContent", "lyricsStructure", "timeline", "renderInputs"], branch.ChangeSet.Areas);
        Assert.Equal(["primary"], branch.ChangeSet.BranchIds); Assert.Empty(branch.ChangeSet.LineIds); Assert.Empty(branch.ChangeSet.TokenIds);
        Assert.True(branch.ChangeSet.RequiresFullRefresh); Assert.True(JsonSerializer.SerializeToUtf8Bytes(branch, Json).Length < 4096);
        Assert.All(f.Workspace.Project.Lyrics.Lines[0].RubyAnnotations, ruby =>
            Assert.Contains(f.Workspace.Project.Lyrics.Lines[0].Tokens, token => token.Id == ruby.BaseTokenId));
        Assert.Equal(5, (await f.History()).UndoCount);
        Project final = f.Workspace.Project;
        string path = Path.Combine(f.DirectoryPath, "tokens.mkgproj"); f.Workspace.SaveAs(path);
        ProjectWorkspace reopened = ProjectWorkspace.CreateNew("reopened"); Assert.True(reopened.Open(path).Loaded);
        Assert.Equal(final.Lyrics.Lines[0].Text, reopened.Project.Lyrics.Lines[0].Text);
        Assert.Equal(final.Lyrics.Lines[0].Tokens.Select(value => value.Id), reopened.Project.Lyrics.Lines[0].Tokens.Select(value => value.Id));
        for (int revision = 5; revision < 10; revision++) Accept(await f.Command(revision.ToString(CultureInfo.InvariantCulture), new("history.undo")));
        Assert.Same(source, f.Workspace.Project);
        for (int revision = 10; revision < 15; revision++) Accept(await f.Command(revision.ToString(CultureInfo.InvariantCulture), new("history.redo")));
        Assert.Same(final, f.Workspace.Project);
    }

    [Fact]
    public async Task 五类noop计数选择与重做保持且无HTTP跨请求合并()
    {
        using Fixture f = await Fixture.Create();
        Accept(await f.Command("0", new MergeTokenCommandDto(Token)));
        Accept(await f.Command("1", new("history.undo")));
        ProjectHistoryResponse history = await f.History();
        ProjectCommandDto[] inputs = [new SplitTokenCommandDto(Token), new MergeTokenCommandDto(new("primary", "one", "second")),
            new ReorderTokenCommandDto(Token, 0, false), new ResplitLineCommandDto(Line, ["甲", "乙"], false),
            new ResplitBranchCommandDto("primary", ProjectTokenSplitRule.None, true)];
        foreach (ProjectCommandDto input in inputs)
        {
            ProjectCommandResponse response = Accept(await f.Command("2", input)); Assert.False(response.Changed);
            Assert.Equal(0, response.TokenResult!.ChangedLineCount); Assert.Null(response.TokenResult.Selection);
            Assert.False(response.ChangeSet.RequiresFullRefresh); Assert.Empty(response.ChangeSet.Areas);
            if (input is SplitTokenCommandDto or ResplitLineCommandDto or ResplitBranchCommandDto)
            { Assert.Equal(0, response.TokenResult.PreservedTokenCount); Assert.Equal(0, response.TokenResult.CreatedTokenCount); Assert.Equal(0, response.TokenResult.EstimatedTokenCount); }
            Assert.Equal(history, await f.History());
        }
        Accept(await f.Command("2", new MergeTokenCommandDto(Token))); Accept(await f.Command("3", new SplitTokenCommandDto(Token)));
        Assert.Equal(2, (await f.History()).UndoCount);
    }

    [Fact]
    public async Task 手工段精确拼接Unicode及原时间冲突拒绝原因字段稳定且原子()
    {
        using Fixture f = await Fixture.Create();
        Project source = f.Workspace.Project;
        foreach (ProjectCommandDto input in new ProjectCommandDto[] { new ResplitLineCommandDto(Line, ["甲", "不等"], false),
            new ReorderTokenCommandDto(Token, 1, false), new ReorderTokenCommandDto(Token, -1, true), new SplitTokenCommandDto(new("primary", "one", "gone")) })
        {
            BackendErrorResponse error = (await f.Command("0", input)).Error!;
            Assert.Equal(BackendErrorCode.CommandRejected, error.Code); Assert.NotNull(error.FailureCode);
            Assert.StartsWith("command", error.Field!, StringComparison.Ordinal); Assert.Same(source, f.Workspace.Project);
        }
        Assert.Equal("InvalidTarget", (await f.Command("0", new ResplitLineCommandDto(Line, ["甲", "" ,"乙"], false))).Error!.FailureCode);
        Assert.Equal(0, (await f.History()).UndoCount);
        LyricLine unicode = LyricLine.Create("one", "e\u0301👩🏽‍🚀", TimeRange.Create(0, 2000000), TimingStatus.Imported,
            [LyricToken.Create("first", "e\u0301👩🏽‍🚀", TimeRange.Create(0, 2000000), TimingStatus.Imported)]);
        f.Workspace.Replace(source with { Lyrics = LyricsDocument.Create([unicode]) });
        Assert.Equal("command.segments", (await f.Command("0", new ResplitLineCommandDto(Line, ["e", "\u0301👩🏽‍🚀"], false))).Error!.Field);
        Assert.True(Accept(await f.Command("0", new ResplitLineCommandDto(Line, ["e\u0301", "👩🏽‍🚀"], true))).Changed);
    }

    [Fact]
    public async Task 手工段20001项先资源拒绝且20000内形状错误仍400()
    {
        using Fixture f = await Fixture.Create();
        Project source = f.Workspace.Project;
        Assert.Equal(BackendErrorCode.ResourceTooLarge, (await f.Command("0", new ResplitLineCommandDto(Line, new string[20001], true))).Error!.Code);
        Assert.Equal(BackendErrorCode.InvalidRequest, (await f.Command("0", new ResplitLineCommandDto(Line, [null!], true))).Error!.Code);
        string bad = "{\"type\":\"lyrics.resplitLine\",\"segments\":[" + string.Join(',', Enumerable.Repeat("1", 20001)) + "],\"estimateTimes\":false}";
        Assert.Throws<ProjectCommandBudgetExceededException>(() => JsonSerializer.Deserialize<ProjectCommandDto>(bad, Json));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ProjectCommandDto>(bad.Replace(",1]", "]", StringComparison.Ordinal), Json));
        Assert.Same(source, f.Workspace.Project); Assert.Equal(0, (await f.History()).UndoCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 完整单行项或字节预算拒绝不先写入且删除仍可修复超大旧行(bool bytes)
    {
        using Fixture f = await Fixture.Create();
        string text = new(bytes ? 'a' : '字', bytes ? 530000 : 20001);
        LyricLine large = LyricLine.Create("one", text, TimeRange.Create(0, 2000000), TimingStatus.Imported,
            [LyricToken.Create("first", text, TimeRange.Create(0, 2000000), TimingStatus.Imported)]);
        f.Workspace.Replace(f.Workspace.Project with { Lyrics = LyricsDocument.Create([large]) });
        Project source = f.Workspace.Project;
        ProjectCommandDto command = bytes ? new SplitTokenCommandDto(Token) : new ResplitBranchCommandDto("primary", ProjectTokenSplitRule.PerTextElement, false);
        Assert.Equal(BackendErrorCode.ResourceTooLarge, (await f.Command("0", command)).Error!.Code);
        Assert.Same(source, f.Workspace.Project); Assert.Equal(0, (await f.History()).UndoCount);
        Assert.True(Accept(await f.Command("0", new RemoveLineCommandDto(Line))).Changed); Assert.Empty(f.Workspace.Project.Lyrics.Lines);
    }

    [Fact]
    public async Task 大合并元素不按grapheme数量误拒绝且最终总项上限仍生效()
    {
        using Fixture f = await Fixture.Create();
        string text = new('a', 40000);
        LyricLine large = LyricLine.Create("one", text, TimeRange.Create(0, 2000000), TimingStatus.Imported,
            [LyricToken.Create("first", text, TimeRange.Create(0, 2000000), TimingStatus.Imported)]);
        f.Workspace.Replace(f.Workspace.Project with { Lyrics = LyricsDocument.Create([large]) });
        Assert.True(Accept(await f.Command("0", new SplitTokenCommandDto(Token))).Changed);
        Assert.True(Accept(await f.Command("1", new ResplitBranchCommandDto("primary", ProjectTokenSplitRule.LatinWords, false))).Changed);
        Assert.Single(f.Workspace.Project.Lyrics.Lines[0].Tokens);
        large = LyricLine.Create("one", new string('字', 20000));
        f.Workspace.Replace(f.Workspace.Project with { Lyrics = LyricsDocument.Create([large]) });
        Assert.Equal(BackendErrorCode.ResourceTooLarge, (await f.Command("2", new ResplitLineCommandDto(Line, Enumerable.Repeat("字", 20000).ToArray(), false))).Error!.Code);
    }

    [Fact]
    public async Task 来源累计改变行16MiB预算整批原子拒绝且不是歌曲格式限制()
    {
        using Fixture f = await Fixture.Create();
        string text = new string('a', 240000) + " b";
        ImmutableArray<LyricLine> lines = Enumerable.Range(0, 36).Select(index => LyricLine.Create($"large-{index}", text)).ToImmutableArray();
        f.Workspace.Replace(f.Workspace.Project with { Lyrics = LyricsDocument.Create(lines) });
        Project source = f.Workspace.Project;
        Assert.False(Accept(await f.Command("0", new ResplitBranchCommandDto("primary", ProjectTokenSplitRule.None, false))).Changed);
        Assert.Equal(BackendErrorCode.ResourceTooLarge, (await f.Command("0", new ResplitBranchCommandDto("primary", ProjectTokenSplitRule.LatinWords, false))).Error!.Code);
        Assert.Same(source, f.Workspace.Project); Assert.Equal(0, (await f.History()).UndoCount);
        ProjectCommandResponse first = Accept(await f.Command("0", new ResplitLineCommandDto(new("primary", "large-0"), [new string('a', 240000), " ", "b"], false)));
        Assert.Equal(1, first.TokenResult!.ChangedLineCount);
        Assert.True((await f.Registry.ReadLyricsWindowAsync("client", f.Session.ProjectSessionId, "primary", "1", limit: 1)).Accepted);
    }

    [Fact]
    public async Task 排队后外部修改segments不改变执行与回执且所有选择进入指纹()
    {
        using Fixture f = await Fixture.Create();
        string[] parts = ["甲乙"]; ProjectCommandRequest request = f.Request("0", new ResplitLineCommandDto(Line, parts, false));
        SemaphoreSlim writer = f.Session.Writer;
        await writer.WaitAsync(); Task<ProjectCommandExecutionResult> pending;
        try { pending = f.Execute(request); Assert.False(pending.IsCompleted); parts[0] = "不等"; }
        finally { writer.Release(); }
        ProjectCommandResponse result = Accept(await pending.WaitAsync(TimeSpan.FromSeconds(10))); Assert.True(result.Changed);
        ProjectCommandRequest frozen = request with { Command = new ResplitLineCommandDto(Line, ["甲乙"], false) };
        Assert.Equal(JsonSerializer.Serialize(result, Json), JsonSerializer.Serialize(Accept(await f.Execute(frozen)), Json));
        Assert.Equal(BackendErrorCode.RequestIdReuse, (await f.Execute(request)).Error!.Code);
        Assert.Equal(BackendErrorCode.RequestIdReuse, (await f.Execute(frozen with { Command = new ResplitLineCommandDto(Line, ["甲乙"], true) })).Error!.Code);
        LyricTimingTargetDto mergedTarget = new("primary", "one", f.Workspace.Project.Lyrics.Lines[0].Tokens[0].Id);
        ProjectCommandRequest reorder = f.Request("1", new ReorderTokenCommandDto(mergedTarget, 0, false)); Accept(await f.Execute(reorder));
        Assert.Equal(BackendErrorCode.RequestIdReuse, (await f.Execute(reorder with { Command = new ReorderTokenCommandDto(mergedTarget, 0, true) })).Error!.Code);
        ProjectCommandRequest branch = f.Request("1", new ResplitBranchCommandDto("primary", ProjectTokenSplitRule.None, false)); Accept(await f.Execute(branch));
        Assert.Equal(BackendErrorCode.RequestIdReuse, (await f.Execute(branch with { Command = new ResplitBranchCommandDto("primary", ProjectTokenSplitRule.Mixed, false) })).Error!.Code);
    }

    [Fact]
    public async Task 九到十修订增长用完整单行真实预算原子拒绝()
    {
        using Fixture f = await Fixture.Create();
        for (int revision = 0; revision < 9; revision++) Accept(await f.Command(revision.ToString(CultureInfo.InvariantCulture), new SetCanvasSizeCommandDto(100 + revision, 100)));
        ProjectLyricsWindowResponse window = (await f.Registry.ReadLyricsWindowAsync("client", f.Session.ProjectSessionId, "primary", "9", limit: 1)).Response!;
        ProjectCommandResponse merged = Accept(await f.Command("9", new MergeTokenCommandDto(Token)));
        ProjectLyricsWindowResponse candidate = (await f.Registry.ReadLyricsWindowAsync("client", f.Session.ProjectSessionId, "primary", "10", limit: 1)).Response! with { ProjectRevision = "9" };
        Accept(await f.Command("10", new("history.undo")));
        // Use a new fixture so the pending real transition is 9 -> 10; padding lives in unchanged ruby metadata.
        using Fixture boundary = await Fixture.Create();
        for (int revision = 0; revision < 9; revision++) Accept(await boundary.Command(revision.ToString(CultureInfo.InvariantCulture), new SetCanvasSizeCommandDto(100 + revision, 100)));
        candidate = candidate with { ProjectSessionId = boundary.Session.ProjectSessionId };
        int padding = ProjectReadModelLimits.MaxResponseBytes - JsonSerializer.SerializeToUtf8Bytes(candidate, Json).Length;
        LyricLine original = boundary.Workspace.Project.Lyrics.Lines[0];
        string ruby = original.RubyAnnotations[0].Text + new string('x', padding);
        boundary.Workspace.Replace(boundary.Workspace.Project with { Lyrics = boundary.Workspace.Project.Lyrics with
            { Lines = boundary.Workspace.Project.Lyrics.Lines.SetItem(0, original with { RubyAnnotations = [original.RubyAnnotations[0] with { Text = ruby }] }) } });
        Project source = boundary.Workspace.Project; ProjectHistoryResponse history = await boundary.History();
        Assert.Equal(BackendErrorCode.ResourceTooLarge, (await boundary.Command("9", new MergeTokenCommandDto(Token))).Error!.Code);
        Assert.Same(source, boundary.Workspace.Project); Assert.Equal(history, await boundary.History());
    }

    [Theory]
    [InlineData("{\"type\":\"lyrics.resplitBranch\",\"rule\":0}")]
    [InlineData("{\"type\":\"lyrics.resplitBranch\",\"rule\":\"Mixed\"}")]
    [InlineData("{\"type\":\"lyrics.reorderToken\",\"destinationIndex\":\"1\"}")]
    [InlineData("{\"type\":\"lyrics.resplitLine\",\"segments\":[1]}")]
    [InlineData("{\"type\":\"lyrics.splitToken\",\"estimateTimes\":false}")]
    [InlineData("{\"type\":\"lyrics.mergeToken\",\"type\":\"lyrics.mergeToken\"}")]
    public void 新type严格解析且未知type继续保留旧错误分派(string wire) => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ProjectCommandDto>(wire, Json));

    [Fact]
    public async Task 整来源快速布局预演不写历史应用一次且识别结果稳定()
    {
        using Fixture f = await Fixture.Create();
        BasicLayoutCommandDto command = new("primary", "KaraokeDouble", null,
            "整来源双行", ["左槽", "右槽"], "已选", "保留", false);
        BasicLayoutCommandDto preview = new("primary", "KaraokeDouble", null,
            "整来源双行", ["左槽", "右槽"], "已选", "保留", true);
        Project source = f.Workspace.Project; ProjectHistoryResponse before = await f.History();
        ProjectCommandResponse candidate = Accept(await f.Command("0", preview));
        Assert.False(candidate.Changed); Assert.Equal("source", candidate.BasicLayoutResult!.Scope);
        Assert.Equal(before, await f.History()); Assert.Same(source, f.Workspace.Project);
        ProjectCommandResponse applied = Accept(await f.Command("0", command));
        Assert.True(applied.Changed); Assert.Equal(["orchestration", "renderInputs"], applied.ChangeSet.Areas);
        Assert.Equal(1, (await f.History()).UndoCount); Assert.Equal("KaraokeDouble", applied.BasicLayoutResult!.RecognizedPreset);
        ProjectOrchestrationResponse model = (await f.Registry.ReadOrchestrationAsync("client", f.Session.ProjectSessionId, "1"))!.Response!;
        Assert.Contains(model.Programs, item => item.BranchId == "primary");
    }

    [Fact]
    public async Task 快速布局无变化保留redo且错误编号稳定()
    {
        using Fixture f = await Fixture.Create();
        BasicLayoutCommandDto command = new("primary", "SingleCentered", null, "单行", ["槽"], "已选", "保留", false);
        Assert.True(Accept(await f.Command("0", command)).Changed);
        ProjectHistoryResponse history = await f.History();
        Assert.False(Accept(await f.Command("1", command)).Changed); Assert.Equal(history, await f.History());
        BackendErrorResponse error = (await f.Command("1", command with { Preset = "unsupported" })).Error!;
        Assert.Equal(BackendErrorCode.CommandRejected, error.Code); Assert.Equal("InvalidPreset", error.FailureCode);
        error = (await f.Command("1", command with { SlotNames = ["槽", "多余"] })).Error!;
        Assert.Equal(BackendErrorCode.InvalidRequest, error.Code); Assert.Equal("InvalidRange", error.FailureCode);
        ProjectCommandResponse selected = Accept(await f.Command("1", command with { SelectedLineIds = ["two"] }));
        Assert.False(selected.Changed);
    }

    [Fact]
    public async Task 片段生命周期一次历史并在删除后可撤销重做()
    {
        using Fixture f = await Fixture.Create();
        string regular = LyricSceneIds.Regular("primary");
        ProjectCommandResponse created = Accept(await f.Command("0", new CreateOrchestrationClipCommandDto(
            "primary", "clip/one", "副歌", ["one"], regular)));
        Assert.True(created.Changed); Assert.Equal(["orchestration", "renderInputs"], created.ChangeSet.Areas);
        Assert.Equal("clip/one", created.ClipResult!.ClipId); Assert.Equal(1, (await f.History()).UndoCount);
        ProjectOrchestrationResponse model = (await f.Registry.ReadOrchestrationAsync("client", f.Session.ProjectSessionId, "1"))!.Response!;
        Assert.Equal("clip/one", Assert.Single(model.Programs.Single(value => value.BranchId == "primary").Clips!).Id);

        ProjectCommandResponse renamed = Accept(await f.Command("1", new RenameOrchestrationClipCommandDto("primary", "clip/one", null)));
        Assert.True(renamed.Changed); Assert.Null(renamed.ClipResult!.Name);
        ProjectCommandResponse deleted = Accept(await f.Command("2", new DeleteOrchestrationClipCommandDto("primary", "clip/one")));
        Assert.True(deleted.Changed); Assert.Empty(f.Workspace.Project.LyricPresentation.WithDefaultPrograms(f.Workspace.Project.Lyrics).FindProgram("primary")!.Clips);
        ProjectCommandResponse undone = Accept(await f.Command("3", new("history.undo")));
        Assert.True(undone.Changed); Assert.Single(f.Workspace.Project.LyricPresentation.WithDefaultPrograms(f.Workspace.Project.Lyrics).FindProgram("primary")!.Clips);
        ProjectCommandResponse redone = Accept(await f.Command("4", new("history.redo")));
        Assert.True(redone.Changed); Assert.Empty(f.Workspace.Project.LyricPresentation.WithDefaultPrograms(f.Workspace.Project.Lyrics).FindProgram("primary")!.Clips);
    }

    [Fact]
    public void 片段命令JSON严格区分重命名显式null与缺失()
    {
        string valid = "{\"type\":\"orchestration.renameClip\",\"branchId\":\"primary\",\"clipId\":\"clip/one\",\"name\":null}";
        RenameOrchestrationClipCommandDto command = Assert.IsType<RenameOrchestrationClipCommandDto>(JsonSerializer.Deserialize<ProjectCommandDto>(valid, Json));
        Assert.Null(command.Name);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ProjectCommandDto>(valid.Replace(",\"name\":null", "", StringComparison.Ordinal), Json));
    }

    [Fact]
    public async Task 片段同值重命名是noop并保留redo()
    {
        using Fixture f = await Fixture.Create();
        string regular = LyricSceneIds.Regular("primary");
        Accept(await f.Command("0", new CreateOrchestrationClipCommandDto("primary", "clip/noop", "副歌", ["one"], regular)));
        Accept(await f.Command("1", new RenameOrchestrationClipCommandDto("primary", "clip/noop", "新名字")));
        Accept(await f.Command("2", new("history.undo")));
        ProjectCommandResponse noop = Accept(await f.Command("3", new RenameOrchestrationClipCommandDto("primary", "clip/noop", "副歌")));
        Assert.False(noop.Changed); Assert.Equal("3", noop.ProjectRevision); Assert.True(noop.CanRedo);
        ProjectCommandResponse redo = Accept(await f.Command("3", new("history.redo")));
        Assert.True(redo.Changed); Assert.Equal("4", redo.ProjectRevision);
    }

    [Fact]
    public async Task G20结构命令覆盖改名槽位增删重排并保持单次历史()
    {
        using Fixture f = await Fixture.Create();
        string revision = "0";
        async Task<ProjectCommandResponse> Send(ProjectCommandDto command)
        {
            ProjectCommandResponse response = Accept(await f.Command(revision, command));
            revision = response.ProjectRevision;
            return response;
        }
        var tree = f.Workspace.Project.LyricPresentation.WithDefaultPrograms(f.Workspace.Project.Lyrics);
        string nodeId = tree.FindProgram("primary")!.DefaultContainerId;
        ProjectCommandResponse renamed = await Send(new SetOrchestrationNodeCommandDto(nodeId, "新布局"));
        Assert.True(renamed.Changed); Assert.Equal(["orchestration", "renderInputs"], renamed.ChangeSet.Areas);
        Assert.Equal("setNode", renamed.StructureResult!.Operation); Assert.True(renamed.StructureResult.Changed);
        Assert.Equal("新布局", f.Workspace.Project.LyricPresentation.FindNode(nodeId)!.Name);
        ProjectCommandResponse undone = await Send(new("history.undo"));
        Assert.True(undone.CanRedo);
        ProjectCommandResponse noop = await Send(new SetOrchestrationNodeCommandDto(nodeId, null));
        Assert.False(noop.Changed); Assert.True(noop.CanRedo);
        Assert.Equal("setNode", noop.StructureResult!.Operation); Assert.False(noop.StructureResult.Changed);

        BasicLayoutCommandDto layout = new("primary", "DoubleCentered", null, "双行", ["一", "二"], "已选", "保留", false);
        ProjectCommandResponse laidOut = await Send(layout);
        Assert.True(laidOut.Changed);
        tree = f.Workspace.Project.LyricPresentation.WithDefaultPrograms(f.Workspace.Project.Lyrics);
        var container = tree.FindProgram("primary")!.DefaultContainerId;
        _ = tree.FindNode(container)!.Layout!.SlotIds;
        BackendErrorResponse referenced = (await f.Command(revision, new RemoveOrchestrationNodeCommandDto(container))).Error!;
        Assert.Equal(BackendErrorCode.CommandRejected, referenced.Code); Assert.Equal("PresentationInvalid", referenced.FailureCode);
        string root = tree.FindProgram("primary")!.RootContainerId;
        string freeNodeId = "group/g20";
        f.Workspace.Replace(f.Workspace.Project with { LyricPresentation = tree with
            { Nodes = tree.Nodes.Add(new SceneContainerDefinition { Id = freeNodeId, ParentId = root }) } });
        string[] children = tree.Nodes.Where(node => node.ParentId == container).Select(node => node.Id).ToArray();
        if (children.Length > 1)
        {
            ProjectCommandResponse childOrder = await Send(new ReorderOrchestrationChildrenCommandDto(container, children.Reverse().ToArray()));
            Assert.True(childOrder.Changed);
            ProjectCommandResponse childUndo = await Send(new("history.undo"));
            Assert.True(childUndo.CanRedo);
            await Send(new("history.redo"));
        }
        ProjectCommandResponse moved = await Send(new ReparentOrchestrationNodeCommandDto(freeNodeId, null));
        Assert.True(moved.Changed); Assert.Null(f.Workspace.Project.LyricPresentation.FindNode(freeNodeId)!.ParentId);
        ProjectCommandResponse sameParent = await Send(new ReparentOrchestrationNodeCommandDto(freeNodeId, null));
        Assert.False(sameParent.Changed);
        Assert.Equal(moved.ProjectRevision, sameParent.PreviousRevision);
        tree = f.Workspace.Project.LyricPresentation.WithDefaultPrograms(f.Workspace.Project.Lyrics);
        container = tree.FindProgram("primary")!.DefaultContainerId;
        ProjectCommandResponse added = await Send(new AddOrchestrationSlotCommandDto(container, "slot/g20", null));
        Assert.True(added.Changed); Assert.Contains("slot/g20", f.Workspace.Project.LyricPresentation.FindNode(container)!.Layout!.SlotIds);
        string[] ordered = f.Workspace.Project.LyricPresentation.FindNode(container)!.Layout!.SlotIds.Reverse().ToArray();
        ProjectCommandResponse reordered = await Send(new ReorderOrchestrationSlotsCommandDto(container, ordered));
        Assert.Equal(ordered, f.Workspace.Project.LyricPresentation.FindNode(container)!.Layout!.SlotIds);
        ProjectCommandResponse removed = await Send(new RemoveOrchestrationNodeCommandDto("slot/g20"));
        Assert.Equal(ordered.Where(id => id != "slot/g20"), f.Workspace.Project.LyricPresentation.FindNode(container)!.Layout!.SlotIds);
        Assert.True(removed.Changed); Assert.Equal(6, (await f.History()).UndoCount);
    }

    [Fact]
    public async Task G21内部布局默认位置单次历史并保持外层位置不变()
    {
        using Fixture f = await Fixture.Create();
        var tree = f.Workspace.Project.LyricPresentation.WithDefaultPrograms(f.Workspace.Project.Lyrics);
        string containerId = tree.FindProgram("primary")!.DefaultContainerId;
        var node = tree.FindNode(containerId)!;
        Assert.NotNull(node.Layout);
        ScenePlacement outer = node.Placement;
        ProjectOrchestrationPlacementDto placement = new(.25, .75, "TopLeft", 12, 1.25);

        ProjectCommandResponse changed = Accept(await f.Command("0",
            new SetOrchestrationLayoutDefaultPlacementCommandDto(containerId, placement)));
        Assert.True(changed.Changed); Assert.Equal("1", changed.ProjectRevision);
        Assert.Equal(["orchestration", "renderInputs"], changed.ChangeSet.Areas);
        Assert.Equal(new OrchestrationLayoutCommandResultDto(containerId, true), changed.LayoutResult);
        var after = f.Workspace.Project.LyricPresentation.FindNode(containerId)!;
        Assert.Equal(outer, after.Placement);
        Assert.Equal(placement.X, after.Layout!.DefaultPlacement!.Position.X);
        Assert.Equal(placement.Y, after.Layout.DefaultPlacement.Position.Y);
        Assert.Equal(ScenePlacementAnchor.TopLeft, after.Layout.DefaultPlacement.Anchor);
        Assert.Equal(12, after.Layout.DefaultPlacement.RotationDegrees);
        Assert.Equal(1.25, after.Layout.DefaultPlacement.Scale);

        ProjectCommandResponse undone = Accept(await f.Command("1", new("history.undo")));
        Assert.True(undone.Changed); Assert.True(undone.CanRedo);
        ProjectCommandResponse noop = Accept(await f.Command("2",
            new SetOrchestrationLayoutDefaultPlacementCommandDto(containerId, null)));
        Assert.False(noop.Changed); Assert.Equal("2", noop.ProjectRevision); Assert.True(noop.CanRedo);
        Assert.Equal(new OrchestrationLayoutCommandResultDto(containerId, false), noop.LayoutResult);
        ProjectCommandResponse redone = Accept(await f.Command("2", new("history.redo")));
        Assert.True(redone.Changed); Assert.Equal("3", redone.ProjectRevision);
        Assert.Equal(placement.X, f.Workspace.Project.LyricPresentation.FindNode(containerId)!.Layout!.DefaultPlacement!.Position.X);
    }

    [Fact]
    public void G21内部布局位置DTO严格字段与数值()
    {
        string valid = "{\"type\":\"orchestration.setLayoutDefaultPlacement\",\"containerId\":\"node\",\"placement\":{\"x\":0.25,\"y\":0.75,\"anchor\":\"TopLeft\",\"rotationDegrees\":12,\"scale\":1.25}}";
        var command = Assert.IsType<SetOrchestrationLayoutDefaultPlacementCommandDto>(JsonSerializer.Deserialize<ProjectCommandDto>(valid, Json));
        Assert.Equal("node", command.ContainerId); Assert.Equal("TopLeft", command.Placement!.Anchor);
        Assert.Null(Assert.IsType<SetOrchestrationLayoutDefaultPlacementCommandDto>(JsonSerializer.Deserialize<ProjectCommandDto>(
            "{\"type\":\"orchestration.setLayoutDefaultPlacement\",\"containerId\":\"node\",\"placement\":null}", Json)).Placement);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ProjectCommandDto>(valid.Replace(",\"scale\":1.25}", ",\"scale\":1.25,\"extra\":1}", StringComparison.Ordinal), Json));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ProjectCommandDto>(valid.Replace("\"scale\":1.25", "\"scale\":0", StringComparison.Ordinal), Json));
    }

    [Fact]
    public void G20结构命令严格区分显式空值与未知字段()
    {
        string valid = "{\"type\":\"orchestration.reparent\",\"nodeId\":\"node\",\"parentId\":null}";
        ReparentOrchestrationNodeCommandDto command = Assert.IsType<ReparentOrchestrationNodeCommandDto>(JsonSerializer.Deserialize<ProjectCommandDto>(valid, Json));
        Assert.Null(command.ParentId);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ProjectCommandDto>(valid.Replace(",\"parentId\":null", "", StringComparison.Ordinal), Json));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ProjectCommandDto>(valid.Replace("}", ",\"extra\":1}", StringComparison.Ordinal), Json));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ProjectCommandDto>(
            "{\"type\":\"orchestration.reorderSlots\",\"containerId\":null,\"orderedIds\":[\"slot\"]}", Json));
    }

    [Theory]
    [InlineData("orchestration.previewBasicLayout", true)]
    [InlineData("orchestration.applyBasicLayout", false)]
    public void 快速布局JSON严格匹配preview与apply类型并保持字段(string type, bool preview)
    {
        string json = $$"""{"type":"{{type}}","branchId":"primary","preset":"SingleCentered","selectedLineIds":null,"layoutName":"单行","slotNames":["槽"],"selectedSegmentName":"已选","retainedSegmentName":"保留"}""";
        BasicLayoutCommandDto command = Assert.IsType<BasicLayoutCommandDto>(JsonSerializer.Deserialize<ProjectCommandDto>(json, Json));
        Assert.Equal(preview, command.Preview);
        Assert.Equal(type, command.Type);
        Assert.Equal(type, JsonSerializer.Deserialize<ProjectCommandDto>(JsonSerializer.Serialize(command, Json), Json)!.Type);
    }

    [Theory]
    [InlineData("{\"type\":\"orchestration.previewBasicLayout\",\"branchId\":\"primary\",\"preset\":\"SingleCentered\",\"layoutName\":\"单行\",\"slotNames\":[\"槽\"],\"selectedSegmentName\":\"已选\",\"retainedSegmentName\":\"保留\",\"extra\":1}")]
    [InlineData("{\"type\":\"orchestration.applyBasicLayout\",\"branchId\":\"primary\",\"preset\":\"SingleCentered\",\"layoutName\":\"单行\",\"slotNames\":[\"槽\"],\"selectedSegmentName\":\"已选\",\"retainedSegmentName\":\"保留\",\"slotNames\":[\"重复\"]}")]
    [InlineData("{\"type\":\"orchestration.previewBasicLayout\",\"branchId\":\"primary\",\"preset\":\"SingleCentered\",\"layoutName\":\"单行\",\"slotNames\":[],\"selectedSegmentName\":\"已选\",\"retainedSegmentName\":\"保留\"}")]
    [InlineData("{\"type\":\"orchestration.applyBasicLayout\",\"branchId\":\"primary\",\"preset\":\"SingleCentered\",\"layoutName\":\"单行\",\"slotNames\":[\"槽\"],\"selectedSegmentName\":null,\"retainedSegmentName\":\"保留\"}")]
    public void 快速布局JSON拒绝unknown重复缺失和非法scope字段(string json) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ProjectCommandDto>(json, Json));

    [Fact]
    public async Task 目标有效仍必须显式提供bool与合法规则且遗漏不等同false()
    {
        using Fixture f = await Fixture.Create();
        ProjectCommandDto[] inputs = [new ReorderTokenCommandDto(Token, 0, null), new ReorderTokenCommandDto(Token, null, false),
            new ResplitLineCommandDto(Line, ["甲", "乙"], null), new ResplitLineCommandDto(Line, null, false),
            new ResplitBranchCommandDto("primary", ProjectTokenSplitRule.None, null), new ResplitBranchCommandDto("primary", null, false),
            new ResplitBranchCommandDto("primary", (ProjectTokenSplitRule)99, false)];
        foreach (ProjectCommandDto input in inputs) Assert.Equal(BackendErrorCode.InvalidRequest, (await f.Command("0", input)).Error!.Code);
        Assert.Equal(0, (await f.History()).UndoCount);
    }

    [Fact]
    public async Task 五DTO完整往返且运行类型绑定防止派生字段逃逸到history()
    {
        using Fixture f = await Fixture.Create();
        ProjectCommandDto[] inputs = [new SplitTokenCommandDto(Token), new MergeTokenCommandDto(Token), new ReorderTokenCommandDto(Token, 1, false),
            new ResplitLineCommandDto(Line, ["甲", "乙"], false), new ResplitBranchCommandDto("primary", ProjectTokenSplitRule.ByPunctuation, false)];
        foreach (ProjectCommandDto input in inputs)
        {
            string wire = JsonSerializer.Serialize(input, Json); ProjectCommandDto parsed = JsonSerializer.Deserialize<ProjectCommandDto>(wire, Json)!;
            Assert.Equal(wire, JsonSerializer.Serialize(parsed, Json)); Assert.Equal(input.GetType(), parsed.GetType());
            Assert.Equal(BackendErrorCode.InvalidRequest, (await f.Command("0", input with { Type = "history.undo" })).Error!.Code);
        }
        Assert.Equal(BackendErrorCode.CommandNotSupported, (await f.Command("0", new("unknown"))).Error!.Code);
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
