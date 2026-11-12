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

/// <summary>真实 AdaptiveStroke 样例经固定媒体绑定读取背景特征并切换描边。</summary>
public sealed class V2MediaSamplePackageTests
{
    [Theory]
    [InlineData(1.0, 0.04)]
    [InlineData(0.0, 0.98)]
    public async Task 纯色背景按亮度切换描边颜色(double background, double expectedRed)
    {
        var (snapshot, plan) = await Snapshot();
        var profile = Profile();
        var input = MediaFeatureInput.CreateAvailableSolid(
            MediaInputContext.Create(profile, MediaMapping.CreateSolid(640, 360), plan.QueryCatalogDigest, plan.FixedDependencyDigest),
            MediaFeatureSolidSource.Create(profile, new(background, background, background, 1)));
        var styles = await StrokesAsync(snapshot with { MediaFeatureInput = input }, plan, input);
        Assert.NotEmpty(styles);
        Assert.All(styles, stroke =>
        {
            var solid = Assert.IsType<MaterialValue.SolidMaterial>(Assert.IsType<StrokeValue>(stroke).Material);
            Assert.Equal(expectedRed, solid.Color.Red, 6);
            Assert.Equal(3, stroke!.WidthPx, 6); // 纯色没有亮度方差，不加宽。
        });
    }

    [Fact]
    public async Task 无媒体时沿用工程描边()
    {
        var (snapshot, plan) = await Snapshot();
        var profile = Profile();
        var input = MediaFeatureInput.CreateNoMedia(MediaInputContext.Create(profile,
            MediaMapping.Create(640, 360, BackgroundFit.Cover, BackgroundPlaybackMode.Loop, new(0, 0, 0, 1), MediaOutputMode.Normal),
            plan.QueryCatalogDigest, plan.FixedDependencyDigest), MediaNoMediaReason.NoBackgroundContribution, new(new string('c', 64)));
        Assert.Empty(await StrokesAsync(snapshot with { MediaFeatureInput = input }, plan, input));
    }

    private static async Task<StrokeValue?[]> StrokesAsync(ProjectSnapshot snapshot, ProjectMediaDependencyPlan plan, MediaFeatureInput input)
    {
        using var sampler = new MediaFeatureSampler(input, plan, null, new MediaWorkingSetBudget(MediaAbCapacityV1.MaximumRuntimeCpuCacheBytes));
        var compiler = new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
        var compiled = await CompiledProjectEffects.CompileAsync(snapshot, compiler);
        Assert.True(compiled.Succeeded, string.Join('\n', compiled.Packages.SelectMany(package => package.Artifact.Diagnostics)));
        await using var session = await compiled.CreateFactory(static () => new SkiaRenderPipeline(), ReplayReason.Initial,
            new ProjectMediaFeatureBinding(sampler)).CreateAsync(snapshot);
        var frame = await session.PrepareAsync(250_000, OutputMode.Opaque, RenderTarget.FromComposition(snapshot.Composition));
        return [.. frame.TokenStyles.Where(style => style.Update.Unsung?.Strokes is { Layers.IsEmpty: false }).Select(style => style.Update.Unsung!.Strokes!.Layers[0])];
    }

    private static async Task<(ProjectSnapshot Snapshot, ProjectMediaDependencyPlan Plan)> Snapshot()
    {
        var package = await EffectPackageStorage.ReadSupportedDirectoryAsync(Path.Combine(AppContext.BaseDirectory, "effect-packages-v2", "AdaptiveStroke"));
        var export = EffectPackageDefinition.Read(package).Controllers.Single();
        var rows = Enumerable.Range(0, 2).Select(index => LyricLine.Create("row-" + index, "田中", new(index * 500_000, index * 500_000 + 400_000),
            TimingStatus.Edited, [LyricToken.Create("word-" + index, "田中", new(index * 500_000, index * 500_000 + 400_000), TimingStatus.Edited)]));
        var project = Project.Create("SDK v2 media", Composition.Create(640, 360, FrameRate.Fps30, outputDurationUs: 2_000_000),
            LyricsDocument.Create(rows), lyricStyle: LyricStyle.Default with { FontFamilyName = "Noto Sans CJK SC", FontSizePx = 64 });
        var snapshot = ProjectSnapshot.FromProject(project with { Effects = new([package], [new() { Id = "v2", Name = "v2",
            PackageId = package.PackageId, Revision = package.Revision, ExportId = export.Id, Subscription = new("project") }]) });
        var plan = ProjectMediaDependencyResolver.Resolve(snapshot.Effects, snapshot.LyricStyle, snapshot.Lyrics,
            snapshot.LyricPresentation, snapshot.PresetApplications);
        Assert.Single(plan.Dependencies);
        return (snapshot, plan);
    }

    private static MediaProfileBundle Profile()
    {
        MediaDigest Hash(char value) => new(new string(value, 64));
        var raw = new MediaProfileBundle(MediaProfilesV1.Presentation, MediaProfilesV1.DecoderPipeline, MediaProfilesV1.Color,
            MediaProfilesV1.Statistics, MediaProfilesV1.Quantization, MediaProfilesV1.Codec, MediaProfilesV1.Query, MediaProfilesV1.Gpu,
            Hash('a'), Hash('b'), default);
        return new(raw.Presentation, raw.DecoderPipeline, raw.Color, raw.Statistics, raw.Quantization, raw.Codec, raw.Query, raw.Gpu,
            raw.DecoderIdentity, raw.ColorInterpretation, new MediaAbIdentityWriter().ComputeProfileDigest(raw));
    }
}
