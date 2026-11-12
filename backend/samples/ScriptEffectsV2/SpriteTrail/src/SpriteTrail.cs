using System;
using System.Collections.Immutable;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.V2;

public sealed record SpriteTrailData(ImageHandle Image, long TrailUs) : PreparedEffect;

/// <summary>宿主解析播放头粒子的出生、运动与拖尾；纯视觉阶段以包内图片绘制旋转星芒和逐样本淡出的拖尾。</summary>
public sealed class SpriteTrail : IPureKaraokeEffect<SpriteTrailData>, IPureVisualEffect<SpriteTrailData>
{
    public SpriteTrailData Declare(DeclareContext source)
    {
        var p = source.Parameters;
        long end = 1;
        foreach (var line in source.Content.Lines)
            if (line.TimeRange is { } range) end = Math.Max(end, range.EndUs);
        foreach (var token in source.Content.Tokens)
            if (token.TimeRange is { } range) end = Math.Max(end, range.EndUs);
        long trailUs = p.GetDuration("trailUs");
        var particle = new ParticleSpec
        {
            LifetimeUs = p.GetDuration("lifeUs"), SizePx = p.GetNumber("sizePx"), Color = p.GetColor("color"),
            SpreadPx = p.GetNumber("spreadPx"), TrailDurationUs = trailUs, Velocity = new(35, -20),
            Rendering = ParticleRenderMode.Custom, SeedStreamKey = "v2-sprite-trail", Layer = OverlayLayer.AboveText,
            Motion = new() { Opacity = new(1, 0, new CurveValue.Easing(EasingKind.Linear)) },
        };
        var bridge = p.GetEnum("transition") == "bridge"
            ? new BridgePolicy(TransitionMode.Bridge, p.GetDuration("bridgeUs"), p.GetDuration("bridgeGapLimitUs"),
                new CurveValue.Easing(EasingKind.Linear))
            : BridgePolicy.Jump;
        double rate = p.GetNumber("rate");
        return new SpriteTrailData(p.GetAsset("image"), trailUs)
        {
            Particles = new([new(new("cursor"), new([new(new(0, end), rate, rate)], [new(0, end)]),
                particle, CursorAnchorMode.AdvanceCenter, bridge)], []),
            VisualOutputs = [new(new("sparkles"), VisualPrimitiveKind.Sprites, OverlayLayer.AboveText)],
        };
    }

    public void Evaluate(in TextFrame frame, SpriteTrailData data, TextOutput output) { }

    public void EvaluateVisuals(in VisualFrame frame, SpriteTrailData settings, VisualOutput output)
    {
        var sprites = ImmutableArray.CreateBuilder<Sprite>();
        long now = frame.TimeUs;
        foreach (var particle in frame.Particles.Alive)
        {
            if (particle.SizePx <= 0 || particle.Color.Alpha <= 0) continue;
            // 旋转角只由歌曲时间与出生时刻决定，任意时刻求值结果相同。
            double angle = (now - particle.BirthUs) / 500_000d;
            var color = particle.Color;
            foreach (var sample in frame.Particles.GetTrail(particle.Id))
            {
                double weight = 1 - (now - sample.TimeUs) / (double)Math.Max(1, settings.TrailUs);
                if (weight <= 0) continue;
                Add(sample.Position, particle.SizePx * .6, angle, new(color.Red, color.Green, color.Blue, color.Alpha * weight * .25));
            }
            Add(particle.Position, particle.SizePx, angle, color);
        }
        output.DrawSprites(new("sparkles"), sprites.ToImmutable(), OverlayLayer.AboveText);

        void Add(Point2 center, double size, double turn, ColorRgba tint)
        {
            double a = Math.Cos(turn) * size, b = Math.Sin(turn) * size, c = -b, d = a;
            sprites.Add(new(settings.Image, new Affine2(a, b, c, d, center.X - (a + c) / 2, center.Y - (b + d) / 2), tint));
        }
    }
}
