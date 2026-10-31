using MakoKaraGenerator.Application.Editing;
using MakoKaraGenerator.Core.Commands;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Application.Tests;

public sealed class LyricTimingGestureTests
{
    private static readonly LyricTimingEditTarget First = new("primary", "one", "first");
    private static readonly LyricTimingEditTarget Second = new("primary", "one", "second");

    [Fact]
    public void 同一拖动中停顿超过原时间窗仍即时提交且只撤销一次()
    {
        Project source = LyricTimingEditUseCaseTests.Sample();
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        int notifications = 0;
        workspace.History.Changed += (_, _) => notifications++;
        using LyricTimingEditGesture gesture = Begin(workspace, First);
        LyricTimingEditResult first = gesture.Update(null, 2_100_001);
        Assert.True(first.Accepted);
        Assert.Same(first.Project, workspace.Project);
        Assert.Equal(TimeRange.Create(1_000_499, 2_100_001), workspace.Project.Lyrics.Lines[0].Tokens[0].Time);
        Assert.Equal(1, workspace.History.UndoCount);
        Assert.False(workspace.History.IsInGroup);

        // 回归真实旧缺陷：900ms 窗口外仍是同一次未结束的指针拖动。
        Thread.Sleep((int)ProjectHistory.DefaultMergeWindowMs + 50);
        LyricTimingEditResult final = gesture.Update(null, 2_200_003);
        Assert.True(final.Accepted);
        Assert.True(final.Changed);
        Assert.Equal(First, Assert.Single(final.ChangeSet.Targets));
        gesture.End();
        Assert.False(gesture.IsActive);
        Assert.Equal(1, workspace.History.UndoCount);
        Assert.Equal("token.time", workspace.UndoName);
        Assert.Equal(1, notifications);
        Assert.Same(source, workspace.Undo());
        Assert.False(workspace.IsDirty);
        Assert.Same(final.Project, workspace.Redo());
        Assert.Equal(3, notifications);
    }

    [Fact]
    public void 行拖动只改行时间并在结束时保留即时结果()
    {
        Project source = LyricTimingEditUseCaseTests.Sample();
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        using LyricTimingEditGesture gesture = Begin(workspace, new("primary", "one"));
        Assert.True(gesture.Update(900_001, null).Accepted);
        LyricTimingEditResult result = gesture.Update(800_003, 5_100_007);
        gesture.End();

        Assert.True(result.Accepted);
        Assert.Equal(TimeRange.Create(800_003, 5_100_007), workspace.Project.Lyrics.Lines[0].Time);
        Assert.Equal(source.Lyrics.Lines[0].Tokens, workspace.Project.Lyrics.Lines[0].Tokens);
        Assert.Equal(TimingStatus.Edited, workspace.Project.Lyrics.Lines[0].TimingStatus);
        Assert.Null(Assert.Single(result.ChangeSet.Targets).TokenId);
        Assert.Equal(1, workspace.History.UndoCount);
        Assert.Same(source, workspace.Undo());
        Assert.Same(result.Project, workspace.Redo());
    }

    [Fact]
    public void 开始无变化移动失败移动和结束都不吃撤销后的重做()
    {
        Project source = LyricTimingEditUseCaseTests.Sample();
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        Project edited = LyricTimingEditUseCase.SetTime(workspace,
            LyricTimingActionsTests.Request(workspace, First, null, 2_100_001)).Project;
        workspace.Undo();
        int notifications = 0;
        workspace.History.Changed += (_, _) => notifications++;
        using LyricTimingEditGesture gesture = Begin(workspace, First);
        Assert.Equal(0, workspace.History.UndoCount);
        Assert.Equal(1, workspace.History.RedoCount);
        LyricTimingEditResult unchanged = gesture.Update(null, null);
        Assert.True(unchanged.Accepted);
        Assert.False(unchanged.Changed);
        Assert.True(gesture.IsActive);
        LyricTimingActionsTests.AssertUnchanged(workspace, () => gesture.Update(-1, 2_100_001), LyricTimingEditErrorCode.InvalidStart);
        Assert.True(gesture.IsActive);
        gesture.End();

        Assert.Same(source, workspace.Project);
        Assert.False(workspace.IsDirty);
        Assert.Equal(TimingStatus.Imported, workspace.Project.Lyrics.Lines[0].Tokens[0].TimingStatus);
        Assert.Equal(0, notifications);
        Assert.Equal(1, workspace.History.RedoCount);
        Assert.Same(edited, workspace.Redo());
    }

