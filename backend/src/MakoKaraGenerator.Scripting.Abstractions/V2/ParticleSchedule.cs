using System.Collections.Immutable;

namespace MakoKaraGenerator.Scripting.V2;

/// <summary>真实半开时间窗内的非负有限起止速率；相等为常速，否则按时间线性变化。</summary>
public sealed record RateSegment(TimeRange Range, double StartPerSecond, double EndPerSecond);

/// <summary>有限速率轨；启用窗内缺段为零率，窗外为停用并重置配额。</summary>
public sealed record RateTrack(ImmutableArray<RateSegment> Segments, ImmutableArray<TimeRange> EnabledRanges);

/// <summary>出生后固定的封闭物理描述；不包含历史推进、发射率或作者回调。</summary>
public sealed record ParticleSpec : ParticleDescription
{
    /// <summary>校验已有解析运动与首期不超过五秒的正寿命。</summary>
    public override void Validate()
    {
        base.Validate();
        if (LifetimeUs > ParticleLimits.MaximumLifetimeUs)
            throw new EffectOutputException("ParticleLifetimeExceeded", "SDK v2 粒子寿命不能超过 5000000 微秒。");
    }
}

/// <summary>只连接真实合法后继的固定桥接策略；Jump 不生成桥接发射头。</summary>
public sealed record BridgePolicy(TransitionMode Mode, long DurationUs, long GapLimitUs, CurveValue Curve)
{
    /// <summary>明确关闭桥接。</summary>
    public static BridgePolicy Jump { get; } = new(TransitionMode.Jump, 0, 0, new CurveValue.Easing(EasingKind.Linear));
}

/// <summary>绑定内每个真实播放头独立发射；同轨道和装载的相接词语保留小数配额。</summary>
public sealed record CursorEmissionPlan(EmitterId Id, RateTrack Rate, ParticleSpec Particle,
    CursorAnchorMode AnchorMode, BridgePolicy Bridge);

/// <summary>固定真实事件的一行 Burst；时间、occurrence 和排序均从宿主事件表重新取得。</summary>
public sealed record BurstEventRow(EventId EventId, int Count, BurstAnchor Anchor, ParticleSpec Particle);

/// <summary>Declare 经真实类型化 payload 条件选择的有限事件行；不保存运行期 predicate。</summary>
public sealed record EventBurstPlan(EmitterId Id, TimelineSelector Selector, ImmutableArray<BurstEventRow> Rows);

/// <summary>固定输入的递归只读发射计划；全部集合按准备期身份固定。</summary>
public sealed record ParticleSchedule(ImmutableArray<CursorEmissionPlan> Continuous, ImmutableArray<EventBurstPlan> Bursts)
{
    /// <summary>没有发射器的默认计划。</summary>
    public static ParticleSchedule Empty { get; } = new([], []);
}

/// <summary>最终锚点的固定采样策略，不随缓存命中或请求顺序变化。</summary>
public enum GeometrySamplingMode
{
    /// <summary>在每个真实出生时刻执行完整必要文字依赖。</summary>
    Exact,
    /// <summary>仅在完整依赖均证明连续的区段使用有理 120 Hz 网格。</summary>
    Continuous120Hz,
}

/// <summary>本实例全部文字输出的连续区段和跳变；宿主仍合并其他实例及源断点。</summary>
public sealed record GeometrySamplingPolicy(GeometrySamplingMode Mode,
    ImmutableArray<TimeRange> ContinuousRanges, ImmutableArray<long> DiscontinuitiesUs)
{
    /// <summary>没有连续性声明时的精确默认值。</summary>
    public static GeometrySamplingPolicy Exact { get; } = new(GeometrySamplingMode.Exact, [], []);
}

/// <summary>晚期具名输出的封闭图元种类。</summary>
public enum VisualPrimitiveKind
{
    /// <summary>圆点批次。</summary>
    Dots,
    /// <summary>平端线段批次。</summary>
    Segments,
    /// <summary>带状拖尾批次。</summary>
    Trails,
    /// <summary>保留完整仿射的图片精灵批次。</summary>
    Sprites,
    /// <summary>固定 SVG 矢量资源的完整仿射批次。</summary>
    Paths,
}

/// <summary>晚期输出使用的固定资源类型；与绘制图元能力独立核验。</summary>
public enum VisualResourceKind
{
    /// <summary>不使用 SVG；普通图片 Sprite 仍由 ImageHandle 表达。</summary>
    None,
    /// <summary>SVG 矢量资源，仅用于 Paths。</summary>
    SvgVector,
    /// <summary>SVG atlas 档位，仅用于 Sprites。</summary>
    SvgAtlas,
}

/// <summary>Declare 固定的具名晚期输出；实际类型和层必须与本声明一致。</summary>
public sealed record VisualOutputDeclaration(OutputId Id, VisualPrimitiveKind Primitive, OverlayLayer Layer,
    VisualResourceKind Resource = VisualResourceKind.None);

/// <summary>M08 固定数量和内存上限；宿主可以使用更严格的工程输出预算。</summary>
public static class ParticleLimits
{
    /// <summary>粒子最大寿命，整数微秒。</summary>
    public const long MaximumLifetimeUs = 5_000_000;
    /// <summary>单实例最大发射器数。</summary>
    public const int MaximumEmitters = 32;
    /// <summary>速率段与启用窗合计上限。</summary>
    public const int MaximumRateElements = 4096;
    /// <summary>来源区段与配额 episode 合计上限。</summary>
    public const int MaximumSourceIntervals = 200_000;
    /// <summary>冻结 Burst 事件行上限。</summary>
    public const int MaximumBurstRows = 50_000;
    /// <summary>精确有理数整数位数上限。</summary>
    public const int MaximumIntegerBits = 4096;
    /// <summary>单次查询完整出生记录上限。</summary>
    public const int MaximumBirths = 50_000;
    /// <summary>单次查询存活粒子上限。</summary>
    public const int MaximumAlive = 20_000;
    /// <summary>单次查询出生反解操作上限。</summary>
    public const int MaximumInversionOperations = 4_000_000;
    /// <summary>单帧解析拖尾节点上限。</summary>
    public const int MaximumTrailNodes = 200_000;
    /// <summary>锚点唯一补算时刻上限。</summary>
    public const int MaximumAnchorTimes = 4096;
    /// <summary>锚点补算文字实例调用上限。</summary>
    public const int MaximumTextCalls = 32_768;
    /// <summary>寿命窗口外及未来端点查询上限。</summary>
    public const int MaximumFutureEndpoints = 1024;
    /// <summary>最终锚点 memo 条目上限。</summary>
    public const int MaximumMemoEntries = 2048;
    /// <summary>最终锚点 memo 字节上限。</summary>
    public const long MaximumMemoBytes = 64L * 1024 * 1024;
    /// <summary>准备期内存字节上限。</summary>
    public const long MaximumPrepareBytes = 256L * 1024 * 1024;
    /// <summary>帧查询临时内存字节上限。</summary>
    public const long MaximumFrameBytes = 64L * 1024 * 1024;
    /// <summary>
    /// 累计锚点补算的挂死保护时长，只拦异常长时间补算；正常内容是否失败由时刻、文字调用、未来端点等
    /// 确定的次数预算决定，结果不随机器快慢变化。低于 Worker 单帧 20 秒操作期限，失败时仍能给出诊断。
    /// 不参与粒子筛选或精度选择。
    /// </summary>
    public const long MaximumAnchorWallMilliseconds = 15_000;
}
