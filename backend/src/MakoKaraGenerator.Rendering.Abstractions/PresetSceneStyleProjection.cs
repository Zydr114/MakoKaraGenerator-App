using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Rendering;

/// <summary>固定应用到实际装载帧的唯一静态投影；所有几何与绘制消费同一帧。</summary>
public static class PresetSceneStyleProjection
{
    // 解析器与父关系底层只读取不可变输入，按引用缓存，避免逐帧、逐文字对象按全曲规模重建。
    private static readonly ConditionalWeakTable<ProjectSnapshot, PresetStyleResolver> Resolvers = new();
    private static readonly ConditionalWeakTable<LyricPresentationDefinition, Dictionary<string, string?>> TreeParents = new();
    private static readonly ConditionalWeakTable<SceneDefinition, Dictionary<string, string?>> DefinitionParents = new();

    /// <summary>创建只读源解析器，不生成编辑工程；同一不可变快照复用同一解析器。</summary>
    public static PresetStyleResolver Resolver(ProjectSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return Resolvers.GetValue(snapshot, static source => new(source.LyricStyle, source.Lyrics, source.PresetApplications));
    }

    /// <summary>实际文本节点的完整根到对象链，包含动态槽位及附着后的父关系。</summary>
    public static ImmutableArray<string> NodePath(ProjectSnapshot snapshot, string nodeId, SceneFrame? frame = null)
        => new ParentIndex(snapshot, frame).Path(nodeId);

    /// <summary>解析当前真实行／元素，未装载的源不猜测未来槽位。</summary>
    public static PresetStyleResolution Lyrics(ProjectSnapshot snapshot, string lineId, string? tokenId = null,
        SceneFrame? frame = null, PresetStyleResolver? resolver = null)
    {
        var sample = frame?.Lines.FirstOrDefault(line => line.Frame.Line!.Id == lineId && line.ExitReplica is null)
            ?? frame?.Lines.FirstOrDefault(line => line.Frame.Line!.Id == lineId);
        if (sample is not null) return LyricsForBinding(snapshot, sample, tokenId, frame!, resolver);
        return (resolver ?? Resolver(snapshot)).ResolveLyrics(lineId, tokenId, sample?.DisplayClipId,
            sample is null ? null : NodePath(snapshot, sample.ElementId, frame));
    }

