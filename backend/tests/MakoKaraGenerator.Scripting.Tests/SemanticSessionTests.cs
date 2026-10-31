using System.Collections.Immutable;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Execution;
using MakoKaraGenerator.Scripting.Projection;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class SemanticSessionTests
{
    private const string Fingerprint = "R2-semantic-test";
    private static readonly Lazy<Task<ScriptCompilationResult>> Probe = new(async () =>
    {
        ScriptCompiler compiler = new(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
        var result = await compiler.CompileAsync([new("src/SessionProbe.cs", """
            using System;
            using System.Linq;
            using MakoKaraGenerator.Scripting;
            public sealed class SessionProbe : EffectBase
            {
                private long elapsed;
                private int starts;
                private string reason = "none";
                private string mode = "";
                private DiagnosticReporter saved;
                public override void Initialize(EffectInitializationContext context)
                {
                    mode = context.Parameters.GetEnum("mode");
                    context.Diagnostics.Report(DiagnosticSeverity.Info, "I", "initialized");
                    Fail("Initialize", context.Diagnostics);
                    if (mode == "Expired") saved = context.Diagnostics;
                }
                public override void Advance(SimulationStep interval, StateContext context)
                {
                    if (!interval.IsValid || context.TimeUs != interval.StartUs || context.History.Records.Any(item => item.TimeUs > interval.StartUs))
                        throw new Exception("invalid left boundary");
                    elapsed += interval.DurationUs;
                    context.Diagnostics.Report(DiagnosticSeverity.Info, "A", interval.StartUs + ":" + interval.EndUs);
                    Fail("Advance", context.Diagnostics);
                }
                public override void OnEvent(TimelineEvent evt, EventContext context)
                {
                    if (context.Event != evt || context.TimeUs != evt.TimeUs || context.History.Records.Last().EventId != evt.EventId)
                        throw new Exception("current event must be last processed history item");
                    if (evt is TokenStarted) { starts++; reason = evt.ReplayReason.ToString(); }
                    context.Diagnostics.Report(DiagnosticSeverity.Info, "C", evt.EventId.Value);
                    Fail("OnEvent", context.Diagnostics);
                }
                public override void Evaluate(FrameContext frame, FrameWriter output)
                {
                    if (mode == "Expired") saved.Report(DiagnosticSeverity.Info, "Expired", "must fail");
                    var all = frame.History.GetStarts(new TimeRange(0, 1000000), new SourceFilter.Project()).Length;
                    frame.Diagnostics.Report(DiagnosticSeverity.Info, "E", frame.TimeUs + ":" + frame.LastSimulationBoundaryUs + ":" + elapsed + ":" + starts + ":" + all + ":" + reason);
                    if (mode == "Warn") frame.Diagnostics.Report(DiagnosticSeverity.Warning, "Notice", "warning only", new SourceLocation("src/SessionProbe.cs", 4, 2));
                    Fail("Evaluate", frame.Diagnostics);
                }
                private void Fail(string stage, DiagnosticReporter reporter)
                {
                    if (mode == stage)
                    {
                        try { reporter.Report(DiagnosticSeverity.Error, "AuthorError", "explicit failure", new SourceLocation("src/SessionProbe.cs", 3, 7)); }
                        catch { }
                    }
                    if (mode == "Throw" + stage) throw new Exception("execution failure");
                    if (mode == "Cancel" + stage) throw new OperationCanceledException();
                }
                public override void Dispose()
                {
                    if (mode == "ThrowDispose") throw new Exception("dispose failure");
                }
            }
            public sealed class HistoryProbe : EffectBase
            {
                public override void Evaluate(FrameContext frame, FrameWriter output) { var history = frame.History; }
            }
            """)]);
        Assert.True(result.Succeeded, string.Join('\n', result.Diagnostics)); return result;
    });
    private static ProjectSnapshot Snapshot()
    {
        var line = LyricLine.Create("row", "田田", new(0, 100_000), TimingStatus.Edited,
            [LyricToken.Create("a", "田", new(1_001, 10_000), TimingStatus.Edited),
             LyricToken.Create("b", "田", new(10_000, 40_000), TimingStatus.Edited)]);
        return ProjectSnapshot.FromProject(Project.Create("session", lyrics: LyricsDocument.Create([line]))) with { EffectInputsFingerprint = Fingerprint };
    }
    private static RenderTarget Target(ProjectSnapshot snapshot) => RenderTarget.FromComposition(snapshot.Composition);
    private static async Task<CompiledFrameEffect> Definition(string mode = "Normal", EffectBinding? binding = null, SourceSubscription? subscription = null)
        => new(new("probe"), await Probe.Value, "SessionProbe",
            new(new Dictionary<string, ParameterValue> { ["mode"] = new ParameterValue.Choice(mode) }),
            binding ?? new EffectBinding.Project(), EffectCapabilities.None) { Subscription = subscription ?? new SourceSubscription.Project() };
    private static async Task<TimelineFrameSourceFactory> Factory(string mode = "Normal", ReplayReason reason = ReplayReason.Initial,
        Func<ILineGeometryProvider>? geometry = null, EffectBinding? binding = null, SourceSubscription? subscription = null)
        => new([await Definition(mode, binding, subscription)], Fingerprint, geometry ?? (static () => new Geometry()), reason);
    private sealed class Geometry(Action<long>? onFrame = null, Action? onDispose = null) : ILineGeometryProvider, IDisposable
    {
        public LineRenderGeometry? GetLineGeometry(ProjectSnapshot snapshot, string lineId) => null;
        public SceneFrame GetSceneFrame(ProjectSnapshot snapshot, long timeUs, string? editingLineId = null)
        { onFrame?.Invoke(timeUs); return LyricSceneSampler.SampleFrame(snapshot, timeUs); }
        public void Dispose() => onDispose?.Invoke();
    }
    private static string Evaluation(PreparedRenderFrame frame) => frame.Diagnostics.Last(item => item.Diagnostic.Code == "E").Diagnostic.Message;

    [Theory]
    [InlineData(0, 0)] [InlineData(8_332, 0)] [InlineData(8_333, 1)] [InlineData(16_665, 1)]
    [InlineData(16_666, 2)] [InlineData(999_999, 119)] [InlineData(1_000_000, 120)]
    public void 整数网格包括向下取整产生的边界(long time, long index)
    {
        Assert.Equal(index, SemanticSimulationGrid.IndexAtOrBefore(time));
        Assert.True(SemanticSimulationGrid.TimeAt(index) <= time);
        Assert.True(SemanticSimulationGrid.TimeAt(index + 1) > time);
    }
    [Fact]
    public void 网格极值不乘法回绕且模拟区间拒绝零长()
    {
        var last = SemanticSimulationGrid.IndexAtOrBefore(long.MaxValue);
        Assert.InRange(SemanticSimulationGrid.TimeAt(last), long.MaxValue - 8_334, long.MaxValue);
        Assert.Throws<OverflowException>(() => SemanticSimulationGrid.TimeAt(last + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => SemanticSimulationGrid.IndexAtOrBefore(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SimulationStep(0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SimulationStep(-1, 1));
        Assert.False(default(SimulationStep).IsValid);
    }
    [Fact]
    public async Task 精确事件加入固定网格且同界历史逐项前进分数帧不推进状态()
    {
        var snapshot = Snapshot(); await using var session = await (await Factory()).CreateAsync(snapshot);
        var frame = await session.PrepareAsync(18_001, OutputMode.Transparent, Target(snapshot));
        Assert.Equal("18001:16666:16666:2:2:Initial", Evaluation(frame));
        var projection = TimelineProjection.Create(snapshot, ReplayReason.Initial);
        Assert.Equal(projection.Events.Where(item => item.TimeUs <= 16_666).Select(item => item.EventId.Value),
            frame.Diagnostics.Where(item => item.Diagnostic.Code == "C").Select(item => item.Diagnostic.Message));
        Assert.Equal((string[])["0:1001", "1001:8333", "8333:10000", "10000:16666"],
            frame.Diagnostics.Where(item => item.Diagnostic.Code == "A").Select(item => item.Diagnostic.Message));
        // 固定边界求值包含 0 和精确事件，而任意分数帧只求值一次外观。
        Assert.Equal((long[])[0, 1001, 8333, 10000, 16666, 18001],
            frame.Diagnostics.Where(item => item.Diagnostic.Code == "E").Select(item => item.TimeUs));
        var redraw = await session.PrepareAsync(18_001, OutputMode.Transparent, Target(snapshot));
        Assert.Equal(frame.Diagnostics.ToArray(), redraw.Diagnostics.ToArray());
        var forwardFraction = await session.PrepareAsync(18_500, OutputMode.Transparent, Target(snapshot));
        Assert.Equal("18500:16666:16666:2:2:Initial", Evaluation(forwardFraction));
    }
    [Fact]
    public async Task 回退从零重建并保留身份但重放原因变化独立会话不共享状态()
    {
        var snapshot = Snapshot(); var factory = await Factory();
        await using var session = await factory.CreateAsync(snapshot);
        var first = await session.PrepareAsync(18_001, OutputMode.Opaque, Target(snapshot));
        await session.PrepareAsync(35_001, OutputMode.Opaque, Target(snapshot));
        var rewind = await session.PrepareAsync(5_001, OutputMode.Opaque, Target(snapshot));
        Assert.Equal("5001:1001:1001:1:1:Seek", Evaluation(rewind));
        var again = await session.PrepareAsync(18_001, OutputMode.Opaque, Target(snapshot));
        Assert.Equal("18001:16666:16666:2:2:Seek", Evaluation(again));
        Assert.Equal(first.Diagnostics.Where(item => item.Diagnostic.Code == "C").Select(item => item.Diagnostic.Message),
            again.Diagnostics.Where(item => item.Diagnostic.Code == "C").Select(item => item.Diagnostic.Message));
        await using var other = await (await Factory(reason: ReplayReason.Export)).CreateAsync(snapshot);
        Assert.Equal("18001:16666:16666:2:2:Export", Evaluation(await other.PrepareAsync(18_001, OutputMode.Transparent, Target(snapshot))));
    }
    [Fact]
    public async Task 订阅只筛选回调完整历史包含绑定之外的起唱()
    {
        var snapshot = Snapshot(); await using var session = await (await Factory(binding: new EffectBinding.Tokens([new("b")]),
            subscription: new SourceSubscription.BoundSources())).CreateAsync(snapshot);
        Assert.Equal("18001:16666:16666:1:2:Initial", Evaluation(await session.PrepareAsync(18_001, OutputMode.Transparent, Target(snapshot))));
    }
    [Theory]
    [InlineData("Initialize", 0)] [InlineData("OnEvent", 0)] [InlineData("Advance", 0)] [InlineData("Evaluate", 0)]
    public async Task 作者报告Error即使捕获返回仍失败且保留定位(string stage, long time)
    {
        var snapshot = Snapshot(); var factory = await Factory(stage);
        EffectFailureException error;
        if (stage == "Initialize") error = await Assert.ThrowsAsync<EffectFailureException>(async () => await factory.CreateAsync(snapshot));
        else
        {
            await using var session = await factory.CreateAsync(snapshot);
            error = await Assert.ThrowsAsync<EffectFailureException>(async () => await session.PrepareAsync(18_001, OutputMode.Transparent, Target(snapshot)));
        }
        Assert.Equal(stage, error.Stage); Assert.Equal(time, error.TimeUs); Assert.Equal("AuthorError", error.Code);
        Assert.Equal(new SourceLocation("src/SessionProbe.cs", 3, 7), error.Diagnostic!.Diagnostic.Location);
        Assert.Equal(new EffectInstanceId("probe"), error.InstanceId); Assert.Equal((await Probe.Value).ArtifactHash, error.ArtifactHash);
    }
    [Theory]
    [InlineData("Initialize")] [InlineData("OnEvent")] [InlineData("Advance")] [InlineData("Evaluate")]
    public async Task 阶段异常具名而取消保持取消(string stage)
    {
        var snapshot = Snapshot();
        foreach (string prefix in new[] { "Throw", "Cancel" })
        {
            var factory = await Factory(prefix + stage);
            async Task Run()
            {
                await using var session = await factory.CreateAsync(snapshot);
                await session.PrepareAsync(18_001, OutputMode.Transparent, Target(snapshot));
            }
            if (prefix == "Cancel") await Assert.ThrowsAsync<OperationCanceledException>(Run);
            else { var error = await Assert.ThrowsAsync<EffectFailureException>(Run); Assert.Equal(stage, error.Stage); }
        }
    }
    [Fact]
    public async Task Warning只记录且旧阶段Reporter不能继续使用()
    {
        var snapshot = Snapshot(); await using var session = await (await Factory("Warn")).CreateAsync(snapshot);
        var frame = await session.PrepareAsync(18_001, OutputMode.Transparent, Target(snapshot));
        Assert.All(frame.Diagnostics, item => Assert.NotEqual(DiagnosticSeverity.Error, item.Diagnostic.Severity));
        Assert.Equal(new SourceLocation("src/SessionProbe.cs", 4, 2), frame.Diagnostics.Last(item => item.Diagnostic.Code == "Notice").Diagnostic.Location);
        await using var expired = await (await Factory("Expired")).CreateAsync(snapshot);
        Assert.Equal("InvalidDiagnosticStage", (await Assert.ThrowsAsync<EffectFailureException>(async () =>
            await expired.PrepareAsync(18_001, OutputMode.Transparent, Target(snapshot)))).Code);
    }
    [Fact]
    public async Task 取消部分推进后下一请求从固定输入重建()
    {
        var snapshot = Snapshot(); using CancellationTokenSource cancellation = new(); bool cancelOnce = true;
        var factory = await Factory(geometry: () => new Geometry(time =>
        { if (time >= 10_000 && cancelOnce) { cancelOnce = false; cancellation.Cancel(); } }));
        await using var session = await factory.CreateAsync(snapshot);
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await session.PrepareAsync(18_001, OutputMode.Transparent, Target(snapshot), cancellation.Token));
        Assert.Equal("18001:16666:16666:2:2:Seek", Evaluation(await session.PrepareAsync(18_001, OutputMode.Transparent, Target(snapshot))));
    }
    [Fact]
    public async Task 纯帧不静默跳过回调也不伪造空历史()
    {
        var snapshot = Snapshot();
        PureFrameSourceFactory stateful = new([await Definition()], Fingerprint, static () => new Geometry());
        Assert.Equal("TimelineSessionRequired", (await Assert.ThrowsAsync<EffectFailureException>(async () => await stateful.CreateAsync(snapshot))).Code);
        PureFrameSourceFactory pure = new([(await Definition()) with { EntryType = "HistoryProbe" }], Fingerprint, static () => new Geometry());
        await using var session = await pure.CreateAsync(snapshot);
        Assert.Equal("TimelineSessionRequired", (await Assert.ThrowsAsync<EffectFailureException>(async () =>
            await session.PrepareAsync(18_001, OutputMode.Transparent, Target(snapshot)))).Code);
    }
    [Fact]
    public async Task 释放失败仍释放全部实例和几何后端且重复释放安全()
    {
        int released = 0; var definition = await Definition("ThrowDispose");
        TimelineFrameSourceFactory factory = new([definition, definition with { Id = new("second") }], Fingerprint,
            () => new Geometry(onDispose: () => released++), ReplayReason.Initial);
        var session = await factory.CreateAsync(Snapshot());
        var error = await Assert.ThrowsAsync<EffectFailureException>(async () => await session.DisposeAsync());
        Assert.Equal("ScriptDisposeFailed", error.Code);
        Assert.Equal(2, Assert.IsType<AggregateException>(error.InnerException).Flatten().InnerExceptions.Count); Assert.Equal(1, released);
        await session.DisposeAsync(); Assert.Equal(1, released);
    }
}
