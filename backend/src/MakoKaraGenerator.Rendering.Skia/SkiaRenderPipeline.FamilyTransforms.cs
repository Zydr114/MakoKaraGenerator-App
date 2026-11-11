using System.Collections.Immutable;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Text;
using SkiaSharp;
using Fx = MakoKaraGenerator.Scripting;
using static MakoKaraGenerator.Rendering.Skia.SkiaSceneGeometry;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class SkiaRenderPipeline
{
    private void ValidateNativePreparedFrame(PreparedRenderFrame? prepared)
    {
        if (prepared is null) return;
        ValidateNativeOverlays(prepared);
        foreach (var transform in prepared.TokenTransforms) Matrix(transform.LocalMatrix);
        foreach (var transform in prepared.BaseTransforms) Matrix(transform.LocalMatrix);
        foreach (var transform in prepared.ClusterTransforms) Matrix(transform.LocalMatrix);
        foreach (var transform in prepared.MarkTransforms) Matrix(transform.LocalMatrix);
        foreach (var transform in prepared.LineTransforms) Matrix(transform.LocalMatrix);
        foreach (var transform in prepared.GroupTransforms) Matrix(transform.LocalMatrix);
        foreach (var geometry in prepared.FinalGroupGeometry)
        {
            Matrix(geometry.BaseWorldMatrix);
            var box = geometry.ReferenceBounds;
            var corners = new Fx.Point2[] { new(box.Left, box.Top), new(box.Right, box.Top), new(box.Right, box.Bottom), new(box.Left, box.Bottom) };
            foreach (var point in corners.Select(geometry.BaseWorldMatrix.Map).Concat(geometry.WorldInkCorners))
                if (!float.IsFinite((float)point.X) || !float.IsFinite((float)point.Y))
                    throw new PreparedFrameException("UnsupportedNativeCoordinates", "组效果几何超出后端有限范围。");
        }
        foreach (var line in prepared.FinalLineGeometry)
        {
            Matrix(line.BaseGeometry.BaseWorldMatrix);
            foreach (var point in line.BaseGeometry.WorldInkCorners)
                if (!float.IsFinite((float)point.X) || !float.IsFinite((float)point.Y))
                    throw new PreparedFrameException("UnsupportedNativeCoordinates", "整行效果几何超出后端有限范围。");
        }
        foreach (var geometry in prepared.FinalMarkGeometry)
        {
            Matrix(geometry.Geometry.BaseWorldMatrix);
            foreach (var point in geometry.Geometry.WorldInkCorners)
                if (!float.IsFinite((float)point.X) || !float.IsFinite((float)point.Y))
                    throw new PreparedFrameException("UnsupportedNativeCoordinates", "标记效果几何超出后端有限范围。");
        }
        foreach (var geometry in prepared.FinalGeometry)
        {
            Matrix(geometry.BaseGeometry.BaseWorldMatrix);
            foreach (var point in geometry.FamilyGeometry.WorldInkCorners)
                if (!float.IsFinite((float)point.X) || !float.IsFinite((float)point.Y))
                    throw new PreparedFrameException("UnsupportedNativeCoordinates", "效果几何超出后端有限坐标范围。");
        }
        foreach (var output in prepared.TokenStyles)
        {
            var style = prepared.GetTokenDrawingStyle(output.Target);
            var line = prepared.Scene.Lines.Single(item => item.BindingId == output.Target.BindingId.Value).Frame.Line!;
            var layout = _geometry.GetLineLayout(line, style);
            var shaped = layout.Shaped;
            ValidateGradients(style, shaped.Width);
            foreach (var ruby in layout.Ruby) ValidateGradients(style, ruby.Shaped.Width);
        }
        foreach (var output in prepared.MarkStyles)
        {
            var style = prepared.GetMarkDrawingStyle(output.Target);
            var mark = prepared.Scene.TextMarks.Single(item => item.NodeId == output.Target.NodeId.Value && item.CueId == output.Target.CueId.Value
                && item.Geometry.BindingId == output.Target.BindingId.Value);
            ValidateGradients(style, _geometry.GetLayout(mark.Text, LayoutOptionsFor(style)).Width);
        }
        foreach (var output in prepared.BaseStyles)
        {
            var style = prepared.GetBaseDrawingStyle(output.Target);
            var line = prepared.Scene.Lines.Single(item => item.BindingId == output.Target.BindingId.Value).Frame.Line!;
            ValidateGradients(style, _geometry.GetLineLayout(line, style).Shaped.Width);
        }
        void ValidateGradients(LyricStyle style, float lineWidth)
        {
            foreach (var gradient in new[] { style.UnsungFill.Gradient, style.SungFill.Gradient, style.Stroke?.Gradient })
            {
                if (gradient is not null && GradientSupport.IsSupported(gradient))
                    ValidateNativeGradient(gradient, lineWidth, style, prepared.Snapshot.Composition);
            }
        }
        static void Matrix(Fx.Affine2 matrix)
        {
            if (!float.IsFinite((float)matrix.A) || !float.IsFinite((float)matrix.B) || !float.IsFinite((float)matrix.C)
                || !float.IsFinite((float)matrix.D) || !float.IsFinite((float)matrix.Tx) || !float.IsFinite((float)matrix.Ty))
                throw new PreparedFrameException("UnsupportedNativeCoordinates", "效果矩阵超出后端有限坐标范围。");
            var native = new SKMatrix((float)matrix.A, (float)matrix.C, (float)matrix.Tx,
                (float)matrix.B, (float)matrix.D, (float)matrix.Ty, 0, 0, 1);
            if (!native.TryInvert(out var inverse) || !float.IsFinite(inverse.ScaleX) || !float.IsFinite(inverse.ScaleY)
                || !float.IsFinite(inverse.SkewX) || !float.IsFinite(inverse.SkewY)
                || !float.IsFinite(inverse.TransX) || !float.IsFinite(inverse.TransY))
                throw new PreparedFrameException("UnsupportedNativeCoordinates", "效果矩阵转换到后端精度后不可逆，不能静默丢失文字。");
        }
    }
    private void DrawChangedFamilies(SKCanvas canvas, RenderRequest request, PreparedRenderFrame? prepared, SceneLineSample scene,
        ShapedLine shaped, TextLayoutOptions options, IReadOnlyList<RubyLayout> ruby,
        LyricStyle lineStyle, Composition composition, float origin, float baseline, bool antialias,
        ImmutableArray<Fx.TokenFamilyTransform> transforms, ImmutableArray<Fx.TokenFamilyStyle> styles, CapturePaint? capture = null,
        IReadOnlyDictionary<string, Fx.TokenGeometrySample>? warpedFamilies = null, ImageSurfaceBudgetFrame? resources = null)
    {
        var sample = scene.Frame; var line = sample.Line!;
        Dictionary<string, Fx.TokenFamilyTransform> byToken = transforms.ToDictionary(static item => item.Target.TokenId.Value, StringComparer.Ordinal);
        var byStyle = styles.ToDictionary(static item => item.Target.TokenId.Value, StringComparer.Ordinal);
        // 有辉光时按与图像协调器一致的部件顺序绘制整域，避免后画族的 halo 覆盖邻族填充。
        // 没有辉光的历史路径保持原有工作量和像素顺序。
        bool hasGlow = capture?.Recipe.IncludeGlow != false && (lineStyle.Glow is { IsEnabled: true }
            || line.Tokens.Any(token => token.StyleOverride is { } overlay && ResolveElementStyle(lineStyle, overlay).Glow is { IsEnabled: true })
            || prepared is not null && prepared.TokenStyles.Any(output => output.Target.BindingId.Value == scene.BindingId
                && prepared.GetTokenDrawingStyle(output.Target).Glow is { IsEnabled: true })
            || prepared is not null && prepared.BaseStyles.Any(output => output.Target.BindingId.Value == scene.BindingId
                && prepared.GetBaseDrawingStyle(output.Target).Glow is { IsEnabled: true }));
        if (hasGlow && (capture is null || capture is { Source: Core.Effects.GraphInputSource.TargetColor, Part: null }))
        {
            var original = capture ?? new CapturePaint(Core.Effects.GraphInputSource.TargetColor,
                new(true, true, true, false, SceneCaptureRegion.FillAndStroke), null);
            foreach (var part in ColorPartOrder)
            {
                var recipe = original.Recipe with
                {
                    IncludeStroke = original.Recipe.IncludeStroke && part is SceneColorPart.BaseStroke or SceneColorPart.RubyStroke,
                    IncludeShadow = original.Recipe.IncludeShadow && part is SceneColorPart.BaseShadow or SceneColorPart.RubyShadow,
                    IncludeGlow = part is SceneColorPart.BaseGlow or SceneColorPart.RubyGlow,
                };
                DrawChangedFamilies(canvas, request, prepared, scene, shaped, options, ruby, lineStyle, composition, origin, baseline,
                    antialias, transforms, styles, original with { Part = part, Recipe = recipe }, warpedFamilies, resources);
            }
            return;
        }
        int offset = 0;
        TextSpan[] spans = [.. line.Tokens.Select(token => { var span = new TextSpan(offset, token.Text.Length); offset = span.End; return span; })];
        var partitions = GlyphPartitioner.Partition(shaped, spans);
        HashSet<int> excludedUnits = [];
        HashSet<int> changedTokens = [];
        HashSet<int> rubyOnlyTokens = [];
        for (int i = 0; i < line.Tokens.Length; i++)
        {
            if (capture?.ExcludedTokens?.Contains(line.Tokens[i].Id) == true)
            {
                if (!partitions[i].CanTransformIndependently)
                    throw new PreparedFrameException("UnsupportedGlyphPartition", "元素图不能从共享字簇中移除字形。");
                changedTokens.Add(i); excludedUnits.UnionWith(partitions[i].DrawUnitIndices); continue;
            }
            if (capture?.TokenId is { } selected && line.Tokens[i].Id != selected) continue;
            bool effect = capture?.TokenId == line.Tokens[i].Id || byToken.ContainsKey(line.Tokens[i].Id) || byStyle.ContainsKey(line.Tokens[i].Id)
                || HasTextPartitions(prepared, scene.BindingId!, line.Tokens[i].Id)
                || warpedFamilies?.ContainsKey(line.Tokens[i].Id) == true;
            if (!partitions[i].CanTransformIndependently && line.Tokens[i].StyleOverride is { } overlay
                && (ResolveElementStyle(lineStyle, overlay).Glow != lineStyle.Glow
                    || lineStyle.Glow is { IsEnabled: true } && ResolveElementStyle(lineStyle, overlay).Opacity != lineStyle.Opacity))
                throw new PreparedFrameException("UnsupportedGlyphPartition", "共享字簇不能独立应用元素外发光，不能将邻字纳入辉光蒙版。");
            bool source = TokenStyleSupport.HasDrawableOverride(line.Tokens[i].StyleOverride) && partitions[i].CanTransformIndependently;
            if (!effect && !source) continue;
            if (!byToken.ContainsKey(line.Tokens[i].Id) && !byStyle.ContainsKey(line.Tokens[i].Id)
                && capture?.TokenId is null
                && prepared?.BaseStyles.Any(item => item.Target.BindingId.Value == scene.BindingId
                    && item.Target.TokenId.Value == line.Tokens[i].Id) != true
                && (HasOnlyRubyWarp(warpedFamilies?.GetValueOrDefault(line.Tokens[i].Id))
                    || warpedFamilies?.ContainsKey(line.Tokens[i].Id) != true
                        && HasOnlyRubyPartitions(prepared, scene.BindingId!, line.Tokens[i].Id)))
            {
                changedTokens.Add(i); rubyOnlyTokens.Add(i); continue;
            }
            if (!partitions[i].CanTransformIndependently)
                throw new PreparedFrameException("UnsupportedGlyphPartition", "实际绘制的字簇不能独立变形。");
            changedTokens.Add(i); excludedUnits.UnionWith(partitions[i].DrawUnitIndices);
        }
        var remaining = Enumerable.Range(0, shaped.DrawUnits.Length).Where(index => !excludedUnits.Contains(index)).ToImmutableArray();
        HashSet<int> changedBaseTokens = [.. changedTokens.Except(rubyOnlyTokens)];
        // 未受影响的文字仍走同一两态样式规则，但不再包含被移走的 glyph。
        if (capture?.TokenId is null) using (SKPath sungRegions = BuildSungRegions(sample, shaped, origin, canvas.LocalClipBounds))
        {
            DrawLineGlow(canvas, shaped, options, origin, baseline, lineStyle, antialias, remaining, capture);
            bool hasSung = !sungRegions.IsEmpty;
            DrawRemaining(false);
            if (hasSung) DrawRemaining(true);
            void DrawRemaining(bool sung)
            {
                canvas.Save();
                if (hasSung) ClipSungRegions(canvas, sungRegions, sung);
                canvas.Save();
                foreach (TokenSample token in sample.Tokens)
                {
                    if (changedBaseTokens.Contains(token.Index) || ElementOverlay(line, token) is not { } overlay) continue;
                    var box = ElementBox(origin, shaped, token, canvas.LocalClipBounds, CapturePadding(lineStyle, overlay, capture));
                    if (box.Right > box.Left) canvas.ClipRect(box, SKClipOperation.Difference);
                }
                DrawStyledLine(canvas, shaped, options, origin, baseline,
                    sung ? lineStyle.SungColor : lineStyle.UnsungColor,
                    sung ? lineStyle.SungFill.Gradient : lineStyle.UnsungFill.Gradient,
                    lineStyle, composition, antialias, remaining, capture, sung);
                canvas.Restore();
                DrawTokenOverrides(canvas, shaped, options, sample, line, origin, baseline, antialias, lineStyle, composition, sung, remaining, changedBaseTokens, capture);
                canvas.Restore();
            }
        }
        if (capture?.TokenId is null) DrawRuby(canvas, ruby.Where(entry => !changedTokens.Contains(entry.TokenIndex)).ToArray(), sample,
            line, lineStyle, composition, origin, baseline, antialias, capture: capture);
        // 每个变化族只画自己的固定单元和注音；已有行／父组矩阵仍只乘一次。
        foreach (int index in changedTokens.Order())
        {
            var id = line.Tokens[index].Id;
            if (capture?.ExcludedTokens?.Contains(id) == true) continue;
            var transform = byToken.GetValueOrDefault(id);
            var styleOutput = byStyle.GetValueOrDefault(id);
            var warped = warpedFamilies?.GetValueOrDefault(id);
            var target = transform?.Target ?? styleOutput?.Target;
            if (target is not null && !prepared!.Scene.TokenGeometries.Single(item => item.WithRuby == target).BaseGeometry.CanTransformIndependently)
                throw new PreparedFrameException("UnsupportedGlyphPartition", "准备帧禁止独立绘制共享字簇。");
            canvas.Save();
            var matrix = transform?.LocalMatrix ?? Fx.Affine2.Identity;
            if (warped is null)
                canvas.Concat(new SKMatrix((float)matrix.A, (float)matrix.C, (float)matrix.Tx,
                    (float)matrix.B, (float)matrix.D, (float)matrix.Ty, 0, 0, 1));
            var token = sample.Tokens.Single(item => item.Index == index);
            var tokenSample = sample with { Tokens = [token] };
            var style = styleOutput is not null ? prepared!.GetTokenDrawingStyle(styleOutput.Target)
                : ElementOverlay(line, token) is { } overlay ? ResolveElementStyle(lineStyle, overlay) : lineStyle;
            if (warped is not null)
            {
                DrawWarpedPartitions(canvas, request, scene, shaped, options, ruby, tokenSample, index,
                    style, lineStyle, composition, origin, baseline, antialias, capture, warped, resources, rubyOnlyTokens.Contains(index));
                canvas.Restore(); continue;
            }
            if (DrawTextPartitions(canvas, prepared, scene, shaped, options, ruby, tokenSample, index,
                style, lineStyle, composition, origin, baseline, antialias, partitions[index].DrawUnitIndices, capture,
                rubyOnlyTokens.Contains(index)))
            {
                canvas.Restore(); continue;
            }
            DrawLineGlow(canvas, shaped, options, origin, baseline, style, antialias, partitions[index].DrawUnitIndices, capture);
            using (SKPath regions = BuildSungRegions(tokenSample, shaped, origin, canvas.LocalClipBounds))
            {
                bool hasSung = !regions.IsEmpty;
                DrawPart(false);
                if (hasSung) DrawPart(true);
                void DrawPart(bool sung)
                {
                    canvas.Save();
                    if (hasSung) ClipSungRegions(canvas, regions, sung);
                    DrawStyledLine(canvas, shaped, options, origin, baseline,
                        sung ? style.SungColor : style.UnsungColor,
                        sung ? style.SungFill.Gradient : style.UnsungFill.Gradient,
                        style, composition, antialias, partitions[index].DrawUnitIndices, capture, sung);
                    canvas.Restore();
                }
            }
            DrawRuby(canvas, ruby.Where(entry => entry.TokenIndex == index).ToArray(), sample,
                line, lineStyle, composition, origin, baseline, antialias, style, capture);
            canvas.Restore();
        }
    }
}
