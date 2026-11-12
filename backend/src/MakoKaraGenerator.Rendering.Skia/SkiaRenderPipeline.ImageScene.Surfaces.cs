using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class SkiaRenderPipeline
{
    private SkiaGraphSurface SnapshotSceneBelow(SKSurface main, RenderRequest request, GraphRect bounds, ImageObjectState state)
    {
        var output = request.ResolveTarget(); var composition = request.Project.Composition;
        long bytes = checked((long)output.PixelWidth * output.PixelHeight * 4);
        using var scratch = state.Resources.Reserve(1, bytes, "SceneBackdropSnapshot");
        state.Resources.ChargeScenePass(request.CancellationToken);
        using var snapshot = main.Snapshot();
        using var wrapped = SkiaGraphSurface.Borrow(snapshot, new(new(0, 0, composition.CanvasWidth, composition.CanvasHeight), GraphCoordinateSpace.Canvas,
            SceneMatrix.Identity, output.ScaleXOf(composition), output.ScaleYOf(composition)), ShaderSurfaceKind.Color, _device);
        return RasterSceneSurface(request, bounds, state.Budget, ShaderSurfaceKind.Color, state.Resources, "SceneBackdropBelow",
            (canvas, _, _) => DrawGraphSurface(canvas, wrapped, SKBlendMode.Src));
    }
    /// <summary>F4：相邻的无历史层在同一张表面上依次绘制；关闭时逐层复制，供回归对照。建计划时写入对象计划的组装方式。</summary>
    internal bool SinglePassLayerAssembly { get; set; } = true;

    /// <remarks>无历史层按源序 SrcOver 叠加：同一张表面上依次绘制与逐层复制再叠加逐字节相同（复制是 Src 原样拷贝）。
    /// 共享空层全透明，SrcOver 不改像素，直接跳过。有历史的层把当前结果交给图链合成，扫字两侧合并层按闭合对合成，都在此断开。</remarks>
    private SkiaGraphSurface AssembleImageLayers(RenderRequest request, ImageObjectState state, IEnumerable<ColorLayer> layers, SkiaGraphSurface? below)
    {
        var ordered = layers.OrderBy(layer => layer.Order).ToArray();
        if (state.Assembly == ObjectLayerAssembly.PerLayer) return AssembleImageLayersPerLayer(request, state, ordered, below);
        SkiaGraphSurface? current = null;
        try
        {
            int index = 0;
            while (true)
            {
                int end = index;
                while (end < ordered.Length && !Composited(ordered[end])) end++;
                var run = ordered[index..end].Where(layer => !ReferenceEquals(layer.Surface, state.Empty)).ToArray();
                if (current is null || run.Length > 0)
                {
                    var previous = current;
                    var source = previous ?? below;
                    var next = RasterSceneSurface(request, state.Bounds, state.Budget, ShaderSurfaceKind.Color, state.Resources,
                        previous is null ? "SceneLayerBase" : "SceneLayerAssembly", (canvas, _, _) =>
                        {
                            if (source is not null) DrawGraphSurface(canvas, source, SKBlendMode.Src);
                            foreach (var layer in run)
                            {
                                request.CancellationToken.ThrowIfCancellationRequested();
                                DrawGraphSurface(canvas, layer.Surface, SKBlendMode.SrcOver);
                            }
                        });
                    previous?.Dispose(); current = next;
                }
                if (end == ordered.Length) return current;
                current = CompositeLayer(request, state, ordered[end], current);
                index = end + 1;
            }
        }
        catch { current?.Dispose(); throw; }
    }
    private SkiaGraphSurface AssembleImageLayersPerLayer(RenderRequest request, ImageObjectState state, ColorLayer[] ordered, SkiaGraphSurface? below)
    {
        SkiaGraphSurface current = RasterSceneSurface(request, state.Bounds, state.Budget, ShaderSurfaceKind.Color, state.Resources, "SceneLayerBase",
            (canvas, _, _) => { if (below is not null) DrawGraphSurface(canvas, below, SKBlendMode.Src); });
        try
        {
            foreach (var layer in ordered)
            {
                request.CancellationToken.ThrowIfCancellationRequested();
                if (Composited(layer)) { current = CompositeLayer(request, state, layer, current); continue; }
                var next = RasterSceneSurface(request, state.Bounds, state.Budget, ShaderSurfaceKind.Color, state.Resources, "SceneLayerAssembly", (canvas, _, _) =>
                { DrawGraphSurface(canvas, current, SKBlendMode.Src); DrawGraphSurface(canvas, layer.Surface, SKBlendMode.SrcOver); });
                current.Dispose(); current = next;
            }
            return current;
        }
        catch { current.Dispose(); throw; }
    }
    /// <summary>不能按 SrcOver 叠加、要与当前结果合成的层：有图链历史，或是扫字两侧合并层（带闭合对）。</summary>
    private static bool Composited(ColorLayer layer) => !layer.History.IsEmpty || layer.ClosedPayload is not null;

    /// <summary>把有历史的层与当前结果按图链合成，扫字两侧合并层按闭合对的替换规则合成；成功后释放旧的当前结果，失败时由调用方释放。</summary>
    /// <remarks>合并层的历史只供后续图读取替换范围，合成用合并后的一对（P3 设计 §10.2 第 4 条）。</remarks>
    private SkiaGraphSurface CompositeLayer(RenderRequest request, ImageObjectState state, ColorLayer layer, SkiaGraphSurface current)
    {
        request.CancellationToken.ThrowIfCancellationRequested();
        SkiaGraphSurface next;
        try
        {
            next = layer.ClosedPayload is { } payload
                ? _compositionProgram.CompositePayload(payload, current, state.Resources, 1, clipToBelow: true, request.CancellationToken)
                : layer.Graph!.CompositeChain(layer.History, current, state.Resources, 1, request.CancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            var owner = layer.Owner!;
            string code = error switch { ImageGraphException e => e.Code, ImageSurfaceBudgetException e => e.Code, _ => "SceneImageCompositeFailed" };
            throw SceneOwnerFailure(owner, "CompositeImageGraph", request.TimeUs, code, error);
        }
        current.Dispose();
        return next;
    }
    private SkiaGraphSurface ApplyImageObjectOpacity(RenderRequest request, ImageObjectState state, SkiaGraphSurface processed, SkiaGraphSurface below)
    {
        using var inputs = state.Resources.BorrowInputs([processed, below]);
        using var shader = _isolationResources.Value.CreateShader(processed, below, state.Opacity, _device, request.CancellationToken);
        return RasterSceneSurface(request, state.Bounds, state.Budget, ShaderSurfaceKind.Color, state.Resources, "SceneObjectOpacity", (canvas, _, _) =>
        {
            var b = state.Bounds;
            using var paint = new SKPaint { Shader = shader, BlendMode = SKBlendMode.Src, IsAntialias = false };
            canvas.DrawRect(new SKRect((float)b.Left, (float)b.Top, (float)b.Right, (float)b.Bottom), paint);
        });
    }
}
