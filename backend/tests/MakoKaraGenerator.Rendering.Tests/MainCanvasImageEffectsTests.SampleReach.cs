using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using Fx = MakoKaraGenerator.Scripting;
using ObjectBelowSource = MakoKaraGenerator.Rendering.Skia.SkiaRenderPipeline.ObjectBelowSource;
using ObjectRenderPlan = MakoKaraGenerator.Rendering.Skia.SkiaRenderPipeline.ObjectRenderPlan;

namespace MakoKaraGenerator.Rendering.Tests;

/// <summary>独立对象（遗留调度，目标快照作下层）的工作框并入图与材质声明的取样偏移，收紧后仍能读到目标框之外的下层。</summary>
public sealed partial class MainCanvasImageEffectsTests
{
    [Theory]
    [InlineData("graph")] [InlineData("material")]
    public void G9_独立对象工作框并入取样偏移且像素与整框一致(string kind)
    {
        // 取样偏移远大于外扩（图：外扩 0、偏移 1024，在固定点读下层；材质：外扩 0、偏移 160，读左侧 160 px 的下层）。
        // 下层上的红点由另一个实例画在目标框之外；读取方自己没有 BehindText 图元，本帧不启用前缀调度。
        var (request, resources, owner, expected) = kind == "graph" ? SampleReachGraph() : SampleReachMaterial();
        using (owner)
        {
            (byte[] Pixels, List<ObjectRenderPlan> Plans) Render(bool tight)
            {
                using var budget = new ImageSurfaceBudgetSession(); List<ObjectRenderPlan> plans = [];
                using var renderer = new SkiaRenderPipeline(null, null, budget, resources) { TightObjectRegions = tight, ObjectPlanBuilt = plans.Add };
                using var frame = renderer.Render(request);
                Assert.Empty(frame.ImageEffectDiagnostics); Assert.Empty(frame.MaterialDiagnostics);
                Assert.Equal(0, budget.Usage.ActiveSurfaces);
                return (frame.Pixels.ToArray(), plans);
            }
            var full = Render(false); var tight = Render(true);

            var plan = Assert.Single(tight.Plans);
            Assert.Equal(ObjectBelowSource.DestinationSnapshot, plan.Below); Assert.False(plan.ClosedExport);
            // 收紧照常生效：图目标框或材质区域只占本行；工作框按声明的取样距离外扩，但不超出收紧前的整框。
            var target = kind == "graph" ? Assert.Single(plan.Graphs).TargetBounds : Assert.NotNull(plan.TargetRegion);
            Assert.True(target.Width * target.Height < Bounds.Width * Bounds.Height / 4, $"目标框 {target}");
            Assert.True(Assert.Single(full.Plans).WorkBounds.Contains(plan.WorkBounds), $"工作框 {plan.WorkBounds}");
            Assert.True(plan.WorkBounds.Contains(expected.Sample), $"工作框 {plan.WorkBounds} 不含取样点 {expected.Sample}");

            Assert.Equal(full.Pixels, tight.Pixels);
            int red = expected.Pixels.Count(point =>
            {
                int offset = (point.Y * 640 + point.X) * 4;
                return tight.Pixels[offset + 2] > 80 && tight.Pixels[offset + 1] <= 2 && tight.Pixels[offset + 3] > 80;
            });
            Assert.True(red >= expected.Minimum, $"读到下层红点的像素 {red} 个");
        }
    }

    /// <summary>行上的探针图：探针条读固定点 (30, 20) 的下层；外扩 0，每个节点声明取样偏移 1024。</summary>
    private static (RenderRequest Request, SceneImageEffectResources Resources, IDisposable Owner, (GraphRect Sample, (int X, int Y)[] Pixels, int Minimum) Expected) SampleReachGraph()
    {
        var request = OwnedRequest(); var spot = OwnedLineSpot(request);
        var graph = OwnedProbe("a", spot);
        var resources = new SceneImageEffectResources([OwnedProgram("a", graph)]);
        var prepared = request.PreparedFrame!.WithOverlayOutput([OwnedDot("other", "dot", new(1, 0, 0, 1))], [])
            .WithImageEffects([OwnedBind("read", Line(request), "a")]);
        return (request with { PreparedFrame = prepared }, resources, new Owned(resources, graph),
            (new GraphRect(30, 20, 31, 21), [(spot.X + 4, spot.Y + 8)], 1));
    }

