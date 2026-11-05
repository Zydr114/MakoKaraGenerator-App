using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using Fx = MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Rendering.Skia;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed partial class MainCanvasImageEffectsTests
{
    [Fact]
    public void G8_材质两张输入及额外raw捕获背景作者图宿主合成逐真实pass且不重复收费()
    {
        var request = Request(Snapshot(false));
        var target = new SceneCaptureTarget.Base(Assert.Single(request.PreparedFrame!.FinalGeometry).BaseOnly);
        using var renderer = new SkiaRenderPipeline();
        using var budget = new ImageSurfaceBudgetSession(); using var frame = budget.BeginFrame();
        int passes = 0; using var account = frame.BeginScenePassBudget(() => passes++);
        using (var material = renderer.CaptureSceneMaterialRegion(request, target, SceneColorPart.BaseFill,
            new EffectMaterialFallback.Solid(new(51,128,204,160)), null, Bounds, GraphExecutionBudget.Default, frame))
            Assert.Equal(2, passes);
        foreach (var source in new[] { GraphInputSource.GlyphCoverage, GraphInputSource.StrokeCoverage, GraphInputSource.SungCoverage })
            using (renderer.CaptureSceneTarget(request, target, Whole, source, Bounds, GraphExecutionBudget.Default, frame)) { }
        Assert.Equal(5, passes);
        Assert.Null(renderer.CaptureSceneBackground(request, GraphInputSource.BackgroundColor, Bounds, GraphExecutionBudget.Default, frame));
        Assert.Null(renderer.CaptureSceneBackground(request with { TransparentBackground = false, PreparedFrame = null }, GraphInputSource.BackgroundMedia,
            Bounds, GraphExecutionBudget.Default, frame));
        Assert.Equal(5, passes); // absent/transparent backgrounds do not allocate or consume a pass.
        using (renderer.CaptureSceneBackground(request with { TransparentBackground = false, PreparedFrame = null }, GraphInputSource.BackgroundColor,
            Bounds, GraphExecutionBudget.Default, frame)) { }
        Assert.Equal(6, passes);
        using var input = renderer.CaptureSceneTarget(request, target, Whole, GraphInputSource.TargetColor, Bounds, GraphExecutionBudget.Default, frame);
        int beforeGraph = passes;
        using var graph = Tint("single-node");
        var context = new ImageGraphFrameContext(request.TimeUs,640,360,Bounds,1,1,MakoKaraGenerator.Scripting.ParameterValues.Empty,true);
        using var result = graph.Execute(context, new Dictionary<string,SkiaGraphInput> { ["color"] = new(input,GraphInputSource.TargetColor) }, frame);
        Assert.Equal(beforeGraph + 1, passes);
        using var device = new CpuSkiaRenderDevice(); using var host = new SceneCompositionProgram(device,GraphExecutionBudget.Default);
        int beforeHost = passes;
        using (host.CaptureCompositionPayload(result.Output,context,frame)) { }
        Assert.Equal(beforeHost + 2, passes); // one color and one erasure raster; not an extra estimated host fee.
        result.Dispose(); input.Dispose();
        Assert.Equal(0,budget.Usage.ActiveSurfaces); Assert.Equal(0,budget.Usage.EstimatedBytes);
    }

    [Fact]
    public void G9_第8192次允许第8193次在实际native分配前失败并可释放恢复帧租约()
    {
        var request = Request(Snapshot(false)); var target = new SceneCaptureTarget.Line(Line(request).Target);
        using var device = new GroupWorkDevice(); using var renderer = new SkiaRenderPipeline(null,null,null,null,false,device,false);
        using var budget = new ImageSurfaceBudgetSession(); using var frame = budget.BeginFrame();
        var work = new SkiaRenderPipeline.GroupCompositionBudget(); work.Pass(8191);
        using (frame.BeginScenePassBudget(() => work.Pass()))
        {
            using (renderer.CaptureSceneTarget(request,target,Fill,GraphInputSource.GlyphCoverage,Bounds,GraphExecutionBudget.Default,frame)) { }
            int before = device.Created;
            var error = Assert.Throws<PreparedFrameException>(() => renderer.CaptureSceneTarget(request,target,Fill,
                GraphInputSource.SungCoverage,Bounds,GraphExecutionBudget.Default,frame));
            Assert.Equal("GroupCompositionBudgetExceeded",error.Code); Assert.Equal(before,device.Created);
            Assert.Equal(0,budget.Usage.ActiveSurfaces); Assert.Equal(0,budget.Usage.EstimatedBytes);
        }
        // A disposed scope cannot poison reuse of the same frame.
        int recovered = 0; using (frame.BeginScenePassBudget(() => recovered++))
            using (renderer.CaptureSceneTarget(request,target,Fill,GraphInputSource.GlyphCoverage,Bounds,GraphExecutionBudget.Default,frame)) { }
        Assert.Equal(1,recovered);
        Assert.Equal(0,budget.Usage.ActiveSurfaces); Assert.Equal(0,budget.Usage.EstimatedBytes);
    }

    [Fact]
    public void G10_活动层以实际拆分数累计512允许513拒绝且失败不污染原计数()
    {
        var work = new SkiaRenderPipeline.GroupCompositionBudget(); int leaf = 0, parent = 0;
        work.ActiveLayers(ref leaf,512);
        var error = Assert.Throws<PreparedFrameException>(() => work.ActiveLayers(ref parent,1));
        Assert.Equal("GroupCompositionBudgetExceeded",error.Code); Assert.Equal(0,parent); Assert.Equal(512,leaf);
        work.ActiveLayers(ref leaf,511); work.ActiveLayers(ref parent,1);
        Assert.Throws<PreparedFrameException>(() => work.ActiveLayers(ref leaf,512)); Assert.Equal(511,leaf);
        work.ActiveLayers(ref parent,0); work.ActiveLayers(ref leaf,512); work.ActiveLayers(ref leaf,0);
        work.ActiveLayers(ref parent,512); work.ActiveLayers(ref parent,0);
    }

    [Fact]
    public void G11_取消先于收费及分配且回调中取消也不得分配()
    {
        var request = Request(Snapshot(false)); var target = new SceneCaptureTarget.Line(Line(request).Target);
        using var device = new GroupWorkDevice(); using var renderer = new SkiaRenderPipeline(null,null,null,null,false,device,false);
        using var budget = new ImageSurfaceBudgetSession(); using var frame = budget.BeginFrame();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        int charges = 0; using (frame.BeginScenePassBudget(() => charges++))
            Assert.Throws<OperationCanceledException>(() => renderer.CaptureSceneTarget(request with { CancellationToken=cancellation.Token },target,
                Fill,GraphInputSource.GlyphCoverage,Bounds,GraphExecutionBudget.Default,frame));
        Assert.Equal(0,charges); Assert.Equal(0,device.Created);
        using var during = new CancellationTokenSource();
        using (frame.BeginScenePassBudget(() => { charges++; during.Cancel(); }))
            Assert.Throws<OperationCanceledException>(() => renderer.CaptureSceneTarget(request with { CancellationToken=during.Token },target,
                Fill,GraphInputSource.GlyphCoverage,Bounds,GraphExecutionBudget.Default,frame));
        Assert.Equal(1,charges); Assert.Equal(0,device.Created);
        Assert.Equal(0,budget.Usage.ActiveSurfaces); Assert.Equal(0,budget.Usage.EstimatedBytes);
    }

    private sealed class GroupWorkDevice : ISkiaRenderDevice
    {
        private readonly CpuSkiaRenderDevice _inner = new();
        internal int Created { get; private set; }
        public object SynchronizationRoot => _inner.SynchronizationRoot;
        public SkiaRenderDeviceInfo Info => _inner.Info;
        public SKSurface CreateSurface(SKImageInfo info) { Created++; return _inner.CreateSurface(info); }
        public void FlushAndSubmit(SKSurface surface) => _inner.FlushAndSubmit(surface);
        public SKImage? RasterizeImage(SKImage image) => _inner.RasterizeImage(image);
        public bool ReadPixels(SKSurface surface,SKImageInfo info,IntPtr destination,int rowBytes) => _inner.ReadPixels(surface,info,destination,rowBytes);
        public bool ReadPixels(SKImage image,SKImageInfo info,IntPtr destination,int rowBytes) => _inner.ReadPixels(image,info,destination,rowBytes);
        public void Dispose() => _inner.Dispose();
    }
}
