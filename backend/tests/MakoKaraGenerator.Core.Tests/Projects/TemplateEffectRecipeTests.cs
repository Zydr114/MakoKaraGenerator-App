using System.Collections.Immutable;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Core.Tests.Projects;

public sealed class TemplateEffectRecipeTests
{
    private static Project Source() => Project.Create("template", lyrics: LyricsDocument.Create([
        LyricLine.Create("row", "甲乙", new(0, 1000000), TimingStatus.Edited,
            [LyricToken.Create("a", "甲", new(0, 500000), TimingStatus.Edited), LyricToken.Create("b", "乙", new(500000, 1000000), TimingStatus.Edited)]),
        LyricLine.Create("missing", "丙", null, TimingStatus.Missing, [LyricToken.Create("c", "丙", null, TimingStatus.Missing)])]));
    private static StyleTemplate Template(string id = "template", double peak = 2, byte resource = 1, string[]? recipes = null, bool line = true)
    {
        var package = EffectPackageFixture.Contents(resource);
        return StyleTemplate.Create(id, "效果", line ? StyleTemplateKind.Line : StyleTemplateKind.Token, LyricStyle.Default,
            [LyricStyleProperty.SungColor], (recipes ?? ["first"]).Select(r => new TemplateEffectRecipe(r, r, TemplateEffectTarget.TokenFamily,
                package.PackageId, package.Revision, "scale", EffectParameterOverrides.Parse($"{{\"peak\":{peak}}}"))), [package]);
    }
    private static StyleTemplateApplyPreview Apply(Project source, StyleTemplate template, bool all = false)
    {
        Assert.True(StyleTemplateApplicator.TryPreview(source, template, "row", "a", all, out var preview, out var reason), reason); return preview;
    }
    [Fact]
    public void 模板静态及效果一次套用并保留身份禁用种子且重复无变化()
    {
        var source = Source(); var applied = Apply(source, Template()); Assert.Equal(1, applied.ChangedCount);
        Assert.Empty(source.Effects.Instances); Assert.Equal(2, applied.Result.Effects.Instances.Length);
        Assert.All(applied.Result.Effects.Instances, i => { Assert.Equal("row", i.TemplateOrigin!.OwnerId); Assert.Equal(StyleTemplateKind.Line, i.TemplateOrigin.OwnerKind); });
        var disabled = applied.Result with { Effects = new(applied.Result.Effects.Packages, applied.Result.Effects.Instances.Select(i => i with { Enabled = false, Seed = ulong.MaxValue })) };
        var repeat = Apply(disabled, Template()); Assert.Same(disabled, repeat.Result); Assert.Equal(0, repeat.ChangedCount);
        var edited = Apply(disabled, Template(peak: 3, resource: 2)); Assert.Equal(1, edited.ChangedCount);
        Assert.Equal(disabled.Effects.Instances.Select(i => i.Id), edited.Result.Effects.Instances.Select(i => i.Id));
        Assert.All(edited.Result.Effects.Instances, i => { Assert.False(i.Enabled); Assert.Equal(ulong.MaxValue, i.Seed); Assert.Contains("3", i.Parameters.Json); });
        Assert.Equal(2, edited.Result.Effects.Packages.Length);
    }
    [Fact]
    public void 配方重排删除更新同槽位而保留其他模板和直接实例顺序()
    {
        var source = Apply(Source(), Template(recipes: ["first", "second"])).Result;
        var other = Apply(source, Template("other")).Result;
        other = other with { Effects = ProjectEffectsEditor.Duplicate(other.Effects, other.Effects.Instances[0].Id) };
        var direct = other.Effects.Instances[^1]; Assert.Null(direct.TemplateOrigin);
        var result = Apply(other, Template(recipes: ["second", "first"])).Result;
        Assert.Equal(["second", "second", "first", "first"], result.Effects.Instances.Take(4).Select(i => i.TemplateOrigin!.RecipeId));
        Assert.Equal(other.Effects.Instances.Skip(4), result.Effects.Instances.Skip(4));
        var removed = Apply(result, Template(recipes: ["second"])).Result;
        Assert.DoesNotContain(removed.Effects.Instances, i => i.TemplateOrigin?.TemplateId == "template" && i.TemplateOrigin.RecipeId == "first");
        Assert.Contains(direct, removed.Effects.Instances);
        Assert.True(StyleTemplateApplicator.TryRemoveEffects(removed, "template", StyleTemplateKind.Line, "row", null, false, out var clean, out _));
        Assert.Equal(1, clean.ChangedCount); Assert.Equal(removed.Lyrics, clean.Result.Lyrics);
        Assert.All(clean.Result.Effects.Instances, i => Assert.True(i.TemplateOrigin is null || i.TemplateOrigin.TemplateId == "other"));
        Assert.Contains(direct, clean.Result.Effects.Instances);
    }
    [Fact]
    public void 全工程包括未定时元素及其他分支并允许纯效果模板()
    {
        var original = Source(); var branch = LyricBranch.Create("translation", "翻译", LyricBranchRole.Translation,
            [LyricLine.Create("translated", "翻", null, TimingStatus.Missing, [LyricToken.Create("d", "翻", null, TimingStatus.Missing)])]);
        original = original with { Lyrics = original.Lyrics with { Branches = original.Lyrics.Branches.Add(branch) } };
        var template = Template(line: false) with { Properties = [] }; var result = Apply(original, template, true);
        Assert.Equal(4, result.TargetCount); Assert.Equal(4, result.ChangedCount); Assert.Equal(4, result.Result.Effects.Instances.Length);
        Assert.All(result.Result.Lyrics.AllLines, l => Assert.Null(l.StyleOverride));
        Assert.All(result.Result.Effects.Instances, i => Assert.Equal(i.Binding.Ids[0], i.TemplateOrigin!.OwnerId));
        Assert.Same(result.Result, Apply(result.Result, template, true).Result);
    }
    [Fact]
    public void 行成员移除后重套用清理旧配方但保留资源修订()
    {
        var applied = Apply(Source(), Template()).Result;
        var line = applied.Lyrics.Lines[0] with { Tokens = [applied.Lyrics.Lines[0].Tokens[0]], Text = "甲" };
        var changed = applied with { Lyrics = applied.Lyrics with { Lines = applied.Lyrics.Lines.SetItem(0, line) } };
        var result = Apply(changed, Template()).Result;
        Assert.Equal("a", Assert.Single(result.Effects.Instances).Binding.Ids[0]); Assert.Single(result.Effects.Packages);
    }
    [Fact]
    public void 旧模板资源删除不影响已套用工程且新字段必须保存()
    {
        using TemporaryDirectory temp = new(); var template = Template(); string library = StyleTemplateLibrary.ResolvePath(temp.Path);
        StyleTemplateLibrary.Save(library, [template]); var applied = Apply(Source(), StyleTemplateLibrary.Load(library).Templates[0]).Result;
        string directory = temp.FilePath("project.mkgproj"); ProjectStore.Save(applied, directory);
        StyleTemplateLibrary.Save(library, []); Directory.Delete(Path.GetDirectoryName(library)!, true);
        var loaded = ProjectStore.Load(directory); Assert.NotNull(loaded.Project); Assert.Equal(applied.Effects, loaded.Project.Effects);
        string file = Path.Combine(directory, "project.json"); var root = JsonNode.Parse(File.ReadAllText(file))!;
        root["effects"]!["instances"]![0]!.AsObject().Remove("templateOrigin"); File.WriteAllText(file, root.ToJsonString());
        Assert.Null(ProjectStore.Load(directory).Project);
    }
    [Theory]
    [InlineData("group")] [InlineData("material")] [InlineData("binding")] [InlineData("parameter")] [InlineData("overlay")] [InlineData("duplicate")] [InlineData("missing")]
    public void 无效配方拒绝且不能将坏闭包当作静态模板(string problem)
    {
        var template = Template(); var package = template.Packages[0]; var recipe = template.EffectRecipes[0];
        switch (problem)
        {
            case "group": package = EffectPackageFixture.GroupTransform(); recipe = new("r", "r", TemplateEffectTarget.TokenFamily, package.PackageId, package.Revision, "group-transform"); break;
            case "material": package = MixedEffectPackageFixture.Contents(); recipe = new("r", "r", TemplateEffectTarget.TokenFamily, package.PackageId, package.Revision, "ink"); break;
            case "binding": recipe = new("r", "r", TemplateEffectTarget.Line, package.PackageId, package.Revision, "scale"); break;
            case "parameter": recipe = new("r", "r", TemplateEffectTarget.TokenFamily, package.PackageId, package.Revision, "scale", EffectParameterOverrides.Parse("{\"peak\":99}")); break;
            case "overlay": recipe = new("r", "r", TemplateEffectTarget.TokenFamily, package.PackageId, package.Revision, "scale", overlayLayer: EffectOverlayLayer.AboveText); break;
        }
        void Create() => StyleTemplate.Create("t", "t", StyleTemplateKind.Line, LyricStyle.Default, [],
            problem == "duplicate" ? [recipe, recipe] : [recipe], problem == "missing" ? [] : [package]);
        if (problem == "parameter") Assert.Throws<EffectPackageException>(Create); else Assert.Throws<ArgumentException>(Create);
    }
    [Fact]
    public void 同模板同目标同配方不能重复持久实例且直接复制移除来源()
    {
        var applied = Apply(Source(), Template()).Result; var original = applied.Effects.Instances[0];
        Assert.Throws<ArgumentException>(() => new ProjectEffects(applied.Effects.Packages, [original, original with { Id = "different" }]));
        var duplicate = ProjectEffectsEditor.Duplicate(applied.Effects, original.Id).Instances[^1]; Assert.Null(duplicate.TemplateOrigin);
        Assert.NotEqual(original.Seed, duplicate.Seed); Assert.NotEqual(original.Id, duplicate.Id);
    }

