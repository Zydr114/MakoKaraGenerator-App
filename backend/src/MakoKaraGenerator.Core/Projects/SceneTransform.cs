namespace MakoKaraGenerator.Core.Projects;

/// <summary>独立于样式的动画几何与图层不透明度。</summary>
public sealed record SceneTransform
{
    /// <summary>不改变画面的值。</summary>
    public static SceneTransform Neutral { get; } = new();
    /// <summary>图层不透明度。</summary>
    public double Opacity { get; init; } = 1;
    /// <summary>相对缩放。</summary>
    public double Scale { get; init; } = 1;
    /// <summary>横向画布像素偏移。</summary>
    public double OffsetXPx { get; init; }
    /// <summary>纵向画布像素偏移。</summary>
    public double OffsetYPx { get; init; }
    /// <summary>额外旋转角度。</summary>
    public double RotationDegrees { get; init; }
    /// <summary>全部参数是否有效。</summary>
    public bool IsValid => double.IsFinite(Opacity) && Opacity is >= 0 and <= 1
        && double.IsFinite(Scale) && Scale > 0 && double.IsFinite(OffsetXPx)
        && double.IsFinite(OffsetYPx) && double.IsFinite(RotationDegrees);
    /// <summary>在两个通道值之间插值。</summary>
    /// <param name="from">起始值。</param>
    /// <param name="to">终止值。</param>
    /// <param name="progress">经过缓动的比例。</param>
    /// <returns>插值结果。</returns>
    public static SceneTransform Interpolate(SceneTransform from, SceneTransform to, double progress) => new()
    {
        Opacity = Mix(from.Opacity, to.Opacity, progress), Scale = Mix(from.Scale, to.Scale, progress),
        OffsetXPx = Mix(from.OffsetXPx, to.OffsetXPx, progress), OffsetYPx = Mix(from.OffsetYPx, to.OffsetYPx, progress),
        RotationDegrees = Mix(from.RotationDegrees, to.RotationDegrees, progress),
    };
    private static double Mix(double a, double b, double progress) => a + (b - a) * progress;
}
