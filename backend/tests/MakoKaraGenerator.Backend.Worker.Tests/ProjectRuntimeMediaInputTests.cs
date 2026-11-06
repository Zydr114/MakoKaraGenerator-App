using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.Compilation;

namespace MakoKaraGenerator.Backend.Worker.Tests;

/// <summary>Worker 从 KRI1 私有媒体授权重建固定输入与采样绑定。</summary>
public sealed class ProjectRuntimeMediaInputTests
{
    [Fact]
    public async Task 运行输入携带纯色媒体输入时Worker创建绑定并求值()
    {
        var package = await EffectPackageStorage.ReadSupportedDirectoryAsync(Path.Combine(AppContext.BaseDirectory, "effect-packages-v2", "AdaptiveStroke"));
        var export = EffectPackageDefinition.Read(package).Controllers.Single();
        var line = LyricLine.Create("row", "田中", new(0, 400_000), TimingStatus.Edited,
            [LyricToken.Create("word", "田中", new(0, 400_000), TimingStatus.Edited)]);
        var project = Project.Create("worker media", Composition.Create(640, 360, FrameRate.Fps30, outputDurationUs: 1_000_000),
            LyricsDocument.Create([line]), lyricStyle: LyricStyle.Default with { FontFamilyName = "Noto Sans CJK SC", FontSizePx = 64 });
        var basis = ProjectSnapshot.FromProject(project with { Effects = new([package], [new() { Id = "v2", Name = "v2",
            PackageId = package.PackageId, Revision = package.Revision, ExportId = export.Id, Subscription = new("project") }]) });
        var plan = ProjectMediaDependencyResolver.Resolve(basis.Effects, basis.LyricStyle, basis.Lyrics, basis.LyricPresentation, basis.PresetApplications);
        MediaDigest Hash(char value) => new(new string(value, 64));
        var raw = new MediaProfileBundle(MediaProfilesV1.Presentation, MediaProfilesV1.DecoderPipeline, MediaProfilesV1.Color,
            MediaProfilesV1.Statistics, MediaProfilesV1.Quantization, MediaProfilesV1.Codec, MediaProfilesV1.Query, MediaProfilesV1.Gpu,
            Hash('a'), Hash('b'), default);
        var profile = new MediaProfileBundle(raw.Presentation, raw.DecoderPipeline, raw.Color, raw.Statistics, raw.Quantization,
            raw.Codec, raw.Query, raw.Gpu, raw.DecoderIdentity, raw.ColorInterpretation, new MediaAbIdentityWriter().ComputeProfileDigest(raw));
        var input = MediaFeatureInput.CreateAvailableSolid(
            MediaInputContext.Create(profile, MediaMapping.CreateSolid(640, 360), plan.QueryCatalogDigest, plan.FixedDependencyDigest),
            MediaFeatureSolidSource.Create(profile, new(1, 1, 1, 1)));
        byte[] bytes = ProjectRuntimeInputCodec.Encode(basis with { MediaFeatureInput = input }, null);

        var compiler = new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
        using var runtime = await ProjectRuntimeInput.OpenAsync(bytes, CancellationToken.None, compiler);
        Assert.Equal(input.InputRevision, runtime.Snapshot.MediaFeatureInput!.InputRevision);
        // 工厂准入要求同一固定输入的宿主绑定：能建会话即证明 Worker 已从授权重建绑定。
        await using (var session = await runtime.CreateFactory(ReplayReason.Initial, renderBackend: "vulkan").CreateAsync(runtime.Snapshot)) { }

        // 双输出验证的透明半程：另建透明无媒体输入，与 Host 透明导出得到同一固定输入。
        using (var transparent = await runtime.CreateTransparentOutputAsync(ReplayReason.Initial, "vulkan", CancellationToken.None))
        {
            Assert.NotNull(transparent);
            var absent = Assert.IsType<MediaFeatureInput.NoMedia>(transparent.Output.Snapshot.MediaFeatureInput);
            Assert.Equal(MediaNoMediaReason.TransparentOutput, absent.Reason);
            Assert.Equal(ProjectRuntimeMediaOutputs.TransparentNoMedia(basis)!.InputRevision, absent.InputRevision);
            await using var session = await transparent.Output.Sessions.CreateAsync(transparent.Output.Snapshot);
            using var frame = await session.RenderAsync(RenderRequest.At(transparent.Output.Snapshot, 200_000) with { TransparentBackground = true });
            Assert.Equal(640, frame.Width);
        }

        using var missing = await ProjectRuntimeInput.OpenAsync(ProjectRuntimeInputCodec.Encode(basis, null), CancellationToken.None, compiler);
        var error = await Assert.ThrowsAsync<EffectOutputException>(async () =>
            await missing.CreateFactory(ReplayReason.Initial, renderBackend: "vulkan").CreateAsync(missing.Snapshot));
        Assert.Equal("MediaFeatureBindingRequired", error.Code);
    }
}
