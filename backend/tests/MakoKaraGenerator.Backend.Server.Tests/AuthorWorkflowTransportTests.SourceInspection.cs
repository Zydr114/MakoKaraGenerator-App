using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed partial class AuthorWorkflowTransportTests
{
    private static readonly JsonSerializerOptions InspectionTestJson = new(JsonSerializerDefaults.Web);
    private static readonly string[] InspectionResponseFields = ["bindingKinds", "diagnostics", "draftId", "draftRevision", "exports", "files", "inputs", "name", "packageId", "packageRevision", "packageVersion", "parameters", "protocolVersion", "sdkVersion", "shaderProfile", "sourceRevision", "structureValid", "version"];

    private static readonly string[] InspectionFileFields = ["path", "role", "sha256", "size"];

    [Fact]
    public async Task HTTP固定源码检查严格查询无body认证CAS及窄响应()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        AuthorDraftResponse draft = await CreateInspectionDraftAsync(fixture);
        string route = $"/api/v1/drafts/{draft.DraftId}/source-inspection";
        string exact = $"expectedDraftRevision={Uri.EscapeDataString(draft.DraftRevision)}&sourceRevision={Uri.EscapeDataString(draft.CurrentSourceRevision)}";
        using (HttpResponseMessage response = await fixture.Http.GetAsync(route + "?" + exact))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(InspectionResponseFields,
                json.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
            Assert.Equal(draft.DraftRevision, json.RootElement.GetProperty("draftRevision").GetString());
            Assert.Equal(draft.CurrentSourceRevision, json.RootElement.GetProperty("sourceRevision").GetString());
            JsonElement file = json.RootElement.GetProperty("files")[0];
            Assert.Equal(InspectionFileFields, file.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        }
        string[] invalidQueries = ["", "?sourceRevision=" + Uri.EscapeDataString(draft.CurrentSourceRevision),
            "?expectedDraftRevision=" + Uri.EscapeDataString(draft.DraftRevision), "?" + exact + "&sourceRevision=" + Uri.EscapeDataString(draft.CurrentSourceRevision),
            "?" + exact + "&unexpected=true", "?expectedDraftRevision=%20&sourceRevision=" + Uri.EscapeDataString(draft.CurrentSourceRevision)];
        foreach (string query in invalidQueries)
        {
            using HttpResponseMessage response = await fixture.Http.GetAsync(route + query);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
        }
        using (HttpRequestMessage body = new(HttpMethod.Get, route + "?" + exact) { Content = JsonContent.Create(new { }) })
        using (HttpResponseMessage response = await fixture.Http.SendAsync(body))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("body", (await response.Content.ReadFromJsonAsync<BackendErrorResponse>())!.Field);
        }
        using (HttpClient anonymous = new() { BaseAddress = fixture.Http.BaseAddress })
        using (HttpResponseMessage response = await anonymous.GetAsync(route + "?" + exact))
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using (HttpResponseMessage response = await fixture.Http.GetAsync(route + "?expectedDraftRevision=" + Uri.EscapeDataString("sha256:" + new string('d', 64))
            + "&sourceRevision=" + Uri.EscapeDataString(draft.CurrentSourceRevision)))
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using (HttpResponseMessage response = await fixture.Http.GetAsync(route + "?expectedDraftRevision=" + Uri.EscapeDataString(draft.DraftRevision)
            + "&sourceRevision=" + Uri.EscapeDataString("sha256:" + new string('e', 64))))
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task HTTP采用兼容旧JSON和null配置完整配置同次发布且拒绝新增身份字段()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        AuthorDraftResponse original = await CreateInspectionDraftAsync(fixture);
        string route = $"/api/v1/drafts/{original.DraftId}/adopt-source-revision";
        using (HttpResponseMessage legacy = await fixture.Http.PostAsJsonAsync(route, new
        {
            expectedDraftRevision = original.DraftRevision, sourceRevision = original.CurrentSourceRevision, metadata = fixture.Metadata(),
        })) Assert.Equal(HttpStatusCode.OK, legacy.StatusCode);
        AuthorDraftResponse current = (await fixture.Projects.ReadAuthorDraftAsync(fixture.Client.ClientId, original.DraftId)).Response!;
        using (HttpResponseMessage explicitNull = await fixture.Http.PostAsJsonAsync(route,
            new AuthorAdoptSourceRevisionRequest(current.DraftRevision, current.CurrentSourceRevision, fixture.Metadata(), Configuration: null)))
            Assert.Equal(HttpStatusCode.OK, explicitNull.StatusCode);
        AuthorPackageConfigurationInputDto configuration = new("HTTP complete config", "scale", "{\"peak\":2}", TemplateEffectTargetDto.TokenFamily,
            TemplateScriptReadScopeDto.Project, null, "18446744073709551615", TemplateEffectOverlayLayerDto.Package);
        AuthorAdoptSourceRevisionRequest request = new(current.DraftRevision, current.CurrentSourceRevision, fixture.Metadata(), Configuration: configuration);
        using (HttpResponseMessage response = await fixture.Http.PostAsJsonAsync(route, request))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            AuthorAdoptSourceRevisionResponse adopted = (await response.Content.ReadFromJsonAsync<AuthorAdoptSourceRevisionResponse>())!;
            AuthorDraftResponse after = (await fixture.Projects.ReadAuthorDraftAsync(fixture.Client.ClientId, original.DraftId)).Response!;
            Assert.Equal(adopted.DraftRevision, after.DraftRevision);
            Assert.Equal(configuration.Name, after.Name);
            Assert.Equal(configuration.Name, after.Configuration!.Name);
            Assert.Equal(configuration.ExportId, after.ExportId);
            Assert.Equal(configuration.ParametersJson, after.Configuration.ParametersJson);
            Assert.Equal(configuration.FixedSeed, after.Configuration.FixedSeed);
            Assert.Equal(current.CurrentSourceRevision, after.Configuration.SourceRevision);
        }
        AuthorDraftResponse persisted = (await fixture.Projects.ReadAuthorDraftAsync(fixture.Client.ClientId, original.DraftId)).Response!;
        foreach (string forbidden in new[] { "packageId", "sourceRevision", "kind", "bindingKinds" })
        {
            var invalid = request with { ExpectedDraftRevision = persisted.DraftRevision, Metadata = fixture.Metadata() };
            JsonObject json = JsonNode.Parse(JsonSerializer.Serialize(invalid, InspectionTestJson))!.AsObject();
            json["configuration"]![forbidden] = "must-not-be-accepted";
            using HttpResponseMessage response = await fixture.Http.PostAsync(route,
                new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(JsonSerializer.Serialize(persisted), JsonSerializer.Serialize((await fixture.Projects.ReadAuthorDraftAsync(fixture.Client.ClientId, original.DraftId)).Response));
        }
        foreach (string required in new[] { "name", "exportId", "parametersJson", "relativeTarget", "readScope", "branchInput", "fixedSeed", "overlayLayer" })
        {
            var missing = request with { ExpectedDraftRevision = persisted.DraftRevision, Metadata = fixture.Metadata() };
            JsonObject json = JsonNode.Parse(JsonSerializer.Serialize(missing, InspectionTestJson))!.AsObject();
            json["configuration"]!.AsObject().Remove(required);
            using HttpResponseMessage response = await fixture.Http.PostAsync(route,
                new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("configuration." + required, (await response.Content.ReadFromJsonAsync<BackendErrorResponse>())!.Field);
            Assert.Equal(JsonSerializer.Serialize(persisted), JsonSerializer.Serialize((await fixture.Projects.ReadAuthorDraftAsync(fixture.Client.ClientId, original.DraftId)).Response));
        }
        using (HttpResponseMessage duplicate = await fixture.Http.PostAsync(route, new StringContent(
            "{\"configuration\":{\"name\":\"a\",\"name\":\"b\"}}", Encoding.UTF8, "application/json")))
        {
            Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
            Assert.Equal("configuration.name", (await duplicate.Content.ReadFromJsonAsync<BackendErrorResponse>())!.Field);
        }
        using (HttpResponseMessage replay = await fixture.Http.PostAsJsonAsync(route, request)) Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using (HttpResponseMessage reused = await fixture.Http.PostAsJsonAsync(route, request with { Configuration = configuration with { Name = "changed" } }))
            Assert.Equal(HttpStatusCode.Conflict, reused.StatusCode);
    }

    private static async Task<AuthorDraftResponse> CreateInspectionDraftAsync(Fixture fixture)
    {
        EffectPackageContents package = EffectPackageFixture.Contents();
        string archive = fixture.Directory.FilePath("inspection-package.mkfx");
        await EffectPackageTransfer.WriteArchiveAsync(archive, package);
        await using FileStream stream = File.OpenRead(archive);
        StoredAsset asset = await fixture.Resources.UploadAsync(fixture.Client.ClientId, "package.mkfx", "application/octet-stream", stream);
        using HttpResponseMessage response = await fixture.Http.PostAsJsonAsync("/api/v1/drafts",
            new AuthorDraftCreateRequest(asset.AssetId, "scale", "inspection transport", fixture.Metadata()));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        AuthorDraftResponse draft = (await response.Content.ReadFromJsonAsync<AuthorDraftResponse>())!;
        using HttpResponseMessage compile = await fixture.Http.PostAsJsonAsync($"/api/v1/drafts/{draft.DraftId}/compilation-jobs",
            new AuthorCompilationRequest(draft.DraftRevision, fixture.Metadata()));
        Assert.Equal(HttpStatusCode.OK, compile.StatusCode);
        AuthorCompilationResponse compiled = (await compile.Content.ReadFromJsonAsync<AuthorCompilationResponse>())!;
        Assert.True(compiled.Succeeded, string.Join('\n', compiled.Diagnostics.Select(d => d.Message)));
        return (await fixture.Projects.ReadAuthorDraftAsync(fixture.Client.ClientId, draft.DraftId)).Response!;
    }
}
