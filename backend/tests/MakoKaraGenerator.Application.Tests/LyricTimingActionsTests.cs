using MakoKaraGenerator.Application.Editing;
using MakoKaraGenerator.Core.Commands;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Application.Tests;

public sealed class LyricTimingActionsTests
{
    private static readonly LyricTimingEditTarget First = new("primary", "one", "first");
    private static readonly LyricTimingEditTarget Second = new("primary", "one", "second");

    [Fact]
    public void 同元素重复微调合并而换元素或操作后分别撤销()
    {
        Project source = LyricTimingEditUseCaseTests.Sample();
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        Assert.True(LyricTimingEditUseCase.Nudge(workspace, Baseline(workspace, First), 1).Accepted);
        Assert.True(LyricTimingEditUseCase.Nudge(workspace, Baseline(workspace, First), 1).Accepted);
        Project firstNudges = workspace.Project;
        Assert.Equal(1, workspace.History.UndoCount);
        Assert.True(LyricTimingEditUseCase.Nudge(workspace, Baseline(workspace, Second), 1).Accepted);
        Project secondNudge = workspace.Project;
        Assert.True(LyricTimingEditUseCase.SetTime(workspace, Request(workspace, Second, null, 4_100_001)).Accepted);

        Assert.Equal(3, workspace.History.UndoCount);
        Assert.Equal("token.time", workspace.UndoName);
        Assert.Same(secondNudge, workspace.Undo());
        Assert.Same(firstNudges, workspace.Undo());
        Assert.Same(source, workspace.Undo());
        Assert.Same(firstNudges, workspace.Redo());
        Assert.Equal(TimeRange.Create(1_067_165, 2_067_165), workspace.Project.Lyrics.Lines[0].Tokens[0].Time);
    }

    [Fact]
    public void 微调按有理帧率换算精确微秒并保留未涉及目标()
    {
        Project source = LyricTimingEditUseCaseTests.Sample();
        source = source with { Composition = source.Composition with { FrameRate = FrameRate.NtscVideo } };
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);

        LyricTimingEditResult forward = LyricTimingEditUseCase.Nudge(workspace, Baseline(workspace, First), 3);
        Assert.True(forward.Accepted);
        Assert.Equal(TimeRange.Create(1_100_599, 2_100_599), workspace.Project.Lyrics.Lines[0].Tokens[0].Time);
        Assert.Equal(TimingStatus.Edited, workspace.Project.Lyrics.Lines[0].Tokens[0].TimingStatus);
        Assert.Equal(First, Assert.Single(forward.ChangeSet.Targets));
        Assert.Same(source.Lyrics.Lines[0].Tokens[1], workspace.Project.Lyrics.Lines[0].Tokens[1]);
        Assert.Equal(source.Lyrics.Lines[0].Time, workspace.Project.Lyrics.Lines[0].Time);

