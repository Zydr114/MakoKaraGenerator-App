using System.Collections.Immutable;

namespace MakoKaraGenerator.Scripting;

/// <summary>文字求值完成后的真实场景几何；不包含可写帧输出或原生资源。</summary>
public sealed record FinalSceneGeometry
{
    /// <summary>歌曲整数微秒时刻。</summary>
    public required long TimeUs { get; init; }
    /// <summary>已按输出绑定筛选的真实对象；组目录只读。</summary>
    public required FramePresentations Presentations { get; init; }
    /// <summary>最终元素族及其全部注音，保留真实装载。</summary>
    public ImmutableArray<TokenGeometrySample> Tokens { get; init; } = [];
    /// <summary>最终整行矩阵，包括没有逐字元素的行。</summary>
    public ImmutableArray<LineGeometrySample> Lines { get; init; } = [];
    /// <summary>最终真实提示标记。</summary>
    public ImmutableArray<TextMarkGeometrySample> Marks { get; init; } = [];
    /// <summary>全部真实组的只读最终参考框与墨迹，读取不授予写权。</summary>
    public ImmutableArray<GroupGeometrySample> Groups { get; init; } = [];
    /// <summary>绑定范围内的全部真实播放头；后端不可用时仍明确不可用。</summary>
    public PlaybackFrame Playback { get; init; } = PlaybackFrame.Unavailable;
}

/// <summary>严格正长整数微秒的视觉模拟区间，不由 UI 帧率决定。</summary>
public readonly record struct VisualSimulationStep
{
    /// <summary>创建半开 [startUs,endUs)，不允许零长或负时间。</summary>
    public VisualSimulationStep(long startUs, long endUs)
    {
        var interval = new SimulationStep(startUs, endUs);
        StartUs = interval.StartUs; EndUs = interval.EndUs;
    }
    /// <summary>已提交的前一边界。</summary>
    public long StartUs { get; }
    /// <summary>文字已求值完成的当前边界。</summary>
    public long EndUs { get; }
    /// <summary>精确整数区间长度。</summary>
    public long DurationUs => EndUs - StartUs;
    /// <summary>default 不表示有效推进。</summary>
    public bool IsValid => StartUs >= 0 && EndUs > StartUs;
}

/// <summary>视觉阶段的固定只读服务，没有文字 writer 或未来布局查询。</summary>
public abstract record VisualContext
{
    /// <summary>固定工程目录。</summary>
    public required ProjectContext Project { get; init; }
    /// <summary>固定参数。</summary>
    public required ParameterValues Parameters { get; init; }
    /// <summary>固定包资源。</summary>
    public ResourceCatalog Resources { get; init; } = ResourceCatalog.Empty;
    /// <summary>与共享阶段相同的纯索引随机服务。</summary>
    public required DeterministicRandom Random { get; init; }
    /// <summary>源输出范围，不等同于源事件订阅。</summary>
    public required EffectBinding Binding { get; init; }
    /// <summary>固定音频分析，缺失不伪造静音。</summary>
    public AudioFeatures Audio => Project.Audio;
    /// <summary>背景输出模式。</summary>
    public OutputMode OutputMode { get; init; }
    /// <summary>仅本阶段有效的诊断。</summary>
    public DiagnosticReporter Diagnostics { get; init; } = DiagnosticReporter.Unavailable;
    /// <summary>宿主取消。</summary>
    public CancellationToken CancellationToken { get; init; }
}

/// <summary>前一区间的视觉推进；当前文字和历史已经确定，不得回写共享状态。</summary>
public sealed record VisualStateContext : VisualContext
{
    /// <summary>前一边界的最终文字几何，不含 overlay。</summary>
    public required FinalSceneGeometry PreviousGeometry { get; init; }
    /// <summary>当前边界的最终文字几何，不含 overlay。</summary>
    public required FinalSceneGeometry CurrentGeometry { get; init; }
    /// <summary>前一边界本实例的宿主管理粒子，包括后来离场绑定的存活对象。</summary>
    public required ImmutableArray<ParticleSample> PreviousParticles { get; init; }
    /// <summary>当前边界本实例完整粒子；已经解析，不得再次推进。</summary>
    public required ImmutableArray<ParticleSample> Particles { get; init; }
    /// <summary>刚完成区间内本实例的全部真实出生，包括期末已死亡粒子。</summary>
    public required EmissionSchedule EmissionSchedule { get; init; }
    /// <summary>区间左端的历史，不包含区间终点事件。</summary>
    public required TimelineHistory PreviousHistory { get; init; }
    /// <summary>当前边界完整已处理历史，订阅不裁剪前缀。</summary>
    public required TimelineHistory History { get; init; }
}

/// <summary>只读视觉呈现输入；只允许写 overlay，不再修改文字。</summary>
public sealed record VisualFrameContext : VisualContext
{
    private readonly TimelineHistory? _history;
    private readonly ImmutableArray<ParticleSample> _particles;
    private readonly EmissionSchedule? _emissionSchedule;
    /// <summary>目标时刻已完成的最终文字几何与播放头。</summary>
    public required FinalSceneGeometry FinalGeometry { get; init; }
    /// <summary>同一最终几何中的绑定内播放头，不提供另一份光标真值。</summary>
    public PlaybackFrame Playback => FinalGeometry.Playback;
    /// <summary>最近已提交的模拟边界几何；纯帧会话没有固定边界。</summary>
    public FinalSceneGeometry? BoundaryGeometry { get; init; }
    /// <summary>真实历史前缀；纯帧读取报告 TimelineSessionRequired。</summary>
    public TimelineHistory History
    {
        get => _history ?? throw new EffectOutputException("TimelineSessionRequired", "视觉历史需要时间线会话。");
        init => _history = value;
    }
    /// <summary>本实例完整存活粒子，包括绑定离场后仍存活的粒子；纯帧查询明确失败。</summary>
    public ImmutableArray<ParticleSample> Particles
    {
        get => !_particles.IsDefault ? _particles : throw new EffectOutputException("TimelineSessionRequired", "粒子查询需要时间线会话。");
        init => _particles = value;
    }
    /// <summary>最近完成固定区间的全部出生，分数帧保持此记录，不建立长期粒子历史。</summary>
    public EmissionSchedule EmissionSchedule
    {
        get => _emissionSchedule ?? throw new EffectOutputException("TimelineSessionRequired", "发射记录需要时间线会话。");
        init => _emissionSchedule = value;
    }
    /// <summary>实际呈现时刻。</summary>
    public long TimeUs => FinalGeometry.TimeUs;
    /// <summary>最近已处理固定边界，不是任意呈现时刻。</summary>
    public long LastSimulationBoundaryUs => History.LastBoundaryUs;
    /// <summary>分数帧距最近边界的整数时长；作者可解析呈现，不再积分。</summary>
    public long FractionalElapsedUs => TimeUs - LastSimulationBoundaryUs;
}
