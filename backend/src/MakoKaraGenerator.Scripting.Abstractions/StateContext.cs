namespace MakoKaraGenerator.Scripting;

/// <summary>一次严格正长的半开模拟区间，时间单位为整数微秒。</summary>
public readonly record struct SimulationStep
{
    /// <summary>建立 [startUs,endUs)；不允许零长或负时间。</summary>
    public SimulationStep(long startUs, long endUs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startUs);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(endUs, startUs);
        StartUs = startUs; EndUs = endUs;
    }
    /// <summary>区间起点，也是本次输入和历史边界。</summary>
    public long StartUs { get; }
    /// <summary>下一处理边界，不包括该时刻事件。</summary>
    public long EndUs { get; }
    /// <summary>整数区间长度。</summary>
    public long DurationUs => EndUs - StartUs;
    /// <summary>default 不是有效推进。</summary>
    public bool IsValid => StartUs >= 0 && EndUs > StartUs;
}

/// <summary>共享语义阶段的固定输入；不提供原生绘制对象或可写工程。</summary>
public abstract record SemanticContext
{
    /// <summary>本阶段有效的只读基本布局查询，不推进共享状态或未来历史。</summary>
    public GeometryQueries GeometryQueries { get; init; } = GeometryQueries.Unavailable;
    /// <summary>固定工程。</summary>
    public required ProjectContext Project { get; init; }
    /// <summary>固定参数。</summary>
    public required ParameterValues Parameters { get; init; }
    /// <summary>固定资源。</summary>
    public ResourceCatalog Resources { get; init; } = ResourceCatalog.Empty;
    /// <summary>实例纯索引随机服务。</summary>
    public required DeterministicRandom Random { get; init; }
    /// <summary>真实已处理工程前缀，订阅仅筛选回调。</summary>
    public required TimelineHistory History { get; init; }
    /// <summary>输出绑定，与源订阅不同。</summary>
    public required EffectBinding Binding { get; init; }
    /// <summary>固定音频分析。</summary>
    public AudioFeatures Audio => Project.Audio;
    /// <summary>仅本阶段有效的诊断。</summary>
    public DiagnosticReporter Diagnostics { get; init; } = DiagnosticReporter.Unavailable;
    /// <summary>宿主取消。</summary>
    public CancellationToken CancellationToken { get; init; }
}

/// <summary>推进输入以 step.StartUs 为准，历史不包括区间终点事件。</summary>
public sealed record StateContext : SemanticContext
{
    /// <summary>输入时刻，等于历史边界。</summary>
    public long TimeUs => History.LastBoundaryUs;
}

/// <summary>一个真实事件的上下文；历史已包含此事件，不包括排序在后的同刻事件。</summary>
public sealed record EventContext : SemanticContext
{
    /// <summary>仅本事件回调有效的 Burst 意图输出；不是立即绘制或最终几何查询。</summary>
    public BurstWriter Bursts { get; init; } = BurstWriter.Unavailable;
    /// <summary>与 OnEvent 参数完全相同的事件。</summary>
    public required TimelineEvent Event { get; init; }
    /// <summary>事件时刻。</summary>
    public long TimeUs => Event.TimeUs;
    /// <summary>稳定事件身份。</summary>
    public EventId EventId => Event.EventId;
}
