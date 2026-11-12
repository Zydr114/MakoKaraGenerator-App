using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Core.Validation;

namespace MakoKaraGenerator.Core.Tests.Projects;

public sealed class PresetApplicationsTests
{
    private static Project Source()
    {
        var first = LyricLine.Create("l1", "甲乙", new(0, 2_000_000), TimingStatus.Confirmed, tokens:
            [LyricToken.Create("t1", "甲", new(0, 1_000_000), TimingStatus.Confirmed), LyricToken.Create("t2", "乙", new(1_000_000, 2_000_000), TimingStatus.Confirmed)]);
        var second = LyricLine.Create("l2", "丙", new(3_000_000, 4_000_000), TimingStatus.Confirmed, tokens: [LyricToken.Create("t3", "丙", new(3_000_000, 4_000_000), TimingStatus.Confirmed)]);
        var project = Project.Create("固定应用", lyrics: LyricsDocument.Create([first, second]));
        var tree = project.LyricPresentation; var program = tree.Programs[0];
        return project with { LyricPresentation = tree with { Programs = tree.Programs.SetItem(0, program with
            { Clips = [new() { Id = "clip", ContainerId = program.DefaultContainerId, LineIds = [first.Id, second.Id] }] }) } };
    }

    private static StyleTemplate Static(string id, RgbaColor color, params LyricStyleProperty[] properties)
        => StyleTemplate.Create(id, id, StyleTemplateKind.Composite, LyricStyle.Default with { SungColor = color, FontSizePx = 88, Opacity = .5f }, properties);
    private static StyleTemplate Global(bool enabled = true, ulong seed = 9, bool extra = false)
    {
        var package = CompositePresetDefinitionTests.ScopedPackage();
        var recipe = new TemplateEffectRecipe("global", "工程强调", TemplateEffectTarget.Application, package.PackageId, package.Revision, "scale",
            enabled: enabled, readScope: ScriptReadScope.Project, fixedSeed: seed);
        return StyleTemplate.Create("global", "全局组合", StyleTemplateKind.Composite,
            LyricStyle.Default with { SungColor = RgbaColor.Black }, [LyricStyleProperty.SungFill], extra ? [recipe,
                new("added", "新增配方", TemplateEffectTarget.Application, package.PackageId, package.Revision, "scale")] : [recipe], [package]);
    }

    private static Project Apply(Project source, StyleTemplate preset, ProjectEffectBinding target, IReadOnlyDictionary<string, string>? inputs = null)
    {
        Assert.True(PresetApplicationsEditor.TryApply(source, preset, [target], inputs, out var result, out var reason), reason);
        return result;
    }

    [Fact]
    public void 全局单实例与局部玻璃并存移除局部恢复继承且不写入歌词源()
    {
        var source = Source(); var global = Apply(source, Global(), new(ProjectEffectBindingKind.Project));
        var package = MixedEffectPackageFixture.MaterialContents(); var glass = new MaterialSpec.PackageMaterial(package.PackageId, package.Revision, "ink");
        var local = StyleTemplate.Create("glass", "局部玻璃", StyleTemplateKind.Composite,
            LyricStyle.Default with { UnsungFill = new() { Material = glass } }, [LyricStyleProperty.UnsungFill], packages: [package]);
        var project = Apply(global, local, new(ProjectEffectBindingKind.Lines, ids: ["l1"]));
        Assert.Equal(source.LyricStyle, project.LyricStyle); Assert.Equal(source.Lyrics, project.Lyrics);
        var style = PresetApplicationsProjection.ResolveLyrics(project, "l1", "t1"); Assert.Equal(glass, style.UnsungFill.Material); Assert.Equal(RgbaColor.Black, style.SungColor);
        var instance = Assert.Single(PresetApplicationsProjection.Effects(project).Instances);
        Assert.Equal(ProjectEffectBindingKind.Project, instance.Binding.Kind); Assert.Equal("project", instance.Subscription.Kind); Assert.Equal(9UL, instance.Seed);
        Assert.Equal(source.LyricStyle.UnsungFill, PresetApplicationsProjection.ResolveLyrics(project, "l2").UnsungFill);
        var removed = PresetApplicationsEditor.Remove(project, [new(ProjectEffectBindingKind.Lines, ids: ["l1"])]);
        Assert.Single(removed.PresetApplications.Items); Assert.Equal(instance, Assert.Single(PresetApplicationsProjection.Effects(removed).Instances));
        Assert.Equal(source.LyricStyle.UnsungFill, PresetApplicationsProjection.ResolveLyrics(removed, "l1").UnsungFill);
        Assert.Equal("EffectPackageInUse", Assert.Throws<EffectPackageException>(() => ProjectEffectsEditor.Unregister(project, package.PackageId, package.Revision)).Code);
    }

