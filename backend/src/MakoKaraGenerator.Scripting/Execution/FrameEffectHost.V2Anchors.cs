using System.Collections.Immutable;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting.Particles;
using MakoKaraGenerator.Scripting.Particles.V2;
using MakoKaraGenerator.Scripting.Projection;
using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Scripting.Execution;

internal sealed partial class FrameEffectHost
{
    private readonly V2FinalAnchorMemo _v2AnchorMemo = new();
    private ParticlePrepareBudget _v2PrepareBudget = new();
    private ImmutableArray<long> _v2AnchorBreakpoints;
    private readonly Dictionary<CursorIdentity, ImmutableArray<long>> _v2CursorCuts = [];
    private int _v2CursorCutCount;

    private sealed class V2AnchorRequest(PreparedRenderFrame request, V2FinalAnchorMemo.Transaction memo,
        ParticleQueryBudget particles, CancellationToken token)
    {
        public PreparedRenderFrame Request { get; } = request;
        public V2FinalAnchorMemo.Transaction Memo { get; } = memo;
        public ParticleQueryBudget Particles { get; } = particles;
        public V2AnchorQueryBudget Budget { get; } = new(token);
        public Dictionary<long, PreparedRenderFrame> TextFrames { get; } = [];
    }

    private Point2 ResolveV2Anchor(ActiveEffect owner, ScheduledBirth birth, V2AnchorRequest query,
        RuntimeDiagnosticBuffer diagnostics, CancellationToken token)
    {
        using var timing = query.Budget.Measure();
        if (birth.Source is not ParticleSource.Bridge bridge)
            return ResolveV2Source(owner, birth.Source, birth.BirthUs, birth.AnchorMode, query, diagnostics, token);
        var plan = owner.Pure!.Prepared.Particles.Continuous.Single(item => item.Id == birth.Id.EmitterId);
        var previous = ResolveV2Source(owner, new ParticleSource.Cursor(bridge.Track, bridge.From),
            bridge.From.TimeRange.EndUs - 1, birth.AnchorMode, query, diagnostics, token);
        var next = ResolveV2Source(owner, new ParticleSource.Cursor(bridge.Track, bridge.To),
            bridge.To.TimeRange.StartUs, birth.AnchorMode, query, diagnostics, token);
        return new ResolvedCursorBridge(bridge, previous, next, plan.Bridge.Curve).At(birth.BirthUs);
    }

    private Point2 ResolveV2Source(ActiveEffect owner, ParticleSource source, long timeUs, CursorAnchorMode anchor,
        V2AnchorRequest query, RuntimeDiagnosticBuffer diagnostics, CancellationToken token)
    {
        query.Budget.Endpoint(source, timeUs, query.Request.TimeUs);
        var scene = _geometry.GetSceneFrame(_snapshot, timeUs);
        // No legacy instance is called during supplement queries. Reject only a
        // legacy geometry writer whose real output binding can affect this source.
        foreach (var legacy in _instances.Where(static item => item.Pure is null))
            if (MayWriteV2Anchor(legacy, source, scene))
                throw new EffectOutputException("FinalAnchorLegacyDependencyUnsupported",
                    "最终锚点依赖有状态 v1 文字输出：" + legacy.Definition.Id.Value);
        var (continuous, start, end) = V2AnchorRegion(owner, source, timeUs, scene, token);
        if (!continuous) return At(timeUs, timeUs, timeUs);
        long grid = SemanticSimulationGrid.IndexAtOrBefore(timeUs);
        long left = Math.Max(start, SemanticSimulationGrid.TimeAt(grid));
        long right;
        try { right = Math.Min(end - 1, SemanticSimulationGrid.TimeAt(checked(grid + 1))); }
        catch (OverflowException) { right = end - 1; }
        if (left == right || timeUs == left) return At(left, start, end);
        if (timeUs == right) return At(right, start, end);
        var a = At(left, start, end); var b = At(right, start, end);
        double amount = (timeUs - left) / (double)(right - left);
        return new(a.X * (1 - amount) + b.X * amount, a.Y * (1 - amount) + b.Y * amount);

        Point2 At(long nodeUs, long regionStart, long regionEnd)
        {
            token.ThrowIfCancellationRequested(); query.Budget.Check();
            var key = new V2AnchorNodeKey(source, anchor, regionStart, regionEnd, nodeUs,
                query.Request.OutputMode, query.Request.Target);
            return query.Memo.Read(key, () =>
            {
                if (!query.TextFrames.TryGetValue(nodeUs, out var text))
                {
                    query.Budget.Time(nodeUs);
                    // This call has no particle/visual reference, and keeps every
                    // loaded pure instance and its original Declare result.
                    text = Evaluate(nodeUs, query.Request.OutputMode, query.Request.Target, null, diagnostics, token,
                        pureTextOnly: true, anchorBudget: query.Budget);
                    long bytes = 1024L + text.FinalGeometry.Sum(static item => 768L + item.Clusters.Length * 192L)
                        + 512L * (text.FinalLineGeometry.Length + text.FinalMarkGeometry.Length + text.FinalGroupGeometry.Length);
                    query.Particles.ReserveBytes(owner.Pure!.ParticleOwner!, bytes, query.Request.TimeUs);
                    query.TextFrames.Add(nodeUs, text);
                }
                return V2SourcePoint(source, anchor, text);
            }, bytes => query.Particles.ReserveBytes(owner.Pure!.ParticleOwner!, bytes, query.Request.TimeUs), query.Budget);
        }
    }

