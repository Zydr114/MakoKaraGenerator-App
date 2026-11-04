using System.Collections.Immutable;

namespace MakoKaraGenerator.Scripting.V2;

/// <summary>可选的纯晚期入口；与文字入口共用一次 Declare、作者实例及固定准备结果。</summary>
public interface IPureVisualEffect
{
    /// <summary>只读取最终文字几何与本实例粒子，写本次调用的临时图元。</summary>
    void EvaluateVisuals(in VisualFrame frame, PreparedEffect data, VisualOutput output);
}

/// <summary>晚期只读帧；不公开历史、文字写入或其他实例的原始输出。</summary>
public readonly struct VisualFrame
{
    internal VisualFrame(DeclareContext source, long timeUs, EffectBinding binding, OutputMode outputMode,
        FinalSceneGeometry finalGeometry, ParticleFrame particles)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(finalGeometry); ArgumentNullException.ThrowIfNull(particles);
        if (timeUs < 0 || finalGeometry.TimeUs != timeUs || !Enum.IsDefined(outputMode)) throw new ArgumentOutOfRangeException(nameof(timeUs));
        Source = source; TimeUs = timeUs; Binding = binding; OutputMode = outputMode; FinalGeometry = finalGeometry; Particles = particles;
    }
    /// <summary>所属固定输入的源服务。</summary>
    public DeclareContext Source { get; }
    /// <summary>当前非负整数微秒时刻。</summary>
    public long TimeUs { get; }
    /// <summary>本实例的输出绑定，不扩大源订阅或资源所有权。</summary>
    public EffectBinding Binding { get; }
    /// <summary>固定输出背景语义。</summary>
    public OutputMode OutputMode { get; }
    /// <summary>完整文字阶段及基础字／字簇合成后的绑定范围只读几何。</summary>
    public FinalSceneGeometry FinalGeometry { get; }
    /// <summary>本实例存活、完整出生窗与解析拖尾的阶段视图。</summary>
    public ParticleFrame Particles { get; }
    /// <summary>固定参数。</summary>
    public ParameterValues Parameters => Source.Parameters;
    /// <summary>固定分析及其明确不可用状态。</summary>
    public AudioFeatures Audio => Source.Audio;
    /// <summary>固定包修订的逻辑资源。</summary>
    public ResourceCatalog Resources => Source.Resources;
    /// <summary>不消耗共享游标的确定随机。</summary>
    public DeterministicRandom Random => Source.Random;
    /// <summary>真实源事件索引。</summary>
    public TimelineIndex Timeline => Source.Timeline;
    /// <summary>按固定订阅查询的源索引。</summary>
    public SourceIndexes Indexes => Source.Indexes;
    /// <summary>画布逻辑宽高。</summary>
    public Vector2 CanvasSize => Source.CanvasSize;
}

/// <summary>本次晚期调用有效的本实例粒子视图；不是可存入准备结果的固定值。</summary>
public sealed class ParticleFrame
{
    private readonly EffectInstanceId _owner;
    private readonly long _timeUs;
    private readonly ImmutableArray<ParticleView> _alive;
    private readonly ImmutableDictionary<ParticleIdentity, ParticleView> _aliveById;
    private readonly ParticleBirthWindow _births;
    private readonly CancellationToken _token;
    private Func<ParticleIdentity, ImmutableArray<ParticleTrailSample>>? _readTrail;
    private readonly Dictionary<ParticleIdentity, ImmutableArray<ParticleTrailSample>> _trails = [];
    private int _trailNodes;
    private bool _closed;

    internal ParticleFrame(EffectInstanceId owner, long timeUs, ImmutableArray<ParticleView> alive,
        ParticleBirthWindow births, Func<ParticleIdentity, ImmutableArray<ParticleTrailSample>>? readTrail = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(births);
        if (!owner.IsValid || timeUs < 0 || alive.IsDefault || births.Births.IsDefault
            || births.Window != new BirthWindow(Math.Max(0, timeUs - ParticleLimits.MaximumLifetimeUs), timeUs))
            throw new EffectOutputException("InvalidParticleFrame", "粒子视图需要真实实例、当前时刻和完整五秒闭出生窗。");
        if (alive.Length > ParticleLimits.MaximumAlive || births.Births.Length > ParticleLimits.MaximumBirths)
            throw new EffectOutputException("ParticleQueryBudgetExceeded", "粒子视图超过出生或存活数量上限。");
        _owner = owner; _timeUs = timeUs; _alive = alive; _births = births; _readTrail = readTrail; _token = token;
        var live = ImmutableDictionary.CreateBuilder<ParticleIdentity, ParticleView>();
        Dictionary<ParticleIdentity, ParticleBirth> birthRecords = [];
        foreach (var birth in births.Births)
        {
            token.ThrowIfCancellationRequested(); ValidateBirth(birth);
            if (!births.Window.Contains(birth.BirthUs) || !birthRecords.TryAdd(birth.Id, birth)) throw Invalid();
        }
        foreach (var item in alive)
        {
            token.ThrowIfCancellationRequested();
            if (item is null) throw Invalid();
            ValidateBirth(item.Birth);
            if (item.BirthUs > timeUs || item.DeathUs <= timeUs || !double.IsFinite(item.SizePx) || item.SizePx < 0
                || !live.TryAdd(item.Id, item) || !birthRecords.TryGetValue(item.Id, out var birth) || birth != item.Birth) throw Invalid();
        }
        _aliveById = live.ToImmutable(); token.ThrowIfCancellationRequested();
    }

