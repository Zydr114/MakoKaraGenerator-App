using System.Collections.Immutable;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Scripting.Projection;

/// <summary>把固定生产源、真实装载及同一几何后端接到 SDK v2；不创建逐查询 IPC 或作者重放。</summary>
public static class PureSourceIndexes
{
    /// <summary>固定 subscription 后建立有限键索引；输出绑定与只读源范围仍然独立。</summary>
    public static SourceIndexes Create(ProjectSnapshot snapshot, TimelineProjection timeline, ILineGeometryProvider geometry,
        EffectBinding binding, SourceSubscription subscription, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(timeline); ArgumentNullException.ThrowIfNull(geometry);
        token.ThrowIfCancellationRequested();
        long sourceValues = (long)timeline.Content.Branches.Length + timeline.Content.Lines.Length + timeline.Content.Tokens.Length
            + timeline.Content.Ruby.Length + timeline.Scene.Nodes.Length + timeline.Scene.Programs.Length + timeline.Scene.Clips.Length
            + timeline.Scene.LeadIns.Length + timeline.Events.Length;
        if (sourceValues > 200_000) throw new PreparedFrameException("SourceIndexBudgetExceeded", "固定源目录与事件超过二十万个源值。");
        var scope = ResolvedSourceSubscription.Create(timeline, binding, subscription);
        var events = new TimelineIndex(timeline.Events.Where(scope.Matches), cancellationToken: token);
        return new(events, new Queries(snapshot, timeline, geometry, scope, token));
    }

    private sealed class Queries : ISourceIndexQuery
    {
        private const int MaximumEntries = 200_000;
        private const int MaximumCacheEntries = 8;
        private const int MaximumCacheValues = 400_000;
        private readonly ProjectSnapshot _snapshot;
        private readonly ProjectSnapshot _sourceSnapshot;
        private readonly TimelineProjection _timeline;
        private readonly ILineGeometryProvider _geometry;
        private readonly ImmutableHashSet<LineId> _lines;
        private readonly ImmutableHashSet<TokenId> _tokens;
        private readonly ImmutableHashSet<TextMarkTarget> _marks;
        private readonly ImmutableHashSet<NodeId> _groups;
        private readonly ImmutableDictionary<BindingId, SourcePresentationLoad> _loads;
        private readonly IntervalTable<SourcePresentationLoad> _presentations;
        private readonly ImmutableArray<SourcePresentationLoad> _openPresentations;
        private readonly IntervalTable<SourceCursorSegment> _cursors;
        private readonly ImmutableDictionary<CursorTrackId, IntervalTable<SourceCursorSegment>> _tracks;
        private readonly ImmutableDictionary<(BranchId BranchId, LineId LineId), LyricLine> _sourceLines;
        private readonly Dictionary<long, CachedScene> _scenes = [];
        private CancellationToken _token;
        private int _cachedValues;
        private long _stamp;
        private sealed record CachedScene(SceneFrame Scene, int Weight, long Stamp);
        private sealed record RawLoad(BindingEntered Entered, long? Hide, int Ordinal);
        private sealed record Selection(ImmutableHashSet<LineId> Lines, ImmutableHashSet<TokenId>? Tokens, bool Project);

