using MakoKaraGenerator.Application.Editing;
using MakoKaraGenerator.Core.Commands;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Application.Tests;

public sealed class PreparedLyricContentEditTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void 五类纯准备不写历史且提交采用同一候选和原反馈(int kind)
    {
        Project original = LyricContentEditUseCaseTests.Sample();
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(original);
        int notifications = 0; workspace.History.Changed += (_, _) => notifications++;
        PreparedLyricContentEdit prepared = Prepare(workspace, kind);
        Assert.True(prepared.Result.Accepted); Assert.True(prepared.Result.Changed);
        Assert.Same(original, workspace.Project); Assert.False(workspace.IsDirty);
        Assert.Equal(0, workspace.History.UndoCount); Assert.Equal(0, notifications);
        LyricContentEditResult committed = LyricContentEditUseCase.CommitPrepared(workspace, prepared);
        Assert.True(committed.Accepted); Assert.Same(prepared.Result.Project, committed.Project);
        Assert.Same(prepared.Result.Project, workspace.Project);
        Assert.Equal(prepared.Result.CreatedLineId, committed.CreatedLineId);
        Assert.Equal(prepared.Result.SelectedLineId, committed.SelectedLineId);
        Assert.Equal(prepared.Result.PreservedCount, committed.PreservedCount);
        Assert.Equal(1, workspace.History.UndoCount); Assert.Equal(1, notifications);
        Assert.Same(original, workspace.Undo());
        Assert.Same(committed.Project, workspace.Redo());
    }

    [Fact]
    public async Task 两个同时准备的候选经唯一所有者提交后第二个拒绝且不丢重做()
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(LyricContentEditUseCaseTests.Sample());
        Task<PreparedLyricContentEdit>[] preparing = [Task.Run(() => Prepare(workspace, 2)), Task.Run(() => Prepare(workspace, 3))];
        PreparedLyricContentEdit[] fixedInputs = await Task.WhenAll(preparing);
        Assert.True(LyricContentEditUseCase.CommitPrepared(workspace, fixedInputs[0]).Accepted);
        Project current = workspace.Project;
        LyricContentEditResult stale = LyricContentEditUseCase.CommitPrepared(workspace, fixedInputs[1]);
        Assert.False(stale.Accepted); Assert.Equal(LyricContentEditErrorCode.TargetStale, stale.Error!.Code);
        Assert.Same(current, workspace.Project); Assert.Equal(1, workspace.History.UndoCount);
    }

    [Fact]
    public void 不同工作区相同工程引用及同工作区新代次均不能提交旧候选()
    {
        Project original = LyricContentEditUseCaseTests.Sample();
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(original);
        PreparedLyricContentEdit prepared = Prepare(workspace, 0);
        ProjectWorkspace other = ProjectWorkspace.Adopt(original);
        Assert.Equal(LyricContentEditErrorCode.WorkspaceStale, LyricContentEditUseCase.CommitPrepared(other, prepared).Error!.Code);
        workspace.StartNew(original);
        Assert.Equal(LyricContentEditErrorCode.WorkspaceStale, LyricContentEditUseCase.CommitPrepared(workspace, prepared).Error!.Code);
        Assert.Same(original, workspace.Project); Assert.Equal(0, workspace.History.UndoCount);
    }

    [Fact]
    public void 预算观察只计真实新增元素且抛出预算异常不改变工程历史和重做()
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(LyricContentEditUseCaseTests.Sample());
        workspace.Execute(ProjectCommand.Create("temporary", project => project with { Name = "changed" })); workspace.Undo();
        Project original = workspace.Project;
        int allocations = 0;
        Assert.Throws<BudgetException>(() => LyricContentEditUseCase.PrepareSetLineText(workspace,
            LyricContentEditUseCaseTests.TextRequest(workspace, new string('新', 10000)), () =>
            {
                if (++allocations > 20) throw new BudgetException();
            }));
        Assert.Equal(21, allocations); Assert.Same(original, workspace.Project);
        Assert.Equal(0, workspace.History.UndoCount); Assert.True(workspace.CanRedo);
    }

    [Fact]
    public void 既有大合并元素不按文字数量误判为新增元素预算超限()
    {
        string text = new('字', 30000);
        LyricLine line = LyricLine.Create("line", text, tokens: [LyricToken.Create("large", text)]);
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(Project.Create("large", lyrics: LyricsDocument.Create([line])));
        int allocations = 0;
        PreparedLyricContentEdit prepared = LyricContentEditUseCase.PrepareSetLineText(workspace,
            new(workspace, workspace.Generation, new("primary", "line"), text, text + "新"), () => allocations++);
        Assert.True(prepared.Result.Accepted); Assert.Equal(1, allocations);
        Assert.Equal(2, prepared.Result.Project.Lyrics.Lines[0].Tokens.Length);
        Assert.Same(line.Tokens[0], prepared.Result.Project.Lyrics.Lines[0].Tokens[0]);
    }

    private static PreparedLyricContentEdit Prepare(ProjectWorkspace workspace, int kind) => kind switch
    {
        0 => LyricContentEditUseCase.PrepareSetLineText(workspace, LyricContentEditUseCaseTests.TextRequest(workspace, "甲新👨‍👩‍👧‍👦é乙")),
        1 => LyricContentEditUseCase.PrepareSetRubyText(workspace, LyricContentEditUseCaseTests.RubyRequest(workspace, "first", "新音")),
        2 => LyricContentEditUseCase.PrepareAddLineAfter(workspace, new(workspace, workspace.Generation, "primary", null)),
        3 => LyricContentEditUseCase.PrepareDuplicateLine(workspace, LyricContentEditUseCaseTests.Baseline(workspace)),
        _ => LyricContentEditUseCase.PrepareRemoveLine(workspace, LyricContentEditUseCaseTests.Baseline(workspace)),
    };
    private sealed class BudgetException : Exception;
}
