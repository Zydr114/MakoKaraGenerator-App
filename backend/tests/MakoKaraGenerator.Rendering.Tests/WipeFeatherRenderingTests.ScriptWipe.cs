using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering.Skia;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

/// <summary>
/// P3 第 6 步：脚本经 SetWipe 写入的扫字样式（羽化与高亮）与源工程写同一值的绘制一致（P3 设计 §9）。
/// 两边都带一个元素族样式输出，走同一条「有变化的元素族」绘制路径；只有扫字样式的来源不同。
/// </summary>
public sealed partial class WipeFeatherRenderingTests
{
    private static readonly TimeRange WipeTime = new(1_000_000, 2_000_000);
    private static readonly Fx.WipeValue ScriptWipe = new(.5, new(new(1, 1, .6, 1), .25, 1, Fx.WipeHighlightBlend.Screen));
    private static readonly WipeSpec SourceWipe = new()
    {
        FeatherRatio = .5,
        Highlight = new() { Color = new(255, 255, 153), WidthRatio = .25, Opacity = 1, Blend = WipeHighlightBlend.Screen },
    };

    private static Project WipeProject(WipeSpec wipe, bool ruby)
    {
        var project = Project(0, [("█", WipeTime)], ruby: ruby ? RubyAnnotation.Create("ruby", "t0", "ab") : null);
        return project with { LyricStyle = project.LyricStyle with { Wipe = wipe } };
    }

    /// <summary>元素族或底字上写入给定样式更新的准备帧。</summary>
    private static PreparedRenderFrame WipeFrame(SkiaRenderPipeline pipeline, ProjectSnapshot snapshot, long time, Fx.StyleUpdate update, bool family)
    {
        RenderTarget target = new() { PixelWidth = Width, PixelHeight = Height };
        var scene = pipeline.GetSceneFrame(snapshot, time);
        var frame = new PreparedRenderFrame(snapshot, time, Fx.OutputMode.Transparent, target, scene, []);
        var token = Assert.Single(frame.FinalGeometry);
        return family
            ? new PreparedRenderFrame(snapshot, time, Fx.OutputMode.Transparent, target, scene, [], [new(token.WithRuby, update)])
            : frame.WithTextPartitions([], [new(token.BaseOnly, update)], []);
    }

    private static byte[] RenderWipe(SkiaRenderPipeline pipeline, Project project, long time, Fx.StyleUpdate update, bool family, bool captured)
    {
        var snapshot = ProjectSnapshot.FromProject(project);
        var request = RenderRequest.At(snapshot, time) with
        {
            PreparedFrame = WipeFrame(pipeline, snapshot, time, update, family), Target = new() { PixelWidth = Width, PixelHeight = Height },
            TransparentBackground = true,
        };
        if (!captured)
        {
            using var frame = pipeline.Render(request);
            return frame.PixelMemory.ToArray();
        }
        SceneCaptureTarget.Line line = new(new(new("line"), new(pipeline.GetSceneFrame(snapshot, time).Lines[0].BindingId!)));
        using var surface = pipeline.CaptureSceneTarget(request, line, new(true, true, true, false, SceneCaptureRegion.FillAndStroke, true),
            GraphInputSource.TargetColor, Canvas, GraphExecutionBudget.Default);
        return SkiaTestReadback.Pixels(surface.Image);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    public void 脚本写入的扫字样式与源工程写同一值逐字节相同(bool family, bool ruby, bool captured)
    {
        using var pipeline = new SkiaRenderPipeline();
        Fx.StyleUpdate none = new(), written = new(wipe: ScriptWipe);
        foreach (long t in new long[] { 900_000, 1_400_000, 2_100_000 })
        {
            var expected = RenderWipe(pipeline, WipeProject(SourceWipe, ruby), t, none, family, captured);
            var actual = RenderWipe(pipeline, WipeProject(WipeSpec.Default, ruby), t, written, family, captured);
            Assert.Equal(expected, actual);
            // 确实画出了羽化与高亮：与不写扫字样式不同。
            Assert.NotEqual(expected, RenderWipe(pipeline, WipeProject(WipeSpec.Default, ruby), t, none, family, captured));
        }
    }

    [Fact]
    public async Task 真实脚本写入扫字样式进入准备帧而文字标记明确拒绝()
    {
        var package = V2DirectAndMaterialPackageFixture.Package();
        var snapshot = V2DirectAndMaterialPackageFixture.Snapshot(package, [V2DirectAndMaterialPackageFixture.Instance(package, mode: 15)]);
        var compiled = await V2DirectAndMaterialPackageFixture.Compile(snapshot);
        await using (var session = await V2DirectAndMaterialPackageFixture.Factory(snapshot, compiled).CreateAsync(snapshot))
        {
            var frame = await V2DirectAndMaterialPackageFixture.Prepare(session, snapshot);
            var style = Assert.Single(frame.TokenStyles);
            Assert.Equal(SourceWipe, frame.GetTokenDrawingStyle(style.Target).Wipe);
        }
        var marked = V2DirectAndMaterialPackageFixture.Snapshot(package, [V2DirectAndMaterialPackageFixture.Instance(package, mode: 15, mark: 1)], mark: true);
        var markCompiled = await V2DirectAndMaterialPackageFixture.Compile(marked);
        await using var markSession = await V2DirectAndMaterialPackageFixture.Factory(marked, markCompiled).CreateAsync(marked);
        var error = await Assert.ThrowsAsync<EffectFailureException>(async () => await V2DirectAndMaterialPackageFixture.Prepare(markSession, marked, 10_000_000));
        Assert.Equal("PropertyTargetUnsupported", error.Code);
    }
}