    [Fact]
    public void 属性逐层继承真实节点和片段上下文且效果祖先先于局部()
    {
        var source = Source(); var regular = source.LyricPresentation.Programs[0].DefaultContainerId; var root = source.LyricPresentation.Programs[0].RootContainerId;
        var white = new RgbaColor(255, 255, 255, 255); var red = new RgbaColor(220, 30, 30, 255); var blue = new RgbaColor(30, 30, 220, 255);
        var project = Apply(source, Static("token", white, LyricStyleProperty.Opacity), new(ProjectEffectBindingKind.Tokens, ids: ["t1"]));
        project = Apply(project, Global(), new(ProjectEffectBindingKind.Project));
        project = Apply(project, Static("branch", white, LyricStyleProperty.FontSizePx), new(ProjectEffectBindingKind.Branch, id: "primary"));
        project = Apply(project, Static("node", red, LyricStyleProperty.SungFill), new(ProjectEffectBindingKind.Node, id: root, includeDescendants: true));
        var context = new[] { root, regular }; var style = PresetApplicationsProjection.ResolveLyrics(project, "l1", "t1", nodePath: context);
        Assert.Equal(red, style.SungColor); Assert.Equal(88, style.FontSizePx); Assert.Equal(.5f, style.Opacity);
        project = Apply(project, Static("clip", blue, LyricStyleProperty.SungFill), new(ProjectEffectBindingKind.Clips, ids: ["clip"]));
        Assert.Equal(blue, PresetApplicationsProjection.ResolveLyrics(project, "l1", "t1", "clip", context).SungColor);
        project = Apply(project, Static("line", white, LyricStyleProperty.SungFill), new(ProjectEffectBindingKind.Lines, ids: ["l1"]));
        Assert.Equal(white, PresetApplicationsProjection.ResolveLyrics(project, "l1", "t1", "clip", context).SungColor);
        Assert.Equal(red, PresetApplicationsProjection.ResolveMark(project, SceneTextAppearance.Default.ToLyricStyle(), context).SungColor);
        var noDescendants = Apply(Source(), Static("self", red, LyricStyleProperty.SungFill), new(ProjectEffectBindingKind.Node, id: root));
        Assert.Equal(Source().LyricStyle.SungColor, PresetApplicationsProjection.ResolveLyrics(noDescendants, "l1", nodePath: context).SungColor);
        Assert.Equal(red, PresetApplicationsProjection.ResolveLyrics(noDescendants, "l1", nodePath: [root]).SungColor);
    }

