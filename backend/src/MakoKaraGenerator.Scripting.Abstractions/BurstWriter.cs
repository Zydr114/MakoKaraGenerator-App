using System.Collections.Immutable;

namespace MakoKaraGenerator.Scripting;

/// <summary>事件发射的显式锚点，不接受任意位置委托或原生对象。</summary>
public abstract record BurstAnchor
{
    private BurstAnchor() { }
    /// <summary>当前真实装载的基础元素 Advance 中心。</summary>
    /// <param name="Id">真实源元素身份。</param>
    public sealed record SourceToken(TokenId Id) : BurstAnchor;
    /// <summary>当前真实装载的整行 Advance 中心。</summary>
    /// <param name="Id">真实源行身份。</param>
    public sealed record SourceLine(LineId Id) : BurstAnchor;
    /// <summary>指定真实文字标记的 Advance 中心。</summary>
    /// <param name="Id">真实文字标记节点。</param>
    public sealed record TextMark(NodeId Id) : BurstAnchor;
    /// <summary>输出绑定内所有实际播放头；无人演唱是合法空集合。</summary>
    /// <param name="Mode">显式播放头锚点。</param>
    public sealed record BindingCursors(CursorAnchorMode Mode) : BurstAnchor;
    internal bool IsValid => this switch
    {
        SourceToken token => token.Id.IsValid, SourceLine line => line.Id.IsValid, TextMark mark => mark.Id.IsValid,
        BindingCursors cursors => Enum.IsDefined(cursors.Mode), _ => false,
    };
}

/// <summary>当前真实事件的瞬时出生描述；每个匹配锚点独立获得 Count 个粒子。</summary>
public sealed record BurstDescriptor : ParticleDescription
{
    /// <summary>每个锚点的非负数量；零不产生粒子。</summary>
    public required int Count { get; init; }
    /// <summary>真实源身份或明确的绑定内播放头选择。</summary>
    public required BurstAnchor Anchor { get; init; }
    /// <inheritdoc/>
    public override void Validate()
    {
        base.Validate();
        if (Count < 0 || Anchor is null || !Anchor.IsValid)
            throw new EffectOutputException("InvalidBurst", "Burst 数量或真实锚点身份无效。");
    }
}

/// <summary>一个当前真实事件的不可变发射意图。</summary>
/// <param name="InstanceId">宿主实例。</param><param name="EmitterId">已声明输出身份。</param>
/// <param name="EventId">宿主当前真实事件。</param><param name="TimeUs">事件时刻。</param>
/// <param name="Descriptor">固定描述。</param>
public sealed record BurstIntent(EffectInstanceId InstanceId, EmitterId EmitterId, EventId EventId, long TimeUs, BurstDescriptor Descriptor);

/// <summary>仅当前 OnEvent 有效，登记意图后由宿主使用最终文字几何解析。</summary>
public sealed class BurstWriter
{
    private readonly EffectInstanceId _instance;
    private readonly TimelineEvent? _event;
    private readonly EffectCapabilities _capabilities;
    private readonly Dictionary<EmitterId, BurstIntent> _intents = [];
    private bool _closed;
    private BurstWriter() { _closed = true; }
    /// <summary>宿主固定实例、能力与真实事件；作者自行创建的 writer 不被采纳。</summary>
    public BurstWriter(EffectInstanceId instance, EffectCapabilities capabilities, TimelineEvent currentEvent)
    {
        if (!instance.IsValid) throw new ArgumentException("实例身份无效。", nameof(instance));
        ArgumentNullException.ThrowIfNull(currentEvent);
        if ((capabilities & ~(EffectCapabilities.Transforms | EffectCapabilities.Style | EffectCapabilities.Overlay | EffectCapabilities.ImageEffects)) != 0)
            throw new ArgumentOutOfRangeException(nameof(capabilities));
        _instance = instance; _event = currentEvent; _capabilities = capabilities;
    }
    /// <summary>同事件同发射器只保留最后登记；不能指定任意事件或出生时间。</summary>
    public void Queue(EmitterId emitterId, BurstDescriptor descriptor)
    {
        RequireOpen();
        if (!_capabilities.HasFlag(EffectCapabilities.Overlay)) throw new EffectOutputException("UndeclaredCapability", "Burst 须声明 Overlay 能力。");
        if (!emitterId.IsValid || descriptor is null) throw new EffectOutputException("InvalidBurst", "Burst 须有真实输出身份和描述。");
        descriptor.Validate(); _intents[emitterId] = new(_instance, emitterId, _event!.EventId, _event.TimeUs, descriptor);
    }
    /// <summary>结束当前事件，按输出身份 ordinal 排序固定意图。</summary>
    public ImmutableArray<BurstIntent> Complete()
    {
        RequireOpen(); _closed = true;
        return [.. _intents.Values.OrderBy(static intent => intent.EmitterId.Value, StringComparer.Ordinal)];
    }
    /// <summary>回调退出或失败后关闭。</summary>
    public void Invalidate() => _closed = true;
    /// <summary>没有当前真实事件的上下文明确不可写。</summary>
    public static BurstWriter Unavailable { get; } = new();
    private void RequireOpen()
    { if (_closed) throw new EffectOutputException("InvalidBurstOutputStage", "Burst writer 已离开当前 OnEvent 阶段。"); }
}
