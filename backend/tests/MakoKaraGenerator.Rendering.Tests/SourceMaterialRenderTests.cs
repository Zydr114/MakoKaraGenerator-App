using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting.Compilation;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class SourceMaterialRenderTests
{
    [Theory]
    [InlineData(GradientKind.Linear, 10_000_000L, false)]
    [InlineData(GradientKind.Linear, 13_000_000L, false)]
    [InlineData(GradientKind.Linear, 13_000_000L, true)]
    [InlineData(GradientKind.Radial, 10_000_000L, false)]
    [InlineData(GradientKind.Radial, 13_000_000L, false)]
    [InlineData(GradientKind.Radial, 13_000_000L, true)]
    public void 单材质持久往返保持真实基础字注音描边和前奏像素(GradientKind kind, long time, bool transparent)
    {
        using TemporaryDirectory temp = new(); var project = LeadInFixture.Create(slots: true);
        var gradient = GradientSpec.Create([new(0, new(255, 0, 0, 80)), new(.4, new(0, 255, 0, 160)), new(1, new(0, 0, 255, 220))],
            kind, angleDegrees: 35, radiusX: .7, radiusY: .4);
        var fill = new FillSpec { Material = new MaterialSpec.Gradient(gradient) };
        var stroke = new StrokeSpec { Material = new MaterialSpec.Gradient(gradient), WidthPx = 4, Join = StrokeJoin.Bevel };
        var style = project.LyricStyle with { UnsungFill = fill, SungFill = fill, Stroke = stroke, Opacity = .65f };
        project = project with { LyricStyle = style, LyricPresentation = project.LyricPresentation with { Nodes = [.. project.LyricPresentation.Nodes.Select(node =>
            node.Content is CountdownSequenceContent sequence ? node with { Content = sequence with { Appearance = SceneTextAppearance.FromStyle(style) } } : node)] } };
        ProjectStore.Save(project, temp.Path); var reopened = ProjectStore.Load(temp.Path); Assert.NotNull(reopened.Project);
        using SkiaRenderPipeline renderer = new();
        using var expected = renderer.Render(RenderRequest.At(ProjectSnapshot.FromProject(project), time) with { TransparentBackground = transparent });
        using var actual = renderer.Render(RenderRequest.At(ProjectSnapshot.FromProject(reopened.Project), time) with { TransparentBackground = transparent });
        Assert.Equal(expected.PixelMemory.ToArray(), actual.PixelMemory.ToArray());
        Assert.Equal(style, reopened.Project.LyricStyle);
    }

    [Fact]
    public void 缺少源材质执行时渲染和基线都明确拒绝而不输出白色种子()
    {
        var package = MixedEffectPackageFixture.Contents(); var material = new MaterialSpec.PackageMaterial(package.PackageId, package.Revision, "ink");
        var project = LeadInFixture.Create() with { Effects = new([package], []) };
        project = project with { LyricStyle = project.LyricStyle with { UnsungFill = new() { Material = material } } };
        var snapshot = ProjectSnapshot.FromProject(project); Assert.NotNull(snapshot.EffectInputsFingerprint);
        using SkiaRenderPipeline renderer = new();
        var error = Assert.Throws<PreparedFrameException>(() => renderer.Render(RenderRequest.At(snapshot, 13_000_000)));
        Assert.Equal("SourceMaterialResourcesRequired", error.Code);
        Assert.Equal("SourceMaterialResourcesRequired", Assert.Throws<PreparedFrameException>(() => StaticStyleProjection.CreateBaseline(snapshot)).Code);
        Assert.Equal("SourceMaterialResourcesRequired", Assert.Throws<PreparedFrameException>(() => PreparedRenderFrame.ValidateRequest(RenderRequest.At(snapshot, 13_000_000))).Code);
    }

    [Fact]
    public async Task 纯实例重载不伪造源材质或脚本实例()
    {
        var package = MixedEffectPackageFixture.Contents(); var project = LeadInFixture.Create() with { Effects = new([package], []) };
        project = project with { LyricStyle = project.LyricStyle with { SungFill = new() { Material = new MaterialSpec.PackageMaterial(package.PackageId, package.Revision, "ink") } } };
        ScriptCompiler compiler = new(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
        var compiled = await CompiledProjectEffects.CompileAsync(project.Effects, compiler);
        Assert.True(compiled.Succeeded); Assert.Empty(compiled.Effects); Assert.Empty(compiled.Packages); Assert.Null(compiled.SourceMaterials);
    }
}
