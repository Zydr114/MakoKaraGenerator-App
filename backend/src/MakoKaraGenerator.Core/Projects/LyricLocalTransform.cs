namespace MakoKaraGenerator.Core.Projects;

/// <summary>源行在当前容器或槽位基础布局上的局部微调，不重复保存绝对位置。</summary>
public sealed record LyricLocalTransform
{
    /// <summary>不产生微调的变换。</summary>
    public static LyricLocalTransform Neutral { get; } = new();
    /// <summary>相对于当前父参考宽度的横向偏移。</summary>
    public double OffsetXRatio { get; init; }
    /// <summary>相对于当前父参考高度的纵向偏移。</summary>
    public double OffsetYRatio { get; init; }
    /// <summary>绕基础文字锚点的旋转角度。</summary>
    public double RotationDegrees { get; init; }
    /// <summary>绕基础文字锚点的等比缩放。</summary>
    public double Scale { get; init; } = 1;
    /// <summary>有限参数与正缩放；允许移到画布外。</summary>
    public bool IsValid => double.IsFinite(OffsetXRatio) && double.IsFinite(OffsetYRatio)
        && double.IsFinite(RotationDegrees) && double.IsFinite(Scale) && Scale > 0;
}
