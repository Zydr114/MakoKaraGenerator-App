using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.Execution;

namespace MakoKaraGenerator.Rendering.Tests;

/// <summary>O7：材质框按本帧对象实际区域收紧，像素与整屏框一致，独立对象的表面只占本行区域。</summary>
public sealed partial class PerfFixturePackageTests
{
    /// <remarks>常量色走独立对象路径；读下层（σ8）走组合调度路径，工作框沿用调度器的整屏框，只收紧材质图本身。</remarks>
    [Theory]
    [InlineData(false, "constant", false)] [InlineData(false, "constant", true)]
    [InlineData(false, "backdrop", false)] [InlineData(false, "backdrop", true)]
    [InlineData(true, "constant", false)] [InlineData(true, "constant", true)]
    [InlineData(true, "backdrop", false)] [InlineData(true, "backdrop", true)]
    public async Task 收紧材质区域与整屏框像素一致(bool gpu, string kind, bool decorated)
    {
        if (gpu && Environment.GetEnvironmentVariable("MAKO_GPU_SMOKE") != "1") return;
        var (full, _) = await RenderRegion(kind, decorated, gpu, false, null);
        var (tight, _) = await RenderRegion(kind, decorated, gpu, true, null);
        Assert.Contains(full, value => value != 0);
        // CPU 逐字节一致；Vulkan 无模糊时逐字节一致。Skia GPU 模糊（阴影、辉光）的结果随源表面尺寸与原点变化，
        // 实测换框后最多差 2／255（按 64 px 网格对齐也不能消除），同框重复渲染则完全一致。
        if (!gpu || kind == "constant" && !decorated) Assert.Equal(full, tight);
        else Assert.InRange(full.Zip(tight, (a, b) => Math.Abs(a - b)).Max(), 0, 4);
    }

    [Fact]
    public async Task 高分辨率输出时对象工作框只占本行区域()
    {
        var target = new RenderTarget { PixelWidth = 2560, PixelHeight = 1440 };
        const long screen = 2560L * 1440 * 4;
        var (full, fullPeak) = await RenderRegion("constant", true, false, false, target);
        var (tight, tightPeak) = await RenderRegion("constant", true, false, true, target);
        Assert.Equal(full, tight);
        // 整屏框时每张中间表面都是整张 2560×1440（实测约 12 屏）；收紧后只剩下层快照的一屏，加上本行（64 px 字＋装饰）的各张小表面。
        Assert.True(fullPeak >= 4 * screen, $"整屏框峰值 {fullPeak}");
        Assert.True(tightPeak < 2 * screen, $"收紧后峰值 {tightPeak}");
    }

    /// <summary>
    /// 注音比例 2 时注音的辉光扩张与模糊随之翻倍（P3 第 4 步，D-P3-10）：收紧后的对象区域与原样辉光工作框须按缩放后的装饰包住注音，
    /// 像素与整屏框逐字节一致。
    /// </summary>
    [Fact]
    public async Task 收紧区域包住按注音比例放大的辉光()
    {
        var (full, _) = await RenderRegion("constant", false, false, false, null, rubyScale: 2);
        var (tight, _) = await RenderRegion("constant", false, false, true, null, rubyScale: 2);
        Assert.Contains(full, value => value != 0);
        Assert.Equal(full, tight);
    }

    private static async Task<(byte[] Pixels, long PeakBytes)> RenderRegion(string kind, bool decorated, bool gpu, bool tight, RenderTarget? target,
        double rubyScale = 0)
    {
        var package = await Read(kind == "constant" ? "MaterialConstant" : "MaterialBackdrop");
        string export = kind == "constant" ? "constant" : "backdrop";
        var project = Plain(package, []);
        var style = project.LyricStyle;
        if (rubyScale > 0)
        {
            // 小字号让放大后的注音整个留在画面里、注音比底字宽，注音左右的光晕不落进底字的区域。
            project = project with { Lyrics = LyricsDocument.Create([project.Lyrics.Lines[0] with
                { RubyAnnotations = [RubyAnnotation.Create("ruby", "token", "xxxx") with { RelativeScale = rubyScale }] }]) };
            style = style with { FontSizePx = 32, Glow = new GlowSpec { SpreadPx = 8, BlurRadiusPx = 2, Color = new RgbaColor(255, 220, 0, 230) } };
        }
        if (decorated)
            style = style with
            {
                // 斜接尖角、偏移阴影与辉光都画到墨迹外；区域须完整包住。
                Stroke = new StrokeSpec { WidthPx = 6, Join = MakoKaraGenerator.Core.Projects.StrokeJoin.Miter, Color = new RgbaColor(20, 40, 200) },
                Shadow = new ShadowSpec { DistancePx = 14, AngleDegrees = 135, BlurRadiusPx = 4, Color = new RgbaColor(0, 0, 0, 200) },
                Glow = new GlowSpec { SpreadPx = 3, BlurRadiusPx = 5, Color = new RgbaColor(255, 220, 0, 180) },
            };
        var material = new MaterialSpec.PackageMaterial(package.PackageId, package.Revision, export,
            EffectParameterOverrides.Parse(kind == "constant" ? "{}" : "{\"blurPx\":8}"));
        style = style with { UnsungFill = new() { Material = material }, SungFill = new() { Material = material } };
        project = project with { LyricStyle = style };
        var compiled = await Compile(project);
        var snapshot = ProjectSnapshot.FromProject(project);
        var output = target ?? RenderTarget.FromComposition(snapshot.Composition);
        await using var session = await compiled.CreateFactory(static () => new SkiaRenderPipeline()).CreateAsync(snapshot);
        var frame = await session.PrepareAsync(TimeUs, OutputMode.Opaque, output);
        var compiledPackage = Assert.Single(compiled.Packages);
        using var device = BaseMaterialRuntimeTests.Device(gpu);
        using var native = CompiledPackageGraphExports.CompileWithDevice(compiledPackage.Definition, null, device);
        using var resources = new SceneImageEffectResources([], null, [native.CreateMaterialProgram(compiledPackage.Resources.GetMaterial(export), Canvas)]);
        using var budget = new ImageSurfaceBudgetSession();
        using var renderer = new SkiaRenderPipeline(null, null, budget, resources, false, device, false) { TightObjectRegions = tight };
        using var image = renderer.Render(RenderRequest.At(snapshot, frame.TimeUs) with { PreparedFrame = frame, Target = output });
        Assert.Empty(image.MaterialDiagnostics); Assert.Empty(image.ImageEffectDiagnostics);
        return (image.PixelMemory.ToArray(), budget.Usage.PeakBytes);
    }
}
