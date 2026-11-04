using System.Collections.Immutable;
using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Scripting;

public sealed partial record TokenGeometrySample
{
    /// <summary>完整整行整形产出的基础字和 Ruby 字簇；跨 token 单元保留相同身份与不可独立原因。</summary>
    public ImmutableArray<TextClusterGeometrySample> Clusters { get; init; } = [];
}

public sealed partial record FrameOutput
{
    /// <summary>全部已验证网格写入，保留调用顺序及被后写覆盖的值，供全实例计账与归属。</summary>
    public ImmutableArray<TextWrite.Warp> TextWarps { get; init; } = [];
    /// <summary>只影响基础字、不影响其 Ruby 的组合仿射。</summary>
    public ImmutableArray<BaseTokenTransform> BaseTransforms { get; init; } = [];
    /// <summary>只影响基础字、不影响其 Ruby 的合成外观。</summary>
    public ImmutableArray<BaseTokenStyle> BaseStyles { get; init; } = [];
    /// <summary>真实独立整形单元的组合仿射；跨 token 单元不得独立输出。</summary>
    public ImmutableArray<TextClusterTransform> ClusterTransforms { get; init; } = [];
}
