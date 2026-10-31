using MakoKaraGenerator.Core.Commands;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Application.Tests;

public sealed class ProjectEffectsHistoryTests
{
    [Fact]
    public void 注册实例参数排序与修订接受通过一次历史原子恢复()
    {
        var package = EffectPackageFixture.Contents(); var project = Project.Create("历史"); ProjectHistory history = new();
        ProjectEffectInstance first = new() { Id = "fx:a", Name = "强调", PackageId = package.PackageId, Revision = package.Revision, ExportId = "scale", Binding = new(ProjectEffectBindingKind.Tokens, ids: ["token"]) };
        var accepted = history.Execute(new SetProjectEffectsCommand(new([package], [first])), project); Assert.Equal(1, history.UndoCount);
        Assert.Same(project, history.Undo(accepted)); Assert.Same(accepted, history.Redo(project));
        Assert.Same(accepted, history.Execute(new SetProjectEffectsCommand(new(accepted.Effects.Packages, accepted.Effects.Instances)), accepted)); Assert.Equal(1, history.UndoCount);
        var duplicate = ProjectEffectsEditor.Duplicate(accepted.Effects, "fx:a"); var revision = EffectPackageFixture.Contents(2);
        var upgraded = ProjectEffectsEditor.AcceptRevision(duplicate, revision, [duplicate.Instances[1].Id]);
        var changed = history.Execute(new SetProjectEffectsCommand(new(upgraded.Packages, upgraded.Instances.Reverse(), new() { LiveParticles = 10 })), accepted);
        Assert.Equal(revision.Revision, changed.Effects.Instances[0].Revision); Assert.Equal(10, changed.Effects.Budgets.LiveParticles);
        Assert.Same(accepted, history.Undo(changed)); Assert.Same(changed, history.Redo(accepted));
    }
}
