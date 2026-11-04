using System.Collections.Immutable;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Scripting.Projection;

/// <summary>从生产几何采样隐藏左极限，建立有界且可重算的退出副本；不执行作者代码。</summary>
public sealed class FrozenExitGeometry
{
    private readonly ProjectSnapshot _snapshot;
    private readonly ILineGeometryProvider _geometry;
    private readonly int _maximumEntries;
    private readonly Dictionary<BindingId, CacheEntry> _cache = [];
    private long _stamp;
    private sealed record Frozen(SceneLineSample Line, ImmutableArray<SceneElementFrame> Elements,
        ImmutableArray<TokenGeometrySample> Tokens, LineGeometrySample Geometry, ImmutableArray<GroupGeometrySample> Groups);
    private sealed record CacheEntry(Frozen Value, long Stamp);

    /// <summary>固定生产快照与几何后端；最多缓存 128 个已测量退出装载。</summary>
    public FrozenExitGeometry(ProjectSnapshot snapshot, ILineGeometryProvider geometry, int maximumEntries = 128)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(geometry);
        if (maximumEntries is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        _snapshot = snapshot; _geometry = geometry; _maximumEntries = maximumEntries;
    }

    /// <summary>向已完成布局和测量的当前帧追加冻结副本；原布局、前奏和源事件保持独立。</summary>
    public SceneFrame Extend(SceneFrame current, ImmutableArray<ExitReplicaOccurrence> exits, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (exits.IsDefault) throw new ArgumentException("退出集合不能为 default。", nameof(exits));
        token.ThrowIfCancellationRequested();
        if (exits.IsEmpty) return current;
        if (exits.Length > 128 || current.Lines.Any(static line => line.ExitReplica is not null))
            throw new PreparedFrameException("ExitReplicaBudgetExceeded", "退出集合超限或当前帧已经包含副本。");
        var lines = current.Lines.ToBuilder(); var elements = current.Elements.ToBuilder();
        var tokens = current.TokenGeometries.ToBuilder(); var geometries = current.LineGeometries.ToBuilder();
        var groups = current.GroupGeometries.ToBuilder();
        var existingBindings = current.Lines.Select(static line => line.BindingId!).ToHashSet(StringComparer.Ordinal);
        foreach (ExitReplicaOccurrence occurrence in exits)
        {
            token.ThrowIfCancellationRequested();
            if (occurrence.HideTimeUs > current.TimeUs || current.TimeUs - occurrence.HideTimeUs >= 3_000_000
                || !existingBindings.Add(occurrence.Binding.BindingId.Value))
                throw new PreparedFrameException("InvalidExitReplica", "退出装载时刻无效或 Binding 重复。");
            Frozen frozen;
            try { frozen = Get(occurrence, token); }
            catch (PreparedFrameException error)
            {
                throw new PreparedFrameException(error.Code, error.Message) { BindingId = occurrence.Binding.BindingId };
            }
            // Performance remains a query of source time. Only layout, slot, source styles and parent geometry freeze.
            lines.Add(frozen.Line with { Frame = SampleVisual(frozen.Line, current.TimeUs) });
            elements.AddRange(frozen.Elements); tokens.AddRange(frozen.Tokens); geometries.Add(frozen.Geometry); groups.AddRange(frozen.Groups);
        }
        token.ThrowIfCancellationRequested();
        var branchOrder = _snapshot.Lyrics.Branches.Select((branch, order) => (branch.Id, order)).ToDictionary(static value => value.Id, static value => value.order, StringComparer.Ordinal);
        var nodeOrder = current.Definition.Elements.Select((node, order) => (node.Id, order)).ToDictionary(static value => value.Id, static value => value.order, StringComparer.Ordinal);
        var result = current with
        {
            Lines = [.. lines.OrderBy(line => branchOrder[line.Branch.Id])
                .ThenBy(line => nodeOrder.GetValueOrDefault(line.SourceElementId, int.MaxValue))
                .ThenBy(static line => line.ExitReplica?.LoadOrdinal ?? int.MaxValue)
                .ThenBy(static line => line.BindingId, StringComparer.Ordinal)],
            Elements = elements.ToImmutable(), TokenGeometries = tokens.ToImmutable(), LineGeometries = geometries.ToImmutable(), GroupGeometries = groups.ToImmutable(),
        };
        if (result.Elements.Select(static node => node.Id).Distinct(StringComparer.Ordinal).Count() != result.Elements.Length)
            throw new PreparedFrameException("InvalidExitReplica", "退出父链别名与当前节点发生冲突。");
        return result with { GroupGeometries = GroupInkProjection.Create(result) };
    }

    private Frozen Get(ExitReplicaOccurrence occurrence, CancellationToken token)
    {
        BindingId binding = occurrence.Binding.BindingId;
        if (_cache.TryGetValue(binding, out var cached))
        {
            if (cached.Value.Line.ExitReplica!.HideTimeUs != occurrence.HideTimeUs)
                throw new PreparedFrameException("InvalidExitReplica", "同一装载不能拥有不同的隐藏边界。");
            _cache[binding] = cached with { Stamp = ++_stamp }; return cached.Value;
        }
        Frozen frozen = Capture(occurrence, token);
        if (_cache.Count == _maximumEntries)
        {
            BindingId oldest = _cache.MinBy(static pair => pair.Value.Stamp).Key;
            _cache.Remove(oldest);
        }
        _cache.Add(binding, new(frozen, ++_stamp));
        return frozen;
    }

