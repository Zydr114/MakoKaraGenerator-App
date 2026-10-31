using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed partial class PreviewTransportTests
{
    [Fact]
    public async Task 包资源asset接口复用幂等资产并提供manifest与文件内容()
    {
        await using Fixture f = await Fixture.Create();
        ProjectSession source = await f.OpenNoAudioProject(PackageProject(), "package-assets");
        ProjectResourcesResponse resources = (await f.Http.GetFromJsonAsync<ProjectResourcesResponse>(
            $"/api/v1/project-sessions/{source.ProjectSessionId}/resources?expectedRevision=0", Json))!;
        ProjectResourceDto manifest = Assert.Single(resources.Resources, item => item.Kind == "effectPackageManifest");
        ProjectResourceDto file = Assert.Single(resources.Resources, item => item.Kind == "effectPackageFile"
            && item.PackagePath == "src/PackEffect.cs");

        RequestMetadataDto metadata = f.Metadata();
        ProjectPackageResourceAssetRequest manifestRequest = new("0", manifest.ResourceId, metadata);
        string route = $"/api/v1/project-sessions/{source.ProjectSessionId}/resources/package-asset";
        using HttpResponseMessage first = await f.Http.PostAsJsonAsync(route, manifestRequest, Json);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        ProjectPackageResourceAssetResponse manifestAsset = (await first.Content.ReadFromJsonAsync<ProjectPackageResourceAssetResponse>(Json))!;
        Assert.Equal("manifest", manifestAsset.PackageRole);
        Assert.Equal("application/json", manifestAsset.Asset.ContentType);
        using (HttpResponseMessage content = await f.Http.GetAsync(manifestAsset.ContentUrl))
        {
            Assert.Equal(HttpStatusCode.OK, content.StatusCode);
            Assert.Equal(PackageProject().Effects.Packages[0].Manifest.ToArray(), await content.Content.ReadAsByteArrayAsync());
        }

        using HttpResponseMessage duplicate = await f.Http.PostAsJsonAsync(route, manifestRequest, Json);
        Assert.Equal(HttpStatusCode.Created, duplicate.StatusCode);
        ProjectPackageResourceAssetResponse duplicateAsset = (await duplicate.Content.ReadFromJsonAsync<ProjectPackageResourceAssetResponse>(Json))!;
        Assert.Equal(manifestAsset.Asset.AssetId, duplicateAsset.Asset.AssetId);

        ProjectPackageResourceAssetRequest fileRequest = new("0", file.ResourceId, f.Metadata());
        using HttpResponseMessage fileResponse = await f.Http.PostAsJsonAsync(route, fileRequest, Json);
        Assert.Equal(HttpStatusCode.Created, fileResponse.StatusCode);
        ProjectPackageResourceAssetResponse fileAsset = (await fileResponse.Content.ReadFromJsonAsync<ProjectPackageResourceAssetResponse>(Json))!;
        Assert.Equal(file.PackagePath, fileAsset.PackagePath);
        Assert.Equal(file.PackageRole, fileAsset.PackageRole);
        using HttpResponseMessage fileContent = await f.Http.GetAsync(fileAsset.ContentUrl);
        Assert.Equal(HttpStatusCode.OK, fileContent.StatusCode);
        Assert.Equal(PackageProject().Effects.Packages[0].Files.Single(item => item.Path == file.PackagePath).Content.ToArray(),
            await fileContent.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task 包资源asset接口遵守修订ACL与工程关闭后的TTL租约()
    {
        await using Fixture f = await Fixture.Create();
        Project project = PackageProject();
        ProjectSession source = await f.OpenNoAudioProject(project, "package-assets-acl");
        ProjectResourcesResponse resources = (await f.Http.GetFromJsonAsync<ProjectResourcesResponse>(
            $"/api/v1/project-sessions/{source.ProjectSessionId}/resources?expectedRevision=0", Json))!;
        string resourceId = Assert.Single(resources.Resources, item => item.Kind == "effectPackageFile"
            && item.PackagePath == "src/PackEffect.cs").ResourceId;
        string route = $"/api/v1/project-sessions/{source.ProjectSessionId}/resources/package-asset";
        ProjectPackageResourceAssetRequest request = new("0", resourceId, f.Metadata());

        using HttpResponseMessage stale = await f.Http.PostAsJsonAsync(route, request with { ExpectedRevision = "1", Metadata = f.Metadata() }, Json);
        await Error(stale, HttpStatusCode.Conflict, BackendErrorCode.RevisionConflict);
        using HttpResponseMessage unknown = await f.Http.PostAsJsonAsync(route,
            request with { ResourceId = "packageFile:missing:path.txt", Metadata = f.Metadata() }, Json);
        await Error(unknown, HttpStatusCode.NotFound, BackendErrorCode.ResourceNotFound);

        ClientRegistrationResponse other = f.Clients.Register(f._security.BootstrapToken, new()).Response!;
        using HttpRequestMessage foreign = new(HttpMethod.Post, route)
        {
            Content = new StringContent(JsonSerializer.Serialize(request, Json), Encoding.UTF8, "application/json"),
        };
        foreign.Headers.Authorization = new AuthenticationHeaderValue("Bearer", other.AccessToken);
        foreign.Headers.Add("X-Mako-Client-Id", other.ClientId);
        using HttpResponseMessage denied = await f.Http.SendAsync(foreign);
        await Error(denied, HttpStatusCode.NotFound, BackendErrorCode.SessionNotFound);

        using HttpResponseMessage accepted = await f.Http.PostAsJsonAsync(route, request with { Metadata = f.Metadata() }, Json);
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        ProjectPackageResourceAssetResponse asset = (await accepted.Content.ReadFromJsonAsync<ProjectPackageResourceAssetResponse>(Json))!;
        BackendOperationResult<JobResponse> close = await f.Projects.StartCloseAsync(f.Client.ClientId, source.ProjectSessionId,
            new("0", "discard", Metadata: f.Metadata()));
        Assert.True(close.Accepted);
        await WaitUntil(() => f.Projects.ReadJob(f.Client.ClientId, close.Response!.JobId).Response!.State == "succeeded");
        f.Clock.Advance(2);
        Assert.True(f.Resources.CollectExpired() >= 1);
        using HttpResponseMessage expired = await f.Http.GetAsync(asset.ContentUrl);
        await Error(expired, HttpStatusCode.Gone, BackendErrorCode.ResourceExpired);
    }
}
