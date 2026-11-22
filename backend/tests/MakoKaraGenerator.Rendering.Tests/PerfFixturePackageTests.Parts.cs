using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.Execution;

namespace MakoKaraGenerator.Rendering.Tests;

/// <summary>F3：必然为空的部件先判空再分配，像素不变，表面与材质执行减少。</summary>
public sealed partial class PerfFixturePackageTests
{
    /// <summary>夹具的装饰：无、两态都有、只有已唱一态有。</summary>
    public enum PartDecoration { None, Both, SungOnly }

    /// <remarks>夹具行没有注音：注音部件与注音填充材质一律跳过；无装饰时描边、阴影部件也跳过。
    /// 只有已唱一态有装饰时描边、阴影部件照常捕获（判空看两态）。
    /// 关闭像素检查（生产配置），否则检查模式会照常捕获来核对判空。</remarks>
    [Theory]
    [InlineData(false, PartDecoration.None)] [InlineData(false, PartDecoration.Both)] [InlineData(false, PartDecoration.SungOnly)]
    [InlineData(true, PartDecoration.None)] [InlineData(true, PartDecoration.Both)] [InlineData(true, PartDecoration.SungOnly)]
    public async Task 必然为空的部件不捕获不执行材质且像素不变(bool gpu, PartDecoration decoration)
    {
        if (gpu && Environment.GetEnvironmentVariable("MAKO_GPU_SMOKE") != "1") return;
        var (full, fullSurfaces) = await RenderParts(decoration, gpu, false);
        var (skipped, skippedSurfaces) = await RenderParts(decoration, gpu, true);
        Assert.Contains(full, value => value != 0);
        Assert.Equal(full, skipped);
        // 实测 41 → 27（无装饰）／41 → 31（有装饰）：省下注音部件的捕获与两次注音填充材质执行，
        // 无装饰时再省行与元素两个成员的描边、阴影捕获。
        Assert.True(fullSurfaces - skippedSurfaces >= (decoration == PartDecoration.None ? 14 : 10), $"表面 {fullSurfaces} → {skippedSurfaces}");
    }

    [Fact]
    public async Task 成员判空只跳无注音元素与已被接管的整行部件且生产像素不变()
    {
        var package = await Read("MaterialConstant");
        var project = Plain(package, []);
        var line = project.Lyrics.AllLines.Single();
        line = line with
        {
            Tokens = [LyricToken.Create("a", "A", new(0, 2 * TimeUs), TimingStatus.Edited),
                LyricToken.Create("b", "B", new(0, 2 * TimeUs), TimingStatus.Edited)],
            RubyAnnotations = [RubyAnnotation.Create("ruby", "a", "x")],
        };
        var material = new MaterialSpec.PackageMaterial(package.PackageId, package.Revision, "constant", EffectParameterOverrides.Parse("{}"));
        project = project with { Lyrics = LyricsDocument.Create([line]), LyricStyle = project.LyricStyle with
            { UnsungFill = new() { Material = material }, SungFill = new() { Material = material } } };
        var compiled = await Compile(project);
        var snapshot = ProjectSnapshot.FromProject(project);
        await using var session = await compiled.CreateFactory(static () => new SkiaRenderPipeline()).CreateAsync(snapshot);
        var frame = await session.PrepareAsync(TimeUs, OutputMode.Opaque, RenderTarget.FromComposition(snapshot.Composition));
        using var device = new GraphPixelCheckModeTests.CountingDevice();
        var compiledPackage = Assert.Single(compiled.Packages);
        using var native = CompiledPackageGraphExports.CompileWithDevice(compiledPackage.Definition, null, device, pixelChecks: false);
        using var resources = new SceneImageEffectResources([], null, [native.CreateMaterialProgram(compiledPackage.Resources.GetMaterial("constant"), Canvas)]);
        List<SkiaRenderPipeline.ObjectRenderPlan> plans = [];
        byte[] Render(bool skip, bool checks)
        {
            using var renderer = new SkiaRenderPipeline(null, null, null, resources, false, device, false, graphPixelChecks: checks)
                { SkipKnownEmptyParts = skip, ObjectPlanBuilt = plans.Add };
            using var image = renderer.Render(RenderRequest.At(snapshot, TimeUs) with { PreparedFrame = frame });
            return image.PixelMemory.ToArray();
        }
        var baseline = Render(false, false);
        plans.Clear();
        var actual = Render(true, false);
        Assert.Equal(0, device.ImageReads); // 两次正式配置都不为判空读回。
        Assert.Equal(baseline, actual);
        var plan = Assert.Single(plans);
        Assert.All(plan.Parts.Where(step => step.Member == plan.Target), step => Assert.Equal(SkiaRenderPipeline.ObjectPartKind.KnownEmpty, step.Kind));
        var noRuby = plan.Parts.Where(step => step.Member is ImageEffectTarget.Token token && token.Target.TokenId.Value == "b" && IsRubyPart(step.Part));
        Assert.NotEmpty(noRuby);
        Assert.All(noRuby, step => Assert.Equal(SkiaRenderPipeline.ObjectPartKind.KnownEmpty, step.Kind));
        Assert.Contains(plan.Parts, step => step.Member is ImageEffectTarget.Token token && token.Target.TokenId.Value == "a"
            && step.Part == SceneColorPart.RubyFill && step.Kind == SkiaRenderPipeline.ObjectPartKind.Material);
        Assert.Equal(actual, Render(true, true)); // 已知为空仍执行原捕获，核对静态证明。
    }

