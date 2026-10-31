using System.Collections.Immutable;
using System.Reflection;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class RuntimeDiagnosticSnapshotTests
{
    private static RuntimeDiagnostic Record(long time = 0) => new(new("instance"), "fixed-artifact", "Evaluate", time,
        new(DiagnosticSeverity.Warning, "Same", "same message", new("src/Probe.cs", 12, 3)));
    private static bool Unmaterialized(RuntimeDiagnosticSnapshot snapshot) => ((ImmutableArray<RuntimeDiagnostic>)typeof(RuntimeDiagnosticSnapshot)
        .GetField("_array", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(snapshot)!).IsDefault;
    [Fact]
    public void 长日志逐次快照与增量读取不物化数组且旧快照稳定()
    {
        var log = new RuntimeDiagnosticBuffer(); var cursor = new RuntimeDiagnosticCursor();
        RuntimeDiagnosticSnapshot? old = null; List<RuntimeDiagnosticOccurrence> observed = [];
        for (int index = 0; index < 10000; index++)
        {
            log.Add(Record(index)); var snapshot = log.Snapshot();
            Assert.Same(snapshot, log.Snapshot()); Assert.True(Unmaterialized(snapshot));
            observed.AddRange(cursor.ReadNew(snapshot)); Assert.Empty(cursor.ReadNew(snapshot));
            if (index == 20) old = snapshot;
        }
        Assert.Equal(10000, cursor.ConsumedCount); Assert.Equal(10000, observed.Count);
        Assert.Equal(Enumerable.Range(1, 10000).Select(static value => (long)value), observed.Select(item => item.Sequence));
        Assert.Single(observed.Select(item => item.Epoch).Distinct());
        Assert.All(observed, item => Assert.Equal("same message", item.Diagnostic.Diagnostic.Message));
        Assert.Equal(21, old!.Count); Assert.Equal(Enumerable.Range(0, 21).Select(static value => (long)value), old.ReadAll().Select(item => item.Diagnostic.TimeUs));
        Assert.True(Unmaterialized(old)); Assert.Empty(cursor.ReadNew(old));
    }
    [Fact]
    public void 分数与绘制后缀独立同文不同发生且后续共享前缀不含旧局部报告()
    {
        var log = new RuntimeDiagnosticBuffer(); log.Add(Record()); var shared = log.Snapshot();
        var local = new RuntimeDiagnosticBuffer(shared); local.Add(Record(1)); var fraction = local.Snapshot();
        var drawn = fraction.Append([Record(2), Record(2)]);
        log.Add(Record(3)); var next = log.Snapshot();
        var cursor = new RuntimeDiagnosticCursor();
        Assert.Single(cursor.ReadNew(shared)); Assert.Equal(3, cursor.ReadNew(drawn).Count());
        Assert.Empty(cursor.ReadNew(fraction)); Assert.Single(cursor.ReadNew(next)); Assert.Empty(cursor.ReadNew(drawn));
        Assert.Equal(5, cursor.ConsumedCount); Assert.Equal(4, drawn.Count); Assert.Equal(2, next.Count);
        Assert.Equal((long[])[0, 1, 2, 2], drawn.ReadAll().Select(item => item.Diagnostic.TimeUs));
        Assert.Equal((long[])[0, 3], next.ReadAll().Select(item => item.Diagnostic.TimeUs));
        Assert.Equal(4, drawn.ReadAll().Select(item => (item.Epoch, item.Sequence)).Distinct().Count());
    }
    [Fact]
    public async Task 完整兼容数组按需物化并发缓存且没有未来追加()
    {
        var log = new RuntimeDiagnosticBuffer(); for (int index = 0; index < 73; index++) log.Add(Record(index));
        var old = log.Snapshot(); Assert.True(Unmaterialized(old)); log.Add(Record(73));
        var arrays = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(old.ToImmutableArray)));
        Assert.All(arrays, array => { Assert.Equal(73, array.Length); Assert.True(array == old.ToImmutableArray()); });
        Assert.Equal(74, log.Snapshot().Count); Assert.Equal(72, old.ToImmutableArray()[^1].TimeUs);
    }
    [Fact]
    public void 新消费者直接读取晚快照取得全部来源错误标记及旧数组严格门禁()
    {
        Assert.Throws<ArgumentException>(() => RuntimeDiagnosticSnapshot.FromArray(default));
        Assert.Throws<ArgumentException>(() => RuntimeDiagnosticSnapshot.FromArray([null!]));
        var records = Enumerable.Range(0, 100).Select(index => Record(index)).ToImmutableArray();
        var snapshot = RuntimeDiagnosticSnapshot.FromArray(records); Assert.False(snapshot.HasError);
        var cursor = new RuntimeDiagnosticCursor(); var observed = cursor.ReadNew(snapshot).ToArray();
        Assert.Equal(records, observed.Select(item => item.Diagnostic)); Assert.Equal(records.Length, cursor.ConsumedCount);
        var error = snapshot.Append([Record(101) with { Diagnostic = new(DiagnosticSeverity.Error, "Stop", "failure") }]);
        Assert.True(error.HasError); Assert.False(snapshot.HasError); Assert.Single(cursor.ReadNew(error));
    }
    [Fact]
    public void 成功准备帧两个快照入口均拒绝Error且不物化数组()
    {
        var snapshot = ProjectSnapshot.FromProject(Project.Create("error gate"));
        var target = RenderTarget.FromComposition(snapshot.Composition); var scene = LyricSceneSampler.SampleFrame(snapshot, 0);
        var failure = RuntimeDiagnosticSnapshot.FromArray([Record() with { Diagnostic = new(DiagnosticSeverity.Error, "Stop", "caught") }]);
        var frame = new PreparedRenderFrame(snapshot, 0, OutputMode.Opaque, target, scene, []);
        Assert.Throws<ArgumentException>(() => PreparedRenderFrame.CreateWithDiagnosticSnapshot(snapshot, 0, OutputMode.Opaque, target,
            scene, [], [], failure, [], [], [], [], []));
        Assert.Equal("InvalidPreparedDiagnostics", Assert.Throws<PreparedFrameException>(() => frame.WithOverlaySnapshot([], failure)).Code);
        Assert.True(Unmaterialized(failure));
    }
}
