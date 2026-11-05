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
        using var snapshot = main.Snapshot();
        using var wrapped = SkiaGraphSurface.Borrow(snapshot, new(new(0, 0, composition.CanvasWidth, composition.CanvasHeight), GraphCoordinateSpace.Canvas,
            SceneMatrix.Identity, output.ScaleXOf(composition), output.ScaleYOf(composition)), ShaderSurfaceKind.Color, _device);
        return RasterSceneSurface(request, bounds, state.Budget, ShaderSurfaceKind.Color, state.Resources, "SceneBackdropBelow",
            (canvas, _, _) => DrawGraphSurface(canvas, wrapped, SKBlendMode.Src));
    }
    private SkiaGraphSurface AssembleImageLayers(RenderRequest request, ImageObjectState state, IEnumerable<ColorLayer> layers, SkiaGraphSurface? below)
    {
        var ordered = layers.OrderBy(layer => layer.Order).ToArray();
        SkiaGraphSurface current = RasterSceneSurface(request, state.Bounds, state.Budget, ShaderSurfaceKind.Color, state.Resources, "SceneLayerBase",
            (canvas, _, _) => { if (below is not null) DrawGraphSurface(canvas, below, SKBlendMode.Src); });
        try
        {
            foreach (var layer in ordered)
            {
                request.CancellationToken.ThrowIfCancellationRequested();
                SkiaGraphSurface next;
                if (!layer.History.IsEmpty)
                {
                    try { next = layer.Graph!.CompositeChain(layer.History, current, state.Resources, 1, request.CancellationToken); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception error)
                    {
                        var owner = layer.Owner!;
                        string code = error switch { ImageGraphException e => e.Code, ImageSurfaceBudgetException e => e.Code, _ => "SceneImageCompositeFailed" };
                        throw SceneOwnerFailure(owner, "CompositeImageGraph", request.TimeUs, code, error);
                    }
                }
                else next = RasterSceneSurface(request, state.Bounds, state.Budget, ShaderSurfaceKind.Color, state.Resources, "SceneLayerAssembly", (canvas, _, _) =>
                { DrawGraphSurface(canvas, current, SKBlendMode.Src); DrawGraphSurface(canvas, layer.Surface, SKBlendMode.SrcOver); });
                current.Dispose(); current = next;
            }
            return current;
        }
        catch { current.Dispose(); throw; }
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
