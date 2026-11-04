using System;
using System.Collections.Immutable;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.V2;

public sealed record CursorTrailData : PreparedEffect;

// Rates describe song time. No frame callback accumulates an emission remainder.
public sealed class CursorTrail : IPureKaraokeEffect
{
    public PreparedEffect Declare(DeclareContext source)
    {
        var p = source.Parameters;
        long end = 1;
        foreach (var line in source.Content.Lines)
            if (line.TimeRange is { } range) end = Math.Max(end, range.EndUs);
        foreach (var token in source.Content.Tokens)
            if (token.TimeRange is { } range) end = Math.Max(end, range.EndUs);
        double rate = p.GetNumber("rate"), finalRate = p.GetNumber("finalRate");
        long split = Math.Min(p.GetDuration("splitUs"), end);
        ImmutableArray<RateSegment> segments = p.GetEnum("rateShape") switch
        {
            "linear" => [new(new(0, end), rate, finalRate)],
            "steps" when split < end => [new(new(0, split), rate, rate), new(new(split, end), finalRate, finalRate)],
            _ => [new(new(0, end), rate, rate)],
        };
        var particle = new ParticleSpec
        {
            LifetimeUs = p.GetDuration("lifeUs"), SizePx = p.GetNumber("sizePx"), Color = p.GetColor("color"),
            SpreadPx = p.GetNumber("spreadPx"), TrailDurationUs = p.GetDuration("trailUs"),
            SeedStreamKey = "v2-cursor-trail", Layer = OverlayLayer.AboveText,
            Motion = new() { Opacity = new(1, 0, new CurveValue.Easing(EasingKind.Linear)) },
        };
        var bridge = p.GetEnum("transition") == "bridge"
            ? new BridgePolicy(TransitionMode.Bridge, p.GetDuration("bridgeUs"), p.GetDuration("gapLimitUs"),
                new CurveValue.Easing(EasingKind.Linear))
            : BridgePolicy.Jump;
        return new CursorTrailData
        {
            Particles = new([new(new("cursor"), new(segments, [new(0, end)]), particle,
                CursorAnchorMode.AdvanceCenter, bridge)], []),
        };
    }

    public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output) { }
}
