using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Rendering;

/// <summary>正式图后端的包门禁；不代表导出许可或工程绑定已经实现。</summary>
public interface IPackageImageGraphValidator
{
    /// <summary>验证包中全部固定图，失败不能忽略图而只启用控制器。</summary>
    void Validate(ImmutableDictionary<string, EffectPackageImageGraph> graphs, CancellationToken cancellationToken = default);
}
