using MakoKaraGenerator.Scripting;

/// <summary>真实起唱／标记可见事件登记Burst；宿主统一出生与绘制，不由预览帧计数。</summary>
public sealed class EventBurst : EffectBase
{
    private BurstDescriptor descriptor = null!;
    private string anchor = "token";
    public override void Initialize(EffectInitializationContext context)
    {
        var p = context.Parameters; anchor = p.GetEnum("anchor");
        double count = p.GetNumber("count");
        if (count < 0 || count > int.MaxValue || count != System.Math.Truncate(count))
            throw new EffectOutputException("InvalidBurst", "count 必须是非负整数。");
        descriptor = new()
        {
            Count = (int)count, Anchor = new BurstAnchor.BindingCursors(CursorAnchorMode.AdvanceCenter),
            LifetimeUs = p.GetDuration("lifeUs"), SizePx = p.GetNumber("sizePx"), Color = p.GetColor("color"),
            SpreadPx = p.GetNumber("spreadPx"), TrailDurationUs = p.GetDuration("trailUs"),
            Velocity = new(0, -10), SeedStreamKey = "event-burst", Layer = OverlayLayer.AboveText
        };
    }
    public override void OnEvent(TimelineEvent evt, EventContext context)
    {
        BurstAnchor? source = anchor switch
        {
            "token" when evt is TokenStarted token => new BurstAnchor.SourceToken(token.TokenId),
            "line" when evt is LinePerformanceStarted line => new BurstAnchor.SourceLine(line.LineId),
            "cursor" when evt is TokenStarted || evt is LinePerformanceStarted { ProgressSource: ProgressSource.Line }
                => new BurstAnchor.BindingCursors(CursorAnchorMode.AdvanceCenter),
            "mark" when evt is ElementVisible { Element: SceneElementIdentity.Mark mark } => new BurstAnchor.TextMark(mark.NodeId),
            _ => null
        };
        if (source is not null) context.Bursts.Queue(new("burst"), descriptor with { Anchor = source });
    }
}
