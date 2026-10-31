using System.Collections.Immutable;
using System.Linq;
using MakoKaraGenerator.Scripting;

/// <summary>宿主固定粒子模拟，纯视觉求值选择圆点、线段或变宽带状拖尾。</summary>
public sealed class VectorTrail : EffectBase
{
    private CursorEmitterDescriptor descriptor = null!;
    private string mode = "ribbon";
    public override void Initialize(EffectInitializationContext context)
    {
        var p = context.Parameters; mode = p.GetEnum("mode");
        descriptor = new()
        {
            AnchorMode = CursorAnchorMode.AdvanceCenter, RatePerSecond = p.GetNumber("rate"),
            LifetimeUs = p.GetDuration("lifeUs"), SizePx = p.GetNumber("sizePx"),
            SpreadPx = p.GetNumber("spreadPx"), Color = p.GetColor("color"),
            TransitionMode = p.GetEnum("transition") == "bridge" ? TransitionMode.Bridge : TransitionMode.Jump,
            BridgeDurationUs = p.GetDuration("bridgeUs"), BridgeGapLimitUs = p.GetDuration("bridgeGapLimitUs"),
            TrailDurationUs = p.GetDuration("trailUs"), Velocity = new(40, -25),
            Rendering = mode == "dots" ? ParticleRenderMode.Dots : ParticleRenderMode.Custom,
            SeedStreamKey = "vector-trail", Motion = new() { Opacity = new(1, 0, new CurveValue.Easing(EasingKind.Linear)) }
        };
    }
    public override void EvaluateEmitters(EmissionFrameContext frame, EmitterWriter output)
        => output.Set(new("cursor"), descriptor);
    public override void EvaluateVisuals(VisualFrameContext frame, VisualWriter output)
    {
        if (mode == "dots") return;
        var heads = ImmutableArray.CreateBuilder<Dot>();
        var trails = ImmutableArray.CreateBuilder<Trail>();
        var segments = ImmutableArray.CreateBuilder<Segment>();
        foreach (var particle in frame.Particles)
        {
            frame.CancellationToken.ThrowIfCancellationRequested();
            if (particle.SizePx > 0 && particle.Color.Alpha > 0) heads.Add(new(particle.Position, particle.SizePx / 2, particle.Color));
            if (particle.Trail.Length < 2) continue;
            if (mode == "segments")
            {
                for (int index = 1; index < particle.Trail.Length; index++)
                    if (particle.SizePx > 0) segments.Add(new(particle.Trail[index - 1].Position, particle.Trail[index].Position, particle.SizePx, particle.Color));
            }
            else
            {
                long start = particle.Trail[0].TimeUs, duration = particle.Trail[^1].TimeUs - start;
                trails.Add(new([.. particle.Trail.Select(sample =>
                {
                    double weight = duration == 0 ? 1 : (sample.TimeUs - start) / (double)duration;
                    var color = particle.Color;
                    return new TrailPoint(sample.Position, particle.SizePx * weight, new(color.Red, color.Green, color.Blue, color.Alpha * weight));
                })]));
            }
        }
        output.Overlay.DrawDots(new("heads"), heads.ToImmutable(), OverlayLayer.AboveText);
        if (mode == "segments") output.Overlay.DrawSegments(new("path"), segments.ToImmutable(), OverlayLayer.AboveText);
        else output.Overlay.DrawTrails(new("path"), trails.ToImmutable(), OverlayLayer.AboveText);
    }
}
