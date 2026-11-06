using System.Text.Json;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering.Abstractions;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.Compilation;

namespace MakoKaraGenerator.Rendering.Tests;

/// <summary>
/// M11 目标复杂样式：同一工程叠加字簇退场、Ruby、Warp 后最终锚点、播放头粒子、SVG 星星扫句与媒体自适应描边。
/// </summary>
public sealed class V2TargetComplexStyleTests
{
    private const long ExitAndActiveUs = 1_300_000; // 第一行退场中，第二行正在唱。
    private static readonly string[] Samples = ["ClusterExit", "WaveWarp", "StarSweep", "CursorTrail", "AdaptiveStroke"];

    [Fact]
    public async Task 全部能力在同一帧共存且星芒与粒子锚点读取Warp后的最终播放头()
    {
        var (snapshot, plan) = await Snapshot(Samples);
        var frame = await PrepareAsync(snapshot, plan, Available(plan, 1.0), ExitAndActiveUs, OutputMode.Opaque);

        Assert.NotEmpty(frame.TextWarps);                       // WaveWarp：第二行正在唱的字。
        Assert.True(frame.ClusterTransforms.Length + frame.LineTransforms.Length > 0); // ClusterExit：第一行退场。
        Assert.NotEmpty(frame.V2Particles);                     // CursorTrail。
        Assert.Contains(frame.FinalGeometry, token => !token.Ruby.IsEmpty);
        Assert.Contains(frame.TokenStyles, style => style.Update.Stroke is { Value.Material: MaterialValue.SolidMaterial { Color.Red: < 0.1 } }); // 白背景→深色描边。

        var cursors = frame.Playback.GetCursors(CursorAnchorMode.AdvanceCenter);
        Assert.NotEmpty(cursors);
        var stars = Assert.Single(frame.OverlayBatches.OfType<PathBatch>()).Paths;
        foreach (var cursor in cursors)
            Assert.Contains(stars, star => Distance(Center(star.UnitToWorld), cursor.Position) < 1e-6);

        // 去掉 Warp 后同一时刻的播放头位置不同，说明星芒确实跟随变形后的几何。
        var (flat, flatPlan) = await Snapshot(Samples.Where(name => name != "WaveWarp").ToArray());
        var straight = await PrepareAsync(flat, flatPlan, Available(flatPlan, 1.0), ExitAndActiveUs, OutputMode.Opaque);
        var straightCursors = straight.Playback.GetCursors(CursorAnchorMode.AdvanceCenter);
        Assert.Equal(cursors.Length, straightCursors.Length);
        Assert.Contains(cursors.Zip(straightCursors), pair => Distance(pair.First.Position, pair.Second.Position) > 0.5);
    }

    [Fact]
    public async Task 媒体不可用时只撤回自适应描边且透明输出照常准备()
    {
        var (snapshot, plan) = await Snapshot(Samples);
        var available = await PrepareAsync(snapshot, plan, Available(plan, 1.0), ExitAndActiveUs, OutputMode.Opaque);
        var none = await PrepareAsync(snapshot, plan, NoMedia(plan, MediaOutputMode.Normal, MediaNoMediaReason.NoBackgroundContribution),
            ExitAndActiveUs, OutputMode.Opaque);
        Assert.Contains(available.TokenStyles, style => style.Update.Stroke is not null);
        Assert.DoesNotContain(none.TokenStyles, style => style.Update.Stroke is not null);
        Assert.Equal(available.TextWarps.Length, none.TextWarps.Length);
        Assert.Equal(available.V2Particles.Length, none.V2Particles.Length);

        var transparent = await PrepareAsync(snapshot, plan, NoMedia(plan, MediaOutputMode.Transparent, MediaNoMediaReason.TransparentOutput),
            ExitAndActiveUs, OutputMode.Transparent);
        Assert.NotEmpty(transparent.TextWarps);
        Assert.NotEmpty(transparent.OverlayBatches.OfType<PathBatch>().Single().Paths);
    }

