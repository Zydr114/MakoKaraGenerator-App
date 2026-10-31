using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed partial class PreviewTransportTests
{
    private const string TemplateRoot = "/api/v1/template-libraries";
    private static async Task<Fixture> TemplateFixture()
    { var fixture = await Fixture.Create(); fixture.Projects.ConfigureTemplateLibrary(); return fixture; }
    private static string TemplateRequest(Fixture f, string revision, string command, RequestMetadataDto? metadata = null) =>
        "{\"expectedRevision\":" + JsonSerializer.Serialize(revision) + ",\"metadata\":" + JsonSerializer.Serialize(metadata ?? f.Metadata(), Json) + ",\"command\":" + command + "}";
    private static Task<HttpResponseMessage> TemplatePost(Fixture f, string revision, string command, RequestMetadataDto? metadata = null, string library = "personal") =>
        f.Http.PostAsync(TemplateRoot + "/" + library + "/commands", new StringContent(TemplateRequest(f, revision, command, metadata), Encoding.UTF8, "application/json"));
    private static async Task<TemplateLibraryCommandResponse> TemplateSuccess(HttpResponseMessage response)
    { Assert.Equal(HttpStatusCode.OK, response.StatusCode); return (await response.Content.ReadFromJsonAsync<TemplateLibraryCommandResponse>(Json))!; }
    private static async Task<TemplateListResponse> TemplateList(Fixture f, string library = "personal") =>
        (await f.Http.GetFromJsonAsync<TemplateListResponse>(TemplateRoot + "/" + library + "/templates", Json))!;

    [Fact]
    public async Task HTTP模板库五命令真实往返历史隔离与成功回执在删除后重放()
    {
        await using var f = await TemplateFixture();
        var descriptors = (await f.Http.GetFromJsonAsync<TemplateLibrariesResponse>(TemplateRoot, Json))!;
        Assert.Equal(["builtin", "personal"], descriptors.Libraries.Select(l => l.LibraryId)); Assert.True(descriptors.Libraries[0].ReadOnly);
        var builtins = await TemplateList(f, "builtin"); Assert.Equal(2, builtins.TotalCount); Assert.Matches("^[0-9a-f]{64}$", builtins.Revision);
        var empty = await TemplateList(f); Assert.Equal("missing", empty.Revision); Assert.Empty(empty.Templates);
        string create = "{\"type\":\"create\",\"definition\":" + TemplateLibraryWireTests.Definition("  我的模板  ") + "}";
        var metadata = f.Metadata(); using var createdResponse = await TemplatePost(f, "missing", create, metadata); var created = await TemplateSuccess(createdResponse);
        Assert.Equal("missing", created.PreviousRevision); Assert.True(created.Changed); Assert.Matches("^[0-9a-f]{64}$", created.Revision);
        var list = await TemplateList(f); Assert.Equal(created.TemplateId, Assert.Single(list.Templates).TemplateId); Assert.Equal("我的模板", list.Templates[0].Name);
        string detailPath = TemplateRoot + "/personal/templates/detail?expectedRevision=" + list.Revision + "&templateId=" + Uri.EscapeDataString(created.TemplateId);
        var detail = (await f.Http.GetFromJsonAsync<TemplateDetailResponse>(detailPath, Json))!;
        Assert.Equal("staticAvailable", detail.DefinitionAvailability); Assert.NotNull(detail.Definition); Assert.Null(detail.Definition.Style.Stroke); Assert.Null(detail.Definition.Style.Typography.FontFamilyName);
        using var replacedResponse = await TemplatePost(f, list.Revision, "{\"type\":\"replace\",\"templateId\":\"" + created.TemplateId + "\",\"definition\":" + TemplateLibraryWireTests.Definition("新定义") + "}");
        var replaced = await TemplateSuccess(replacedResponse); Assert.Equal(created.TemplateId, replaced.TemplateId); Assert.True(replaced.Changed);
        using var renamedResponse = await TemplatePost(f, replaced.Revision, "{\"type\":\"rename\",\"templateId\":\"" + created.TemplateId + "\",\"name\":\"新名字\"}");
        var renamed = await TemplateSuccess(renamedResponse);
        using var noopResponse = await TemplatePost(f, renamed.Revision, "{\"type\":\"rename\",\"templateId\":\"" + created.TemplateId + "\",\"name\":\" 新名字 \"}");
        var noop = await TemplateSuccess(noopResponse); Assert.False(noop.Changed); Assert.Equal(renamed.Revision, noop.Revision);
        using var copiedResponse = await TemplatePost(f, noop.Revision, "{\"type\":\"copy\",\"sourceLibraryId\":\"builtin\",\"sourceRevision\":\"" + builtins.Revision + "\",\"templateId\":\"builtin:line-classic\",\"name\":\"内置副本\"}");
        var copied = await TemplateSuccess(copiedResponse); Assert.NotEqual(created.TemplateId, copied.TemplateId);
        using var deletedResponse = await TemplatePost(f, copied.Revision, "{\"type\":\"delete\",\"templateId\":\"" + created.TemplateId + "\"}");
        var deleted = await TemplateSuccess(deletedResponse); Assert.True(deleted.Changed); Assert.Equal(created.TemplateId, deleted.TemplateId);
        using var replay = await TemplatePost(f, "missing", create, metadata); Assert.Equal(created, await TemplateSuccess(replay));
        Assert.Equal(deleted.Revision, (await TemplateList(f)).Revision); Assert.Single((await TemplateList(f)).Templates);
        Assert.Equal("0", (await f.Projects.ReadHistoryAsync(f.Client.ClientId, f.Project.ProjectSessionId))!.ProjectRevision);
        var caps = (await f.Http.GetFromJsonAsync<BackendCapabilitiesResponse>("/api/v1/capabilities", Json))!;
        foreach (string type in new[] { "create", "replace", "copy", "rename", "delete" }) Assert.Contains("template-libraries.commands." + type, caps.Features);
    }

    [Fact]
    public async Task HTTP模板库共享profile双client同修订竞态只提交一次且读取CAS严格()
    {
        await using var f = await TemplateFixture();
        var second = f.Clients.Register(f._security.BootstrapToken, new()).Response!;
        using var other = new HttpClient { BaseAddress = f.Http.BaseAddress };
        other.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", second.AccessToken);
        other.DefaultRequestHeaders.Add("X-Mako-Client-Id", second.ClientId);
        string command = "{\"type\":\"create\",\"definition\":" + TemplateLibraryWireTests.Definition() + "}";
        var calls = await Task.WhenAll(TemplatePost(f, "missing", command), other.PostAsync(TemplateRoot + "/personal/commands", new StringContent(TemplateRequest(f, "missing", command), Encoding.UTF8, "application/json")));
        try { Assert.Single(calls, c => c.StatusCode == HttpStatusCode.OK); Assert.Single(calls, c => c.StatusCode == HttpStatusCode.Conflict); }
        finally { foreach (var call in calls) call.Dispose(); }
        var list = await TemplateList(f); Assert.Single(list.Templates);
        var shared = (await other.GetFromJsonAsync<TemplateListResponse>(TemplateRoot + "/personal/templates", Json))!; Assert.Equal(list.Revision, shared.Revision);
        using var stale = await f.Http.GetAsync(TemplateRoot + "/personal/templates?expectedRevision=missing"); await Error(stale, HttpStatusCode.Conflict, BackendErrorCode.RevisionConflict);
        using var staleDetail = await f.Http.GetAsync(TemplateRoot + "/personal/templates/detail?expectedRevision=missing&templateId=absent"); await Error(staleDetail, HttpStatusCode.Conflict, BackendErrorCode.RevisionConflict);
        using var absent = await f.Http.GetAsync(TemplateRoot + "/personal/templates/detail?expectedRevision=" + list.Revision + "&templateId=absent"); await Error(absent, HttpStatusCode.NotFound, BackendErrorCode.ResourceNotFound);
        using var beyond = await f.Http.GetAsync(TemplateRoot + "/personal/templates?offset=2"); await Error(beyond, HttpStatusCode.BadRequest, BackendErrorCode.InvalidRequest);
        using var atEnd = await f.Http.GetAsync(TemplateRoot + "/personal/templates?offset=1"); Assert.Empty((await atEnd.Content.ReadFromJsonAsync<TemplateListResponse>(Json))!.Templates);
    }

    [Theory]
    [InlineData("?x=1")]
    [InlineData("/personal/templates?ExpectedRevision=missing")]
    [InlineData("/personal/templates?offset=0&offset=0")]
    [InlineData("/personal/templates?offset=-1")]
    [InlineData("/personal/templates?limit=101")]
    [InlineData("/personal/templates?limit=1.0")]
    [InlineData("/personal/templates/detail?templateId=x")]
    [InlineData("/personal/templates/detail?expectedRevision=missing")]
    [InlineData("/personal/templates/detail?expectedRevision=missing&templateId=x&templateId=x")]
    [InlineData("/personal/templates/detail?expectedRevision=missing&templateId=x&unknown=1")]
    public async Task HTTP模板库查询闭合唯一分页边界(string suffix)
    { await using var f = await TemplateFixture(); using var response = await f.Http.GetAsync(TemplateRoot + suffix); await Error(response, HttpStatusCode.BadRequest, BackendErrorCode.InvalidRequest); }

    [Fact]
    public async Task HTTP模板库形状400先于stale且新意图413不改missing入口()
    {
        await using var f = await TemplateFixture();
        foreach (string invalid in TemplateLibraryWireTests.InvalidShapes().Select(value => (string)value[0]))
        {
            using var response = await f.Http.PostAsync(TemplateRoot + "/personal/commands", new StringContent(invalid, Encoding.UTF8, "application/json"));
            await Error(response, HttpStatusCode.BadRequest, BackendErrorCode.InvalidRequest);
        }
        using var budget = await TemplatePost(f, "missing", "{\"type\":\"create\",\"definition\":" + TemplateLibraryWireTests.Definition(new string('长',257)) + "}");
        await Error(budget, HttpStatusCode.RequestEntityTooLarge, BackendErrorCode.ResourceTooLarge);
        Assert.Equal("missing", (await TemplateList(f)).Revision);
        using var builtin = await TemplatePost(f, "missing", "{\"type\":\"delete\",\"templateId\":\"builtin:line-classic\"}", library: "builtin");
        await Error(builtin, HttpStatusCode.Forbidden, BackendErrorCode.ReadOnlySession);
        f.Http.DefaultRequestHeaders.Authorization = null;
        using var denied = await f.Http.GetAsync(TemplateRoot); Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
    }

    [Fact]
    public async Task HTTP坏个人库不遮蔽内置目录且失败不泄漏绝对路径()
    {
        await using var f = await TemplateFixture();
        var persistence = typeof(ProjectSessionRegistry).GetField("_persistence", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(f.Projects)!;
        string state = (string)persistence.GetType().GetProperty("StateRoot")!.GetValue(persistence)!;
        string index = Path.Combine(state, "libraries", "templates", "index.json"); Directory.CreateDirectory(Path.GetDirectoryName(index)!); await File.WriteAllTextAsync(index, "{broken");
        using var descriptors = await f.Http.GetAsync(TemplateRoot); Assert.Equal(HttpStatusCode.OK, descriptors.StatusCode);
        Assert.Equal(2, (await TemplateList(f, "builtin")).TotalCount);
        using var personal = await f.Http.GetAsync(TemplateRoot + "/personal/templates"); await Error(personal, HttpStatusCode.UnprocessableEntity, BackendErrorCode.CommandRejected);
        Assert.DoesNotContain(state, await personal.Content.ReadAsStringAsync());
        using var mutation = await TemplatePost(f, "missing", "{\"type\":\"create\",\"definition\":" + TemplateLibraryWireTests.Definition() + "}");
        await Error(mutation, HttpStatusCode.UnprocessableEntity, BackendErrorCode.CommandRejected); Assert.Equal("{broken", await File.ReadAllTextAsync(index));
    }
}