    /// <summary>本实例全部存活粒子，保留已离开文字展示范围的出生。</summary>
    public ImmutableArray<ParticleView> Alive { get { RequireOpen(); return _alive; } }
    /// <summary>固定五秒闭窗内的完整出生记录，包含已经死亡的粒子。</summary>
    public ParticleBirthWindow Births { get { RequireOpen(); return _births; } }
    /// <summary>只为本实例当前存活身份查询已经计入宿主预算的解析拖尾。</summary>
    public ImmutableArray<ParticleTrailSample> GetTrail(ParticleIdentity id)
    {
        RequireOpen(); ArgumentNullException.ThrowIfNull(id);
        if (id.InstanceId != _owner || !_aliveById.TryGetValue(id, out var particle))
            throw new EffectOutputException("ParticleOutsideInstance", "拖尾查询只接受本实例当前存活的结构身份。");
        if (_trails.TryGetValue(id, out var cached)) return cached;
        var result = _readTrail?.Invoke(id) ?? ImmutableArray<ParticleTrailSample>.Empty;
        if (result.IsDefault || result.Length > ParticleLimits.MaximumTrailNodes - _trailNodes)
            throw new EffectOutputException("ParticleTrailBudgetExceeded", "解析拖尾节点超过固定预算。");
        long previous = -1, start = Math.Max(particle.BirthUs, _timeUs - Math.Min(_timeUs, particle.Birth.Spec.TrailDurationUs));
        foreach (var point in result)
        {
            _token.ThrowIfCancellationRequested();
            if (point.TimeUs < start || point.TimeUs > _timeUs || point.TimeUs <= previous)
                throw new EffectOutputException("InvalidParticleTrail", "拖尾必须在固定年龄窗口内严格按时间排序。");
            previous = point.TimeUs;
        }
        _token.ThrowIfCancellationRequested(); _trails.Add(id, result); _trailNodes += result.Length;
        return result;
    }
    internal void Invalidate() { _closed = true; _readTrail = null; _trails.Clear(); }
    private void RequireOpen()
    {
        if (_closed) throw new EffectOutputException("InvalidVisualOutputStage", "粒子视图已经离开本次晚期调用。");
        _token.ThrowIfCancellationRequested();
    }
    private void ValidateBirth(ParticleBirth birth)
    {
        if (birth?.Id is null || birth.Source is null || birth.Spec is null || birth.Id.InstanceId != _owner
            || !birth.Id.EmitterId.IsValid || birth.Id.Origin is null || !birth.Source.BindingId.IsValid
            || birth.BirthUs < 0 || birth.DeathUs <= birth.BirthUs
            || birth.DeathUs - birth.BirthUs != birth.Spec.LifetimeUs) throw Invalid();
        birth.Spec.Validate();
        bool validOrigin = birth.Id.Origin switch
        {
            ParticleOrigin.Continuous { Head: ParticleHead.Normal head } continuous => birth.EventId is null
                && head.Track.IsValid && head.Binding.IsValid && continuous.EpisodeStartUs >= 0
                && continuous.EpisodeStartUs <= birth.BirthUs && continuous.SequenceStartingAt1 > 0
                && birth.Source is ParticleSource.Cursor cursor && cursor.Track == head.Track && cursor.BindingId == head.Binding
                && cursor.Identity.TimeRange.Contains(birth.BirthUs),
            ParticleOrigin.Continuous { Head: ParticleHead.Bridge { Source: not null } head } continuous => birth.EventId is null
                && continuous.EpisodeStartUs >= head.Source.FlightRange.StartUs && continuous.EpisodeStartUs <= birth.BirthUs
                && continuous.SequenceStartingAt1 > 0 && birth.Source == head.Source && head.Source.FlightRange.Contains(birth.BirthUs),
            ParticleOrigin.Burst burst => burst.EventId.IsValid && birth.EventId == burst.EventId
                && burst.PlanOrdinal >= 0 && burst.ParticleOrdinal >= 0 && burst.Anchor is not null && burst.Anchor == birth.Source,
            _ => false,
        };
        if (!validOrigin) throw Invalid();
    }
    private static EffectOutputException Invalid() => new("InvalidParticleFrame", "粒子视图的身份、出生／死亡或完整性无效。");
}
