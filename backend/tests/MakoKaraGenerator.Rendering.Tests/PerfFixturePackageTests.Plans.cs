using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.Execution;
using ObjectLayerAssembly = MakoKaraGenerator.Rendering.Skia.SkiaRenderPipeline.ObjectLayerAssembly;
using ObjectPartKind = MakoKaraGenerator.Rendering.Skia.SkiaRenderPipeline.ObjectPartKind;
using ObjectRenderPlan = MakoKaraGenerator.Rendering.Skia.SkiaRenderPipeline.ObjectRenderPlan;

namespace MakoKaraGenerator.Rendering.Tests;

/// <summary>对象计划：区域收紧、判空与层组装各是计划上的一步，只改各自的字段，部件的成员、层序与两态不变。</summary>
public sealed partial class PerfFixturePackageTests
{
    private static bool IsRubyPart(SceneColorPart part)
        => part is SceneColorPart.RubyGlow or SceneColorPart.RubyStroke or SceneColorPart.RubyShadow or SceneColorPart.RubyFill;

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task 对象计划的三个优化各自只改写自己的字段(bool decorated)
    {
        var basic = await PlanObject(decorated, tight: false, skipEmpty: false, singlePass: false);
        var tight = await PlanObject(decorated, tight: true, skipEmpty: false, singlePass: false);
        var skipped = await PlanObject(decorated, tight: false, skipEmpty: true, singlePass: false);
        var merged = await PlanObject(decorated, tight: false, skipEmpty: false, singlePass: true);

        // 基本计划：常量色材质占未唱、已唱两个填充槽，独立对象取目标快照、直接写回；区域取程序框，逐层组装，没有判空。
        Assert.Empty(basic.Graphs);
        Assert.Equal(2, basic.Materials.Length);
        Assert.Equal(SkiaRenderPipeline.ObjectBelowSource.DestinationSnapshot, basic.Below);
        Assert.False(basic.ClosedExport);
        Assert.Null(basic.TargetRegion);
        Assert.Equal(Canvas, basic.WorkBounds);
        Assert.Equal(ObjectLayerAssembly.PerLayer, basic.Assembly);
        Assert.DoesNotContain(basic.Parts, step => step.Kind == ObjectPartKind.KnownEmpty);
        // 填充材质同时落在基础字与注音的填充部件上，各分未唱、已唱两态（注音那两次在判空后才省掉）。
        Assert.Equal([(SceneColorPart.BaseFill, false), (SceneColorPart.BaseFill, true), (SceneColorPart.RubyFill, false), (SceneColorPart.RubyFill, true)],
            basic.Parts.Where(step => step.Kind == ObjectPartKind.Material).Select(step => (step.Part, step.Sung!.Value)));
        Assert.Equal(decorated, basic.HasGlow);
        Assert.Equal(decorated, basic.Parts.Any(step => step.Part == SceneColorPart.BaseGlow));

        // 三步都不改变部件的成员、色彩部件、层序与两态。
        static object Shape(ObjectRenderPlan plan) => plan.Parts.Select(step => (step.Member, step.Part, step.Order, step.Sung, step.SlotHasMaterial)).ToArray();
        Assert.Equal(Shape(basic), Shape(tight));
        Assert.Equal(Shape(basic), Shape(skipped));
        Assert.Equal(Shape(basic), Shape(merged));

        // 区域收紧（O7）：材质目标区域与工作框只占本行，部件处理方式与组装方式不变。
        var region = Assert.NotNull(tight.TargetRegion);
        Assert.True(region.Bottom - region.Top < (Canvas.Bottom - Canvas.Top) / 2, $"目标区域 {region}");
        Assert.True(tight.WorkBounds.Bottom - tight.WorkBounds.Top < (Canvas.Bottom - Canvas.Top) / 2, $"工作框 {tight.WorkBounds}");
        Assert.Equal(basic.Parts.Select(step => step.Kind), tight.Parts.Select(step => step.Kind));
        Assert.Equal(ObjectLayerAssembly.PerLayer, tight.Assembly);

        // 判空（F3）：夹具行没有注音，注音部件一律为空；无装饰时描边、阴影部件也为空；有材质的填充照常执行。区域与组装方式不变。
        Assert.All(skipped.Parts.Where(step => IsRubyPart(step.Part)), step => Assert.Equal(ObjectPartKind.KnownEmpty, step.Kind));
        Assert.Contains(skipped.Parts, step => IsRubyPart(step.Part));
        var decoration = skipped.Parts.Where(step => step.Part is SceneColorPart.BaseStroke or SceneColorPart.BaseShadow).ToArray();
        Assert.NotEmpty(decoration);
        Assert.All(decoration, step => Assert.Equal(decorated && step.Member != skipped.Target ? ObjectPartKind.Original : ObjectPartKind.KnownEmpty, step.Kind));
        // 全部基础字都已交给独立元素族，整行原样成员无需保留空捕获。
        Assert.All(skipped.Parts.Where(step => step.Member == skipped.Target), step => Assert.Equal(ObjectPartKind.KnownEmpty, step.Kind));
        Assert.Equal([(SceneColorPart.BaseFill, false), (SceneColorPart.BaseFill, true)],
            skipped.Parts.Where(step => step.Kind == ObjectPartKind.Material).Select(step => (step.Part, step.Sung!.Value)));
        Assert.All(skipped.Parts.Where(step => step.Kind == ObjectPartKind.KnownEmpty), step => Assert.Null(step.Material));
        Assert.Null(skipped.TargetRegion);
        Assert.Equal(basic.WorkBounds, skipped.WorkBounds);
        Assert.Equal(ObjectLayerAssembly.PerLayer, skipped.Assembly);

        // 层组装（F4）：只改组装方式。
        Assert.Equal(ObjectLayerAssembly.MergeAdjacentSourceLayers, merged.Assembly);
        Assert.Equal(basic.Parts.Select(step => step.Kind), merged.Parts.Select(step => step.Kind));
        Assert.Null(merged.TargetRegion);
        Assert.Equal(basic.WorkBounds, merged.WorkBounds);
    }

