using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class SkiaRenderPipeline
{
    /// <summary>捕获本帧真实基础字／注音彩色部件；不纳入后代，共同合成透明度单独返回，不逐部件烘入。</summary>
    /// <remarks>行和独立元素族复用最终固定 glyph 与逐字裁切。组由孩子重组；标记没有注音部件。</remarks>
    public SceneColorPartResult CaptureSceneColorPart(RenderRequest request, SceneCaptureTarget target, SceneColorPart part,
        GraphRect bounds, GraphExecutionBudget budget, ImageSurfaceBudgetFrame? surfaceBudget = null)
    {
        RejectRawMaterialColor(request, target);
        return CaptureColorPart(request, target, part, bounds, budget, surfaceBudget, null);
    }

    private SceneColorPartResult CaptureColorPart(RenderRequest request, SceneCaptureTarget target, SceneColorPart part,
        GraphRect bounds, GraphExecutionBudget budget, ImageSurfaceBudgetFrame? surfaceBudget, HashSet<string>? excludedTokens, bool? sungPass = null,
        CaptureDrawProbe? probe = null)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(target); ArgumentNullException.ThrowIfNull(budget);
        ObjectDisposedException.ThrowIf(_disposed, this); request.CancellationToken.ThrowIfCancellationRequested();
        ValidateRequestOnce(request); ValidateNativeOnce(request.PreparedFrame);
        if (!Enum.IsDefined(part)) throw new PreparedFrameException("InvalidSceneColorPart", "部件类别无效。");
        bool ruby = part is SceneColorPart.RubyFill or SceneColorPart.RubyStroke or SceneColorPart.RubyShadow or SceneColorPart.RubyGlow;
        if (target is SceneCaptureTarget.Group || (target is SceneCaptureTarget.Mark or SceneCaptureTarget.Base) && ruby)
            throw new PreparedFrameException("UnsupportedSceneColorPartTarget", "组没有自己的字形部件，文字标记没有注音部件。");
        var recipe = new SceneCaptureRecipe(ruby, part is SceneColorPart.BaseStroke or SceneColorPart.RubyStroke,
            part is SceneColorPart.BaseShadow or SceneColorPart.RubyShadow, false, SceneCaptureRegion.FillAndStroke, part is SceneColorPart.BaseGlow or SceneColorPart.RubyGlow);
        var frame = request.PreparedFrame?.HierarchyScene ?? _geometry.GetSceneFrame(request.Project, request.TimeUs, request.LayoutLineId);
        var item = SelectCaptureItems(frame, target, recipe).Single();
        _ = CaptureMatrix(item.Line?.Geometry?.WorldMatrix ?? item.Mark!.Geometry.WorldMatrix!.Value);
        double opacity = item.Line?.Transform.Opacity ?? MarkOpacity();
        if (!double.IsFinite(opacity) || opacity is < 0 or > 1)
            throw new PreparedFrameException("InvalidSceneColorPartOpacity", "真实共同透明度必须有限且在 [0,1]，不能夹值。");
        byte opacityByte = ToOpacityByte((float)opacity);
        var surface = RasterSceneSurface(request, bounds, budget, ShaderSurfaceKind.Color, surfaceBudget, "CaptureSceneColorPart", (canvas, resources, bytes) =>
        {
            var paint = new CapturePaint(GraphInputSource.TargetColor, recipe, CaptureTokenId(target),
                resources, bytes, part, excludedTokens, RequestedSungPass: sungPass, Probe: probe);
            if (item.Line is { } line) DrawSceneLine(canvas, request, request.Project.Composition, line, paint);
            else DrawTextMark(canvas, request, request.Project.Composition, item.Mark!, paint);
        });
        return new(request, target, part, surface, opacity, opacityByte);

        double MarkOpacity()
        {
            var mark = item.Mark!;
            var actual = ((SceneCaptureTarget.Mark)target).Target;
            var style = request.PreparedFrame?.MarkStyles.Any(style => style.Target == actual) == true
                ? request.PreparedFrame.GetMarkDrawingStyle(actual) : mark.Appearance.ToLyricStyle();
            return mark.Opacity * style.Opacity;
        }
    }
}
