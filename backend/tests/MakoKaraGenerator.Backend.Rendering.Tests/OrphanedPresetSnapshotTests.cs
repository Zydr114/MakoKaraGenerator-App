using System.Text;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Validation;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Runtime.Hosting;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.Compilation;

namespace MakoKaraGenerator.Backend.Rendering.Tests;

public sealed class OrphanedPresetSnapshotTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 删除节点保留完整预设但跳过执行且快照往返后撤销恢复(bool packaged)
    {
        var source = PackageSnapshotContractFixture.ToProject(StaticSnapshotFixture.Create());
        var node = new SceneContainerDefinition { Id = "temporary-preset-target" };
        Assert.True(SceneTreeEditor.TryAddNodes(source, [node], out source, out var report), report.ToSummary());
        var package = packaged ? MixedEffectPackageFixture.Contents() : null;
        var style = packaged ? source.LyricStyle with { UnsungFill = new() { Material = new MaterialSpec.PackageMaterial(package!.PackageId, package.Revision, "ink") } }
            : source.LyricStyle with { Opacity = .25f };
        var recipe = packaged ? new TemplateEffectRecipe("recipe", "固定脚本", TemplateEffectTarget.Application,
            package!.PackageId, package.Revision, "controller", readScope: ScriptReadScope.BranchInput, branchInput: "vocals", fixedSeed: ulong.MaxValue) : null;
        var template = StyleTemplate.Create("orphan", "可撤销预设", StyleTemplateKind.Composite, style,
            [packaged ? LyricStyleProperty.UnsungFill : LyricStyleProperty.Opacity], recipe is null ? [] : [recipe], package is null ? [] : [package]);
        Assert.True(PresetApplicationsEditor.TryApply(source, template, [new(ProjectEffectBindingKind.Node, node.Id)],
            packaged ? new Dictionary<string, string> { ["vocals"] = "primary" } : null, out var applied, out var reason), reason);
        var original = ProjectSnapshot.FromProject(applied);
        Assert.Equal(original.EffectInputsFingerprint, ProjectRuntimeEffectInputs.Fingerprint(original));
        Assert.True(SceneTreeEditor.TryDeleteNode(applied, node.Id, out var deleted, out report), report.ToSummary());
        var validation = ProjectValidator.Validate(deleted, ValidationContext.Export);
        Assert.False(validation.HasErrors, validation.ToSummary());
        var warning = Assert.Single(validation.Issues.Where(issue => issue.Location == "presetApplications." + applied.PresetApplications.Items[0].Id));
        Assert.Equal(IssueSeverity.Warning, warning.Severity);
        Assert.Contains("跳过", warning.Message, StringComparison.Ordinal);
        Assert.Equal(applied.PresetApplications, deleted.PresetApplications);
        Assert.True(deleted.PresetApplications.Items[0].Enabled);
        var snapshot = ProjectSnapshot.FromProject(deleted);
        Assert.Empty(snapshot.Effects.Instances);
        Assert.Null(ProjectRuntimeEffectInputs.Fingerprint(snapshot));
        Assert.False(ScriptBackendRequirement.RequiresVulkan(snapshot));
        byte[] bytes = ProjectSnapshotSourceProfile.Encode(snapshot);
        var copy = ProjectSnapshotSourceProfile.Decode(bytes);
        Assert.Equal(bytes, ProjectSnapshotSourceProfile.Encode(copy));
        Assert.Equal(snapshot.EffectInputsFingerprint, copy.EffectInputsFingerprint);
        Assert.Equal(deleted.PresetApplications, copy.PresetApplications);
        Assert.Equal(deleted, ProjectSnapshotSourceProject.Restore(copy));
        if (packaged)
        {
            Assert.Single(SourceMaterialCatalog.Collect(deleted));
            Assert.NotNull(snapshot.EffectInputsFingerprint);
            Assert.Equal("SnapshotPresetInvalid", Assert.Throws<SnapshotContractException>(() => ProjectSnapshotSourceProject.Restore(copy with
                { Effects = new(copy.Effects.Packages, original.Effects.Instances, copy.Effects.Budgets) })).Code);
            Assert.Equal("SnapshotFingerprintMismatch", Assert.Throws<SnapshotContractException>(() => ProjectSnapshotSourceProfile.Encode(copy with
                { EffectInputsFingerprint = new string('0', 64) })).Code);
            var damaged = JsonNode.Parse(bytes)!;
            damaged["snapshot"]!["presetApplications"]!["items"]![0]!["snapshot"]!["style"]!["unsung"]!["fill"]!["material"]!["parameters"] = new JsonObject { ["amount"] = "invalid" };
            Assert.Throws<SnapshotContractException>(() => ProjectSnapshotSourceProfile.Decode(Encoding.UTF8.GetBytes(damaged.ToJsonString())));
            damaged = JsonNode.Parse(bytes)!;
            damaged["snapshot"]!["effects"]!["packages"]!.AsArray().Clear();
            Assert.Throws<SnapshotContractException>(() => ProjectSnapshotSourceProfile.Decode(Encoding.UTF8.GetBytes(damaged.ToJsonString())));
        }
        using TemporaryDirectory temp = new(); using FfmpegVideoFrameProvider videos = new();
        var compiler = new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"), temp.FilePath("cache"));
        var compiled = await CompiledProjectEffects.CompileAsync(copy, compiler);
        Assert.Empty(compiled.Packages); Assert.Empty(compiled.Effects); Assert.Null(compiled.SourceMaterials);
        var factory = new ProjectRenderSessionFactory(compiler, videos, ReplayReason.Initial, renderBackend: "cpu");
        await using var session = await factory.CreateAsync(copy);
        var request = RenderRequest.At(copy, 1_000_000) with { Target = new() { PixelWidth = 160, PixelHeight = 90 } };
        using var frame = await session.RenderAsync(request);
        using var renderer = new SkiaRenderPipeline();
        using var measured = renderer.MeasureAndRender(request, layoutReference: false);
        Assert.Equal(frame.PixelMemory.ToArray(), measured.Frame.PixelMemory.ToArray());
        Assert.True(SceneTreeEditor.TryAddNodes(ProjectSnapshotSourceProject.Restore(copy), [node], out var restored, out report), report.ToSummary());
        Assert.Equal(original, ProjectSnapshot.FromProject(restored));
        Assert.Equal(ProjectSnapshotSourceProfile.Encode(original), ProjectSnapshotSourceProfile.Encode(ProjectSnapshot.FromProject(restored)));
    }
}
