using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using SkiaSharp;
using Fx = MakoKaraGenerator.Scripting;
using static MakoKaraGenerator.Rendering.Skia.SkiaSceneGeometry;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class SkiaRenderPipeline
{
    private sealed record CapturePaint(GraphInputSource Source, SceneCaptureRecipe Recipe, string? TokenId,
        ImageSurfaceBudgetFrame? Budget = null, long PixelBytes = 0, SceneColorPart? Part = null, HashSet<string>? ExcludedTokens = null,
        EffectMaterialFallback? MaterialFallback = null, bool? RequestedSungPass = null, bool MaterialOperation = false,
        CaptureDrawProbe? Probe = null)
    {
        /// <summary>即将在未被剪空的画布上发起部件绘制时记录；未记录即可证明捕获面仍是初始透明。</summary>
        public void MarkDrawn(SKCanvas canvas) { if (Probe is { } probe && !canvas.IsClipEmpty) probe.Drew = true; }
        public bool IsCoverage => Source != GraphInputSource.TargetColor;
        public bool SungOnly => Source == GraphInputSource.SungCoverage;
        public bool SuppressCommonOpacity => Part.HasValue;
        public bool ShadowOnly => Part is SceneColorPart.BaseShadow or SceneColorPart.RubyShadow;
        public bool GlowOnly => Part is SceneColorPart.BaseGlow or SceneColorPart.RubyGlow;
        public bool StrokeOnly => Part is SceneColorPart.BaseStroke or SceneColorPart.RubyStroke;
        public bool IncludesGlyph(bool ruby) => (!ruby || Recipe.IncludeRuby) && (Part is null || ruby == (Part is SceneColorPart.RubyFill or SceneColorPart.RubyStroke or SceneColorPart.RubyShadow or SceneColorPart.RubyGlow));
        public ImageSurfaceReservation? Layer(string operation) => Budget?.Reserve(1, PixelBytes, operation);
        public LyricStyle Style(LyricStyle source)
        {
            if (!IsCoverage && MaterialFallback is not null)
            {
                var materialFill = MaterialFallback switch
                {
                    EffectMaterialFallback.Solid solid => new FillSpec { Color = solid.Color },
                    EffectMaterialFallback.Gradient gradient => new FillSpec { Color = gradient.Value.Stops[0].Color, Gradient = gradient.Value },
                    _ => throw new PreparedFrameException("InvalidMaterialFallback", "材质基础外观必须是明确纯色或完整渐变。"),
                };
                return source with { UnsungFill = materialFill, SungFill = materialFill, Opacity = 1, Shadow = null, Glow = null,
                    Stroke = Recipe.IncludeStroke && source.Stroke is { } outline ? outline with { Material = materialFill.Material } : null };
            }
            if (!IsCoverage) return source with { Stroke = Recipe.IncludeStroke ? source.Stroke : null, Shadow = Recipe.IncludeShadow ? source.Shadow : null, Glow = Recipe.IncludeGlow ? source.Glow : null };
            bool stroke = Source == GraphInputSource.StrokeCoverage || Source == GraphInputSource.TargetRegionCoverage && Recipe.Region is SceneCaptureRegion.Stroke or SceneCaptureRegion.FillAndStroke;
            bool fill = Source != GraphInputSource.StrokeCoverage && (Source != GraphInputSource.TargetRegionCoverage || Recipe.Region != SceneCaptureRegion.Stroke);
            var color = fill ? RgbaColor.White : new RgbaColor(0, 0, 0, 0);
            return source with { UnsungFill = new() { Color = color }, SungFill = new() { Color = color }, Opacity = 1,
                Stroke = stroke && source.Stroke is { IsValid: true, WidthPx: > 0 } spec ? spec with { Material = new MaterialSpec.Solid(RgbaColor.White) } : null, Shadow = null, Glow = null };
        }
    }
    // 记录同一捕获的全部 with 副本（含网格源面）是否真正发起过绘制；保守：任何可能着色的绘制都记为已绘制。
    private sealed class CaptureDrawProbe { public bool Drew { get; set; } }
    internal sealed record SceneDrawItem(string NodeId, SceneLineSample? Line, SceneTextMarkSample? Mark);
    private static IEnumerable<SceneDrawItem> DrawItems(SceneFrame frame)
    {
        Dictionary<string, int> last = new(StringComparer.Ordinal);
        for (int i = 0; i < frame.Lines.Length; i++) last[frame.Lines[i].Branch.Id] = i;
        for (int i = 0; i < frame.Lines.Length; i++)
        {
            var line = frame.Lines[i];
            yield return new(line.ElementId, line, null);
            if (last[line.Branch.Id] != i) continue;
            foreach (var mark in frame.TextMarks.Where(mark => frame.LeadIns.Any(cue => cue.CueId == mark.CueId && cue.BranchId == line.Branch.Id)))
                yield return new(mark.NodeId, null, mark);
        }
    }
    /// <summary>使用生产固定字形／最终变换捕获真实目标；源数据只读，返回图像由调用方释放。</summary>
    /// <remarks>框为显式 Canvas 逻辑框；Coverage 不包含外观或入场 alpha。产物和显式 SaveLayer 接入固定会话／可共享帧账。</remarks>
    public SkiaGraphSurface CaptureSceneTarget(RenderRequest request, SceneCaptureTarget target, SceneCaptureRecipe recipe,
        GraphInputSource source, GraphRect bounds, GraphExecutionBudget budget, ImageSurfaceBudgetFrame? surfaceBudget = null)
        => CaptureSceneTargetCore(request, target, recipe, source, bounds, budget, surfaceBudget, false);

    private SkiaGraphSurface CaptureSceneTargetCore(RenderRequest request, SceneCaptureTarget target, SceneCaptureRecipe recipe,
        GraphInputSource source, GraphRect bounds, GraphExecutionBudget budget, ImageSurfaceBudgetFrame? surfaceBudget, bool materialOperation)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(recipe); ArgumentNullException.ThrowIfNull(budget);
        ObjectDisposedException.ThrowIf(_disposed, this); request.CancellationToken.ThrowIfCancellationRequested();
        ValidateRequestOnce(request); ValidateNativeOnce(request.PreparedFrame);
        if (!SceneCaptureChannels.IsSupported(source) || !Enum.IsDefined(recipe.Region))
            throw new PreparedFrameException("UnsupportedCaptureChannel", "捕获来源或目标区域无效；背景和包图片由独立生产器提供。");
        recipe = BoundCaptureRecipe(target, recipe);
        var frame = request.PreparedFrame?.HierarchyScene ?? _geometry.GetSceneFrame(request.Project, request.TimeUs, request.LayoutLineId);
        var selected = SelectCaptureItems(frame, target, recipe);
        if (source == GraphInputSource.TargetColor) RejectRawMaterialColor(request, target, selected);
        if (source == GraphInputSource.TargetColor && request.PreparedFrame is { GroupOpacities.IsEmpty: false } prepared)
        {
            string? scope = target switch { SceneCaptureTarget.Group group => group.Target.NodeId.Value,
                SceneCaptureTarget.Line => selected[0].NodeId, _ => null };
            if (scope is not null && prepared.GroupOpacities.Any(opacity =>
                opacity.Opacity.Target.NodeId.Value == scope || recipe.IncludeDescendantGroups && IsDescendant(opacity.Opacity.Target.NodeId.Value)))
                throw new PreparedFrameException("GroupOpacityCapturePending", "组最终颜色捕获须读取隔离产物；原始捕获不能忽略已写入的组透明度。");
            bool IsDescendant(string id)
            {
                HashSet<string> seen = new(StringComparer.Ordinal); string? current = id;
                while (current is not null && seen.Add(current))
                {
                    var node = frame.Elements.Single(item => item.Id == current);
                    if (current == scope || node.SourceNodeId == scope) return true;
                    current = node.ParentId;
                }
                return false;
            }
        }
        foreach (var item in selected)
            _ = CaptureMatrix(item.Line?.Geometry?.WorldMatrix ?? item.Mark!.Geometry.WorldMatrix!.Value);
        return RasterSceneSurface(request, bounds, budget,
            source == GraphInputSource.TargetColor ? ShaderSurfaceKind.Color : ShaderSurfaceKind.Coverage,
            surfaceBudget, "CaptureSceneTarget", (canvas, resources, pixelBytes) =>
            {
                var paint = new CapturePaint(source, recipe, CaptureTokenId(target), resources, pixelBytes, MaterialOperation: materialOperation);
                foreach (var item in selected)
                {
                    request.CancellationToken.ThrowIfCancellationRequested();
                    if (item.Line is { } line) DrawSceneLine(canvas, request, request.Project.Composition, line, paint);
                    else DrawTextMark(canvas, request, request.Project.Composition, item.Mark!, paint);
                }
            });
    }
    private static ImmutableArray<SceneDrawItem> SelectCaptureItems(SceneFrame frame, SceneCaptureTarget target, SceneCaptureRecipe recipe)
    {
        var items = DrawItems(frame).ToArray(); var nodes = frame.Elements.ToDictionary(item => item.Id, StringComparer.Ordinal);
        bool Inside(string id, string ancestor)
        {
            HashSet<string> seen = new(StringComparer.Ordinal); string? current = id;
            while (current is not null)
            {
                if (!seen.Add(current)) throw new PreparedFrameException("InvalidCaptureTree", "捕获运行父树含环。");
                if (current == ancestor) return true;
                if (!nodes.TryGetValue(current, out var node)) return false;
                if (node.SourceNodeId == ancestor) return true;
                current = node.ParentId;
            }
            return false;
        }
        if (target is SceneCaptureTarget.Line { Target: { } line } && line.LineId.IsValid && line.BindingId.IsValid)
        {
            var item = items.SingleOrDefault(item => item.Line?.Frame.Line?.Id == line.LineId.Value && item.Line.BindingId == line.BindingId.Value)
                ?? throw Missing();
            return [.. items.Where(candidate => candidate == item || recipe.IncludeDescendantGroups && Inside(candidate.NodeId, item.NodeId))];
        }
        if (target is SceneCaptureTarget.Token { Target: { } token } && token.TokenId.IsValid && token.BindingId.IsValid)
        {
            var geometry = frame.TokenGeometries.SingleOrDefault(item => item.WithRuby == token) ?? throw Missing();
            if (!geometry.BaseGeometry.CanTransformIndependently)
                throw new PreparedFrameException(geometry.BaseGeometry.DiagnosticCode ?? "UnsupportedGlyphPartition", "元素共享字簇，不能独立捕获。");
            return [items.Single(item => item.Line?.BindingId == token.BindingId.Value)];
        }
        if (target is SceneCaptureTarget.Base { Target: { } basic } && basic.TokenId.IsValid && basic.BindingId.IsValid)
        {
            var geometry = frame.TokenGeometries.SingleOrDefault(item => item.BaseOnly == basic) ?? throw Missing();
            if (!geometry.BaseGeometry.CanTransformIndependently)
                throw new PreparedFrameException(geometry.BaseGeometry.DiagnosticCode ?? "UnsupportedGlyphPartition", "共享字簇不能独立捕获基础字。")
                    { TextTarget = new Scripting.V2.TextTarget.Base(basic) };
            return [items.Single(item => item.Line?.BindingId == basic.BindingId.Value)];
        }
        if (target is SceneCaptureTarget.Mark { Target: { } mark } && mark.NodeId.IsValid && mark.CueId.IsValid && mark.BindingId.IsValid)
            return [items.SingleOrDefault(item => item.Mark?.NodeId == mark.NodeId.Value && item.Mark.CueId == mark.CueId.Value
                && item.Mark.Geometry.BindingId == mark.BindingId.Value) ?? throw Missing()];
        if (target is SceneCaptureTarget.Group { Target: { } group } && group.NodeId.IsValid
            && frame.Elements.Any(item => item.Id == group.NodeId.Value && item.Kind == SceneElementKind.Group))
            return [.. items.Where(item => recipe.IncludeDescendantGroups ? Inside(item.NodeId, group.NodeId.Value)
                : nodes.TryGetValue(item.NodeId, out var node) && node.ParentId is { } parent
                    && (parent == group.NodeId.Value || nodes.GetValueOrDefault(parent)?.SourceNodeId == group.NodeId.Value))];
        throw Missing();
        static PreparedFrameException Missing() => new("CaptureTargetUnavailable", "捕获目标不是本帧真实装载；不从旧 Binding／Cue 或任意 ID 猜测。");
    }
    private static SceneCaptureRecipe BoundCaptureRecipe(SceneCaptureTarget target, SceneCaptureRecipe recipe)
        => target is SceneCaptureTarget.Base ? recipe with { IncludeRuby = false, IncludeDescendantGroups = false } : recipe;
    private static string? CaptureTokenId(SceneCaptureTarget target) => target switch
    { SceneCaptureTarget.Token token => token.Target.TokenId.Value, SceneCaptureTarget.Base basic => basic.Target.TokenId.Value, _ => null };
    private static SKMatrix CaptureMatrix(SceneMatrix matrix)
    {
        var result = new SKMatrix((float)matrix.A, (float)matrix.C, (float)matrix.Tx, (float)matrix.B, (float)matrix.D, (float)matrix.Ty, 0, 0, 1);
        if (!float.IsFinite(result.ScaleX) || !float.IsFinite(result.ScaleY) || !float.IsFinite(result.SkewX) || !float.IsFinite(result.SkewY)
            || !float.IsFinite(result.TransX) || !float.IsFinite(result.TransY) || !result.TryInvert(out var inverse)
            || !float.IsFinite(inverse.ScaleX) || !float.IsFinite(inverse.ScaleY) || !float.IsFinite(inverse.SkewX) || !float.IsFinite(inverse.SkewY)
            || !float.IsFinite(inverse.TransX) || !float.IsFinite(inverse.TransY))
            throw new PreparedFrameException("UnsupportedNativeCoordinates", "捕获矩阵不能作为有限可逆 float32 执行。");
        return result;
    }
    private static float CapturePadding(LyricStyle lineStyle, LyricStyleOverride overlay, CapturePaint? capture)
    {
        if (capture is null || capture.Source == GraphInputSource.TargetColor) return ElementPadding(lineStyle, overlay);
        return Math.Max(Extent(capture.Style(lineStyle)), Extent(capture.Style(ResolveElementStyle(lineStyle, overlay))));
        static float Extent(LyricStyle style)
        {
            double stroke = style.Stroke is { IsEnabled: true } outline ? outline.WidthPx : 0;
            double shadow = style.Shadow is { IsEnabled: true } spec
                ? Math.Max(Math.Abs((double)spec.OffsetXPx), Math.Abs((double)spec.OffsetYPx)) + 3 * (double)spec.BlurRadiusPx : 0;
            double total = stroke + shadow + 2;
            if (!float.IsFinite((float)total)) throw new PreparedFrameException("UnsupportedNativeStyleValues", "捕获装饰外扩超出后端范围，不能夹值。");
            return (float)total;
        }
    }
}
