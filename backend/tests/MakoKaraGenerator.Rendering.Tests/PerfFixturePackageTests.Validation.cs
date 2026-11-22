using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.Execution;

namespace MakoKaraGenerator.Rendering.Tests;

/// <summary>F6：门禁按请求实例只校验一次；已通过的实例不能让内容不同的新实例跳过校验。</summary>
public sealed partial class PerfFixturePackageTests
{
    [Fact]
    public async Task 已校验请求之外的新请求仍完整校验()
    {
        var package = await Read("MaterialConstant");
        var material = new MaterialSpec.PackageMaterial(package.PackageId, package.Revision, "constant", EffectParameterOverrides.Parse("{}"));
        var project = Plain(package, []);
        project = project with { LyricStyle = project.LyricStyle with { UnsungFill = new() { Material = material }, SungFill = new() { Material = material } } };
        var compiled = await Compile(project);
        var snapshot = ProjectSnapshot.FromProject(project);
        await using var session = await compiled.CreateFactory(static () => new SkiaRenderPipeline()).CreateAsync(snapshot);
        var frame = await session.PrepareAsync(TimeUs, OutputMode.Opaque, RenderTarget.FromComposition(snapshot.Composition));
        var compiledPackage = Assert.Single(compiled.Packages);
        using var native = CompiledPackageGraphExports.Compile(compiledPackage.Definition);
        using var resources = new SceneImageEffectResources([], null, [native.CreateMaterialProgram(compiledPackage.Resources.GetMaterial("constant"), Canvas)]);
        using var renderer = new SkiaRenderPipeline(null, null, null, resources);
        var request = RenderRequest.At(snapshot, TimeUs) with { PreparedFrame = frame };
        using (renderer.Render(request)) { }
        GraphExecutionBudget budget = new();
        using (renderer.CaptureSceneBackground(request with { }, GraphInputSource.BackgroundColor, Canvas, budget)) { }
        Assert.Equal("PreparedInputsMismatch", Assert.Throws<PreparedFrameException>(() =>
            renderer.CaptureSceneBackground(request with { TimeUs = TimeUs + 1 }, GraphInputSource.BackgroundColor, Canvas, budget)).Code);
        Assert.Equal("PreparedInputsMismatch", Assert.Throws<PreparedFrameException>(() =>
            renderer.CaptureSceneBackground(request with { TransparentBackground = true }, GraphInputSource.BackgroundColor, Canvas, budget)).Code);
    }
}
