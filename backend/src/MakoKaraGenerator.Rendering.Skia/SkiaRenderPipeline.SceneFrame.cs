using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class SkiaRenderPipeline
{
    private SceneFrame MeasureFrameElements(ProjectSnapshot snapshot, SceneFrame frame)
    {
        frame = MeasureTextMarks(snapshot, frame, out var groups, out var references);
        List<SceneElementFrame> elements = [];
        var parents = frame.Definition.Elements.ToDictionary(node => node.Id, node => node.ParentId, StringComparer.Ordinal);
        foreach (var line in frame.Lines) parents[line.ElementId] = line.ParentId;
        foreach (var cue in frame.LeadIns)
            if (references.ContainsKey(cue.ContainerId)) parents[cue.ContainerId] = cue.TargetElementId;
        bool Inside(string? current, string id)
        {
            while (current is not null)
            { if (current == id) return true; current = parents[current]; }
            return false;
        }
        // 定义含全曲每行及注音；按（分支，行）一次建立在场行索引，取各行在帧中的第一个装载，
        // 不再对每个定义扫描、复制在场行（旧做法随全曲行数×在场行数增长）。
        Dictionary<(string? Branch, string? Line), SceneLineSample> present = [];
        foreach (var line in frame.Lines) present.TryAdd((line.Branch.Id, line.Frame.Line!.Id), line);
        foreach (var definition in frame.Definition.Elements)
        {
            if (definition.Kind == SceneElementKind.Group)
            {
                var container = groups[definition.Id];
                var bound = frame.Lines.Where(sample => Inside(sample.ParentId, definition.Id)).ToArray();
                var marks = frame.TextMarks.Where(mark => Inside(mark.NodeId, definition.Id)).ToArray();
                var reference = references.GetValueOrDefault(definition.Id)
                    ?? (definition.ParentId is { } parentId ? groups[parentId]
                        : new SceneTreeGeometry(snapshot.Composition.CanvasWidth, snapshot.Composition.CanvasHeight, SceneMatrix.Identity, new(0, 0)));
                elements.Add(new() { Id = definition.Id, ParentId = parents[definition.Id], SourceBinding = definition.SourceBinding,
                    Kind = definition.Kind, IsVisible = bound.Length > 0 || marks.Length > 0, Pivot = container.Pivot, WorldMatrix = container.WorldMatrix,
                    PlacementReference = reference,
                    WorldCorners = [.. bound.Where(sample => sample.Geometry is not null).SelectMany(sample => sample.Geometry!.Corners()),
                        .. marks.SelectMany(mark => mark.Geometry.Corners())] });
                continue;
            }
            if (definition.Content is TextMarkContent)
            {
                var mark = frame.TextMarks.FirstOrDefault(item => item.NodeId == definition.Id);
                if (mark is not null)
                    elements.Add(new() { Id = definition.Id, ParentId = definition.ParentId, SourceBinding = definition.SourceBinding,
                        Kind = SceneElementKind.Text, BindingId = mark.Geometry.BindingId, WorldMatrix = mark.Geometry.WorldMatrix!.Value,
                        Pivot = mark.Geometry.Pivot, IsVisible = true, Opacity = mark.Opacity,
                        PlacementReference = groups[mark.ContainerId], WorldCorners = [.. mark.Geometry.Corners()] });
                continue;
            }
            var sample = present.GetValueOrDefault((definition.SourceBinding.BranchId, definition.SourceBinding.LineId));
            if (sample?.Geometry is not { } geometry) continue;
            var corners = geometry.Corners();
            if (definition.SourceBinding.RubyId is { } rubyId)
            {
                var line = sample.Frame.Line!;
                var style = LyricStyleResolver.Resolve(snapshot.LyricStyle, lineOverride: line.StyleOverride, branchOverride: sample.Branch.StyleOverride).Style;
                var annotation = line.RubyAnnotations.First(item => item.Id == rubyId);
                var ruby = GetLineLayout(line, style).Ruby.FirstOrDefault(item => item.Id == annotation.Id);
                if (ruby is null) continue;
                corners = new[] { new CanvasPoint(geometry.OriginX + ruby.Ink.Left, geometry.BaselineY + ruby.Ink.Top),
                    new(geometry.OriginX + ruby.Ink.Right, geometry.BaselineY + ruby.Ink.Top),
                    new(geometry.OriginX + ruby.Ink.Right, geometry.BaselineY + ruby.Ink.Bottom),
                    new(geometry.OriginX + ruby.Ink.Left, geometry.BaselineY + ruby.Ink.Bottom) }.Select(geometry.Transform).ToArray();
            }
            elements.Add(new() { Id = definition.Id, ParentId = definition.LocalLayout == SceneLocalLayout.Ruby ? sample.ElementId : sample.ParentId,
                SourceBinding = definition.SourceBinding, Kind = definition.Kind, BindingId = sample.BindingId,
                WorldMatrix = geometry.WorldMatrix!.Value, Pivot = geometry.Pivot,
                IsVisible = true, Opacity = sample.Transform.Opacity, WorldCorners = [.. corners] });
        }
        return frame with { Elements = [.. elements], GroupGeometries = [.. elements.Where(static node => node.Kind == SceneElementKind.Group).Select(node =>
        {
            var reference = groups[node.Id]; var matrix = node.WorldMatrix;
            return new MakoKaraGenerator.Scripting.GroupGeometrySample { Target = new(new(node.Id)),
                ReferenceBounds = new(0, 0, reference.Width, reference.Height),
                BaseWorldMatrix = new(matrix.A, matrix.B, matrix.C, matrix.D, matrix.Tx, matrix.Ty),
                WorldPivot = new(node.Pivot.X, node.Pivot.Y) };
        })] };
    }
}
