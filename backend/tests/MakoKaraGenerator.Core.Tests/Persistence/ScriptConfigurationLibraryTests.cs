using System.Text;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Persistence;

namespace MakoKaraGenerator.Core.Tests.Persistence;

public sealed class ScriptConfigurationLibraryTests
{
    private static ScriptConfiguration Configuration(EffectPackageContents? package = null, string id = "emphasis")
        => ScriptConfiguration.Create(id, "逐字强调", package ?? EffectPackageFixture.Contents(), "scale",
            EffectParameterOverrides.Parse("{\"peak\":1.2345678901234567}"), ScriptRelativeTarget.TokenFamily,
            ScriptReadScope.BranchInput, "count-source", ulong.MaxValue);

    [Fact]
    public void 独立配置库搬移后保持源码资源参数精度及完整种子()
    {
        using TemporaryDirectory temp = new(); var original = Configuration();
        string path = ScriptConfigurationLibrary.ResolvePath(temp.FilePath("first"));
        ScriptConfigurationLibrary.Save(path, [original]);
        string old = Path.GetDirectoryName(path)!; string moved = temp.FilePath("moved"); Directory.Move(old, moved);
        var loaded = ScriptConfigurationLibrary.Load(Path.Combine(moved, ScriptConfigurationLibrary.FileName));
        Assert.Empty(loaded.Issues); var item = Assert.Single(loaded.Configurations);
        Assert.Equal(original, item);
        Assert.Equal(ScriptConfigurationLibrary.Encode([original]), ScriptConfigurationLibrary.Encode([item]));
        Assert.Equal(original.Package.Revision, item.Package.Revision);
        Assert.Equal(original.Package.Files.Single(file => file.Path == "src/PackEffect.cs").Content.ToArray(), item.Package.Files.Single(file => file.Path == "src/PackEffect.cs").Content.ToArray());
        Assert.Equal(ulong.MaxValue, item.FixedSeed);
        var instance = item.CreateInstance("instance", new(ProjectEffectBindingKind.Tokens, ids: ["actual-token"]), 2,
            new Dictionary<string, string> { ["count-source"] = "other-branch" });
        Assert.Equal("other-branch", instance.Subscription.BranchId); Assert.Equal(ulong.MaxValue, instance.Seed);
        Assert.Equal(item.Parameters, instance.Parameters);
        Assert.Throws<ArgumentException>(() => item.CreateInstance("instance", instance.Binding, 2));
        Assert.DoesNotContain("actual-token", Encoding.UTF8.GetString(ScriptConfigurationLibrary.Encode([item])));
        Assert.DoesNotContain("other-branch", Encoding.UTF8.GetString(ScriptConfigurationLibrary.Encode([item])));
    }

    [Fact]
    public void 全局是绑定范围并保持单个实例与独立订阅()
    {
        var files = EffectPackageFixture.Files(); var manifest = EffectPackageFixture.Manifest(files);
        manifest["exports"]![0]!["bindingKinds"] = new JsonArray("project", "branch", "lines", "tokens", "clips", "node");
        var package = EffectPackageContents.Create(EffectPackageFixture.Bytes(manifest), EffectPackageFixture.Inputs(files), EffectPackageFixture.Profile);
        var configuration = ScriptConfiguration.Create("global", "工程逐字强调", package, "scale", readScope: ScriptReadScope.Project);
        var instance = configuration.CreateInstance("global-instance", new(ProjectEffectBindingKind.Project), 42);
        var effects = new ProjectEffects([package], [instance]);
        Assert.Single(effects.Instances); Assert.Equal(ScriptConfigurationKind.Effect, configuration.Kind);
        Assert.Equal(ProjectEffectBindingKind.Project, instance.Binding.Kind); Assert.Empty(instance.Binding.Ids);
        Assert.Equal("project", instance.Subscription.Kind); Assert.Equal(42UL, instance.Seed);
        Assert.Throws<InvalidOperationException>(() => configuration.ToMaterial());
    }

