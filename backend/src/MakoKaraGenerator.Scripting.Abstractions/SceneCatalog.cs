using System.Collections.Immutable;

namespace MakoKaraGenerator.Scripting;

/// <summary>公开节点角色；槽位仍是普通组，不是第二种歌词。</summary>
public enum SceneNodeKind
{
    /// <summary>容器或槽位。</summary>
    Group,
    /// <summary>源歌词、注音或真实标记。</summary>
    Text,
}

/// <summary>容器的内部布局规则，纯读取不能改写调度。</summary>
public enum NodeLayoutKind
{
    /// <summary>没有内部排版。</summary>
    None,
    /// <summary>按源行局部位置。</summary>
    FreeLine,
    /// <summary>任意槽位序列。</summary>
    SlotSequence,
    /// <summary>静态翻译流。</summary>
    TranslationFlow,
    /// <summary>跟随基础字的注音。</summary>
    Ruby,
}

/// <summary>固定场景目录节点；父关联是定义，实时槽位装载从 FrameLine 读取。</summary>
/// <param name="Id">真实节点。</param><param name="ParentId">固定父节点。</param><param name="Kind">角色。</param>
/// <param name="Layout">内部排版。</param><param name="ReferenceRatio">相对父参考框的宽高。</param>
/// <param name="BranchId">适用的源分支。</param><param name="LineId">适用的源行。</param><param name="RubyId">适用的注音。</param>
/// <param name="Order">定义的稳定顺序，不是媒体图层顺序。</param>
public sealed record SourceNode(NodeId Id, NodeId? ParentId, SceneNodeKind Kind, NodeLayoutKind Layout,
    Vector2 ReferenceRatio, BranchId? BranchId, LineId? LineId, RubyId? RubyId, int Order);

/// <summary>真实显示程序的只读身份和归属。</summary>
/// <param name="Id">程序。</param><param name="BranchId">源分支。</param><param name="RootNodeId">根组。</param><param name="DefaultNodeId">默认归属容器。</param>
public sealed record SourceProgram(ProgramId Id, BranchId BranchId, NodeId RootNodeId, NodeId DefaultNodeId);

/// <summary>实际片段及其随源时间派生的区间。</summary>
/// <param name="Id">片段。</param><param name="ProgramId">程序。</param><param name="ContainerId">归属容器。</param>
/// <param name="LineIds">源成员。</param><param name="TimeRange">首句起点至末句终点。</param>
public sealed record SourceClip(ClipId Id, ProgramId ProgramId, NodeId ContainerId, ImmutableArray<LineId> LineIds, TimeRange TimeRange);

/// <summary>真实前奏配置；不制造源字或起唱事件。</summary>
/// <param name="Id">前奏。</param><param name="ProgramId">程序。</param><param name="TargetLineId">源目标句。</param>
/// <param name="ContainerId">提示容器。</param><param name="Enabled">是否启用。</param><param name="TimeRange">有效准备区间，未知保留 null。</param>
public sealed record SourceCue(CueId Id, ProgramId ProgramId, LineId TargetLineId, NodeId ContainerId, bool Enabled, TimeRange? TimeRange);

/// <summary>完整场景目录；不是当前可见节点集合，不返回领域对象。</summary>
public sealed class SceneCatalog
{
    private readonly ImmutableDictionary<NodeId, SourceNode> _nodes;
    /// <summary>固定全部节点／程序／片段／前奏，并检查身份、父树与源关联。</summary>
    public SceneCatalog(ContentCatalog content, ImmutableArray<SourceNode> nodes, ImmutableArray<SourceProgram> programs,
        ImmutableArray<SourceClip> clips, ImmutableArray<SourceCue> cues)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (nodes.IsDefault || programs.IsDefault || clips.IsDefault || cues.IsDefault) throw new ArgumentException("目录数组不能是 default。");
        _nodes = nodes.ToImmutableDictionary(static node => node.Id);
        var byProgram = programs.ToImmutableDictionary(static program => program.Id);
        if (clips.Select(static clip => clip.Id).Distinct().Count() != clips.Length || cues.Select(static cue => cue.Id).Distinct().Count() != cues.Length)
            throw new ArgumentException("场景片段或前奏身份重复。");
        foreach (var node in nodes)
        {
            if (!node.Id.IsValid || !Enum.IsDefined(node.Kind) || !Enum.IsDefined(node.Layout) || node.ReferenceRatio.X <= 0 || node.ReferenceRatio.Y <= 0
                || node.Order < 0 || node.BranchId is { } branch && !content.Branches.Any(item => item.Id == branch)
                || node.LineId is { } line && !content.Lines.Any(item => item.Id == line)
                || node.RubyId is { } ruby && !content.Ruby.Any(item => item.Id == ruby)) throw new ArgumentException("场景节点关联无效。");
            HashSet<NodeId> visited = [node.Id];
            NodeId? parent = node.ParentId;
            while (parent is { } id)
            {
                if (!visited.Add(id) || !_nodes.TryGetValue(id, out var ancestor)) throw new ArgumentException("场景父树循环或缺少父节点。");
                parent = ancestor.ParentId;
            }
        }
        foreach (var program in programs)
            if (!program.Id.IsValid || !content.Branches.Any(branch => branch.Id == program.BranchId)
                || !_nodes.ContainsKey(program.RootNodeId) || !_nodes.ContainsKey(program.DefaultNodeId)) throw new ArgumentException("显示程序关联无效。");
        foreach (var clip in clips)
        {
            if (!clip.Id.IsValid || !byProgram.TryGetValue(clip.ProgramId, out var program) || !_nodes.ContainsKey(clip.ContainerId)
                || clip.LineIds.IsDefaultOrEmpty || !clip.TimeRange.IsValid || clip.LineIds.Distinct().Count() != clip.LineIds.Length
                || clip.LineIds.Any(id => content.GetLine(id).BranchId != program.BranchId)) throw new ArgumentException("展示片段关联无效。");
        }
        foreach (var cue in cues)
            if (!cue.Id.IsValid || !byProgram.TryGetValue(cue.ProgramId, out var program) || !_nodes.ContainsKey(cue.ContainerId)
                || content.GetLine(cue.TargetLineId).BranchId != program.BranchId || cue.TimeRange is { IsValid: false }) throw new ArgumentException("前奏关联无效。");
        Nodes = nodes; Programs = programs; Clips = clips; LeadIns = cues;
    }
    /// <summary>完整节点。</summary>
    public ImmutableArray<SourceNode> Nodes { get; }
    /// <summary>完整显示程序。</summary>
    public ImmutableArray<SourceProgram> Programs { get; }
    /// <summary>所有显式片段。</summary>
    public ImmutableArray<SourceClip> Clips { get; }
    /// <summary>所有真实前奏，包括禁用项。</summary>
    public ImmutableArray<SourceCue> LeadIns { get; }
    /// <summary>按独立节点身份查询；不存在不创建默认节点。</summary>
    public SourceNode GetNode(NodeId id) => _nodes.TryGetValue(id, out var node) ? node : throw new KeyNotFoundException("UnknownNode: " + id.Value);
}
