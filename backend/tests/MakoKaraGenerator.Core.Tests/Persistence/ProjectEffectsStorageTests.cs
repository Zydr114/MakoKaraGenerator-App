using System.Text;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Validation;

namespace MakoKaraGenerator.Core.Tests.Persistence;

public sealed class ProjectEffectsStorageTests
{
    private static ProjectEffectInstance Instance(EffectPackageContents package, string id = "fx:one") => new()
    {
        Id = id, Name = "强调", PackageId = package.PackageId, Revision = package.Revision, ExportId = "scale",
        Binding = new(ProjectEffectBindingKind.Tokens, ids: ["token"]), Seed = ulong.MaxValue,
        Parameters = EffectParameterOverrides.Parse("{\"peak\":1.2345678901234567}"),
    };
    private static Project Source()
    {
        var package = EffectPackageFixture.Contents(); return Project.Create("持久效果") with { Effects = new([package], [Instance(package)]) };
    }
    private static JsonObject Entry(string directory) => JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "project.json")))!.AsObject();
    private static void Change(string directory, Action<JsonObject> edit)
    { var entry = Entry(directory); edit(entry); File.WriteAllText(Path.Combine(directory, "project.json"), entry.ToJsonString()); }

    [Fact]
    public void 全部绑定订阅多修订顺序预算与完整种子可重新打开()
    {
        using TemporaryDirectory directory = new(); var first = EffectPackageFixture.Contents(); var second = EffectPackageFixture.Contents(2);
        var bindings = new[] { new ProjectEffectBinding(ProjectEffectBindingKind.Project), new(ProjectEffectBindingKind.Branch, "primary"),
            new(ProjectEffectBindingKind.Lines, ids: ["l2", "l1"]), new(ProjectEffectBindingKind.Tokens, ids: ["t2", "t1"]),
            new(ProjectEffectBindingKind.Clips, ids: ["c2", "c1"]), new(ProjectEffectBindingKind.Node, "node", includeDescendants: true) };
        var instances = bindings.Select((binding, i) => Instance(i % 2 == 0 ? first : second, "fx:" + i) with
        { Binding = binding, Subscription = new(i % 3 == 0 ? "project" : i % 3 == 1 ? "branch" : "boundSources", i % 3 == 1 ? "primary" : null), Enabled = i % 2 == 0 }).ToArray();
        var effects = new ProjectEffects([first, second], instances, new() { SurfaceMemoryBytes = 12345678, LiveParticles = 3, TrailSamples = 4, EmissionRecords = 5, DrawCommands = 6 });
        var project = Project.Create("全部") with { Effects = effects }; ProjectStore.Save(project, directory.Path);
        var restored = ProjectStore.Load(directory.Path); Assert.NotNull(restored.Project); Assert.Equal(effects, restored.Project.Effects);
        Assert.Equal(effects.GetHashCode(), restored.Project.Effects.GetHashCode()); Assert.Equal(effects.InputsFingerprint, restored.Project.Effects.InputsFingerprint);
        Assert.Equal("ffffffffffffffff", Entry(directory.Path)["effects"]!["instances"]![0]!["seed"]!.GetValue<string>());
        Assert.Equal(2, Directory.GetDirectories(directory.FilePath("effects/packages")).Length);
        Assert.Contains(restored.Report.Issues, i => i.Code == DiagnosticCode.InvalidEffectInstance);
    }
    [Fact]
    public void 另存为与每份备份具有独立包闭包不引用原目录()
    {
        using TemporaryDirectory original = new(); using TemporaryDirectory copy = new(); using TemporaryDirectory state = new();
        var project = Source(); ProjectStore.Save(project, original.Path); ProjectStore.Save(project, copy.Path);
        string backup = AutoSaveStore.Save(project, AutoSaveStore.ResolveRoot(original.Path, state.Path), DateTimeOffset.UtcNow);
        Directory.Delete(original.FilePath("effects"), true);
        foreach (string directory in new[] { copy.Path, backup })
        {
            var restored = ProjectStore.Load(directory).Project!; Assert.Equal(project.Effects, restored.Effects);
            Assert.Equal(project.Effects.Packages[0].Manifest.ToArray(), restored.Effects.Packages[0].Manifest.ToArray());
            foreach (var file in project.Effects.Packages[0].Files)
                Assert.Equal(file.Content.ToArray(), restored.Effects.Packages[0].Files.Single(f => f.Path == file.Path).Content.ToArray());
        }
    }
    [Fact]
    public void 同修订不覆写包文件且未注册实例不能替换旧入口()
    {
        using TemporaryDirectory directory = new(); var project = Source(); ProjectStore.Save(project, directory.Path);
        string manifest = directory.FilePath(ProjectEffectsJson.PackagePath(project.Effects.Packages[0].Revision) + "/manifest.json");
        DateTime stamp = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc); File.SetLastWriteTimeUtc(manifest, stamp);
        ProjectStore.Save(project with { Name = "新名称" }, directory.Path); Assert.Equal(stamp, File.GetLastWriteTimeUtc(manifest));
        byte[] entry = File.ReadAllBytes(directory.FilePath("project.json"));
        var missing = project with { Effects = new([], project.Effects.Instances) };
        Assert.Throws<EffectPackageException>(() => ProjectStore.Save(missing, directory.Path));
        Assert.Equal(entry, File.ReadAllBytes(directory.FilePath("project.json"))); Assert.Equal("新名称", ProjectStore.Load(directory.Path).Project!.Name);
    }
    [Fact]
    public void 新包发布失败保留旧根与旧闭包()
    {
        using TemporaryDirectory directory = new(); var old = Source(); ProjectStore.Save(old, directory.Path);
        byte[] entry = File.ReadAllBytes(directory.FilePath("project.json")); var second = EffectPackageFixture.Contents(2);
        string damaged = directory.FilePath(ProjectEffectsJson.PackagePath(second.Revision)); Directory.CreateDirectory(damaged); File.WriteAllText(Path.Combine(damaged, "extra"), "bad");
        var update = old with { Effects = new([second], [Instance(second)]) };
        Assert.Throws<EffectPackageException>(() => ProjectStore.Save(update, directory.Path));
        Assert.Equal(entry, File.ReadAllBytes(directory.FilePath("project.json"))); Assert.Equal(old.Effects, ProjectStore.Load(directory.Path).Project!.Effects);
        Assert.DoesNotContain(Directory.GetDirectories(directory.FilePath("effects/packages")), p => Path.GetFileName(p).StartsWith(".package-", StringComparison.Ordinal));
    }
    [Theory]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("changed")]
    [InlineData("link")]
    public void 包缺失多余损坏链接均拒绝打开且不改入口(string kind)
    {
        using TemporaryDirectory directory = new(); var project = Source(); ProjectStore.Save(project, directory.Path);
        string package = directory.FilePath(ProjectEffectsJson.PackagePath(project.Effects.Packages[0].Revision)); string source = Path.Combine(package, "src/PackEffect.cs");
        byte[] entry = File.ReadAllBytes(directory.FilePath("project.json"));
        switch (kind)
        {
            case "missing": File.Delete(source); break;
            case "extra": File.WriteAllText(Path.Combine(package, "extra"), "bad"); break;
            case "changed": File.AppendAllText(source, "changed"); break;
            case "link": string outside = directory.FilePath("outside.cs"); File.Move(source, outside); File.CreateSymbolicLink(source, outside); break;
        }
        var loaded = ProjectStore.Load(directory.Path); Assert.Null(loaded.Project); Assert.Contains(loaded.Report.Issues, i => i.Code == DiagnosticCode.InvalidEffectData);
        Assert.Equal(entry, File.ReadAllBytes(directory.FilePath("project.json")));
    }
    [Theory]
    [InlineData("missing")]
    [InlineData("path")]
    [InlineData("identity")]
    [InlineData("simulation")]
    [InlineData("budget")]
    [InlineData("seed")]
    [InlineData("binding")]
    [InlineData("subscription")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    public void 工程效果严格结构不生成空效果回退(string kind)
    {
        using TemporaryDirectory directory = new(); ProjectStore.Save(Source(), directory.Path);
        Change(directory.Path, root =>
        {
            var effects = root["effects"]!; var instance = effects["instances"]![0]!;
            switch (kind)
            {
                case "missing": root.Remove("effects"); break;
                case "path": effects["packages"]![0]!["path"] = "../outside"; break;
                case "identity": effects["packages"]![0]!["packageId"] = "another"; break;
                case "simulation": effects["simulation"]!["stepHz"] = 60; break;
                case "budget": effects["budgets"]!["drawCommands"] = 0; break;
                case "seed": instance["seed"] = "18446744073709551615"; break;
                case "binding": instance["binding"]!["kind"] = "unrecognized"; break;
                case "subscription": instance["subscription"]!["kind"] = "unrecognized"; break;
                case "unknown": effects["fallback"] = true; break;
                case "duplicate": effects["instances"]!.AsArray().Add(instance.DeepClone()); break;
            }
        });
        var loaded = ProjectStore.Load(directory.Path); Assert.Null(loaded.Project); Assert.Contains(loaded.Report.Issues, i => i.Code == DiagnosticCode.InvalidEffectData);
    }
    [Fact]
    public void 坏源码非法参数失效绑定可保存且启用实例阻止导出()
    {
        using TemporaryDirectory directory = new(); var files = EffectPackageFixture.Files(); files["src/PackEffect.cs"] = Encoding.UTF8.GetBytes("broken !!!");
        var package = EffectPackageContents.Create(EffectPackageFixture.Bytes(EffectPackageFixture.Manifest(files)), EffectPackageFixture.Inputs(files), EffectPackageFixture.Profile);
        var instance = Instance(package) with { Parameters = EffectParameterOverrides.Parse("{\"peak\":1000}") };
        var project = Project.Create("坏源码") with { Effects = new([package], [instance]) }; ProjectStore.Save(project, directory.Path);
        var loaded = ProjectStore.Load(directory.Path); Assert.Equal(project.Effects, loaded.Project!.Effects);
        Assert.Contains(ProjectValidator.Validate(loaded.Project, ValidationContext.Export).Issues, i => i.Code == DiagnosticCode.InvalidEffectInstance && i.Severity == IssueSeverity.Error);
        var disabled = project with { Effects = new([package], [instance with { Enabled = false }]) };
        Assert.Null(disabled.Effects.InputsFingerprint); Assert.Contains(ProjectValidator.Validate(disabled, ValidationContext.Open).Issues,
            i => i.Code == DiagnosticCode.InvalidEffectInstance && i.Severity == IssueSeverity.Warning);
    }
    [Fact]
    public void 参数内容精度排序重复门禁与实例编辑()
    {
        var a = EffectParameterOverrides.Parse("{\"z\":9223372036854775807,\"a\":0.12345678901234567}");
        var b = EffectParameterOverrides.Parse("{\"a\":0.12345678901234567,\"z\":9223372036854775807}"); Assert.Equal(a, b);
        Assert.Contains("9223372036854775807", a.Json, StringComparison.Ordinal); Assert.Contains("0.12345678901234567", a.Json, StringComparison.Ordinal);
        Assert.Throws<EffectPackageException>(() => EffectParameterOverrides.Parse("{\"x\":1,\"x\":2}"));
        var source = Source(); Assert.Same(source, ProjectEffectsEditor.Replace(source, new(source.Effects.Packages, source.Effects.Instances)));
        Assert.Equal("EffectPackageInUse", Assert.Throws<EffectPackageException>(() => ProjectEffectsEditor.Unregister(source.Effects, source.Effects.Packages[0].PackageId, source.Effects.Packages[0].Revision)).Code);
        var duplicate = ProjectEffectsEditor.Duplicate(source.Effects, "fx:one"); Assert.NotEqual(duplicate.Instances[0].Id, duplicate.Instances[1].Id); Assert.NotEqual(duplicate.Instances[0].Seed, duplicate.Instances[1].Seed);
        var revision = EffectPackageFixture.Contents(3); var accepted = ProjectEffectsEditor.AcceptRevision(duplicate, revision, [duplicate.Instances[1].Id]);
        Assert.Equal(source.Effects.Packages[0].Revision, accepted.Instances[0].Revision); Assert.Equal(revision.Revision, accepted.Instances[1].Revision); Assert.Equal(2, accepted.Packages.Length);
    }

    [Fact]
    public void 实例编辑器保持执行顺序并拒绝无效绑定或参数()
    {
        var package = EffectPackageFixture.Contents();
        var effects = new ProjectEffects([package], [Instance(package)]);
        var second = Instance(package, "fx:two") with { Name = "第二个" };

        effects = ProjectEffectsEditor.AddInstance(effects, second);
        Assert.Equal(["fx:one", "fx:two"], effects.Instances.Select(item => item.Id));
        effects = ProjectEffectsEditor.MoveInstance(effects, "fx:two", -1);
        Assert.Equal(["fx:two", "fx:one"], effects.Instances.Select(item => item.Id));

        var changed = effects.Instances[0] with { Parameters = EffectParameterOverrides.Parse("{\"peak\":2.5}") };
        effects = ProjectEffectsEditor.ReplaceInstance(effects, changed);
        Assert.Equal("{\"peak\":2.5}", effects.Instances[0].Parameters.Json);
        Assert.Throws<EffectPackageException>(() => ProjectEffectsEditor.ReplaceInstance(effects,
            changed with { Binding = new(ProjectEffectBindingKind.Project) }));
        Assert.Throws<EffectPackageException>(() => ProjectEffectsEditor.ReplaceInstance(effects,
            changed with { Parameters = EffectParameterOverrides.Parse("{\"peak\":100}") }));

        effects = ProjectEffectsEditor.RemoveInstance(effects, "fx:two");
        Assert.Equal("fx:one", Assert.Single(effects.Instances).Id);
    }
}
