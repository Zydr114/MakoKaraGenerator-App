using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class SkiaSceneGeometry
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
            double opacity = cue.BlinkOpacity;
            int count = sequence.MarkNodeIds.Length;
            for (int i = 0; i < count; i++)
            {
                var node = tree.FindNode(sequence.MarkNodeIds[i])!;
                var text = (TextMarkContent)node.Content!;
                var appearance = snapshot.PresetApplications.Items.IsEmpty ? sequence.Appearance.Resolve(text.AppearanceOverride)
                    : SceneTextAppearance.FromStyle(PresetSceneStyleProjection.Mark(snapshot, node.Id, frame).Style);
                var style = appearance.ToLyricStyle();
                var nodeGeometry = geometry[node.Id];
                var (isElapsed, wipeProgress) = LeadInAnimationEvaluator.EvaluateMark(cue.Range, i, count, sequence.Animation, frame.TimeUs);
                LineRenderGeometry measured;
                if (sequence.Shape.Kind == CountdownShapeKind.Text)
                {
                    var shaped = GetLayout(text.Text, LayoutOptionsFor(style));
                    // 节点位置及旋转已在其参考矩形矩阵中，文字在固定矩形中心整形。
                    var ink = shaped.InkBounds;
                    double left = ink?.Left ?? 0, right = ink?.Right ?? shaped.Width;
                    double top = ink?.Top ?? -style.FontSizePx * .85, bottom = ink?.Bottom ?? style.FontSizePx * .15;
                    double origin = nodeGeometry.Width / 2 - (left + right) / 2;
                    double baseline = nodeGeometry.Height / 2 - (top + bottom) / 2;
                    measured = new LineRenderGeometry
                    {
                        LineId = string.Empty, ElementId = node.Id, ContainerId = group.Id,
                        BindingId = cue.TargetBindingId, Placement = node.Placement, OriginX = origin, BaselineY = baseline,
                        WorldMatrix = nodeGeometry.WorldMatrix, Pivot = nodeGeometry.Pivot,
                        TopLeft = new(origin + left, baseline + top), BottomRight = new(origin + right, baseline + bottom),
                    };
                }
                else
                {
                    var (bx0, by0, bx1, by1) = MeasureShapeBox(sequence.Shape, i, count, nodeGeometry.Width, nodeGeometry.Height);
                    measured = new LineRenderGeometry
                    {
                        LineId = string.Empty, ElementId = node.Id, ContainerId = group.Id,
                        BindingId = cue.TargetBindingId, Placement = node.Placement,
                        OriginX = (bx0 + bx1) / 2, BaselineY = (by0 + by1) / 2,
                        WorldMatrix = nodeGeometry.WorldMatrix, Pivot = nodeGeometry.Pivot,
                        TopLeft = new(bx0, by0), BottomRight = new(bx1, by1),
                    };
                }
                marks.Add(new()
                {
                    NodeId = node.Id, ContainerId = group.Id, CueId = cue.CueId,
                    Text = text.Text, Appearance = appearance,
                    IsElapsed = isElapsed, MarkIndex = i, MarkCount = count,
                    Shape = sequence.Shape, Animation = sequence.Animation, WipeProgress = wipeProgress,
                    Opacity = opacity, Geometry = measured,
                });
            }
        }
        return frame with { TextMarks = [.. marks] };
    }

    private static (double Left, double Top, double Right, double Bottom) MeasureShapeBox(CountdownShapeSpec shape, int index, int count,
        double cellWidth, double cellHeight)
    {
        double w = Math.Max(4, cellWidth);
        double h = Math.Max(4, cellHeight);
        double side = Math.Min(w, h) * 0.84d;
        double cx = w / 2d;
        double cy = h / 2d;
        double bw, bh;
        switch (shape.Kind)
        {
            case CountdownShapeKind.SteppedBars:
                double t = count <= 1 ? 0d : (double)index / (count - 1);
                double heightRatio = 1d - t * (1d - Math.Clamp(shape.BarMinHeightRatio, 0.15d, 1d));
                bw = Math.Min(w * 0.62d, side * 0.56d);
                bh = Math.Max(4d, h * 0.86d * heightRatio);
                break;
            case CountdownShapeKind.Capsule:
                bw = w * 0.86d;
                bh = Math.Min(bw * 0.52d, h * 0.52d);
                break;
            default:
                bw = side;
                bh = side;
                break;
        }
        return (cx - bw / 2d, cy - bh / 2d, cx + bw / 2d, cy + bh / 2d);
    }
}