    private static Point2 V2SourcePoint(ParticleSource source, CursorAnchorMode anchor, PreparedRenderFrame text)
    {
        switch (source)
        {
            case ParticleSource.Cursor cursor:
                var actual = text.Playback.GetCursors(anchor).SingleOrDefault(item => item.TrackId == cursor.Track && item.Identity == cursor.Identity);
                if (actual is not null) return actual.Position;
                break;
            case ParticleSource.Token token:
                var tokenGeometry = text.FinalGeometry.SingleOrDefault(item => item.WithRuby == token.Target);
                if (tokenGeometry is not null) return Center(tokenGeometry.BaseGeometry);
                break;
            case ParticleSource.Line line:
                var lineGeometry = text.FinalLineGeometry.SingleOrDefault(item => item.Target == line.Target);
                if (lineGeometry is not null) return Center(lineGeometry.BaseGeometry);
                break;
            case ParticleSource.TextMark mark:
                var markGeometry = text.FinalMarkGeometry.SingleOrDefault(item => item.Target == mark.Target);
                if (markGeometry is not null) return Center(markGeometry.Geometry);
                break;
        }
        throw new EffectOutputException("FinalParticleAnchorUnavailable", "真实出生或桥接端点没有匹配的最终文字锚点。");
        static Point2 Center(GeometrySample geometry) => geometry.BaseWorldMatrix.Map(geometry.AdvanceBounds.Center);
    }

    private bool MayWriteV2Anchor(ActiveEffect active, ParticleSource source, SceneFrame scene)
    {
        if (!active.Definition.Capabilities.HasFlag(EffectCapabilities.Transforms)) return false;
        FrameBindingScope scope = new(scene); var binding = active.Definition.Binding;
        TokenFamilyTarget? token = source switch
        {
            ParticleSource.Cursor { Identity.TokenId: { } id } cursor => new(id, cursor.Identity.BindingId),
            ParticleSource.Token value => value.Target, _ => null,
        };
        if (token is not null)
        {
            var frameToken = FrameProjection.Tokens(scene).SingleOrDefault(item => item.WithRuby == token);
            return frameToken is not null && scope.ContainsToken(binding, frameToken);
        }
        if (source is ParticleSource.TextMark mark)
        {
            var frameMark = FrameProjection.Marks(scene, _project.Scene).SingleOrDefault(item => item.Target == mark.Target);
            return frameMark is not null && scope.ContainsMark(binding, frameMark);
        }
        LineTarget? line = source switch
        {
            ParticleSource.Cursor cursor => new(cursor.Identity.LineId, cursor.Identity.BindingId),
            ParticleSource.Line value => value.Target, _ => null,
        };
        var frameLine = FrameProjection.Lines(scene, _project.Scene).SingleOrDefault(item => item.Target == line);
        return frameLine is not null && binding is not EffectBinding.Tokens && scope.ContainsLine(binding, frameLine);
    }