        internal Queries(ProjectSnapshot snapshot, TimelineProjection timeline, ILineGeometryProvider geometry,
            ResolvedSourceSubscription scope, CancellationToken token)
        {
            _sourceSnapshot = snapshot;
            _snapshot = snapshot with { LyricPresentation = snapshot.LyricPresentation.WithDefaultPrograms(snapshot.Lyrics) };
            _timeline = timeline; _geometry = geometry; _token = token;
            _lines = [.. scope.LineIds]; _tokens = [.. scope.TokenIds];
            _marks = [.. timeline.Events.OfType<ElementTimelineEvent>().Where(scope.Matches)
                .Select(static item => item.Element).OfType<SceneElementIdentity.Mark>()
                .Select(static mark => new TextMarkTarget(mark.NodeId, mark.CueId, mark.Target.BindingId))];
            _groups = [.. timeline.Scene.Nodes.Where(static node => node.Kind == SceneNodeKind.Group)
                .Where(node => scope.Matches(new ElementVisible(new SceneElementIdentity.Group(node.Id, node.BranchId), 0,
                    EventSourceOrder.Global, ReplayReason.Initial))).Select(static node => node.Id)];
            _sourceLines = _snapshot.Lyrics.Branches
                .SelectMany(branch => branch.Lines.Select(line => (Key: (new BranchId(branch.Id), new LineId(line.Id)), Line: line)))
                .ToImmutableDictionary(static item => item.Key, static item => item.Line);
            var loads = BuildPresentations(scope);
            _loads = loads.ToImmutableDictionary(static load => load.Binding.BindingId);
            _presentations = new(loads, static load => load.RevealTimeUs, static load => load.HideTimeUs ?? long.MaxValue, token);
            _openPresentations = [.. loads.Where(static load => load.HideTimeUs is null)];
            var cursors = BuildCursors(loads);
            _cursors = new(cursors, static cursor => cursor.ActiveRange.StartUs, static cursor => cursor.ActiveRange.EndUs, token);
            _tracks = cursors.GroupBy(static cursor => cursor.TrackId).ToImmutableDictionary(static group => group.Key,
                group => new IntervalTable<SourceCursorSegment>([.. group], static cursor => cursor.ActiveRange.StartUs, static cursor => cursor.ActiveRange.EndUs, token));
            token.ThrowIfCancellationRequested();
        }

        public void SetCancellation(CancellationToken token) => _token = token;

        public SourcePresentationLoad? Presentation(BindingId bindingId)
        {
            _token.ThrowIfCancellationRequested();
            if (!bindingId.IsValid) throw new ArgumentException("装载身份无效。", nameof(bindingId));
            return _loads.GetValueOrDefault(bindingId);
        }

        public ImmutableArray<SourcePresentationSample> PresentationsAt(long timeUs, SourceFilter filter)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(timeUs); var selected = Select(filter);
            var result = ImmutableArray.CreateBuilder<SourcePresentationSample>();
            foreach (var load in timeUs == long.MaxValue ? _openPresentations : _presentations.At(timeUs, _token))
            {
                _token.ThrowIfCancellationRequested();
                if (!selected.Lines.Contains(load.Binding.LineId) || !load.Contains(timeUs)) continue;
                var line = _timeline.Content.GetLine(load.Binding.LineId);
                DisplayPhase phase = load.PreparationRange is { } preparation && preparation.Contains(timeUs) ? DisplayPhase.Preparing
                    : line.TimeRange is { } performance && timeUs >= performance.EndUs ? DisplayPhase.Retained : DisplayPhase.Performing;
                double exit = load.LayoutExitRange is { } range ? Math.Clamp((double)(timeUs - range.StartUs) / range.DurationUs, 0, 1) : 0;
                result.Add(new(load, timeUs, phase, exit));
            }
            return result.ToImmutable();
        }

        public ImmutableArray<SourcePresentationLoad> PresentationsIn(TimeRange range, SourceFilter filter)
        {
            RequireRange(range); var selected = Select(filter);
            return [.. _presentations.In(range, _token).Where(load => selected.Lines.Contains(load.Binding.LineId))];
        }

