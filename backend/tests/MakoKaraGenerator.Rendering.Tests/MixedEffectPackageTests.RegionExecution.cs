using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed partial class MixedEffectPackageTests
{
    private static (RenderRequest Request, SceneCaptureTarget Target) RegionRequest(SkiaRenderPipeline renderer, int density = 1)
    {
        var snapshot = Snapshot() with { LyricStyle = Snapshot().LyricStyle with { Stroke = StrokeSpec.Create(RgbaColor.White, 4) } };
        var scene = renderer.GetSceneFrame(snapshot, 1_000_000); var target = Assert.Single(scene.TokenGeometries).WithRuby;
        var output = new RenderTarget { PixelWidth = 640 * density, PixelHeight = 360 * density };
        var prepared = new PreparedRenderFrame(snapshot, scene.TimeUs, Fx.OutputMode.Opaque, output, scene,
            [new(target, Fx.Affine2.ScaleAt(1.2, Assert.Single(scene.TokenGeometries).BaseGeometry.AdvanceBounds.Center))]);
        return (RenderRequest.At(snapshot, scene.TimeUs) with { PreparedFrame = prepared, Target = output }, new SceneCaptureTarget.Token(target));
    }
    private static Fx.ParameterValues RegionParameters(double amount = .5) => new([new("amount", new Fx.ParameterValue.Number(amount))]);
    private static ImageGraphFrameContext RegionFrame(RenderRequest request, Fx.ParameterValues? parameters = null, bool transparent = false)
        => new(request.TimeUs, 640, 360, new(0, 0, 640, 360), request.ResolveTarget().PixelWidth / 640d, request.ResolveTarget().PixelHeight / 360d,
            parameters ?? RegionParameters(), transparent);

    [Theory]
    [InlineData(SceneColorPart.BaseFill, 1)] [InlineData(SceneColorPart.RubyFill, 2)]
    [InlineData(SceneColorPart.BaseStroke, 2)] [InlineData(SceneColorPart.RubyStroke, 1)]
    public void 真实填充描边与注音材质读取包基础外观且只覆盖一次(SceneColorPart part, int density)
    {
        var definition = EffectPackageDefinition.Read(MixedEffectPackageFixture.Contents(pure: true));
        using var native = CompiledPackageGraphExports.Compile(definition); using SkiaRenderPipeline renderer = new();
        var (request, target) = RegionRequest(renderer, density); var handle = native.Resources.GetMaterial("ink");
        var regionKind = part is SceneColorPart.BaseStroke or SceneColorPart.RubyStroke ? SceneCaptureRegion.Stroke : SceneCaptureRegion.Fill;
        using var captured = renderer.CaptureSceneMaterialRegion(request, target, part, definition.GetMaterial("ink").Fallback, null,
            new(0, 0, 640, 360), GraphExecutionBudget.Default);
        using var old = renderer.CaptureSceneColorPart(request, target, part, new(0, 0, 640, 360), GraphExecutionBudget.Default);
        // 即使调用者提供旧外观，材质 TargetColor 也由真实基础外观替换。
        using var result = native.ExecuteMaterial(handle, regionKind, RegionFrame(request), captured.Region, captured.TargetColor,
            new Dictionary<string, SkiaGraphInput> { ["original"] = new(old.Surface, GraphInputSource.TargetColor) });
        var mask = Bytes(captured.Region); var fallback = Bytes(captured.TargetColor); var actual = Bytes(result.Output);
        Assert.Contains(mask.Where((_, index) => index % 4 == 3), alpha => alpha is > 0 and < 255);
        Assert.Contains(actual.Where((_, index) => index % 4 == 3), alpha => alpha > 0);
        for (int i = 0; i < mask.Length; i += 4)
        {
            Assert.InRange(Math.Abs(fallback[i + 3] - Math.Round(mask[i + 3] * 160 / 255d)), 0, 2);
            Assert.Equal(fallback[i + 3], actual[i + 3]);
            for (int c = 0; c < 3; c++) Assert.InRange(Math.Abs(actual[i + c] - fallback[i + c] * .5), 0, 1);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void 真实互补逐字区域与全区域一致且不烘入源填充透明度(bool stroke)
    {
        var definition = EffectPackageDefinition.Read(MixedEffectPackageFixture.Contents(pure: true));
        using SkiaRenderPipeline renderer = new(); var (request, target) = RegionRequest(renderer);
        var part = stroke ? SceneColorPart.BaseStroke : SceneColorPart.BaseFill; var bounds = new GraphRect(0, 0, 640, 360);
        using var all = renderer.CaptureSceneMaterialRegion(request, target, part, definition.GetMaterial("ink").Fallback, null, bounds, GraphExecutionBudget.Default);
        using var sung = renderer.CaptureSceneMaterialRegion(request, target, part, definition.GetMaterial("ink").Fallback, true, bounds, GraphExecutionBudget.Default);
        using var unsung = renderer.CaptureSceneMaterialRegion(request, target, part, definition.GetMaterial("ink").Fallback, false, bounds, GraphExecutionBudget.Default);
        var a = Bytes(all.Region); var b = Bytes(sung.Region); var c = Bytes(unsung.Region);
        for (int i = 3; i < a.Length; i += 4) Assert.Equal(a[i], b[i] + c[i]);
        Assert.Contains(a.Where((_, index) => index % 4 == 3), alpha => alpha == 255);
    }

    [Theory]
    [InlineData("masked", "solid")] [InlineData("unmasked", "solid")]
    [InlineData("masked", "gradient")] [InlineData("unmasked", "gradient")]
    public void 透明材质跳过背景图并使用自己的已覆盖基础外观(string mode, string appearance)
    {
        var definition = EffectPackageDefinition.Read(MixedEffectPackageFixture.Contents(manifest => manifest["exports"]![0]!["coverageMode"] = mode,
            pure: true, background: true, fallback: appearance == "gradient" ? MixedEffectPackageFixture.Gradient() : null));
        using var native = CompiledPackageGraphExports.Compile(definition); using SkiaRenderPipeline renderer = new();
        var (request, target) = RegionRequest(renderer);
        using var captured = renderer.CaptureSceneMaterialRegion(request, target, SceneColorPart.BaseFill, definition.GetMaterial("ink").Fallback,
            null, new(0, 0, 640, 360), GraphExecutionBudget.Default);
        using var result = native.ExecuteMaterial(native.Resources.GetMaterial("ink"), SceneCaptureRegion.Fill, RegionFrame(request, transparent: true),
            captured.Region, captured.TargetColor, new Dictionary<string, SkiaGraphInput>());
        Assert.Equal(Bytes(captured.TargetColor), Bytes(result.Output)); Assert.Null(result.Coverage);
        Assert.Equal("TransparentBackgroundDependency", Assert.Single(result.Diagnostics).Reason);
    }

    [Fact]
    public void 未覆盖纯色shader限制在真实字形区域而非矩形输出()
    {
        var definition = EffectPackageDefinition.Read(MixedEffectPackageFixture.Contents(manifest => manifest["exports"]![0]!["coverageMode"] = "unmasked",
            pure: true, shaderSource: "uniform float amount; uniform shader original; half4 main(float2 p) { return half4(amount,0,original.eval(p).a*0.001,1); }"));
        using var native = CompiledPackageGraphExports.Compile(definition); using SkiaRenderPipeline renderer = new();
        var (request, target) = RegionRequest(renderer);
        using var captured = renderer.CaptureSceneMaterialRegion(request, target, SceneColorPart.BaseFill, definition.GetMaterial("ink").Fallback,
            null, new(0, 0, 640, 360), GraphExecutionBudget.Default);
        using var result = native.ExecuteMaterial(native.Resources.GetMaterial("ink"), SceneCaptureRegion.Fill, RegionFrame(request),
            captured.Region, captured.TargetColor, new Dictionary<string, SkiaGraphInput>());
        var mask = Bytes(captured.Region); var actual = Bytes(result.Output);
        for (int i = 3; i < mask.Length; i += 4) Assert.Equal(mask[i], actual[i]);
    }

    [Theory]
    [InlineData("set", "PackageParameterSetMismatch")]
    [InlineData("range", "GraphParameterOutOfRange")]
    [InlineData("region", "PackageMaterialRegionInvalid")]
    [InlineData("revision", "InvalidMaterialResource")]
    public void 材质真实导出约束失败不能执行部分资源(string change, string code)
    {
        var definition = EffectPackageDefinition.Read(MixedEffectPackageFixture.Contents(pure: true)); using var native = CompiledPackageGraphExports.Compile(definition);
        using SkiaRenderPipeline renderer = new(); var (request, target) = RegionRequest(renderer);
        using var captured = renderer.CaptureSceneMaterialRegion(request, target, SceneColorPart.BaseFill, definition.GetMaterial("ink").Fallback,
            null, new(0, 0, 640, 360), GraphExecutionBudget.Default);
        var handle = change == "revision" ? new Fx.ResourceCatalog("other", "sha256:" + new string('0', 64), ["ink"], [], []).GetMaterial("ink") : native.Resources.GetMaterial("ink");
        var frame = RegionFrame(request, change == "set" ? Fx.ParameterValues.Empty : RegionParameters(change == "range" ? 2 : .5));
        var error = Assert.ThrowsAny<Exception>(() => native.ExecuteMaterial(handle, change == "region" ? SceneCaptureRegion.FillAndStroke : SceneCaptureRegion.Fill,
            frame, captured.Region, captured.TargetColor, new Dictionary<string, SkiaGraphInput>()));
        Assert.Equal(code, error is EffectPackageException package ? package.Code : Assert.IsType<ImageGraphException>(error).Code);
    }

    [Fact]
    public void 区域双表面捕获在第二次分配失败时回收第一张且拒绝阴影区域()
    {
        using SkiaRenderPipeline renderer = new(); var (request, target) = RegionRequest(renderer);
        var definition = EffectPackageDefinition.Read(MixedEffectPackageFixture.Contents(pure: true));
        using var budget = new ImageSurfaceBudgetSession(new() { MaximumSurfaces = 1 }); using var frame = budget.BeginFrame();
        Assert.Throws<ImageSurfaceBudgetException>(() => renderer.CaptureSceneMaterialRegion(request, target, SceneColorPart.BaseFill,
            definition.GetMaterial("ink").Fallback, null, new(0, 0, 640, 360), GraphExecutionBudget.Default, frame));
        Assert.Equal(0, budget.Usage.ActiveSurfaces);
        Assert.Equal("InvalidMaterialRegionTarget", Assert.Throws<PreparedFrameException>(() => renderer.CaptureSceneMaterialRegion(request, target, SceneColorPart.BaseShadow,
            definition.GetMaterial("ink").Fallback, null, new(0, 0, 640, 360), GraphExecutionBudget.Default)).Code);
    }

    [Fact]
    public void 完整渐变基础外观的停止点alpha不会再乘首个停止点alpha()
    {
        var fallback = MixedEffectPackageFixture.Gradient(space: "Canvas"); fallback["gradient"]!["angleDegrees"] = 0;
        var definition = EffectPackageDefinition.Read(MixedEffectPackageFixture.Contents(pure: true, fallback: fallback));
        using SkiaRenderPipeline renderer = new(); var (request, target) = RegionRequest(renderer);
        var appearance = Assert.IsType<EffectMaterialFallback.Gradient>(definition.GetMaterial("ink").Fallback);
        using var captured = renderer.CaptureSceneMaterialRegion(request, target, SceneColorPart.BaseFill, appearance, null,
            new(0, 0, 640, 360), GraphExecutionBudget.Default);
        var region = Bytes(captured.Region); var actual = Bytes(captured.TargetColor); int checkedPixels = 0;
        for (int i = 3; i < region.Length; i += 4)
            if (region[i] == 255)
            {
                int x = (i / 4) % 640;
                byte expected = GradientSampler.Sample(appearance.Value, (x + 1.5) / 642).Alpha;
                Assert.InRange(Math.Abs(actual[i] - expected), 0, 2); checkedPixels++;
            }
        Assert.True(checkedPixels > 0);
    }

    [Fact]
    public void 透明跳过材质图也不能隐藏未声明输入()
    {
        var definition = EffectPackageDefinition.Read(MixedEffectPackageFixture.Contents(pure: true, background: true));
        using var native = CompiledPackageGraphExports.Compile(definition); using SkiaRenderPipeline renderer = new();
        var (request, target) = RegionRequest(renderer);
        using var captured = renderer.CaptureSceneMaterialRegion(request, target, SceneColorPart.BaseFill, definition.GetMaterial("ink").Fallback, null,
            new(0, 0, 640, 360), GraphExecutionBudget.Default);
        Assert.Equal("GraphInputUnknown", Assert.Throws<ImageGraphException>(() => native.ExecuteMaterial(native.Resources.GetMaterial("ink"), SceneCaptureRegion.Fill,
            RegionFrame(request, transparent: true), captured.Region, captured.TargetColor,
            new Dictionary<string, SkiaGraphInput> { ["unknown"] = new(captured.TargetColor, GraphInputSource.TargetColor) })).Code);
    }
}
