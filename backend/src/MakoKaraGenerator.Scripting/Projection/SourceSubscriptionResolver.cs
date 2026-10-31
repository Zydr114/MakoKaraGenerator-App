using System.Collections.Immutable;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Scripting.Projection;

/// <summary>一次固定输入的源订阅解析；完整历史与输出范围仍独立。</summary>
public sealed class ResolvedSourceSubscription
{
    private ResolvedSourceSubscription? _outputScope;
    internal ResolvedSourceSubscription OutputScope => _outputScope ??= Create(_timeline, _binding, new SourceSubscription.BoundSources());
    internal bool AllowsMark(NodeId id)
    {
        if (_binding is EffectBinding.Tokens) return false;
        if (_binding is EffectBinding.Node) return _nodes.Contains(id);
        NodeId? current = id;
        while (current is { } ancestor)
        {
            if (_timeline.Scene.LeadIns.Any(cue => cue.ContainerId == ancestor && _lines.Contains(cue.TargetLineId))) return true;
            current = _timeline.Scene.GetNode(ancestor).ParentId;
        }
        return false;
    }
    private readonly TimelineProjection _timeline;
    private readonly EffectBinding _binding;
    private readonly SourceSubscription _subscription;
    private readonly ImmutableHashSet<TokenId> _tokens;
    private readonly ImmutableHashSet<LineId> _lines;
    private readonly ImmutableHashSet<NodeId> _nodes;
    private ResolvedSourceSubscription(TimelineProjection timeline, EffectBinding binding, SourceSubscription subscription)
    {
        _timeline = timeline; _binding = binding; _subscription = subscription;
        _nodes = binding is EffectBinding.Node node ? Descendants(timeline.Scene, node) : [];
        var bindings = timeline.Events.OfType<BindingEntered>().Select(static evt => evt.Binding).ToArray();
        var selected = timeline.Content.Lines.Where(line => SelectLine(line, bindings)).ToImmutableArray();
        _lines = selected.Select(static line => line.Id).ToImmutableHashSet();
        _tokens = timeline.Content.Tokens.Where(token => SelectToken(token, bindings)).Select(static token => token.Id).ToImmutableHashSet();
        LineIds = [.. timeline.Content.Lines.Where(line => _lines.Contains(line.Id)).Select(static line => line.Id)];
        TokenIds = [.. timeline.Content.Tokens.Where(token => _tokens.Contains(token.Id)).Select(static token => token.Id)];
    }
    /// <summary>按源顺序解析的真实订阅行；纯组／标记可为空。</summary>
    public ImmutableArray<LineId> LineIds { get; }
    /// <summary>按源顺序解析的真实订阅元素；没有假提示元素。</summary>
    public ImmutableArray<TokenId> TokenIds { get; }
    /// <summary>验证输出绑定和独立订阅；非法目标不自动变为工程范围。</summary>
    public static ResolvedSourceSubscription Create(TimelineProjection timeline, EffectBinding binding, SourceSubscription subscription)
    {
        ArgumentNullException.ThrowIfNull(timeline); ArgumentNullException.ThrowIfNull(binding); ArgumentNullException.ThrowIfNull(subscription);
        ValidateBinding(timeline.Content, timeline.Scene, binding);
        if (subscription is SourceSubscription.Branch branch && !timeline.Content.Branches.Any(item => item.Id == branch.Id))
            throw new PreparedFrameException("InvalidSourceSubscription", "订阅分支不存在：" + branch.Id.Value);
        return new(timeline, binding, subscription);
    }
    /// <summary>检查真实事件是否属于固定订阅；音频 hop 是全工程只读输入。</summary>
    public bool Matches(TimelineEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (_subscription is SourceSubscription.Project || evt is AudioFeatureTick) return true;
        if (_subscription is SourceSubscription.Branch branch) return BranchOf(evt) == branch.Id;
        return evt switch
        {
            TokenPerformanceEvent token => _tokens.Contains(token.TokenId),
            LinePerformanceEvent line => _lines.Contains(line.LineId),
            BindingTimelineEvent binding => ContainsAssociatedBinding(binding.Binding),
            ElementTimelineEvent { Element: SceneElementIdentity.Lyric lyric } => ContainsLyric(lyric),
            ElementTimelineEvent { Element: SceneElementIdentity.Mark mark } => _binding is EffectBinding.Node
                ? ContainsNode(mark.NodeId) : ContainsBinding(mark.Target),
            ElementTimelineEvent { Element: SceneElementIdentity.Group group } => ContainsGroup(group),
            LeadInTimelineEvent cue => ContainsAssociatedBinding(cue.Target.Target) || ContainsIndicator(cue.Target.Cue.ContainerId),
            CursorTransitionEvent transition => ContainsCursor(transition.From) && ContainsCursor(transition.To),
            _ => false,
        };
    }
    private bool ContainsCursor(CursorIdentity cursor)
    {
        bool source = cursor.TokenId is { } token ? _tokens.Contains(token) : _lines.Contains(cursor.LineId);
        return source && _timeline.Events.OfType<BindingEntered>().Any(evt => evt.Binding.BindingId == cursor.BindingId
            && evt.Binding.LineId == cursor.LineId && ContainsBinding(evt.Binding));
    }
    private bool SelectToken(SourceToken token, FrameLine[] bindings)
    {
        if (_subscription is SourceSubscription.Project) return true;
        if (_subscription is SourceSubscription.Branch branch) return token.BranchId == branch.Id;
        if (!_lines.Contains(token.LineId)) return false;
        if (_binding is EffectBinding.Tokens ids) return ids.Ids.Contains(token.Id);
        if (_binding is not EffectBinding.Node) return true;
        return _timeline.Scene.Nodes.Any(node => _nodes.Contains(node.Id) && node.LineId == token.LineId
            && (node.RubyId is not { } ruby || _timeline.Content.GetRuby(ruby).BaseTokenId == token.Id))
            || bindings.Any(binding => binding.LineId == token.LineId && ContainsBinding(binding));
    }
    private bool SelectLine(SourceLine line, FrameLine[] bindings)
    {
        if (_subscription is SourceSubscription.Project) return true;
        if (_subscription is SourceSubscription.Branch branch) return line.BranchId == branch.Id;
        return _binding switch
        {
            EffectBinding.Project => true,
            EffectBinding.Branch selected => line.BranchId == selected.Id,
            EffectBinding.Lines ids => ids.Ids.Contains(line.Id),
            EffectBinding.Tokens ids => line.TokenIds.Any(ids.Ids.Contains),
            EffectBinding.Clips ids => _timeline.Scene.Clips.Any(clip => ids.Ids.Contains(clip.Id) && clip.LineIds.Contains(line.Id)),
            EffectBinding.Node => _timeline.Scene.Nodes.Any(node => node.LineId == line.Id && _nodes.Contains(node.Id))
                || bindings.Any(binding => binding.LineId == line.Id && ContainsBinding(binding)),
            _ => false,
        };
    }
    private bool ContainsBinding(FrameLine binding) => _binding switch
    {
        EffectBinding.Project => true,
        EffectBinding.Branch branch => binding.BranchId == branch.Id,
        EffectBinding.Lines ids => ids.Ids.Contains(binding.LineId),
        EffectBinding.Tokens ids => _timeline.Content.GetLine(binding.LineId).TokenIds.Any(ids.Ids.Contains),
        EffectBinding.Clips ids => binding.ClipId is { } clip && ids.Ids.Contains(clip),
        EffectBinding.Node node => _nodes.Contains(binding.NodeId) || node.IncludeDescendants && _nodes.Contains(binding.ParentId),
        _ => false,
    };
    private bool ContainsAssociatedBinding(FrameLine binding) => ContainsBinding(binding) || _binding is EffectBinding.Node
        && _timeline.Scene.Nodes.Any(node => _nodes.Contains(node.Id) && node.RubyId is not null && node.LineId == binding.LineId);
    private bool ContainsLyric(SceneElementIdentity.Lyric lyric)
    {
        if (_binding is EffectBinding.Node node)
            return ContainsNode(lyric.NodeId) || node.IncludeDescendants && _nodes.Contains(lyric.Binding.ParentId);
        if (_binding is EffectBinding.Tokens && lyric.RubyId is { } ruby)
            return _tokens.Contains(_timeline.Content.GetRuby(ruby).BaseTokenId);
        return ContainsBinding(lyric.Binding);
    }
    private bool ContainsNode(NodeId id) => _binding is EffectBinding.Node ? _nodes.Contains(id) : false;
    private bool ContainsIndicator(NodeId container)
    {
        foreach (var selected in _nodes)
        {
            NodeId? current = selected;
            while (current is { } id)
            {
                if (id == container) return true;
                current = _timeline.Scene.GetNode(id).ParentId;
            }
        }
        return false;
    }
    private bool ContainsGroup(SceneElementIdentity.Group group) => _binding switch
    {
        EffectBinding.Project => true,
        EffectBinding.Branch branch => group.BranchId == branch.Id,
        EffectBinding.Node => _nodes.Contains(group.NodeId),
        _ => false,
    };
    private static BranchId? BranchOf(TimelineEvent evt) => evt switch
    {
        TokenPerformanceEvent token => token.BranchId,
        LinePerformanceEvent line => line.BranchId,
        BindingTimelineEvent binding => binding.Binding.BranchId,
        ElementTimelineEvent { Element: SceneElementIdentity.Lyric lyric } => lyric.Binding.BranchId,
        ElementTimelineEvent { Element: SceneElementIdentity.Mark mark } => mark.Target.BranchId,
        ElementTimelineEvent { Element: SceneElementIdentity.Group group } => group.BranchId,
        LeadInTimelineEvent cue => cue.Target.Target.BranchId,
        CursorTransitionEvent transition => transition.From.BranchId,
        _ => null,
    };
    private static ImmutableHashSet<NodeId> Descendants(SceneCatalog scene, EffectBinding.Node selected)
    {
        ImmutableHashSet<NodeId>.Builder result = ImmutableHashSet.CreateBuilder<NodeId>(); result.Add(selected.Id);
        if (selected.IncludeDescendants)
            foreach (var node in scene.Nodes)
            {
                NodeId? current = node.ParentId;
                while (current is { } id)
                {
                    if (id == selected.Id) { result.Add(node.Id); break; }
                    current = scene.GetNode(id).ParentId;
                }
            }
        return result.ToImmutable();
    }
    /// <summary>验证固定输出身份；纯帧工厂和事件解析共享同一当前目录门禁。</summary>
    public static void ValidateBinding(ContentCatalog content, SceneCatalog scene, EffectBinding binding)
    {
        ArgumentNullException.ThrowIfNull(content); ArgumentNullException.ThrowIfNull(scene); ArgumentNullException.ThrowIfNull(binding);
        bool valid = binding switch
        {
            EffectBinding.Project => true,
            EffectBinding.Branch branch => content.Branches.Any(item => item.Id == branch.Id),
            EffectBinding.Lines lines => !lines.Ids.IsDefaultOrEmpty && lines.Ids.Distinct().Count() == lines.Ids.Length
                && lines.Ids.All(id => content.Lines.Any(item => item.Id == id)),
            EffectBinding.Tokens tokens => !tokens.Ids.IsDefaultOrEmpty && tokens.Ids.Distinct().Count() == tokens.Ids.Length
                && tokens.Ids.All(id => content.Tokens.Any(item => item.Id == id)),
            EffectBinding.Clips clips => !clips.Ids.IsDefaultOrEmpty && clips.Ids.Distinct().Count() == clips.Ids.Length
                && clips.Ids.All(id => scene.Clips.Any(item => item.Id == id)),
            EffectBinding.Node node => scene.Nodes.Any(item => item.Id == node.Id),
            _ => false,
        };
        if (!valid) throw new PreparedFrameException("InvalidEffectBinding", "效果绑定不存在、重复或为空，不自动换成工程范围。");
    }
}