        Assert.True(LyricTimingEditUseCase.Nudge(workspace, Baseline(workspace, First), -3).Accepted);
        Assert.Equal(source.Lyrics.Lines[0].Tokens[0].Time, workspace.Project.Lyrics.Lines[0].Tokens[0].Time);
        Assert.Same(source, workspace.Undo());
    }

    [Theory]
    [InlineData(-1, 2_000_000, LyricTimingEditErrorCode.InvalidStart)]
    [InlineData(2_000_000, 2_000_000, LyricTimingEditErrorCode.InvalidRange)]
    [InlineData(2_000_001, 2_000_000, LyricTimingEditErrorCode.InvalidRange)]
    [InlineData(1_000_499, 3_000_001, LyricTimingEditErrorCode.DomainValidationFailed)]
    public void 标量设置保留解析区间与领域错误区别且不夹紧(long start, long end, LyricTimingEditErrorCode code)
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(LyricTimingEditUseCaseTests.Sample());
        AssertUnchanged(workspace, () => LyricTimingEditUseCase.SetTime(workspace,
            Request(workspace, First, start, end)), code);
    }

    [Fact]
    public void 微调边界不夹紧并区分未定时零步与邻字冲突()
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(LyricTimingEditUseCaseTests.Sample());
        AssertUnchanged(workspace, () => LyricTimingEditUseCase.Nudge(workspace, Baseline(workspace, First), -31),
            LyricTimingEditErrorCode.NudgeOutOfBounds);
        AssertUnchanged(workspace, () => LyricTimingEditUseCase.Nudge(workspace, Baseline(workspace, First), 1, 2_033_831),
            LyricTimingEditErrorCode.NudgeOutOfBounds);
        AssertUnchanged(workspace, () => LyricTimingEditUseCase.Nudge(workspace, Baseline(workspace, First), 30),
            LyricTimingEditErrorCode.DomainValidationFailed);
        AssertUnchanged(workspace, () => LyricTimingEditUseCase.Nudge(workspace,
            Baseline(workspace, new("primary", "two", "third")), 0), LyricTimingEditErrorCode.Untimed);

        LyricTimingEditResult zero = LyricTimingEditUseCase.Nudge(workspace, Baseline(workspace, First), 0);
        Assert.True(zero.Accepted);
        Assert.False(zero.Changed);
        Assert.False(workspace.IsDirty);
        Assert.Equal(0, workspace.History.UndoCount);
        Assert.True(LyricTimingEditUseCase.Nudge(workspace, Baseline(workspace, First), 1, 2_033_832).Accepted);
        Assert.Equal(2_033_832, workspace.Project.Lyrics.Lines[0].Tokens[0].Time!.Value.EndUs);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void 大整数与帧数极值不溢出或绕回合法时间(int frameDelta)
    {
        const long Start = long.MaxValue - 100;
        Project source = Project.Create("long", lyrics: LyricsDocument.Create([
            LyricLine.Create("one", "甲", TimeRange.Create(Start - 1, long.MaxValue), TimingStatus.Imported, tokens: [
                LyricToken.Create("first", "甲", TimeRange.Create(Start, Start + 1), TimingStatus.Imported)]) ]));
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        AssertUnchanged(workspace, () => LyricTimingEditUseCase.Nudge(workspace, Baseline(workspace, First), frameDelta),
            frameDelta < 0 ? LyricTimingEditErrorCode.DomainValidationFailed : LyricTimingEditErrorCode.NudgeOutOfBounds);
    }

    [Fact]
    public void 大于双精度边界的标量设置与微调全程保留整数()
    {
        const long Start = 9_007_199_254_740_993;
        Project source = Project.Create("精确", lyrics: LyricsDocument.Create([
            LyricLine.Create("one", "甲", TimeRange.Create(Start, Start + 1_000_000), TimingStatus.Imported, tokens: [
                LyricToken.Create("first", "甲", TimeRange.Create(Start + 7, Start + 100_007), TimingStatus.Imported)]) ]));
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        Assert.True(LyricTimingEditUseCase.SetTime(workspace, Request(workspace, First, null, Start + 200_009)).Accepted);
        Assert.Equal(Start + 7, workspace.Project.Lyrics.Lines[0].Tokens[0].Time!.Value.StartUs);
        Assert.True(LyricTimingEditUseCase.Nudge(workspace, Baseline(workspace, First), 1).Accepted);
        Assert.Equal(TimeRange.Create(Start + 33_340, Start + 233_342), workspace.Project.Lyrics.Lines[0].Tokens[0].Time);
    }

    [Fact]
    public void 清除只改明确元素时间状态且一步历史通知可撤销重做()
    {
        Project source = LyricTimingEditUseCaseTests.Sample();
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        int notifications = 0;
        workspace.History.Changed += (_, _) => notifications++;
        LyricTimingEditResult result = LyricTimingEditUseCase.ClearToken(workspace, Baseline(workspace, First));

        Assert.True(result.Accepted);
        Assert.True(result.Changed);
        Assert.Null(workspace.Project.Lyrics.Lines[0].Tokens[0].Time);
        Assert.Equal(TimingStatus.Missing, workspace.Project.Lyrics.Lines[0].Tokens[0].TimingStatus);
        Assert.Equal(source.Lyrics.Lines[0].Time, workspace.Project.Lyrics.Lines[0].Time);
        Assert.Same(source.Lyrics.Lines[0].Tokens[1], workspace.Project.Lyrics.Lines[0].Tokens[1]);
        Assert.Equal(First, Assert.Single(result.ChangeSet.Targets));
        Assert.True(result.ChangeSet.InvalidatesPreview);
        Assert.Equal("token.time.clear", workspace.UndoName);
        Assert.Equal(1, workspace.History.UndoCount);
        Assert.Equal(1, notifications);
        Assert.Same(source, workspace.Undo());
        Assert.False(workspace.IsDirty);
        Assert.Same(result.Project, workspace.Redo());
        Assert.Equal(3, notifications);
    }

    [Fact]
    public void 清除或设置的无变化保留原状态引用和撤销后重做栈()
    {
        Project source = LyricTimingEditUseCaseTests.Sample();
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        Project changed = LyricTimingEditUseCase.SetTime(workspace, Request(workspace, First, null, 2_100_000)).Project;
        workspace.Undo();
        int notifications = 0;
        workspace.History.Changed += (_, _) => notifications++;
        Assert.True(LyricTimingEditUseCase.SetTime(workspace, Request(workspace, First, null, null)).Accepted);
        LyricTimingEditResult clear = LyricTimingEditUseCase.ClearToken(workspace,
            Baseline(workspace, new("primary", "two", "third")));

        Assert.True(clear.Accepted);
        Assert.False(clear.Changed);
        Assert.Same(ProjectTimingChangeSet.None, clear.ChangeSet);
        Assert.Same(source, workspace.Project);
        Assert.False(workspace.IsDirty);
        Assert.Equal(TimingStatus.Imported, workspace.Project.Lyrics.Lines[0].Tokens[0].TimingStatus);
        Assert.Equal(0, workspace.History.UndoCount);
        Assert.Equal(1, workspace.History.RedoCount);
        Assert.Equal(0, notifications);
        Assert.Same(changed, workspace.Redo());
    }

    [Fact]
    public void 无时间但仍有估算状态的元素清除会修正状态且不是空操作()
    {
        Project source = Project.Create("状态", lyrics: LyricsDocument.Create([
            LyricLine.Create("one", "甲", TimeRange.Create(1_000_000, 5_000_000), TimingStatus.Imported, tokens: [
                LyricToken.Create("first", "甲") with { TimingStatus = TimingStatus.Estimated }]) ]));
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        LyricTimingEditResult result = LyricTimingEditUseCase.ClearToken(workspace, Baseline(workspace, First));
        Assert.True(result.Accepted);
        Assert.True(result.Changed);
        Assert.Null(workspace.Project.Lyrics.Lines[0].Tokens[0].Time);
        Assert.Equal(TimingStatus.Missing, workspace.Project.Lyrics.Lines[0].Tokens[0].TimingStatus);
        Assert.Equal(1, workspace.History.UndoCount);
        Assert.Equal(First, Assert.Single(result.ChangeSet.Targets));
        Assert.Same(source, workspace.Undo());
    }

    [Fact]
    public void 标量微调清除均拒绝同标识新工作区和旧时间()
    {
        ProjectWorkspace original = ProjectWorkspace.Adopt(LyricTimingEditUseCaseTests.Sample());
        LyricTimingEditBaseline baseline = Baseline(original, First);
        ProjectWorkspace replacement = ProjectWorkspace.Adopt(original.Project);
        AssertUnchanged(replacement, () => LyricTimingEditUseCase.SetTime(replacement,
            new(original, original.Generation, First, baseline.ExpectedTime, null, 2_100_000)), LyricTimingEditErrorCode.WorkspaceStale);
        AssertUnchanged(replacement, () => LyricTimingEditUseCase.Nudge(replacement, baseline, 1), LyricTimingEditErrorCode.WorkspaceStale);
        AssertUnchanged(replacement, () => LyricTimingEditUseCase.ClearToken(replacement, baseline), LyricTimingEditErrorCode.WorkspaceStale);
        Assert.True(LyricTimingEditUseCase.Nudge(original, baseline, 1).Accepted);
        AssertUnchanged(original, () => LyricTimingEditUseCase.ClearToken(original, baseline), LyricTimingEditErrorCode.TargetStale);
        AssertUnchanged(original, () => LyricTimingEditUseCase.Nudge(original, baseline, 1), LyricTimingEditErrorCode.TargetStale);
        AssertUnchanged(original, () => LyricTimingEditUseCase.SetTime(original,
            new(original, original.Generation, First, baseline.ExpectedTime, null, 2_100_000)), LyricTimingEditErrorCode.TargetStale);
        original.StartNew(LyricTimingEditUseCaseTests.Sample());
        AssertUnchanged(original, () => LyricTimingEditUseCase.ClearToken(original, baseline), LyricTimingEditErrorCode.WorkspaceStale);
    }

    [Fact]
    public void 批次非法中间状态拒绝整批且重复目标沿原顺序处理()
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(LyricTimingEditUseCaseTests.Sample());
        var first = Change(workspace, First, 1_100_001, 3_200_000);
        var second = Change(workspace, Second, 3_200_000, 4_100_000);
        LyricTimingTokenBatchRequest request = new(workspace, workspace.Generation, [first, second]);
        AssertUnchanged(workspace, () => LyricTimingEditUseCase.CommitTokenBatch(workspace, request),
            LyricTimingEditErrorCode.DomainValidationFailed);
        LyricTimingEditResult accepted = LyricTimingEditUseCase.CommitTokenBatch(workspace,
            request with { Changes = [second, first] });
        Assert.True(accepted.Accepted);
        Assert.Equal(2, accepted.ChangeSet.Targets.Length);
        Assert.Equal(1, workspace.History.UndoCount);
        Assert.Equal(TimeRange.Create(1_100_001, 3_200_000), workspace.Project.Lyrics.Lines[0].Tokens[0].Time);
        Assert.Equal(TimeRange.Create(3_200_000, 4_100_000), workspace.Project.Lyrics.Lines[0].Tokens[1].Time);

        workspace.Undo();
        LyricTimingEditResult repeated = LyricTimingEditUseCase.CommitTokenBatch(workspace,
            request with { Changes = [first with { EndUs = 2_100_001 }, first with { EndUs = 2_200_003 }] });
        Assert.True(repeated.Accepted);
        Assert.Equal(TimeRange.Create(1_100_001, 2_200_003), workspace.Project.Lyrics.Lines[0].Tokens[0].Time);
        Assert.Equal(First, Assert.Single(repeated.ChangeSet.Targets));
        Assert.Equal(1, workspace.History.UndoCount);
    }

    [Fact]
    public void 批次失效末目标不提交已合法的首目标并且不接受行目标()
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(LyricTimingEditUseCaseTests.Sample());
        var first = Change(workspace, First, 1_100_001, 2_100_001);
        var missing = new LyricTimingTokenTimeChange(new("primary", "one", "gone"), null, 3_100_000, 4_100_000);
        LyricTimingTokenBatchRequest request = new(workspace, workspace.Generation, [first, missing]);
        AssertUnchanged(workspace, () => LyricTimingEditUseCase.CommitTokenBatch(workspace, request), LyricTimingEditErrorCode.TargetStale);
        AssertUnchanged(workspace, () => LyricTimingEditUseCase.CommitTokenBatch(workspace,
            request with { Changes = [first with { Target = new("primary", "one") }] }), LyricTimingEditErrorCode.TokenRequired);
        AssertUnchanged(workspace, () => LyricTimingEditUseCase.CommitTokenBatch(workspace,
            request with { Changes = [first with { ExpectedTime = TimeRange.Create(1_000_000, 2_000_000) }] }), LyricTimingEditErrorCode.TargetStale);
    }

    [Fact]
    public void 批次空或相同值无通知保留导入状态且同ID新工作区仍拒绝请求()
    {
        Project source = LyricTimingEditUseCaseTests.Sample();
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        int notifications = 0;
        workspace.History.Changed += (_, _) => notifications++;
        LyricTimingTokenBatchRequest request = new(workspace, workspace.Generation,
            [Change(workspace, First, 1_000_499, 2_000_499)]);
        LyricTimingEditResult empty = LyricTimingEditUseCase.CommitTokenBatch(workspace, request with { Changes = [] });
        LyricTimingEditResult unchanged = LyricTimingEditUseCase.CommitTokenBatch(workspace, request);
        Assert.True(empty.Accepted);
        Assert.True(unchanged.Accepted);
        Assert.False(empty.Changed);
        Assert.False(unchanged.Changed);
        Assert.Same(source, workspace.Project);
        Assert.False(workspace.IsDirty);
        Assert.Equal(0, notifications);
        Assert.Equal(0, workspace.History.UndoCount);
        Assert.Equal(TimingStatus.Imported, workspace.Project.Lyrics.Lines[0].Tokens[0].TimingStatus);
        ProjectWorkspace replacement = ProjectWorkspace.Adopt(source);
        AssertUnchanged(replacement, () => LyricTimingEditUseCase.CommitTokenBatch(replacement, request), LyricTimingEditErrorCode.WorkspaceStale);
    }

    [Theory]
    [InlineData("branch", "one", "first")]
    [InlineData("primary", "gone", "first")]
    [InlineData("primary", "two", "first")]
    [InlineData("primary", "one", "gone")]
    public void 标量微调清除均拒绝失效稳定目标而不回退下标(string branch, string line, string token)
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(LyricTimingEditUseCaseTests.Sample());
        LyricTimingEditBaseline baseline = new(workspace, workspace.Generation, new(branch, line, token), null);
        AssertUnchanged(workspace, () => LyricTimingEditUseCase.SetTime(workspace,
            new(workspace, workspace.Generation, baseline.Target, null, 1_100_001, 2_100_001)), LyricTimingEditErrorCode.TargetStale);
        AssertUnchanged(workspace, () => LyricTimingEditUseCase.Nudge(workspace, baseline, 1), LyricTimingEditErrorCode.TargetStale);
        AssertUnchanged(workspace, () => LyricTimingEditUseCase.ClearToken(workspace, baseline), LyricTimingEditErrorCode.TargetStale);
    }

    [Fact]
    public void 媒体边界标量检查但旧批次保持只按行包含性校验()
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(LyricTimingEditUseCaseTests.Sample());
        AssertUnchanged(workspace, () => LyricTimingEditUseCase.SetTime(workspace,
            Request(workspace, Second, null, 4_100_003) with { ContentDurationUs = 4_100_002 }), LyricTimingEditErrorCode.BeyondContentDuration);
        Assert.True(LyricTimingEditUseCase.CommitTokenBatch(workspace,
            new(workspace, workspace.Generation, [Change(workspace, Second, 3_000_000, 4_100_003)])).Accepted);
        Assert.Equal(TimeRange.Create(3_000_000, 4_100_003), workspace.Project.Lyrics.Lines[0].Tokens[1].Time);
    }

    [Fact]
    public void 副来源同名目标独立且FollowPrimary保留已有直接调用语义()
    {
        Project source = LyricTimingEditUseCaseTests.Sample();
        LyricBranch branch = LyricBranch.Create("translation", "译", LyricBranchRole.Translation,
            [LyricLine.Create("one", "甲乙", TimeRange.Create(1_000_000, 5_000_000), TimingStatus.Imported, tokens: [
                LyricToken.Create("first", "甲", TimeRange.Create(1_000_499, 2_000_499), TimingStatus.Imported)]) ],
            timingMode: LyricBranchTimingMode.FollowPrimary);
        source = source with { Lyrics = source.Lyrics with { Branches = source.Lyrics.Branches.Add(branch) } };
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        LyricTimingEditTarget translated = new("translation", "one", "first");
        Assert.True(LyricTimingEditUseCase.Nudge(workspace, Baseline(workspace, First), 1).Accepted);
        Project primaryChanged = workspace.Project;
        Assert.True(LyricTimingEditUseCase.Nudge(workspace, Baseline(workspace, translated), 1).Accepted);
        Assert.Equal(2, workspace.History.UndoCount);
        Assert.Equal("primary", workspace.Project.Lyrics.PrimaryBranchId);
        Assert.Same(primaryChanged.Lyrics.PrimaryBranch, workspace.Project.Lyrics.PrimaryBranch);
        Assert.True(LyricTimingEditUseCase.ClearToken(workspace, Baseline(workspace, translated)).Accepted);
        Assert.Null(workspace.Project.Lyrics.Branches[1].Lines[0].Tokens[0].Time);
        LyricBranch primary = Assert.IsType<LyricBranch>(workspace.Project.Lyrics.PrimaryBranch);
        Assert.NotNull(primary.Lines[0].Tokens[0].Time);
    }

    [Fact]
    public void 显式打点短组仍合并时间和状态且结束后不吞其他命令()
    {
        Project source = LyricTimingEditUseCaseTests.Sample();
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        using (workspace.BeginGroup("token.stamp"))
        {
            Assert.True(LyricTimingEditUseCase.SetTime(workspace, Request(workspace, First, null, 2_100_001)).Accepted);
            workspace.Execute(ProjectCommand.Create("token.status", project =>
            {
                var line = project.Lyrics.Lines[0];
                var estimated = line.Tokens[0] with { TimingStatus = TimingStatus.Estimated };
                return project with { Lyrics = project.Lyrics with
                    { Lines = project.Lyrics.Lines.SetItem(0, line with { Tokens = line.Tokens.SetItem(0, estimated) }) } };
            }));
        }
        Project stamp = workspace.Project;
        Assert.Equal("token.stamp", workspace.UndoName);
        Assert.Equal(TimingStatus.Estimated, workspace.Project.Lyrics.Lines[0].Tokens[0].TimingStatus);
        Assert.Equal(1, workspace.History.UndoCount);
        Assert.True(LyricTimingEditUseCase.SetTime(workspace, Request(workspace, First, null, 2_200_003)).Accepted);
        Assert.Equal(2, workspace.History.UndoCount);
        Assert.Same(stamp, workspace.Undo());
        Assert.Same(source, workspace.Undo());
    }

    [Fact]
    public void 旧公开同名命令与新带身份端点命令不互相吞并()
    {
        Project source = LyricTimingEditUseCaseTests.Sample();
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        workspace.Execute(ProjectCommand.Create("token.time", project => project with { Name = "旧入口之前" }, true));
        Project legacyBefore = workspace.Project;
        Assert.True(LyricTimingEditUseCase.SetTime(workspace, Request(workspace, First, null, 2_100_001)).Accepted);
        Project timing = workspace.Project;
        workspace.Execute(ProjectCommand.Create("token.time", project => project with { Name = "旧入口之后" }, true));

        Assert.Equal(3, workspace.History.UndoCount);
        Assert.Same(timing, workspace.Undo());
        Assert.Same(legacyBefore, workspace.Undo());
        Assert.Same(source, workspace.Undo());
    }

    [Fact]
    public void 当前工程与栈顶After不一致时共享端点不得并入旧历史()
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(LyricTimingEditUseCaseTests.Sample());
        Assert.True(LyricTimingEditUseCase.SetTime(workspace, Request(workspace, First, null, 2_100_001)).Accepted);
        Project replaced = workspace.Project with { Name = "未经过历史的替换" };
        workspace.Replace(replaced);
        Assert.True(LyricTimingEditUseCase.SetTime(workspace, Request(workspace, First, null, 2_200_003)).Accepted);
        Assert.Equal(2, workspace.History.UndoCount);
        Assert.Same(replaced, workspace.Undo());
    }

    internal static LyricTimingEditBaseline Baseline(ProjectWorkspace workspace, LyricTimingEditTarget target)
    {
        Assert.True(LyricTimingEditUseCase.TryReadTime(workspace.Project, target, out TimeRange? time));
        return new(workspace, workspace.Generation, target, time);
    }

    internal static LyricTimingEditRequest Request(ProjectWorkspace workspace, LyricTimingEditTarget target, long? start, long? end)
    {
        var baseline = Baseline(workspace, target);
        return new(workspace, workspace.Generation, target, baseline.ExpectedTime, start, end);
    }

    private static LyricTimingTokenTimeChange Change(ProjectWorkspace workspace, LyricTimingEditTarget target, long start, long end) =>
        new(target, Baseline(workspace, target).ExpectedTime, start, end);

    internal static void AssertUnchanged(ProjectWorkspace workspace, Func<LyricTimingEditResult> operation, LyricTimingEditErrorCode code)
    {
        Project source = workspace.Project;
        int undo = workspace.History.UndoCount, redo = workspace.History.RedoCount;
        bool dirty = workspace.IsDirty;
        int notifications = 0;
        EventHandler handler = (_, _) => notifications++;
        workspace.History.Changed += handler;
        LyricTimingEditResult result;
        try { result = operation(); }
        finally { workspace.History.Changed -= handler; }
        Assert.False(result.Accepted);
        Assert.False(result.Changed);
        Assert.Equal(code, result.Error!.Code);
        Assert.Same(source, result.Project);
        Assert.Same(source, workspace.Project);
        Assert.Same(ProjectTimingChangeSet.None, result.ChangeSet);
        Assert.Equal(undo, workspace.History.UndoCount);
        Assert.Equal(redo, workspace.History.RedoCount);
        Assert.Equal(dirty, workspace.IsDirty);
        Assert.Equal(0, notifications);
    }
}
