using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering;

/// <summary>由已验证包元数据投影唯一 SDK 资源目录；本函数不替代源码及 native 资源门禁。</summary>
public static class EffectPackageResourceCatalog
{
    /// <summary>完整投影图、材质、图片与 SVG 固定档位，不按文件名猜测资源类型。</summary>
    public static ResourceCatalog Create(EffectPackageDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return new(definition.Contents.PackageId, definition.Contents.Revision,
            [.. definition.Materials.Select(material => material.Id)],
            [.. definition.Graphs.Select(graph => graph.Id)], definition.ImagePaths,
            [.. definition.SvgResources.ById.Values.Where(resource => resource.Mode == SvgResourceMode.Vector).Select(resource => resource.Id)],
            definition.SvgResources.ById.Values.Where(resource => resource.Mode == SvgResourceMode.Atlas)
                .ToImmutableDictionary(resource => resource.Id, resource => resource.Variants.Select(variant => variant.Id).ToImmutableArray(), StringComparer.Ordinal));
    }
}
