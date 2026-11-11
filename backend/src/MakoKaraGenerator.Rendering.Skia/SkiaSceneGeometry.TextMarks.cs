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
}
