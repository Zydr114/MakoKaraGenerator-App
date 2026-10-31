using System.Collections.Immutable;

namespace MakoKaraGenerator.Scripting;

/// <summary>发射器使用绑定内所有实际播放头，不挑选唯一头。</summary>
public enum CursorTrackFilter { /// <summary>绑定内每条头分别积累配额。</summary>
    BindingCursors }
/// <summary>歌词换行时的发射点策略。</summary>
public enum TransitionMode { /// <summary>新头直接换点，旧粒子保留出生坐标。</summary>
    Jump, /// <summary>在允许空档内连接全部合法后继路径。</summary>
    Bridge }
/// <summary>无唯一图像位置时的粒子策略。</summary>
public enum ImageOnlyPolicy { /// <summary>读取文字语义最终播放头。</summary>
    SemanticCursor, /// <summary>纳入目标捕获；须正式捕获协调器支持。</summary>
    IncludeInTargetCapture }

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
/// <summary>固定区间左端连续发射描述；每次 Set 不累计出生。</summary>
public sealed record CursorEmitterDescriptor : ParticleDescription
{
    /// <summary>绑定内全部实际播放头。</summary>
    public CursorTrackFilter TrackFilter { get; init; } = CursorTrackFilter.BindingCursors;
    /// <summary>必须显式选择字形基线或 Advance 中心。</summary>
    public required CursorAnchorMode AnchorMode { get; init; }
    /// <summary>每条合法头独立的每秒发射率，有限非负。</summary>
    public required double RatePerSecond { get; init; }
    /// <summary>换行策略；Bridge 在实际空档末段移动发射点。</summary>
    public TransitionMode TransitionMode { get; init; } = TransitionMode.Jump;
    /// <summary>桥接时长，微秒。</summary>
    public long BridgeDurationUs { get; init; }
    /// <summary>可桥接空档上限，微秒。</summary>
    public long BridgeGapLimitUs { get; init; }
    /// <summary>桥接世界线段的时间曲线；Jump 不读取。</summary>
    public CurveValue BridgeCurve { get; init; } = new CurveValue.Easing(EasingKind.EaseInOut);
    /// <summary>图像位置策略。</summary>
    public ImageOnlyPolicy ImageOnlyPolicy { get; init; } = ImageOnlyPolicy.SemanticCursor;
    /// <summary>校验描述；不夹值或降级请求。</summary>
    public override void Validate()
    {
        base.Validate();
        if (!Enum.IsDefined(TrackFilter) || !Enum.IsDefined(AnchorMode) || !Enum.IsDefined(TransitionMode)
            || !Enum.IsDefined(ImageOnlyPolicy) || !double.IsFinite(RatePerSecond) || RatePerSecond < 0
            || BridgeDurationUs < 0 || BridgeGapLimitUs < 0 || BridgeCurve is null)
            throw new EffectOutputException("InvalidEmitter", "发射描述的身份、数值、枚举或曲线无效。");
        if (ImageOnlyPolicy != ImageOnlyPolicy.SemanticCursor) throw new EffectOutputException("EmitterTargetCapturePending", "目标内粒子须完成正式捕获协调器。");
    }
}
/// <summary>不可变连续发射输出。</summary>
public sealed record EmitterBinding(EffectInstanceId InstanceId, EmitterId Id, CursorEmitterDescriptor Descriptor);
/// <summary>会话级固定粒子及拖尾预算。</summary>
public sealed record ParticleSimulationBudget
{
    /// <summary>创建明确正预算；初始值来自已接受合同 B。</summary>
    public ParticleSimulationBudget(int liveParticleLimit = 20_000, int trailSampleLimit = 200_000, int emissionRecordLimit = 100_000)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(liveParticleLimit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(trailSampleLimit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(emissionRecordLimit);
        LiveParticleLimit = liveParticleLimit; TrailSampleLimit = trailSampleLimit; EmissionRecordLimit = emissionRecordLimit;
    }
    /// <summary>全部实例存活粒子峰值上限。</summary>
    public int LiveParticleLimit { get; }
    /// <summary>全部实例固定拖尾样本总上限。</summary>
    public int TrailSampleLimit { get; }
    /// <summary>全部实例每个固定半开区间的真实出生记录上限，包括期末已死亡粒子。</summary>
    public int EmissionRecordLimit { get; }
    /// <summary>已接受初始预算。</summary>
    public static ParticleSimulationBudget Default { get; } = new();
}
