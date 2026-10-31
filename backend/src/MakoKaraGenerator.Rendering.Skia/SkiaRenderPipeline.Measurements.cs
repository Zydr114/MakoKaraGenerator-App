using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class SkiaRenderPipeline
{
    /// <summary>同一几何后端的测量采样接缝；效果会话可使用布局代表或固定编辑行。</summary>
    public SceneFrame GetMeasurementSceneFrame(ProjectSnapshot snapshot, long timeUs, bool layoutReference,
        string? editingLineId = null, string? editingCueId = null)
        => MeasureSceneFrame(layoutReference ? LayoutReferenceScene.CreateSnapshot(snapshot) : snapshot,
            timeUs, editingLineId, layoutReference, editingCueId);

    /// <summary>一次真实整形测量，并将同一 SceneFrame 直接交给绘制，不重采样。</summary>
    public MeasuredRenderFrame MeasureAndRender(RenderRequest request, bool layoutReference, string? editingLineId = null, string? editingCueId = null)
    {
        lock (_device.SynchronizationRoot)
            return MeasureAndRenderCore(request, layoutReference, editingLineId, editingCueId);
    }

    private MeasuredRenderFrame MeasureAndRenderCore(RenderRequest request, bool layoutReference, string? editingLineId, string? editingCueId)
    {
        if (request.PreparedFrame is { } prepared)
        {
            var actual = prepared.HierarchyScene with { TokenGeometries = prepared.FinalGeometry,
                LineGeometries = prepared.FinalLineGeometry, MarkGeometries = prepared.FinalMarkGeometry,
                GroupGeometries = prepared.FinalGroupGeometry };
            var groups = prepared.FinalGroupGeometry.ToDictionary(item => item.Target.NodeId.Value, StringComparer.Ordinal);
            var marks = prepared.FinalMarkGeometry.ToDictionary(item => item.Target.NodeId.Value, StringComparer.Ordinal);
            var byBinding = actual.Lines.ToDictionary(item => item.BindingId!, StringComparer.Ordinal);
            actual = actual with { Elements = [.. actual.Elements.Select(element =>
            {
                if (groups.TryGetValue(element.Id, out var group))
                    return element with { WorldCorners = [.. group.WorldInkCorners.Select(point => new CanvasPoint(point.X, point.Y))] };
                if (marks.TryGetValue(element.Id, out var mark))
                    return element with { WorldCorners = [.. mark.Geometry.WorldInkCorners.Select(point => new CanvasPoint(point.X, point.Y))] };
                if (element.BindingId is not { } binding || !byBinding.TryGetValue(binding, out var line)) return element;
                var tokens = prepared.FinalGeometry.Where(token => token.WithRuby.BindingId.Value == binding).ToArray();
                if (tokens.Length == 0) return element;
                var corners = element.SourceBinding.RubyId is { } rubyId
                    ? tokens.SelectMany(token => token.Ruby).Where(ruby => ruby.RubyId.Value == rubyId).SelectMany(ruby => ruby.Geometry.WorldInkCorners)
                    : LineInk();
                return element with { WorldCorners = [.. corners.Select(point => new CanvasPoint(point.X, point.Y))] };

                IEnumerable<MakoKaraGenerator.Scripting.Point2> LineInk()
                {
                    var fullLine = prepared.FinalLineGeometry.Single(item => item.Target.BindingId.Value == binding);
                    var units = tokens.SelectMany(token => token.BaseGeometry.DrawUnits).ToHashSet();
                    // Match GroupInkProjection: a partial token partition cannot
                    // replace the complete fixed line's unpartitioned glyphs.
                    return fullLine.BaseGeometry.DrawUnits.All(units.Contains)
                        ? tokens.SelectMany(token => token.FamilyGeometry.WorldInkCorners)
                        : fullLine.BaseGeometry.WorldInkCorners.Concat(tokens.SelectMany(token => token.Ruby)
                            .SelectMany(ruby => ruby.Geometry.WorldInkCorners));
                }
            })] };
            var actualReferences = SceneAttachmentReferences.Resolve(request.Project, actual);
            var actualNodes = SceneTreeGeometryResolver.Resolve(request.Project.LyricPresentation.WithDefaultPrograms(request.Project.Lyrics).Nodes,
                request.Project.Composition.CanvasWidth, request.Project.Composition.CanvasHeight, actualReferences).ToDictionary();
            foreach (var element in actual.Elements)
                if (actualNodes.TryGetValue(element.Id, out var node)) actualNodes[element.Id] = node with { WorldMatrix = element.WorldMatrix, Pivot = element.Pivot };
            return new(RenderCore(request, actual), actual, actualNodes, actualReferences);
        }
        if (request.Project.EffectInputsFingerprint is not null)
            throw new PreparedFrameException("SceneMeasurementUnsupported", "效果测量需要同一 Runtime 准备帧。");
        var snapshot = layoutReference ? LayoutReferenceScene.CreateSnapshot(request.Project) : request.Project;
        var scene = MeasureSceneFrame(snapshot, request.TimeUs, editingLineId, layoutReference, editingCueId);
        var references = SceneAttachmentReferences.Resolve(snapshot, scene);
        var nodes = SceneTreeGeometryResolver.Resolve(snapshot.LyricPresentation.WithDefaultPrograms(snapshot.Lyrics).Nodes,
            snapshot.Composition.CanvasWidth, snapshot.Composition.CanvasHeight, references);
        return new(RenderCore(request with { Project = snapshot }, scene), scene, nodes, references);
    }
}
