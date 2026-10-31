using MakoKaraGenerator.Application.Editing;
using MakoKaraGenerator.Core.Commands;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Application.Tests;

public sealed class LyricBranchPrepareTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void 四类纯准备不修改工作区历史或发布通知且只提交固定候选一次(int kind)
    {
        ProjectWorkspace workspace = Workspace(); Project source = workspace.Project;
        int events = 0; workspace.History.Changed += (_, _) => events++;
        PreparedLyricBranchEdit prepared = Prepare(workspace, kind);
        Assert.True(prepared.Result.Accepted); Assert.True(prepared.Result.Changed);
        Assert.Same(source, workspace.Project); Assert.Equal(0, events); Assert.Equal(0, workspace.History.UndoCount);
        Project candidate = prepared.Result.Project;
        var committed = LyricBranchEditUseCase.CommitPrepared(workspace, prepared);
        Assert.Same(candidate, committed.Project); Assert.Same(candidate, workspace.Project);
        Assert.Equal(1, events); Assert.Equal(1, workspace.History.UndoCount);
        Assert.Same(source, workspace.Undo()); Assert.Same(candidate, workspace.Redo());
        var repeated = LyricBranchEditUseCase.CommitPrepared(workspace, prepared);
        Assert.False(repeated.Accepted); Assert.Equal(LyricBranchEditErrorCode.TargetStale, repeated.Error!.Code);
        Assert.Same(candidate, workspace.Project); Assert.Equal(1, workspace.History.UndoCount);
    }

    [Fact]
    public void 新增候选身份只分配一次且准备之后无关工程修改拒绝覆盖()
    {
        ProjectWorkspace workspace = Workspace();
        var prepared = Prepare(workspace, 0);
        string selected = prepared.Result.SelectedBranchId!;
        Assert.Single(prepared.Result.Project.Lyrics.Branches, branch => branch.Id == selected);
        workspace.Execute(ProjectCommand.Create("rename", project => project with { Name = "new" }));
        Project current = workspace.Project;
        var rejected = LyricBranchEditUseCase.CommitPrepared(workspace, prepared);
        Assert.False(rejected.Accepted); Assert.Equal(LyricBranchEditErrorCode.TargetStale, rejected.Error!.Code);
        Assert.Same(current, workspace.Project); Assert.Equal(1, workspace.History.UndoCount);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void 工作区或代次改变拒绝固定候选(bool other)
    {
        ProjectWorkspace workspace = Workspace(); var prepared = Prepare(workspace, 0);
        ProjectWorkspace current = other ? ProjectWorkspace.Adopt(workspace.Project) : workspace;
        if (!other) workspace.StartNew(workspace.Project);
        var rejected = LyricBranchEditUseCase.CommitPrepared(current, prepared);
        Assert.False(rejected.Accepted); Assert.Equal(LyricBranchEditErrorCode.WorkspaceStale, rejected.Error!.Code);
        Assert.Equal(0, current.History.UndoCount);
    }

    [Fact]
    public void 相同元数据或悬空主行关联准备提交均保原引用和重做()
    {
        ProjectWorkspace workspace = Workspace();
        var branch = workspace.Project.Lyrics.Branches[1];
        Project source = workspace.Project with { Lyrics = workspace.Project.Lyrics.ReplaceBranch(branch with
        {
            Lines = [branch.Lines[0] with { PrimaryLineId = "missing-primary" }],
        }) };
        workspace = ProjectWorkspace.Adopt(source);
        workspace.Execute(ProjectCommand.Create("rename", project => project with { Name = "other" })); workspace.Undo();
        var prepared = LyricBranchEditUseCase.PrepareLinkPrimaryLine(workspace,
            new(workspace, workspace.Generation, branch.Id, branch.Lines[0].Id, "missing-primary", "missing-primary"));
        Assert.True(prepared.Result.Accepted); Assert.False(prepared.Result.Changed);
        var result = LyricBranchEditUseCase.CommitPrepared(workspace, prepared);
        Assert.True(result.Accepted); Assert.Same(source, result.Project); Assert.Same(source, workspace.Project);
        Assert.Equal(1, workspace.History.RedoCount); Assert.Equal(0, workspace.History.UndoCount);
    }

    private static PreparedLyricBranchEdit Prepare(ProjectWorkspace workspace, int kind)
    {
        var branch = workspace.Project.Lyrics.Branches[1];
        LyricBranchEditBaseline baseline = new(workspace, workspace.Generation, branch.Id, branch);
        return kind switch
        {
            0 => LyricBranchEditUseCase.PrepareAddTranslation(workspace, new(workspace, workspace.Generation, workspace.Project.Lyrics, " 新翻译 ", "ja")),
            1 => LyricBranchEditUseCase.PrepareRemove(workspace, baseline),
            2 => LyricBranchEditUseCase.PrepareConfigure(workspace, new(baseline,
                new("changed", null, LyricBranchRole.Annotation, LyricBranchTimingMode.Independent, branch.StyleOverride))),
            _ => LyricBranchEditUseCase.PrepareLinkPrimaryLine(workspace, new(workspace, workspace.Generation, branch.Id, branch.Lines[0].Id, null, "main")),
        };
    }

    private static ProjectWorkspace Workspace() => ProjectWorkspace.Adopt(new Project
    {
        Name = "prepare",
        Lyrics = new LyricsDocument
        {
            PrimaryBranchId = "primary",
            Branches = [LyricBranch.Create("primary", "主", LyricBranchRole.Primary, [LyricLine.Create("main", "主")]),
                LyricBranch.Create("translation", "翻译", LyricBranchRole.Translation,
                    [LyricLine.Create("translated", "译")], "zh-CN", LyricBranchTimingMode.FollowPrimary)],
        },
    });
}
