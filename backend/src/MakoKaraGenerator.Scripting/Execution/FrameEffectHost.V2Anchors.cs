using System.Collections.Immutable;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting.Particles;
using MakoKaraGenerator.Scripting.Particles.V2;
using MakoKaraGenerator.Scripting.Projection;
using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Scripting.Execution;

internal sealed partial class FrameEffectHost
{
    internal V2RuntimeBudgetOptions V2Budgets { get; init; } = V2RuntimeBudgetOptions.Default;
    private readonly V2FinalAnchorMemo _v2AnchorMemo = new();
    private ParticlePrepareBudget _v2PrepareBudget = new();
    private ImmutableArray<long> _v2AnchorBreakpoints;
    private readonly Dictionary<CursorIdentity, ImmutableArray<long>> _v2CursorCuts = [];
    private int _v2CursorCutCount;

    private sealed class V2AnchorRequest(PreparedRenderFrame request, V2FinalAnchorMemo.Transaction memo,
        ParticleQueryBudget particles, V2AnchorQueryBudget anchorBudget)
    {
        public PreparedRenderFrame Request { get; } = request;
        public V2FinalAnchorMemo.Transaction Memo { get; } = memo;
        public ParticleQueryBudget Particles { get; } = particles;
        public V2AnchorQueryBudget Budget { get; } = anchorBudget;
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
        V2AnchorWriters writers = new(this, source, scene);
        foreach (var legacy in _instances.Where(static item => !item.IsPureText))
            if (writers.May(legacy))
                throw new EffectOutputException("FinalAnchorLegacyDependencyUnsupported",
                    "最终锚点依赖有状态 v1 文字输出：" + legacy.Definition.Id.Value);
        var (continuous, start, end) = V2AnchorRegionCore(owner, source, timeUs, scene, writers, token);
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
                    text = Evaluate(nodeUs, query.Request.OutputMode, query.Request.Target, diagnostics, token,
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
                if (tokenGeometry is not null) return tokenGeometry.MapPoint(tokenGeometry.BaseGeometry.AdvanceBounds.Center);
                break;
            case ParticleSource.Line line:
                var lineGeometry = text.FinalLineGeometry.SingleOrDefault(item => item.Target == line.Target);
                if (lineGeometry is not null)
                {
                    var point = lineGeometry.BaseGeometry.AdvanceBounds.Center;
                    var tokenAt = text.FinalGeometry.FirstOrDefault(item => item.WithRuby.BindingId == line.Target.BindingId
                        && point.X >= item.BaseGeometry.AdvanceBounds.Left && point.X < item.BaseGeometry.AdvanceBounds.Right);
                    return tokenAt?.BaseGeometry.Mapping is not null
                        ? tokenAt.MapPoint(point)
                        : Center(lineGeometry.BaseGeometry);
                }
                break;
            case ParticleSource.TextMark mark:
                var markGeometry = text.FinalMarkGeometry.SingleOrDefault(item => item.Target == mark.Target);
                if (markGeometry is not null) return Center(markGeometry.Geometry);
                break;
        }
        throw new EffectOutputException("FinalParticleAnchorUnavailable", "真实出生或桥接端点没有匹配的最终文字锚点。");
        static Point2 Center(GeometrySample geometry) => geometry.MapPoint(geometry.AdvanceBounds.Center);
    }

    /// <summary>
    /// 同一锚点来源在同一帧上对各实例的写入判定。绑定范围、来源对应的帧对象、运行父树与组几何只建一次，
    /// 逐实例只做范围查询；旧做法每个实例都重建整帧投影，模板逐行应用时随实例数×在场元素数增长。
    /// </summary>
    private sealed class V2AnchorWriters(FrameEffectHost host, ParticleSource source, SceneFrame scene)
    {
        private FrameBindingScope? _scope;
        private bool _resolved;
        private TokenFamilyTarget? _sourceToken;
        private FrameToken? _token;
        private FrameMark? _mark;
        private FrameLine? _line;
        private string? _ancestorStart;
        private Dictionary<string, SceneElementFrame>? _nodes;
        private HashSet<GroupTarget>? _groups;

        internal bool May(ActiveEffect active)
        {
            if (!active.Definition.Capabilities.HasFlag(EffectCapabilities.Transforms)) return false;
            var scope = _scope ??= new(scene); var binding = active.Definition.Binding;
            Resolve();
            if (_sourceToken is not null)
                return _token is not null && (scope.ContainsToken(binding, _token)
                    || Ancestor(active, scope, _ancestorStart ??= scene.Lines.Single(item => item.BindingId == _sourceToken.BindingId.Value).ElementId));
            if (source is ParticleSource.TextMark mark)
                return _mark is not null && (scope.ContainsMark(binding, _mark) || Ancestor(active, scope, mark.Target.NodeId.Value));
            return _line is not null && (binding is not EffectBinding.Tokens && scope.ContainsLine(binding, _line)
                || Ancestor(active, scope, _ancestorStart ??= scene.Lines.Single(item => item.BindingId == _line.BindingId.Value).ElementId));
        }