    [Fact]
    public void 材质配置复制为固定材质且不伪装为实例()
    {
        var package = MixedEffectPackageFixture.MaterialContents();
        var configuration = ScriptConfiguration.Create("glass", "玻璃", package, "ink");
        Assert.Equal(ScriptConfigurationKind.Material, configuration.Kind); Assert.Empty(configuration.BindingKinds);
        var material = configuration.ToMaterial(); Assert.Equal(package.Revision, material.Revision); Assert.Equal("ink", material.ExportId);
        Assert.Throws<ArgumentException>(() => configuration.CreateInstance("fake", new(ProjectEffectBindingKind.Project), 1));
        Assert.Throws<ArgumentException>(() => ScriptConfiguration.Create("glass", "玻璃", package, "ink", fixedSeed: 1));
        Assert.Throws<ArgumentException>(() => ScriptConfiguration.Create("glass", "玻璃", package, "ink", readScope: ScriptReadScope.Project));
        using TemporaryDirectory temp = new(); string path = ScriptConfigurationLibrary.ResolvePath(temp.Path);
        ScriptConfigurationLibrary.Save(path, [configuration]); var loaded = ScriptConfigurationLibrary.Load(path);
        Assert.Empty(loaded.Issues); Assert.Equal(material, Assert.Single(loaded.Configurations).ToMaterial());
    }

    [Fact]
    public void 失败源码可以保存完整新修订且不回退旧字节()
    {
        using TemporaryDirectory temp = new(); string path = ScriptConfigurationLibrary.ResolvePath(temp.Path);
        var old = Configuration(); ScriptConfigurationLibrary.Save(path, [old]);
        var files = EffectPackageFixture.Files(); files["src/PackEffect.cs"] = Encoding.UTF8.GetBytes("this is failed author source");
        var failed = EffectPackageContents.Create(EffectPackageFixture.Bytes(EffectPackageFixture.Manifest(files)), EffectPackageFixture.Inputs(files), EffectPackageFixture.Profile);
        var configuration = Configuration(failed); ScriptConfigurationLibrary.Save(path, [configuration]);
        var loaded = Assert.Single(ScriptConfigurationLibrary.Load(path).Configurations);
        Assert.NotEqual(old.Package.Revision, loaded.Package.Revision); Assert.Equal(failed.Revision, loaded.Package.Revision);
        Assert.Equal(files["src/PackEffect.cs"], loaded.Package.Files.Single(file => file.Path == "src/PackEffect.cs").Content.ToArray());
        Assert.True(Directory.Exists(Path.Combine(Path.GetDirectoryName(path)!, ScriptConfigurationLibrary.PackagePath(old.Package.Revision))));
    }

