using System.Text;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class BackendAuthorPackageFilesTests
{
    [Theory]
    [InlineData("ink")]
    [InlineData("tint")]
    public async Task R10无CSharp材质图草稿不宣称可编译(string exportId)
    {
        await using Fixture fixture = await Fixture.CreateAsync(MixedEffectPackageFixture.Contents(pure: true), exportId);
        Assert.False(fixture.Draft.CanCompile);
    }

    [Fact]
    public async Task 原源码入口无变化保存保留草稿源码修订及编译摘要()
    {
        await using Fixture fixture = await Fixture.CreateAsync(EffectPackageFixture.Contents(), "scale");
        BackendOperationResult<AuthorCompilationResponse> compilation = await fixture.Registry.CompileAuthorDraftAsync("client",
            fixture.Draft.DraftId, new(fixture.Draft.DraftRevision, fixture.Metadata()));
        Assert.True(compilation.Accepted, compilation.Error?.Message);
        AuthorDraftResponse before = fixture.Registry.ReadAuthorDraft("client", fixture.Draft.DraftId).Response!;
        Assert.NotNull(before.LastCompilation);
        AuthorDraftSourcesResponse sources = fixture.Registry.ReadAuthorSources("client", before.DraftId,
            before.DraftRevision, before.CurrentSourceRevision).Response!;
        BackendOperationResult<AuthorSourceRevisionResponse> saved = await fixture.Registry.SaveAuthorSourceRevisionAsync("client",
            before.DraftId, new(before.DraftRevision, sources.Files, fixture.Metadata()));
        Assert.True(saved.Accepted, saved.Error?.Message);
        AuthorDraftResponse after = fixture.Registry.ReadAuthorDraft("client", before.DraftId).Response!;
        Assert.Equal(before.DraftRevision, saved.Response!.DraftRevision);
        Assert.Equal(before.CurrentSourceRevision, saved.Response.SourceRevision);
        Assert.Equal(before.LastCompilation, after.LastCompilation);
    }

    [Fact]
    public async Task 包文件编辑允许文本变更并保留非文本且失败不落盘()
    {
        await using Fixture fixture = await Fixture.CreateAsync(EffectPackageFixture.Contents(), "scale");
        AuthorPackageFilesResponse current = fixture.Registry.ReadAuthorPackageFiles("client", fixture.Draft.DraftId,
            fixture.Draft.DraftRevision, fixture.Draft.CurrentSourceRevision).Response!;
        AuthorPackageTextFileDto[] textFiles = [.. current.Files.Where(file => file.Editable)
            .Select(file => new AuthorPackageTextFileDto(file.Path, file.Text! + "\n// edited"))];
        BackendOperationResult<AuthorDraftResponse> changed = await fixture.Registry.SaveAuthorPackageFilesAsync("client",
            fixture.Draft.DraftId, new(current.DraftRevision, current.ManifestJson, textFiles, Metadata: fixture.Metadata()));
        Assert.True(changed.Accepted, changed.Error?.Message);
        AuthorPackageFilesResponse edited = fixture.Registry.ReadAuthorPackageFiles("client", fixture.Draft.DraftId,
            changed.Response!.DraftRevision, changed.Response.CurrentSourceRevision).Response!;
        Assert.NotEqual(current.PackageRevision, edited.PackageRevision);
        Assert.Equal(current.Files.Where(file => !file.Editable).Select(file => (file.Path, file.Role, file.Sha256)),
            edited.Files.Where(file => !file.Editable).Select(file => (file.Path, file.Role, file.Sha256)));

        BackendOperationResult<AuthorDraftResponse> noop = await fixture.Registry.SaveAuthorPackageFilesAsync("client",
            fixture.Draft.DraftId, new(edited.DraftRevision, edited.ManifestJson,
                [.. edited.Files.Where(file => file.Editable).Select(file => new AuthorPackageTextFileDto(file.Path, file.Text!))],
                Metadata: fixture.Metadata()));
        Assert.True(noop.Accepted, noop.Error?.Message);
        Assert.Equal(edited.DraftRevision, noop.Response!.DraftRevision);

        JsonObject broken = JsonNode.Parse(edited.ManifestJson)!.AsObject();
        broken["files"]!.AsArray().First(entry => entry!["path"]!.GetValue<string>() == "README.md")!["sha256"] = new string('0', 64);
        BackendOperationResult<AuthorDraftResponse> rejected = await fixture.Registry.SaveAuthorPackageFilesAsync("client",
            fixture.Draft.DraftId, new(edited.DraftRevision, broken.ToJsonString(),
                [.. edited.Files.Where(file => file.Editable).Select(file => new AuthorPackageTextFileDto(file.Path, file.Text!))],
                Metadata: fixture.Metadata()));
        Assert.False(rejected.Accepted);
        Assert.Equal(BackendErrorCode.InvalidRequest, rejected.Error!.Code);
        Assert.Equal(edited.DraftRevision, fixture.Registry.ReadAuthorDraft("client", fixture.Draft.DraftId).Response!.DraftRevision);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TemporaryDirectory _directory;
        internal ProjectSessionRegistry Registry { get; }
        internal AuthorDraftResponse Draft { get; }
        private Fixture(TemporaryDirectory directory, ProjectSessionRegistry registry, AuthorDraftResponse draft)
        { _directory = directory; Registry = registry; Draft = draft; }
        internal RequestMetadataDto Metadata() => new(Guid.NewGuid().ToString("N"), Registry.HostInstanceId, Registry.ServerTimeUtc.AddMinutes(1));
        internal static async Task<Fixture> CreateAsync(EffectPackageContents package, string exportId)
        {
            TemporaryDirectory directory = new();
            ResourceStore resources = new(new() { ResourceRoot = directory.FilePath("resources") });
            ProjectSessionRegistry registry = new("author-package-files", resources, NewProjectPreparationService.CreateDefault());
            registry.ConfigurePersistence(new() { StateRoot = directory.FilePath("state") });
            registry.ConfigureAuthorWorkflows(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
            try
            {
                string archive = directory.FilePath("package.mkfx");
                await EffectPackageTransfer.WriteArchiveAsync(archive, package);
                await using FileStream stream = File.OpenRead(archive);
                StoredAsset asset = await resources.UploadAsync("client", "package.mkfx", "application/octet-stream", stream);
                BackendOperationResult<AuthorDraftResponse> created = await registry.CreateAuthorDraftAsync("client",
                    new(asset.AssetId, exportId, "author", new(Guid.NewGuid().ToString("N"), registry.HostInstanceId, registry.ServerTimeUtc.AddMinutes(1))));
                Assert.True(created.Accepted, created.Error?.Message);
                return new(directory, registry, created.Response!);
            }
            catch { await registry.DisposeAsync(); directory.Dispose(); throw; }
        }
        public async ValueTask DisposeAsync() { await Registry.DisposeAsync(); _directory.Dispose(); }
    }
}
