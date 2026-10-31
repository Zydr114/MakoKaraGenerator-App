using System.Net;
using System.Net.Http.Json;
using System.Text;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed partial class PreviewTransportTests
{
    [Fact]
    public async Task HTTP效果包只读检查授权资源固定引用及严格形状()
    {
        await using var fixture = await Fixture.Create();
        var package = EffectPackageFixture.Contents();
        string archive = fixture._directory.FilePath("inspect.mkfx"); await EffectPackageTransfer.WriteArchiveAsync(archive, package);
        await using var stream = File.OpenRead(archive);
        var asset = await fixture.Resources.UploadAsync(fixture.Client.ClientId, "inspect.mkfx", "application/octet-stream", stream);
        using var response = await fixture.Http.PostAsJsonAsync("/api/v1/effect-packages/inspect", new EffectPackageInspectRequest(asset.AssetId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var inspected = (await response.Content.ReadFromJsonAsync<EffectPackageInspectResponse>(Json))!;
        Assert.True(inspected.StructureValid); Assert.Equal(package.Revision, inspected.PackageRevision);
        Assert.Equal("1.5", Assert.Single(inspected.Parameters).DefaultValueJson); Assert.Equal("tokens", Assert.Single(inspected.BindingKinds));
        Assert.DoesNotContain(fixture._directory.Path, await response.Content.ReadAsStringAsync());
        using var fixedReference = await fixture.Http.PostAsJsonAsync("/api/v1/effect-packages/inspect", new EffectPackageInspectRequest(Reference: inspected.Reference));
        Assert.Equal(HttpStatusCode.OK, fixedReference.StatusCode);
        foreach (string invalid in new[] { "{}", "{\"packageAssetId\":\"" + asset.AssetId + "\",\"path\":\"/tmp/server\"}", "{\"reference\":{\"kind\":\"library\"}}" })
        {
            using var rejected = await fixture.Http.PostAsync("/api/v1/effect-packages/inspect", new StringContent(invalid, Encoding.UTF8, "application/json"));
            await Error(rejected, HttpStatusCode.BadRequest, BackendErrorCode.InvalidRequest);
        }
        using var stale = await fixture.Http.PostAsJsonAsync("/api/v1/effect-packages/inspect", new EffectPackageInspectRequest(asset.AssetId, "changed"));
        await Error(stale, HttpStatusCode.Conflict, BackendErrorCode.RevisionConflict);
        fixture.Http.DefaultRequestHeaders.Authorization = null;
        using var unauthorized = await fixture.Http.PostAsJsonAsync("/api/v1/effect-packages/inspect", new EffectPackageInspectRequest(asset.AssetId));
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
    }
}
