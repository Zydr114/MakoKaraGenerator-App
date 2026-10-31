using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Rendering;

/// <summary>节点自有文字的最终帧状态，几何与绘制共用同一结果。</summary>
public sealed record SceneTextMarkSample
{
    /// <summary>稳定标记节点。</summary>
    public required string NodeId { get; init; }
    /// <summary>所属提示容器。</summary>
    public required string ContainerId { get; init; }
    /// <summary>本次前奏条目。</summary>
    public required string CueId { get; init; }
    /// <summary>最终整形文字。</summary>
    public required string Text { get; init; }
    /// <summary>解析后的规范两态外观。</summary>
    public required SceneTextAppearance Appearance { get; init; }
    /// <summary>该标记对应的步骤是否已经过。</summary>
    public bool IsElapsed { get; init; }
    /// <summary>附着继承的动画透明度；外观透明度在绘制链上只乘一次。</summary>
    public double Opacity { get; init; } = 1;
    /// <summary>已测量的最终文字几何。</summary>
    public required LineRenderGeometry Geometry { get; init; }
}
