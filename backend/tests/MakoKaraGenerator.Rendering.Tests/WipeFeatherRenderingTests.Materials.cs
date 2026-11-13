using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.TestSupport;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

/// <summary>
/// P3 第 5 步 5b：图场景中按两态拆开的填充部件在过渡带内按 m 合并（P3 设计 §10.2）。
/// 两种合成模式对下层都是仿射的，合并后的结果应恰好是「整字未唱」与「整字已唱」两帧按 m 的交叉淡化。
/// 输出不透明、底色不为黑：对象以目标快照（或组合前缀）为下层合成，擦除量 k 与前景 α 不同时才看得出区别；
/// 替换合成的材质把前景整体乘 amount，α 小于 1 而替换覆盖为 1。
/// </summary>
public sealed partial class WipeFeatherRenderingTests
{
    /// <summary>哪一态绑定填充材质；另一态原样捕获。</summary>
    public enum MaterialSides { Both, Unsung, Sung }

    private static readonly TimeRange SideTime = new(1_000_000, 2_000_000);
    private static readonly RgbaColor SideUnsung = new(255, 40, 40, 150), SideSung = new(40, 40, 255, 110), SideBackground = new(30, 90, 60);

    /// <summary>单字「█」，不透明底色；无描边、阴影与辉光。</summary>
    private static Project SideProject(double feather, RgbaColor unsung, RgbaColor sung)
    {
        var project = Project(feather, [("█", SideTime)], unsung, sung, SideBackground);
        return project with { LyricStyle = project.LyricStyle with { Stroke = null, Shadow = null, Glow = null } };
    }

    /// <summary>替换合成的材质前景整体乘 amount（预乘），α 小于替换覆盖。</summary>
    private const string ScaledInk = "uniform float amount; uniform shader original; half4 main(float2 p) { return original.eval(p)*half(amount); }";

    /// <summary>替换合成的材质：覆盖取材质区域。</summary>
    private static void ReplaceComposite(JsonObject graph)
    {
        graph["inputs"]!.AsArray().Add(EffectPackageGraphFixture.Input("region", "TargetRegionCoverage", "coverage"));
        graph["composite"] = new JsonObject { ["mode"] = "replaceCoveredBackdrop", ["coverage"] = "input:region" };
    }

    /// <summary>
    /// 手工准备帧：每个元素的 Base 按 <see cref="MaterialSides"/> 绑定「ink」（rgb × amount，未唱 0.25、已唱 1）；可选用组 identity 图触发前缀调度。
    /// 描边模式下改写两态各一层同宽描边并绑定描边槽；same 时两态用同一材质。
    /// </summary>
    private sealed class SideRig : IDisposable
    {
        private static readonly Fx.ResourceCatalog GroupCatalog = new("wipe-sides", "sha256:" + new string('d', 64), [], ["identity"], []);
        private readonly ISkiaRenderDevice _device;
        private readonly CompiledPackageGraphExports _native;
        private readonly CompiledImageGraph _identity;
        private readonly Fx.MaterialHandle _handle;
        private readonly Fx.MaterialValue.PackageMaterial _unsung, _sung;
        private readonly EffectPackageContents _package;

        internal SideRig(bool replace, bool gpu)
        {
            _package = replace ? BaseMaterialPackageFixture.Package(ReplaceComposite, ScaledInk) : BaseMaterialPackageFixture.Package();
            _device = BaseMaterialRuntimeTests.Device(gpu);
            _native = CompiledPackageGraphExports.CompileWithDevice(EffectPackageDefinition.Read(_package), null, _device);
            _handle = _native.Resources.GetMaterial("ink");
            // 两态同一材质、参数不同：材质的原色输入两态相同，靠 amount 区分两态。
            _unsung = Ink(.25); _sung = Ink(1);
            _identity = CompiledImageGraph.Compile(ImageGraphCompiler.Compile(new("identity", ImageGraphDefinition.CurrentVersion,
                [new("color", GraphInputSource.TargetColor, ShaderSurfaceKind.Color, true, GraphBackgroundRequirement.None, GraphCoordinateSpace.Canvas)],
                [], [], GraphReference.Input("color"), GraphCoordinateSpace.Canvas, 0, GraphAnchorMode.ColorOnly, GraphCompositeMode.SourceOver), [], []));
        }

        internal const double StrokeWidth = 8;