    [Fact]
    public async Task 乱序重复与重开得到同一准备帧()
    {
        var (snapshot, plan) = await Snapshot(Samples);
        var input = Available(plan, 1.0);
        var withMedia = snapshot with { MediaFeatureInput = input };
        using var sampler = new MediaFeatureSampler(input, plan, null, new MediaWorkingSetBudget(MediaAbCapacityV1.MaximumRuntimeCpuCacheBytes));
        var compiled = await Compile(withMedia);
        var factory = compiled.CreateFactory(static () => new SkiaRenderPipeline(), ReplayReason.Initial, new ProjectMediaFeatureBinding(sampler));
        await using var session = await factory.CreateAsync(withMedia);
        var target = RenderTarget.FromComposition(withMedia.Composition);
        string first = Signature(await session.PrepareAsync(ExitAndActiveUs, OutputMode.Opaque, target));
        foreach (long time in new long[] { 2_100_000, 400_000, 1_650_000 })
            await session.PrepareAsync(time, OutputMode.Opaque, target);
        Assert.Equal(first, Signature(await session.PrepareAsync(ExitAndActiveUs, OutputMode.Opaque, target)));
        await using var reopened = await factory.CreateAsync(withMedia);
        Assert.Equal(first, Signature(await reopened.PrepareAsync(ExitAndActiveUs, OutputMode.Opaque, target)));
    }

    private static async Task<PreparedRenderFrame> PrepareAsync(ProjectSnapshot snapshot, ProjectMediaDependencyPlan plan,
        MediaFeatureInput input, long time, OutputMode mode)
    {
        var withMedia = snapshot with { MediaFeatureInput = input };
        using var sampler = new MediaFeatureSampler(input, plan, null, new MediaWorkingSetBudget(MediaAbCapacityV1.MaximumRuntimeCpuCacheBytes));
        var compiled = await Compile(withMedia);
        await using var session = await compiled.CreateFactory(static () => new SkiaRenderPipeline(), ReplayReason.Initial,
            new ProjectMediaFeatureBinding(sampler)).CreateAsync(withMedia);
        return await session.PrepareAsync(time, mode, RenderTarget.FromComposition(withMedia.Composition));
    }

    private static async Task<CompiledProjectEffects> Compile(ProjectSnapshot snapshot)
    {
        var compiled = await CompiledProjectEffects.CompileAsync(snapshot, new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0")),
            new PackageImageValidator(), new PackageShaderValidator(), new PackageImageGraphValidator(), new PackageMaterialValidator(), new PackageSvgValidator());
        Assert.True(compiled.Succeeded, string.Join('\n', compiled.Packages.SelectMany(package => package.Artifact.Diagnostics)));
        return compiled;
    }