    private Frozen Capture(ExitReplicaOccurrence occurrence, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (occurrence.HideTimeUs <= 0) throw new PreparedFrameException("ExitReplicaGeometryUnavailable", "隐藏左极限不在有效时间范围。");
        SceneFrame source = _geometry.GetSceneFrame(_snapshot, occurrence.HideTimeUs - 1);
        token.ThrowIfCancellationRequested();
        var line = source.Lines.SingleOrDefault(value => value.BindingId == occurrence.Binding.BindingId.Value);
        if (line?.Geometry is null || line.Frame.Line?.Id != occurrence.Binding.LineId.Value || line.SourceElementId != occurrence.Binding.NodeId.Value)
            throw new PreparedFrameException("ExitReplicaGeometryUnavailable", "隐藏左极限缺少原装载的生产行几何。");
        LineTarget target = new(occurrence.Binding.LineId, occurrence.Binding.BindingId);
        var lineGeometry = source.LineGeometries.SingleOrDefault(value => value.Target == target)
            ?? throw new PreparedFrameException("ExitReplicaGeometryUnavailable", "隐藏左极限缺少原装载的整行路径。");
        var tokenGeometry = source.TokenGeometries.Where(value => value.WithRuby.BindingId == target.BindingId).ToImmutableArray();
        var nodes = source.Elements.ToDictionary(static value => value.Id, StringComparer.Ordinal);
        HashSet<string> selected = new(StringComparer.Ordinal);
        string? current = line.ElementId;
        while (current is not null)
        {
            token.ThrowIfCancellationRequested();
            if (!selected.Add(current) || !nodes.TryGetValue(current, out var node))
                throw new PreparedFrameException("ExitReplicaGeometryUnavailable", "隐藏左极限的源父链缺失或成环。");
            current = node.ParentId;
        }
        foreach (var node in source.Elements.Where(value => value.SourceBinding.RubyId is not null && value.BindingId == line.BindingId)) selected.Add(node.Id);
        long drawUnits = lineGeometry.BaseGeometry.DrawUnits.Length + tokenGeometry.Sum(static value =>
            value.Ruby.Sum(static ruby => (long)ruby.Geometry.DrawUnits.Length));
        if (selected.Count > 256 || tokenGeometry.Length > 4096 || drawUnits > 65_536)
            throw new PreparedFrameException("ExitReplicaBudgetExceeded", "单个退出副本的父链、元素或字形单元超过预算。");
        string Alias(string id) => "exit:" + occurrence.Binding.BindingId.Value + ":" + id;
        var cloned = source.Elements.Where(node => selected.Contains(node.Id)).Select(node => node with
        {
            Id = Alias(node.Id), SourceNodeId = node.SourceNodeId ?? node.Id,
            ParentId = node.ParentId is null ? null : Alias(node.ParentId),
        }).ToImmutableArray();
        var resolver = PresetSceneStyleProjection.Resolver(_snapshot);
        var frozenStyles = line.Frame.Line.Tokens.ToImmutableDictionary(tokenValue => new TokenId(tokenValue.Id), tokenValue =>
            PresetSceneStyleProjection.LyricsForBinding(_snapshot, line, tokenValue.Id, source, resolver));
        var metadata = new ExitReplicaMetadata(occurrence.HideTimeUs, occurrence.LoadOrdinal,
            PresetSceneStyleProjection.NodePath(_snapshot, line.ElementId, source),
            PresetSceneStyleProjection.LyricsForBinding(_snapshot, line, null, source, resolver), frozenStyles);
        line = line with { RuntimeElementId = Alias(line.ElementId), ParentNodeId = Alias(line.ParentId), DisplayContainerId = Alias(line.ContainerId), ExitReplica = metadata };
        var clonedGroups = source.GroupGeometries.Where(group => selected.Contains(group.Target.NodeId.Value))
            .Select(group => group with { Target = new(new(Alias(group.Target.NodeId.Value))) }).ToImmutableArray();
        return new(line, cloned, tokenGeometry, lineGeometry, clonedGroups);
    }

    private static LyricFrameSample SampleVisual(SceneLineSample line, long timeUs)
    {
        var frame = LyricSampler.SampleLine(line.Frame.Line!, timeUs, line.Frame.LineIndex);
        // Preserve the shared sampler's static / FollowPrimary drawing policy. Author
        // Performance remains a separate true source-time projection in FrameProjection.
        return line.AnimationPolicy == LyricBranchAnimationPolicy.Karaoke ? frame : frame with
        {
            LineProgress = 0, CurrentTokenIndex = -1,
            Tokens = [.. frame.Tokens.Select(static token => token with { Progress = 0, IsCurrent = false, Source = Rendering.ProgressSource.Unavailable })],
            RubySegments = [.. frame.RubySegments.Select(static segment => segment with { Progress = 0, IsCurrent = false, Source = Rendering.ProgressSource.Unavailable })],
        };
    }
}
