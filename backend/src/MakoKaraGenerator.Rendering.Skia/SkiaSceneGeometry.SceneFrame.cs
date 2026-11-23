using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class SkiaSceneGeometry
{
    private SceneFrame MeasureFrameElements(ProjectSnapshot snapshot, SceneFrame frame)
    {
        frame = MeasureTextMarks(snapshot, frame, out var groups, out var references);
        List<SceneElementFrame> elements = [];
        // 定义层父关系按不可变定义缓存（含全曲每行），本帧装载与提示附着放在小覆盖层，不再逐帧复制整表。
        var definitionParents = SceneDefinitionBuilder.ElementParents(frame.Definition);
        Dictionary<string, string?> runtimeParents = new(StringComparer.Ordinal);
        foreach (var line in frame.Lines) runtimeParents[line.ElementId] = line.ParentId;
        foreach (var cue in frame.LeadIns)
            if (references.ContainsKey(cue.ContainerId)) runtimeParents[cue.ContainerId] = cue.TargetElementId;
        string? Parent(string id) => runtimeParents.TryGetValue(id, out var parent) ? parent : definitionParents[id];
        bool Inside(string? current, string id)
        {
            while (current is not null)
            { if (current == id) return true; current = Parent(current); }
            return false;
        }
        // 定义含全曲每行及注音；按（分支，行）一次建立在场行索引，取各行在帧中的第一个装载，
        // 不再对每个定义扫描、复制在场行（旧做法随全曲行数×在场行数增长）。
        Dictionary<(string? Branch, string? Line), SceneLineSample> present = [];
        foreach (var line in frame.Lines) present.TryAdd((line.Branch.Id, line.Frame.Line!.Id), line);
        // 定义遍历覆盖全曲每行；循环体本身不含捕获循环变量的委托，组、标记与注音分支在各自函数内处理，
        // 普通文字元素（多数不在场）不再逐个分配闭包。
        SceneElementFrame Group(SceneElementDefinition definition)
        {
            var container = groups[definition.Id];
            var bound = frame.Lines.Where(sample => Inside(sample.ParentId, definition.Id)).ToArray();
            var marks = frame.TextMarks.Where(mark => Inside(mark.NodeId, definition.Id)).ToArray();
            var reference = references.GetValueOrDefault(definition.Id)
                ?? (definition.ParentId is { } parentId ? groups[parentId]
                    : new SceneTreeGeometry(snapshot.Composition.CanvasWidth, snapshot.Composition.CanvasHeight, SceneMatrix.Identity, new(0, 0)));
            return new() { Id = definition.Id, ParentId = Parent(definition.Id), SourceBinding = definition.SourceBinding,
                Kind = definition.Kind, IsVisible = bound.Length > 0 || marks.Length > 0, Pivot = container.Pivot, WorldMatrix = container.WorldMatrix,
                PlacementReference = reference,
                WorldCorners = [.. bound.Where(sample => sample.Geometry is not null).SelectMany(sample => sample.Geometry!.Corners()),
                    .. marks.SelectMany(mark => mark.Geometry.Corners())] };
        }
        SceneElementFrame? Mark(SceneElementDefinition definition)
        {
            var mark = frame.TextMarks.FirstOrDefault(item => item.NodeId == definition.Id);
            return mark is null ? null : new() { Id = definition.Id, ParentId = definition.ParentId, SourceBinding = definition.SourceBinding,
                Kind = SceneElementKind.Text, BindingId = mark.Geometry.BindingId, WorldMatrix = mark.Geometry.WorldMatrix!.Value,
                Pivot = mark.Geometry.Pivot, IsVisible = true, Opacity = mark.Opacity,
                PlacementReference = groups[mark.ContainerId], WorldCorners = [.. mark.Geometry.Corners()] };
        }
        foreach (var definition in frame.Definition.Elements)
        {
            if (definition.Kind == SceneElementKind.Group) { elements.Add(Group(definition)); continue; }
            if (definition.Content is TextMarkContent)
            {
                if (Mark(definition) is { } mark) elements.Add(mark);
                continue;
            }
            var sample = present.GetValueOrDefault((definition.SourceBinding.BranchId, definition.SourceBinding.LineId));
            if (sample?.Geometry is not { } geometry) continue;
            var corners = definition.SourceBinding.RubyId is { } rubyId ? RubyCorners(snapshot, sample, geometry, rubyId) : geometry.Corners();
            if (corners is null) continue;
            elements.Add(new() { Id = definition.Id, ParentId = definition.LocalLayout == SceneLocalLayout.Ruby ? sample.ElementId : sample.ParentId,
                SourceBinding = definition.SourceBinding, Kind = definition.Kind, BindingId = sample.BindingId,
                WorldMatrix = geometry.WorldMatrix!.Value, Pivot = geometry.Pivot,
                IsVisible = true, Opacity = 1, WorldCorners = [.. corners] });
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

    // 注音元素的世界角点；注音布局缺失时返回空（该元素本帧不输出）。
    private CanvasPoint[]? RubyCorners(ProjectSnapshot snapshot, SceneLineSample sample, LineRenderGeometry geometry, string rubyId)
    {
        var line = sample.Frame.Line!;
        var style = LineBaseStyle(snapshot.LyricStyle, line, sample.Branch);
        var annotation = line.RubyAnnotations.First(item => item.Id == rubyId);
        var ruby = GetLineLayout(line, style).Ruby.FirstOrDefault(item => item.Id == annotation.Id);
        if (ruby is null) return null;
        return new[] { new CanvasPoint(geometry.OriginX + ruby.Ink.Left, geometry.BaselineY + ruby.Ink.Top),
            new(geometry.OriginX + ruby.Ink.Right, geometry.BaselineY + ruby.Ink.Top),
            new(geometry.OriginX + ruby.Ink.Right, geometry.BaselineY + ruby.Ink.Bottom),
            new(geometry.OriginX + ruby.Ink.Left, geometry.BaselineY + ruby.Ink.Bottom) }.Select(geometry.Transform).ToArray();
    }
}
