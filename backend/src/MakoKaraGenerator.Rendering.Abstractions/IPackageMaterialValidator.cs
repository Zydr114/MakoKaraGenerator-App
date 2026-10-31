using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Rendering;

/// <summary>正式材质后端的完整回退门禁；不能在透明输出时才发现不支持的基础外观。</summary>
public interface IPackageMaterialValidator
{
    /// <summary>检查全部材质，包括尚未引用的导出；取消保留取消语义。</summary>
    void Validate(ImmutableArray<EffectMaterialDefinition> materials, CancellationToken cancellationToken = default);
}
