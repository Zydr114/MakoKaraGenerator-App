using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting.Projection;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class SourceSubscriptionTests
{
    [Fact]
    public void 项目订阅比输出范围广但不会改写输出绑定()
    {
        var timeline = TimelineProjection.Create(TimelineProjectionTests.Source(), ReplayReason.Initial);
        EffectBinding binding = new EffectBinding.Tokens([new("a")]);
        var bound = ResolvedSourceSubscription.Create(timeline, binding, new SourceSubscription.BoundSources());
        var broad = ResolvedSourceSubscription.Create(timeline, binding, new SourceSubscription.Project());
        Assert.Equal([new TokenId("a")], bound.TokenIds.ToArray());
        Assert.Equal(4, broad.TokenIds.Length);
        Assert.Single(timeline.Events.OfType<TokenStarted>(), bound.Matches);
        Assert.Equal(3, timeline.Events.OfType<TokenStarted>().Count(broad.Matches));
        Assert.Equal([new TokenId("a")], Assert.IsType<EffectBinding.Tokens>(binding).Ids.ToArray());
        Assert.Equal(3, timeline.Events.OfType<TokenStarted>().Count(evt => evt.TimeUs <= 5000));
    }

    [Fact]
    public void 分支订阅可以关联输出以外源分支但不自动加入翻译()
    {
        var timeline = TimelineProjection.Create(ProjectSnapshot.FromProject(LeadInFixture.Create()), ReplayReason.Initial);
        var bound = ResolvedSourceSubscription.Create(timeline, new EffectBinding.Lines([new("first")]), new SourceSubscription.BoundSources());
        var translated = ResolvedSourceSubscription.Create(timeline, new EffectBinding.Lines([new("first")]), new SourceSubscription.Branch(new("translation")));
        Assert.Equal([new LineId("first")], bound.LineIds.ToArray());
        Assert.DoesNotContain(timeline.Events.Where(bound.Matches), evt => evt is BindingTimelineEvent binding && binding.Binding.BranchId.Value == "translation");
        Assert.Equal(6, translated.LineIds.Length);
        Assert.All(translated.LineIds, id => Assert.StartsWith("translation-", id.Value));
        Assert.DoesNotContain(timeline.Events.OfType<TokenStarted>(), translated.Matches);
        Assert.Equal(6, timeline.Events.OfType<BindingEntered>().Count(translated.Matches));
        Assert.Contains(timeline.Events.OfType<LeadInStarted>(), bound.Matches);
    }

    [Fact]
    public void 片段只订阅成员而不吞并默认归属或其他片段()
    {
        var timeline = TimelineProjection.Create(ProjectSnapshot.FromProject(LeadInFixture.Create(true, true)), ReplayReason.Initial);
        var selected = ResolvedSourceSubscription.Create(timeline, new EffectBinding.Clips([new("chorus-clip")]), new SourceSubscription.BoundSources());
        Assert.Equal([new LineId("chorus")], selected.LineIds.ToArray());
        Assert.Equal([new TokenId("chorus-a"), new TokenId("chorus-b")], selected.TokenIds.ToArray());
        var entered = Assert.Single(timeline.Events.OfType<BindingEntered>(), selected.Matches);
        Assert.Equal(new ClipId("chorus-clip"), entered.Binding.ClipId);
        Assert.Equal(37_000_000, entered.TimeUs);
        Assert.Single(timeline.Events.OfType<LeadInStarted>(), selected.Matches);
        Assert.Equal(2, timeline.Events.OfType<TokenStarted>().Count(selected.Matches));
        Assert.DoesNotContain(timeline.Events.OfType<ElementVisible>(), evt => evt.Element is SceneElementIdentity.Group && selected.Matches(evt));
    }

    [Fact]
    public void 槽位包含后代时订阅所有真实装载而不是当前帧或静态父树()
    {
        var timeline = TimelineProjection.Create(ProjectSnapshot.FromProject(LeadInFixture.Create(true)), ReplayReason.Initial);
        var firstBinding = timeline.Events.OfType<BindingEntered>().Single(evt => evt.Binding.LineId.Value == "first").Binding;
        var selected = ResolvedSourceSubscription.Create(timeline, new EffectBinding.Node(firstBinding.ParentId, true), new SourceSubscription.BoundSources());
        Assert.Equal([new LineId("first"), new LineId("tail-a")], selected.LineIds.ToArray());
        Assert.Equal(4, timeline.Events.OfType<TokenStarted>().Count(selected.Matches));
        Assert.Equal(2, timeline.Events.OfType<BindingEntered>().Count(selected.Matches));
        Assert.Contains(timeline.Events.OfType<ElementVisible>(), evt => evt.Element.NodeId == firstBinding.ParentId && selected.Matches(evt));
        var groupOnly = ResolvedSourceSubscription.Create(timeline, new EffectBinding.Node(firstBinding.ParentId, false), new SourceSubscription.BoundSources());
        Assert.Empty(groupOnly.LineIds); Assert.Empty(groupOnly.TokenIds);
        Assert.DoesNotContain(timeline.Events.OfType<TokenStarted>(), groupOnly.Matches);
        Assert.Contains(timeline.Events.OfType<ElementVisible>(), evt => evt.Element.NodeId == firstBinding.ParentId && groupOnly.Matches(evt));
    }

    [Fact]
    public void 注音节点只关联基础元素且不会订阅全句其他字()
    {
        var timeline = TimelineProjection.Create(TimelineProjectionTests.Source(), ReplayReason.Initial);
        var ruby = timeline.Scene.Nodes.Single(node => node.RubyId is not null);
        var selected = ResolvedSourceSubscription.Create(timeline, new EffectBinding.Node(ruby.Id, false), new SourceSubscription.BoundSources());
        Assert.Equal([new TokenId("a")], selected.TokenIds.ToArray());
        Assert.Equal([new LineId("first")], selected.LineIds.ToArray());
        Assert.Single(timeline.Events.OfType<TokenStarted>(), selected.Matches);
        var visible = Assert.Single(timeline.Events.OfType<ElementVisible>(), selected.Matches);
        Assert.Equal(ruby.Id, visible.Element.NodeId);
        Assert.Single(timeline.Events.OfType<BindingEntered>(), selected.Matches);
    }

    [Fact]
    public void 纯前奏组与文字标记没有虚构歌词但有真实可见和前奏事件()
    {
        var timeline = TimelineProjection.Create(ProjectSnapshot.FromProject(LeadInFixture.Create()), ReplayReason.Initial);
        var cue = timeline.Scene.LeadIns[0];
        var indicator = ResolvedSourceSubscription.Create(timeline, new EffectBinding.Node(cue.ContainerId, true), new SourceSubscription.BoundSources());
        Assert.Empty(indicator.LineIds); Assert.Empty(indicator.TokenIds);
        Assert.DoesNotContain(timeline.Events.OfType<TokenStarted>(), indicator.Matches);
        Assert.Equal(2, timeline.Events.OfType<LeadInStarted>().Count(indicator.Matches));
        var mark = Assert.IsType<SceneElementIdentity.Mark>(timeline.Events.OfType<ElementVisible>().First(evt => evt.Element is SceneElementIdentity.Mark).Element);
        var markOnly = ResolvedSourceSubscription.Create(timeline, new EffectBinding.Node(mark.NodeId, false), new SourceSubscription.BoundSources());
        Assert.Empty(markOnly.TokenIds);
        Assert.Contains(timeline.Events.OfType<ElementVisible>(), evt => evt.Element.NodeId == mark.NodeId && markOnly.Matches(evt));
        Assert.Equal(2, timeline.Events.OfType<LeadInStarted>().Count(markOnly.Matches));
        Assert.DoesNotContain(timeline.Events.OfType<BindingEntered>(), markOnly.Matches);
    }

    [Fact]
    public void 节点不含后代不会订阅注音或附着提示的可见事件()
    {
        var timeline = TimelineProjection.Create(ProjectSnapshot.FromProject(LeadInFixture.Create()), ReplayReason.Initial);
        var source = timeline.Scene.Nodes.Single(node => node.LineId?.Value == "first" && node.RubyId is null);
        var nodeOnly = ResolvedSourceSubscription.Create(timeline, new EffectBinding.Node(source.Id, false), new SourceSubscription.BoundSources());
        var withRuby = ResolvedSourceSubscription.Create(timeline, new EffectBinding.Node(source.Id, true), new SourceSubscription.BoundSources());
        Assert.Single(timeline.Events.OfType<ElementVisible>(), nodeOnly.Matches);
        Assert.Equal(2, timeline.Events.OfType<ElementVisible>().Count(withRuby.Matches));
        Assert.DoesNotContain(timeline.Events.OfType<ElementVisible>(), evt => evt.Element is SceneElementIdentity.Mark && withRuby.Matches(evt));
        Assert.Single(timeline.Events.OfType<LeadInStarted>(), nodeOnly.Matches);
        // 精确另一个基础字仍读取整句装载，但不读取第一个基础字的注音可见事件。
        var anotherToken = ResolvedSourceSubscription.Create(timeline, new EffectBinding.Tokens([new("first-b")]), new SourceSubscription.BoundSources());
        Assert.DoesNotContain(timeline.Events.OfType<ElementVisible>(), evt => evt.Element is SceneElementIdentity.Lyric { RubyId: not null } && anotherToken.Matches(evt));
    }

    [Fact]
    public void 精确元素绑定不订阅范围外光标后继()
    {
        var timeline = TimelineProjection.Create(TimelineProjectionTests.Source(), ReplayReason.Initial);
        var binding = timeline.Events.OfType<BindingEntered>().First().Binding;
        CursorIdentity Identity(string id)
        {
            var token = timeline.Content.GetToken(new(id)); var range = token.TimeRange!.Value;
            return new(token.BranchId, token.LineId, binding.BindingId, range, ProgressSource.Token,
                token.Id, StableIdentities.Occurrence(token.BranchId, token.LineId, token.Id, range));
        }
        var transition = new CursorTransitionEvent(new("track"), Identity("a"), Identity("b"), 2000, CursorTransitionReason.TokenChange, new(0, 0, 2), ReplayReason.Initial);
        var narrow = ResolvedSourceSubscription.Create(timeline, new EffectBinding.Tokens([new("a")]), new SourceSubscription.BoundSources());
        var both = ResolvedSourceSubscription.Create(timeline, new EffectBinding.Tokens([new("a"), new("b")]), new SourceSubscription.BoundSources());
        Assert.False(narrow.Matches(transition)); Assert.True(both.Matches(transition));
        var broad = ResolvedSourceSubscription.Create(timeline, new EffectBinding.Tokens([new("a")]), new SourceSubscription.Project());
        Assert.True(broad.Matches(transition));
    }

    [Fact]
    public void 失效空或重复绑定与订阅拒绝且不会换成工程范围()
    {
        var timeline = TimelineProjection.Create(TimelineProjectionTests.Source(), ReplayReason.Initial);
        EffectBinding[] invalid = [new EffectBinding.Tokens([]), new EffectBinding.Tokens([new("a"), new("a")]),
            new EffectBinding.Tokens([new("unknown")]), new EffectBinding.Lines([new("unknown")]),
            new EffectBinding.Lines(default), new EffectBinding.Clips([new("unknown")]), new EffectBinding.Node(new("unknown"), true),
            new EffectBinding.Branch(new("unknown"))];
        foreach (var binding in invalid)
            Assert.Equal("InvalidEffectBinding", Assert.Throws<PreparedFrameException>(() => ResolvedSourceSubscription.Create(timeline, binding, new SourceSubscription.Project())).Code);
        Assert.Equal("InvalidSourceSubscription", Assert.Throws<PreparedFrameException>(() => ResolvedSourceSubscription.Create(timeline,
            new EffectBinding.Project(), new SourceSubscription.Branch(new("unknown")))).Code);
    }
}
