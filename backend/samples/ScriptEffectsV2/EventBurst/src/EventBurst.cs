using System;
using System.Collections.Immutable;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.V2;

public sealed record EventBurstData : PreparedEffect;

public sealed class EventBurst : IPureKaraokeEffect
{
    public PreparedEffect Declare(DeclareContext source)
    {
        var p = source.Parameters;
        string mode = p.GetEnum("anchor");
        var first = source.Timeline.Nth(new(TimelineEventKind.TokenStarted, new TimelineScope.Project()), 0) as TokenStarted;
        TimelineScope? scope = p.GetEnum("scope") switch
        {
            "firstBranch" => source.Content.Branches.Length == 0 ? null : new TimelineScope.Branch(source.Content.Branches[0].Id),
            "firstOccurrence" => first is null ? null : new TimelineScope.Occurrence(first.OccurrenceId),
            _ => new TimelineScope.Project(),
        };
        if (scope is null) return new EventBurstData();
        string text = p.GetText("matchText");
        string? sourceText = text.Length == 0 ? null : text;
        int count = (int)p.GetInteger("count"), every = (int)p.GetInteger("everyNth");
        long minimum = p.GetDuration("minimumDurationUs");
        var particle = new ParticleSpec
        {
            LifetimeUs = p.GetDuration("lifeUs"), SizePx = p.GetNumber("sizePx"), Color = p.GetColor("color"),
            SpreadPx = p.GetNumber("spreadPx"), Velocity = new(0, -10),
            SeedStreamKey = "v2-event-burst", Layer = OverlayLayer.AboveText,
        };
        var plans = ImmutableArray.CreateBuilder<EventBurstPlan>();
        if (mode is "token" or "cursor")
            plans.Add(BuildPlan(source, new("token-burst"), new(TimelineEventKind.TokenStarted, scope, sourceText), mode, minimum, every, count, particle));
        if (mode is "line" or "cursor")
            plans.Add(BuildPlan(source, new("line-burst"), new(TimelineEventKind.LinePerformanceStarted, scope, sourceText), mode, minimum, every, count, particle));
        if (mode == "mark")
            plans.Add(BuildPlan(source, new("mark-burst"), new(TimelineEventKind.ElementVisible, scope), mode, minimum, every, count, particle));
        return new EventBurstData { Particles = new([], plans.ToImmutable()) };
    }

    private static EventBurstPlan BuildPlan(DeclareContext source, EmitterId id, TimelineSelector selector,
        string mode, long minimum, int every, int count, ParticleSpec particle)
    {
        var rows = ImmutableArray.CreateBuilder<BurstEventRow>();
        for (int index = 0; index < source.Timeline.Count(selector); index++)
        {
            var item = source.Timeline.Nth(selector, index);
            BurstAnchor? anchor = item switch
            {
                TokenStarted token when token.Source.TimeRange is { } range && range.DurationUs >= minimum
                    && (source.Timeline.Ordinal(selector, token.OccurrenceId) ?? 0) % every == 0
                    => mode == "token" ? new BurstAnchor.SourceToken(token.TokenId)
                        : new BurstAnchor.BindingCursors(CursorAnchorMode.AdvanceCenter),
                LinePerformanceStarted line when line.PerformanceRange.DurationUs >= minimum
                    && (mode == "line" || line.ProgressSource == ProgressSource.Line)
                    => mode == "line" ? new BurstAnchor.SourceLine(line.LineId)
                        : new BurstAnchor.BindingCursors(CursorAnchorMode.AdvanceCenter),
                ElementVisible { Element: SceneElementIdentity.Mark mark } => new BurstAnchor.TextMark(mark.NodeId),
                _ => null,
            };
            if (anchor is not null) rows.Add(new(item!.EventId, count, anchor, particle));
        }
        return new(id, selector, rows.ToImmutable());
    }

    public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output) { }
}
