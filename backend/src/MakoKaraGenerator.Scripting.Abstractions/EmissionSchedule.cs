using System.Collections.Immutable;

namespace MakoKaraGenerator.Scripting;

/// <summary>真实出生记录，寿命短于区间的粒子也保留，不代表当前存活状态。</summary>
/// <param name="Id">宿主稳定粒子身份。</param><param name="Source">真实出生来源。</param>
/// <param name="BirthUs">整数出生时刻。</param><param name="DeathUs">寿命半开末端。</param>
/// <param name="BirthPosition">实际最终世界出生位置。</param><param name="Descriptor">固定的物理描述。</param>
/// <param name="EventId">Burst 触发事件；连续出生为 null。</param>
public sealed record ParticleBirthSample(ParticleId Id, ParticleSource Source, long BirthUs, long DeathUs,
    Point2 BirthPosition, ParticleDescription Descriptor, EventId? EventId = null)
{
    /// <summary>只有 Cursor 来源有轨道。</summary>
    public CursorTrackId? TrackId => Source.TrackId;
    /// <summary>来源的真实装载。</summary>
    public BindingId BindingId => Source.BindingId;
}

/// <summary>最近完成固定半开区间的真实出生；只读，不重复推进，不积累长期历史。</summary>
public sealed record EmissionSchedule
{
    /// <summary>创建固定区间；初始 [0,0) 只允许空集合。</summary>
    public EmissionSchedule(long startUs, long endUs, ImmutableArray<ParticleBirthSample> births)
    {
        if (startUs < 0 || endUs < startUs || endUs == startUs && startUs != 0 || births.IsDefault
            || births.Any(item => item is null || !item.Id.InstanceId.IsValid || !item.Id.EmitterId.IsValid || item.Source is null || !item.BindingId.IsValid || item.EventId is { IsValid: false }
                || item.Descriptor is null || item.BirthUs < startUs || item.BirthUs >= endUs || item.DeathUs <= item.BirthUs))
            throw new ArgumentException("出生记录须处在明确的半开区间内。", nameof(births));
        StartUs = startUs; EndUs = endUs; Births = births;
    }
    /// <summary>区间左端。</summary>
    public long StartUs { get; }
    /// <summary>区间右端，不纳入该时刻出生。</summary>
    public long EndUs { get; }
    /// <summary>实际时序出生，包含期末已死亡对象。</summary>
    public ImmutableArray<ParticleBirthSample> Births { get; }
    /// <summary>初始尚无已完成区间。</summary>
    public static EmissionSchedule Initial { get; } = new(0, 0, []);
}
