using MakoKaraGenerator.Application.Editing;
using MakoKaraGenerator.Core.Commands;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Application.Tests;

public sealed class LyricTimingEditUseCaseTests
{
    [Fact]
    public void 元素组提交保留未改端点的精确微秒且一步历史可撤销重做()
    {
        Project source = Sample();
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        LyricTimingEditTarget target = new("primary", "one", "first");
        int notifications = 0;
        workspace.History.Changed += (_, _) => notifications++;

        LyricTimingEditResult result = LyricTimingEditUseCase.Commit(workspace,
            Request(workspace, target, start: null, end: 2_200_501));

        Assert.True(result.Accepted);
        Assert.True(result.Changed);
        Assert.Null(result.Error);
        Assert.Same(result.Project, workspace.Project);
        Assert.Equal(TimeRange.Create(1_000_499, 2_200_501), result.Project.Lyrics.Lines[0].Tokens[0].Time);
        Assert.Equal(TimingStatus.Edited, result.Project.Lyrics.Lines[0].Tokens[0].TimingStatus);
        Assert.Same(source.Lyrics.Lines[0].Tokens[1], result.Project.Lyrics.Lines[0].Tokens[1]);
        Assert.Same(source.Lyrics.Lines[1], result.Project.Lyrics.Lines[1]);
        Assert.Equal(source.Lyrics.Lines[0].Time, result.Project.Lyrics.Lines[0].Time);
        Assert.Same(source.LyricPresentation, result.Project.LyricPresentation);
        Assert.Equal(target, Assert.Single(result.ChangeSet.Targets));
        Assert.False(result.ChangeSet.FullRefreshRequired);
        Assert.True(result.ChangeSet.InvalidatesPreview);
        Assert.Equal("token.time", workspace.UndoName);
        Assert.Equal(1, workspace.History.UndoCount);
        Assert.Equal(1, notifications);
        Assert.True(workspace.IsDirty);

        Assert.Same(source, workspace.Undo());
        Assert.False(workspace.IsDirty);
        Assert.Same(result.Project, workspace.Redo());
        Assert.True(workspace.IsDirty);
        Assert.Equal(3, notifications);
    }

    [Fact]
    public void 行组提交不改元素并保留大于双精度整数边界的微秒()
    {
        const long ExactStart = 9_007_199_254_740_993;
        const long ExactEnd = ExactStart + 1_003;
        Project source = Project.Create("large", lyrics: LyricsDocument.Create([
            LyricLine.Create("large", "长", TimeRange.Create(ExactStart, ExactEnd), TimingStatus.Imported)]));
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);

        LyricTimingEditResult result = LyricTimingEditUseCase.Commit(workspace,
            Request(workspace, new("primary", "large"), start: null, end: ExactEnd + 17));

