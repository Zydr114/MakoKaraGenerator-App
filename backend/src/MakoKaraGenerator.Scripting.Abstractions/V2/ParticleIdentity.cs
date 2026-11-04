using System.Collections.Immutable;

namespace MakoKaraGenerator.Scripting.V2;

/// <summary>连续配额的封闭真实头身份；普通头不按 token 重置配额。</summary>
public abstract record ParticleHead
{
    private ParticleHead() { }
    /// <summary>同轨道与真实装载共享连续配额，保留 reload 身份。</summary>
    public sealed record Normal(CursorTrackId Track, BindingId Binding) : ParticleHead;
    /// <summary>每个真实转换与飞行窗使用独立配额。</summary>
    public sealed record Bridge(ParticleSource.Bridge Source) : ParticleHead;
}

/// <summary>不依赖请求顺序或哈希截断的封闭出生身份。</summary>
public abstract record ParticleOrigin
{
    private ParticleOrigin() { }
    /// <summary>连续 episode 内从一开始的出生序号。</summary>
    public sealed record Continuous(ParticleHead Head, long EpisodeStartUs, ulong SequenceStartingAt1) : ParticleOrigin;
    /// <summary>真实事件、固定计划序号、真实装载锚点及从零开始的 Burst 内序号。</summary>
    public sealed record Burst(EventId EventId, int PlanOrdinal, ParticleSource Anchor, int ParticleOrdinal) : ParticleOrigin;
}

/// <summary>SDK v2 规范结构身份；独立于 v1 ParticleId 的稠密 BirthIndex。</summary>
public sealed record ParticleIdentity(EffectInstanceId InstanceId, EmitterId EmitterId, ParticleOrigin Origin);

/// <summary>明确包含两端的非负出生窗，避免为了包含 Int64 最大时刻而计算 t+1。</summary>
public readonly record struct BirthWindow
{
    /// <summary>建立闭区间；零长度表示单个微秒时刻。</summary>
    public BirthWindow(long startInclusiveUs, long endInclusiveUs)
    {
        if (startInclusiveUs < 0 || endInclusiveUs < startInclusiveUs) throw new ArgumentOutOfRangeException(nameof(startInclusiveUs));
        StartInclusiveUs = startInclusiveUs; EndInclusiveUs = endInclusiveUs;
    }
    /// <summary>包含的起点。</summary>
    public long StartInclusiveUs { get; }
    /// <summary>包含的终点。</summary>
    public long EndInclusiveUs { get; }
    /// <summary>是否位于闭区间内。</summary>
    public bool Contains(long timeUs) => timeUs >= StartInclusiveUs && timeUs <= EndInclusiveUs;
}

/// <summary>已经固定最终出生锚点和散布的完整记录；死亡后仍可存在于明确出生窗中。</summary>
public sealed record ParticleBirth(ParticleIdentity Id, ParticleSource Source, EventId? EventId,
    long BirthUs, long DeathUs, Point2 BirthPosition, Vector2 InitialVelocity, ParticleSpec Spec);

/// <summary>指定时刻的只读解析粒子；出生记录不依赖当前文字是否仍然可见。</summary>
public sealed record ParticleView(ParticleBirth Birth, Point2 Position, double SizePx, ColorRgba Color)
{
    /// <summary>规范结构身份。</summary>
    public ParticleIdentity Id => Birth.Id;
    /// <summary>出生时的真实源装载。</summary>
    public ParticleSource Source => Birth.Source;
    /// <summary>包含的出生边界。</summary>
    public long BirthUs => Birth.BirthUs;
    /// <summary>不包含的死亡边界。</summary>
    public long DeathUs => Birth.DeathUs;
    /// <summary>出生时固定的图元层。</summary>
    public OverlayLayer Layer => Birth.Spec.Layer;
    /// <summary>自动或 Custom 的固定选择。</summary>
    public ParticleRenderMode Rendering => Birth.Spec.Rendering;
}

/// <summary>指定闭窗内完整的本实例出生记录，不从当前存活表反推。</summary>
public sealed record ParticleBirthWindow(BirthWindow Window, ImmutableArray<ParticleBirth> Births);
