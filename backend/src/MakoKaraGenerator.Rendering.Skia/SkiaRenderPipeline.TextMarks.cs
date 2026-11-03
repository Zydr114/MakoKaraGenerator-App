using MakoKaraGenerator.Core.Projects;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class SkiaRenderPipeline
{
    private SceneFrame MeasureTextMarks(ProjectSnapshot snapshot, SceneFrame frame,
        out IReadOnlyDictionary<string, SceneTreeGeometry> geometry,
        out Dictionary<string, SceneTreeGeometry> references)
    {
        var tree = snapshot.LyricPresentation.WithDefaultPrograms(snapshot.Lyrics);
        references = SceneAttachmentReferences.Resolve(snapshot, frame);
        geometry = SceneTreeGeometryResolver.Resolve(tree.Nodes, snapshot.Composition.CanvasWidth, snapshot.Composition.CanvasHeight, references);
        List<SceneTextMarkSample> marks = [];
        foreach (var cue in frame.LeadIns)
        {
            var group = tree.FindNode(cue.ContainerId)!;
            var sequence = (CountdownSequenceContent)group.Content!;
            var target = frame.Lines.Single(line => line.ElementId == cue.TargetElementId && line.BindingId == cue.TargetBindingId);
            double opacity = group.Attachment?.Mode == SceneAttachmentMode.CueTargetText ? target.Transform.Opacity : 1;
            for (int i = 0; i < sequence.MarkNodeIds.Length; i++)
            {
                var node = tree.FindNode(sequence.MarkNodeIds[i])!;
                var text = (TextMarkContent)node.Content!;
                var appearance = snapshot.PresetApplications.Items.IsEmpty ? sequence.Appearance.Resolve(text.AppearanceOverride)
                    : SceneTextAppearance.FromStyle(PresetSceneStyleProjection.Mark(snapshot, node.Id, frame).Style);
                var style = appearance.ToLyricStyle();
                var shaped = GetLayout(text.Text, LayoutOptionsFor(style));
                var nodeGeometry = geometry[node.Id];
                // 节点位置及旋转已在其参考矩形矩阵中，文字在固定矩形中心整形。
                var ink = shaped.InkBounds;
                double left = ink?.Left ?? 0, right = ink?.Right ?? shaped.Width;
                double top = ink?.Top ?? -style.FontSizePx * .85, bottom = ink?.Bottom ?? style.FontSizePx * .15;
                double origin = nodeGeometry.Width / 2 - (left + right) / 2;
                double baseline = nodeGeometry.Height / 2 - (top + bottom) / 2;
                var measured = new LineRenderGeometry { LineId = string.Empty, ElementId = node.Id, ContainerId = group.Id,
                    BindingId = cue.TargetBindingId, Placement = node.Placement, OriginX = origin, BaselineY = baseline,
                    WorldMatrix = nodeGeometry.WorldMatrix, Pivot = nodeGeometry.Pivot,
                    TopLeft = new(origin + left, baseline + top), BottomRight = new(origin + right, baseline + bottom) };
                marks.Add(new() { NodeId = node.Id, ContainerId = group.Id, CueId = cue.CueId,
                    Text = text.Text, Appearance = appearance, IsElapsed = i < cue.ElapsedMarks, Opacity = opacity, Geometry = measured });
            }
        }
        return frame with { TextMarks = [.. marks] };
    }

    private void DrawTextMark(SKCanvas canvas, RenderRequest request, Composition composition, SceneTextMarkSample mark, CapturePaint? capture = null)
    {
        // 全部装饰统一应用一次节点透明度，避免阴影过滤器再次乘入填充透明度。
        MakoKaraGenerator.Scripting.TextMarkTarget target = new(new(mark.NodeId), new(mark.CueId), new(mark.Geometry.BindingId!));
        var prepared = request.PreparedFrame;
        var effectiveStyle = prepared?.MarkStyles.Any(item => item.Target == target) == true
            ? prepared.GetMarkDrawingStyle(target) : mark.Appearance.ToLyricStyle();
        var style = effectiveStyle with { Opacity = 1 };
        double opacity = capture?.IsCoverage == true || capture?.SuppressCommonOpacity == true ? 1 : mark.Opacity * effectiveStyle.Opacity;
        var options = LayoutOptionsFor(style);
        var shaped = GetLayout(mark.Text, options);
        var geometry = mark.Geometry;
        canvas.Save();
        bool layer = opacity < 1;
        using var opacityReservation = layer ? capture?.Layer("CaptureMarkOpacityLayer") : null;
        if (layer)
        {
            using SKPaint paint = new() { Color = SKColors.White.WithAlpha(ToOpacityByte((float)opacity)) };
            canvas.SaveLayer(paint);
        }
        var basis = geometry.WorldMatrix!.Value;
        var matrix = prepared?.FinalMarkGeometry.Single(item => item.Target == target).Geometry.BaseWorldMatrix
            ?? new MakoKaraGenerator.Scripting.Affine2(basis.A, basis.B, basis.C, basis.D, basis.Tx, basis.Ty);
        canvas.Concat(new SKMatrix((float)matrix.A, (float)matrix.C, (float)matrix.Tx,
            (float)matrix.B, (float)matrix.D, (float)matrix.Ty, 0, 0, 1));
        DrawLineGlow(canvas, shaped, options, (float)geometry.OriginX, (float)geometry.BaselineY,
            style, request.Quality != RenderQuality.Draft, capture: capture);
        var fill = mark.IsElapsed ? style.SungFill : style.UnsungFill;
        DrawStyledLine(canvas, shaped, options, (float)geometry.OriginX, (float)geometry.BaselineY,
            fill.Color, fill.Gradient, style, composition, request.Quality != RenderQuality.Draft, capture: capture, sungPass: mark.IsElapsed);
        RestoreAnimationLayer(canvas, layer);
        canvas.Restore();
    }
}
