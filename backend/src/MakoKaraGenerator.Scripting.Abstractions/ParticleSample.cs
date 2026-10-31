using System.Collections.Immutable;

namespace MakoKaraGenerator.Scripting;

/// <summary>同一执行修订中稳定的粒子身份。</summary>
public readonly record struct ParticleId(EffectInstanceId InstanceId, EmitterId EmitterId, ulong BirthIndex);
/// <summary>固定时间拖尾采样，不是 UI 帧记录。</summary>
public readonly record struct ParticleTrailSample(long TimeUs, Point2 Position);
/// <summary>正式准备帧的不可变粒子视图。</summary>
public sealed record ParticleSample
{
    /// <summary>实际稳定身份。</summary>
    public required ParticleId Id { get; init; }
    /// <summary>真实出生来源，之后不因场景重挂变化。</summary>
    public required ParticleSource Source { get; init; }
    /// <summary>只有光标来源具有真实轨道。</summary>
    public CursorTrackId? TrackId => Source.TrackId;
    /// <summary>实际出生装载。</summary>
    public BindingId BindingId => Source.BindingId;
    /// <summary>Burst 的真实触发事件；连续出生为 null。</summary>
    public EventId? EventId { get; init; }
    /// <summary>整数微秒出生时间。</summary>
    public required long BirthUs { get; init; }
    /// <summary>寿命末端，不包含。</summary>
    public required long DeathUs { get; init; }
    /// <summary>出生最终世界位置，之后不随文本移动。</summary>
    public required Point2 BirthPosition { get; init; }
    /// <summary>当前解析世界位置。</summary>
    public required Point2 Position { get; init; }
    /// <summary>实际年龄曲线直径。</summary>
    public required double SizePx { get; init; }
    /// <summary>实际年龄曲线直乘颜色。</summary>
    public required ColorRgba Color { get; init; }
    /// <summary>本帧发射器图元层，包含模板／实例覆盖；Custom 作者可读作为绘制层建议。</summary>
    public OverlayLayer Layer { get; init; } = OverlayLayer.AboveText;
    /// <summary>出生、固定边界及当前解析末端的不可变样本。</summary>
    public ImmutableArray<ParticleTrailSample> Trail { get; init; } = [];
}