        internal byte[] Render(Project project, MaterialSides sides, long time, bool prefix, out ImageSurfaceUsage usage, bool stroke = false, bool same = false, bool objectGraph = false, bool checks = false)
        {
            var snapshot = ProjectSnapshot.FromProject(project with { Effects = new([_package], []) });
            RenderTarget target = new() { PixelWidth = Width, PixelHeight = Height };
            SceneFrame scene;
            using (var measure = new SkiaRenderPipeline()) scene = measure.GetSceneFrame(snapshot, time);
            var frame = new PreparedRenderFrame(snapshot, time, Fx.OutputMode.Opaque, target, scene, []);
            var owner = new PreparedMaterialOwner.Script(new("writer"), "writer-artifact");
            Fx.MaterialValue.PackageMaterial? unsung = sides is MaterialSides.Sung ? null : _unsung, sung = sides is MaterialSides.Unsung ? null : same ? _unsung : _sung;
            PreparedMaterialSlot unsungSlot = stroke ? PreparedMaterialSlot.UnsungStroke : PreparedMaterialSlot.UnsungFill;
            PreparedMaterialSlot sungSlot = stroke ? PreparedMaterialSlot.SungStroke : PreparedMaterialSlot.SungFill;
            PreparedMaterialSlot[] slots = sides switch
            {
                MaterialSides.Both => [unsungSlot, sungSlot],
                MaterialSides.Unsung => [unsungSlot],
                _ => [sungSlot],
            };
            var update = stroke ? new Fx.StyleUpdate(Strokes(unsung), Strokes(sung)) : TestStyle.Update(unsung, sung);
            var tokens = frame.FinalGeometry.ToArray();
            frame = frame.WithTextPartitions([], [.. tokens.Select(token => new Fx.V2.BaseTokenStyle(token.BaseOnly, update))], [],
                [.. tokens.SelectMany(token => slots.Select(slot => new PreparedBaseMaterialBinding(owner, token.BaseOnly, slot,
                    slot == sungSlot ? sung! : unsung!)))]);
            if (prefix)
            {
                Fx.ImageEffectTarget.Group root = new(new(new(snapshot.LyricPresentation.FindProgram("primary")!.RootContainerId)));
                frame = frame.WithImageEffects([new(new("group"), "artifact-group", new(root, GroupCatalog.GetGraph("identity"), Fx.ParameterValues.Empty))]);
            }
            if (objectGraph)
            {
                var token = tokens[0].WithRuby;
                frame = frame.WithImageEffects([.. frame.ImageEffects, new(new("token"), "artifact-token",
                    new(new Fx.ImageEffectTarget.Token(token), GroupCatalog.GetGraph("identity"), Fx.ParameterValues.Empty))]);
            }
            SceneImageGraphProgram[] graphs = prefix || objectGraph
                ? [new(GroupCatalog.GetGraph("identity"), _identity, new(true, true, true, true, SceneCaptureRegion.FillAndStroke, true), Canvas)] : [];
            using var resources = new SceneImageEffectResources(graphs, null, [_native.CreateMaterialProgram(_handle, Canvas)]);
            using var budget = new ImageSurfaceBudgetSession();
            using var renderer = new SkiaRenderPipeline(null, null, budget, resources, false, _device, false, graphPixelChecks: checks);
            using var rendered = renderer.Render(RenderRequest.At(snapshot, time) with { PreparedFrame = frame, Target = target, TransparentBackground = false });
            Assert.Empty(rendered.MaterialDiagnostics);
            usage = budget.Usage;
            Assert.Equal(0, usage.ActiveSurfaces);
            return rendered.PixelMemory.ToArray();
        }

        private static Fx.AppearanceUpdate? Strokes(Fx.MaterialValue? material) =>
            material is null ? null : new(strokes: new([new(StrokeWidth, material, Fx.StrokeJoin.Miter)]));

        private Fx.MaterialValue.PackageMaterial Ink(double amount) => (Fx.MaterialValue.PackageMaterial)Fx.MaterialValue.PackageExport(_handle,
            new Fx.ParameterValues([new("amount", new Fx.ParameterValue.Number(amount))]));

        public void Dispose() { _identity.Dispose(); _native.Dispose(); _device.Dispose(); }
    }

    /// <summary>字内三行逐像素：实际 ≈ (1 − m)·整字未唱 + m·整字已唱；带内像素足够多且两态确实不同。</summary>
    private static void AssertCrossfade(byte[] before, byte[] after, byte[] actual, float left, float right, long t, int tolerance, string where)
    {
        int band = 0, distinct = 0;
        foreach (int y in new[] { Row - 25, Row, Row + 25 })
        for (int x = (int)left; x < (int)right; x++)
        {
            double m = Mask(left, right, SideTime, t, 50, x + .5);
            int offset = (y * Width + x) * 4;
            if (m is > 0 and < 1)
            {
                band++;
                if (Enumerable.Range(0, 4).Any(c => Math.Abs(before[offset + c] - after[offset + c]) > 20)) distinct++;
            }
            for (int c = 0; c < 4; c++)
            {
                double expected = before[offset + c] * (1 - m) + after[offset + c] * m;
                Assert.True(Math.Abs(actual[offset + c] - expected) <= tolerance,
                    $"{where}：({x},{y}) 分量 {c} m={m:F3} 期望 {expected:F1}（未唱 {before[offset + c]}，已唱 {after[offset + c]}），实际 {actual[offset + c]}。");
            }
        }
        Assert.True(band >= 120 && distinct * 2 > band, $"{where}：带内像素 {band}，两态不同的 {distinct}。");
    }

