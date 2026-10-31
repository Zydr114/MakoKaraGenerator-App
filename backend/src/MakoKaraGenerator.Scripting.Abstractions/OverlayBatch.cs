namespace MakoKaraGenerator.Scripting;

/// <summary>封闭的世界逻辑图元批次，共用实例、名字空间和绘制预算。</summary>
public abstract record OverlayBatch
{
    private protected OverlayBatch(EffectInstanceId instanceId, OutputId id, OverlayLayer layer)
    { InstanceId = instanceId; Id = id; Layer = layer; }
    /// <summary>宿主固定实例。</summary>
    public EffectInstanceId InstanceId { get; init; }
    /// <summary>本实例内跨图元类型唯一的名字。</summary>
    public OutputId Id { get; init; }
    /// <summary>明确的画布合成层。</summary>
    public OverlayLayer Layer { get; init; }
    /// <summary>全部数据有效；不可用 default 数组冒充空输出。</summary>
    public abstract bool IsValid { get; }
    /// <summary>圆点／线段各一项，拖尾每点一项；透明数据也计入。</summary>
    public abstract long ItemCount { get; }
}
