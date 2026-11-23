using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting.Projection;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class CursorTransitionProjectionTests
{
    [Fact]
    public void 句内定时元素与行依据连接真实端点且缺时间不制造身份()
    {
        var snapshot = TimelineProjectionTests.Source(); var timeline = TimelineProjection.Create(snapshot, ReplayReason.Initial);
        var edges = timeline.CursorTransitions;
        Assert.Equal(["a", "b", "c"], edges.Select(edge => edge.From.TokenId!.Value.Value));
        Assert.Equal(["b", "c", null], edges.Select(edge => edge.To.TokenId?.Value));
        Assert.Equal([0L, 1000L, 1000L], edges.Select(edge => edge.GapUs));
        Assert.Equal([2000L, 4000L, 6000L], edges.Select(edge => edge.TimeUs));
        Assert.Equal(ProgressSource.Line, edges[^1].To.ProgressSource); Assert.Null(edges[^1].To.OccurrenceId);
        Assert.Equal(CursorTransitionReason.LineChange, edges[^1].TransitionReason);
        Assert.Same(edges[0], timeline.Events.OfType<CursorTransitionEvent>().First());
        foreach (var edge in edges)
        {
            Assert.Contains(PlaybackProjection.GetSources(snapshot, LyricSceneSampler.SampleFrame(snapshot, edge.From.TimeRange.EndUs - 1)),
                source => source.TrackId == edge.TrackId && source.Identity == edge.From);
            Assert.Contains(PlaybackProjection.GetSources(snapshot, LyricSceneSampler.SampleFrame(snapshot, edge.To.TimeRange.StartUs)),
                source => source.TrackId == edge.TrackId && source.Identity == edge.To);
            Assert.Equal([0, 2, 3], timeline.Events.Where(evt => evt.TimeUs == 2000).Select(evt => evt.BoundaryPriority).Distinct());
        }
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void 前奏不创建起唱后继且跨槽位片段只认共享装载(bool slots)
    {
        var snapshot = ProjectSnapshot.FromProject(LeadInFixture.Create(slots, true));
        var timeline = TimelineProjection.Create(snapshot, ReplayReason.Initial);
        Assert.Equal(11, timeline.CursorTransitions.Length);
        var intoCue = Assert.Single(timeline.CursorTransitions, edge => edge.To.LineId.Value == "chorus" && edge.To.TokenId?.Value == "chorus-a");
        Assert.Equal(40_000_000, intoCue.TimeUs); Assert.Equal(20_000_000, intoCue.GapUs);
        var preparedBinding = timeline.Events.OfType<LeadInStarted>().Single(evt => evt.CueId.Value == "cue-chorus").Target.Target.BindingId;
        Assert.Equal(preparedBinding, intoCue.To.BindingId);
        Assert.DoesNotContain(timeline.CursorTransitions, edge => edge.From.BranchId.Value == "translation" || edge.TimeUs == 37_000_000);
        var chorusOnly = timeline.GetCursorSuccessors(intoCue.From, new EffectBinding.Clips([new("chorus-clip")]));
        Assert.Empty(chorusOnly); // 宽源订阅不能扩大此输出范围。
        var chorusToken = timeline.Content.GetToken(new("chorus-a"));
        var internalEdge = Assert.Single(timeline.CursorTransitions, edge => edge.From.TokenId == chorusToken.Id);
        Assert.Equal(internalEdge, Assert.Single(timeline.GetCursorSuccessors(internalEdge.From, new EffectBinding.Clips([new("chorus-clip")]))));
    }
    [Fact]
    public void 窄输出不跨越范围外后继且节点剪辑按实际Binding过滤()
    {
        var timeline = TimelineProjection.Create(ProjectSnapshot.FromProject(LeadInFixture.Create(true, true)), ReplayReason.Initial);
        var edge = Assert.Single(timeline.CursorTransitions, edge => edge.From.TokenId?.Value == "second-b");
        Assert.Empty(timeline.GetCursorSuccessors(edge.From, new EffectBinding.Lines([new("second"), new("tail-a")])));
        Assert.Empty(timeline.GetCursorSuccessors(edge.From, new EffectBinding.Tokens([new("second-b")])));
        Assert.Equal(edge, Assert.Single(timeline.GetCursorSuccessors(edge.From, new EffectBinding.Tokens([new("second-b"), new("chorus-a")]))));
        var node = timeline.Scene.Programs.Single(program => program.BranchId.Value == "primary").DefaultNodeId;
        var scoped = ResolvedSourceSubscription.Create(timeline, new EffectBinding.Node(node, true), new SourceSubscription.BoundSources());
        var firstInternal = timeline.CursorTransitions.First();
        Assert.True(scoped.Matches(firstInternal)); Assert.False(scoped.Matches(edge));
        var wrongBinding = new CursorIdentity(firstInternal.To.BranchId, firstInternal.To.LineId, edge.To.BindingId, firstInternal.To.TimeRange,
            firstInternal.To.ProgressSource, firstInternal.To.TokenId, firstInternal.To.OccurrenceId);
        var forged = new CursorTransitionEvent(firstInternal.TrackId, firstInternal.From, wrongBinding, firstInternal.TimeUs,
            firstInternal.TransitionReason, firstInternal.SourceOrder, ReplayReason.Initial);
        Assert.False(scoped.Matches(forged));
        Assert.True(ResolvedSourceSubscription.Create(timeline, new EffectBinding.Node(node, true), new SourceSubscription.Project()).Matches(edge));
    }
    [Fact]
    public void 并发起唱保留全部合法后继而重叠和逆时间不伪造飞行空档()
    {
        var snapshot = Rows(Row("from", 0, 500), Row("left", 800, 1200), Row("right", 800, 1300), Row("after", 1100, 1600));
        var timeline = TimelineProjection.Create(snapshot, ReplayReason.Initial);
        var from = timeline.CursorTransitions.First().From;
        var successors = timeline.GetCursorSuccessors(from, new EffectBinding.Project());
        Assert.Equal(["left", "right"], successors.Select(edge => edge.To.LineId.Value));
        Assert.All(successors, edge => Assert.Equal(300, edge.GapUs));
        Assert.DoesNotContain(timeline.CursorTransitions, edge => edge.From.LineId.Value == "left" && edge.To.LineId.Value == "right");
        Assert.Equal(3, PlaybackProjection.GetSources(snapshot, LyricSceneSampler.SampleFrame(snapshot, 1150)).Length);
        var overlapping = timeline.CursorTransitions.Where(edge => edge.To.LineId.Value == "after").ToArray();
        Assert.Equal(2, overlapping.Length); Assert.All(overlapping, edge => { Assert.Equal(0, edge.GapUs); Assert.Equal(1100, edge.TimeUs); });
        var reverse = TimelineProjection.Create(Rows(Row("late", 1000, 1400), Row("early", 500, 900)), ReplayReason.Initial);
        Assert.Empty(reverse.CursorTransitions);
    }
    [Fact]
    public void 未定时行阻断跨句且不可见分支不给出假装载()
    {
        var snapshot = Rows(Row("from", 0, 500), LyricLine.Create("unknown", "?"), Row("next", 800, 1200));
        Assert.Empty(TimelineProjection.Create(snapshot, ReplayReason.Initial).CursorTransitions);
        var hidden = TimelineProjectionTests.Source();
        hidden = hidden with { Lyrics = hidden.Lyrics with { Branches = [hidden.Lyrics.PrimaryBranch! with { TimingMode = LyricBranchTimingMode.None }] } };
        var timeline = TimelineProjection.Create(hidden, ReplayReason.Initial);
        Assert.Empty(timeline.CursorTransitions); Assert.NotEmpty(timeline.Events.OfType<TokenStarted>());
    }
    [Fact]
    public void 槽位提前替换旧句时不补造离场端点但源完成事件保持()
    {
        var original = Project.Create("槽位替换", lyrics: LyricsDocument.Create([Row("from", 0, 500), Row("next", 300, 800)]));
        var profile = SlotSequenceAnimationProfile.Default with { MaxVisibleLines = 1,
            RevealMode = SlotRevealMode.LeadBeforeNextStart, ConflictPolicy = SlotConflictPolicy.ReplacePrevious,
            Slots = [SlotSequenceSlot.Create("only", ScenePlacement.Default)] };
        var project = PresentationEditor.SetBranch(original, new() { BranchId = "primary", SlotSequenceProfile = profile });
        Assert.NotSame(original, project); // 正式树校验接受该明确的槽位替换策略。
        var snapshot = ProjectSnapshot.FromProject(project); var timeline = TimelineProjection.Create(snapshot, ReplayReason.Initial);
        Assert.Equal("from", Assert.Single(PlaybackProjection.GetSources(snapshot, LyricSceneSampler.SampleFrame(snapshot, 200))).Identity.LineId.Value);
        Assert.DoesNotContain(PlaybackProjection.GetSources(snapshot, LyricSceneSampler.SampleFrame(snapshot, 499)),
            item => item.Identity.LineId.Value == "from");
        Assert.Empty(timeline.CursorTransitions);
        Assert.Contains(timeline.Events.OfType<TokenCompleted>(), evt => evt.TokenId.Value == "from-token" && evt.TimeUs == 500);
        Assert.Equal("next", Assert.Single(PlaybackProjection.GetSources(snapshot, LyricSceneSampler.SampleFrame(snapshot, 300))).Identity.LineId.Value);
    }
    [Fact]
    public void 双分支事件互不接续且重建身份顺序与历史不受FPS影响()
    {
        var snapshot = Rows(Row("from", 0, 500), Row("to", 800, 1200));
        snapshot = snapshot with { Lyrics = snapshot.Lyrics with { Branches = snapshot.Lyrics.Branches.Add(
            LyricBranch.Create("other", "另一分支", LyricBranchRole.Custom, [Row("other-from", 0, 500), Row("other-to", 800, 1200)])) } };
        var original = TimelineProjection.Create(snapshot, ReplayReason.Initial);
        Assert.Equal(2, original.CursorTransitions.Length);
        Assert.All(original.CursorTransitions, edge => Assert.Equal(edge.From.BranchId, edge.To.BranchId));
        foreach (var reason in Enum.GetValues<ReplayReason>()) foreach (int fps in new[] { 24, 30, 60 })
        {
            var replay = TimelineProjection.Create(snapshot with { Composition = snapshot.Composition with { FrameRate = new(fps, 1) } }, reason);
            Assert.Equal(original.Events.Select(evt => evt.EventId), replay.Events.Select(evt => evt.EventId));
            Assert.All(replay.CursorTransitions, edge => Assert.Equal(reason, edge.ReplayReason));
            Assert.DoesNotContain(replay.Events.OfType<CursorTransitionEvent>(), evt => evt.TimeUs <= 799);
        }
    }
    internal static LyricLine Row(string id, long start, long end) => LyricLine.Create(id, "歌", new(start, end), TimingStatus.Edited,
        [LyricToken.Create(id + "-token", "歌", new(start, end), TimingStatus.Edited)]);
    private static ProjectSnapshot Rows(params LyricLine[] rows)
    {
        var project = Project.Create("后继", lyrics: LyricsDocument.Create(rows));
        var profile = SlotSequenceAnimationProfile.Default with { MaxVisibleLines = 4,
            RevealMode = SlotRevealMode.LeadBeforeNextStart, Slots = [.. Enumerable.Range(0, 4).Select(index =>
                SlotSequenceSlot.Create("slot-" + index, ScenePlacement.Create(new(.5, .2 + index * .2))))] };
        project = PresentationEditor.SetBranch(project, new() { BranchId = "primary", SlotSequenceProfile = profile });
        return ProjectSnapshot.FromProject(project);
    }
}
