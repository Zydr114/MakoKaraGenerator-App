using MakoKaraGenerator.Application;
using MakoKaraGenerator.Application.Editing;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Application.Tests;

public sealed class LyricTimingStampTests
{
    private static readonly LyricTimingEditTarget Untimed = new("primary", "two", "third");
    private static readonly LyricTimingEditTarget Timed = new("primary", "one", "first");

    [Fact]
    public void 起点打点为未定时元素补一帧估算并一次撤销恢复()
    {
        Project source = LyricTimingEditUseCaseTests.Sample();
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        LyricTimingEditBaseline baseline = Baseline(workspace, Untimed);
        int notifications = 0;
        workspace.History.Changed += (_, _) => notifications++;

        LyricTimingStampResult result = LyricTimingEditUseCase.StampTokenStart(
            workspace, baseline, playheadUs: 6_500_001, contentDurationUs: 8_000_000);

        Assert.True(result.Accepted, $"{result.Error?.Code}: {result.Error?.Detail}");
        Assert.True(result.Changed);
        Assert.True(result.EstimatedPlaceholder);
        Assert.Equal(TimeRange.Create(6_500_001, 6_533_334),
            workspace.Project.Lyrics.Lines[1].Tokens[0].Time);
        Assert.Equal(TimingStatus.Estimated, workspace.Project.Lyrics.Lines[1].Tokens[0].TimingStatus);
        Assert.Equal(Untimed, Assert.Single(result.ChangeSet.Targets));
        Assert.Equal("token.stamp", workspace.UndoName);
        Assert.Equal(1, workspace.History.UndoCount);
        Assert.Equal(1, notifications);

        Assert.Same(source, workspace.Undo());
        Assert.Null(workspace.Project.Lyrics.Lines[1].Tokens[0].Time);
        Assert.Same(result.Project, workspace.Redo());
        Assert.Equal(TimingStatus.Estimated, workspace.Project.Lyrics.Lines[1].Tokens[0].TimingStatus);
        Assert.Equal(3, notifications);
    }

    [Fact]
    public void 未知媒体时长只保证播放头非负不抹掉时间点()
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(LyricTimingEditUseCaseTests.Sample());

        LyricTimingStampResult result = LyricTimingEditUseCase.StampTokenStart(
            workspace, Baseline(workspace, Untimed), playheadUs: 6_500_001, contentDurationUs: 0);

