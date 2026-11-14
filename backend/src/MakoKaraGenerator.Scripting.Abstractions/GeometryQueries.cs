using System.Collections.Immutable;

namespace MakoKaraGenerator.Scripting;

/// <summary>只读基本场景几何；不包含作者效果、背景、粒子或未来历史。</summary>
public sealed record BaseSceneGeometry
{
    /// <summary>真实采样时刻。</summary>
    public required long TimeUs { get; init; }
    /// <summary>筛选后的真实装载。</summary>
    public ImmutableArray<FrameLine> Lines { get; init; } = [];
    /// <summary>同一后端的基础元素族。</summary>
    public ImmutableArray<TokenGeometrySample> Tokens { get; init; } = [];
    /// <summary>同一后端的完整行。</summary>
    public ImmutableArray<LineGeometrySample> LineGeometry { get; init; } = [];
    /// <summary>源筛选匹配的真实提示标记。</summary>
    public ImmutableArray<FrameMark> Marks { get; init; } = [];
    /// <summary>匹配显示对象的真实祖先组；Project 保留完整节点目录。</summary>
    public ImmutableArray<FrameGroup> Groups { get; init; } = [];
    /// <summary>真实提示标记固定字形。</summary>
    public ImmutableArray<TextMarkGeometrySample> MarkGeometry { get; init; } = [];
    /// <summary>真实祖先组参考框及运行矩阵。</summary>
    public ImmutableArray<GroupGeometrySample> GroupGeometry { get; init; } = [];
}
