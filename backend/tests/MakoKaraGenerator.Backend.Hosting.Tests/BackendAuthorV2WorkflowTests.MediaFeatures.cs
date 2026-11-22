using System.Text.Json.Nodes;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed partial class BackendAuthorV2WorkflowTests
{
    [Fact]
    public async Task 媒体依赖作者试样透明预览固定为透明无媒体且未装分析服务时为无背景贡献()
    {
        await using Fixture fixture = await Fixture.CreateAsync(configureManifest: AddMediaQuery);
        MediaFeatureInput transparent = await SampleInputAsync(fixture, transparentOutput: true);
        Assert.Equal(MediaNoMediaReason.TransparentOutput, Assert.IsType<MediaFeatureInput.NoMedia>(transparent).Reason);
        Assert.Equal(MediaOutputMode.Transparent, transparent.Context.Mapping.OutputMode);
        MediaFeatureInput opaque = await SampleInputAsync(fixture, transparentOutput: false);
        Assert.Equal(MediaNoMediaReason.NoBackgroundContribution, Assert.IsType<MediaFeatureInput.NoMedia>(opaque).Reason);
        Assert.Equal(MediaOutputMode.Normal, opaque.Context.Mapping.OutputMode);
    }

    [Fact]
    public async Task 媒体依赖作者试样经真实宿主分析得到可用文件输入且再次试样复用同一分析制品()
    {
        string tools = Path.Combine(AppContext.BaseDirectory, "media-tools");
        if (!MediaPresentationDeploymentDiscovery.IsInstalled(tools)) return; // 非 Linux 部署没有受控解码。
        await using Fixture fixture = await Fixture.CreateAsync(configureManifest: AddMediaQuery);
        string state = Directory.CreateTempSubdirectory("mkg-author-media-").FullName;
        try
        {
            await using BackendMediaFeatureService service = BackendMediaFeatureService.TryCreate(state, tools)!;
            fixture.Registry.ConfigureMediaFeatures(service);
            var first = Assert.IsType<MediaFeatureInput.AvailableFile>(await SampleInputAsync(fixture, transparentOutput: false));
            var second = Assert.IsType<MediaFeatureInput.AvailableFile>(await SampleInputAsync(fixture, transparentOutput: false));
            // 每次试样都重新生成场景，依赖摘要随之不同；试样背景相同，分析只做一次。
            Assert.Equal(first.Source, second.Source);
            Assert.Equal(first.Artifact, second.Artifact);
            Assert.Equal(MediaOutputMode.Normal, first.Context.Mapping.OutputMode);
            Assert.IsType<MediaFeatureInput.NoMedia>(await SampleInputAsync(fixture, transparentOutput: true));
        }
        finally { Directory.Delete(state, recursive: true); }
    }

    private static async Task<MediaFeatureInput> SampleInputAsync(Fixture fixture, bool transparentOutput)
    {
        AuthorDraftResponse draft = await fixture.ReadAsync();
        PreviewSourceDto source = new("authorDraft", draft.DraftId, draft.DraftRevision,
            new(TemplatePreviewLimits.SingleLineSampleId, "token"), draft.Configuration!.SourceRevision);
        BackendOperationResult<AuthorPreviewCapture> captured = await fixture.Registry.CaptureAuthorPreviewAsync("client", source, "gpu",
            transparentOutput);
        Assert.True(captured.Accepted, captured.Error?.Message);
        using BackendPreparedProjectRuntimeInput prepared = captured.Response!.PreparedInput;
        return Assert.IsAssignableFrom<MediaFeatureInput>(ProjectRuntimeInputCodec.Decode(prepared.SourceBytes).Snapshot.MediaFeatureInput);
    }

    // 与 Core.Tests 的 MediaQueryPackageFixture 同形：控制器导出声明一个媒体查询，三种回退都走作者分支。
    private static void AddMediaQuery(JsonObject manifest)
    {
        manifest["requires"] = new JsonArray("media.features", "token.affine");
        manifest["parameters"]!.AsArray().Add(new JsonObject { ["id"] = "window", ["type"] = "duration", ["label"] = "窗口",
            ["default"] = 200_000L, ["min"] = 0, ["max"] = 2_000_000L });
        manifest["mediaQueries"] = new JsonArray(new JsonObject { ["id"] = "query", ["space"] = "canvasPixels",
            ["window"] = new JsonObject { ["kind"] = "parameter", ["parameter"] = "window" } });
        JsonObject export = manifest["exports"]![0]!.AsObject();
        export["parameters"]!.AsArray().Add("window");
        JsonObject State() => new() { ["kind"] = "authorBranch", ["outputs"] = new JsonArray("scale") };
        export["mediaQueries"] = new JsonArray(new JsonObject { ["query"] = "query",
            ["fallback"] = new JsonObject { ["pending"] = State(), ["unavailable"] = State(), ["noMedia"] = State() } });
    }
}
