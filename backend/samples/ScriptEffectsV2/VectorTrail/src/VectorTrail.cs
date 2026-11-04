using System;
using System.Collections.Immutable;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.V2;

public sealed record VectorTrailData(string Mode, bool ShowBirths, ImageHandle Image) : PreparedEffect;

public sealed class VectorTrail : IPureKaraokeEffect, IPureVisualEffect
{
    public PreparedEffect Declare(DeclareContext source)
    {
        var p = source.Parameters;
        long end = 1;
        foreach (var line in source.Content.Lines)
            if (line.TimeRange is { } range) end = Math.Max(end, range.EndUs);
        foreach (var token in source.Content.Tokens)
            if (token.TimeRange is { } range) end = Math.Max(end, range.EndUs);
        var particle = new ParticleSpec
        {
            LifetimeUs = p.GetDuration("lifeUs"), SizePx = p.GetNumber("sizePx"), Color = p.GetColor("color"),
            SpreadPx = p.GetNumber("spreadPx"), TrailDurationUs = p.GetDuration("trailUs"),
            Velocity = new(40, -25), Rendering = ParticleRenderMode.Custom,
            SeedStreamKey = "v2-vector-trail", Layer = OverlayLayer.AboveText,
            Motion = new() { Acceleration = new(0, 20), Opacity = new(1, 0, new CurveValue.Easing(EasingKind.Linear)) },
        };
        var bridge = p.GetEnum("transition") == "bridge"
            ? new BridgePolicy(TransitionMode.Bridge, p.GetDuration("bridgeUs"), p.GetDuration("gapLimitUs"),
                new CurveValue.Easing(EasingKind.Linear))
            : BridgePolicy.Jump;
        double rate = p.GetNumber("rate");
        return new VectorTrailData(p.GetEnum("mode"), p.GetBoolean("showBirths"), source.Resources.GetImage("assets/spark.png"))
        {
            Particles = new([new(new("cursor"), new([new(new(0, end), rate, rate)], [new(0, end)]),
                particle, CursorAnchorMode.AdvanceCenter, bridge)], []),
            GeometrySampling = p.GetEnum("sampling") == "grid"
                ? new(GeometrySamplingMode.Continuous120Hz, [new(0, end)], []) : GeometrySamplingPolicy.Exact,
            VisualOutputs = [new(new("heads"), VisualPrimitiveKind.Dots, OverlayLayer.AboveText),
                new(new("births"), VisualPrimitiveKind.Dots, OverlayLayer.BehindText),
                new(new("segments"), VisualPrimitiveKind.Segments, OverlayLayer.AboveText),
                new(new("ribbons"), VisualPrimitiveKind.Trails, OverlayLayer.AboveText),
                new(new("sprites"), VisualPrimitiveKind.Sprites, OverlayLayer.AboveText)],
        };
    }

    public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output) { }

    public void EvaluateVisuals(in VisualFrame frame, PreparedEffect data, VisualOutput output)
    {
        var settings = (VectorTrailData)data;
        var heads = ImmutableArray.CreateBuilder<Dot>();
        var births = ImmutableArray.CreateBuilder<Dot>();
        var segments = ImmutableArray.CreateBuilder<Segment>();
        var ribbons = ImmutableArray.CreateBuilder<Trail>();
        var sprites = ImmutableArray.CreateBuilder<Sprite>();
        // Births is the complete fixed five-second window, including particles that have already died.
        if (settings.ShowBirths)
            foreach (var birth in frame.Particles.Births.Births)
                births.Add(new(birth.BirthPosition, 1, new(0.2, 0.6, 1, 0.35)));
        foreach (var particle in frame.Particles.Alive)
        {
            if (particle.SizePx <= 0 || particle.Color.Alpha <= 0) continue;
            heads.Add(new(particle.Position, particle.SizePx / 2, particle.Color));
            var trail = frame.Particles.GetTrail(particle.Id);
            if (settings.Mode == "segments")
            {
                for (int i = 1; i < trail.Length; i++)
                    segments.Add(new(trail[i - 1].Position, trail[i].Position, particle.SizePx, particle.Color));
            }
            else if (settings.Mode == "ribbon" && trail.Length >= 2)
            {
                var points = ImmutableArray.CreateBuilder<TrailPoint>();
                long duration = trail[^1].TimeUs - trail[0].TimeUs;
                foreach (var point in trail)
                {
                    double weight = duration == 0 ? 1 : (double)(point.TimeUs - trail[0].TimeUs) / duration;
                    var color = particle.Color;
                    points.Add(new(point.Position, particle.SizePx * weight,
                        new(color.Red, color.Green, color.Blue, color.Alpha * weight)));
                }
                ribbons.Add(new(points.ToImmutable()));
            }
            else if (settings.Mode == "sprites")
            {
                double size = particle.SizePx;
                // Full affine includes a shear and a reflection, followed by one world translation.
                sprites.Add(new(settings.Image,
                    new Affine2(-size, size * 0.25, 0, size, particle.Position.X + size / 2, particle.Position.Y - size / 2),
                    particle.Color));
            }
        }
        output.DrawDots(new("heads"), heads.ToImmutable(), OverlayLayer.AboveText);
        output.DrawDots(new("births"), births.ToImmutable(), OverlayLayer.BehindText);
        output.DrawSegments(new("segments"), segments.ToImmutable(), OverlayLayer.AboveText);
        output.DrawTrails(new("ribbons"), ribbons.ToImmutable(), OverlayLayer.AboveText);
        output.DrawSprites(new("sprites"), sprites.ToImmutable(), OverlayLayer.AboveText);
    }
}
