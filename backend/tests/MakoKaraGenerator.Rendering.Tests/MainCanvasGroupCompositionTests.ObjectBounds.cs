using MakoKaraGenerator.Rendering.Skia;
using ObjectBelowSource = MakoKaraGenerator.Rendering.Skia.SkiaRenderPipeline.ObjectBelowSource;
using ObjectRenderPlan = MakoKaraGenerator.Rendering.Skia.SkiaRenderPipeline.ObjectRenderPlan;

namespace MakoKaraGenerator.Rendering.Tests;

/// <summary>对象级计划第 3 刀：组合调度下对象的工作框按自身范围收紧，像素与沿用调度器整框时一致。</summary>
public sealed partial class MainCanvasImageEffectsTests
{
    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void G8_组合调度下对象工作框收紧且像素与整框一致(bool decorated, bool transparent)
    {
        // 父组 identity 图触发前缀调度；孩子是没有图或材质的普通行，只为导出闭合层而捕获全部部件。
        var request = Request(Snapshot(decorated), transparent, 1280, 720);
        using var identity = GroupIdentity();
        using var resources = new SceneImageEffectResources([new(Catalog.GetGraph("first"), identity, GroupWhole, Bounds)]);
        var bound = Bindings(request, Bind("parent", GroupRoot(request)));

        (byte[] Pixels, long PeakBytes, List<ObjectRenderPlan> Plans) Render(bool tight)
        {
            using var device = new CpuSkiaRenderDevice(); using var budget = new ImageSurfaceBudgetSession();
            List<ObjectRenderPlan> plans = [];
            using var renderer = new SkiaRenderPipeline(null, null, budget, resources, false, device, false)
                { TightObjectRegions = tight, ObjectPlanBuilt = plans.Add };
            using var frame = renderer.Render(bound);
            Assert.Empty(frame.ImageEffectDiagnostics); Assert.Equal(0, budget.Usage.ActiveSurfaces);
            return (frame.Pixels.ToArray(), budget.Usage.PeakBytes, plans);
        }
        var full = Render(false); var tight = Render(true);

        Assert.Contains(full.Pixels, value => value != 0);
        Assert.Equal(full.Pixels, tight.Pixels);
        Assert.NotEmpty(tight.Plans); Assert.Equal(full.Plans.Count, tight.Plans.Count);
        Assert.All(full.Plans.Concat(tight.Plans), plan =>
        {
            Assert.True(plan.ClosedExport); Assert.Equal(ObjectBelowSource.CompositionPrefix, plan.Below);
            Assert.Empty(plan.Graphs); Assert.Empty(plan.Materials);
        });
        // 不收紧时每个对象沿用调度器给定的整框；收紧后只占本行（含注音与装饰外扩），并且不超出调度器的框。
        var scheduler = Assert.Single(full.Plans.Select(plan => plan.WorkBounds).Distinct());
        Assert.True(scheduler.Contains(Bounds));
        Assert.All(tight.Plans, plan =>
        {
            Assert.True(scheduler.Contains(plan.WorkBounds), $"工作框 {plan.WorkBounds} 超出调度器的框 {scheduler}");
            Assert.True(plan.WorkBounds.Width * plan.WorkBounds.Height < scheduler.Width * scheduler.Height / 2, $"工作框 {plan.WorkBounds}");
        });
        // 部件捕获、闭合层与导出载荷都按对象的小框分配。
        Assert.True(tight.PeakBytes < full.PeakBytes, $"预算峰值 {full.PeakBytes} → {tight.PeakBytes}");
    }
}