    /// <summary>按真实装载解析源外观；退出副本使用隐藏左极限的固定来源，不读取当前槽位。</summary>
    public static PresetStyleResolution LyricsForBinding(ProjectSnapshot snapshot, SceneLineSample sample, string? tokenId,
        SceneFrame frame, PresetStyleResolver? resolver = null)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (sample.ExitReplica is { } exit) return ExitStyle(exit, tokenId);
        return (resolver ?? Resolver(snapshot)).ResolveLyrics(sample.Frame.Line!.Id, tokenId, sample.DisplayClipId,
            NodePath(snapshot, sample.ElementId, frame));
    }

    /// <summary>真实提示组／标记来源及实际运行父树。</summary>
    public static PresetStyleResolution Mark(ProjectSnapshot snapshot, string nodeId,
        SceneFrame? frame = null, PresetStyleResolver? resolver = null)
    {
        var tree = snapshot.LyricPresentation.WithDefaultPrograms(snapshot.Lyrics);
        var node = tree.FindNode(nodeId);
        if (node?.Content is not TextMarkContent mark || tree.FindNode(node.ParentId!) is not { Content: CountdownSequenceContent sequence } parent)
            throw new PreparedFrameException("InvalidMarkParent", "标记缺少真实提示组外观。");
        string? branchId = frame?.LeadIns.FirstOrDefault(cue => cue.ContainerId == parent.Id)?.BranchId
            ?? tree.Programs.FirstOrDefault(program => program.LeadIns.Any(cue => cue.IndicatorContainerId == parent.Id))?.BranchId;
        return (resolver ?? Resolver(snapshot)).ResolveMark(sequence.Appearance.ToLyricStyle(),
            "lyricPresentation.nodes." + parent.Id + ".content.appearance", mark.AppearanceOverride?.ToStyleOverride(),
            "lyricPresentation.nodes." + nodeId + ".content.appearanceOverride", NodePath(snapshot, nodeId, frame), branchId);
    }

    /// <summary>将生效外观写到只读帧副本，保持所有源ID／时间／文字与正式装载身份。</summary>
    public static SceneFrame ApplyLines(ProjectSnapshot snapshot, SceneFrame frame)
    {
        if (snapshot.PresetApplications.Items.IsEmpty) return frame;
        var resolver = Resolver(snapshot);
        ParentIndex? parents = null;
        return frame with { Lines = [.. frame.Lines.Select(sample =>
        {
            var source = sample.Frame.Line!;
            // 同一装载的行与各文字对象共用一条实际父链，每行只求一次。
            var path = sample.ExitReplica is null ? (parents ??= new(snapshot, frame)).Path(sample.ElementId) : default;
            PresetStyleResolution Resolve(string? tokenId) => sample.ExitReplica is { } exit ? ExitStyle(exit, tokenId)
                : resolver.ResolveLyrics(source.Id, tokenId, sample.DisplayClipId, path);
            var lineStyle = Resolve(null).Style;
            var tokens = source.Tokens.Select(token =>
            {
                var style = Resolve(token.Id).Style;
                return token with { StyleOverride = style == lineStyle ? null :
                    StyleBatchEditor.CopyProperties(LyricStyleOverride.Empty, style, TokenStyleSupport.SupportedAtTokenLevel) };
            }).ToImmutableArray();
            var line = source with { StyleOverride = StyleBatchEditor.CopyProperties(LyricStyleOverride.Empty,
                lineStyle, LyricStylePropertyExtensions.All), Tokens = tokens };
            return sample with { Frame = sample.Frame with { Line = line } };
        })] };
    }

    private static PresetStyleResolution ExitStyle(ExitReplicaMetadata exit, string? tokenId)
        => tokenId is null ? exit.LineStyle : exit.TokenStyles.TryGetValue(new(tokenId), out var value) ? value
            : throw new PreparedFrameException("MissingBaselineTarget", "退出副本缺少隐藏左极限的元素外观。");

    /// <summary>
    /// 实际父关系的分层视图，后层覆盖前层：展示树节点 → 场景定义元素 → 本帧装载行、提示附着与已测量元素。
    /// 前两层只依赖不可变输入并按引用缓存；帧层只含本帧对象，不随全曲行数增长。
    /// </summary>
    private sealed class ParentIndex
    {
        private readonly Dictionary<string, string?> _tree;
        private readonly Dictionary<string, string?>? _definition;
        private readonly Dictionary<string, string?>? _frame;

        internal ParentIndex(ProjectSnapshot snapshot, SceneFrame? frame)
        {
            _tree = TreeParents.GetValue(snapshot.LyricPresentation.WithDefaultPrograms(snapshot.Lyrics),
                static tree => tree.Nodes.ToDictionary(node => node.Id, node => node.ParentId, StringComparer.Ordinal));
            if (frame is null) return;
            _definition = DefinitionParents.GetValue(frame.Definition, static definition =>
            {
                Dictionary<string, string?> parents = new(StringComparer.Ordinal);
                foreach (var node in definition.Elements) parents[node.Id] = node.ParentId;
                return parents;
            });
            _frame = new(StringComparer.Ordinal);
            foreach (var line in frame.Lines) _frame[line.ElementId] = line.ParentId;
            foreach (var cue in frame.LeadIns)
                if (snapshot.LyricPresentation.FindNode(cue.ContainerId)?.Attachment?.Mode == SceneAttachmentMode.CueTargetText)
                    _frame[cue.ContainerId] = cue.TargetElementId;
            foreach (var node in frame.Elements) _frame[node.Id] = node.ParentId;
        }

        internal ImmutableArray<string> Path(string nodeId)
        {
            List<string> path = [];
            HashSet<string> seen = new(StringComparer.Ordinal);
            string? current = nodeId;
            while (current is not null)
            {
                if (!seen.Add(current)) throw new PreparedFrameException("InvalidSceneTransform", "样式继承的实际父链循环。");
                path.Add(current);
                current = Parent(current);
            }
            path.Reverse();
            return [.. path];
        }

        private string? Parent(string id)
            => _frame is not null && _frame.TryGetValue(id, out var parent) ? parent
                : _definition is not null && _definition.TryGetValue(id, out parent) ? parent
                : _tree.GetValueOrDefault(id);
    }
}