    [Fact]
    public void 无变化手势移动不结束身份且领域错误后仍可修正()
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(LyricTimingEditUseCaseTests.Sample());
        using LyricTimingEditGesture gesture = Begin(workspace, First);
        Assert.True(gesture.Update(null, 2_100_001).Accepted);
        LyricTimingEditResult unchanged = gesture.Update(null, null);
        Assert.True(unchanged.Accepted);
        Assert.False(unchanged.Changed);
        Assert.True(gesture.IsActive);
        LyricTimingActionsTests.AssertUnchanged(workspace, () => gesture.Update(null, 3_100_000),
            LyricTimingEditErrorCode.DomainValidationFailed);
        Assert.True(gesture.IsActive);
        Assert.True(gesture.Update(null, 2_200_003).Accepted);
        Assert.Equal(1, workspace.History.UndoCount);
    }

    [Fact]
    public void 历史通知中的只读IsActive观察不使正在接受自身更新的手势失效()
    {
        Project source = LyricTimingEditUseCaseTests.Sample();
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        using LyricTimingEditGesture gesture = Begin(workspace, First);
        bool? observedActive = null;
        int notifications = 0;
        workspace.History.Changed += (_, _) =>
        {
            notifications++;
            observedActive = gesture.IsActive;
        };

        LyricTimingEditResult first = gesture.Update(null, 2_100_001);
        Assert.True(first.Accepted);
        Assert.True(observedActive);
        Assert.True(gesture.IsActive);
        LyricTimingEditResult second = gesture.Update(null, 2_200_003);
        Assert.True(second.Accepted);
        Assert.True(gesture.IsActive);
        Assert.Equal(1, notifications);
        Assert.Equal(1, workspace.History.UndoCount);
        gesture.End();
        Assert.Same(source, workspace.Undo());
        Assert.False(observedActive);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 历史通知中显式结束仍生效且接受结果不复活已结束手势(bool dispose)
    {
        Project source = LyricTimingEditUseCaseTests.Sample();
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        using LyricTimingEditGesture gesture = Begin(workspace, First);
        workspace.History.Changed += (_, _) =>
        {
            if (dispose) gesture.Dispose(); else gesture.End();
        };

        LyricTimingEditResult result = gesture.Update(null, 2_100_001);
        Assert.True(result.Accepted);
        Assert.Same(result.Project, workspace.Project);
        Assert.False(gesture.IsActive);
        LyricTimingActionsTests.AssertUnchanged(workspace, () => gesture.Update(null, 2_200_003), LyricTimingEditErrorCode.GestureStale);
        Assert.Equal(1, workspace.History.UndoCount);
        Assert.Same(source, workspace.Undo());
    }

    [Fact]
    public void 同目标两次拖动与微调按动作边界分别撤销()
    {
        Project source = LyricTimingEditUseCaseTests.Sample();
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        Assert.True(LyricTimingEditUseCase.Nudge(workspace, LyricTimingActionsTests.Baseline(workspace, First), 1).Accepted);
        Project nudge = workspace.Project;
        using (LyricTimingEditGesture first = Begin(workspace, First))
            Assert.True(first.Update(null, 2_100_001).Accepted);
        Project drag1 = workspace.Project;
        using (LyricTimingEditGesture second = Begin(workspace, First))
            Assert.True(second.Update(null, 2_200_003).Accepted);
        Project drag2 = workspace.Project;
        Assert.True(LyricTimingEditUseCase.Nudge(workspace, LyricTimingActionsTests.Baseline(workspace, First), 1).Accepted);

        Assert.Equal(4, workspace.History.UndoCount);
        Assert.Same(drag2, workspace.Undo());
        Assert.Same(drag1, workspace.Undo());
        Assert.Same(nudge, workspace.Undo());
        Assert.Same(source, workspace.Undo());
    }

    [Theory]
    [InlineData("rename")]
    [InlineData("sameNameLegacy")]
    [InlineData("sharedNudge")]
    public void 手势中插入无关命令不被吞并且旧拖动永久失效(string operation)
    {
        Project source = LyricTimingEditUseCaseTests.Sample();
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        using LyricTimingEditGesture gesture = Begin(workspace, First);
        Assert.True(gesture.Update(null, 2_100_001).Accepted);
        Project drag = workspace.Project;
        if (operation == "sharedNudge")
            Assert.True(LyricTimingEditUseCase.Nudge(workspace, LyricTimingActionsTests.Baseline(workspace, Second), 1).Accepted);
        else
            workspace.Execute(ProjectCommand.Create(operation == "rename" ? "rename" : "token.time",
                project => project with { Name = "无关变化" }, true));
        Assert.Equal(2, workspace.History.UndoCount);
        Assert.False(gesture.IsActive);
        LyricTimingActionsTests.AssertUnchanged(workspace, () => gesture.Update(null, 2_200_003), LyricTimingEditErrorCode.GestureStale);
        Assert.Same(drag, workspace.Undo());
        Assert.False(gesture.IsActive);
        LyricTimingActionsTests.AssertUnchanged(workspace, () => gesture.Update(null, 2_200_003), LyricTimingEditErrorCode.GestureStale);
        Assert.Same(source, workspace.Undo());
    }

    [Theory]
    [InlineData("undo")]
    [InlineData("undoRedo")]
    [InlineData("externalUndo")]
    [InlineData("break")]
    [InlineData("clear")]
    [InlineData("replaceBack")]
    [InlineData("group")]
    [InlineData("new")]
    public void 历史或工作区边界后即使回同工程引用也不能复活手势(string boundary)
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(LyricTimingEditUseCaseTests.Sample());
        using LyricTimingEditGesture gesture = Begin(workspace, First);
        Assert.True(gesture.Update(null, 2_100_001).Accepted);
        Project captured = workspace.Project;
        switch (boundary)
        {
            case "undo": workspace.Undo(); break;
            case "undoRedo": workspace.Undo(); Assert.Same(captured, workspace.Redo()); break;
            case "externalUndo":
                workspace.Execute(ProjectCommand.Create("rename", project => project with { Name = "变化" }));
                Assert.Same(captured, workspace.Undo()); break;
            case "break": workspace.History.BreakMergeGroup(); break;
            case "clear": workspace.History.Clear(); break;
            case "replaceBack":
                workspace.Replace(captured with { Name = "临时替换" });
                workspace.Replace(captured); break;
            case "group": using (workspace.BeginGroup("empty")) { } break;
            case "new": workspace.StartNew(LyricTimingEditUseCaseTests.Sample()); break;
        }

        Assert.False(gesture.IsActive);
        LyricTimingActionsTests.AssertUnchanged(workspace, () => gesture.Update(null, 2_200_003), LyricTimingEditErrorCode.GestureStale);
        gesture.End();
        Assert.False(gesture.IsActive);
    }

    [Fact]
    public void 新手势开始但尚未更新时旧手势迟到结束不得断开新身份()
    {
        Project source = LyricTimingEditUseCaseTests.Sample();
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        using LyricTimingEditGesture oldGesture = Begin(workspace, First);
        Assert.True(oldGesture.Update(null, 2_100_001).Accepted);
        Project oldDrag = workspace.Project;
        using LyricTimingEditGesture newer = Begin(workspace, Second);
        Assert.Equal(1, workspace.History.UndoCount);
        oldGesture.End();
        Assert.True(newer.IsActive);
        Assert.True(newer.Update(null, 4_100_001).Accepted);
        Assert.True(newer.Update(null, 4_200_003).Accepted);
        Assert.Equal(2, workspace.History.UndoCount);
        Assert.Same(oldDrag, workspace.Undo());
        Assert.Same(source, workspace.Undo());
    }

    [Fact]
    public void 新手势已有更新后旧Update及End不得影响它继续合并()
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(LyricTimingEditUseCaseTests.Sample());
        using LyricTimingEditGesture oldGesture = Begin(workspace, First);
        Assert.True(oldGesture.Update(null, 2_100_001).Accepted);
        using LyricTimingEditGesture newer = Begin(workspace, First);
        Assert.True(newer.Update(null, 2_200_003).Accepted);
        LyricTimingActionsTests.AssertUnchanged(workspace, () => oldGesture.Update(null, 2_300_005), LyricTimingEditErrorCode.GestureStale);
        oldGesture.End();
        Assert.True(newer.IsActive);
        Assert.True(newer.Update(null, 2_400_007).Accepted);
        Assert.Equal(2, workspace.History.UndoCount);
    }

    [Fact]
    public void 满容量时新手势仍合并且仅替换最旧一步()
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(LyricTimingEditUseCaseTests.Sample());
        for (int index = 0; index < ProjectHistory.DefaultCapacity; index++)
        {
            string name = $"历史{index}";
            workspace.Execute(ProjectCommand.Create("rename", project => project with { Name = name }));
        }
        Project beforeDrag = workspace.Project;
        Assert.Equal(ProjectHistory.DefaultCapacity, workspace.History.UndoCount);
        using LyricTimingEditGesture gesture = Begin(workspace, First);
        Assert.True(gesture.Update(null, 2_100_001).Accepted);
        LyricTimingEditResult final = gesture.Update(null, 2_200_003);
        Assert.True(final.Accepted);
        Assert.Equal(ProjectHistory.DefaultCapacity, workspace.History.UndoCount);
        gesture.End();
        Assert.Same(beforeDrag, workspace.Undo());
        Assert.Same(final.Project, workspace.Redo());
        Assert.Equal(ProjectHistory.DefaultCapacity, workspace.History.UndoCount);
    }

    [Fact]
    public void 失败开始不打断已有有效手势也不增加历史()
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(LyricTimingEditUseCaseTests.Sample());
        using LyricTimingEditGesture active = Begin(workspace, First);
        Assert.True(active.Update(null, 2_100_001).Accepted);
        var baseline = LyricTimingActionsTests.Baseline(workspace, First);
        Assert.False(LyricTimingEditUseCase.TryBeginGesture(workspace,
            baseline with { ExpectedTime = TimeRange.Create(1_000_000, 2_000_000) }, out var stale, out var staleError));
        Assert.Null(stale);
        Assert.Equal(LyricTimingEditErrorCode.TargetStale, staleError!.Code);
        Assert.False(LyricTimingEditUseCase.TryBeginGesture(workspace,
            LyricTimingActionsTests.Baseline(workspace, new("primary", "two", "third")), out var untimed, out var untimedError));
        Assert.Null(untimed);
        Assert.Equal(LyricTimingEditErrorCode.Untimed, untimedError!.Code);
        Assert.True(active.IsActive);
        Assert.True(active.Update(null, 2_200_003).Accepted);
        Assert.Equal(1, workspace.History.UndoCount);
    }

    [Fact]
    public void 开始拒绝同ID新工作区或旧代次且不碰新工程()
    {
        ProjectWorkspace original = ProjectWorkspace.Adopt(LyricTimingEditUseCaseTests.Sample());
        var baseline = LyricTimingActionsTests.Baseline(original, First);
        ProjectWorkspace replacement = ProjectWorkspace.Adopt(original.Project);
        Assert.False(LyricTimingEditUseCase.TryBeginGesture(replacement, baseline, out var gesture, out var error));
        Assert.Null(gesture);
        Assert.Equal(LyricTimingEditErrorCode.WorkspaceStale, error!.Code);
        Assert.False(replacement.IsDirty);
        Assert.Equal(0, replacement.History.UndoCount);
        original.StartNew(LyricTimingEditUseCaseTests.Sample());
        Assert.False(LyricTimingEditUseCase.TryBeginGesture(original, baseline, out _, out var generationError));
        Assert.Equal(LyricTimingEditErrorCode.WorkspaceStale, generationError!.Code);
        Assert.Equal(0, original.History.UndoCount);
    }

    [Fact]
    public void 无变化时间组提交仍断开两次微调但不产生空历史()
    {
        Project source = LyricTimingEditUseCaseTests.Sample();
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        Assert.True(LyricTimingEditUseCase.Nudge(workspace, LyricTimingActionsTests.Baseline(workspace, First), 1).Accepted);
        Project first = workspace.Project;
        int notifications = 0;
        workspace.History.Changed += (_, _) => notifications++;
        LyricTimingEditResult commit = LyricTimingEditUseCase.Commit(workspace,
            LyricTimingActionsTests.Request(workspace, First, null, null));
        Assert.True(commit.Accepted);
        Assert.False(commit.Changed);
        Assert.Same(first, workspace.Project);
        Assert.Equal(0, notifications);
        Assert.True(LyricTimingEditUseCase.Nudge(workspace, LyricTimingActionsTests.Baseline(workspace, First), 1).Accepted);
        Assert.Equal(2, workspace.History.UndoCount);
        Assert.Same(first, workspace.Undo());
        Assert.Same(source, workspace.Undo());
    }

    [Fact]
    public void 结束后旧手势不能写入且释放保留已接受变化()
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(LyricTimingEditUseCaseTests.Sample());
        LyricTimingEditGesture gesture = Begin(workspace, First);
        Project accepted = gesture.Update(null, 2_100_001).Project;
        gesture.Dispose();
        gesture.Dispose();
        Assert.Same(accepted, workspace.Project);
        LyricTimingActionsTests.AssertUnchanged(workspace, () => gesture.Update(null, 2_200_003), LyricTimingEditErrorCode.GestureStale);
        Assert.Equal(1, workspace.History.UndoCount);
    }

    private static LyricTimingEditGesture Begin(ProjectWorkspace workspace, LyricTimingEditTarget target)
    {
        Assert.True(LyricTimingEditUseCase.TryBeginGesture(workspace, LyricTimingActionsTests.Baseline(workspace, target),
            out LyricTimingEditGesture? gesture, out LyricTimingEditError? error));
        Assert.Null(error);
        return Assert.IsType<LyricTimingEditGesture>(gesture);
    }
}