    private (bool Continuous, long Start, long End) V2AnchorRegion(ActiveEffect owner, ParticleSource source,
        long timeUs, SceneFrame scene, CancellationToken token)
    {
        long start = 0, end = long.MaxValue;
        var load = owner.Pure!.Source.Indexes.Presentations.Get(source.BindingId);
        if (load is null || !load.Contains(timeUs)) return (false, timeUs, timeUs);
        start = load.RevealTimeUs; end = load.HideTimeUs ?? long.MaxValue;
        if (source is ParticleSource.Cursor cursor)
        {
            start = Math.Max(start, cursor.Identity.TimeRange.StartUs); end = Math.Min(end, cursor.Identity.TimeRange.EndUs);
            ClipV2Region(V2CursorCuts(owner, cursor, scene, token), timeUs, ref start, ref end);
        }
        // The requesting policy chooses Exact by default. Every real text
        // dependency must independently prove continuity over the whole cell.
        foreach (var dependency in _instances.Where(item => item.Pure is not null
            && (ReferenceEquals(item, owner) || MayWriteV2Anchor(item, source, scene))))
        {
            var policy = dependency.Pure!.Prepared.GeometrySampling;
            if (policy.Mode != GeometrySamplingMode.Continuous120Hz) return (false, timeUs, timeUs);
            var range = policy.ContinuousRanges.FirstOrDefault(item => item.Contains(timeUs));
            if (!range.IsValid) return (false, timeUs, timeUs);
            start = Math.Max(start, range.StartUs); end = Math.Min(end, range.EndUs);
            ClipV2Region(policy.DiscontinuitiesUs, timeUs, ref start, ref end);
        }
        if (_v2AnchorBreakpoints.IsDefault)
        {
            SortedSet<long> boundaries = [0];
            foreach (var evt in _timeline!.Events) { token.ThrowIfCancellationRequested(); boundaries.Add(evt.TimeUs); }
            foreach (var exit in _exitDurations.Values)
            {
                boundaries.Add(exit.Hide);
                if (exit.Hide <= long.MaxValue - exit.Duration) boundaries.Add(exit.Hide + exit.Duration);
            }
            _v2AnchorBreakpoints = [.. boundaries];
            _v2PrepareBudget.ReserveBytes(owner.Pure!.ParticleOwner!, 8L * boundaries.Count);
        }
        ClipV2Region(_v2AnchorBreakpoints, timeUs, ref start, ref end);
        return (start <= timeUs && timeUs < end, start, end);
    }

    private ImmutableArray<long> V2CursorCuts(ActiveEffect owner, ParticleSource.Cursor cursor, SceneFrame scene, CancellationToken token)
    {
        if (_v2CursorCuts.TryGetValue(cursor.Identity, out var cuts)) return cuts;
        SortedSet<long> boundaries = [];
        if (cursor.Identity.TokenId is { } id)
        {
            var tokenGeometry = scene.TokenGeometries.SingleOrDefault(item => item.WithRuby.TokenId == id && item.WithRuby.BindingId == cursor.BindingId);
            if (tokenGeometry?.ProgressPath is { } path)
            {
                foreach (double edge in tokenGeometry.Clusters.Where(static item => item.Target.RubyId is null)
                    .SelectMany(static item => new[] { item.Geometry.AdvanceBounds.Left, item.Geometry.AdvanceBounds.Right }).Distinct())
                {
                    token.ThrowIfCancellationRequested();
                    long low = cursor.Identity.TimeRange.StartUs, high = cursor.Identity.TimeRange.EndUs - 1;
                    bool increasing = path.EndX >= path.StartX;
                    bool Beyond(long t)
                    {
                        double x = path.At((t - cursor.Identity.TimeRange.StartUs) / (double)cursor.Identity.TimeRange.DurationUs, CursorAnchorMode.Baseline).X;
                        return increasing ? x >= edge : x < edge;
                    }
                    if (!Beyond(high)) continue;
                    while (low < high) { token.ThrowIfCancellationRequested(); long mid = low + (high - low) / 2; if (Beyond(mid)) high = mid; else low = mid + 1; }
                    boundaries.Add(low);
                }
            }
        }
        if (_v2CursorCutCount + boundaries.Count > ParticleLimits.MaximumSourceIntervals)
            throw new EffectOutputException("FinalAnchorPrepareBudgetExceeded", "播放头字簇切换断点超过固定准备预算。");
        _v2PrepareBudget.ReserveBytes(owner.Pure!.ParticleOwner!, 128L + boundaries.Count * 8L);
        cuts = [.. boundaries]; _v2CursorCuts.Add(cursor.Identity, cuts); _v2CursorCutCount += cuts.Length; return cuts;
    }

    private static void ClipV2Region(ImmutableArray<long> boundaries, long timeUs, ref long start, ref long end)
    {
        int low = 0, high = boundaries.Length;
        while (low < high) { int mid = low + (high - low) / 2; if (boundaries[mid] <= timeUs) low = mid + 1; else high = mid; }
        if (low > 0) start = Math.Max(start, boundaries[low - 1]);
        if (low < boundaries.Length) end = Math.Min(end, boundaries[low]);
    }
}
