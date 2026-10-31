using System.Collections.Immutable;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Rendering;

/// <summary>固定应用到实际装载帧的唯一静态投影；所有几何与绘制消费同一帧。</summary>
public static class PresetSceneStyleProjection
{
    /// <summary>创建只读源解析器，不生成编辑工程。</summary>
    public static PresetStyleResolver Resolver(ProjectSnapshot snapshot)
        => new(snapshot.LyricStyle, snapshot.Lyrics, snapshot.PresetApplications);

    /// <summary>实际文本节点的完整根到对象链，包含动态槽位及附着后的父关系。</summary>
    public static ImmutableArray<string> NodePath(ProjectSnapshot snapshot, string nodeId, SceneFrame? frame = null)
    {
        var parents = snapshot.LyricPresentation.WithDefaultPrograms(snapshot.Lyrics).Nodes
            .ToDictionary(node => node.Id, node => node.ParentId, StringComparer.Ordinal);
        if (frame is not null)
        {
            foreach (var node in frame.Definition.Elements) parents[node.Id] = node.ParentId;
            foreach (var line in frame.Lines) parents[line.ElementId] = line.ParentId;
            foreach (var cue in frame.LeadIns)
                if (snapshot.LyricPresentation.FindNode(cue.ContainerId)?.Attachment?.Mode == SceneAttachmentMode.CueTargetText)
                    parents[cue.ContainerId] = cue.TargetElementId;
            foreach (var node in frame.Elements) parents[node.Id] = node.ParentId;
        }
        List<string> path = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        string? current = nodeId;
        while (current is not null)
        {
            if (!seen.Add(current)) throw new PreparedFrameException("InvalidSceneTransform", "样式继承的实际父链循环。");
            path.Add(current);
            current = parents.GetValueOrDefault(current);
        }
        path.Reverse();
        return [.. path];
    }

    /// <summary>解析当前真实行／元素，未装载的源不猜测未来槽位。</summary>
    public static PresetStyleResolution Lyrics(ProjectSnapshot snapshot, string lineId, string? tokenId = null,
        SceneFrame? frame = null, PresetStyleResolver? resolver = null)
    {
        var sample = frame?.Lines.SingleOrDefault(line => line.Frame.Line!.Id == lineId);
        return (resolver ?? Resolver(snapshot)).ResolveLyrics(lineId, tokenId, sample?.DisplayClipId,
            sample is null ? null : NodePath(snapshot, sample.ElementId, frame));
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
        return frame with { Lines = [.. frame.Lines.Select(sample =>
        {
            var source = sample.Frame.Line!;
            var lineStyle = Lyrics(snapshot, source.Id, frame: frame, resolver: resolver).Style;
            var tokens = source.Tokens.Select(token =>
            {
                var style = Lyrics(snapshot, source.Id, token.Id, frame, resolver).Style;
                return token with { StyleOverride = style == lineStyle ? null :
                    StyleBatchEditor.CopyProperties(LyricStyleOverride.Empty, style, TokenStyleSupport.SupportedAtTokenLevel) };
            }).ToImmutableArray();
            var line = source with { StyleOverride = StyleBatchEditor.CopyProperties(LyricStyleOverride.Empty,
                lineStyle, LyricStylePropertyExtensions.All), Tokens = tokens };
            return sample with { Frame = sample.Frame with { Line = line } };
        })] };
    }
}
