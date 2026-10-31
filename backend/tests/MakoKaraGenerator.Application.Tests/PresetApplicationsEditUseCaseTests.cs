using MakoKaraGenerator.Application;
using MakoKaraGenerator.Application.Editing;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Application.Tests;

public sealed class PresetApplicationsEditUseCaseTests
{
    private static StyleTemplate Template() => StyleTemplate.Create("static", "静态", StyleTemplateKind.Composite, LyricStyle.Default, [LyricStyleProperty.SungColor]);

    [Fact]
    public void ApplyUndoRedoAndNoOpKeepRedo()
    {
        var workspace = ProjectWorkspace.CreateNew("应用");
        var prepared = PresetApplicationsEditUseCase.PrepareApply(workspace, Template(), [new(ProjectEffectBindingKind.Project)]);
        var applied = PresetApplicationsEditUseCase.CommitPrepared(workspace, prepared);
        Assert.True(applied.Accepted); Assert.Equal(1, applied.ChangedApplicationCount); Assert.Single(workspace.Project.PresetApplications.Items);
        var undone = workspace.History.Undo(workspace.Project);
        Assert.Empty(undone.PresetApplications.Items);
        Assert.True(workspace.CanRedo);
        var replay = PresetApplicationsEditUseCase.CommitPrepared(workspace, PresetApplicationsEditUseCase.PrepareApply(workspace, Template(), [new(ProjectEffectBindingKind.Project)]));
        Assert.True(replay.Accepted); Assert.False(replay.Changed); Assert.True(workspace.CanRedo);
        Assert.Single(workspace.Project.PresetApplications.Items);
    }

    [Fact]
    public void UpdatePreservesApplicationIdentitySeedAndEnabled()
    {
        var workspace = ProjectWorkspace.CreateNew("更新"); var original = Template();
        PresetApplicationsEditUseCase.CommitPrepared(workspace, PresetApplicationsEditUseCase.PrepareApply(workspace, original, [new(ProjectEffectBindingKind.Project)]));
        string id = workspace.Project.PresetApplications.Items[0].Id;
        PresetApplicationsEditUseCase.CommitPrepared(workspace, PresetApplicationsEditUseCase.PrepareSetEnabled(workspace, [id], false));
        var updated = PresetApplicationsEditUseCase.CommitPrepared(workspace, PresetApplicationsEditUseCase.PrepareUpdate(workspace, original with { Name = "改名" }, [id]));
        var item = Assert.Single(workspace.Project.PresetApplications.Items);
        Assert.True(updated.Accepted); Assert.Equal(id, item.Id); Assert.False(item.Enabled);
    }

    [Fact]
    public void StaleWorkspaceIsRejectedAndRemoveChangesOne()
    {
        var workspace = ProjectWorkspace.CreateNew("过期"); var prepared = PresetApplicationsEditUseCase.PrepareApply(workspace, Template(), [new(ProjectEffectBindingKind.Project)]);
        workspace.StartNew(Project.Create("新工程"));
        var stale = PresetApplicationsEditUseCase.CommitPrepared(workspace, prepared);
        Assert.False(stale.Accepted);
        var fresh = PresetApplicationsEditUseCase.PrepareApply(workspace, Template(), [new(ProjectEffectBindingKind.Project)]);
        Assert.True(PresetApplicationsEditUseCase.CommitPrepared(workspace, fresh).Accepted);
        string id = workspace.Project.PresetApplications.Items[0].Id;
        var removed = PresetApplicationsEditUseCase.CommitPrepared(workspace, PresetApplicationsEditUseCase.PrepareRemove(workspace, [id]));
        Assert.True(removed.Accepted); Assert.Equal(1, removed.ChangedApplicationCount); Assert.Empty(workspace.Project.PresetApplications.Items);
    }
}