        Assert.True(result.Accepted);
        Assert.Equal(TimeRange.Create(ExactStart, ExactEnd + 17), workspace.Project.Lyrics.Lines[0].Time);
        Assert.Equal(TimingStatus.Edited, workspace.Project.Lyrics.Lines[0].TimingStatus);
        Assert.Equal(source.Lyrics.Lines[0].Tokens, workspace.Project.Lyrics.Lines[0].Tokens);
        Assert.Null(Assert.Single(result.ChangeSet.Targets).TokenId);
        Assert.Same(source, workspace.Undo());
    }

    [Fact]
    public void 两次组提交不合并到之前或之后的可合并时间手势()
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(Sample());
        LyricTimingEditTarget target = new("primary", "one", "first");
        workspace.Execute(ProjectCommand.Create("token.time", project => project with { Name = "之前" }, mergeable: true));
        Assert.True(LyricTimingEditUseCase.Commit(workspace, Request(workspace, target, null, 2_100_000)).Accepted);
        Project first = workspace.Project;
        Assert.True(LyricTimingEditUseCase.Commit(workspace, Request(workspace, target, null, 2_200_000)).Accepted);
        workspace.Execute(ProjectCommand.Create("token.time", project => project with { Name = "之后" }, mergeable: true));

        Assert.Equal(4, workspace.History.UndoCount);
        workspace.Undo();
        Assert.Same(first, workspace.Undo());
    }

    [Fact]
    public void 相同端点不改变导入状态或干净基线且不通知历史()
    {
        Project source = Sample();
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        int notifications = 0;
        workspace.History.Changed += (_, _) => notifications++;

        LyricTimingEditResult result = LyricTimingEditUseCase.Commit(workspace,
            Request(workspace, new("primary", "one", "first"), null, null));

        Assert.True(result.Accepted);
        Assert.False(result.Changed);
        Assert.Same(source, result.Project);
        Assert.Same(ProjectTimingChangeSet.None, result.ChangeSet);
        Assert.Equal(TimingStatus.Imported, workspace.Project.Lyrics.Lines[0].Tokens[0].TimingStatus);
        Assert.False(workspace.IsDirty);
        Assert.False(workspace.CanUndo);
        Assert.Equal(0, notifications);
    }

    [Fact]
    public void 撤销后的无变化提交保留重做栈()
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(Sample());
        LyricTimingEditTarget target = new("primary", "one", "first");
        Project edited = LyricTimingEditUseCase.Commit(workspace, Request(workspace, target, null, 2_200_000)).Project;
        workspace.Undo();

        LyricTimingEditResult unchanged = LyricTimingEditUseCase.Commit(workspace, Request(workspace, target, null, null));

        Assert.True(unchanged.Accepted);
        Assert.False(unchanged.Changed);
        Assert.True(workspace.CanRedo);
        Assert.Same(edited, workspace.Redo());
    }

    [Fact]
    public void 副来源提交只写明确目标且不改变主来源或其他来源()
    {
        Project source = Sample();
        LyricBranch branch = LyricBranch.Create("translation", "翻译", LyricBranchRole.Translation,
            [LyricLine.Create("translated", "译", TimeRange.Create(1_000_000, 5_000_000), TimingStatus.Imported)]);
        source = source with { Lyrics = source.Lyrics with { Branches = source.Lyrics.Branches.Add(branch) } };
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        LyricTimingEditTarget target = new("translation", "translated");

        LyricTimingEditResult result = LyricTimingEditUseCase.Commit(workspace, Request(workspace, target, 900_499, null));

        Assert.True(result.Accepted);
        Assert.Equal("primary", workspace.Project.Lyrics.PrimaryBranchId);
        Assert.Same(source.Lyrics.PrimaryBranch, workspace.Project.Lyrics.PrimaryBranch);
        Assert.Equal(TimeRange.Create(900_499, 5_000_000), workspace.Project.Lyrics.Branches[1].Lines[0].Time);
        Assert.Equal(target, Assert.Single(result.ChangeSet.Targets));
        Assert.Same(source, workspace.Undo());
        Assert.Same(result.Project, workspace.Redo());
    }

    [Theory]
    [InlineData("missing", "one", "first")]
    [InlineData("primary", "missing", "first")]
    [InlineData("primary", "two", "first")]
    [InlineData("primary", "one", "third")]
    public void 缺失或属于其他行的稳定目标不会回退到当前位置(string branch, string line, string token)
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(Sample());
        LyricTimingEditRequest request = new(workspace, workspace.Generation, new(branch, line, token), null,
            1_200_000, 2_200_000);

        AssertRejected(workspace, request, LyricTimingEditErrorCode.TargetStale);
    }

    [Fact]
    public void 同标识同代次的另一个工作区不能消费旧请求()
    {
        ProjectWorkspace original = ProjectWorkspace.Adopt(Sample());
        LyricTimingEditRequest request = Request(original, new("primary", "one", "first"), null, 2_200_000);
        ProjectWorkspace replacement = ProjectWorkspace.Adopt(original.Project);
        Assert.Equal(original.Generation, replacement.Generation);

        AssertRejected(replacement, request, LyricTimingEditErrorCode.WorkspaceStale);
        Assert.False(original.IsDirty);
        Assert.False(original.CanUndo);
    }

    [Fact]
    public void 同一工作区换工程后不能消费旧请求()
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(Sample());
        LyricTimingEditRequest request = Request(workspace, new("primary", "one", "first"), null, 2_200_000);
        workspace.StartNew(Sample());

        AssertRejected(workspace, request, LyricTimingEditErrorCode.WorkspaceStale);
    }

    [Fact]
    public void 原时间改变或目标删除拒绝旧输入且保持既有历史()
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(Sample());
        LyricTimingEditTarget target = new("primary", "one", "first");
        LyricTimingEditRequest old = Request(workspace, target, null, 2_200_000);
        Assert.True(LyricTimingEditUseCase.Commit(workspace, Request(workspace, target, null, 2_100_000)).Accepted);
        AssertRejected(workspace, old, LyricTimingEditErrorCode.TargetStale);
        workspace.Execute(ProjectCommand.Create("remove", project => project with
            { Lyrics = project.Lyrics with { Lines = project.Lyrics.Lines.RemoveAt(0) } }));
        AssertRejected(workspace, old, LyricTimingEditErrorCode.TargetStale);
    }

    [Fact]
    public void 无关工程编辑不使原时间未变的输入过期()
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(Sample());
        LyricTimingEditRequest request = Request(workspace, new("primary", "one", "first"), null, 2_200_000);
        workspace.Execute(ProjectCommand.Create("rename", project => project with { Name = "新名称" }));

        Assert.True(LyricTimingEditUseCase.Commit(workspace, request).Accepted);
        Assert.Equal("新名称", workspace.Project.Name);
        Assert.Equal(2, workspace.History.UndoCount);
    }

    [Theory]
    [InlineData(-1, 2_200_000)]
    [InlineData(1_000_499, 1_000_499)]
    [InlineData(900_000, 2_200_000)]
    [InlineData(1_000_499, 3_100_000)]
    public void 非法区间越出行或邻字冲突拒绝整组且不修正端点(long start, long end)
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(Sample());
        AssertRejected(workspace, Request(workspace, new("primary", "one", "first"), start, end),
            LyricTimingEditErrorCode.DomainValidationFailed);
    }

    [Fact]
    public void 缩小行导致元素越界拒绝而合法行编辑不改元素()
    {
        Project source = Sample();
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        LyricTimingEditTarget target = new("primary", "one");
        AssertRejected(workspace, Request(workspace, target, 1_100_000, null),
            LyricTimingEditErrorCode.DomainValidationFailed);

        LyricTimingEditResult result = LyricTimingEditUseCase.Commit(workspace, Request(workspace, target, 900_001, 4_900_003));

        Assert.True(result.Accepted);
        Assert.Equal(source.Lyrics.Lines[0].Tokens, workspace.Project.Lyrics.Lines[0].Tokens);
        Assert.Equal(TimingStatus.Edited, workspace.Project.Lyrics.Lines[0].TimingStatus);
        Assert.Equal(target, Assert.Single(result.ChangeSet.Targets));
    }

    [Fact]
    public void 媒体边界可精确相接但越过一微秒拒绝且未知时长不阻断()
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(Sample());
        LyricTimingEditRequest request = Request(workspace, new("primary", "one", "first"), null, 2_200_000)
            with { ContentDurationUs = 2_199_999 };
        AssertRejected(workspace, request, LyricTimingEditErrorCode.BeyondContentDuration);
        Assert.True(LyricTimingEditUseCase.Commit(workspace, request with { ContentDurationUs = 2_200_000 }).Accepted);
        Assert.True(LyricTimingEditUseCase.Commit(workspace,
            Request(workspace, request.Target, null, 2_300_000) with { ContentDurationUs = 0 }).Accepted);
    }

    [Fact]
    public void 初次定时必须提供两个端点且不要求时间轴已有块()
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(Sample());
        LyricTimingEditTarget target = new("primary", "two", "third");
        Assert.True(LyricTimingEditUseCase.TryReadTime(workspace.Project, target, out TimeRange? original));
        Assert.Null(original);
        AssertRejected(workspace, Request(workspace, target, null, 7_000_000), LyricTimingEditErrorCode.MissingEndpoint);
        AssertRejected(workspace, Request(workspace, target, 6_000_000, null), LyricTimingEditErrorCode.MissingEndpoint);

        LyricTimingEditResult result = LyricTimingEditUseCase.Commit(workspace, Request(workspace, target, 6_000_001, 7_000_003));

        Assert.True(result.Accepted);
        Assert.Equal(TimeRange.Create(6_000_001, 7_000_003), workspace.Project.Lyrics.Lines[1].Tokens[0].Time);
        Assert.Equal(TimingStatus.Edited, workspace.Project.Lyrics.Lines[1].Tokens[0].TimingStatus);
    }

    [Fact]
    public void 正式展示独占冲突拒绝而不发布已计算候选()
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(ExclusiveDisplayFixture.Create());
        LyricTimingEditTarget target = new("primary", "line-2");

        LyricTimingEditResult result = AssertRejected(workspace, Request(workspace, target, 3_500_000, null),
            LyricTimingEditErrorCode.DomainValidationFailed);

        Assert.Contains("重叠", result.Error!.Detail, StringComparison.Ordinal);
    }

    private static LyricTimingEditRequest Request(
        ProjectWorkspace workspace, LyricTimingEditTarget target, long? start, long? end)
    {
        Assert.True(LyricTimingEditUseCase.TryReadTime(workspace.Project, target, out TimeRange? original));
        return new(workspace, workspace.Generation, target, original, start, end);
    }

    private static LyricTimingEditResult AssertRejected(
        ProjectWorkspace workspace, LyricTimingEditRequest request, LyricTimingEditErrorCode code)
    {
        Project source = workspace.Project;
        int undo = workspace.History.UndoCount;
        int redo = workspace.History.RedoCount;
        bool dirty = workspace.IsDirty;
        int notifications = 0;
        EventHandler handler = (_, _) => notifications++;
        workspace.History.Changed += handler;
        LyricTimingEditResult result;
        try { result = LyricTimingEditUseCase.Commit(workspace, request); }
        finally { workspace.History.Changed -= handler; }
        Assert.False(result.Accepted);
        Assert.False(result.Changed);
        Assert.Equal(code, result.Error!.Code);
        Assert.Same(source, result.Project);
        Assert.Same(source, workspace.Project);
        Assert.False(result.ChangeSet.InvalidatesPreview);
        Assert.Equal(undo, workspace.History.UndoCount);
        Assert.Equal(redo, workspace.History.RedoCount);
        Assert.Equal(dirty, workspace.IsDirty);
        Assert.Equal(0, notifications);
        return result;
    }

    internal static Project Sample() => Project.Create("时间用例", lyrics: LyricsDocument.Create([
        LyricLine.Create("one", "甲乙", TimeRange.Create(1_000_000, 5_000_000), TimingStatus.Imported, [
            LyricToken.Create("first", "甲", TimeRange.Create(1_000_499, 2_000_499), TimingStatus.Imported),
            LyricToken.Create("second", "乙", TimeRange.Create(3_000_000, 4_000_000), TimingStatus.Confirmed)]),
        LyricLine.Create("two", "丙", TimeRange.Create(6_000_000, 8_000_000), TimingStatus.Edited, [
            LyricToken.Create("third", "丙")]) ]));
}