    private static async Task<(byte[] Pixels, int Surfaces)> RenderParts(PartDecoration decoration, bool gpu, bool skip)
    {
        var package = await Read("MaterialConstant");
        var project = Plain(package, []);
        var style = project.LyricStyle;
        var decorations = new TextAppearance
        {
            Strokes = StrokeLayers.FromStroke(new StrokeSpec { WidthPx = 6, Color = new RgbaColor(20, 40, 200) }),
            Shadow = new ShadowSpec { DistancePx = 14, AngleDegrees = 135, BlurRadiusPx = 4, Color = new RgbaColor(0, 0, 0, 200) },
        };
        if (decoration == PartDecoration.Both)
            style = style with { Unsung = decorations with { Fill = style.UnsungFill } };
        if (decoration == PartDecoration.SungOnly)
        {
            style = style with { Sung = SungAppearance.Explicit(decorations with { Fill = style.SungFill }) };
            // 夹具元素长达 602 秒，此刻几乎还没唱；缩短到此刻唱到一半，已唱区域才有像素。
            var line = project.Lyrics.AllLines.Single();
            project = project with { Lyrics = LyricsDocument.Create([line with { Tokens = [line.Tokens[0] with { Time = new(0, 2 * TimeUs) }] }]) };
        }
        var material = new MaterialSpec.PackageMaterial(package.PackageId, package.Revision, "constant", EffectParameterOverrides.Parse("{}"));
        project = project with { LyricStyle = style with { UnsungFill = new() { Material = material }, SungFill = new() { Material = material } } };
        var compiled = await Compile(project);
        var snapshot = ProjectSnapshot.FromProject(project);
        await using var session = await compiled.CreateFactory(static () => new SkiaRenderPipeline()).CreateAsync(snapshot);
        var frame = await session.PrepareAsync(TimeUs, OutputMode.Opaque, RenderTarget.FromComposition(snapshot.Composition));
        var compiledPackage = Assert.Single(compiled.Packages);
        using var device = new GraphPixelCheckModeTests.CountingDevice(BaseMaterialRuntimeTests.Device(gpu));
        using var native = CompiledPackageGraphExports.CompileWithDevice(compiledPackage.Definition, null, device, pixelChecks: false);
        using var resources = new SceneImageEffectResources([], null, [native.CreateMaterialProgram(compiledPackage.Resources.GetMaterial("constant"), Canvas)]);
        using var renderer = new SkiaRenderPipeline(null, null, null, resources, false, device, false, graphPixelChecks: false) { SkipKnownEmptyParts = skip };
        using var image = renderer.Render(RenderRequest.At(snapshot, TimeUs) with { PreparedFrame = frame });
        Assert.Empty(image.MaterialDiagnostics);
        return (image.PixelMemory.ToArray(), device.Surfaces);
    }
}
