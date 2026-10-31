using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Core.Tests.Projects;

public sealed class CompositePresetDefinitionTests
{
    internal static EffectPackageContents ScopedPackage()
    {
        var files = EffectPackageFixture.Files(); var manifest = EffectPackageFixture.Manifest(files);
        manifest["exports"]![0]!["bindingKinds"] = new JsonArray("project", "branch", "lines", "tokens", "clips", "node");
        return EffectPackageContents.Create(EffectPackageFixture.Bytes(manifest), EffectPackageFixture.Inputs(files), EffectPackageFixture.Profile);
    }

    [Fact]
    public void 组合预设固定全局配置和局部材质资源且库变化不跟随()
    {
        using TemporaryDirectory directory = new(); var controller = ScopedPackage(); var material = MixedEffectPackageFixture.MaterialContents();
        var config = ScriptConfiguration.Create("shared", "工程强调", controller, "scale", readScope: ScriptReadScope.BranchInput,
            branchInput: "count", fixedSeed: ulong.MaxValue);
        var recipe = TemplateEffectRecipe.FromConfiguration("global", config);
        var style = LyricStyle.Default with { UnsungFill = new() { Material = ScriptConfiguration.Create("glass", "玻璃", material, "ink").ToMaterial() } };
        var preset = StyleTemplate.Create("composite", "玻璃与强调", StyleTemplateKind.Composite, style,
            [LyricStyleProperty.UnsungColor], [recipe], [controller, material]);
        string path = StyleTemplateLibrary.ResolvePath(directory.Path); StyleTemplateLibrary.Save(path, [preset]);
        string configs = ScriptConfigurationLibrary.ResolvePath(directory.Path); ScriptConfigurationLibrary.Save(configs, [config]);
        ScriptConfigurationLibrary.Save(configs, []); Directory.Delete(Path.GetDirectoryName(configs)!, true);
        var result = StyleTemplateLibrary.Load(path); Assert.Empty(result.Issues); var loaded = Assert.Single(result.Templates);
        Assert.Equal(preset, loaded); Assert.Equal(preset.GetHashCode(), loaded.GetHashCode());
        var fixedConfig = Assert.Single(loaded.EffectRecipes).ToConfiguration(loaded.Packages.Single(p => p.PackageId == controller.PackageId && p.Revision == controller.Revision));
        Assert.Equal(config, fixedConfig); Assert.Equal(TemplateEffectTarget.Application, recipe.Target);
        Assert.Equal(ulong.MaxValue, fixedConfig.FixedSeed); Assert.Equal("count", fixedConfig.BranchInput);
        Assert.Equal("shared", recipe.ConfigurationId); Assert.Equal(2, loaded.Packages.Length);
    }

    [Fact]
    public void 配方保存启停和执行顺序并按实际绑定能力拒绝组越权()
    {
        using TemporaryDirectory directory = new(); var group = EffectPackageFixture.GroupTransform();
        var first = new TemplateEffectRecipe("first", "禁用组", TemplateEffectTarget.Application, group.PackageId, group.Revision, "group-transform",
            enabled: false, readScope: ScriptReadScope.Project);
        var second = new TemplateEffectRecipe("second", "启用组", TemplateEffectTarget.Application, group.PackageId, group.Revision, "group-transform");
        var preset = StyleTemplate.Create("group", "组合组", StyleTemplateKind.Composite, LyricStyle.Default, [], [first, second], [group]);
        string path = StyleTemplateLibrary.ResolvePath(directory.Path); StyleTemplateLibrary.Save(path, [preset]);
        var loaded = Assert.Single(StyleTemplateLibrary.Load(path).Templates);
        Assert.Equal(preset, loaded); Assert.Equal([false, true], loaded.EffectRecipes.Select(r => r.Enabled).ToArray());
        Assert.Throws<ArgumentException>(() => StyleTemplate.Create("bad", "行不能写组", StyleTemplateKind.Line, LyricStyle.Default, [], [first], [group]));
        var instance = first.ToConfiguration(group).CreateInstance("one", new(ProjectEffectBindingKind.Project), 3);
        Assert.Equal(ProjectEffectBindingKind.Project, instance.Binding.Kind); Assert.Equal("project", instance.Subscription.Kind);
        Assert.Throws<ArgumentException>(() => first.ToConfiguration(group).CreateInstance("bad", new(ProjectEffectBindingKind.Branch, id: "lyrics"), 3));
    }

