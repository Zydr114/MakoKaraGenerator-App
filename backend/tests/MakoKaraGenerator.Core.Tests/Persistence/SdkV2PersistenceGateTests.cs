using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Core.Tests.Persistence;

public sealed class SdkV2PersistenceGateTests
{
    [Fact]
    public void 当前工程格式与效果合同显式往返v1v2及独立读取范围()
    {
        using TemporaryDirectory temp = new();
        var old = EffectPackageFixture.Contents(); var current = SdkV2PackageFixture.Contents();
        var instance = new ProjectEffectInstance
        {
            Id = "v2-instance", Name = "纯求值", PackageId = current.PackageId, Revision = current.Revision, ExportId = "scale",
            Binding = new(ProjectEffectBindingKind.Tokens, ids: ["missing-token"]), Subscription = new("project"), Seed = ulong.MaxValue,
        };
        var project = Project.Create("开发期并存") with { Effects = new([old, current], [instance]) };
        ProjectStore.Save(project, temp.Path);
        var entry = JsonNode.Parse(File.ReadAllBytes(temp.FilePath("project.json")))!;
        Assert.Equal(Project.CurrentSchemaVersion, entry["schemaVersion"]!.GetValue<int>());
        Assert.Equal(ProjectEffectsJson.CurrentContractVersion, entry["effects"]!["contractVersion"]!.GetValue<int>());
        var loaded = ProjectStore.Load(temp.Path);
        Assert.NotNull(loaded.Project); Assert.Equal(project.Effects, loaded.Project.Effects);
        Assert.Equal([EffectPackageProfiles.V1, EffectPackageProfiles.V2], loaded.Project.Effects.Packages.Select(package => package.Profile));
        Assert.Equal("project", Assert.Single(loaded.Project.Effects.Instances).Subscription.Kind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 旧schema保存门禁发生于创建目录或发布资源之前(bool withV2)
    {
        using TemporaryDirectory temp = new(); string target = temp.FilePath("uncreated");
        var package = withV2 ? SdkV2PackageFixture.Contents() : EffectPackageFixture.Contents();
        var project = Project.Create("旧schema") with { SchemaVersion = Project.CurrentSchemaVersion - 1, Effects = new([package]) };
        Assert.Equal("ProjectSchemaVersionUnsupported", Assert.Throws<EffectPackageException>(() => ProjectStore.Save(project, target)).Code);
        Assert.False(Directory.Exists(target));
    }

    [Theory]
    [InlineData("project")]
    [InlineData("effects")]
    public void 旧工程或旧效果合同只报告错误且保留全部源字节(string kind)
    {
        using TemporaryDirectory temp = new();
        var package = SdkV2PackageFixture.Contents();
        ProjectStore.Save(Project.Create("新工程") with { Effects = new([package]) }, temp.Path);
        string path = temp.FilePath("project.json"); var root = JsonNode.Parse(File.ReadAllBytes(path))!;
        if (kind == "project") root["schemaVersion"] = Project.CurrentSchemaVersion - 1;
        else root["effects"]!["contractVersion"] = ProjectEffectsJson.CurrentContractVersion - 1;
        File.WriteAllText(path, root.ToJsonString()); byte[] before = File.ReadAllBytes(path);
        var loaded = ProjectStore.Load(temp.Path); Assert.Null(loaded.Project);
        Assert.Contains(loaded.Report.Issues, issue => issue.Code == (kind == "project" ? DiagnosticCode.UnsupportedSchemaVersion : DiagnosticCode.InvalidEffectData));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void 配置库2和模板库8固定保存v2资源且旧库拒绝覆盖()
    {
        using TemporaryDirectory temp = new(); var package = SdkV2PackageFixture.Contents();
        var configuration = ScriptConfiguration.Create("pure", "纯求值", package, "scale", target: ScriptRelativeTarget.TokenFamily);
        string scriptPath = ScriptConfigurationLibrary.ResolvePath(temp.Path);
        ScriptConfigurationLibrary.Save(scriptPath, [configuration]);
        var scripts = ScriptConfigurationLibrary.Load(scriptPath);
        Assert.Empty(scripts.Issues); Assert.Equal(configuration, Assert.Single(scripts.Configurations));
        Assert.Equal(ScriptConfigurationLibrary.CurrentSchemaVersion, scripts.SchemaVersion);
        var template = StyleTemplate.Create("pure-template", "纯求值模板", StyleTemplateKind.Token, LyricStyle.Default, [],
            [TemplateEffectRecipe.FromConfiguration("pure-recipe", configuration)], [package]);
        string templatePath = StyleTemplateLibrary.ResolvePath(temp.Path);
        StyleTemplateLibrary.Save(templatePath, [template]); var templates = StyleTemplateLibrary.Load(templatePath);
        Assert.Empty(templates.Issues); Assert.Equal(template, Assert.Single(templates.Templates));
        Assert.Equal(StyleTemplateLibrary.CurrentSchemaVersion, templates.SchemaVersion);

        var oldScript = JsonNode.Parse(File.ReadAllBytes(scriptPath))!; oldScript["schemaVersion"] = ScriptConfigurationLibrary.CurrentSchemaVersion - 1;
        File.WriteAllText(scriptPath, oldScript.ToJsonString()); byte[] scriptBytes = File.ReadAllBytes(scriptPath);
        Assert.True(ScriptConfigurationLibrary.Load(scriptPath).VersionMismatch);
        Assert.Throws<IOException>(() => ScriptConfigurationLibrary.Save(scriptPath, [])); Assert.Equal(scriptBytes, File.ReadAllBytes(scriptPath));
        var oldTemplate = JsonNode.Parse(File.ReadAllBytes(templatePath))!; oldTemplate["schemaVersion"] = StyleTemplateLibrary.CurrentSchemaVersion - 1;
        File.WriteAllText(templatePath, oldTemplate.ToJsonString()); byte[] templateBytes = File.ReadAllBytes(templatePath);
        Assert.True(StyleTemplateLibrary.Load(templatePath).VersionMismatch);
        Assert.Throws<IOException>(() => StyleTemplateLibrary.Save(templatePath, [])); Assert.Equal(templateBytes, File.ReadAllBytes(templatePath));
    }

    [Fact]
    public void 新库入口不迁移旧目录并明确报告原样保留()
    {
        using TemporaryDirectory temp = new();
        string oldScript = temp.FilePath("script-configurations-v1/index.json"), oldTemplate = temp.FilePath("templates-v7/index.json");
        Directory.CreateDirectory(Path.GetDirectoryName(oldScript)!); Directory.CreateDirectory(Path.GetDirectoryName(oldTemplate)!);
        File.WriteAllText(oldScript, "retained script source"); File.WriteAllText(oldTemplate, "retained template source");
        string currentScript = ScriptConfigurationLibrary.ResolvePath(temp.Path), currentTemplate = StyleTemplateLibrary.ResolvePath(temp.Path);
        Assert.True(ScriptConfigurationLibrary.Load(currentScript).LegacyLibraryRetained);
        Assert.True(StyleTemplateLibrary.Load(currentTemplate).LegacyLibraryRetained);
        Assert.False(File.Exists(currentScript)); Assert.False(File.Exists(currentTemplate));
        Assert.Equal("retained script source", File.ReadAllText(oldScript)); Assert.Equal("retained template source", File.ReadAllText(oldTemplate));
    }

    [Fact]
    public void 未知包身份不能借助当前效果合同写出()
    {
        var files = EffectPackageFixture.Files(); var manifest = EffectPackageFixture.Manifest(files); manifest["shaderProfile"] = "foreign";
        var package = EffectPackageContents.Create(EffectPackageFixture.Bytes(manifest), EffectPackageFixture.Inputs(files), new(2, "1.0.0", "foreign"));
        using TemporaryDirectory temp = new(); string target = temp.FilePath("uncreated");
        Assert.Equal("PackageProfileMismatch", Assert.Throws<EffectPackageException>(() => ProjectStore.Save(
            Project.Create("未知包") with { Effects = new([package]) }, target)).Code);
        Assert.False(Directory.Exists(target));
        Assert.Equal("PackageProfileMismatch", Assert.Throws<EffectPackageException>(() => ProjectEffectsJson.Encode(new([package]))).Code);
    }
}
