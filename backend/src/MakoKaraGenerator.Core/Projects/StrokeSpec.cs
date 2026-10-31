namespace MakoKaraGenerator.Core.Projects;

/// <summary>描边连接方式。</summary>
public enum StrokeJoin
{
    /// <summary>尖角连接。</summary>
    Miter = 0,

    /// <summary>圆角连接。</summary>
    Round = 1,

    /// <summary>斜切连接。</summary>
    Bevel = 2,
}

/// <summary>描边相对字形的位置。</summary>
public enum StrokePosition
{
    /// <summary>完全位于字形外部；当前实现唯一支持的语义。</summary>
    Outside = 0,
}

/// <summary>
/// 文字描边的绘制描述。
/// </summary>
/// <remarks>
/// 描边的位置语义固定为外部：渲染器先画外扩轮廓，再减去不透明字形遮罩，
/// 因而半透明或透明填充也不会露出轮廓内侧。提供 <see cref="Gradient"/> 时使用其颜色，
/// 否则使用 <see cref="Color"/>；渐变不叠乘纯色透明度。
/// </remarks>
public sealed record StrokeSpec
{
    /// <summary>唯一完整描边材质，几何另行保存。</summary>
    public MaterialSpec Material { get; init; } = new MaterialSpec.Solid(RgbaColor.White);
    /// <summary>静态颜色访问外观；null建立透明solid。</summary>
    public RgbaColor? Color { get => Material.RepresentativeColor; init => Material = Material.WithColor(value ?? RgbaColor.Transparent); }
    /// <summary>静态渐变访问外观；包材质须明确整体替换。</summary>
    public GradientSpec? Gradient { get => (Material as MaterialSpec.Gradient)?.Value; init => Material = Material.WithGradient(value); }

    /// <summary>描边宽度（画布像素）。</summary>
    public float WidthPx { get; init; } = 2f;

    /// <summary>描边连接方式。</summary>
    public StrokeJoin Join { get; init; } = StrokeJoin.Round;

    /// <summary>描边位置；当前固定为外部。</summary>
    public StrokePosition Position { get; init; } = StrokePosition.Outside;

    /// <summary>描边描述是否有效。</summary>
    public bool IsValid => float.IsFinite(WidthPx)
        && WidthPx >= 0f
        && Enum.IsDefined(Join)
        && Enum.IsDefined(Position)
        && Material is { IsValid: true };

    /// <summary>有效且非零宽的描边是否有颜色或渐变。</summary>
    public bool IsEnabled => IsValid
        && WidthPx > 0f
        && Material is not MaterialSpec.Solid { Color.Alpha: 0 };

    /// <summary>创建并校验一份描边描述。</summary>
    /// <param name="color">描边颜色；为空且未提供渐变时关闭描边。</param>
    /// <param name="widthPx">描边宽度，必须为非负有限数。</param>
    /// <param name="join">描边连接方式。</param>
    /// <param name="position">描边位置；当前必须为外部。</param>
    /// <returns>新的描边描述。</returns>
    /// <exception cref="ArgumentOutOfRangeException">宽度或连接方式无效。</exception>
    public static StrokeSpec Create(
        RgbaColor? color,
        float widthPx,
        StrokeJoin join = StrokeJoin.Round,
        StrokePosition position = StrokePosition.Outside)
    {
        if (!float.IsFinite(widthPx) || widthPx < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(widthPx), widthPx, "描边宽度必须是非负有限数。");
        }

        if (!Enum.IsDefined(join))
        {
            throw new ArgumentOutOfRangeException(nameof(join), join, "描边连接方式无效。");
        }

        if (!Enum.IsDefined(position))
        {
            throw new ArgumentOutOfRangeException(nameof(position), position, "描边位置无效。");
        }

        return new StrokeSpec { Color = color, WidthPx = widthPx, Join = join, Position = position };
    }
}
