using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Execution;
using MakoKaraGenerator.Scripting.Projection;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class V2ReloadIdentityTests
{
    [Fact]
    public async Task 同源重入与旧退出副本并存且源演唱身份只计一次()
    {
        var snapshot = await Snapshot(); var timeline = TimelineProjection.Create(snapshot, ReplayReason.Initial);
        var loads = timeline.Events.OfType<BindingEntered>().Where(static item => item.Binding.LineId.Value == "outer").ToArray();
        Assert.Equal([0L, 700_000L], loads.Select(static item => item.TimeUs));
        Assert.NotEqual(loads[0].Binding.BindingId, loads[1].Binding.BindingId);
        Assert.Equal(loads[0].Binding.BindingId.Value, LyricSceneSampler.SourceBindingKey(loads[1].Binding.BindingId.Value));
        Assert.EndsWith("/reload/700000", loads[1].Binding.BindingId.Value, StringComparison.Ordinal);
        var sourceStart = Assert.Single(timeline.Events.OfType<TokenStarted>(), static item => item.TokenId.Value == "outer-token");
        Assert.Equal(0, sourceStart.TimeUs);

        var compiler = new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
        var compiled = await CompiledProjectEffects.CompileAsync(snapshot, compiler);
        int compilationCountAfterLoad = compiler.CompilationCount;
        Assert.True(compiled.Succeeded, string.Join('\n', compiled.Packages.SelectMany(static package => package.Artifact.Diagnostics)));
        var factory = new PureFrameSourceFactory(compiled.Effects, snapshot.EffectInputsFingerprint!, static () => new SkiaRenderPipeline());
        await using var session = await factory.CreateAsync(snapshot);
        long[] times = [750_000, 499_999, 500_000, 699_999, 700_000, 1_099_999, 1_100_000, 1_300_000, 750_000, 2_100_000, 750_000];
        var frames = new List<PreparedRenderFrame>();
        foreach (long time in times) frames.Add(await session.PrepareAsync(time, OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition)));
        var shared = frames[0];
        var sameSource = shared.Scene.Lines.Where(static line => line.Frame.Line!.Id == "outer").ToArray();
        Assert.Equal(2, sameSource.Length);
        var old = Assert.Single(sameSource, static line => line.ExitReplica is not null);
        var current = Assert.Single(sameSource, static line => line.ExitReplica is null);
        Assert.Equal(loads[0].Binding.BindingId.Value, old.BindingId); Assert.Equal(500_000, old.ExitReplica!.HideTimeUs);
        Assert.Equal(loads[1].Binding.BindingId.Value, current.BindingId);
        Assert.Equal(old.SourceElementId, current.SourceElementId); Assert.NotEqual(old.ElementId, current.ElementId);
        Assert.NotEqual(old.ParentId, current.ParentId);
        Assert.Equal(3, shared.Scene.Lines.Length);
        Assert.Equal(shared.Scene.Elements.Length, shared.Scene.Elements.Select(static node => node.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(shared.Scene.TokenGeometries.Length, shared.Scene.TokenGeometries.Select(static token => token.WithRuby).Distinct().Count());
        Assert.Contains(shared.ClusterTransforms, transform => transform.Target.BindingId == loads[0].Binding.BindingId);
        Assert.DoesNotContain(shared.ClusterTransforms, transform => transform.Target.BindingId == loads[1].Binding.BindingId);
        var cursors = PlaybackProjection.GetSources(snapshot, shared.Scene);
        var cursor = Assert.Single(cursors);
        Assert.Equal(loads[1].Binding.BindingId, cursor.Identity.BindingId); Assert.Equal(sourceStart.OccurrenceId, cursor.Identity.OccurrenceId);
        using var geometry = new SkiaRenderPipeline();
        var indexes = PureSourceIndexes.Create(snapshot, timeline, geometry, new EffectBinding.Project(), new SourceSubscription.Project());
        Assert.Equal(loads[1].Binding.BindingId, Assert.Single(indexes.Presentations.GetAt(750_000, new SourceFilter.Project())).Load.Binding.BindingId);
        Assert.Equal(cursor.Identity, Assert.Single(indexes.Cursors.GetBaseAt(750_000, new SourceFilter.Project(), CursorAnchorMode.Baseline)).Identity);
        Assert.DoesNotContain(frames[6].Scene.Lines, line => line.BindingId == loads[0].Binding.BindingId.Value);
        Assert.Single(frames[7].Scene.Lines);
        var laterExit = Assert.Single(frames[9].Scene.Lines, static line => line.Frame.Line!.Id == "outer");
        Assert.Equal(loads[1].Binding.BindingId.Value, laterExit.BindingId); Assert.Equal(2_000_000, laterExit.ExitReplica!.HideTimeUs);
        var pixels = Pixels(snapshot, shared);
        Assert.Contains(pixels.Where(static (_, index) => index % 4 == 3), static alpha => alpha > 0);
        foreach (int position in new[] { 8, 10 })
        {
            Assert.Equal(shared.ClusterTransforms.ToArray(), frames[position].ClusterTransforms.ToArray());
            Assert.Equal(shared.TokenStyles.ToArray(), frames[position].TokenStyles.ToArray());
            Assert.Equal(pixels, Pixels(snapshot, frames[position]));
        }
        await using var reopened = await factory.CreateAsync(snapshot);
        var direct = await reopened.PrepareAsync(750_000, OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
        Assert.Equal(shared.ClusterTransforms.ToArray(), direct.ClusterTransforms.ToArray());
        Assert.Equal(pixels, Pixels(snapshot, direct)); Assert.Equal(compilationCountAfterLoad, compiler.CompilationCount);
    }

    [Fact]
    public async Task 装载代次只由实际启用v2与固定边界决定且取消不污染后续查询()
    {
        var snapshot = await Snapshot();
        using CancellationTokenSource cancelled = new(); cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => LyricSceneSampler.SampleFrame(snapshot, 750_000, cancelled.Token));
        var futureFirst = Assert.Single(LyricSceneSampler.SampleFrame(snapshot, 750_000).Lines).BindingId;
        var before = Assert.Single(LyricSceneSampler.SampleFrame(snapshot, 499_999).Lines).BindingId;
        Assert.NotEqual(before, futureFirst);
        foreach (long time in new long[] { 700_000, 750_000, 1_999_999 })
            Assert.Equal(futureFirst, Assert.Single(LyricSceneSampler.SampleFrame(snapshot, time).Lines).BindingId);
        Assert.Equal(before, Assert.Single(LyricSceneSampler.SampleFrame(snapshot, 0).Lines).BindingId);
        var disabled = snapshot with { Effects = new(snapshot.Effects.Packages, snapshot.Effects.Instances.Select(static instance => instance with { Enabled = false })) };
        Assert.Equal(before, Assert.Single(LyricSceneSampler.SampleFrame(disabled, 750_000).Lines).BindingId);
        var unused = snapshot with { Effects = new(snapshot.Effects.Packages) };
        Assert.Equal(before, Assert.Single(LyricSceneSampler.SampleFrame(unused, 750_000).Lines).BindingId);
        var reopened = snapshot with { Composition = snapshot.Composition with { FrameRate = new(60, 1) } };
        Assert.Equal(futureFirst, Assert.Single(LyricSceneSampler.SampleFrame(reopened, 750_000).Lines).BindingId);
    }

    private static async Task<ProjectSnapshot> Snapshot()
    {
        var package = await EffectPackageStorage.ReadSupportedDirectoryAsync(Path.Combine(AppContext.BaseDirectory, "effect-packages-v2", "ClusterExit"));
        var outer = LyricLine.Create("outer", "AB", new(0, 2_000_000), TimingStatus.Edited,
            [LyricToken.Create("outer-token", "AB", new(0, 2_000_000), TimingStatus.Edited)]) with
            { RubyAnnotations = [RubyAnnotation.Create("outer-ruby", "outer-token", "ab")] };
        var inner = LyricLine.Create("inner", "CD", new(500_000, 700_000), TimingStatus.Edited,
            [LyricToken.Create("inner-token", "CD", new(500_000, 700_000), TimingStatus.Edited)]);
        var project = Project.Create("真实同源重入", Composition.Create(640, 360, FrameRate.Fps30, outputDurationUs: 3_000_000),
            LyricsDocument.Create([outer, inner]), lyricStyle: LyricStyle.Default with { FontFamilyName = "DejaVu Sans", FontSizePx = 56,
                Stroke = null, Shadow = null, Glow = null, UnsungColor = RgbaColor.White, SungColor = RgbaColor.White });
        return ProjectSnapshot.FromProject(project with { EnterAnimation = null, Effects = new([package],
            [new ProjectEffectInstance { Id = "reload-exit", Name = "reload-exit", PackageId = package.PackageId, Revision = package.Revision, ExportId = "cluster-exit" }]) });
    }

    private static byte[] Pixels(ProjectSnapshot snapshot, PreparedRenderFrame prepared)
    {
        using SkiaRenderPipeline renderer = new();
        using var frame = renderer.Render(RenderRequest.At(snapshot, prepared.TimeUs) with { TransparentBackground = true, PreparedFrame = prepared });
        return frame.PixelMemory.ToArray();
    }
}
