using System.Collections.Immutable;
using System.Reflection;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Projection;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class HistoryPrefixSharingTests
{
    private static ImmutableArray<EventRecord> Field(TimelineHistory history, string name)
        => (ImmutableArray<EventRecord>)typeof(TimelineHistory).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(history)!;

    [Fact]
    public void 内置查询不物化数组且保留的旧快照不因后续历史增长()
    {
        var timeline = TimelineProjection.Create(TimelineProjectionTests.Source(), ReplayReason.Export);
        var before = timeline.HistoryAt(999); var current = timeline.HistoryAt(2000);
        Assert.True(Field(before, "_records").IsDefault); Assert.True(Field(current, "_records").IsDefault);
        Assert.Empty(before.GetStarts(new(0, long.MaxValue), new SourceFilter.Project()));
        Assert.Equal(2, current.GetStarts(new(0, long.MaxValue), new SourceFilter.Project()).Length);
        Assert.True(Field(current, "_records").IsDefault);
        var future = timeline.HistoryAt(7000);
        Assert.Equal(3, future.GetStarts(new(0, long.MaxValue), new SourceFilter.Project()).Length);
        Assert.True(Field(before, "_table") == Field(current, "_table") && Field(current, "_table") == Field(future, "_table"));
        Assert.All(before.Records, record => Assert.True(record.TimeUs <= 999));
        Assert.All(current.Records, record => Assert.True(record.TimeUs <= 2000));
        Assert.Equal(999, before.LastBoundaryUs); Assert.Equal(2000, current.LastBoundaryUs);
        var projection = current.Records; Assert.True(projection == current.Records);
        Assert.NotEqual(projection.Length, future.Records.Length);
    }

    [Fact]
    public async Task 数组兼容入口并发读取仍返回同一缓存且不能看到未来()
    {
        var timeline = TimelineProjection.Create(TimelineProjectionTests.Source(), ReplayReason.Initial);
        var prefix = timeline.HistoryAt(2000);
        var arrays = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => prefix.Records)));
        Assert.All(arrays, array => Assert.True(array == prefix.Records));
        Assert.All(prefix.Records, record => Assert.True(record.TimeUs <= 2000));
        Assert.Empty(prefix.GetStarts(new(2001, long.MaxValue), new SourceFilter.Project()));
    }

    [Fact]
    public void 公共构造仍逐项校验默认数组空元素负时刻及不可信来源()
    {
        var timeline = TimelineProjection.Create(TimelineProjectionTests.Source(), ReplayReason.Initial);
        Assert.Throws<ArgumentException>(() => new TimelineHistory(timeline.Content, 1, default));
        Assert.Throws<ArgumentException>(() => new TimelineHistory(timeline.Content, 1, [null!]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimelineHistory(timeline.Content, -1, []));
        Assert.Throws<ArgumentNullException>(() => new TimelineHistory(null!, 0, []));
        var evt = timeline.Events.OfType<TokenStarted>().First();
        Assert.Throws<ArgumentException>(() => new TimelineHistory(timeline.Content, long.MaxValue,
            [new(new TokenStarted(evt.Source with { Text = "forged" }, evt.SourceOrder, evt.ReplayReason))]));
        var valid = new TimelineHistory(timeline.Content, evt.TimeUs, [new(evt)]);
        Assert.Single(valid.Records); Assert.True(valid.Records == valid.Records);
    }

    [Fact]
    public async Task 脚本作者不能调用受控事件表或给公共构造传可信开关()
    {
        var compiler = new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
        var privateTable = await compiler.CompileAsync([new("src/Private.cs", """
            using MakoKaraGenerator.Scripting;
            public class PrivateProbe { public void Run() { var table = new TimelineHistory.ValidatedEventTable(null, default, default); } }
            """)]);
        Assert.False(privateTable.Succeeded); Assert.Contains(privateTable.Diagnostics, diagnostic => diagnostic.Code == "CS0122");
        var trustedSwitch = await compiler.CompileAsync([new("src/Switch.cs", """
            using MakoKaraGenerator.Scripting;
            public class SwitchProbe { public void Run() { var history = new TimelineHistory(null, 0, default, true); } }
            """)]);
        Assert.False(trustedSwitch.Succeeded); Assert.Contains(trustedSwitch.Diagnostics, diagnostic => diagnostic.Code == "CS1729");
    }
}