    /// <summary>元素族的填充材质：未遮罩输出整片读左侧 160 px 的下层，由宿主按字形覆盖遮罩；外扩 0、取样偏移 160。</summary>
    private static (RenderRequest Request, SceneImageEffectResources Resources, IDisposable Owner, (GraphRect Sample, (int X, int Y)[] Pixels, int Minimum) Expected) SampleReachMaterial()
    {
        var contents = OwnedMaterialPackage();
        var native = CompiledPackageGraphExports.Compile(EffectPackageDefinition.Read(contents));
        var handle = native.Resources.GetMaterial("ink");
        var material = (Fx.MaterialValue.PackageMaterial)Fx.MaterialValue.PackageExport(handle,
            new Fx.ParameterValues([new("amount", new Fx.ParameterValue.Number(.5))]));
        var style = LyricStyle.Default with { FontFamilyName = "DejaVu Sans", FontSizePx = 64, Opacity = 1,
            UnsungColor = RgbaColor.White, SungColor = RgbaColor.White, Stroke = null, Shadow = null, Glow = null };
        var project = Project.Create("sample-reach", Composition.Create(640, 360, Core.Time.FrameRate.Fps30, outputDurationUs: 5_000_000),
            LyricsDocument.Create([LyricLine.Create("line", "AB", new(0, 5_000_000), TimingStatus.Confirmed,
                [LyricToken.Create("token", "AB", new(0, 4_000_000), TimingStatus.Confirmed)])]), lyricStyle: style)
            with { Effects = new([contents], []) };
        var snapshot = ProjectSnapshot.FromProject(project);
        var target = new RenderTarget { PixelWidth = 640, PixelHeight = 360 };
        using var measure = new SkiaRenderPipeline();
        var frame = new PreparedRenderFrame(snapshot, 3_000_000, Fx.OutputMode.Transparent, target, measure.GetSceneFrame(snapshot, 3_000_000), []);
        var token = Assert.Single(frame.FinalGeometry); var family = new Fx.ImageEffectTarget.Token(token.WithRuby);
        var writer = new PreparedMaterialOwner.Script(new("read"), "material-writer");
        frame = new PreparedRenderFrame(snapshot, frame.TimeUs, frame.OutputMode, frame.Target, frame.Scene, [],
            [new(token.WithRuby, TestStyle.Update(material, material))], [], [], [], [], [],
            [new(writer, family, PreparedMaterialSlot.UnsungFill, material), new(writer, family, PreparedMaterialSlot.SungFill, material)]);
        frame = frame.WithOverlayOutput([new Fx.DotBatch(new("other"), new("other"), Fx.OverlayLayer.BehindText, [new(new(160, 240), 110, new(1, 0, 0, 1))])], []);
        var resources = new SceneImageEffectResources([], null, [native.CreateMaterialProgram(handle, Bounds)]);
        // 字形内、左移 160 px 后仍落在红点里的像素。
        using var ink = OwnedInkRegion(token.FamilyGeometry);
        List<(int X, int Y)> pixels = [];
        for (int y = 40; y < 340; y++) for (int x = 300; x < 460; x++)
            if (ink.Contains(x + .5f, y + .5f) && Math.Pow(x - 160 - 160, 2) + Math.Pow(y - 240, 2) < 100 * 100) pixels.Add((x, y));
        Assert.True(pixels.Count > 100);
        var request = RenderRequest.At(snapshot, frame.TimeUs) with { PreparedFrame = frame, Target = target, TransparentBackground = true };
        // 字形左缘往左 150 px（取样偏移 160 之内）的一点，必须落在工作框内。
        var corners = token.FamilyGeometry.WorldInkCorners;
        double left = corners.Min(point => point.X), top = corners.Min(point => point.Y);
        return (request, resources, new Owned(resources, native), (new GraphRect(left - 150, top, left - 149, top + 1), [.. pixels], 50));
    }

    private sealed class Owned(params IDisposable[] values) : IDisposable
    {
        public void Dispose() { foreach (var value in values) value.Dispose(); }
    }
}
