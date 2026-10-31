using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Rendering.Tests;

public sealed class AdvancedPresetSnapshotCodecTests
{
    internal static ProjectSnapshot Create()
    {
        var source = PackageSnapshotContractFixture.Create();
        var package = source.Effects.Packages.Single(value => EffectPackageDefinition.Read(value).Exports.Any(export => export.Id == "scale"));
        var recipe = new TemplateEffectRecipe("fixed-recipe", "固定效果", TemplateEffectTarget.TokenFamily,
            package.PackageId, package.Revision, "scale", EffectParameterOverrides.Parse("{\"peak\":2.25}"),
            readScope: ScriptReadScope.BranchInput, branchInput: "vocals", fixedSeed: ulong.MaxValue,
            configurationId: "fixed-configuration");
        var template = StyleTemplate.Create("advanced-preset", "高级预设", StyleTemplateKind.Token, source.LyricStyle,
            [LyricStyleProperty.Opacity], [recipe], [package]);
        var binding = new ProjectEffectBinding(ProjectEffectBindingKind.Tokens, ids: ["first-a"]);
        var project = PackageSnapshotContractFixture.ToProject(source);
        Assert.True(PresetApplicationsEditor.TryApply(project, template, [binding],
            new Dictionary<string, string> { ["vocals"] = "primary" }, out var applied, out var reason), reason);
        return ProjectSnapshot.FromProject(applied);
    }

    [Fact]
    public void 高级预设完整保配方包引用实例身份种子与分支输入()
    {
        var source = Create();
        Assert.Equal(PackagePresetProjectSnapshotCodec.Profile, ProjectSnapshotSourceProfile.Select(source));
        Assert.Equal(PackagePresetProjectSnapshotCodec.Profile, ProjectPreviewSnapshotProfile.Select(source));
        var bytes = ProjectSnapshotSourceProfile.Encode(source);
        var copy = ProjectSnapshotSourceProfile.Decode(bytes);
        Assert.Equal(source.PresetApplications, copy.PresetApplications);
        Assert.Equal(source.Effects, copy.Effects);
        Assert.Equal(source.EffectInputsFingerprint, copy.EffectInputsFingerprint);
        Assert.Equal(bytes, ProjectPreviewSnapshotProfile.Encode(copy));
        var root = JsonNode.Parse(bytes)!;
        var reference = root["snapshot"]!["presetApplications"]!["items"]![0]!["snapshot"]!["packages"]![0]!;
        Assert.Equal(2, reference.AsObject().Count);
        Assert.True(copy.PresetApplications.Items[0].Identities[0].Enabled);
        Assert.Equal(source, ProjectSnapshot.FromProject(ProjectSnapshotSourceProject.Restore(copy)));
    }

    [Theory]
    [InlineData("package")] [InlineData("recipe")] [InlineData("input")] [InlineData("identity")]
    public void 高级预设不允许缺闭包或重新建立运行身份(string mutation)
    {
        var root = JsonNode.Parse(ProjectSnapshotSourceProfile.Encode(Create()))!;
        var item = root["snapshot"]!["presetApplications"]!["items"]![0]!;
        switch (mutation)
        {
            case "package": item["snapshot"]!["packages"]![0]!["revision"] = new string('0', 64); break;
            case "recipe": item["snapshot"]!["effectRecipes"]![0]!.AsObject().Remove("configurationId"); break;
            case "input": item["branchInputs"]!.AsObject().Clear(); break;
            case "identity": item["identities"]!.AsArray().Clear(); break;
        }
        Assert.Throws<SnapshotContractException>(() => ProjectSnapshotSourceProfile.Decode(System.Text.Encoding.UTF8.GetBytes(root.ToJsonString())));
    }
}
