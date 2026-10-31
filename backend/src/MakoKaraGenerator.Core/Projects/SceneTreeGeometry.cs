namespace MakoKaraGenerator.Core.Projects;

/// <summary>后端无关的容器固定参考几何。</summary>
/// <param name="Width">局部参考宽度。</param>
/// <param name="Height">局部参考高度。</param>
/// <param name="WorldMatrix">父子组合矩阵。</param>
/// <param name="Pivot">画布中的固定枢轴。</param>
public sealed record SceneTreeGeometry(double Width, double Height, SceneMatrix WorldMatrix, CanvasPoint Pivot);

/// <summary>编辑与渲染共用的纯容器几何，不依赖源文本测量。</summary>
public static class SceneTreeGeometryResolver
{
    /// <summary>递归计算全部节点；父引用与循环失效时显式失败。</summary>
    public static IReadOnlyDictionary<string, SceneTreeGeometry> Resolve(IEnumerable<SceneContainerDefinition> nodes, double canvasWidth, double canvasHeight,
        IReadOnlyDictionary<string, SceneTreeGeometry>? parentReferences = null)
    {
        var definitions = nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        Dictionary<string, SceneTreeGeometry> result = new(StringComparer.Ordinal);
        HashSet<string> visiting = new(StringComparer.Ordinal);
        SceneTreeGeometry Visit(string id)
        {
            if (result.TryGetValue(id, out var known)) return known;
            if (!visiting.Add(id)) throw new InvalidOperationException("容器父子关系存在循环：" + id);
            if (!definitions.TryGetValue(id, out var node)) throw new InvalidOperationException("父容器不存在：" + id);
            var parent = parentReferences?.TryGetValue(id, out var reference) == true ? reference
                : node.ParentId is null ? new SceneTreeGeometry(canvasWidth, canvasHeight, SceneMatrix.Identity, new(0, 0)) : Visit(node.ParentId);
            double width = parent.Width * node.ReferenceRect.WidthRatio, height = parent.Height * node.ReferenceRect.HeightRatio;
            var placement = node.Placement;
            var anchor = SceneMatrix.Anchor(placement.Anchor, width, height);
            var pivot = new CanvasPoint(placement.Position.X * parent.Width, placement.Position.Y * parent.Height);
            var matrix = parent.WorldMatrix * SceneMatrix.Translate(pivot.X, pivot.Y)
                * SceneMatrix.RotateScale(placement.RotationDegrees, placement.Scale) * SceneMatrix.Translate(-anchor.X, -anchor.Y);
            var geometry = new SceneTreeGeometry(width, height, matrix, parent.WorldMatrix.Map(pivot));
            visiting.Remove(id); result.Add(id, geometry); return geometry;
        }
        foreach (string id in definitions.Keys) Visit(id);
        return result;
    }
}