    private static async Task<(ProjectSnapshot Snapshot, ProjectMediaDependencyPlan Plan)> Snapshot(string[] samples)
    {
        List<EffectPackageContents> packages = [];
        List<ProjectEffectInstance> instances = [];
        foreach (string name in samples)
        {
            var package = await EffectPackageStorage.ReadSupportedDirectoryAsync(Path.Combine(AppContext.BaseDirectory, "effect-packages-v2", name));
            packages.Add(package);
            instances.Add(new()
            {
                Id = name, Name = name, PackageId = package.PackageId, Revision = package.Revision, Seed = 11,
                ExportId = EffectPackageDefinition.Read(package).Controllers.Single().Id, Subscription = new("project"),
                Parameters = EffectParameterOverrides.Parse(JsonSerializer.Serialize(new { })),
            });
        }
        LyricLine Row(string id, string[] texts, string[] rubies, long start, long end)
        {
            long step = (end - start) / texts.Length;
            var tokens = texts.Select((text, index) => LyricToken.Create(id + "-" + index, text,
                new(start + index * step, index == texts.Length - 1 ? end : start + (index + 1) * step), TimingStatus.Edited)).ToArray();
            return LyricLine.Create(id, string.Concat(texts), new(start, end), TimingStatus.Edited, tokens) with
            {
                RubyAnnotations = [.. rubies.Select((ruby, index) => RubyAnnotation.Create(id + "-ruby-" + index, tokens[index].Id, ruby))],
            };
        }
        var rows = new[]
        {
            Row("first", ["星", "空"], ["ほし", "ぞら"], 0, 1_000_000),
            Row("second", ["流", "れ", "る"], ["なが"], 1_100_000, 2_400_000),
        };
        var project = Project.Create("M11 目标复杂样式", Composition.Create(640, 360, FrameRate.Fps30, outputDurationUs: 3_000_000),
            LyricsDocument.Create(rows), lyricStyle: LyricStyle.Default with { FontFamilyName = "Noto Sans CJK SC", FontSizePx = 64 });
        var snapshot = ProjectSnapshot.FromProject(project with { Effects = new([.. packages], [.. instances]), EnterAnimation = null });
        var plan = ProjectMediaDependencyResolver.Resolve(snapshot.Effects, snapshot.LyricStyle, snapshot.Lyrics,
            snapshot.LyricPresentation, snapshot.PresetApplications);
        Assert.Single(plan.Dependencies);
        return (snapshot, plan);
    }

    private static MediaFeatureInput.AvailableSolid Available(ProjectMediaDependencyPlan plan, double gray)
    {
        var profile = Profile();
        return MediaFeatureInput.CreateAvailableSolid(
            MediaInputContext.Create(profile, MediaMapping.CreateSolid(640, 360), plan.QueryCatalogDigest, plan.FixedDependencyDigest),
            MediaFeatureSolidSource.Create(profile, new(gray, gray, gray, 1)));
    }

    private static MediaFeatureInput.NoMedia NoMedia(ProjectMediaDependencyPlan plan, MediaOutputMode mode, MediaNoMediaReason reason)
        => MediaFeatureInput.CreateNoMedia(MediaInputContext.Create(Profile(),
            MediaMapping.Create(640, 360, BackgroundFit.Cover, BackgroundPlaybackMode.Loop, new(0, 0, 0, 1), mode),
            plan.QueryCatalogDigest, plan.FixedDependencyDigest), reason, new(new string('c', 64)));

    private static MediaProfileBundle Profile()
    {
        MediaDigest Hash(char value) => new(new string(value, 64));
        var raw = new MediaProfileBundle(MediaProfilesV1.Presentation, MediaProfilesV1.DecoderPipeline, MediaProfilesV1.Color,
            MediaProfilesV1.Statistics, MediaProfilesV1.Quantization, MediaProfilesV1.Codec, MediaProfilesV1.Query, MediaProfilesV1.Gpu,
            Hash('a'), Hash('b'), default);
        return new(raw.Presentation, raw.DecoderPipeline, raw.Color, raw.Statistics, raw.Quantization, raw.Codec, raw.Query, raw.Gpu,
            raw.DecoderIdentity, raw.ColorInterpretation, new MediaAbIdentityWriter().ComputeProfileDigest(raw));
    }

    private static Point2 Center(Affine2 unitToWorld) => unitToWorld.Map(new(0.5, 0.5));

    private static double Distance(Point2 a, Point2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static string Signature(PreparedRenderFrame frame) => JsonSerializer.Serialize(new
    {
        Warps = frame.TextWarps.Select(item => JsonSerializer.Serialize(item, item.GetType())).ToArray(),
        Clusters = frame.ClusterTransforms.Select(item => JsonSerializer.Serialize(item, item.GetType())).ToArray(),
        Styles = frame.TokenStyles.Select(item => JsonSerializer.Serialize(item, item.GetType())).ToArray(),
        frame.V2Particles,
        Batches = frame.OverlayBatches.Select(batch => JsonSerializer.Serialize(batch, batch.GetType())).ToArray(),
    });
}
