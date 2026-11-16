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
using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Rendering.Tests;

/// <summary>
/// M11 目标复杂样式：同一工程叠加字簇退场、Ruby、Warp 后最终锚点、播放头粒子、SVG 星星扫句与媒体自适应描边。
/// M13 补全夹具：同句双实例、缺时间字、跨 token 连字与根容器父子矩阵，并在行／字／退场边界前后 1 µs 取帧。
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
        Assert.Contains(frame.TokenStyles, style => style.Update.Unsung?.Strokes?.Layers is [{ Material: MaterialValue.SolidMaterial { Color.Red: < 0.1 } }, ..]); // 白背景→深色描边。

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
        Assert.Contains(available.TokenStyles, style => style.Update.Unsung?.Strokes is not null);
        Assert.DoesNotContain(none.TokenStyles, style => style.Update.Unsung?.Strokes is not null);
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

    [Fact]
    public async Task 组合夹具在边界前后一微秒按半开区间切换且乱序重开逐项一致()
    {
        var (snapshot, plan) = await Snapshot(Samples);
        var input = Available(plan, 1.0);
        var withMedia = snapshot with { MediaFeatureInput = input };
        using var sampler = new MediaFeatureSampler(input, plan, null, new MediaWorkingSetBudget(MediaAbCapacityV1.MaximumRuntimeCpuCacheBytes));
        var factory = (await Compile(withMedia)).CreateFactory(static () => new SkiaRenderPipeline(), ReplayReason.Initial,
            new ProjectMediaFeatureBinding(sampler));
        var target = RenderTarget.FromComposition(withMedia.Composition);
        Dictionary<long, PreparedRenderFrame> frames = [];
        await using (var forward = await factory.CreateAsync(withMedia))
            foreach (long time in Boundaries) frames[time] = await forward.PrepareAsync(time, OutputMode.Opaque, target);

        // 行区间与退场副本：第一行 [0, 1 s)，退场 600 ms 到 1.6 s（不含）；进度为 0 的首个时刻不写字簇变换。
        Assert.Equal(["first"], Lines(frames[999_999])); Assert.Equal(["first*"], Lines(frames[1_000_000]));
        Assert.Empty(frames[1_000_000].ClusterTransforms);
        Assert.All(frames[1_000_001].ClusterTransforms, item => Assert.Equal(Binding(frames[1_000_001], "first"), item.Target.BindingId.Value));
        Assert.NotEmpty(frames[1_000_001].ClusterTransforms);
        Assert.Equal(["first*"], Lines(frames[1_099_999])); Assert.Equal(["first*", "second"], Lines(frames[1_100_000]));
        Assert.Equal(["first*", "second"], Lines(frames[1_599_999])); Assert.Equal(["second"], Lines(frames[1_600_000]));
        // 行间空档没有真实播放头；起唱时刻即有。
        Assert.Single(frames[999_999].Playback.GetCursors(CursorAnchorMode.AdvanceCenter));
        Assert.Empty(frames[1_000_000].Playback.GetCursors(CursorAnchorMode.AdvanceCenter));
        Assert.Empty(frames[1_099_999].Playback.GetCursors(CursorAnchorMode.AdvanceCenter));
        Assert.Single(frames[1_100_000].Playback.GetCursors(CursorAnchorMode.AdvanceCenter));
        // Warp 在起唱时刻包络为 0（riseUs 渐入），1 µs 后才写；唱完后按 settleUs 回落而非立即消失。
        Assert.Equal(["first-0"], Warped(frames[500_000])); Assert.Equal(["first-0", "first-1"], Warped(frames[500_001]));
        Assert.DoesNotContain("second-0", Warped(frames[1_100_000])); Assert.Contains("second-0", Warped(frames[1_100_001]));
        // 缺时间字没有演唱区间、跨 token 连字不可拆，任何时刻都不变形。
        Assert.All(frames.Values, frame => Assert.Empty(Warped(frame).Intersect(["second-untimed", "third-1", "third-2", "third-3"])));
        Assert.Contains(frames[2_450_000].Scene.TokenGeometries.SelectMany(token => token.Clusters), cluster => cluster.OwnerTokens.Length > 1);
        // 连字行退场整行移动，不出字簇变换；其他行仍按字簇退场。
        Assert.Empty(frames[2_870_000].LineTransforms);
        Assert.Equal(Binding(frames[2_870_001], "third"), Assert.Single(frames[2_870_001].LineTransforms).Target.BindingId.Value);
        Assert.DoesNotContain(frames[2_870_001].ClusterTransforms, item => item.Target.BindingId.Value == Binding(frames[2_870_001], "third"));
        Assert.Contains(frames[2_400_001].ClusterTransforms, item => item.Target.BindingId.Value == Binding(frames[2_400_001], "second"));
        // 同句双实例：工程实例与第二行实例同时发射，身份互不重叠；第二行实例只在该行起唱后出生、行末后不再出生。
        Assert.Empty(Births(frames[1_099_999], "CursorTrail-second"));
        var both = frames[1_599_999].V2ParticleSnapshots.Where(item => item.InstanceId.Value.StartsWith("CursorTrail", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, both.Length); Assert.All(both, item => Assert.NotEmpty(item.Alive));
        Assert.Empty(both[0].Alive.Select(item => item.Id).Intersect(both[1].Alive.Select(item => item.Id)));
        Assert.NotEmpty(Births(frames[2_400_001], "CursorTrail-second"));
        Assert.Equal(Births(frames[2_400_001], "CursorTrail-second"), Births(frames[2_870_001], "CursorTrail-second"));
        // 根容器旋转缩放（父子矩阵）下，每个有播放头的边界帧星芒都落在最终播放头上。
        foreach (var frame in frames.Values)
            foreach (var cursor in frame.Playback.GetCursors(CursorAnchorMode.AdvanceCenter))
                Assert.Contains(frame.OverlayBatches.OfType<PathBatch>().Single().Paths, star => Distance(Center(star.UnitToWorld), cursor.Position) < 1e-6);

        // 倒序取帧与重开会话得到逐项相同的准备帧。
        await using var reversed = await factory.CreateAsync(withMedia);
        foreach (long time in Enumerable.Reverse(Boundaries))
            Assert.Equal(Signature(frames[time]), Signature(await reversed.PrepareAsync(time, OutputMode.Opaque, target)));
    }

    private static readonly long[] Boundaries = [499_999, 500_000, 500_001, 999_999, 1_000_000, 1_000_001, 1_099_999, 1_100_000, 1_100_001,
        1_599_999, 1_600_000, 1_600_001, 2_399_999, 2_400_000, 2_400_001, 2_449_999, 2_450_000, 2_450_001, 2_869_999, 2_870_000, 2_870_001];

    private static string[] Lines(PreparedRenderFrame frame)
        => [.. frame.Scene.Lines.Select(line => line.Frame.Line!.Id + (line.ExitReplica is null ? "" : "*")).Order(StringComparer.Ordinal)];

    private static string Binding(PreparedRenderFrame frame, string lineId) => frame.Scene.Lines.Single(line => line.Frame.Line!.Id == lineId).BindingId!;

    private static string[] Warped(PreparedRenderFrame frame) => [.. frame.TextWarps
        .Select(item => item.Target is TextTarget.Family family ? family.Value.TokenId.Value : "non-family").Order(StringComparer.Ordinal)];

    private static ParticleIdentity[] Births(PreparedRenderFrame frame, string instance)
        => [.. frame.V2ParticleSnapshots.Single(item => item.InstanceId.Value == instance).Births.Births.Select(item => item.Id)];

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
            if (name == "CursorTrail") // 同句双实例：第二个实例只装载第二行，与工程实例在同一行并存。
                instances.Add(instances[^1] with { Id = name + "-second", Name = name + " 第二行", Seed = 12,
                    Binding = new(ProjectEffectBindingKind.Lines, ids: ["second"]) });
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
        var second = Row("second", ["流", "れ", "る"], ["なが"], 1_100_000, 2_400_000);
        var rows = new[]
        {
            Row("first", ["星", "空"], ["ほし", "ぞら"], 0, 1_000_000),
            // 末字“光”缺时间。
            second with { Text = "流れる光", Tokens = second.Tokens.Add(LyricToken.Create("second-untimed", "光")) },
            // 逐字母 token：字体把 ffi 整形为跨 token 连字。
            LyricLine.Create("third", "office", new(2_450_000, 2_870_000), TimingStatus.Edited,
                [.. "office".Select((character, index) => LyricToken.Create("third-" + index, character.ToString(),
                    new(2_450_000 + index * 70_000, 2_520_000 + index * 70_000), TimingStatus.Edited))]),
        };
        var project = Project.Create("M11 目标复杂样式", Composition.Create(640, 360, FrameRate.Fps30, outputDurationUs: 3_000_000),
            LyricsDocument.Create(rows), lyricStyle: LyricStyle.Default with { FontFamilyName = TestFonts.Cjk, FontSizePx = 64 });
        // 根容器带旋转与缩放：所有文字、播放头和锚点都经过父子矩阵。
        {
            var tree = project.LyricPresentation; var root = tree.FindNode(tree.FindProgram("primary")!.RootContainerId)!;
            project = project with { LyricPresentation = tree with { Nodes = tree.Nodes.Replace(root,
                root with { Placement = root.Placement with { RotationDegrees = 6, Scale = .9 } }) } };
        }
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
        Lines = Lines(frame),
        LineTransforms = frame.LineTransforms.Select(item => JsonSerializer.Serialize(item, item.GetType())).ToArray(),
        Warps = frame.TextWarps.Select(item => JsonSerializer.Serialize(item, item.GetType())).ToArray(),
        Clusters = frame.ClusterTransforms.Select(item => JsonSerializer.Serialize(item, item.GetType())).ToArray(),
        Styles = frame.TokenStyles.Select(item => JsonSerializer.Serialize(item, item.GetType())).ToArray(),
        frame.V2Particles,
        Batches = frame.OverlayBatches.Select(batch => JsonSerializer.Serialize(batch, batch.GetType())).ToArray(),
    });
}
