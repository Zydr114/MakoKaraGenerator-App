using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Application.Tests;

public sealed class ClipMembershipOperationTests
{
    [Fact]
    public async Task 显式拆分保完整相对配方实例种子执行顺序与实际槽位时钟()
    {
        var source = Source();
        var package = await EffectPackageTransfer.ReadDirectoryAsync(Path.Combine(AppContext.BaseDirectory, "effect-packages", "WordEmphasis"), ProjectEffectsJson.PackageProfile);
        source = source with { Effects = new([package], [new() { Id = "direct", Name = "direct", PackageId = package.PackageId,
            Revision = package.Revision, ExportId = "word-emphasis", Binding = new(ProjectEffectBindingKind.Clips, ids: [ExclusiveDisplayFixture.ClipId]), Seed = 987, Enabled = false }]) };
        var preset = StyleTemplate.Create("relative", "固定成员", StyleTemplateKind.Composite, source.LyricStyle,
            [LyricStyleProperty.Opacity], [new("recipe", "成员", TemplateEffectTarget.TokenFamily, package.PackageId, package.Revision, "word-emphasis", fixedSeed: 73)], [package]);
        Assert.True(PresetApplicationsEditor.TryApply(source, preset, [new(ProjectEffectBindingKind.Clips, ids: [ExclusiveDisplayFixture.ClipId])], null, out source, out var reason), reason);
        var identities = Assert.Single(source.PresetApplications.Items).Identities;
        var beforeProgram = source.LyricPresentation.FindProgram("primary")!;
        var branch = source.Lyrics.Branches.Single(item => item.Id == "primary");
        var before = LyricDisplayScheduleBuilder.Build(branch, beforeProgram, source.LyricPresentation);
        Assert.True(ClipMembershipOperation.TrySplit(source, "primary", ExclusiveDisplayFixture.ClipId, "line-2", "clip/right", out var result, out var error), error?.ToString());
        Assert.Collection(Assert.Single(result.Effects.Instances).Binding.Ids,
            id => Assert.Equal(ExclusiveDisplayFixture.ClipId, id), id => Assert.Equal("clip/right", id));
        Assert.Equal(source.Effects.Instances[0] with { Binding = result.Effects.Instances[0].Binding }, result.Effects.Instances[0]);
        Assert.Equal(identities.OrderBy(item => item.InstanceId), result.PresetApplications.Items.SelectMany(item => item.Identities).OrderBy(item => item.InstanceId));
        var after = LyricDisplayScheduleBuilder.Build(branch, result.LyricPresentation.FindProgram("primary")!, result.LyricPresentation);
        Assert.Equal(before.Select(item => (item.Line.Id, item.SlotIndex, item.RevealTimeUs, item.HideTimeUs)), after.Select(item => (item.Line.Id, item.SlotIndex, item.RevealTimeUs, item.HideTimeUs)));
        using TemporaryDirectory directory = new(); ProjectStore.Save(result, directory.Path);
        var loaded = ProjectStore.Load(directory.Path); Assert.NotNull(loaded.Project);
        Assert.Equal(result.PresetApplications, loaded.Project.PresetApplications); Assert.Equal(result.LyricPresentation, loaded.Project.LyricPresentation);
        Assert.False(ClipMembershipOperation.TrySetMembers(result, "primary", "clip/right", ["line-3", "line-4"], out var rejected, out error));
        Assert.Equal("ReferenceConflict", error!.Code); Assert.Same(result, rejected);
    }

    [Fact]
    public async Task 显式拆分不安全范围配方原子拒绝且合并直接片段集合完整去重映射()
    {
        var source = Source();
        var package = await EffectPackageTransfer.ReadDirectoryAsync(Path.Combine(AppContext.BaseDirectory, "effect-packages", "WordEmphasis"), ProjectEffectsJson.PackageProfile);
        var preset = StyleTemplate.Create("range", "范围", StyleTemplateKind.Composite, source.LyricStyle,
            [LyricStyleProperty.Opacity], [new("recipe", "范围", TemplateEffectTarget.Application, package.PackageId, package.Revision, "word-emphasis", fixedSeed: 73)], [package]);
        Assert.True(PresetApplicationsEditor.TryApply(source, preset, [new(ProjectEffectBindingKind.Clips, ids: [ExclusiveDisplayFixture.ClipId])], null, out var withPreset, out var reason), reason);
        Assert.False(ClipMembershipOperation.TrySplit(withPreset, "primary", ExclusiveDisplayFixture.ClipId, "line-2", "clip/right", out var rejected, out var error));
        Assert.Equal("ReferenceConflict", error!.Code); Assert.Same(withPreset, rejected);
        source = source with { Effects = new([package], [new() { Id = "direct", Name = "direct", PackageId = package.PackageId,
            Revision = package.Revision, ExportId = "word-emphasis", Binding = new(ProjectEffectBindingKind.Clips, ids: [ExclusiveDisplayFixture.ClipId]), Seed = 987 }]) };
        Assert.True(ClipMembershipOperation.TrySplit(source, "primary", ExclusiveDisplayFixture.ClipId, "line-2", "clip/right", out var split, out error), error?.ToString());
        Assert.True(ClipMembershipOperation.TryMerge(split, "primary", [ExclusiveDisplayFixture.ClipId, "clip/right"], "clip/right", out var merged, out error), error?.ToString());
        Assert.Collection(Assert.Single(merged.Effects.Instances).Binding.Ids, id => Assert.Equal("clip/right", id));
        Assert.Equal(987UL, merged.Effects.Instances[0].Seed); Assert.Equal("direct", merged.Effects.Instances[0].Id);
        Assert.Equal("clip/right", Assert.Single(merged.LyricPresentation.FindProgram("primary")!.Clips).Id);
    }

    private static Project Source()
    {
        var source = ExclusiveDisplayFixture.Create(); var tree = source.LyricPresentation; var program = tree.FindProgram("primary")!;
        var clip = program.Clips.Single() with { LineIds = [.. source.Lyrics.Lines.Select(line => line.Id)] };
        Assert.True(PresentationEditor.TrySetTree(source, tree with { Programs = tree.Programs.SetItem(tree.Programs.IndexOf(program), program with { Clips = [clip] }) }, out source, out var report), report.ToSummary());
        return source;
    }
}