    [Theory]
    [InlineData("version")] [InlineData("schema-type")] [InlineData("unknown")] [InlineData("path")]
    [InlineData("parameter")] [InlineData("role")] [InlineData("seed")] [InlineData("resource")]
    public void 破损配置整库拒绝并保护旧入口(string failure)
    {
        using TemporaryDirectory temp = new(); var configuration = Configuration(); string path = ScriptConfigurationLibrary.ResolvePath(temp.Path);
        ScriptConfigurationLibrary.Save(path, [configuration]); var root = JsonNode.Parse(File.ReadAllText(path))!;
        var item = root["configurations"]![0]!;
        switch (failure)
        {
            case "version": root["schemaVersion"] = 2; break;
            case "schema-type": root["schemaVersion"] = "one"; break;
            case "unknown": item["songId"] = "unexpected"; break;
            case "path": root["packages"]![0]!["path"] = "../outside"; break;
            case "parameter": item["parameters"]!["peak"] = 99; break;
            case "role": item["branchInput"] = null; break;
            case "seed": item["fixedSeed"] = "FFFFFFFFFFFFFFFF"; break;
            case "resource": File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!, ScriptConfigurationLibrary.PackagePath(configuration.Package.Revision), "src/PackEffect.cs"), "corrupt"); break;
        }
        File.WriteAllText(path, root.ToJsonString()); byte[] bytes = File.ReadAllBytes(path);
        var result = ScriptConfigurationLibrary.Load(path); Assert.Empty(result.Configurations); Assert.NotEmpty(result.Issues);
        Assert.Equal(failure == "version", result.VersionMismatch);
        Assert.Throws<IOException>(() => ScriptConfigurationLibrary.Save(path, [])); Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void 共享修订去重预算覆盖全库且删除不删除历史资源()
    {
        using TemporaryDirectory temp = new(); string path = ScriptConfigurationLibrary.ResolvePath(temp.Path);
        var first = Configuration(); var second = Configuration(first.Package, "other");
        ScriptConfigurationLibrary.Save(path, [first, second]); byte[] bytes = File.ReadAllBytes(path);
        var tiny = new EffectPackageContentBudget { MaximumFiles = 4 };
        Assert.NotEmpty(ScriptConfigurationLibrary.Load(path, tiny).Issues);
        Assert.Throws<EffectPackageException>(() => ScriptConfigurationLibrary.Save(path, [first, second], tiny));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Empty(ScriptConfigurationLibrary.Load(path, tiny with { MaximumFiles = 5 }).Issues);
        ScriptConfigurationLibrary.Save(path, []); Assert.Empty(ScriptConfigurationLibrary.Load(path).Configurations);
        Assert.True(Directory.Exists(Path.Combine(Path.GetDirectoryName(path)!, ScriptConfigurationLibrary.PackagePath(first.Package.Revision))));
    }

    [Fact]
    public void 链接入口或包目录拒绝访问外部资源()
    {
        using TemporaryDirectory temp = new(); var configuration = Configuration(); string path = ScriptConfigurationLibrary.ResolvePath(temp.Path);
        ScriptConfigurationLibrary.Save(path, [configuration]); string original = temp.FilePath("outside.json");
        File.Move(path, original); File.CreateSymbolicLink(path, original);
        Assert.NotEmpty(ScriptConfigurationLibrary.Load(path).Issues); Assert.ThrowsAny<IOException>(() => ScriptConfigurationLibrary.Save(path, []));
        File.Delete(path); File.Move(original, path);
        string package = Path.Combine(Path.GetDirectoryName(path)!, ScriptConfigurationLibrary.PackagePath(configuration.Package.Revision));
        Directory.Move(package, temp.FilePath("outside-package")); Directory.CreateSymbolicLink(package, temp.FilePath("outside-package"));
        Assert.NotEmpty(ScriptConfigurationLibrary.Load(path).Issues);
    }

    [Fact]
    public void 配置种类与相对目标必须遵守实际导出能力()
    {
        var package = EffectPackageFixture.Contents();
        Assert.Throws<ArgumentException>(() => ScriptConfiguration.Create("bad", "错误", package, "scale", target: ScriptRelativeTarget.Line));
        Assert.Throws<ArgumentException>(() => ScriptConfiguration.Create("bad", "错误", package, "scale", branchInput: "concrete-id"));
        Assert.Throws<ArgumentException>(() => ScriptConfiguration.Create("bad", "错误", package, "scale", overlayLayer: EffectOverlayLayer.AboveText));
        var configuration = Configuration();
        Assert.Throws<ArgumentException>(() => ScriptConfigurationLibrary.Encode([configuration, configuration]));
        Assert.Throws<ArgumentException>(() => configuration.CreateInstance("bad", new(ProjectEffectBindingKind.Project), 1));
        Assert.Throws<EffectPackageException>(() => ScriptConfiguration.Create("bad", "错误", package, "scale", EffectParameterOverrides.Parse("{\"peak\":99}")));
    }

    [Fact]
    public void 组输出声明不放宽实际工程或节点写权()
    {
        var package = EffectPackageFixture.GroupTransform();
        var configuration = ScriptConfiguration.Create("group", "组变换", package, "group-transform");
        Assert.Equal(["project", "node"], configuration.BindingKinds.ToArray());
        Assert.Throws<ArgumentException>(() => configuration.CreateInstance("group", new(ProjectEffectBindingKind.Branch, id: "source"), 1));
        Assert.Equal(ProjectEffectBindingKind.Node,
            configuration.CreateInstance("group", new(ProjectEffectBindingKind.Node, id: "actual-node"), 1).Binding.Kind);
    }
}
