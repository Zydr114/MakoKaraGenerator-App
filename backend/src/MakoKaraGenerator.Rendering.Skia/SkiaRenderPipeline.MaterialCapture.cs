using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class SkiaRenderPipeline
{
    /// <summary>用生产固定 glyph、最终变换及互补演唱裁切生成真实材质输入；fallback 来自已验证的材质定义。</summary>
    /// <remarks>part 须是基础字或注音的填充／外描边；sungPass 为 null 时取完整区域，否则只取已唱／未唱的一侧。
    /// 不纳入源填充、阴影、入场或组 alpha，调用方在生产对象合成时应用它们。</remarks>
    public SceneMaterialRegionCapture CaptureSceneMaterialRegion(RenderRequest request, SceneCaptureTarget target, SceneColorPart part,
        EffectMaterialFallback fallback, bool? sungPass, GraphRect bounds, GraphExecutionBudget budget,
        ImageSurfaceBudgetFrame? surfaceBudget = null)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(target); ArgumentNullException.ThrowIfNull(fallback);
        ArgumentNullException.ThrowIfNull(budget); ObjectDisposedException.ThrowIf(_disposed, this);
        request.CancellationToken.ThrowIfCancellationRequested(); ValidateRequestOnce(request); ValidateNativeOnce(request.PreparedFrame);
        bool stroke = part is SceneColorPart.BaseStroke or SceneColorPart.RubyStroke;
        bool ruby = part is SceneColorPart.RubyFill or SceneColorPart.RubyStroke;
        if (part is not (SceneColorPart.BaseFill or SceneColorPart.BaseStroke or SceneColorPart.RubyFill or SceneColorPart.RubyStroke)
            || target is SceneCaptureTarget.Group || (target is SceneCaptureTarget.Mark or SceneCaptureTarget.Base) && ruby)
            throw new PreparedFrameException("InvalidMaterialRegionTarget", "材质只绑定真实填充／外描边；组和阴影不是材质区域。");
        if (fallback is EffectMaterialFallback.Gradient gradient && !GradientSupport.IsSupported(gradient.Value))
            throw new PreparedFrameException("PackageMaterialFallbackUnsupported", GradientSupport.UnsupportedReason(gradient.Value)!);
        var recipe = new SceneCaptureRecipe(ruby, stroke, false, false, stroke ? SceneCaptureRegion.Stroke : SceneCaptureRegion.Fill, IncludeGlow: false);
        var frame = request.PreparedFrame?.HierarchyScene ?? _geometry.GetSceneFrame(request.Project, request.TimeUs, request.LayoutLineId);
        var item = SelectCaptureItems(frame, target, recipe).Single();
        _ = CaptureMatrix(item.Line?.Geometry?.WorldMatrix ?? item.Mark!.Geometry.WorldMatrix!.Value);
        using var ownFrame = surfaceBudget is null ? _surfaceBudget.BeginFrame() : null;
        var sharedBudget = surfaceBudget ?? ownFrame!;
        SkiaGraphSurface? region = null;
        try
        {
            region = Capture(GraphInputSource.TargetRegionCoverage, ShaderSurfaceKind.Coverage, null);
            var color = Capture(GraphInputSource.TargetColor, ShaderSurfaceKind.Color, fallback);
            return new(region, color);
        }
        catch { region?.Dispose(); throw; }

        SkiaGraphSurface Capture(GraphInputSource source, ShaderSurfaceKind kind, EffectMaterialFallback? appearance)
            => RasterSceneSurface(request, bounds, budget, kind, sharedBudget, "CaptureMaterialRegion", (canvas, resources, bytes) =>
            {
                var paint = new CapturePaint(source, recipe, CaptureTokenId(target),
                    resources, bytes, part, MaterialFallback: appearance, RequestedSungPass: sungPass, MaterialOperation: true);
                if (item.Line is { } line) DrawSceneLine(canvas, request, request.Project.Composition, line, paint);
                else DrawTextMark(canvas, request, request.Project.Composition, item.Mark!, paint);
            });
    }
}