        private void Resolve()
        {
            if (_resolved) return;
            _sourceToken = source switch
            {
                ParticleSource.Cursor { Identity.TokenId: { } id } cursor => new(id, cursor.Identity.BindingId),
                ParticleSource.Token value => value.Target, _ => null,
            };
            if (_sourceToken is not null) _token = FrameProjection.Tokens(scene).SingleOrDefault(item => item.WithRuby == _sourceToken);
            else if (source is ParticleSource.TextMark mark)
                _mark = FrameProjection.Marks(scene, host._project.Scene).SingleOrDefault(item => item.Target == mark.Target);
            else
            {
                LineTarget? line = source switch
                {
                    ParticleSource.Cursor cursor => new(cursor.Identity.LineId, cursor.Identity.BindingId),
                    ParticleSource.Line value => value.Target, _ => null,
                };
                _line = FrameProjection.Lines(scene, host._project.Scene).SingleOrDefault(item => item.Target == line);
            }
            _resolved = true;
        }

        private bool Ancestor(ActiveEffect active, FrameBindingScope scope, string nodeId)
        {
            // SceneAffineProjection applies a real group's local matrix both to that
            // node and to its direct runtime aliases, then inherits it down ParentId.
            // Node(group, false) can therefore affect text outside its direct scope.
            if (active.Definition.PackageDeclaration is { } declaration
                && !declaration.Outputs.Any(output => output.Kind == "VisualTransform" && output.Target == "group")) return false;
            var nodes = _nodes ??= scene.Elements.ToDictionary(node => node.Id, StringComparer.Ordinal);
            var groups = _groups ??= [.. scene.GroupGeometries.Select(static group => group.Target)];
            HashSet<string> visited = new(StringComparer.Ordinal);
            string? current = nodeId;
            while (current is not null)
            {
                if (!visited.Add(current) || !nodes.TryGetValue(current, out var node))
                    throw new PreparedFrameException("InvalidSceneTransform", "最终锚点的运行父树循环或缺少真实节点。");
                if (node.Kind == SceneElementKind.Group)
                {
                    var target = new GroupTarget(new(node.SourceNodeId ?? node.Id));
                    if (groups.Contains(target) && scope.ContainsGroup(active.Definition.Binding, target)) return true;
                }
                current = node.ParentId;
            }
            return false;
        }
    }

    // 单次区段查询入口（白盒测试按名反射调用）；正式补算路径与 v1 写入检查共用同一判定对象，走 Core。
    private (bool Continuous, long Start, long End) V2AnchorRegion(ActiveEffect owner, ParticleSource source,
        long timeUs, SceneFrame scene, CancellationToken token)
        => V2AnchorRegionCore(owner, source, timeUs, scene, new(this, source, scene), token);

    private (bool Continuous, long Start, long End) V2AnchorRegionCore(ActiveEffect owner, ParticleSource source,
        long timeUs, SceneFrame scene, V2AnchorWriters writers, CancellationToken token)
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
            && (ReferenceEquals(item, owner) || writers.May(item))))
        {
            // Media may vary at any exact project time even when author policy claims continuity.
            if (dependency.Pure!.MediaProjection?.Owner is not null) return (false, timeUs, timeUs);
            var policy = dependency.Pure.Prepared.GeometrySampling;
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
            Core.Lyrics.LyricLine? sourceLine = _snapshot.Lyrics.Branches
                .FirstOrDefault(branch => branch.Id == cursor.Identity.BranchId.Value)?.Lines
                .FirstOrDefault(line => line.Id == cursor.Identity.LineId.Value);
            Core.Lyrics.LyricToken? sourceToken = sourceLine?.Tokens.FirstOrDefault(item => item.Id == id.Value);
            if (sourceLine is not null && sourceToken is not null)
            {
                foreach (long cut in LyricSampler.GetPiecewiseBreakpoints(sourceLine, sourceToken))
                {
                    if (cut > cursor.Identity.TimeRange.StartUs && cut < cursor.Identity.TimeRange.EndUs)
                        boundaries.Add(cut);
                }
            }

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
                        double progress = sourceLine is not null && sourceToken is not null
                            ? LyricSampler.ComputeTokenProgress(sourceLine, sourceToken, t)
                            : (t - cursor.Identity.TimeRange.StartUs) / (double)cursor.Identity.TimeRange.DurationUs;
                        double x = path.At(progress, CursorAnchorMode.Baseline).X;
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
