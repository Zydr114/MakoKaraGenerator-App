using MakoKaraGenerator.Core.Commands;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Application.Tests;

public sealed class PresetApplicationTransactionsTests
{
    private static StyleTemplate Preset()
    {
        var package = EffectPackageFixture.GroupTransform();
        var config = ScriptConfiguration.Create("group-config", "工程变换", package, "group-transform", readScope: ScriptReadScope.Project, fixedSeed: ulong.MaxValue);
        return StyleTemplate.Create("combined", "固定组合", StyleTemplateKind.Composite, LyricStyle.Default with { SungColor = RgbaColor.Black },
            [LyricStyleProperty.SungColor], [TemplateEffectRecipe.FromConfiguration("group", config)], [package]);
    }

    [Fact]
    public void 应用移除各一次历史且撤销重做恢复完整身份资源和固定快照()
    {
        var source = Project.Create("历史"); var history = new ProjectHistory(); var preset = Preset();
        Assert.True(PresetApplicationsEditor.TryApply(source, preset, [new(ProjectEffectBindingKind.Project)], null, out var prepared, out var reason), reason);
        var applied = history.Execute(new SetPresetApplicationsCommand(prepared.PresetApplications), source);
        Assert.Equal(1, history.UndoCount); Assert.Single(applied.PresetApplications.Items); Assert.Single(applied.Effects.Packages);
        var identity = Assert.Single(Assert.Single(applied.PresetApplications.Items).Identities); Assert.Equal(ulong.MaxValue, identity.Seed);
        Assert.Same(applied, history.Execute(new SetPresetApplicationsCommand(applied.PresetApplications), applied)); Assert.Equal(1, history.UndoCount);
        Assert.Same(source, history.Undo(applied)); Assert.Same(applied, history.Redo(source));
        var preview = PresetApplicationsEditor.Remove(applied, [new(ProjectEffectBindingKind.Project)]);
        var removed = history.Execute(new SetPresetApplicationsCommand(preview.PresetApplications), applied);
        Assert.Empty(removed.PresetApplications.Items); Assert.Equal(2, history.UndoCount); Assert.Single(removed.Effects.Packages);
        Assert.Same(applied, history.Undo(removed)); Assert.Equal(identity, Assert.Single(applied.PresetApplications.Items[0].Identities));
    }

    [Fact]
    public async Task 新组合模板分享后删除配置和模板来源工程副本仍有完整包与身份()
    {
        using TemporaryDirectory temp = new(); var preset = Preset(); string library = StyleTemplateLibrary.ResolvePath(temp.FilePath("library"));
        string configurations = ScriptConfigurationLibrary.ResolvePath(temp.FilePath("configuration-library"));
        var configuration = preset.EffectRecipes[0].ToConfiguration(preset.Packages[0]); ScriptConfigurationLibrary.Save(configurations, [configuration]);
        StyleTemplateLibrary.Save(library, [preset]); string archive = temp.FilePath("combined.mktpl");
        await StyleTemplateTransfer.WriteArchiveAsync(archive, StyleTemplateLibrary.Load(library).Templates);
        Directory.Delete(Path.GetDirectoryName(library)!, true); Directory.Delete(Path.GetDirectoryName(configurations)!, true);
        var imported = Assert.Single(await StyleTemplateTransfer.ReadArchiveAsync(archive));
        Assert.Equal(preset, imported); var source = Project.Create("闭包");
        Assert.True(PresetApplicationsEditor.TryApply(source, imported, [new(ProjectEffectBindingKind.Project)], null, out var project, out var reason), reason);
        string first = temp.FilePath("project"), copy = temp.FilePath("copy"); ProjectStore.Save(project, first); ProjectStore.Save(project, copy);
        Directory.Delete(first, true); File.Delete(archive); var reopened = ProjectStore.Load(copy); Assert.NotNull(reopened.Project);
        Assert.Equal(project.PresetApplications, reopened.Project.PresetApplications); Assert.Equal(project.Effects.Packages[0].Revision, Assert.Single(reopened.Project.Effects.Packages).Revision);
        Assert.Equal("group-config", Assert.Single(reopened.Project.PresetApplications.Items[0].Snapshot.EffectRecipes).ConfigurationId);
        Assert.Equal(ulong.MaxValue, Assert.Single(reopened.Project.PresetApplications.Items[0].Identities).Seed);
        Assert.Equal(PresetApplicationsProjection.Effects(project), PresetApplicationsProjection.Effects(reopened.Project));
    }
}
