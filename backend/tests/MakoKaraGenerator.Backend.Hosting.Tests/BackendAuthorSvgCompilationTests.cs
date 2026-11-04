using System.Diagnostics;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class BackendAuthorSvgCompilationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 无SVG作者编译不把旧shader执行校验并入源码编译(bool asynchronous)
    {
        using TemporaryDirectory directory = new();
        var package = EffectPackageShaderFixture.Contents(source: "half4 main(float2 p) { return missing; }");
        string archive = directory.FilePath("legacy-shader.mkfx");
        await EffectPackageTransfer.WriteArchiveAsync(archive, package);
        ResourceStore resources = new(new() { ResourceRoot = directory.FilePath("resources") });
        await using FileStream stream = File.OpenRead(archive);
        StoredAsset asset = await resources.UploadAsync("client", "legacy-shader.mkfx", "application/octet-stream", stream);
        ProjectSessionRegistry registry = new("author-svg-scope", resources, NewProjectPreparationService.CreateDefault());
        registry.ConfigurePersistence(new() { StateRoot = directory.FilePath("state") });
        registry.ConfigureAuthorWorkflows(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
        try
        {
            var created = await registry.CreateAuthorDraftAsync("client", new(asset.AssetId, "scale", "Legacy shader", Metadata(registry)));
            Assert.True(created.Accepted, created.Error?.Message);
            AuthorDraftResponse draft = created.Response!;
            AuthorCompilationResponse result;
            if (asynchronous)
            {
                var accepted = await registry.StartAuthorCompilationAsync("client", draft.DraftId,
                    new(draft.DraftRevision, Metadata(registry), "async"));
                Assert.True(accepted.Accepted, accepted.Error?.Message);
                string jobId = accepted.Response!.JobId;
                Stopwatch elapsed = Stopwatch.StartNew();
                while (registry.ReadJob("client", jobId).Response?.State is not ("succeeded" or "failed" or "cancelled"))
                {
                    Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(8), "作者编译未在8秒内结束。");
                    await Task.Delay(20);
                }
                Assert.Equal("succeeded", registry.ReadJob("client", jobId).Response!.State);
                var read = registry.ReadAuthorCompilationResult("client", jobId);
                Assert.True(read.Accepted, read.Error?.Message); result = read.Response!;
            }
            else
            {
                var compiled = await registry.CompileAuthorDraftAsync("client", draft.DraftId, new(draft.DraftRevision, Metadata(registry)));
                Assert.True(compiled.Accepted, compiled.Error?.Message); result = compiled.Response!;
            }
            Assert.True(result.Succeeded, string.Join('\n', result.Diagnostics.Select(diagnostic => diagnostic.Message)));
            Assert.Equal(package.Revision, result.SourceRevision); Assert.Equal(draft.DraftRevision, result.DraftRevision);
            Assert.False(result.Stale); Assert.NotEqual("none", result.ArtifactHash);
            Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.IsError);
            AuthorDraftResponse current = registry.ReadAuthorDraft("client", draft.DraftId).Response!;
            Assert.Equal(draft.CurrentSourceRevision, current.CurrentSourceRevision);
            Assert.Equal(result.SourceRevision, current.LastCompilation!.SourceRevision);
            Assert.True(current.LastCompilation.Succeeded);
        }
        finally { await registry.DisposeAsync(); }
    }

    private static RequestMetadataDto Metadata(ProjectSessionRegistry registry)
        => new(Guid.NewGuid().ToString("N"), registry.HostInstanceId, registry.ServerTimeUtc.AddMinutes(1));
}
