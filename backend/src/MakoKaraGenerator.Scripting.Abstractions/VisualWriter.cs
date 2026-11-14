using System.Collections.Immutable;

namespace MakoKaraGenerator.Scripting;

/// <summary>画布 overlay 的明确合成层级。</summary>
public enum OverlayLayer
{
    /// <summary>背景之后、全部文字之前。</summary>
    BehindText,
    /// <summary>全部文字、注音和提示之后。</summary>
    AboveText,
}

/// <summary>固定帧输出预算，由工程效果预算持久化。</summary>
public sealed record VisualOutputBudget
{
    /// <summary>创建明确的绘制项限制，不静默减少输出。</summary>
    public VisualOutputBudget(int drawItemLimit = 100_000)
    { ArgumentOutOfRangeException.ThrowIfNegativeOrZero(drawItemLimit); DrawItemLimit = drawItemLimit; }
    /// <summary>全部实例合计的每帧绘制项上限。</summary>
    public int DrawItemLimit { get; }
    /// <summary>已接受合同 B 的初始预算。</summary>
    public static VisualOutputBudget Default { get; } = new();
}

/// <summary>最终画布逻辑空间的圆点，不修改文字或源布局。</summary>
public readonly record struct Dot
{
    /// <summary>创建正有限半径和直乘颜色的圆点。</summary>
    public Dot(Point2 position, double radius, ColorRgba color)
    {
        Position = position; Radius = radius; Color = color;
        if (!IsValid) throw new ArgumentOutOfRangeException(nameof(radius), "圆点半径和外边界须有限且半径大于零。");
    }
    /// <summary>最终画布逻辑位置，不再次乘文字矩阵。</summary>
    public Point2 Position { get; }
    /// <summary>逻辑像素半径。</summary>
    public double Radius { get; }
    /// <summary>sRGB 直乘颜色，后端只预乘一次。</summary>
    public ColorRgba Color { get; }
    /// <summary>default 圆点无效，外边界溢出也无效。</summary>
    public bool IsValid => double.IsFinite(Radius) && Radius > 0
        && double.IsFinite(Position.X - Radius) && double.IsFinite(Position.X + Radius)
        && double.IsFinite(Position.Y - Radius) && double.IsFinite(Position.Y + Radius);
}

/// <summary>稳定实例及命名批次的不可变圆点输出。</summary>
/// <param name="InstanceId">宿主实例身份。</param><param name="Id">本实例内唯一批次 ID。</param>
/// <param name="Layer">合成层级。</param><param name="Dots">批次内有序圆点。</param>
public sealed record DotBatch(EffectInstanceId InstanceId, OutputId Id, OverlayLayer Layer, ImmutableArray<Dot> Dots)
    : OverlayBatch(InstanceId, Id, Layer)
{
    /// <inheritdoc/>
    public override bool IsValid => !Dots.IsDefault && Dots.All(static dot => dot.IsValid);
    /// <inheritdoc/>
    public override long ItemCount => Dots.Length;
}
