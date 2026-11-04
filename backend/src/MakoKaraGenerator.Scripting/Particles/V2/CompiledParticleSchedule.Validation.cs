using System.Collections.Immutable;
using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Scripting.Particles.V2;

public sealed partial class CompiledParticleSchedule
{
    private static void ValidatePlan(ParticleSchedule plan, EmissionSourceCatalog sources, ParticleOwner owner,
        ParticlePrepareBudget budget, CancellationToken token)
    {
        if (plan.Continuous.IsDefault || plan.Bursts.IsDefault || sources.Continuous.IsDefault || sources.Bursts.IsDefault)
            throw Invalid(owner, "粒子计划与来源目录不能使用 default 集合。");
        budget.Reserve(owner, emitters: checked(plan.Continuous.Length + plan.Bursts.Length),
            intervals: checked(sources.Continuous.Length + sources.Bursts.Length));
        budget.ReserveBytes(owner, checked((long)sources.Continuous.Length * 1536 + (long)sources.Bursts.Length * 2048 + 16_384));
        HashSet<EmitterId> emitters = [];
        foreach (var emitter in plan.Continuous)
        {
            token.ThrowIfCancellationRequested();
            if (emitter is null || !emitter.Id.IsValid || !emitters.Add(emitter.Id) || emitter.Rate is null
                || emitter.Rate.Segments.IsDefault || emitter.Rate.EnabledRanges.IsDefault || !Enum.IsDefined(emitter.AnchorMode)
                || emitter.Bridge is null || !Enum.IsDefined(emitter.Bridge.Mode) || emitter.Bridge.DurationUs < 0
                || emitter.Bridge.Mode == TransitionMode.Bridge && emitter.Bridge.DurationUs == 0
                || emitter.Bridge.GapLimitUs < 0 || emitter.Bridge.Curve is null)
                throw Invalid(owner, "连续计划的发射器、时间轨或桥接声明无效。");
            ValidateSpec(emitter.Particle, owner);
            budget.Reserve(owner, rateEntries: checked(emitter.Rate.Segments.Length + emitter.Rate.EnabledRanges.Length));
            budget.ReserveBytes(owner, checked((long)emitter.Rate.Segments.Length * 4096 + (long)emitter.Rate.EnabledRanges.Length * 64));
            long previous = -1;
            foreach (var segment in emitter.Rate.Segments)
            {
                token.ThrowIfCancellationRequested();
                if (segment is null || !segment.Range.IsValid || segment.Range.StartUs < previous
                    || !double.IsFinite(segment.StartPerSecond) || !double.IsFinite(segment.EndPerSecond)
                    || segment.StartPerSecond < 0 || segment.EndPerSecond < 0)
                    throw owner.Failure("PrepareParticles", 0, "InvalidParticleRate", "速率段必须有序、不重叠、有限且非负。");
                previous = segment.Range.EndUs;
            }
            ValidateRanges(emitter.Rate.EnabledRanges, owner, token);
        }
        int declaredRows = 0;
        foreach (var emitter in plan.Bursts)
        {
            token.ThrowIfCancellationRequested();
            if (emitter is null || !emitter.Id.IsValid || !emitters.Add(emitter.Id) || emitter.Selector is null || emitter.Rows.IsDefault)
                throw Invalid(owner, "Burst 计划需要唯一发射器、selector 和明确行集合。");
            declaredRows = checked(declaredRows + emitter.Rows.Length); HashSet<EventId> events = [];
            foreach (var row in emitter.Rows)
            {
                token.ThrowIfCancellationRequested();
                if (row is null || !row.EventId.IsValid || row.Count < 0 || row.Anchor is null || !row.Anchor.IsValid || !events.Add(row.EventId))
                    throw Invalid(owner, "Burst 行需要真实唯一事件、非负数量和明确锚点。");
                ValidateSpec(row.Particle, owner);
            }
        }
        budget.Reserve(owner, burstRows: Math.Max(declaredRows, sources.Bursts.Length));
        budget.ReserveBytes(owner, checked((long)declaredRows * 256));
        var continuous = plan.Continuous.ToDictionary(static row => row.Id);
        foreach (var interval in sources.Continuous)
        {
            token.ThrowIfCancellationRequested();
            if (interval is null || interval.Head is null || interval.Source is null || !interval.Range.IsValid)
                throw Invalid(owner, "来源目录不能包含空行或无效区间。");
            if (!continuous.TryGetValue(interval.EmitterId, out var emitter)) throw Invalid(owner, "来源引用未知连续发射器。");
            ValidateSource(interval, emitter, owner);
            budget.ReserveBytes(owner, checked((ParticleIdentityEncoding.HeadCharacters(interval.Head)
                + ParticleIdentityEncoding.SourceCharacters(interval.Source)) * 8));
        }
        foreach (var anchor in sources.Bursts)
        {
            token.ThrowIfCancellationRequested();
            if (anchor is null || anchor.Event is null || anchor.Source is null)
                throw Invalid(owner, "Burst 来源不能包含空行、事件或锚点。");
            budget.ReserveBytes(owner, checked(ParticleIdentityEncoding.SourceCharacters(anchor.Source) * 8));
        }
    }