    [Fact]
    public void 明确更新保留身份种子启停新增配方创建独立身份重复更新无变化()
    {
        var source = Source(); var project = Apply(source, Global(enabled: false), new(ProjectEffectBindingKind.Project));
        var item = Assert.Single(project.PresetApplications.Items); var identity = Assert.Single(item.Identities);
        project = PresetApplicationsEditor.SetEnabled(project, item.Id, false);
        Assert.True(PresetApplicationsEditor.TryUpdate(project, item.Id, Global(enabled: true, seed: 20, extra: true), null, out var updated, out var reason), reason);
        var application = Assert.Single(updated.PresetApplications.Items); Assert.False(application.Enabled); Assert.Equal(item.Id, application.Id);
        var old = application.Identities.Single(member => member.RecipeId == "global"); Assert.Equal(identity.InstanceId, old.InstanceId); Assert.Equal(9UL, old.Seed); Assert.False(old.Enabled);
        Assert.NotEqual(old.InstanceId, application.Identities.Single(member => member.RecipeId == "added").InstanceId);
        Assert.All(PresetApplicationsProjection.Effects(updated).Instances, instance => Assert.False(instance.Enabled));
        Assert.True(PresetApplicationsEditor.TryUpdate(updated, item.Id, Global(enabled: true, seed: 20, extra: true), null, out var repeated, out reason), reason);
        Assert.Same(updated, repeated); Assert.Same(updated, PresetApplicationsEditor.SetEnabled(updated, item.Id, false));
        Assert.False(PresetApplicationsEditor.TryUpdate(updated, item.Id, Static("different", RgbaColor.Black, LyricStyleProperty.SungFill), null, out var rejected, out _)); Assert.Same(updated, rejected);
        var replaced = Apply(updated, Static("different", RgbaColor.Black, LyricStyleProperty.SungFill), new(ProjectEffectBindingKind.Project));
        Assert.Single(replaced.PresetApplications.Items); Assert.Empty(PresetApplicationsProjection.Effects(replaced).Instances);
    }

    [Fact]
    public void 相对目标保留工程分支和动态节点单实例且不扩大文字目标()
    {
        var source = Source(); var package = CompositePresetDefinitionTests.ScopedPackage();
        var config = ScriptConfiguration.Create("words", "逐字", package, "scale", target: ScriptRelativeTarget.TokenFamily,
            readScope: ScriptReadScope.BranchInput, branchInput: "count");
        var preset = StyleTemplate.Create("words", "逐字组合", StyleTemplateKind.Composite, LyricStyle.Default, [], [TemplateEffectRecipe.FromConfiguration("words", config)], [package]);
        var project = Apply(source, preset, new(ProjectEffectBindingKind.Branch, id: "primary"), new Dictionary<string, string> { ["count"] = "primary" });
        var branchInstance = Assert.Single(PresetApplicationsProjection.Effects(project).Instances);
        Assert.Equal(ProjectEffectBindingKind.Branch, branchInstance.Binding.Kind); Assert.Equal("primary", branchInstance.Subscription.BranchId);
        Assert.False(PresetApplicationsEditor.TryApply(source, preset, [new(ProjectEffectBindingKind.Project)], null, out var rejected, out _)); Assert.Same(source, rejected);
        Assert.True(PresetApplicationTargets.CanApply(source, Global(), new(ProjectEffectBindingKind.Tokens, ids: ["t1"]), out _));
        Assert.False(PresetApplicationTargets.CanApply(source, Static("font", RgbaColor.Black, LyricStyleProperty.FontSizePx), new(ProjectEffectBindingKind.Tokens, ids: ["t1"]), out _));
        var node = new ProjectEffectBinding(ProjectEffectBindingKind.Node, id: source.LyricPresentation.Nodes[0].Id, includeDescendants: true);
        var nodeProject = Apply(source, preset, node, new Dictionary<string, string> { ["count"] = "primary" });
        Assert.Equal(node, Assert.Single(PresetApplicationsProjection.Effects(nodeProject).Instances).Binding);
        Assert.Single(PresetApplicationTargets.Resolve(source, new(ProjectEffectBindingKind.Project), TemplateEffectTarget.TokenFamily));
        Assert.Equal(2, PresetApplicationTargets.Resolve(source, new(ProjectEffectBindingKind.Lines, ids: ["l1"]), TemplateEffectTarget.TokenFamily).Length);
    }

