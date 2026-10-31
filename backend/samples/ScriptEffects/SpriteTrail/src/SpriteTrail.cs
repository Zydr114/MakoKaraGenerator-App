using System;
using System.Collections.Immutable;
using MakoKaraGenerator.Scripting;

/// <summary>宿主真实粒子及固定拖尾以包图片纯绘制，不读取机器路径或 native 图像。</summary>
public sealed class SpriteTrail : EffectBase
{
    private CursorEmitterDescriptor descriptor = null!;
    private ImageHandle image = null!;
    public override void Initialize(EffectInitializationContext context)
    {
        var p = context.Parameters; image = p.GetAsset("image");
        descriptor = new()
        {
            AnchorMode = CursorAnchorMode.AdvanceCenter, RatePerSecond = p.GetNumber("rate"),
            LifetimeUs = p.GetDuration("lifeUs"), SizePx = p.GetNumber("sizePx"),
            SpreadPx = p.GetNumber("spreadPx"), Color = p.GetColor("color"),
            TransitionMode = p.GetEnum("transition") == "bridge" ? TransitionMode.Bridge : TransitionMode.Jump,
            BridgeDurationUs = p.GetDuration("bridgeUs"), BridgeGapLimitUs = p.GetDuration("bridgeGapLimitUs"),
            TrailDurationUs = p.GetDuration("trailUs"), Velocity = new(35, -20),
            Rendering = ParticleRenderMode.Custom, SeedStreamKey = "sprite-trail",
            Motion = new() { Opacity = new(1, 0, new CurveValue.Easing(EasingKind.Linear)) }
        };
    }
    public override void EvaluateEmitters(EmissionFrameContext frame, EmitterWriter output)
        => output.Set(new("cursor"), descriptor);
    public override void EvaluateVisuals(VisualFrameContext frame, VisualWriter output)
    {
        var sprites = ImmutableArray.CreateBuilder<Sprite>();
        foreach (var particle in frame.Particles)
        {
            frame.CancellationToken.ThrowIfCancellationRequested();
            if (particle.SizePx <= 0) continue;
            double angle = (frame.TimeUs - particle.BirthUs) / 500_000d;
            foreach (var sample in particle.Trail)
            {
                double weight = 1 - (frame.TimeUs - sample.TimeUs) / (double)Math.Max(1, descriptor.TrailDurationUs);
                if (weight <= 0) continue;
                var color = particle.Color;
                Add(sample.Position, particle.SizePx * .6, angle, new(color.Red, color.Green, color.Blue, color.Alpha * weight * .25));
            }
            Add(particle.Position, particle.SizePx, angle, particle.Color);
        }
        output.Overlay.DrawSprites(new("sparkles"), sprites.ToImmutable(), OverlayLayer.AboveText);
        void Add(Point2 center, double size, double angle, ColorRgba tint)
        {
            double a = Math.Cos(angle) * size, b = Math.Sin(angle) * size, c = -b, d = a;
            sprites.Add(new(image, new(a, b, c, d, center.X - (a + c) / 2, center.Y - (b + d) / 2), tint));
        }
    }
}