    private static void ValidateSpec(ParticleSpec? spec, ParticleOwner owner)
    {
        if (spec is null) throw Invalid(owner, "粒子描述缺失。");
        try { spec.Validate(); }
        catch (EffectOutputException error) { throw owner.Failure("PrepareParticles", 0, error.Code, error.Message); }
    }

    private static void ValidateRanges(ImmutableArray<TimeRange> ranges, ParticleOwner owner, CancellationToken token)
    {
        long previous = -1;
        foreach (var range in ranges)
        {
            token.ThrowIfCancellationRequested();
            if (!range.IsValid || range.StartUs < previous) throw Invalid(owner, "启用窗须有序且不重叠。");
            previous = range.EndUs;
        }
    }

    private static void ValidateSource(EmissionSourceInterval interval, CursorEmissionPlan plan, ParticleOwner owner)
    {
        if (interval is null || interval.Source is null || interval.Head is null || !interval.Range.IsValid)
            throw Invalid(owner, "连续来源必须是有效真实半开区段。");
        bool valid = (interval.Head, interval.Source) switch
        {
            (ParticleHead.Normal head, ParticleSource.Cursor cursor) => head.Track.IsValid && head.Binding.IsValid
                && head.Track == cursor.Track && head.Binding == cursor.BindingId
                && interval.Range.StartUs >= cursor.Identity.TimeRange.StartUs && interval.Range.EndUs <= cursor.Identity.TimeRange.EndUs,
            (ParticleHead.Bridge head, ParticleSource.Bridge bridge) => head.Source == bridge && plan.Bridge.Mode == TransitionMode.Bridge
                && interval.Range.StartUs >= bridge.FlightRange.StartUs && interval.Range.EndUs <= bridge.FlightRange.EndUs
                && bridge.To.TimeRange.StartUs - bridge.From.TimeRange.EndUs <= plan.Bridge.GapLimitUs
                && bridge.FlightRange.DurationUs <= plan.Bridge.DurationUs,
            _ => false,
        };
        if (!valid) throw Invalid(owner, "配额 head 与真实来源、装载或存在窗不一致。");
    }

    private static Burst[] CompileBursts(ParticleSchedule plan, EmissionSourceCatalog sources, ParticleOwner owner, CancellationToken token)
    {
        List<Burst> result = []; HashSet<(int, EventId, ParticleSource)> seen = [];
        var rows = plan.Bursts.Select(static emitter => emitter.Rows.ToDictionary(static row => row.EventId)).ToArray();
        foreach (var anchor in sources.Bursts)
        {
            token.ThrowIfCancellationRequested();
            if (anchor is null || anchor.Event is null || anchor.Source is null || anchor.Event.TimeUs < 0
                || anchor.PlanOrdinal < 0 || anchor.PlanOrdinal >= plan.Bursts.Length)
                throw Invalid(owner, "Burst 来源不属于固定计划或真实非负事件。");
            var emitter = plan.Bursts[anchor.PlanOrdinal];
            rows[anchor.PlanOrdinal].TryGetValue(anchor.Event.EventId, out var row);
            if (emitter.Id != anchor.EmitterId || row is null || anchor.Event.Kind != emitter.Selector.Kind
                || !seen.Add((anchor.PlanOrdinal, anchor.Event.EventId, anchor.Source)) || !Matches(row.Anchor, anchor.Source))
                throw Invalid(owner, "Burst 锚点、事件或计划身份不匹配，或重复锚点。");
            if (row.Anchor is BurstAnchor.BindingCursors && anchor.Source is ParticleSource.Cursor cursor
                && !cursor.Identity.TimeRange.Contains(anchor.Event.TimeUs))
                throw Invalid(owner, "Burst 事件时刻不存在该 Current 身份。");
            if (row.Count > 0 && anchor.Event.TimeUs > long.MaxValue - row.Particle.LifetimeUs)
                throw owner.Failure("PrepareParticles", anchor.Event.TimeUs, "ParticleTimeOrIdentityOverflow", "Burst 寿命末端超过 Int64。");
            result.Add(new(emitter.Id, anchor.PlanOrdinal, anchor.Event, anchor.Source, row,
                row.Anchor is BurstAnchor.BindingCursors cursors ? cursors.Mode : CursorAnchorMode.AdvanceCenter));
        }
        return result.OrderBy(static row => row.Event, TimelineEvent.Comparer)
            .ThenBy(static row => row.Emitter.Value, StringComparer.Ordinal).ThenBy(static row => row.PlanOrdinal)
            .ThenBy(static row => ParticleIdentityEncoding.Source(row.Source), StringComparer.Ordinal).ToArray();
    }

    private static bool Matches(BurstAnchor anchor, ParticleSource source) => (anchor, source) switch
    {
        (BurstAnchor.SourceToken expected, ParticleSource.Token actual) => expected.Id == actual.Target.TokenId,
        (BurstAnchor.SourceLine expected, ParticleSource.Line actual) => expected.Id == actual.Target.LineId,
        (BurstAnchor.TextMark expected, ParticleSource.TextMark actual) => expected.Id == actual.Target.NodeId,
        (BurstAnchor.BindingCursors, ParticleSource.Cursor) => true,
        _ => false,
    };
}
