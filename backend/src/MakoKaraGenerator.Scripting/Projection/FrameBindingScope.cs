using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Scripting.Projection;

/// <summary>同一真实帧的输出绑定查询；只描述适用范围，不声明作者已经产生输出。</summary>
public sealed class FrameBindingScope
{
    private readonly Dictionary<string, SceneLineSample> _lines;
    private readonly Dictionary<string, SceneElementFrame> _elements;
    private readonly Dictionary<TextMarkTarget, SceneTextMarkSample> _marks;
    private readonly Dictionary<string, HashSet<string>> _ancestors = new(StringComparer.Ordinal);

    /// <summary>固定当前装载与运行父树；不沿持久定义推测槽位或前奏归属。</summary>
    public FrameBindingScope(SceneFrame scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        _lines = new(StringComparer.Ordinal); _elements = new(StringComparer.Ordinal); _marks = [];
        foreach (var line in scene.Lines)
            if (line.BindingId is not { } binding || !_lines.TryAdd(binding, line))
                throw new PreparedFrameException("InvalidSceneBinding", "当前行装载缺少或重复 BindingId。");
        foreach (var element in scene.Elements)
            if (!_elements.TryAdd(element.Id, element))
                throw new PreparedFrameException("InvalidSceneTransform", "运行父树存在重复节点。");
        foreach (var mark in scene.TextMarks)
        {
            if (mark.Geometry.BindingId is not { } binding
                || !_marks.TryAdd(new(new(mark.NodeId), new(mark.CueId), new(binding)), mark))
                throw new PreparedFrameException("InvalidSceneBinding", "当前标记装载缺少或重复身份。");
        }
    }

    /// <summary>查询当前真实元素族的输出绑定；过期装载或不存在的源元素不匹配。</summary>
    public bool ContainsToken(EffectBinding binding, FrameToken token)
    {
        ArgumentNullException.ThrowIfNull(binding); ArgumentNullException.ThrowIfNull(token);
        if (!_lines.TryGetValue(token.BindingId.Value, out var line)
            || line.Frame.Line is not { } source || line.Branch.Id != token.BranchId.Value || source.Id != token.LineId.Value
            || !source.Tokens.Any(item => item.Id == token.TokenId.Value)) return false;
        return binding switch
        {
            EffectBinding.Project => true,
            EffectBinding.Branch branch => branch.Id == token.BranchId,
            EffectBinding.Lines lines => lines.Ids.Contains(token.LineId),
            EffectBinding.Tokens tokens => tokens.Ids.Contains(token.TokenId),
            EffectBinding.Clips clips => line.DisplayClipId is { } id && clips.Ids.Contains(new(id)),
            EffectBinding.Node node => Inside(node, line.ElementId, line.ParentId),
            _ => false,
        };
    }

    /// <summary>查询当前行的只读绑定；Tokens 可读所属行，行 writer 仍须独立拒绝 Tokens。</summary>
    public bool ContainsLine(EffectBinding binding, FrameLine line)
    {
        ArgumentNullException.ThrowIfNull(binding); ArgumentNullException.ThrowIfNull(line);
        if (!IsCurrentLine(line, out var sample)) return false;
        return binding switch
        {
            EffectBinding.Project => true,
            EffectBinding.Branch branch => branch.Id == line.BranchId,
            EffectBinding.Lines lines => lines.Ids.Contains(line.LineId),
            EffectBinding.Tokens tokens => sample!.Frame.Line!.Tokens.Any(token => tokens.Ids.Contains(new(token.Id))),
            EffectBinding.Clips clips => line.ClipId is { } id && clips.Ids.Contains(id),
            EffectBinding.Node node => Inside(node, sample!.ElementId, sample.ParentId),
            _ => false,
        };
    }

    /// <summary>查询真实提示标记；Tokens 不匹配标记，Node 使用附着后的运行父树。</summary>
    public bool ContainsMark(EffectBinding binding, FrameMark mark)
    {
        ArgumentNullException.ThrowIfNull(binding); ArgumentNullException.ThrowIfNull(mark);
        if (!_marks.ContainsKey(mark.Target) || mark.Target.BindingId != mark.TargetLine.BindingId
            || !IsCurrentLine(mark.TargetLine, out _)) return false;
        if (binding is EffectBinding.Tokens) return false;
        return binding is EffectBinding.Node node ? Inside(node, mark.Target.NodeId.Value)
            : ContainsLine(binding, mark.TargetLine);
    }

    /// <summary>查询当前真实组的写范围；仅工程或显式 Node 绑定拥有组写权。</summary>
    public bool ContainsGroup(EffectBinding binding, GroupTarget target)
    {
        ArgumentNullException.ThrowIfNull(binding); ArgumentNullException.ThrowIfNull(target);
        if (!_elements.TryGetValue(target.NodeId.Value, out var element) || element.Kind != SceneElementKind.Group
            || element.SourceNodeId is not null) return false;
        return binding is EffectBinding.Project || binding is EffectBinding.Node node && Inside(node, target.NodeId.Value);
    }

    private bool IsCurrentLine(FrameLine line, out SceneLineSample? sample)
    {
        if (!_lines.TryGetValue(line.BindingId.Value, out sample)) return false;
        return sample.Branch.Id == line.BranchId.Value && sample.Frame.Line!.Id == line.LineId.Value
            && sample.SourceElementId == line.NodeId.Value
            && SourceParent(sample) == line.ParentId.Value
            && sample.DisplayClipId == line.ClipId?.Value;
    }
    private bool Inside(EffectBinding.Node binding, string id, string? actualParent = null)
    {
        if (!_elements.TryGetValue(id, out var element))
            throw new PreparedFrameException("InvalidSceneTransform", "绑定范围缺少真实运行节点：" + id);
        if (actualParent is not null && element.ParentId != actualParent)
            throw new PreparedFrameException("InvalidSceneTransform", "真实行装载与运行父树不一致：" + id);
        if (!_ancestors.TryGetValue(id, out var ancestors))
        {
            ancestors = new(StringComparer.Ordinal); HashSet<string> visited = new(StringComparer.Ordinal); string? current = id;
            while (current is not null)
            {
                if (!visited.Add(current))
                    throw new PreparedFrameException("InvalidSceneTransform", "绑定范围的运行父树循环。");
                if (!_elements.TryGetValue(current, out var node))
                    throw new PreparedFrameException("InvalidSceneTransform", "绑定范围缺少真实运行父节点：" + current);
                ancestors.Add(current);
                if (node.SourceNodeId is { } source) ancestors.Add(source);
                current = node.ParentId;
            }
            _ancestors.Add(id, ancestors);
        }
        return id == binding.Id.Value || element.SourceNodeId == binding.Id.Value || binding.IncludeDescendants && ancestors.Contains(binding.Id.Value);
    }

    private static string SourceParent(SceneLineSample sample) => sample.ExitReplica is { SourceNodePath.Length: >= 2 } exit
        ? exit.SourceNodePath[^2] : sample.ParentId;
}
