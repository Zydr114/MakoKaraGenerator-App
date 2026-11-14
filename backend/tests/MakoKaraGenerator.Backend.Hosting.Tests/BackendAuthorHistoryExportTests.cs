using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class BackendAuthorHistoryExportTests
{
    [Fact]
    public async Task 源码历史按修订分页并保留编译摘要及拒绝未知游标()
    {
        await using Fixture fixture = await Fixture.CreateAsync(configureLibrary: false);
        AuthorDraftResponse draft = fixture.Draft;
        AuthorCompilationResponse baseCompilation = Accepted(await fixture.Registry.CompileAuthorDraftAsync("client", draft.DraftId,
            new(draft.DraftRevision, fixture.Metadata())));

        AuthorDraftSourcesResponse source = Accepted(fixture.Registry.ReadAuthorSources("client", draft.DraftId,
            draft.DraftRevision, draft.CurrentSourceRevision));
        AuthorSourceRevisionResponse saved = Accepted(await fixture.Registry.SaveAuthorSourceRevisionAsync("client", draft.DraftId,
            new(draft.DraftRevision, [new("src/PackEffect.cs", source.Files[0].Text + "\n// history-a")], fixture.Metadata())));
        AuthorCompilationResponse savedCompilation = Accepted(await fixture.Registry.CompileAuthorDraftAsync("client", draft.DraftId,
            new(saved.DraftRevision, fixture.Metadata())));

        AuthorSourceRevisionsResponse first = Accepted(await fixture.Registry.ReadAuthorSourceRevisionsAsync("client", draft.DraftId,
            saved.DraftRevision, limit: 1));
        Assert.Single(first.Revisions);
        Assert.NotNull(first.NextAfterSourceRevision);
        Assert.NotNull(first.Revisions[0].Compilation);
        string firstRevision = first.Revisions[0].SourceRevision;
        Assert.True(firstRevision == baseCompilation.SourceRevision || firstRevision == savedCompilation.SourceRevision);

        AuthorSourceRevisionsResponse second = Accepted(await fixture.Registry.ReadAuthorSourceRevisionsAsync("client", draft.DraftId,
            saved.DraftRevision, first.NextAfterSourceRevision, 100));
        AuthorSourceRevisionDto[] all = [.. first.Revisions, .. second.Revisions];
        Assert.Equal(all.Select(item => item.SourceRevision).Order(StringComparer.Ordinal), all.Select(item => item.SourceRevision));
        Assert.Contains(all, item => item.SourceRevision == baseCompilation.SourceRevision);
        Assert.Contains(all, item => item.SourceRevision == savedCompilation.SourceRevision);
        Assert.Contains(all, item => item.Compilation?.ArtifactHash == baseCompilation.ArtifactHash);
        Assert.Equal(BackendErrorCode.ResourceNotFound,
            (await fixture.Registry.ReadAuthorSourceRevisionsAsync("client", draft.DraftId, saved.DraftRevision,
                "sha256:" + new string('f', 64))).Error!.Code);
        Assert.Equal(BackendErrorCode.RevisionConflict,
            (await fixture.Registry.ReadAuthorSourceRevisionsAsync("client", draft.DraftId, draft.DraftRevision,
                limit: 100)).Error!.Code);
    }

    [Fact]
    public async Task 草稿旧修订和组件配置均可导出并保持完整包校验()
    {
        await using Fixture fixture = await Fixture.CreateAsync(configureLibrary: true);
        AuthorDraftResponse draft = fixture.Draft;
        (string PackageId, string Revision) currentArchive = await fixture.ReadArchiveAsync(Accepted(await fixture.Registry.OpenAuthorPackageAsync("client",
            draft.DraftId, draft.DraftRevision, draft.CurrentSourceRevision)));
        Assert.Equal(draft.PackageId, currentArchive.PackageId);
        Assert.Equal(draft.CurrentSourceRevision, currentArchive.Revision);

        AuthorDraftSourcesResponse source = Accepted(fixture.Registry.ReadAuthorSources("client", draft.DraftId,
            draft.DraftRevision, draft.CurrentSourceRevision));
        AuthorSourceRevisionResponse saved = Accepted(await fixture.Registry.SaveAuthorSourceRevisionAsync("client", draft.DraftId,
            new(draft.DraftRevision, [new("src/PackEffect.cs", source.Files[0].Text + "\n// export-a")], fixture.Metadata())));
        AuthorDraftResponse changed = Accepted(fixture.Registry.ReadAuthorDraft("client", draft.DraftId));
        Assert.Equal(BackendErrorCode.RevisionConflict,
            (await fixture.Registry.OpenAuthorPackageAsync("client", draft.DraftId, draft.DraftRevision,
                draft.CurrentSourceRevision)).Error!.Code);
        (string PackageId, string Revision) oldArchive = await fixture.ReadArchiveAsync(Accepted(await fixture.Registry.OpenAuthorPackageAsync("client",
            draft.DraftId, changed.DraftRevision, draft.CurrentSourceRevision)));
        Assert.Equal(draft.CurrentSourceRevision, oldArchive.Revision);
        Assert.NotEqual(saved.SourceRevision, oldArchive.Revision);

        ScriptConfigurationCommandResponse created = Accepted(await fixture.Registry.ExecuteScriptConfigurationCommandAsync("client",
            new("missing", fixture.Metadata(), new ScriptConfigurationCreateCommandDto("create", new(
                "saved", "scale", "{\"peak\":2.25}", TemplateEffectTargetDto.Application,
                TemplateScriptReadScopeDto.BoundSources, null, null, TemplateEffectOverlayLayerDto.Package,
                new(DraftId: changed.DraftId, ExpectedDraftRevision: changed.DraftRevision, SourceRevision: changed.CurrentSourceRevision))))));
        (string PackageId, string Revision) configurationArchive = await fixture.ReadArchiveAsync(Accepted(await fixture.Registry.OpenScriptConfigurationPackageAsync(
            created.ConfigurationId, created.Revision)));
        Assert.Equal(changed.PackageId, configurationArchive.PackageId);
        Assert.Equal(changed.CurrentSourceRevision, configurationArchive.Revision);
        Assert.Equal(BackendErrorCode.RevisionConflict,
            (await fixture.Registry.OpenScriptConfigurationPackageAsync(created.ConfigurationId, "missing")).Error!.Code);
    }

    [Fact]
    public async Task 下载并发槽为二且关闭租约后恢复并拒绝错误所有者()
    {
        await using Fixture fixture = await Fixture.CreateAsync(configureLibrary: false);
        AuthorDraftResponse draft = fixture.Draft;
        EffectPackageDownloadLease first = Accepted(await fixture.Registry.OpenAuthorPackageAsync("client", draft.DraftId,
            draft.DraftRevision, draft.CurrentSourceRevision));
        EffectPackageDownloadLease second = Accepted(await fixture.Registry.OpenAuthorPackageAsync("client", draft.DraftId,
            draft.DraftRevision, draft.CurrentSourceRevision));
        try
        {
            BackendOperationResult<EffectPackageDownloadLease> third = await fixture.Registry.OpenAuthorPackageAsync("client",
                draft.DraftId, draft.DraftRevision, draft.CurrentSourceRevision);
            Assert.False(third.Accepted);
            Assert.Equal(BackendErrorCode.RequestQuotaExceeded, third.Error!.Code);
        }
        finally { first.Dispose(); second.Dispose(); }

        Assert.Equal(BackendErrorCode.ResourceUnauthorized,
            (await fixture.Registry.OpenAuthorPackageAsync("other", draft.DraftId, draft.DraftRevision,
                draft.CurrentSourceRevision)).Error!.Code);

        using EffectPackageDownloadLease recovered = Accepted(await fixture.Registry.OpenAuthorPackageAsync("client", draft.DraftId,
            draft.DraftRevision, draft.CurrentSourceRevision));
        Assert.True(recovered.Stream.Length > 0);
    }

    private static T Accepted<T>(BackendOperationResult<T> result)
    {
        Assert.True(result.Accepted, result.Error?.ToString());
        return result.Response!;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TemporaryDirectory _directory;
        internal ProjectSessionRegistry Registry { get; }
        internal AuthorDraftResponse Draft { get; }

        private Fixture(TemporaryDirectory directory, ProjectSessionRegistry registry, AuthorDraftResponse draft)
        { _directory = directory; Registry = registry; Draft = draft; }

        internal RequestMetadataDto Metadata() => new(Guid.NewGuid().ToString("N"), Registry.HostInstanceId,
            Registry.ServerTimeUtc.AddMinutes(1));

        internal async Task<(string PackageId, string Revision)> ReadArchiveAsync(EffectPackageDownloadLease lease)
        {
            using (lease)
            {
                string path = _directory.FilePath("download-" + Guid.NewGuid().ToString("N") + ".mkfx");
                await using (FileStream output = File.Create(path)) await lease.Stream.CopyToAsync(output);
                EffectPackageContents package = await EffectPackageTransfer.ReadArchiveAsync(path,
                    EffectPackageProfiles.V2);
                return (package.PackageId, package.Revision);
            }
        }

        internal static async Task<Fixture> CreateAsync(bool configureLibrary)
        {
            TemporaryDirectory directory = new();
            ResourceStore resources = new(new() { ResourceRoot = directory.FilePath("resources") });
            ProjectSessionRegistry registry = new("author-history-export", resources, NewProjectPreparationService.CreateDefault());
            registry.ConfigurePersistence(new() { StateRoot = directory.FilePath("state") });
            registry.ConfigureAuthorWorkflows(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
            if (configureLibrary) registry.ConfigureScriptConfigurations();
            try
            {
                string archive = directory.FilePath("package.mkfx");
                await EffectPackageTransfer.WriteArchiveAsync(archive, EffectPackageFixture.Contents());
                await using FileStream stream = File.OpenRead(archive);
                StoredAsset asset = await resources.UploadAsync("client", "package.mkfx", "application/octet-stream", stream);
                BackendOperationResult<AuthorDraftResponse> created = await registry.CreateAuthorDraftAsync("client",
                    new(asset.AssetId, "scale", "author", new(Guid.NewGuid().ToString("N"), registry.HostInstanceId,
                        registry.ServerTimeUtc.AddMinutes(1))));
                Assert.True(created.Accepted, created.Error?.ToString());
                return new(directory, registry, created.Response!);
            }
            catch
            {
                await registry.DisposeAsync();
                directory.Dispose();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Registry.DisposeAsync();
            _directory.Dispose();
        }
    }
}
