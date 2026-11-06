using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
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

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class V2ExitBoundaryOwnershipTests
{
    [Fact]
    public async Task 第一百二十九个合格退出装载预算错误归属其真实声明实例()
    {
        var package = await Package(600_000); var unrelated = await Package(3_000_000);
        var snapshot = Snapshot(129, new([unrelated, package],
            [Instance(unrelated, "unrelated-first", "line-000"), Instance(package, "budget-owner")]));
        var compiled = await Compile(snapshot);
        var timeline = TimelineProjection.Create(snapshot, ReplayReason.Initial);
        var last = timeline.Events.OfType<BindingLeft>().OrderBy(static left => left.TimeUs)
            .ThenBy(static left => left.Binding.BindingId.Value, StringComparer.Ordinal).Last();
        Assert.NotEqual("line-000", last.Binding.LineId.Value);
        var queryError = Assert.Throws<PreparedFrameException>(() => new PresentationIndex(timeline).Query(1_300_000, 3_000_000));
        Assert.Equal(last.Binding.BindingId, queryError.BindingId);
        await using var session = await new PureFrameSourceFactory(compiled.Effects, snapshot.EffectInputsFingerprint!,
            static () => new SkiaRenderPipeline()).CreateAsync(snapshot);
        var error = await Assert.ThrowsAsync<EffectFailureException>(async () => await session.PrepareAsync(1_300_000,
            OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition)));
        Assert.Equal("ExitReplicaBudgetExceeded", error.Code); Assert.Equal("PrepareExitReplica", error.Stage);
        Assert.Equal(1_300_000, error.TimeUs); Assert.Equal("budget-owner", error.InstanceId.Value);
        Assert.Equal(new EffectPackageIdentity(package.PackageId, package.Revision, "cluster-exit"), error.PackageIdentity);
        Assert.Equal(last.Binding.BindingId, Assert.IsType<PreparedFrameException>(error.InnerException).BindingId);
    }

    [Fact]
    public async Task 全退出索引初始化失败归属首个启用的真实寿命声明()
    {
        var noExit = await Package(0); var package = await Package(600_000); var later = await Package(3_000_000);
        var snapshot = Snapshot(1, new([noExit, package, later],
            [Instance(noExit, "no-exit-first"), Instance(package, "initialization-owner"), Instance(later, "later")]));
        var compiled = await Compile(snapshot);
        var timeline = TimelineProjection.Create(snapshot, ReplayReason.Initial);
        var enter = Assert.Single(timeline.Events.OfType<BindingEntered>());
        // A deliberately corrupt internal index exercises this admission boundary
        // without allocating two hundred thousand production loads. It is not a
        // claim that a normal project can create duplicate source events.
        var timelineConstructor = Assert.Single(typeof(TimelineProjection).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic));
        var corrupt = (TimelineProjection)timelineConstructor.Invoke([timeline.Content, timeline.Scene,
            ImmutableArray.Create<TimelineEvent>(enter, enter), ReplayReason.Initial]);
        var context = new ProjectContext { Content = timeline.Content, Scene = timeline.Scene,
            CanvasSize = new(snapshot.Composition.CanvasWidth, snapshot.Composition.CanvasHeight), DurationUs = snapshot.Composition.OutputDurationUs };
        using SkiaRenderPipeline geometry = new();
        var hostType = typeof(PureFrameSourceFactory).Assembly.GetType("MakoKaraGenerator.Scripting.Execution.FrameEffectHost", throwOnError: true)!;
        var hostConstructor = Assert.Single(hostType.GetConstructors());
        var wrapper = Assert.Throws<TargetInvocationException>(() => hostConstructor.Invoke([snapshot, context, geometry,
            compiled.Effects, ImmutableArray<ResolvedSourceSubscription>.Empty, VisualOutputBudget.Default, ParticleSimulationBudget.Default,
            null, corrupt, null, null, CancellationToken.None]));
        var error = Assert.IsType<EffectFailureException>(wrapper.InnerException);
        Assert.Equal("InvalidPresentationIndex", error.Code); Assert.Equal("InitializeExitReplica", error.Stage);
        Assert.Equal(0, error.TimeUs); Assert.Equal("initialization-owner", error.InstanceId.Value);
        Assert.Equal(new EffectPackageIdentity(package.PackageId, package.Revision, "cluster-exit"), error.PackageIdentity);
        Assert.IsType<PreparedFrameException>(error.InnerException);
    }

    private static async Task<EffectPackageContents> Package(long durationUs)
    {
        var package = await EffectPackageStorage.ReadSupportedDirectoryAsync(Path.Combine(AppContext.BaseDirectory, "effect-packages-v2", "ClusterExit"));
        var manifest = JsonNode.Parse(package.Manifest.AsSpan())!;
        manifest["lineExit"]!["durationUs"] = durationUs;
        return EffectPackageContents.CreateSupported(Encoding.UTF8.GetBytes(manifest.ToJsonString()),
            package.Files.Select(file => KeyValuePair.Create(file.Path, (ReadOnlyMemory<byte>)file.Content.ToArray())));
    }
    private static ProjectEffectInstance Instance(EffectPackageContents package, string id, string? lineId = null) => new()
    {
        Id = id, Name = id, PackageId = package.PackageId, Revision = package.Revision, ExportId = "cluster-exit",
        Binding = lineId is null ? new(ProjectEffectBindingKind.Project) : new(ProjectEffectBindingKind.Lines, ids: [lineId]),
    };
    private static ProjectSnapshot Snapshot(int count, ProjectEffects effects)
    {
        var branches = Enumerable.Range(0, count).Select(index => LyricBranch.Create($"branch-{index:D3}", "parallel", LyricBranchRole.Custom,
            [LyricLine.Create($"line-{index:D3}", "A", new(0, 1_000_000), TimingStatus.Edited,
                [LyricToken.Create($"word-{index:D3}", "A", new(0, 1_000_000), TimingStatus.Edited)])]));
        var lyrics = new LyricsDocument { PrimaryBranchId = "branch-000", Branches = [.. branches] };
        var project = Project.Create("exit boundary owner", Composition.Create(640, 360, FrameRate.Fps30, outputDurationUs: 4_000_000), lyrics,
            lyricStyle: LyricStyle.Default with { FontFamilyName = "DejaVu Sans", FontSizePx = 32, Stroke = null, Shadow = null, Glow = null });
        return ProjectSnapshot.FromProject(project with { Effects = effects, EnterAnimation = null });
    }
    private static async Task<CompiledProjectEffects> Compile(ProjectSnapshot snapshot)
    {
        var compiler = new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
        var compiled = await CompiledProjectEffects.CompileAsync(snapshot, compiler);
        Assert.True(compiled.Succeeded, string.Join('\n', compiled.Packages.SelectMany(package => package.Artifact.Diagnostics)));
        return compiled;
    }
}
