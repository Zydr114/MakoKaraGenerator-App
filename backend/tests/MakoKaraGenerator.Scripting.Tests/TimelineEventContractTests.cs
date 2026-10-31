using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Loading;
using MakoKaraGenerator.Scripting.Projection;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class TimelineEventContractTests
{
    [Fact]
    public void 事件种类封闭且身份与可选源语义由各派生记录表达()
    {
        var types = typeof(TimelineEvent).Assembly.GetTypes().Where(type => typeof(TimelineEvent).IsAssignableFrom(type) && !type.IsAbstract).ToArray();
        Assert.Equal(Enum.GetValues<TimelineEventKind>().Length, types.Length);
        Assert.All(types, type => Assert.True(type.IsSealed));
        Assert.DoesNotContain(typeof(TimelineEvent).GetProperties(), property => property.Name is "TokenId" or "LineId" or "BindingId");
        Assert.DoesNotContain(typeof(AudioFeatureTick).GetProperties(), property => property.Name is "TokenId" or "LineId");
        Assert.DoesNotContain(typeof(SceneElementIdentity.Mark).GetProperties(), property => property.Name == "TokenId");
        Assert.DoesNotContain(typeof(SceneElementIdentity.Group).GetProperties(), property => property.Name is "TokenId" or "LineId");
        Assert.All(typeof(TimelineEvent).GetProperties(), property => Assert.True(property.SetMethod is null || !property.SetMethod.IsPublic));
    }

    [Fact]
    public void 事件边界身份不能串改时间源状态或前奏步骤()
    {
        var timeline = TimelineProjection.Create(ProjectSnapshot.FromProject(LeadInFixture.Create()), ReplayReason.Initial);
        var start = timeline.Events.OfType<TokenStarted>().First();
        Assert.Throws<ArgumentException>(() => new TokenStarted(start.Source with { TimeRange = null }, start.SourceOrder, ReplayReason.Initial));
        Assert.Throws<ArgumentException>(() => new TokenStarted(start.Source with { TimingStatus = SourceTimingStatus.Missing }, start.SourceOrder, ReplayReason.Initial));
        Assert.Throws<ArgumentException>(() => new TokenStarted(start.Source, new(0, 0, 9), ReplayReason.Initial));
        Assert.Throws<ArgumentOutOfRangeException>(() => start.WithReplayReason((ReplayReason)99));
        var cue = timeline.Events.OfType<LeadInStarted>().First();
        Assert.Throws<ArgumentNullException>(() => new LeadInStarted(null!, cue.SourceOrder, ReplayReason.Initial));
        Assert.Throws<ArgumentNullException>(() => new LeadInCompleted(null!, cue.SourceOrder, ReplayReason.Initial));
        Assert.Throws<ArgumentException>(() => new LeadInStepChanged(cue.Target, 10_000_001, 1, cue.SourceOrder, ReplayReason.Initial));
        Assert.Throws<ArgumentException>(() => new LeadInStepChanged(cue.Target, 12_000_000, 3, cue.SourceOrder, ReplayReason.Initial));
        Assert.Throws<ArgumentException>(() => new LeadInEventTarget(cue.Target.Cue with { Enabled = false }, cue.Target.Target, 3));
        Assert.Throws<ArgumentException>(() => new BindingEntered(cue.Target.Target with { EnterProgress = double.NaN }, 0, cue.SourceOrder, ReplayReason.Initial));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EventSourceOrder(0, -1, 0));
    }

    [Fact]
    public void 前奏非整除步骤与Core共享整数微秒边界()
    {
        var timeline = TimelineProjection.Create(ProjectSnapshot.FromProject(LeadInFixture.Create()), ReplayReason.Initial);
        var original = timeline.Events.OfType<LeadInStarted>().First().Target;
        var target = new LeadInEventTarget(original.Cue with { TimeRange = new TimeRange(9_000_000, 12_000_001) }, original.Target, 3);
        var range = new Core.Time.TimeRange(9_000_000, 12_000_001);
        Assert.Equal(10_000_000, new LeadInStepChanged(target, LeadInTiming.StepBoundary(range, 3, 1), 1, new(0, 0), ReplayReason.Initial).TimeUs);
        Assert.Equal(11_000_000, new LeadInStepChanged(target, LeadInTiming.StepBoundary(range, 3, 2), 2, new(0, 0), ReplayReason.Initial).TimeUs);
        Assert.Equal(12_000_001, new LeadInCompleted(target, new(0, 0), ReplayReason.Initial).TimeUs);
    }

    [Fact]
    public void 行光标无假元素且历史拒绝冒用有逐字时间行的行依据()
    {
        var timeline = TimelineProjection.Create(TimelineProjectionTests.Source(), ReplayReason.Initial);
        var bindings = timeline.Events.OfType<BindingEntered>().ToArray();
        var first = bindings.Single(evt => evt.Binding.LineId.Value == "first").Binding;
        var fallback = bindings.Single(evt => evt.Binding.LineId.Value == "fallback").Binding;
        var from = new CursorIdentity(first.BranchId, first.LineId, first.BindingId, new(0, 6000), ProgressSource.Line);
        var to = new CursorIdentity(fallback.BranchId, fallback.LineId, fallback.BindingId, new(6000, 7000), ProgressSource.Line);
        Assert.Null(to.TokenId); Assert.Null(to.OccurrenceId);
        Assert.Throws<ArgumentException>(() => new CursorIdentity(to.BranchId, to.LineId, to.BindingId, to.TimeRange, ProgressSource.Line, new("a")));
        Assert.Throws<ArgumentException>(() => new CursorIdentity(to.BranchId, to.LineId, to.BindingId, to.TimeRange, ProgressSource.Token));
        var transition = new CursorTransitionEvent(new("track"), from, to, 6000, CursorTransitionReason.LineChange, new(0, 1), ReplayReason.Initial);
        Assert.Equal(0, transition.GapUs);
        Assert.Throws<ArgumentException>(() => new TimelineHistory(timeline.Content, 7000, [new(transition)]));
        // 固定表发布真实转换；有逐字时间的 From 不能冒用行依据。
        var actual = Assert.Single(timeline.CursorTransitions, edge => edge.To.ProgressSource == ProgressSource.Line);
        Assert.Equal(new TokenId("c"), actual.From.TokenId); Assert.Equal(new TimeRange(4000, 5000), actual.From.TimeRange);
        Assert.Equal(to, actual.To); Assert.Equal(1000, actual.GapUs);
        Assert.Equal(timeline.Events.Length, timeline.HistoryAt(7000).Records.Length);
    }

    [Fact]
    public async Task 真正SDK编译的消费者按occurrence计数且不把可见或行依据算作起唱()
    {
        var compiler = new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
        var result = await compiler.CompileAsync([new("src/EventConsumer.cs", """
            using System;
            using System.Collections.Generic;
            using MakoKaraGenerator.Scripting;
            public sealed class EventConsumer : IDisposable
            {
                private readonly HashSet<OccurrenceId> seen = new();
                public int Count { get; private set; }
                public void Read(TimelineEvent evt)
                {
                    if (evt is TokenStarted started && started.Source.Text == "请" && seen.Add(started.OccurrenceId)) Count++;
                }
                public void Dispose() { }
            }
            """)]);
        Assert.True(result.Succeeded, string.Join('\n', result.Diagnostics));
        using var module = new ScriptModule(result.PeImage.AsMemory(), result.PdbImage.AsMemory());
        var consumer = module.CreateInstance<IDisposable>("EventConsumer");
        var read = consumer.GetType().GetMethod("Read")!;
        var count = consumer.GetType().GetProperty("Count")!;
        var timeline = TimelineProjection.Create(TimelineProjectionTests.Source(), ReplayReason.Initial);
        foreach (var evt in timeline.Events) { read.Invoke(consumer, [evt]); read.Invoke(consumer, [evt]); }
        Assert.Equal(3, count.GetValue(consumer));
        var forbidden = await compiler.CompileAsync([new("src/Illegal.cs", """
            using MakoKaraGenerator.Scripting;
            public record Illegal : TokenStarted { }
            """)]);
        Assert.False(forbidden.Succeeded);
        Assert.Contains(forbidden.Diagnostics, diagnostic => diagnostic.Code == "CS0509");
    }
}
