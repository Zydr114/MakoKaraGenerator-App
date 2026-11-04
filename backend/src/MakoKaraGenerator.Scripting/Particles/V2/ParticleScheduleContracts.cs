using System.Collections.Immutable;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Scripting.Particles.V2;

/// <summary>固定宿主所有者；不进入作者准备值或随机请求顺序。</summary>
/// <param name="InstanceId">实际实例。</param><param name="PackageIdentity">包、修订和导出。</param>
/// <param name="RuntimeHash">编译执行身份。</param><param name="Seed">固定种子。</param>
/// <param name="InputsFingerprint">固定输入身份。</param><param name="PreparationFingerprint">固定准备身份。</param>
public sealed record ParticleOwner(EffectInstanceId InstanceId, EffectPackageIdentity? PackageIdentity,
    string RuntimeHash, ulong Seed, string InputsFingerprint, string PreparationFingerprint)
{
    internal EffectFailureException Failure(string stage, long timeUs, string code, string message)
        => new(InstanceId, RuntimeHash, stage, timeUs, code, new EffectOutputException(code, message), PackageIdentity);
}

/// <summary>已经过真实源及输出范围核验的一个 Current 或 bridge 区段；不含最终几何。</summary>
/// <param name="EmitterId">声明的发射器。</param><param name="Head">配额 head。</param>
/// <param name="Source">该区段的真实锚点身份。</param><param name="Range">实际半开存在窗。</param>
public sealed record EmissionSourceInterval(EmitterId EmitterId, ParticleHead Head, ParticleSource Source, TimeRange Range);

/// <summary>准备期固定的真实事件锚点；一个事件可有多个不同装载锚点。</summary>
/// <param name="EmitterId">声明的发射器。</param><param name="PlanOrdinal">Bursts 数组下标。</param>
/// <param name="Event">固定索引中的真实事件。</param><param name="Source">已核验的真实锚点。</param>
public sealed record EmissionBurstAnchor(EmitterId EmitterId, int PlanOrdinal, TimelineEvent Event, ParticleSource Source);

/// <summary>运行时从源索引和输出绑定构造的有限目录；不枚举历史粒子。</summary>
/// <param name="Continuous">真实 Current／bridge 区段。</param><param name="Bursts">真实事件锚点行。</param>
public sealed record EmissionSourceCatalog(ImmutableArray<EmissionSourceInterval> Continuous, ImmutableArray<EmissionBurstAnchor> Bursts)
{
    /// <summary>没有可发射来源的目录。</summary>
    public static EmissionSourceCatalog Empty { get; } = new([], []);
}

/// <summary>已经定位出生时间和结构身份、尚未查询最终文字锚点的不可变行。</summary>
/// <param name="Id">无压缩的规范身份。</param><param name="Source">出生时真实来源。</param>
/// <param name="EventId">Burst 事件；连续出生为空。</param><param name="BirthUs">包含的出生时刻。</param>
/// <param name="DeathUs">排除的寿命末端。</param><param name="Spec">固定物理描述。</param>
/// <param name="AnchorMode">最终播放头的纵向锚点。</param>
public sealed record ScheduledBirth(ParticleIdentity Id, ParticleSource Source, EventId? EventId,
    long BirthUs, long DeathUs, ParticleSpec Spec, CursorAnchorMode AnchorMode)
{
    internal TimelineEvent? Trigger { get; init; }
    internal string OriginOrder { get; init; } = "";
}