    [Theory]
    [InlineData(StyleTemplateKind.Line, "line")]
    [InlineData(StyleTemplateKind.Line, "textMark")]
    [InlineData(StyleTemplateKind.Token, "line")]
    [InlineData(StyleTemplateKind.Token, "textMark")]
    public void 元素族配方不能借tokens声明写入行或标记(StyleTemplateKind kind, string outputTarget)
    {
        EffectPackageContents package = MixedEffectPackageFixture.Contents(pure: true,
            manifestChange: manifest => manifest["exports"]!.AsArray()
                .Single(export => export!["id"]!.GetValue<string>() == "tint")!["outputs"]![0]!["target"] = outputTarget);
        _ = EffectPackageDefinition.Read(package);
        TemplateEffectRecipe recipe = new("r", "元素族", TemplateEffectTarget.TokenFamily,
            package.PackageId, package.Revision, "tint");

        Assert.Throws<ArgumentException>(() => StyleTemplate.Create("t", "模板", kind,
            LyricStyle.Default, [], [recipe], [package]));
    }

    [Fact]
    public void 同配方显式换目标后重套用清理旧来源并保留直接实例()
    {
        var files = EffectPackageFixture.Files();
        var manifest = EffectPackageFixture.Manifest(files);
        manifest["exports"]![0]!["bindingKinds"] = new JsonArray("lines", "tokens");
        var package = EffectPackageContents.Create(EffectPackageFixture.Bytes(manifest), EffectPackageFixture.Inputs(files), EffectPackageFixture.Profile);
        StyleTemplate TemplateFor(TemplateEffectTarget target) => StyleTemplate.Create("same-template", "换目标", StyleTemplateKind.Line,
            LyricStyle.Default, [], [new("same-recipe", "强调", target, package.PackageId, package.Revision, "scale")], [package]);
        Project first = Apply(Source(), TemplateFor(TemplateEffectTarget.Line)).Result;
        ProjectEffectInstance old = Assert.Single(first.Effects.Instances);
        var disabled = new ProjectEffects(first.Effects.Packages, [old with { Enabled = false, Seed = ulong.MaxValue }]);
        var effects = ProjectEffectsEditor.Duplicate(disabled, old.Id);
        ProjectEffectInstance direct = effects.Instances[^1];

        Project expanded = Apply(first with { Effects = effects }, TemplateFor(TemplateEffectTarget.TokenFamily)).Result;
        Assert.Contains(direct, expanded.Effects.Instances);
        Assert.False(direct.Enabled);
        Assert.DoesNotContain(expanded.Effects.Instances, instance => instance.Id == old.Id);
        Assert.Equal(["a", "b"], expanded.Effects.Instances.Where(instance => instance.TemplateOrigin is not null)
            .Select(instance => Assert.Single(instance.Binding.Ids)));
        Assert.All(expanded.Effects.Instances.Where(instance => instance.TemplateOrigin is not null), instance =>
        {
            Assert.Equal(ProjectEffectBindingKind.Tokens, instance.Binding.Kind);
            Assert.Equal("same-recipe", instance.TemplateOrigin!.RecipeId);
            Assert.True(instance.Enabled);
        });
        Assert.Same(expanded, Apply(expanded, TemplateFor(TemplateEffectTarget.TokenFamily)).Result);
        Project collapsed = Apply(expanded, TemplateFor(TemplateEffectTarget.Line)).Result;
        Assert.Contains(direct, collapsed.Effects.Instances);
        Assert.Equal(ProjectEffectBindingKind.Lines,
            Assert.Single(collapsed.Effects.Instances.Where(instance => instance.TemplateOrigin is not null)).Binding.Kind);
    }
}
