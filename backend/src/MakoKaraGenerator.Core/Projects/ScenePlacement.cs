namespace MakoKaraGenerator.Core.Projects;

/// <summary>通用场景位置使用的锚点。</summary>
public enum ScenePlacementAnchor
{
    /// <summary>以行几何中心为锚点。</summary>
    Center = 0,

    /// <summary>以左上角为锚点。</summary>
    TopLeft = 1,

    /// <summary>以顶部中心为锚点。</summary>
    TopCenter = 2,

    /// <summary>以右下角为锚点。</summary>
    BottomRight = 3,

    /// <summary>以左侧中心为锚点。</summary>
    LeftCenter = 4,

    /// <summary>以右侧中心为锚点。</summary>
    RightCenter = 5,
}

/// <summary>画布归一化坐标，两个分量通常落在 0 到 1 之间。</summary>
/// <param name="X">横向位置。</param>
/// <param name="Y">纵向位置。</param>
public readonly record struct NormalizedPosition(double X, double Y)
{
    /// <summary>坐标是否为有限数；允许画布外位置。</summary>
    public bool IsValid => double.IsFinite(X) && double.IsFinite(Y);

    /// <summary>创建一个已校验的归一化坐标。</summary>
    /// <param name="x">横向位置，归一化到父参考矩形。</param>
    /// <param name="y">纵向位置，归一化到父参考矩形。</param>
    /// <returns>新的坐标。</returns>
    /// <exception cref="ArgumentOutOfRangeException">坐标不是有限数。</exception>
    public static NormalizedPosition Create(double x, double y)
    {
        NormalizedPosition position = new(x, y);

        return position.IsValid
            ? position
            : throw new ArgumentOutOfRangeException(nameof(x), "位置必须为有限数。");
    }
}

/// <summary>通用场景节点或基础文字的静态位置。</summary>
public sealed record ScenePlacement
{
    /// <summary>默认位置：画布中心。</summary>
    public static ScenePlacement Default { get; } = new();

    /// <summary>归一化位置。</summary>
    public NormalizedPosition Position { get; init; } = new(0.5d, 0.5d);

    /// <summary>位置锚点。</summary>
    public ScenePlacementAnchor Anchor { get; init; } = ScenePlacementAnchor.Center;

    /// <summary>旋转角度，单位为度。</summary>
    public double RotationDegrees { get; init; }

    /// <summary>相对于样式基础缩放的静态缩放。</summary>
    public double Scale { get; init; } = 1d;

    /// <summary>位置数据是否有效。</summary>
    public bool IsValid => Enum.IsDefined(Anchor)
        && Position.IsValid
        && double.IsFinite(RotationDegrees)
        && double.IsFinite(Scale)
        && Scale > 0d;

    /// <summary>创建一个已校验的行位置。</summary>
    /// <param name="position">归一化坐标。</param>
    /// <param name="anchor">锚点。</param>
    /// <param name="rotationDegrees">旋转角度。</param>
    /// <param name="scale">静态缩放，必须为正。</param>
    /// <returns>新的位置。</returns>
    /// <exception cref="ArgumentOutOfRangeException">参数越界或不是有限数。</exception>
    public static ScenePlacement Create(
        NormalizedPosition position,
        ScenePlacementAnchor anchor = ScenePlacementAnchor.Center,
        double rotationDegrees = 0d,
        double scale = 1d)
    {
        ScenePlacement result = new()
        {
            Position = position,
            Anchor = anchor,
            RotationDegrees = rotationDegrees,
            Scale = scale,
        };

        return result.IsValid
            ? result
            : throw new ArgumentOutOfRangeException(nameof(position), "行位置必须使用有效归一化坐标和正缩放。");
    }
}