    [Fact]
    public void 旧模板库保留原字节不转换或覆写()
    {
        using TemporaryDirectory directory = new(); string oldDirectory = directory.FilePath("templates-v5"); Directory.CreateDirectory(oldDirectory);
        string old = Path.Combine(oldDirectory, "index.json"); string text = "{\"schemaVersion\":5,\"unknownLegacy\":true}"; File.WriteAllText(old, text);
        string path = StyleTemplateLibrary.ResolvePath(directory.Path); var result = StyleTemplateLibrary.Load(path);
        Assert.Empty(result.Templates); Assert.Empty(result.Issues); Assert.True(result.LegacyLibraryRetained);
        Assert.Equal("templates-v6", Path.GetFileName(Path.GetDirectoryName(path)));
        StyleTemplateLibrary.Save(path, []); Assert.Equal(text, File.ReadAllText(old));
        var rejected = StyleTemplateLibrary.Load(old); Assert.True(rejected.VersionMismatch);
        Assert.Throws<IOException>(() => StyleTemplateLibrary.Save(old, [])); Assert.Equal(text, File.ReadAllText(old));
    }

    [Theory]
    [InlineData("missing")] [InlineData("scope")] [InlineData("seed")] [InlineData("branch")] [InlineData("enabled")]
    public void 新配方字段严格读取不会猜测订阅或种子(string failure)
    {
        using TemporaryDirectory directory = new(); var package = ScopedPackage();
        var recipe = new TemplateEffectRecipe("r", "强调", TemplateEffectTarget.Application, package.PackageId, package.Revision, "scale");
        var preset = StyleTemplate.Create("p", "组合", StyleTemplateKind.Composite, LyricStyle.Default, [], [recipe], [package]);
        string path = StyleTemplateLibrary.ResolvePath(directory.Path); StyleTemplateLibrary.Save(path, [preset]);
        var root = JsonNode.Parse(File.ReadAllText(path))!; var item = root["templates"]![0]!["effectRecipes"]![0]!;
        switch (failure)
        {
            case "missing": item.AsObject().Remove("readScope"); break;
            case "scope": item["readScope"] = "unknown"; break;
            case "seed": item["fixedSeed"] = "FFFFFFFFFFFFFFFF"; break;
            case "branch": item["branchInput"] = "concrete-without-role"; break;
            case "enabled": item["enabled"] = "true"; break;
        }
        File.WriteAllText(path, root.ToJsonString()); byte[] bytes = File.ReadAllBytes(path);
        var result = StyleTemplateLibrary.Load(path); Assert.Empty(result.Templates); Assert.NotEmpty(result.Issues);
        Assert.Throws<IOException>(() => StyleTemplateLibrary.Save(path, [])); Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void 材质不能装入效果配方且相对目标需要显式选择()
    {
        var material = MixedEffectPackageFixture.MaterialContents();
        var config = ScriptConfiguration.Create("glass", "玻璃", material, "ink");
        Assert.Throws<ArgumentException>(() => TemplateEffectRecipe.FromConfiguration("bad", config));
        var package = ScopedPackage(); var relative = ScriptConfiguration.Create("token", "逐字", package, "scale", target: ScriptRelativeTarget.TokenFamily);
        var recipe = TemplateEffectRecipe.FromConfiguration("words", relative);
        Assert.Equal(TemplateEffectTarget.TokenFamily, recipe.Target); Assert.Equal(relative, recipe.ToConfiguration(package));
        Assert.Throws<ArgumentException>(() => recipe.ToConfiguration(EffectPackageFixture.Contents(preview: 2)));
    }

    [Theory]
    [InlineData(LyricStyleProperty.VerticalCenterRatio)] [InlineData(LyricStyleProperty.HorizontalAlignment)]
    public void 组合预设不携带画面布局属性(LyricStyleProperty property)
    {
        Assert.Throws<ArgumentException>(() => StyleTemplate.Create("layout", "位置", StyleTemplateKind.Composite, LyricStyle.Default, [property]));
    }
}
