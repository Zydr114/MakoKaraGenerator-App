using MakoKaraGenerator.Scripting;

/// <summary>起唱事件的最小公开源码示例；数量和物理外观由显式参数提供。</summary>
public sealed class EventBurst : EffectBase
{
    private BurstDescriptor descriptor = null!;
    public override void Initialize(EffectInitializationContext context)
    {
        var p = context.Parameters;
        double count = p.GetNumber("count");
        if (count < 0 || count > int.MaxValue || count != System.Math.Truncate(count))
            throw new EffectOutputException("InvalidBurst", "count 必须是非负整数。");
        descriptor = new()
        {
            Count = (int)count, Anchor = new BurstAnchor.BindingCursors(CursorAnchorMode.AdvanceCenter),
            LifetimeUs = p.GetDuration("lifeUs"), SizePx = p.GetNumber("sizePx"), Color = p.GetColor("color"),
            SpreadPx = p.GetNumber("spreadPx"), TrailDurationUs = p.GetDuration("trailUs"),
            Velocity = new Vector2(0, -10), SeedStreamKey = "event-burst", Layer = OverlayLayer.AboveText
        };
    }
    public override void OnEvent(TimelineEvent evt, EventContext context)
    {
        if (evt is TokenStarted token)
            context.Bursts.Queue(new("burst"), descriptor with { Anchor = new BurstAnchor.SourceToken(token.TokenId) });
    }
}
