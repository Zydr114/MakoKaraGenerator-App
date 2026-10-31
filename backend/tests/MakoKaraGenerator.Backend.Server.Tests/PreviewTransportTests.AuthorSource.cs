using System.Net;
using System.Net.Http.Json;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed partial class PreviewTransportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HTTP预览换源不得在工程与作者草稿之间切换(bool authorToProject)
    {
        Factory factory = new();
        await using Fixture fixture = await Fixture.Create(injectedFactory: new RuntimePassthroughFactory(factory));
        fixture.Projects.ConfigureAuthorWorkflows(null);
        string archive = fixture._directory.FilePath("author-source.mkfx");
        await EffectPackageTransfer.WriteArchiveAsync(archive, EffectPackageFixture.Contents());
        await using FileStream stream = File.OpenRead(archive);
        StoredAsset asset = await fixture.Resources.UploadAsync(fixture.Client.ClientId, "author-source.mkfx", "application/octet-stream", stream);
        var draftResult = await fixture.Projects.CreateAuthorDraftAsync(fixture.Client.ClientId,
            new(asset.AssetId, "scale", "author source", fixture.Metadata()));
        Assert.True(draftResult.Accepted, draftResult.Error?.Message);
        AuthorDraftResponse draft = draftResult.Response!;
        PreviewSourceDto authorSource = new("authorDraft", draft.DraftId, draft.DraftRevision,
            new(TemplatePreviewLimits.SingleLineSampleId, "token"), draft.Configuration!.SourceRevision);
        PreviewSourceDto initialSource = authorToProject ? authorSource : fixture.Source;
        PreviewSessionRequest create = new(initialSource, new(1, 1), Metadata: fixture.Metadata(),
            PreviewKind: authorToProject ? "authorDraft" : "project", Committable: !authorToProject);
        using HttpResponseMessage created = await fixture.Http.PostAsJsonAsync("/api/v1/preview-sessions", create, Json);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        PreviewSessionResponse preview = (await created.Content.ReadFromJsonAsync<PreviewSessionResponse>(Json))!;
        PreviewSourceDto replacement = authorToProject ? fixture.Source : authorSource;

        using HttpResponseMessage updated = await fixture.Http.PutAsJsonAsync($"/api/v1/preview-sessions/{preview.PreviewId}/input",
            new PreviewProjectInputRequest(preview.PreviewGeneration, preview.Source.SourceRevision, replacement,
                preview.Size, preview.AlphaMode, fixture.Metadata()), Json);

        await Error(updated, HttpStatusCode.UnprocessableEntity, BackendErrorCode.CommandNotSupported);
        PreviewSessionStateResponse state = (await fixture.Http.GetFromJsonAsync<PreviewSessionStateResponse>(
            $"/api/v1/preview-sessions/{preview.PreviewId}", Json))!;
        Assert.Equal(preview, state.Session);
    }

    private sealed class RuntimePassthroughFactory(Factory inner) : IRenderFrameSessionFactory,
        IBackendRuntimePreviewFactory, IBackendPreviewProfileCapability
    {
        public IRenderFrameSessionFactory Bind(BackendPreparedProjectRuntimeInput input) => inner;
        public ValueTask<BackendErrorResponse?> EnsureProfileAsync(string profile, CancellationToken cancellationToken = default) =>
            inner.EnsureProfileAsync(profile, cancellationToken);
        public ValueTask<IRenderFrameSession> CreateAsync(ProjectSnapshot snapshot, CancellationToken cancellationToken = default) =>
            inner.CreateAsync(snapshot, cancellationToken);
    }
}
