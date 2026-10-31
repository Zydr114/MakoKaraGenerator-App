using System.Collections.Immutable;
using MakoKaraGenerator.Application.Editing;
using MakoKaraGenerator.Core.Commands;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Application.Tests;

public sealed class PreparedLyricTokenEditTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void 四类纯准备不提交且固定候选一次采用不重新分配ID(int kind)
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(LyricTokenEditUseCaseTests.Sample());
        if (kind == 1) LyricTokenEditUseCase.SplitToken(workspace, LyricTokenEditUseCaseTests.Baseline(workspace));
        Project source = workspace.Project; int history = workspace.History.UndoCount;
        PreparedLyricTokenEdit prepared = Prepare(workspace, kind);
        Assert.True(prepared.Result.Accepted); Assert.True(prepared.Result.Changed);
        Assert.Same(source, workspace.Project); Assert.Equal(history, workspace.History.UndoCount);
        LyricTokenEditResult committed = LyricTokenEditUseCase.CommitPrepared(workspace, prepared);
        Assert.True(committed.Accepted); Assert.Same(prepared.Result.Project, workspace.Project);
        Assert.Equal(prepared.Result.SelectedTokenId, committed.SelectedTokenId);
        Assert.Equal(history + 1, workspace.History.UndoCount);
        Assert.Same(source, workspace.Undo()); Assert.Same(committed.Project, workspace.Redo());
    }

    [Fact]
    public async Task 两个同时准备的结果按唯一所有者提交后第二个失效且其他工作区代次拒绝()
    {
        Project original = LyricTokenEditUseCaseTests.Sample();
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(original);
        PreparedLyricTokenEdit[] prepared = await Task.WhenAll(Task.Run(() => Prepare(workspace, 0)), Task.Run(() => Prepare(workspace, 2)));
        Assert.True(LyricTokenEditUseCase.CommitPrepared(workspace, prepared[0]).Accepted);
        Assert.Equal(LyricContentEditErrorCode.TargetStale, LyricTokenEditUseCase.CommitPrepared(workspace, prepared[1]).Error!.Code);
        Assert.Equal(LyricContentEditErrorCode.WorkspaceStale,
            LyricTokenEditUseCase.CommitPrepared(ProjectWorkspace.Adopt(original), prepared[1]).Error!.Code);
        workspace.StartNew(original);
        Assert.Equal(LyricContentEditErrorCode.WorkspaceStale, LyricTokenEditUseCase.CommitPrepared(workspace, prepared[1]).Error!.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 来源后行段或ID预算异常不触发任何历史且保留先前重做(bool stopAtIds)
    {
        Project source = LyricTokenEditUseCaseTests.Sample();
        LyricLine second = LyricLine.Create("two", new string('新', 100000));
        source = source with { Lyrics = source.Lyrics with { Lines = source.Lyrics.Lines.Add(second) } };
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(source);
        workspace.Execute(ProjectCommand.Create("temporary", project => project with { Name = "changed" })); workspace.Undo();
        int currentLine = -1, observed = 0, completed = 0;
        var observers = new LyricTokenPreparationObservers(OnStartingLine: index => { currentLine = index; observed = 0; },
            OnCreatingSegment: () => { if (!stopAtIds && currentLine == 1 && ++observed > 20) throw new BudgetException(); },
            OnCreatingToken: () => { if (stopAtIds && currentLine == 1 && ++observed > 20) throw new BudgetException(); },
            OnPreparedLine: (_, _) => completed++);
        Assert.Throws<BudgetException>(() => LyricTokenEditUseCase.PrepareResplitBranch(workspace,
            new(new(workspace, workspace.Generation, "primary", source.Lyrics.PrimaryBranch!), TokenSplitRule.PerTextElement, false), observers));
        Assert.Equal(21, observed); Assert.Equal(1, completed); Assert.Same(source, workspace.Project);
        Assert.Equal(0, workspace.History.UndoCount); Assert.True(workspace.CanRedo);
    }

    [Fact]
    public void 手工重切流式Unicode检查保留大合并元素且拒绝拆开组合边界()
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(LyricTokenEditUseCaseTests.Sample("e\u0301乙"));
        LyricContentEditBaseline baseline = LyricTokenEditUseCaseTests.Baseline(workspace, null);
        PreparedLyricTokenEdit rejected = LyricTokenEditUseCase.PrepareResplitLine(workspace, new(baseline, ["e", "\u0301乙"], false));
        Assert.False(rejected.Result.Accepted); Assert.Equal(LyricContentEditErrorCode.InvalidTarget, rejected.Result.Error!.Code);
        PreparedLyricTokenEdit accepted = LyricTokenEditUseCase.PrepareResplitLine(workspace, new(baseline, ["e\u0301", "乙"], false));
        Assert.True(accepted.Result.Accepted); Assert.Equal("e\u0301乙", string.Concat(accepted.Result.Project.Lyrics.Lines[0].Tokens.Select(token => token.Text)));
    }

    [Fact]
    public void 重排纯准备保留旧预演语义并在提交前验证完整工程引用()
    {
        ProjectWorkspace workspace = ProjectWorkspace.Adopt(LyricTokenEditUseCaseTests.Sample());
        LyricTokenEditUseCase.SplitToken(workspace, LyricTokenEditUseCaseTests.Baseline(workspace));
        LyricContentEditBaseline baseline = LyricTokenEditUseCaseTests.Baseline(workspace, null);
        string id = baseline.ExpectedLine.Tokens[0].Id;
        PreparedLyricContentEdit prepared = LyricContentEditUseCase.PrepareReorderToken(workspace, new(baseline, id, 1, true));
        Project source = workspace.Project; Assert.True(prepared.Result.Accepted); Assert.Same(source, workspace.Project);
        Assert.True(LyricContentEditUseCase.CommitPrepared(workspace, prepared).Accepted);
        Assert.Equal("line.reorder", workspace.UndoName);
    }

    private static PreparedLyricTokenEdit Prepare(ProjectWorkspace workspace, int kind)
    {
        LyricContentEditBaseline baseline = LyricTokenEditUseCaseTests.Baseline(workspace,
            kind == 1 ? workspace.Project.Lyrics.Lines[0].Tokens[0].Id : "joined");
        return kind switch
        {
            0 => LyricTokenEditUseCase.PrepareSplitToken(workspace, baseline),
            1 => LyricTokenEditUseCase.PrepareMergeToken(workspace, baseline),
            2 => LyricTokenEditUseCase.PrepareResplitLine(workspace, new(baseline with { Target = new("primary", "one") }, ["甲", "乙"], true)),
            _ => LyricTokenEditUseCase.PrepareResplitBranch(workspace,
                new(new(workspace, workspace.Generation, "primary", workspace.Project.Lyrics.PrimaryBranch!), TokenSplitRule.PerTextElement, true)),
        };
    }
    private sealed class BudgetException : Exception;
}