        public BaseSceneGeometry LayoutAt(long timeUs, SourceFilter filter)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(timeUs); var selected = Select(filter);
            SceneFrame frame = SceneAt(timeUs);
            var lines = FrameProjection.Lines(frame, _timeline.Scene).Where(line => _loads.ContainsKey(line.BindingId) && selected.Lines.Contains(line.LineId)).ToImmutableArray();
            var bindings = lines.Select(static line => line.BindingId).ToHashSet();
            var marks = FrameProjection.Marks(frame, _timeline.Scene).Where(mark => _marks.Contains(mark.Target)
                && FilterContainsLine(filter, mark.TargetLine.LineId)).ToImmutableArray();
            var markTargets = marks.Select(static mark => mark.Target).ToHashSet();
            HashSet<NodeId> ancestors = [];
            foreach (var node in lines.Select(static line => line.NodeId).Concat(marks.Select(static mark => mark.Target.NodeId)))
            {
                NodeId? current = node;
                while (current is { } id)
                {
                    _token.ThrowIfCancellationRequested();
                    var source = _timeline.Scene.GetNode(id);
                    if (source.Kind == SceneNodeKind.Group) ancestors.Add(id);
                    current = source.ParentId;
                }
            }
            return new()
            {
                TimeUs = timeUs, Lines = lines, Marks = marks,
                Tokens = [.. frame.TokenGeometries.Where(item => bindings.Contains(item.WithRuby.BindingId)
                    && _tokens.Contains(item.WithRuby.TokenId) && (selected.Tokens is null || selected.Tokens.Contains(item.WithRuby.TokenId)))],
                LineGeometry = [.. frame.LineGeometries.Where(item => bindings.Contains(item.Target.BindingId))],
                MarkGeometry = [.. frame.MarkGeometries.Where(item => markTargets.Contains(item.Target))],
                Groups = [.. FrameProjection.Groups(frame).Where(group => ancestors.Contains(group.Target.NodeId) || selected.Project && _groups.Contains(group.Target.NodeId))],
                GroupGeometry = [.. frame.GroupGeometries.Where(group => ancestors.Contains(group.Target.NodeId) || selected.Project && _groups.Contains(group.Target.NodeId))],
            };
        }

        public ImmutableArray<SourceCursorSegment> CursorSegments(CursorTrackId trackId, TimeRange range)
        {
            _token.ThrowIfCancellationRequested(); RequireRange(range);
            if (!trackId.IsValid) throw new ArgumentException("播放头轨道身份无效。", nameof(trackId));
            return _tracks.TryGetValue(trackId, out var track) ? track.In(range, _token) : [];
        }

        public ImmutableArray<PlaybackCursor> CursorsAt(long timeUs, SourceFilter filter, CursorAnchorMode anchorMode)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(timeUs);
            if (!Enum.IsDefined(anchorMode)) throw new ArgumentOutOfRangeException(nameof(anchorMode));
            var selected = Select(filter);
            var segments = _cursors.At(timeUs, _token).Where(segment => segment.ActiveRange.Contains(timeUs) && selected.Lines.Contains(segment.Identity.LineId)
                && (selected.Tokens is null || segment.Identity.TokenId is { } token && selected.Tokens.Contains(token))).ToArray();
            if (segments.Length == 0) return [];
            var geometry = LayoutAt(timeUs, filter);
            var tokens = geometry.Tokens.ToDictionary(static value => value.WithRuby);
            var lines = geometry.LineGeometry.ToDictionary(static value => value.Target);
            var result = ImmutableArray.CreateBuilder<PlaybackCursor>(segments.Length);
            foreach (var segment in segments)
            {
                _token.ThrowIfCancellationRequested(); var identity = segment.Identity;
                TextProgressPath? path; Affine2 world;
                if (identity.TokenId is { } tokenId)
                {
                    if (!tokens.TryGetValue(new(tokenId, identity.BindingId), out var token))
                        throw new EffectOutputException("BaseCursorGeometryUnavailable", "基础播放头缺少同一真实装载的逐字几何。");
                    path = token.ProgressPath; world = token.BaseGeometry.BaseWorldMatrix;
                }
                else
                {
                    if (!lines.TryGetValue(new(identity.LineId, identity.BindingId), out var line))
                        throw new EffectOutputException("BaseCursorGeometryUnavailable", "基础播放头缺少同一真实装载的整行几何。");
                    path = line.ProgressPath; world = line.BaseGeometry.BaseWorldMatrix;
                }
                if (path is null) throw new EffectOutputException("BaseCursorGeometryUnavailable", "基础播放头没有真实整形进度路径。");
                double progress;
                if (identity.TokenId is { } progressTokenId
                    && _sourceLines.TryGetValue((identity.BranchId, identity.LineId), out LyricLine? sourceLine)
                    && sourceLine.Tokens.FirstOrDefault(item => item.Id == progressTokenId.Value) is { } sourceToken)
                {
                    progress = LyricSampler.ComputeTokenProgress(sourceLine, sourceToken, timeUs);
                }
                else
                {
                    progress = (double)(timeUs - identity.TimeRange.StartUs) / identity.TimeRange.DurationUs;
                }
                result.Add(new CursorGeometry { TrackId = segment.TrackId, Identity = identity, TimeUs = timeUs,
                    Progress = progress, Path = path, WorldMatrix = world }.At(anchorMode));
            }
            return result.MoveToImmutable();
        }

        private ImmutableArray<SourcePresentationLoad> BuildPresentations(ResolvedSourceSubscription scope)
        {
            var schedules = new Dictionary<string, (LyricBranch Branch, LyricDisplayOccurrence Occurrence)>(StringComparer.Ordinal);
            var sourceLines = _snapshot.Lyrics.Branches.SelectMany(branch => branch.Lines.Select(line => (Branch: branch, Line: line)))
                .ToDictionary(static item => item.Line.Id, StringComparer.Ordinal);
            foreach (var branch in _snapshot.Lyrics.Branches)
            {
                _token.ThrowIfCancellationRequested();
                if (branch.TimingMode == LyricBranchTimingMode.None) continue;
                var program = _snapshot.LyricPresentation.FindProgram(branch.Id)!;
                foreach (var occurrence in LyricDisplayScheduleBuilder.Build(branch, program, _snapshot.LyricPresentation))
                {
                    _token.ThrowIfCancellationRequested();
                    if (schedules.Count >= MaximumEntries) throw Budget("装载显示计划超过二十万个区段。");
                    string container = occurrence.Segment.ContainerId(program);
                    string parent = occurrence.SlotIndex < 0 ? container : _snapshot.LyricPresentation.FindNode(container)!.Layout!.SlotIds[occurrence.SlotIndex];
                    string id = Uri.EscapeDataString(program.Id) + "/binding/" + Uri.EscapeDataString(occurrence.Segment.Identity) + "/"
                        + Uri.EscapeDataString(parent) + "/" + Uri.EscapeDataString(occurrence.Line.Id);
                    if (!schedules.TryAdd(id, (branch, occurrence))) throw new PreparedFrameException("InvalidSourceIndex", "装载计划身份重复。");
                }
            }
            var rawLoads = new List<RawLoad>();
            var active = new Dictionary<BindingId, (BindingEntered Entered, int Ordinal)>();
            int ordinal = 0;
            foreach (var item in _timeline.Events)
            {
                _token.ThrowIfCancellationRequested();
                if (item is BindingEntered entered)
                {
                    if (ordinal >= MaximumEntries) throw Budget("源装载索引超过二十万个装载。");
                    if (!active.TryAdd(entered.Binding.BindingId, (entered, ordinal++)))
                        throw new PreparedFrameException("InvalidSourceIndex", "相同源装载同时重复揭示。");
                }
                else if (item is BindingLeft left)
                {
                    if (!active.Remove(left.Binding.BindingId, out var previous))
                        throw new PreparedFrameException("InvalidSourceIndex", "源装载隐藏边界没有对应揭示。");
                    rawLoads.Add(new(previous.Entered, left.TimeUs, previous.Ordinal));
                }
            }
            foreach (var remaining in active.Values)
            { _token.ThrowIfCancellationRequested(); rawLoads.Add(new(remaining.Entered, null, remaining.Ordinal)); }
            var enteredByLine = rawLoads.GroupBy(static load => load.Entered.Binding.LineId)
                .ToDictionary(static group => group.Key, static group => group.OrderBy(static load => load.Ordinal).ToArray());
            var result = ImmutableArray.CreateBuilder<SourcePresentationLoad>();
            foreach (var raw in rawLoads.OrderBy(static load => load.Ordinal))
            {
                _token.ThrowIfCancellationRequested(); var entered = raw.Entered;
                if (!scope.Matches(entered)) continue;
                long? hide = raw.Hide;
                bool hasSchedule = schedules.TryGetValue(LyricSceneSampler.SourceBindingKey(entered.Binding.BindingId.Value), out var plan);
                var original = sourceLines[entered.Binding.LineId.Value];
                if (!hasSchedule && original.Branch.TimingMode == LyricBranchTimingMode.FollowPrimary && original.Line.PrimaryLineId is { } primaryLine
                    && enteredByLine.TryGetValue(new(primaryLine), out var associated))
                {
                    foreach (var primary in associated)
                    {
                        _token.ThrowIfCancellationRequested();
                        if (primary.Entered.TimeUs > entered.TimeUs || primary.Hide is { } primaryHide && primaryHide <= entered.TimeUs) continue;
                        if (!schedules.TryGetValue(LyricSceneSampler.SourceBindingKey(primary.Entered.Binding.BindingId.Value), out plan)) continue;
                        hasSchedule = true;
                        break;
                    }
                }
                TimeRange? preparation = hasSchedule && plan.Occurrence.Preparation is { } range
                    ? Clip(range.StartUs, range.EndUs, entered.TimeUs, hide) : null;
                result.Add(new(entered.Binding, entered.TimeUs, hide, raw.Ordinal, preparation, null));
            }
            return result.ToImmutable();
        }

        private ImmutableArray<SourceCursorSegment> BuildCursors(ImmutableArray<SourcePresentationLoad> loads)
        {
            var result = ImmutableArray.CreateBuilder<SourceCursorSegment>();
            foreach (var load in loads)
            {
                _token.ThrowIfCancellationRequested();
                var line = _timeline.Content.GetLine(load.Binding.LineId);
                var timed = line.TokenIds.Select(_timeline.Content.GetToken).Where(static token => token.TimeRange is not null).ToArray();
                var track = StableIdentities.CursorTrack(line.BranchId, load.Binding.ProgramId);
                if (timed.Length == 0)
                {
                    if (line.TimeRange is { } range) Add(new(line.BranchId, line.Id, load.Binding.BindingId, range, ProgressSource.Line), track, load);
                }
                else if (string.Concat(line.TokenIds.Select(id => _timeline.Content.GetToken(id).Text)) == line.Text)
                    foreach (var token in timed)
                    {
                        _token.ThrowIfCancellationRequested();
                        if (!_tokens.Contains(token.Id)) continue;
                        var range = token.TimeRange!.Value;
                        Add(new(line.BranchId, line.Id, load.Binding.BindingId, range, ProgressSource.Token, token.Id,
                            StableIdentities.Occurrence(line.BranchId, line.Id, token.Id, range)), track, load);
                    }
            }
            return result.ToImmutable();

            void Add(CursorIdentity identity, CursorTrackId track, SourcePresentationLoad load)
            {
                if (Clip(identity.TimeRange.StartUs, identity.TimeRange.EndUs, load.RevealTimeUs, load.HideTimeUs) is not { } active) return;
                if (result.Count >= MaximumEntries) throw Budget("基础播放头索引超过二十万个身份区段。");
                result.Add(new(track, identity, active));
            }
        }

        private Selection Select(SourceFilter filter)
        {
            _token.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(filter);
            return filter switch
            {
                SourceFilter.Project => new(_lines, null, true),
                SourceFilter.Branch branch => new([.. _timeline.Content.GetBranch(branch.Id).LineIds.Where(_lines.Contains)], null, false),
                SourceFilter.Lines lines => new([.. lines.Ids.Where(id => { _ = _timeline.Content.GetLine(id); return _lines.Contains(id); })], null, false),
                SourceFilter.Tokens tokens => ForTokens(tokens),
                _ => throw new ArgumentOutOfRangeException(nameof(filter)),
            };
        }
        private Selection ForTokens(SourceFilter.Tokens filter)
        {
            var tokens = filter.Ids.Select(_timeline.Content.GetToken).Where(token => _tokens.Contains(token.Id)).ToArray();
            return new([.. tokens.Select(static token => token.LineId)], [.. tokens.Select(static token => token.Id)], false);
        }
        private bool FilterContainsLine(SourceFilter filter, LineId lineId) => filter switch
        {
            SourceFilter.Project => true,
            SourceFilter.Branch branch => _timeline.Content.GetLine(lineId).BranchId == branch.Id,
            SourceFilter.Lines lines => lines.Ids.Contains(lineId),
            SourceFilter.Tokens tokens => tokens.Ids.Any(id => _timeline.Content.GetToken(id).LineId == lineId),
            _ => false,
        };

        private SceneFrame SceneAt(long timeUs)
        {
            _token.ThrowIfCancellationRequested();
            if (_scenes.TryGetValue(timeUs, out var cached))
            {
                _scenes[timeUs] = cached with { Stamp = ++_stamp }; return cached.Scene;
            }
            SceneFrame frame = _geometry.GetSceneFrame(_sourceSnapshot, timeUs);
            _token.ThrowIfCancellationRequested();
            if (frame.TimeUs != timeUs || frame.Lines.Any(static line => line.ExitReplica is not null))
                throw new EffectOutputException("InvalidSourceLayout", "源索引需要同一真实时刻且没有作者退出副本的基础布局。");
            long weight = (long)frame.Lines.Length + frame.Elements.Length + frame.TokenGeometries.Length + frame.LineGeometries.Length
                + frame.MarkGeometries.Length + frame.GroupGeometries.Length;
            foreach (var token in frame.TokenGeometries) weight += token.Clusters.Length + token.Ruby.Length + token.BaseGeometry.DrawUnits.Length;
            if (weight > MaximumEntries) throw Budget("单次基础布局查询超过二十万个源值。");
            while (_scenes.Count >= MaximumCacheEntries || _cachedValues + weight > MaximumCacheValues)
            {
                var oldest = _scenes.MinBy(static pair => pair.Value.Stamp);
                _cachedValues -= oldest.Value.Weight; _scenes.Remove(oldest.Key);
            }
            _scenes.Add(timeUs, new(frame, (int)weight, ++_stamp)); _cachedValues += (int)weight;
            return frame;
        }

        private static TimeRange? Clip(long start, long end, long reveal, long? hide)
        {
            start = Math.Max(start, reveal); end = Math.Min(end, hide ?? long.MaxValue);
            return start < end ? new(start, end) : null;
        }
        private static void RequireRange(TimeRange range)
        { if (!range.IsValid) throw new ArgumentException("源索引查询需要有效半开区间。", nameof(range)); }
        private static PreparedFrameException Budget(string message) => new("SourceIndexBudgetExceeded", message);
    }

    // Sorted starts and prefix maximum ends preserve overlapping/concurrent identities without a full-source scan.
    private sealed class IntervalTable<T>
    {
        private readonly ImmutableArray<T> _values;
        private readonly long[] _starts, _ends, _prefixEnds;
        internal IntervalTable(ImmutableArray<T> values, Func<T, long> start, Func<T, long> end, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            _values = [.. values.OrderBy(start)]; _starts = new long[_values.Length]; _ends = new long[_values.Length]; _prefixEnds = new long[_values.Length];
            for (int i = 0; i < _values.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                _starts[i] = start(_values[i]); _ends[i] = end(_values[i]);
                _prefixEnds[i] = i == 0 ? _ends[i] : Math.Max(_prefixEnds[i - 1], _ends[i]);
            }
        }
        internal ImmutableArray<T> At(long timeUs, CancellationToken token)
        {
            int end = Bound(_starts, timeUs, true), start = Bound(_prefixEnds, timeUs, true);
            var result = ImmutableArray.CreateBuilder<T>();
            for (int i = start; i < end; i++)
            { token.ThrowIfCancellationRequested(); if (_ends[i] > timeUs) result.Add(_values[i]); }
            return result.ToImmutable();
        }
        internal ImmutableArray<T> In(TimeRange range, CancellationToken token)
        {
            int end = Bound(_starts, range.EndUs, false), start = Bound(_prefixEnds, range.StartUs, true);
            var result = ImmutableArray.CreateBuilder<T>();
            for (int i = start; i < end; i++)
            { token.ThrowIfCancellationRequested(); if (_ends[i] > range.StartUs) result.Add(_values[i]); }
            return result.ToImmutable();
        }
        private static int Bound(long[] values, long timeUs, bool equal)
        {
            int low = 0, high = values.Length;
            while (low < high)
            { int middle = low + (high - low) / 2; if (values[middle] < timeUs || equal && values[middle] == timeUs) low = middle + 1; else high = middle; }
            return low;
        }
    }
}