    /// <summary>渲染一帧常量色材质夹具，返回唯一一个对象（夹具只有一行在场）的计划。</summary>
    private static async Task<ObjectRenderPlan> PlanObject(bool decorated, bool tight, bool skipEmpty, bool singlePass)
    {
        var package = await Read("MaterialConstant");
        var project = Plain(package, []);
        var style = project.LyricStyle;
        if (decorated)
            style = style with
            {
                Stroke = new StrokeSpec { WidthPx = 6, Color = new RgbaColor(20, 40, 200) },
                Shadow = new ShadowSpec { DistancePx = 14, AngleDegrees = 135, BlurRadiusPx = 4, Color = new RgbaColor(0, 0, 0, 200) },
                Glow = new GlowSpec { SpreadPx = 3, BlurRadiusPx = 5, Color = new RgbaColor(255, 220, 0, 180) },
            };
        var material = new MaterialSpec.PackageMaterial(package.PackageId, package.Revision, "constant", EffectParameterOverrides.Parse("{}"));
        project = project with { LyricStyle = style with { UnsungFill = new() { Material = material }, SungFill = new() { Material = material } } };
        var compiled = await Compile(project);
        var snapshot = ProjectSnapshot.FromProject(project);
        await using var session = await compiled.CreateFactory(static () => new SkiaSceneGeometry(), ReplayReason.Export).CreateAsync(snapshot);
        var frame = await session.PrepareAsync(TimeUs, OutputMode.Opaque, RenderTarget.FromComposition(snapshot.Composition));
        var compiledPackage = Assert.Single(compiled.Packages);
        using var device = BaseMaterialRuntimeTests.Device(false);
        using var native = CompiledPackageGraphExports.CompileWithDevice(compiledPackage.Definition, null, device, pixelChecks: false);
        using var resources = new SceneImageEffectResources([], null, [native.CreateMaterialProgram(compiledPackage.Resources.GetMaterial("constant"), Canvas)]);
        List<ObjectRenderPlan> plans = [];
        using var renderer = new SkiaRenderPipeline(null, null, null, resources, false, device, false, graphPixelChecks: false)
            { TightObjectRegions = tight, SkipKnownEmptyParts = skipEmpty, SinglePassLayerAssembly = singlePass, ObjectPlanBuilt = plans.Add };
        using var image = renderer.Render(RenderRequest.At(snapshot, TimeUs) with { PreparedFrame = frame });
        Assert.Empty(image.MaterialDiagnostics);
        return Assert.Single(plans);
    }
}