    [Fact]
    public void 多选任一对象失效整次取消同一对象或身份重复拒绝()
    {
        var source = Source(); var preset = Global();
        Assert.False(PresetApplicationsEditor.TryApply(source, preset,
            [new(ProjectEffectBindingKind.Lines, ids: ["l1"]), new(ProjectEffectBindingKind.Lines, ids: ["missing"])], null, out var rejected, out _));
        Assert.Same(source, rejected); Assert.Empty(rejected.Effects.Packages);
        Assert.Throws<ArgumentException>(() => new PresetApplication("bad", new(ProjectEffectBindingKind.Lines, ids: ["l1", "l2"]), preset));
        var applied = Apply(source, preset, new(ProjectEffectBindingKind.Project)); var item = Assert.Single(applied.PresetApplications.Items);
        Assert.Throws<ArgumentException>(() => new PresetApplications([item, item]));
        var duplicate = new PresetApplication("other", item.Target, item.Snapshot, item.Identities);
        Assert.Throws<ArgumentException>(() => new PresetApplications([item, duplicate]));
        Assert.Throws<ArgumentException>(() => new PresetApplication("missing-identities", item.Target, preset));
    }

    [Fact]
    public void 工程快照搬移重开保持身份与完整资源且失效对象保留供移除()
    {
        using TemporaryDirectory directory = new(); string first = directory.FilePath("first");
        var project = Apply(Source(), Global(), new(ProjectEffectBindingKind.Lines, ids: ["l1"]));
        ProjectStore.Save(project, first); string moved = directory.FilePath("moved"); Directory.Move(first, moved);
        var loaded = ProjectStore.Load(moved); Assert.NotNull(loaded.Project); Assert.Equal(project.PresetApplications, loaded.Project.PresetApplications);
        Assert.Equal(PresetApplicationsJson.Encode(project.PresetApplications), PresetApplicationsJson.Encode(loaded.Project.PresetApplications));
        Assert.Equal(project.Lyrics, loaded.Project.Lyrics); Assert.Equal(project.LyricStyle, loaded.Project.LyricStyle);
        Assert.Equal(PresetApplicationsProjection.Effects(project), PresetApplicationsProjection.Effects(loaded.Project));
        var missing = project with { Lyrics = LyricsDocument.Create([project.Lyrics.Lines[1]]) };
        ProjectStore.Save(missing, moved); var invalid = ProjectStore.Load(moved); Assert.NotNull(invalid.Project); Assert.True(invalid.Report.Contains(DiagnosticCode.InvalidEffectInstance));
        Assert.Single(invalid.Project.PresetApplications.Items);
        var removed = PresetApplicationsEditor.Remove(invalid.Project, [new(ProjectEffectBindingKind.Lines, ids: ["l1"])]); Assert.Empty(removed.PresetApplications.Items);
        byte[] bytes = File.ReadAllBytes(Path.Combine(moved, "project.json"));
        Assert.Throws<EffectPackageException>(() => ProjectStore.Save(project with { Effects = ProjectEffects.Empty }, moved)); Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(moved, "project.json")));
    }

    [Theory]
    [InlineData("missing")] [InlineData("snapshot-version")] [InlineData("unknown")] [InlineData("duplicate")] [InlineData("identity")]
    [InlineData("resource")] [InlineData("seed")] [InlineData("package")]
    public void 损坏固定应用不返回部分工程并保持原文件(string failure)
    {
        using TemporaryDirectory directory = new(); var project = Apply(Source(), Global(), new(ProjectEffectBindingKind.Project)); ProjectStore.Save(project, directory.Path);
        string path = directory.FilePath("project.json"); var root = JsonNode.Parse(File.ReadAllText(path))!; var item = root["presetApplications"]!["items"]![0]!;
        switch (failure)
        {
            case "missing": root.AsObject().Remove("presetApplications"); break;
            case "snapshot-version": root["presetApplications"]!["templateSchemaVersion"] = 5; break;
            case "unknown": item["song"] = "ignored?"; break;
            case "duplicate": root["presetApplications"]!["items"]!.AsArray().Add(item.DeepClone()); break;
            case "identity": item["identities"]!.AsArray().Clear(); break;
            case "package": root["effects"]!["packages"]!.AsArray().Clear(); break;
            case "seed": item["identities"]![0]!["seed"] = "FFFFFFFFFFFFFFFF"; break;
            case "resource": File.WriteAllText(Path.Combine(directory.Path, ProjectEffectsJson.PackagePath(project.Effects.Packages[0].Revision), "src/PackEffect.cs"), "damaged"); break;
        }
        File.WriteAllText(path, root.ToJsonString()); byte[] bytes = File.ReadAllBytes(path); var result = ProjectStore.Load(directory.Path);
        Assert.Null(result.Project); Assert.True(result.Report.HasErrors); Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void 旧程序版本先于坏预设解析被拒绝()
    {
        using TemporaryDirectory directory = new(); ProjectStore.Save(Source(), directory.Path); string path = directory.FilePath("project.json");
        var root = JsonNode.Parse(File.ReadAllText(path))!; root["applicationVersion"] = "0.41.1"; root["schemaVersion"] = Project.CurrentSchemaVersion - 1; root["presetApplications"] = "bad";
        File.WriteAllText(path, root.ToJsonString()); byte[] bytes = File.ReadAllBytes(path); var result = ProjectStore.Load(directory.Path);
        Assert.Null(result.Project); Assert.True(result.Report.Contains(DiagnosticCode.ApplicationVersionMismatch));
        Assert.DoesNotContain("presetApplications", result.Report.ToSummary(), StringComparison.Ordinal); Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void 自动备份独立保留快照闭包原工程删除后可恢复()
    {
        using TemporaryDirectory directory = new(); var project = Apply(Source(), Global(), new(ProjectEffectBindingKind.Project));
        string original = directory.FilePath("project"); ProjectStore.Save(project, original);
        string backup = AutoSaveStore.Save(project, directory.FilePath("backups"), new DateTimeOffset(2026, 10, 7, 2, 0, 0, TimeSpan.Zero));
        Directory.Delete(original, true); var restored = ProjectStore.Load(backup); Assert.NotNull(restored.Project);
        Assert.Equal(project.PresetApplications, restored.Project.PresetApplications);
        Assert.Equal(PresetApplicationsProjection.Effects(project), PresetApplicationsProjection.Effects(restored.Project));
        Assert.Empty(Directory.GetDirectories(directory.FilePath("backups"), "*.tmp"));
    }

    [Fact]
    public void 仅声明工程绑定的逐字控制器不要求伪造tokens绑定或逐字实例()
    {
        var files = EffectPackageFixture.Files(); var manifest = EffectPackageFixture.Manifest(files);
        manifest["exports"]![0]!["bindingKinds"] = new JsonArray("project");
        var package = EffectPackageContents.Create(EffectPackageFixture.Bytes(manifest), EffectPackageFixture.Inputs(files), EffectPackageFixture.Profile);
        var configuration = ScriptConfiguration.Create("project-only", "工程逐字强调", package, "scale", target: ScriptRelativeTarget.TokenFamily);
        var preset = StyleTemplate.Create("global-words", "逐字组合", StyleTemplateKind.Composite, LyricStyle.Default, [], [TemplateEffectRecipe.FromConfiguration("words", configuration)], [package]);
        var project = Apply(Source(), preset, new(ProjectEffectBindingKind.Project));
        Assert.Equal(ProjectEffectBindingKind.Project, Assert.Single(PresetApplicationsProjection.Effects(project).Instances).Binding.Kind);
        Assert.False(PresetApplicationTargets.CanApply(Source(), preset, new(ProjectEffectBindingKind.Branch, id: "primary"), out _));
    }
}