        Assert.True(result.Accepted, $"{result.Error?.Code}: {result.Error?.Detail}");
        Assert.Equal(TimeRange.Create(6_500_001, 6_533_334),
            workspace.Project.Lyrics.Lines[1].Tokens[0].Time);
    }

    [Fact]
    public void 起点打点保留已有终点并标为已修改()
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(LyricTimingEditUseCaseTests.Sample());

        LyricTimingStampResult result = LyricTimingEditUseCase.StampTokenStart(
            workspace, Baseline(workspace, Timed), playheadUs: 1_500_001, contentDurationUs: 8_000_000);

        Assert.True(result.Accepted, $"{result.Error?.Code}: {result.Error?.Detail}");
        Assert.False(result.EstimatedPlaceholder);
        Assert.Equal(TimeRange.Create(1_500_001, 2_000_499),
            workspace.Project.Lyrics.Lines[0].Tokens[0].Time);
        Assert.Equal(TimingStatus.Edited, workspace.Project.Lyrics.Lines[0].Tokens[0].TimingStatus);
        Assert.Equal(1, workspace.History.UndoCount);
    }

    [Fact]
    public void 终点打点需要已有起点并把估算状态变为已修改()
    {
        Project source = LyricTimingEditUseCaseTests.Sample();
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        LyricTimingStampResult missing = LyricTimingEditUseCase.StampTokenEnd(
            workspace, Baseline(workspace, Untimed), playheadUs: 7_000_000, contentDurationUs: 8_000_000);
        Assert.False(missing.Accepted);
        Assert.Equal(LyricTimingEditErrorCode.Untimed, missing.Error!.Code);
        Assert.Same(source, workspace.Project);
        Assert.Equal(0, workspace.History.UndoCount);

        LyricTimingStampResult start = LyricTimingEditUseCase.StampTokenStart(
            workspace, Baseline(workspace, Untimed), playheadUs: 6_500_001, contentDurationUs: 8_000_000);
        Assert.True(start.Accepted, $"{start.Error?.Code}: {start.Error?.Detail}");
        LyricTimingStampResult end = LyricTimingEditUseCase.StampTokenEnd(
            workspace, Baseline(workspace, Untimed), playheadUs: 6_700_001, contentDurationUs: 8_000_000);

        Assert.True(end.Accepted);
        Assert.False(end.EstimatedPlaceholder);
        Assert.Equal(TimeRange.Create(6_500_001, 6_700_001),
            workspace.Project.Lyrics.Lines[1].Tokens[0].Time);
        Assert.Equal(TimingStatus.Edited, workspace.Project.Lyrics.Lines[1].Tokens[0].TimingStatus);
        Assert.Equal(2, workspace.History.UndoCount);
        Assert.Same(start.Project, workspace.Undo());
        Assert.Same(source, workspace.Undo());
    }

    [Fact]
    public void 终点恰好落在估算占位末端也确认来源状态()
    {
        Project source = LyricTimingEditUseCaseTests.Sample();
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        LyricTimingStampResult start = LyricTimingEditUseCase.StampTokenStart(
            workspace, Baseline(workspace, Untimed), playheadUs: 6_500_001, contentDurationUs: 8_000_000);
        Assert.True(start.Accepted);
        LyricTimingEditBaseline baseline = Baseline(workspace, Untimed);
        LyricTimingStampResult end = LyricTimingEditUseCase.StampTokenEnd(
            workspace, baseline, playheadUs: 6_533_334, contentDurationUs: 8_000_000);

        Assert.True(end.Accepted);
        Assert.True(end.Changed);
        Assert.Equal(TimingStatus.Edited, workspace.Project.Lyrics.Lines[1].Tokens[0].TimingStatus);
        Assert.Equal(2, workspace.History.UndoCount);
    }

    [Fact]
    public void 终点早于起点和媒体边界拒绝且不写历史()
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(LyricTimingEditUseCaseTests.Sample());
        LyricTimingEditBaseline baseline = Baseline(workspace, Timed);
        LyricTimingStampResult early = LyricTimingEditUseCase.StampTokenEnd(
            workspace, baseline, playheadUs: 1_000_499);
        Assert.False(early.Accepted);
        Assert.Equal(LyricTimingEditErrorCode.InvalidRange, early.Error!.Code);
        Assert.Equal(0, workspace.History.UndoCount);

        LyricTimingStampResult beyond = LyricTimingEditUseCase.StampTokenStart(
            workspace, Baseline(workspace, Untimed), playheadUs: 6_500_001, contentDurationUs: 6_500_001);
        Assert.False(beyond.Accepted);
        Assert.Equal(LyricTimingEditErrorCode.BeyondContentDuration, beyond.Error!.Code);
        Assert.Equal(0, workspace.History.UndoCount);
    }

    [Fact]
    public void 起点打点的旧工作区和稳定原值失效时拒绝()
    {
        ProjectWorkspace original = ProjectWorkspace.Adopt(LyricTimingEditUseCaseTests.Sample());
        LyricTimingEditBaseline baseline = Baseline(original, Timed);
        ProjectWorkspace replacement = ProjectWorkspace.Adopt(original.Project);
        LyricTimingStampResult otherWorkspace = LyricTimingEditUseCase.StampTokenStart(
            replacement, baseline, playheadUs: 6_500_001, contentDurationUs: 8_000_000);
        Assert.False(otherWorkspace.Accepted);
        Assert.Equal(LyricTimingEditErrorCode.WorkspaceStale, otherWorkspace.Error!.Code);

        Assert.True(LyricTimingEditUseCase.ClearToken(original, baseline).Accepted);
        LyricTimingStampResult stale = LyricTimingEditUseCase.StampTokenStart(
            original, baseline, playheadUs: 6_500_001, contentDurationUs: 8_000_000);
        Assert.False(stale.Accepted);
        Assert.Equal(LyricTimingEditErrorCode.TargetStale, stale.Error!.Code);
    }

    private static LyricTimingEditBaseline Baseline(ProjectWorkspace workspace, LyricTimingEditTarget target)
    {
        Assert.True(LyricTimingEditUseCase.TryReadTime(workspace.Project, target, out TimeRange? time));
        return new(workspace, workspace.Generation, target, time);
    }
}
