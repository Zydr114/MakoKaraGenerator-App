using System.Collections.Immutable;
using MakoKaraGenerator.Scripting.Particles.V2;
using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Scripting.Execution;

internal sealed partial class FrameEffectHost
{
    // This finite catalog contains source identities, never final positions or births.
    // The source lease already intersects subscription; OutputScope is an additional gate.
    private EmissionSourceCatalog BuildV2EmissionSources(ActiveEffect active, PureEffectRuntime pure, CancellationToken token)
    {
        if (pure.Prepared.Particles.Continuous.IsEmpty && pure.Prepared.Particles.Bursts.IsEmpty) return EmissionSourceCatalog.Empty;
        var timeline = _timeline ?? throw new EffectOutputException("PureSourceIndexRequired", "粒子声明需要固定源时间线。");
        var subscription = active.Subscription ?? throw new EffectOutputException("PureSourceIndexRequired", "粒子声明缺少固定源订阅。");
        var scope = subscription.OutputScope;
        pure.Source.Indexes.SetQueryCancellation(token);
        var loads = pure.Source.Indexes.Presentations.GetRange(new(0, long.MaxValue), new SourceFilter.Project());
        var outputLoads = loads.Where(load => scope.ContainsOutputBinding(load.Binding)).ToDictionary(static load => load.Binding.BindingId);
        var tracks = outputLoads.Values.Select(load => StableIdentities.CursorTrack(load.Binding.BranchId, load.Binding.ProgramId)).Distinct().ToArray();
        List<EmissionSourceInterval> intervals = [];
        List<EmissionBurstAnchor> bursts = [];
        foreach (var plan in pure.Prepared.Particles.Continuous)
        {
            foreach (var track in tracks)
                foreach (var segment in pure.Source.Indexes.Cursors.GetSegments(track, new(0, long.MaxValue)))
                {
                    token.ThrowIfCancellationRequested();
                    if (!Allowed(segment.Identity)) continue;
                    Add(new(plan.Id, new ParticleHead.Normal(track, segment.Identity.BindingId),
                        new ParticleSource.Cursor(track, segment.Identity), segment.ActiveRange));
                }
            if (plan.Bridge.Mode != TransitionMode.Bridge || plan.Bridge.DurationUs == 0 || plan.Bridge.GapLimitUs == 0) continue;
            foreach (var edge in timeline.CursorTransitions)
            {
                token.ThrowIfCancellationRequested();
                if (edge.GapUs <= 0 || edge.GapUs > plan.Bridge.GapLimitUs || !subscription.Matches(edge)
                    || !scope.Matches(edge) || !Allowed(edge.From) || !Allowed(edge.To)) continue;
                long fromTime = edge.From.TimeRange.EndUs - 1, toTime = edge.To.TimeRange.StartUs;
                if (!outputLoads[edge.From.BindingId].Contains(fromTime) || !outputLoads[edge.To.BindingId].Contains(toTime)) continue;
                long start = toTime - Math.Min(edge.GapUs, plan.Bridge.DurationUs);
                var source = new ParticleSource.Bridge(edge, new(start, toTime));
                Add(new(plan.Id, new ParticleHead.Bridge(source), source, source.FlightRange));
            }
        }
        var events = pure.Source.Timeline.Events.ToDictionary(static item => item.EventId);
        for (int ordinal = 0; ordinal < pure.Prepared.Particles.Bursts.Length; ordinal++)
        {
            var plan = pure.Prepared.Particles.Bursts[ordinal];
            foreach (var row in plan.Rows)
            {
                token.ThrowIfCancellationRequested();
                if (!events.TryGetValue(row.EventId, out var evt) || !subscription.Matches(evt)
                    || pure.Source.Timeline.Ordinal(plan.Selector, row.EventId) is null)
                    throw new EffectOutputException("BurstEventOutsideSubscription", "Burst 行不属于声明 selector 和固定源订阅。");
                foreach (var source in ResolveSources(row.Anchor, evt.TimeUs))
                {
                    token.ThrowIfCancellationRequested(); CheckCapacity();
                    bursts.Add(new(plan.Id, ordinal, evt, source));
                }
            }
        }
        return new([.. intervals], [.. bursts]);

        bool Allowed(CursorIdentity identity) => outputLoads.ContainsKey(identity.BindingId)
            && (identity.TokenId is { } id ? scope.TokenIds.Contains(id)
                : active.Definition.Binding is not EffectBinding.Tokens && scope.LineIds.Contains(identity.LineId));
        void CheckCapacity()
        {
            if ((long)intervals.Count + bursts.Count >= ParticleLimits.MaximumSourceIntervals)
                throw new EffectOutputException("ParticlePrepareBudgetExceeded", "发射源区段和 Burst 锚点总数超过固定准备预算。");
        }
        void Add(EmissionSourceInterval interval) { CheckCapacity(); intervals.Add(interval); }

        ImmutableArray<ParticleSource> ResolveSources(BurstAnchor anchor, long timeUs)
        {
            var geometry = pure.Source.Indexes.Layout.GetBaseAt(timeUs, new SourceFilter.Project());
            var visible = geometry.Lines.Where(scope.ContainsOutputBinding).Select(static line => line.BindingId).ToHashSet();
            IEnumerable<ParticleSource> sources;
            switch (anchor)
            {
                case BurstAnchor.BindingCursors cursors:
                    return [.. pure.Source.Indexes.Cursors.GetBaseAt(timeUs, new SourceFilter.Project(), cursors.Mode)
                        .Where(cursor => Allowed(cursor.Identity)).OrderBy(static cursor => cursor.TrackId.Value, StringComparer.Ordinal)
                        .ThenBy(static cursor => cursor.BindingId.Value, StringComparer.Ordinal)
                        .Select(static cursor => (ParticleSource)new ParticleSource.Cursor(cursor.TrackId, cursor.Identity))];
                case BurstAnchor.SourceToken selected:
                    if (!Content.Tokens.Any(item => item.Id == selected.Id)) Unknown();
                    if (!scope.TokenIds.Contains(selected.Id)) Outside();
                    sources = geometry.Tokens.Where(item => item.WithRuby.TokenId == selected.Id && visible.Contains(item.WithRuby.BindingId))
                        .Select(static item => (ParticleSource)new ParticleSource.Token(item.WithRuby));
                    break;
                case BurstAnchor.SourceLine selected:
                    if (!Content.Lines.Any(item => item.Id == selected.Id)) Unknown();
                    if (active.Definition.Binding is EffectBinding.Tokens || !scope.LineIds.Contains(selected.Id)) Outside();
                    sources = geometry.LineGeometry.Where(item => item.Target.LineId == selected.Id && visible.Contains(item.Target.BindingId))
                        .Select(static item => (ParticleSource)new ParticleSource.Line(item.Target));
                    break;
                case BurstAnchor.TextMark selected:
                    if (!_sourceMarkStyles.ContainsKey(selected.Id)) Unknown();
                    if (!scope.AllowsMark(selected.Id)) Outside();
                    sources = geometry.MarkGeometry.Where(item => item.Target.NodeId == selected.Id && visible.Contains(item.Target.BindingId))
                        .Select(static item => (ParticleSource)new ParticleSource.TextMark(item.Target));
                    break;
                default: throw new EffectOutputException("InvalidBurst", "未知的 Burst 锚点类型。");
            }
            var resolved = sources.OrderBy(static item => item.BindingId.Value, StringComparer.Ordinal).ToImmutableArray();
            if (resolved.IsEmpty) throw new EffectOutputException("BurstAnchorUnavailable", "Burst 事件时刻没有真实输出装载锚点。");
            return resolved;
        }
        static void Unknown() => throw new EffectOutputException("UnknownBurstSource", "Burst 指定的源身份不存在。");
        static void Outside() => throw new EffectOutputException("BurstTargetOutsideBinding", "Burst 的源订阅不能扩大输出绑定。");
    }
}
