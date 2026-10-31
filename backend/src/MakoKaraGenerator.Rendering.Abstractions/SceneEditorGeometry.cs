using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Rendering;

/// <summary>从共用测量帧提取任意节点与单句微调的编辑几何。</summary>
public static class SceneEditorGeometry
{
    /// <summary>提取指定节点；优先包围当前装载内容，空节点使用固定参考矩形。</summary>
    public static LineRenderGeometry? ForNode(ProjectSnapshot snapshot, SceneFrame frame, string nodeId)
    {
        var tree = snapshot.LyricPresentation.WithDefaultPrograms(snapshot.Lyrics);
        if (tree.FindNode(nodeId) is not { } node) return null;
        var groups = SceneTreeGeometryResolver.Resolve(tree.Nodes, snapshot.Composition.CanvasWidth, snapshot.Composition.CanvasHeight);
        var geometry = groups[nodeId];
        var measured = frame.Elements.FirstOrDefault(element => element.Id == nodeId);
        var worldMatrix = measured?.WorldMatrix ?? geometry.WorldMatrix;
        var pivot = measured?.Pivot ?? geometry.Pivot;
        var points = measured?.WorldCorners.Select(worldMatrix.Unmap).ToArray() ?? [];
        var parent = measured?.PlacementReference ?? (node.ParentId is null
            ? new SceneTreeGeometry(snapshot.Composition.CanvasWidth, snapshot.Composition.CanvasHeight, SceneMatrix.Identity, new(0, 0))
            : groups[node.ParentId]);
        return new() { LineId = string.Empty, ElementId = nodeId, ContainerId = nodeId, Placement = node.Placement,
            WorldMatrix = worldMatrix, Pivot = pivot,
            TopLeft = points.Length == 0 ? new(0, 0) : new(points.Min(point => point.X), points.Min(point => point.Y)),
            BottomRight = points.Length == 0 ? new(geometry.Width, geometry.Height)
                : new(points.Max(point => point.X), points.Max(point => point.Y)),
            EditCoordinateTransform = parent.WorldMatrix, EditReferenceWidth = parent.Width, EditReferenceHeight = parent.Height };
    }

    /// <summary>提取当前句的偏移编辑数值，世界轮廓仍使用最终共用测量结果。</summary>
    public static LineRenderGeometry? ForLine(ProjectSnapshot snapshot, SceneFrame frame, string lineId)
    {
        if (frame.Lines.FirstOrDefault(line => line.Frame.Line?.Id == lineId) is not { Geometry: { } geometry } sample) return null;
        var local = snapshot.LyricPresentation.FindLine(lineId)?.LocalTransform ?? LyricLocalTransform.Neutral;
        return geometry with { Placement = ScenePlacement.Create(new(local.OffsetXRatio, local.OffsetYRatio),
                rotationDegrees: local.RotationDegrees, scale: local.Scale),
            EditCoordinateTransform = sample.ParentMatrix, EditReferenceWidth = sample.ParentReferenceWidth,
            EditReferenceHeight = sample.ParentReferenceHeight };
    }
}
