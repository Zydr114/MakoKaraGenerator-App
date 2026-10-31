using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Rendering;

/// <summary>固定容器参考尺寸及世界变换，不依赖歌词测量。</summary>
/// <param name="Width">局部参考宽度。</param>
/// <param name="Height">局部参考高度。</param>
/// <param name="WorldMatrix">完整父子变换。</param>
/// <param name="Pivot">世界枢轴。</param>
public sealed record SceneContainerGeometry(double Width, double Height, SceneMatrix WorldMatrix, CanvasPoint Pivot);

/// <summary>将运行定义投影到 Core 的统一父子几何。</summary>
public static class SceneContainerGeometryResolver
{
    /// <summary>计算全部容器，编辑与渲染使用相同的纯矩阵规则。</summary>
    public static IReadOnlyDictionary<string, SceneContainerGeometry> Resolve(SceneDefinition definition, Composition composition)
        => SceneTreeGeometryResolver.Resolve(definition.Elements.Where(node => node.Kind == SceneElementKind.Group || node.Content is TextMarkContent)
            .Select(node => new SceneContainerDefinition { Id = node.Id, ParentId = node.ParentId,
                Placement = node.Placement!, ReferenceRect = node.ReferenceRect }), composition.CanvasWidth, composition.CanvasHeight)
            .ToDictionary(pair => pair.Key, pair => new SceneContainerGeometry(pair.Value.Width, pair.Value.Height,
                pair.Value.WorldMatrix, pair.Value.Pivot), StringComparer.Ordinal);
}
