using System.Collections.Immutable;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Execution;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class DiagnosticSnapshotSessionTests
{
    private const string Fingerprint = "diagnostic-snapshot-session";
    private static readonly Lazy<Task<ScriptCompilationResult>> Probe = new(async () =>
    {
        var compiler = new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
        var result = await compiler.CompileAsync([new("src/DiagnosticsProbe.cs", """
            using System;
            using MakoKaraGenerator.Scripting;
            public sealed class WarningProbe : EffectBase
            {
                public override void Evaluate(FrameContext frame, FrameWriter writer)
                    => frame.Diagnostics.Report(DiagnosticSeverity.Warning, "Same", "same message", new SourceLocation("src/DiagnosticsProbe.cs", 12, 3));
            }
            public sealed class AllStageProbe : EffectBase
            {
                private string fail;
                private void Report(DiagnosticReporter reporter, string stage)
                {
                    reporter.Report(DiagnosticSeverity.Warning, "Same", "same message", new SourceLocation("src/DiagnosticsProbe.cs", 22, 3));
                    if (stage == fail) { try { reporter.Report(DiagnosticSeverity.Error, "Stop", "caught error"); } catch { } }
                }
                public override void Initialize(EffectInitializationContext context) { fail = context.Parameters.GetEnum("fail"); Report(context.Diagnostics, "Initialize"); }
                public override void OnEvent(TimelineEvent evt, EventContext context) => Report(context.Diagnostics, "OnEvent");
                public override void Advance(SimulationStep step, StateContext context) => Report(context.Diagnostics, "Advance");
                public override void Evaluate(FrameContext frame, FrameWriter writer) { Report(frame.Diagnostics, "Evaluate"); Report(frame.Diagnostics, "Evaluate"); }
                public override void EvaluateEmitters(EmissionFrameContext frame, EmitterWriter writer) => Report(frame.Diagnostics, "EvaluateEmitters");
                public override void AdvanceVisuals(VisualSimulationStep step, VisualStateContext context) => Report(context.Diagnostics, "AdvanceVisuals");
                public override void EvaluateVisuals(VisualFrameContext frame, VisualWriter writer) => Report(frame.Diagnostics, "EvaluateVisuals");
                public override void Dispose() { if (fail == "Dispose") throw new Exception("dispose failure"); }
            }
            """)]);
        Assert.True(result.Succeeded, string.Join('\n', result.Diagnostics)); return result;
    });
    private static ProjectSnapshot Snapshot(bool lyrics = false) => ProjectSnapshot.FromProject(Project.Create("diagnostics", lyrics: lyrics
        ? LyricsDocument.Create([LyricLine.Create("line", "甲", new(1000, 50000), TimingStatus.Edited,
            [LyricToken.Create("token", "甲", new(1000, 50000), TimingStatus.Edited)])]) : LyricsDocument.Create([])))
        with { EffectInputsFingerprint = Fingerprint };
    private static async Task<TimelineFrameSourceFactory> Factory(string entry = "WarningProbe", string fail = "none", Action<long>? beforeSample = null) => new(
        [new(new("instance"), await Probe.Value, entry, new(new Dictionary<string, ParameterValue> { ["fail"] = new ParameterValue.Choice(fail) }),
            new EffectBinding.Project(), EffectCapabilities.Overlay) { Subscription = new SourceSubscription.Project() }],
        Fingerprint, () => new Geometry(beforeSample), ReplayReason.Initial);
    private sealed class Geometry(Action<long>? beforeSample = null) : ILineGeometryProvider
    {
        public LineRenderGeometry? GetLineGeometry(ProjectSnapshot snapshot, string lineId) => null;
        public SceneFrame GetSceneFrame(ProjectSnapshot snapshot, long timeUs, string? editingLineId = null)
        { beforeSample?.Invoke(timeUs); return LyricSceneSampler.SampleFrame(snapshot, timeUs); }
    }
    [Theory]
    [InlineData(0, 1)] [InlineData(1, 121)] [InlineData(2, 241)] [InlineData(5, 601)]
    public async Task 完整诊断兼容旧探针且增量只消费实际发生(int seconds, int count)
    {
        var snapshot = Snapshot(); var factory = await Factory(); var target = RenderTarget.FromComposition(snapshot.Composition);
        await using var session = await factory.CreateAsync(snapshot); var cursor = new RuntimeDiagnosticCursor();
        for (int index = 0; index <= seconds * 30; index++)
        {
            var frame = await session.PrepareAsync(index * 1000000L / 30, OutputMode.Opaque, target);
            foreach (var occurrence in cursor.ReadNew(frame.DiagnosticSnapshot)) Assert.Equal("Same", occurrence.Diagnostic.Diagnostic.Code);
        }
        var last = await session.PrepareAsync(seconds * 1000000L, OutputMode.Opaque, target);
        Assert.Equal(count, last.Diagnostics.Length); Assert.Equal(count, cursor.ConsumedCount); Assert.Empty(cursor.ReadNew(last.DiagnosticSnapshot));
        await session.DisposeAsync(); Assert.Equal(count, last.Diagnostics.Length); Assert.Equal(count, last.DiagnosticSnapshot.ReadAll().Count());
    }
    [Fact]
    public async Task 所有阶段同文多次报告分数重绘与代次重建保持完整发生和位置()
    {
        var snapshot = Snapshot(lyrics: true); var target = RenderTarget.FromComposition(snapshot.Composition);
        await using var session = await (await Factory("AllStageProbe")).CreateAsync(snapshot);
        var at = await session.PrepareAsync(1000, OutputMode.Opaque, target); var cursor = new RuntimeDiagnosticCursor();
        var first = cursor.ReadNew(at.DiagnosticSnapshot).ToArray(); Assert.Empty(cursor.ReadNew((await session.PrepareAsync(1000, OutputMode.Opaque, target)).DiagnosticSnapshot));
        var fraction = await session.PrepareAsync(1001, OutputMode.Opaque, target); Assert.Equal(3, cursor.ReadNew(fraction.DiagnosticSnapshot).Count());
        Assert.Equal(3, cursor.ReadNew((await session.PrepareAsync(1001, OutputMode.Opaque, target)).DiagnosticSnapshot).Count());
        var later = await session.PrepareAsync(33333, OutputMode.Opaque, target); Assert.NotEmpty(cursor.ReadNew(later.DiagnosticSnapshot).ToArray());
        foreach (string stage in new[] { "Initialize", "OnEvent", "Advance", "Evaluate", "EvaluateEmitters", "AdvanceVisuals", "EvaluateVisuals" })
            Assert.Contains(later.Diagnostics, diagnostic => diagnostic.Stage == stage);
        Assert.All(later.Diagnostics, diagnostic => Assert.Equal(new SourceLocation("src/DiagnosticsProbe.cs", 22, 3), diagnostic.Diagnostic.Location));
        Assert.Equal(first.Length, at.Diagnostics.Length); Assert.DoesNotContain(later.Diagnostics, diagnostic => diagnostic.Stage == "Evaluate" && diagnostic.TimeUs == 1001);
        var rewind = await session.PrepareAsync(0, OutputMode.Opaque, target); var repeated = cursor.ReadNew(rewind.DiagnosticSnapshot).ToArray();
        Assert.NotEmpty(repeated); Assert.DoesNotContain(repeated[0].Epoch, first.Select(item => item.Epoch));
        var mode = await session.PrepareAsync(0, OutputMode.Transparent, target); Assert.NotEmpty(cursor.ReadNew(mode.DiagnosticSnapshot));
        await session.DisposeAsync(); Assert.Equal(first.Select(item => item.Diagnostic), at.Diagnostics);
    }
    [Theory]
    [InlineData("Initialize")] [InlineData("OnEvent")] [InlineData("Advance")] [InlineData("Evaluate")]
    [InlineData("EvaluateEmitters")] [InlineData("AdvanceVisuals")] [InlineData("EvaluateVisuals")]
    public async Task 各阶段作者捕获Error仍失败且保留错误前完整稳定日志(string stage)
    {
        var snapshot = Snapshot(lyrics: true); var target = RenderTarget.FromComposition(snapshot.Composition); var factory = await Factory("AllStageProbe", stage);
        var failure = await Assert.ThrowsAsync<EffectFailureException>(async () =>
        { await using var session = await factory.CreateAsync(snapshot); await session.PrepareAsync(33333, OutputMode.Opaque, target); });
        Assert.Equal(stage, failure.Stage); Assert.Equal("Stop", failure.Code); Assert.True(failure.DiagnosticSnapshot!.HasError);
        Assert.Contains(failure.DiagnosticSnapshot.ReadAll(), item => item.Diagnostic.Diagnostic.Severity == DiagnosticSeverity.Warning);
        Assert.Equal(failure.Diagnostic, failure.DiagnosticSnapshot.ToImmutableArray()[^1]);
    }
    [Fact]
    public async Task 释放失败仍报告并保留先前帧及完整来源日志()
    {
        var snapshot = Snapshot(lyrics: true); var target = RenderTarget.FromComposition(snapshot.Composition);
        var session = await (await Factory("AllStageProbe", "Dispose")).CreateAsync(snapshot);
        var frame = await session.PrepareAsync(33333, OutputMode.Opaque, target);
        var failure = await Assert.ThrowsAsync<EffectFailureException>(async () => await session.DisposeAsync());
        Assert.Equal("Dispose", failure.Stage); Assert.Equal("ScriptDisposeFailed", failure.Code);
        Assert.Equal(frame.Diagnostics, failure.DiagnosticSnapshot!.ToImmutableArray()); await session.DisposeAsync();
    }
    [Fact]
    public async Task 取消后首次远时刻重放建立新诊断代次并保留旧快照()
    {
        var snapshot = Snapshot(); var target = RenderTarget.FromComposition(snapshot.Composition);
        using var cancel = new CancellationTokenSource(); bool cancelled = false;
        await using var session = await (await Factory(beforeSample: time =>
        { if (time > 1_000_000 && !cancelled) { cancelled = true; cancel.Cancel(); } })).CreateAsync(snapshot);
        var initial = await session.PrepareAsync(1_000_000, OutputMode.Opaque, target);
        Assert.Equal(121, initial.DiagnosticSnapshot.Count);
        var initialRecords = initial.DiagnosticSnapshot.ReadAll().ToArray();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await session.PrepareAsync(5_000_000, OutputMode.Opaque, target, cancel.Token));
        Assert.True(cancelled);
        var after = await session.PrepareAsync(5_000_000, OutputMode.Opaque, target);
        var cursor = new RuntimeDiagnosticCursor();
        Assert.Equal(601, cursor.ReadNew(after.DiagnosticSnapshot).Count());
        Assert.Empty(cursor.ReadNew(after.DiagnosticSnapshot));
        Assert.NotEqual(initialRecords[0].Epoch, after.DiagnosticSnapshot.ReadAll().First().Epoch);
        Assert.Equal(initialRecords, initial.DiagnosticSnapshot.ReadAll());
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task 早期宿主门禁保留前一实例初始化报告(bool requiredAudio)
    {
        var first = new CompiledFrameEffect(new("first"), await Probe.Value, "AllStageProbe",
            new(new Dictionary<string, ParameterValue> { ["fail"] = new ParameterValue.Choice("none") }),
            new EffectBinding.Project(), EffectCapabilities.None);
        var second = first with { Id = new("second"), EntryType = requiredAudio ? "AllStageProbe" : "MissingEntry", RequiresAudio = requiredAudio };
        var factory = new TimelineFrameSourceFactory([first, second], Fingerprint, static () => new Geometry(), ReplayReason.Initial);
        var failure = await Assert.ThrowsAsync<EffectFailureException>(async () => await factory.CreateAsync(Snapshot()));
        Assert.Equal(requiredAudio ? "ValidateAudio" : "Load", failure.Stage);
        Assert.Equal(new EffectInstanceId("second"), failure.InstanceId);
        var preceding = Assert.Single(failure.DiagnosticSnapshot!.ReadAll());
        Assert.Equal(new EffectInstanceId("first"), preceding.Diagnostic.InstanceId);
        Assert.Equal("Initialize", preceding.Diagnostic.Stage);
        Assert.Equal(DiagnosticSeverity.Warning, preceding.Diagnostic.Diagnostic.Severity);
        Assert.Null(failure.Diagnostic); Assert.False(failure.DiagnosticSnapshot.HasError);
    }
}
