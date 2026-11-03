using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Execution;
using MakoKaraGenerator.Scripting.Loading;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class HistoryPrefixSessionTests
{
    private const string Fingerprint = "history-prefix-session";
    private static readonly Lazy<Task<ScriptCompilationResult>> Probe = new(async () =>
    {
        var compiler = new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
        var artifact = await compiler.CompileAsync([new("src/PrefixProbe.cs", """
            using System;
            using MakoKaraGenerator.Scripting;
            public sealed class PrefixProbe : EffectBase
            {
                private TimelineHistory first;
                private int starts;
                public override void OnEvent(TimelineEvent evt, EventContext context)
                {
                    if (first == null) first = context.History;
                    if (evt is TokenStarted) starts++;
                    var records = context.History.Records;
                    if (records[records.Length-1].EventId != evt.EventId || first.Records.Length != 1
                        || context.History.GetStarts(new TimeRange(0, long.MaxValue), new SourceFilter.Project()).Length != starts)
                        throw new Exception("History grew or same-time dispatch leaked its suffix");
                }
                public override void Evaluate(FrameContext frame, FrameWriter writer)
                {
                    int queried = frame.History.GetStarts(new TimeRange(0, long.MaxValue), new SourceFilter.Project()).Length;
                    frame.Diagnostics.Report(DiagnosticSeverity.Info, "Prefix", starts + ":" + queried + ":" + first.Records.Length + ":" + first.Records[0].Event.ReplayReason);
                }
            }
            """)]);
        Assert.True(artifact.Succeeded, string.Join("\n", artifact.Diagnostics)); return artifact;
    });
    private static ProjectSnapshot Snapshot(int fps = 30)
    {
        var line = LyricLine.Create("line", "甲乙", new(0, 100_000), TimingStatus.Edited,
            [LyricToken.Create("a", "甲", new(1001, 40_000), TimingStatus.Edited),
             LyricToken.Create("b", "乙", new(1001, 50_000), TimingStatus.Edited)]);
        var project = Project.Create("prefix", lyrics: LyricsDocument.Create([line]));
        return ProjectSnapshot.FromProject(project with { Composition = project.Composition with { FrameRate = new(fps, 1) } })
            with { EffectInputsFingerprint = Fingerprint };
    }
    private static async Task<TimelineFrameSourceFactory> Factory(Func<ILineGeometryProvider>? geometry = null) => new(
        [new(new("prefix"), await Probe.Value, "PrefixProbe", ParameterValues.Empty, new EffectBinding.Project(), EffectCapabilities.None)
        { Subscription = new SourceSubscription.Project() }], Fingerprint, geometry ?? (static () => new Geometry()), ReplayReason.Initial);
    private static string Summary(PreparedRenderFrame frame) => frame.Diagnostics.Last(item => item.Diagnostic.Code == "Prefix").Diagnostic.Message;
    private sealed class Geometry(Action<long>? sample = null) : ILineGeometryProvider
    {
        public LineRenderGeometry? GetLineGeometry(ProjectSnapshot snapshot, string lineId) => null;
        public SceneFrame GetSceneFrame(ProjectSnapshot snapshot, long timeUs, string? editingLineId = null)
        { sample?.Invoke(timeUs); return LyricSceneSampler.SampleFrame(snapshot, timeUs); }
    }
    [Theory]
    [InlineData(24)] [InlineData(30)] [InlineData(60)]
    public async Task 同刻逐事件可见保留旧历史且普通透明和不同帧率保持状态(int fps)
    {
        var snapshot = Snapshot(fps); var factory = await Factory(); var target = RenderTarget.FromComposition(snapshot.Composition);
        await using var opaque = await factory.CreateAsync(snapshot); await using var transparent = await factory.CreateAsync(snapshot);
        for (int index = 0; index <= fps / 10; index++)
        {
            long time = index * 1_000_000L / fps;
            var normal = await opaque.PrepareAsync(time, OutputMode.Opaque, target);
            var alpha = await transparent.PrepareAsync(time, OutputMode.Transparent, target);
            Assert.Equal(Summary(normal), Summary(alpha));
        }
        var late = await opaque.PrepareAsync(120_001, OutputMode.Opaque, target);
        Assert.Equal("2:2:1:Initial", Summary(late));
        var rewind = await opaque.PrepareAsync(500, OutputMode.Opaque, target);
        Assert.Equal("0:0:1:Seek", Summary(rewind));
        var changedMode = await opaque.PrepareAsync(80_001, OutputMode.Transparent, target);
        Assert.Equal("2:2:1:InputsChanged", Summary(changedMode));
        Assert.Equal("2:2:1:Initial", Summary(late));
    }
    [Fact]
    public async Task 取消后正确新代次重放且旧帧保持稳定()
    {
        using var cancellation = new CancellationTokenSource(); bool stopOnce = true;
        var factory = await Factory(() => new Geometry(time => { if (time > 10_000 && stopOnce) { stopOnce = false; cancellation.Cancel(); } }));
        var snapshot = Snapshot(); var target = RenderTarget.FromComposition(snapshot.Composition);
        await using var session = await factory.CreateAsync(snapshot);
        var old = await session.PrepareAsync(1001, OutputMode.Opaque, target);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await session.PrepareAsync(80_001, OutputMode.Opaque, target, cancellation.Token));
        var rebuilt = await session.PrepareAsync(80_001, OutputMode.Opaque, target);
        Assert.Equal("2:2:1:Seek", Summary(rebuilt)); Assert.Equal("2:2:1:Initial", Summary(old));
    }

    [Fact]
    public async Task 取消重放与会话释放后的脚本加载代次可以回收()
    {
        WeakReference[] generations = await CaptureCancelledGenerationsAsync();
        for (int i = 0; i < 12 && generations.Any(item => item.IsAlive); i++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.All(generations, item => Assert.False(item.IsAlive));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference[]> CaptureCancelledGenerationsAsync()
    {
        using CancellationTokenSource cancel = new();
        bool stopOnce = true;
        var factory = await Factory(() => new Geometry(time => { if (time > 10_000 && stopOnce) { stopOnce = false; cancel.Cancel(); } }));
        var snapshot = Snapshot(); var target = RenderTarget.FromComposition(snapshot.Composition);
        await using var session = await factory.CreateAsync(snapshot);
        WeakReference old = ModuleReference(session);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await session.PrepareAsync(80_001, OutputMode.Opaque, target, cancel.Token));
        await session.PrepareAsync(80_001, OutputMode.Opaque, target);
        WeakReference recovered = ModuleReference(session);
        Assert.NotSame(old.Target, recovered.Target);
        return [old, recovered];
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ModuleReference(IEvaluatedFrameSource session)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object host = session.GetType().GetFields(flags).Single(field => field.FieldType.Name == "FrameEffectHost").GetValue(session)!;
        var effects = (System.Collections.IEnumerable)host.GetType().GetField("_instances", flags)!.GetValue(host)!;
        object effect = Assert.Single(effects.Cast<object>());
        var module = (ScriptModule)effect.GetType().GetProperty("Module")!.GetValue(effect)!;
        return module.UnloadReference;
    }
}
