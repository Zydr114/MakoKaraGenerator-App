using MakoKaraGenerator.Scripting;

/// <summary>连续光标圆点与固定拖尾；使用真实歌曲时间而非显示器帧数。</summary>
public sealed class CursorTrail : EffectBase
{
    private CursorEmitterDescriptor descriptor = null!;
    public override void Initialize(EffectInitializationContext context)
    {
        var p = context.Parameters;
        descriptor = new()
        {
            AnchorMode = CursorAnchorMode.AdvanceCenter,
            RatePerSecond = p.GetNumber("rate"), LifetimeUs = p.GetDuration("lifeUs"),
            SizePx = p.GetNumber("sizePx"), Color = p.GetColor("color"), SpreadPx = p.GetNumber("spreadPx"),
            TrailDurationUs = p.GetDuration("trailUs"),
            TransitionMode = p.GetEnum("transition") == "bridge" ? TransitionMode.Bridge : TransitionMode.Jump,
            BridgeDurationUs = p.GetDuration("bridgeUs"), BridgeGapLimitUs = p.GetDuration("bridgeGapLimitUs"),
            Layer = OverlayLayer.AboveText, ImageOnlyPolicy = ImageOnlyPolicy.SemanticCursor,
            SeedStreamKey = "cursor-trail",
            Motion = new() { Opacity = new(1, 0, new CurveValue.Easing(EasingKind.Linear)) }
        };
    }
    public override void EvaluateEmitters(EmissionFrameContext frame, EmitterWriter output)
        => output.Set(new("cursor"), descriptor);
}
