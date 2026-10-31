using System.Text.Json;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting.Projection;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class TimelineProjectionTests
{
    [Fact]
    public void 起唱使用真实元素时间且缺时间字和提前可见均不增加次数()
    {
        var snapshot = Source(); var timeline = TimelineProjection.Create(snapshot, ReplayReason.Initial);
        var starts = timeline.Events.OfType<TokenStarted>().ToArray();
        Assert.Equal([1000L, 2000L, 4000L], starts.Select(evt => evt.TimeUs));
        Assert.Equal(["a", "b", "c"], starts.Select(evt => evt.TokenId.Value));
        Assert.DoesNotContain(starts, evt => evt.TokenId.Value == "missing");
        Assert.Equal(3, starts.Select(evt => evt.OccurrenceId).Distinct().Count());
        var performances = timeline.Events.OfType<LinePerformanceStarted>().ToArray();
        Assert.Equal([1000L, 6000L], performances.Select(evt => evt.TimeUs));
        Assert.Equal([ProgressSource.Token, ProgressSource.Line], performances.Select(evt => evt.ProgressSource));
        Assert.Equal(new TimeRange(1000, 5000), performances[0].PerformanceRange);
        Assert.Equal(new TimeRange(6000, 7000), performances[1].PerformanceRange);
        var atSharedEdge = timeline.Events.Where(evt => evt.TimeUs == 2000).ToArray();
        Assert.IsType<TokenCompleted>(atSharedEdge[0]); Assert.IsType<TokenStarted>(atSharedEdge[1]);
        Assert.False(LyricSampler.SampleLine(snapshot.Lyrics.Lines[0], 2000).Tokens[0].IsCurrent);
        Assert.True(LyricSampler.SampleLine(snapshot.Lyrics.Lines[0], 2000).Tokens[2].IsCurrent);
        Assert.Equal(0, timeline.Events.OfType<BindingEntered>().First().TimeUs);
        Assert.Empty(timeline.HistoryAt(999).GetStarts(new(0, 7000), new SourceFilter.Project()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 源起唱不受静态绘制或分支不可见策略抹去(bool hidden)
    {
        var snapshot = Source();
        if (hidden)
            snapshot = snapshot with { Lyrics = snapshot.Lyrics with { Branches = [snapshot.Lyrics.PrimaryBranch! with { TimingMode = LyricBranchTimingMode.None }] } };
        else
            snapshot = snapshot with { LyricPresentation = snapshot.LyricPresentation.SetBranch(new()
                { BranchId = snapshot.Lyrics.PrimaryBranchId, AnimationPolicy = LyricBranchAnimationPolicy.Static }) };
        var timeline = TimelineProjection.Create(snapshot, ReplayReason.Initial);
        Assert.Equal(3, timeline.Events.OfType<TokenStarted>().Count());
        Assert.Equal(3, timeline.HistoryAt(5000).GetStarts(new(0, 7000), new SourceFilter.Project()).Length);
        if (hidden)
            Assert.Empty(timeline.Events.OfType<BindingTimelineEvent>());
        else
            Assert.False(LyricSceneSampler.SampleFrame(snapshot, 1500).Lines.Single().Frame.Tokens[0].IsCurrent);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void 前奏沿用装载且步骤起唱与离场均遵循共享采样器(bool slots, bool crossClip)
    {
        var snapshot = ProjectSnapshot.FromProject(LeadInFixture.Create(slots, crossClip));
        var timeline = TimelineProjection.Create(snapshot, ReplayReason.Initial);
        var first = timeline.Events.OfType<LeadInStarted>().Single(evt => evt.CueId.Value == "cue-first");
        Assert.Equal(9_000_000, first.TimeUs); Assert.Equal(3, first.Target.MarkCount);
        Assert.Equal(DisplayPhase.Preparing, first.Target.Target.DisplayPhase);
        var steps = timeline.Events.OfType<LeadInStepChanged>().Where(evt => evt.CueId == first.CueId).ToArray();
        Assert.Equal([10_000_000L, 11_000_000L], steps.Select(evt => evt.TimeUs));
        Assert.Equal([1, 2], steps.Select(evt => evt.ElapsedMarks));
        var complete = timeline.Events.OfType<LeadInCompleted>().Single(evt => evt.CueId == first.CueId);
        Assert.Equal(12_000_000, complete.TimeUs); Assert.Equal(3, complete.ElapsedMarks);
        Assert.Equal(1, timeline.Events.OfType<BindingEntered>().Count(evt => evt.Binding.LineId.Value == "first"));
        Assert.Single(timeline.Events.OfType<TokenStarted>(), evt => evt.TokenId.Value == "first-a");
        Assert.Equal(12_000_000, timeline.Events.OfType<TokenStarted>().Single(evt => evt.TokenId.Value == "first-a").TimeUs);
        Assert.Equal(first.Target.Target.BindingId, FrameProjection.Lines(LyricSceneSampler.SampleFrame(snapshot, 12_000_000), timeline.Scene)
            .Single(line => line.LineId.Value == "first").BindingId);
        Assert.DoesNotContain(timeline.Events.OfType<TokenPerformanceEvent>(), evt => evt.BranchId.Value == "translation");
        Assert.Contains(timeline.Events.OfType<BindingEntered>(), evt => evt.Binding.LineId.Value == "translation-first" && evt.TimeUs == 9_000_000);
        Assert.Equal(12, timeline.Events.OfType<TokenStarted>().Count());
        var marks = timeline.Events.OfType<ElementVisible>().Where(evt => evt.TimeUs == 9_000_000 && evt.Element is SceneElementIdentity.Mark).ToArray();
        Assert.Equal(3, marks.Length);
        foreach (var evt in marks)
        {
            var mark = Assert.IsType<SceneElementIdentity.Mark>(evt.Element);
            Assert.Equal(first.CueId, mark.CueId); Assert.Equal(first.Target.Target.BindingId, mark.Target.BindingId);
            Assert.Null(timeline.Scene.GetNode(mark.NodeId).LineId);
        }
        // 在精确边界及不同帧率附近重建事件可见集合，与生产采样器直接对照。
        HashSet<long> times = [0, 57_000_000];
        foreach (long boundary in timeline.Events.Select(evt => evt.TimeUs).Distinct())
        {
            times.Add(boundary); times.Add(boundary + 1); if (boundary > 0) times.Add(boundary - 1);
            foreach (long fps in new long[] { 24, 30, 60 })
            {
                long frame = boundary * fps / 1_000_000;
                times.Add(frame * 1_000_000 / fps); times.Add((frame + 1) * 1_000_000 / fps);
            }
        }
        foreach (long time in times)
        {
            HashSet<BindingId> active = [];
            foreach (var evt in timeline.Events.Where(evt => evt.TimeUs <= time))
                if (evt is BindingEntered entered) Assert.True(active.Add(entered.Binding.BindingId));
                else if (evt is BindingLeft left) Assert.True(active.Remove(left.Binding.BindingId));
            var actual = FrameProjection.Lines(LyricSceneSampler.SampleFrame(snapshot, time), timeline.Scene);
            Assert.Equal(actual.Select(line => line.BindingId.Value).Order(StringComparer.Ordinal), active.Select(id => id.Value).Order(StringComparer.Ordinal));
        }
        var atSingStart = timeline.Events.Where(evt => evt.TimeUs == 12_000_000).ToArray();
        Assert.True(Array.IndexOf(atSingStart, complete) < Array.FindIndex(atSingStart, evt => evt is TokenStarted));
    }

    [Theory]
    [InlineData(ReplayReason.Seek)]
    [InlineData(ReplayReason.Loop)]
    [InlineData(ReplayReason.InputsChanged)]
    [InlineData(ReplayReason.Export)]
    [InlineData(ReplayReason.AnchorQuery)]
    public void 重放原因不改变事件身份或源顺序(ReplayReason reason)
    {
        var snapshot = ProjectSnapshot.FromProject(LeadInFixture.Create(true, true));
        var original = TimelineProjection.Create(snapshot, ReplayReason.Initial);
        var replay = TimelineProjection.Create(snapshot, reason);
        Assert.Equal(original.Events.Select(evt => evt.EventId), replay.Events.Select(evt => evt.EventId));
        Assert.Equal(original.Events.Select(evt => Payload(evt.WithReplayReason(reason))), replay.Events.Select(Payload));
        Assert.All(replay.Events, evt => Assert.Equal(reason, evt.ReplayReason));
        Assert.Same(original.Events[0], original.Events[0].WithReplayReason(ReplayReason.Initial));
        static string Payload(TimelineEvent evt) => JsonSerializer.Serialize(evt, evt.GetType());
    }

    [Fact]
    public void 音频事件只来自已完成分析的真实hop且不制造歌词身份()
    {
        var snapshot = Source();
        AudioFeatures analyzed = new("analysis-revision", 6000, [new(0, new(.1, .2, null, 0)), new(2000, new(.3, .4, .5, .8)), new(4000, new(0, 0, null, 0))]);
        var timeline = TimelineProjection.Create(snapshot, ReplayReason.Initial, analyzed);
        var hops = timeline.Events.OfType<AudioFeatureTick>().ToArray();
        Assert.Equal([0L, 2000L, 4000L], hops.Select(evt => evt.TimeUs));
        Assert.All(hops, evt => { Assert.Equal("analysis-revision", evt.AnalysisRevision); Assert.Equal(EventSourceOrder.Global, evt.SourceOrder); });
        Assert.Equal(3, timeline.Events.OfType<TokenStarted>().Count());
        Assert.Empty(TimelineProjection.Create(snapshot, ReplayReason.Initial, AudioFeatures.NotAnalyzed).Events.OfType<AudioFeatureTick>());
        Assert.Empty(TimelineProjection.Create(snapshot, ReplayReason.Initial, AudioFeatures.Unavailable(AudioAvailability.AnalysisFailed, "decode")).Events.OfType<AudioFeatureTick>());
    }

    [Fact]
    public void 固定时间线取消保持取消类型且非法重放原因拒绝()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => TimelineProjection.Create(Source(), ReplayReason.Initial, cancellationToken: cancellation.Token));
        Assert.Throws<ArgumentOutOfRangeException>(() => TimelineProjection.Create(Source(), (ReplayReason)99));
    }

    internal static ProjectSnapshot Source()
    {
        var line = LyricLine.Create("first", "请问请请", new(0, 6000), TimingStatus.Edited,
            [LyricToken.Create("a", "请", new(1000, 2000), TimingStatus.Edited), LyricToken.Create("missing", "问"),
                LyricToken.Create("b", "请", new(2000, 3000), TimingStatus.Edited), LyricToken.Create("c", "请", new(4000, 5000), TimingStatus.Edited)])
            with { RubyAnnotations = [RubyAnnotation.Create("ruby", "a", "qǐng")] };
        var fallback = LyricLine.Create("fallback", "无逐字时间", new(6000, 7000), TimingStatus.Edited);
        var missing = LyricLine.Create("untimed", "缺行时间");
        var project = Project.Create("timeline", lyrics: LyricsDocument.Create([line, fallback, missing]));
        return ProjectSnapshot.FromProject(project);
    }
}