    [Theory]
    [InlineData(MaterialSides.Both, false, false)]
    [InlineData(MaterialSides.Both, false, true)]
    [InlineData(MaterialSides.Both, true, false)]
    [InlineData(MaterialSides.Both, true, true)]
    [InlineData(MaterialSides.Unsung, false, false)]
    [InlineData(MaterialSides.Unsung, true, true)]
    [InlineData(MaterialSides.Sung, false, true)]
    [InlineData(MaterialSides.Sung, true, false)]
    public void 拆侧填充在过渡带内等于两态完整结果按m交叉淡化(MaterialSides sides, bool replace, bool prefix)
    {
        // 只有未唱一侧是材质时两态取同一颜色：外观相同，只因拆侧才需要过渡带。
        var (unsung, sung) = sides is MaterialSides.Unsung ? (SideSung, SideSung) : (SideUnsung, SideSung);
        var project = SideProject(.5, unsung, sung);
        using var rig = new SideRig(replace, gpu: false);
        using var measure = new SkiaRenderPipeline();
        var (left, right) = Assert.Single(Spans(measure, project));
        const long t = 1_400_000;
        byte[] before = rig.Render(project, sides, 500_000, prefix, out _), after = rig.Render(project, sides, 3_000_000, prefix, out _);
        var actual = rig.Render(project, sides, t, prefix, out _);
        AssertCrossfade(before, after, actual, left, right, t, 2, $"{sides} replace={replace} prefix={prefix}");
        // 两条调度路径整帧一致。
        if (!prefix) return;
        var legacy = rig.Render(project, sides, t, false, out _);
        int worst = legacy.Select((value, index) => Math.Abs(value - actual[index])).Max();
        Assert.True(worst <= 1, $"前缀调度与逐对象调度最大差 {worst}。");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void GPU上拆侧填充在过渡带内按m交叉淡化(bool replace, bool prefix)
    {
        if (Environment.GetEnvironmentVariable("MAKO_GPU_SMOKE") != "1") return;
        var project = SideProject(.5, SideUnsung, SideSung);
        using var rig = new SideRig(replace, gpu: true);
        using var measure = new SkiaRenderPipeline();
        var (left, right) = Assert.Single(Spans(measure, project));
        const long t = 1_400_000;
        byte[] before = rig.Render(project, MaterialSides.Both, 500_000, prefix, out _), after = rig.Render(project, MaterialSides.Both, 3_000_000, prefix, out _);
        AssertCrossfade(before, after, rig.Render(project, MaterialSides.Both, t, prefix, out _), left, right, t, 3, $"GPU replace={replace} prefix={prefix}");
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void 羽化闭合后保留后续元素图所需替换范围且父组导出像素一致(bool prefix)
    {
        var project = SideProject(.5, SideUnsung, SideSung);
        using var rig = new SideRig(replace: true, gpu: false);
        const long t = 1_400_000;
        var closed = rig.Render(project, MaterialSides.Both, t, prefix, out _);
        var continued = rig.Render(project, MaterialSides.Both, t, prefix, out _, objectGraph: true);
        Assert.InRange(closed.Zip(continued, (a, b) => Math.Abs(a - b)).Max(), 0, 2);
        Assert.Equal(continued, rig.Render(project, MaterialSides.Both, t, prefix, out _, objectGraph: true, checks: true));
    }

    [Fact]
    public void 羽化但本帧没有过渡带时拆侧与不羽化逐字节相同且表面峰值不变()
    {
        using var rig = new SideRig(replace: false, gpu: false);
        foreach (bool prefix in new[] { false, true })
        foreach (long t in new long[] { 500_000, 2_500_000 })
        {
            var hard = rig.Render(SideProject(0, SideUnsung, SideSung), MaterialSides.Both, t, prefix, out var hardUsage);
            var feathered = rig.Render(SideProject(.5, SideUnsung, SideSung), MaterialSides.Both, t, prefix, out var featheredUsage);
            Assert.Equal(hard, feathered);
            Assert.Equal((hardUsage.PeakSurfaces, hardUsage.PeakBytes), (featheredUsage.PeakSurfaces, featheredUsage.PeakBytes));
        }
    }
}
