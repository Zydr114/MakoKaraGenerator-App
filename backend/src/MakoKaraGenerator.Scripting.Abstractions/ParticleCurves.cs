namespace MakoKaraGenerator.Scripting;

/// <summary>歌词换行时的发射点策略。</summary>
public enum TransitionMode
{
    /// <summary>新头直接换点，旧粒子保留出生坐标。</summary>
    Jump,
    /// <summary>在允许空档内连接全部合法后继路径。</summary>
    Bridge,
}

/// <summary>年龄归一化曲线及显式端值，不夹值。</summary>
public sealed record ParticleAgeCurve
{
    /// <summary>创建有限端值与已有固定曲线。</summary>
    public ParticleAgeCurve(double start, double end, CurveValue curve)
    { Point2.Finite(start); Point2.Finite(end); ArgumentNullException.ThrowIfNull(curve); Start = start; End = end; Curve = curve; }
    /// <summary>出生端值。</summary>
    public double Start { get; }
    /// <summary>寿命末端值。</summary>
    public double End { get; }
    /// <summary>固定年龄曲线，可使用已实现三种曲线。</summary>
    public CurveValue Curve { get; }
    /// <summary>解析端值插值；溢出报告错误。</summary>
    public double Evaluate(double age) { double value = Start + (End - Start) * Curve.Evaluate(age); Point2.Finite(value); return value; }
    /// <summary>恒定 1，保持描述的尺寸或透明度。</summary>
    public static ParticleAgeCurve ConstantOne { get; } = new(1, 1, new CurveValue.Easing(EasingKind.Linear));
}

/// <summary>宿主解析运动，无作者回调或逐帧积分。</summary>
public sealed record ParticleMotion
{
    /// <summary>逻辑像素／秒平方的恒定加速度。</summary>
    public Vector2 Acceleration { get; init; }
    /// <summary>年龄透明度乘数，结果必须在 [0,1]。</summary>
    public ParticleAgeCurve Opacity { get; init; } = ParticleAgeCurve.ConstantOne;
    /// <summary>年龄尺寸乘数，结果必须非负；零表示不可见。</summary>
    public ParticleAgeCurve Size { get; init; } = ParticleAgeCurve.ConstantOne;
    /// <summary>零加速度、固定尺寸及透明度的常速度运动。</summary>
    public static ParticleMotion ConstantVelocity { get; } = new();
}
