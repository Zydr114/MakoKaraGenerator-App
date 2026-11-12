using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed partial class PreviewTransportTests
{
    [Fact]
    public async Task HTTP完整字体包归档采用原包内容预算并保持导入导出全部字体字节()
    {
        await using var f = await TemplateFixture();
        var budget = EffectPackageContentBudget.Default;
        Assert.Equal(TemplateTransferLimits.MaxExpandedBytes, budget.MaximumBytes);
        Assert.Equal(TemplateTransferLimits.MaxFileBytes, budget.MaximumFileBytes);
        Assert.Equal(TemplateTransferLimits.MaxEntries, budget.MaximumFiles);
        var package = PackageFontFixture.Contents();
        byte[] font = package.Files.Single(file => file.Role == "font").Content.ToArray();
        Assert.True(font.Length > 8 * 1024 * 1024);
        var template = StyleTemplate.Create("font", "完整字体", StyleTemplateKind.Line,
            LyricStyle.Default with { FontFamilyName = PackageFontFixture.Family }, [LyricStyleProperty.FontFamilyName],
            [new("scale", "缩放", TemplateEffectTarget.TokenFamily, package.PackageId, package.Revision, "scale")], [package]);
        var preflight = await TransferPreflight(f, await TransferBytes([template]));
        using var importedResponse = await f.Http.PostAsJsonAsync(TemplateRoot + "/personal/imports",
            new TemplateImportRequest("missing", preflight.PreflightId, f.Metadata()), Json);
        Assert.Equal(HttpStatusCode.OK, importedResponse.StatusCode);
        var imported = (await importedResponse.Content.ReadFromJsonAsync<TemplateImportResponse>(Json))!;
        var export = await TransferExport(f, imported.Revision, [Assert.Single(imported.ImportedTemplates).TemplateId]);
        using TemporaryDirectory temporary = new();
        string archive = temporary.FilePath("font.mktpl");
        await File.WriteAllBytesAsync(archive, await f.Http.GetByteArrayAsync(export.ContentUrl));
        var restored = Assert.Single(await StyleTemplateTransfer.ReadArchiveAsync(archive));
        Assert.Equal(package.Revision, Assert.Single(restored.Packages).Revision);
        Assert.Equal(font, restored.Packages[0].Files.Single(file => file.Role == "font").Content.ToArray());
    }

    private static async Task<TemplateImportPreflightResponse> TransferPreflight(Fixture f, byte[] bytes)
    {
        var asset = await f.Resources.UploadAsync(f.Client.ClientId, "templates.mktpl", "application/octet-stream", new MemoryStream(bytes));
        using var response = await f.Http.PostAsJsonAsync(TemplateRoot + "/personal/imports/preflight", new TemplateImportPreflightRequest(asset.AssetId, f.Metadata()), Json);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TemplateImportPreflightResponse>(Json))!;
    }
    private static async Task<byte[]> TransferBytes(IEnumerable<StyleTemplate> templates)
    {
        using TemporaryDirectory temporary = new(); string path = temporary.FilePath("source.mktpl");
        await StyleTemplateTransfer.WriteArchiveAsync(path, templates);
        return await File.ReadAllBytesAsync(path);
    }
    private static async Task<TemplateArchiveExportResponse> TransferExport(Fixture f, string revision, string[] ids)
    {
        using var response = await f.Http.PostAsJsonAsync(TemplateRoot + "/personal/exports", new TemplateArchiveExportRequest(revision, ids, f.Metadata()), Json);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TemplateArchiveExportResponse>(Json))!;
    }

    [Fact]
    public async Task HTTP归档完整高级闭包与静态透明null保真新身份一次CAS回执与原字节oracle()
    {
        await using var f = await TemplateFixture();
        var package = MixedEffectPackageFixture.Contents();
        var material = new MaterialSpec.PackageMaterial(package.PackageId, package.Revision, "ink",
            EffectParameterOverrides.Parse("{\"amount\":0.12345678901234567}"));
        var advanced = StyleTemplate.Create("advanced", "完整高级", StyleTemplateKind.Line,
            LyricStyle.Default with { UnsungFill = new() { Material = material } }, [LyricStyleProperty.UnsungFill], packages: [package]);
        var recipePackage = EffectPackageFixture.Contents();
        var recipe = StyleTemplate.Create("recipe", "完整配方", StyleTemplateKind.Token, LyricStyle.Default, [],
            [new("r", "缩放", TemplateEffectTarget.TokenFamily, recipePackage.PackageId, recipePackage.Revision, "scale")], [recipePackage]);
        var plain = StyleTemplate.Create("static", "透明静态", StyleTemplateKind.Line,
            LyricStyle.Default with { Opacity = .3f, Stroke = null, Shadow = null,
                Typography = LyricStyle.Default.Typography with { FontFamilyName = null } }, [LyricStyleProperty.Opacity, LyricStyleProperty.UnsungStrokes]);
        StyleTemplate[] originals = [advanced, recipe, plain];
        var preflight = await TransferPreflight(f, await TransferBytes(originals));
        Assert.Equal(["advancedRuntimeAvailable", "advancedRuntimeAvailable", "staticAvailable"], preflight.Templates.Select(item => item.DefinitionAvailability));
        var request = new TemplateImportRequest("missing", preflight.PreflightId, f.Metadata());
        using var importedResponse = await f.Http.PostAsJsonAsync(TemplateRoot + "/personal/imports", request, Json);
        Assert.Equal(HttpStatusCode.OK, importedResponse.StatusCode);
        var imported = (await importedResponse.Content.ReadFromJsonAsync<TemplateImportResponse>(Json))!;
        Assert.True(imported.Changed); Assert.Equal(3, imported.ImportedTemplates.Length);
        Assert.Equal(originals.Select(item => item.Id), imported.ImportedTemplates.Select(item => item.SourceTemplateId));
        Assert.All(imported.ImportedTemplates, item => Assert.NotEqual(item.SourceTemplateId, item.TemplateId));
        using var replay = await f.Http.PostAsJsonAsync(TemplateRoot + "/personal/imports", request, Json);
        Assert.Equal(await importedResponse.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        using var consumed = await f.Http.PostAsJsonAsync(TemplateRoot + "/personal/imports", request with { ExpectedRevision = imported.Revision, Metadata = f.Metadata() }, Json);
        await Error(consumed, HttpStatusCode.NotFound, BackendErrorCode.ResourceNotFound);
        var list = await TemplateList(f); Assert.Equal(3, list.TotalCount);
        var exported = await TransferExport(f, imported.Revision, imported.ImportedTemplates.Select(item => item.TemplateId).ToArray());
        using var downloaded = await f.Http.GetAsync(exported.ContentUrl);
        byte[] bytes = await downloaded.Content.ReadAsByteArrayAsync();
        Assert.Equal(exported.SizeBytes, bytes.LongLength); Assert.Equal(exported.Sha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        using TemporaryDirectory temporary = new(); string path = temporary.FilePath("download.mktpl"); await File.WriteAllBytesAsync(path, bytes);
        var restored = await StyleTemplateTransfer.ReadArchiveAsync(path);
        Assert.Equal(StyleTemplateLibrary.Encode(originals), StyleTemplateLibrary.Encode(restored.Select((item, index) => item with { Id = originals[index].Id })));
        string oracle = temporary.FilePath("oracle.mktpl"); await StyleTemplateTransfer.WriteArchiveAsync(oracle, restored);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(oracle));
        Assert.Equal(package.Revision, Assert.Single(restored[0].Packages).Revision); Assert.Equal(material, restored[0].Style.UnsungFill.Material);
        Assert.Null(restored[2].Style.Stroke); Assert.Null(restored[2].Style.Shadow); Assert.Null(restored[2].Style.Typography.FontFamilyName);
        Assert.Equal("0", (await f.Projects.ReadSummaryAsync(f.Client.ClientId, f.Project.ProjectSessionId))!.ProjectRevision);
        // Complete exported closure imports into a genuinely empty, independent profile.
        await using var fresh = await TemplateFixture(); var next = await TransferPreflight(fresh, bytes);
        using var intoFresh = await fresh.Http.PostAsJsonAsync(TemplateRoot + "/personal/imports", new TemplateImportRequest("missing", next.PreflightId, fresh.Metadata()), Json);
        Assert.Equal(HttpStatusCode.OK, intoFresh.StatusCode); Assert.Equal(3, (await TemplateList(fresh)).TotalCount);
    }

    [Fact]
    public async Task HTTP归档固定下载ETagRangeHEAD跨ownerCAS与builtin只读()
    {
        await using var f = await TemplateFixture();
        var original = StyleTemplate.Create("t", "归档", StyleTemplateKind.Line, LyricStyle.Default, [LyricStyleProperty.FontSizePx]);
        byte[] source = await TransferBytes([original]); var preflight = await TransferPreflight(f, source);
        var metadata = f.Metadata(); var stale = new TemplateImportRequest(new string('0', 64), preflight.PreflightId, metadata);
        using var conflict = await f.Http.PostAsJsonAsync(TemplateRoot + "/personal/imports", stale, Json);
        await Error(conflict, HttpStatusCode.Conflict, BackendErrorCode.RevisionConflict); Assert.Equal("missing", (await TemplateList(f)).Revision);
        using var builtin = await f.Http.PostAsJsonAsync(TemplateRoot + "/builtin/imports", stale with { ExpectedRevision = "missing", Metadata = f.Metadata() }, Json);
        await Error(builtin, HttpStatusCode.Forbidden, BackendErrorCode.ReadOnlySession);
        var otherClient = f.Clients.Register(f._security.BootstrapToken, new()).Response!;
        using var other = new HttpClient { BaseAddress = f.Http.BaseAddress };
        other.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", otherClient.AccessToken);
        other.DefaultRequestHeaders.Add("X-Mako-Client-Id", otherClient.ClientId);
        using var deniedImport = await other.PostAsJsonAsync(TemplateRoot + "/personal/imports", stale with { ExpectedRevision = "missing", Metadata = f.Metadata() }, Json);
        await Error(deniedImport, HttpStatusCode.NotFound, BackendErrorCode.ResourceNotFound);
        using var importedResponse = await f.Http.PostAsJsonAsync(TemplateRoot + "/personal/imports", stale with { ExpectedRevision = "missing", Metadata = f.Metadata() }, Json);
        Assert.Equal(HttpStatusCode.OK, importedResponse.StatusCode);
        var imported = (await importedResponse.Content.ReadFromJsonAsync<TemplateImportResponse>(Json))!;
        var export = await TransferExport(f, imported.Revision, [imported.ImportedTemplates[0].TemplateId]);
        byte[] fixedBytes = await f.Http.GetByteArrayAsync(export.ContentUrl);
        using var renamed = await TemplatePost(f, imported.Revision, "{\"type\":\"rename\",\"templateId\":\"" + imported.ImportedTemplates[0].TemplateId + "\",\"name\":\"后来改变\"}");
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal(fixedBytes, await f.Http.GetByteArrayAsync(export.ContentUrl));
        using var head = await f.Http.SendAsync(new(HttpMethod.Head, export.ContentUrl));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode); Assert.Equal(export.SizeBytes, head.Content.Headers.ContentLength); Assert.Empty(await head.Content.ReadAsByteArrayAsync());
        Assert.Equal("\"" + export.Sha256 + "\"", head.Headers.ETag!.Tag);
        using var cachedRequest = new HttpRequestMessage(HttpMethod.Get, export.ContentUrl); cachedRequest.Headers.IfNoneMatch.Add(head.Headers.ETag);
        using var cached = await f.Http.SendAsync(cachedRequest); Assert.Equal(HttpStatusCode.NotModified, cached.StatusCode);
        using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, export.ContentUrl); rangeRequest.Headers.Range = new(0, 7);
        using var range = await f.Http.SendAsync(rangeRequest); Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode); Assert.Equal(fixedBytes[..8], await range.Content.ReadAsByteArrayAsync());
        using var denied = await other.GetAsync(export.ContentUrl); await Error(denied, HttpStatusCode.NotFound, BackendErrorCode.ResourceNotFound);
        f.Http.DefaultRequestHeaders.Authorization = null;
        using var unauthenticated = await f.Http.GetAsync(export.ContentUrl); Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
    }

    [Theory]
    [InlineData("imports/preflight", "{\"assetId\":\"x\",\"assetId\":\"y\"}")]
    [InlineData("imports/preflight", "{\"assetId\":\"x\",\"unknown\":true}")]
    [InlineData("imports/preflight", "{}")]
    [InlineData("imports", "{\"expectedRevision\":\"missing\"}")]
    [InlineData("exports", "{\"expectedRevision\":\"missing\",\"templateIds\":[],\"TemplateIds\":[]}")]
    [InlineData("exports", "{\"expectedRevision\":\"missing\"}")]
    public async Task HTTP归档JSON闭合字段唯一和必填(string route, string json)
    {
        await using var f = await TemplateFixture();
        using var response = await f.Http.PostAsync(TemplateRoot + "/personal/" + route, new StringContent(json, Encoding.UTF8, "application/json"));
        await Error(response, HttpStatusCode.BadRequest, BackendErrorCode.InvalidRequest);
        Assert.Equal("missing", (await TemplateList(f)).Revision);
    }

    [Theory]
    [InlineData("extra")] [InlineData("duplicate")] [InlineData("link")] [InlineData("../escape")] [InlineData("missing")] [InlineData("directories")]
    public async Task HTTP完整ZIP安全验证不写个人库且释放上传租约(string mutation)
    {
        await using var f = await TemplateFixture(); using TemporaryDirectory temporary = new(); string path = temporary.FilePath("evil.mktpl");
        await StyleTemplateTransfer.WriteArchiveAsync(path, [StyleTemplate.Create("t", "归档", StyleTemplateKind.Line, LyricStyle.Default, [LyricStyleProperty.FontSizePx])]);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            if (mutation == "missing") zip.GetEntry("index.json")!.Delete();
            else if (mutation == "directories") for (int i = 0; i <= TemplateTransferLimits.MaxEntries; i++) zip.CreateEntry("d" + i + "/");
            else { var entry = zip.CreateEntry(mutation == "duplicate" ? "index.json" : mutation); if (mutation == "link") entry.ExternalAttributes = 0xA000 << 16; }
        }
        var asset = await f.Resources.UploadAsync(f.Client.ClientId, "evil.mktpl", "application/octet-stream", new MemoryStream(await File.ReadAllBytesAsync(path)));
        using var response = await f.Http.PostAsJsonAsync(TemplateRoot + "/personal/imports/preflight", new TemplateImportPreflightRequest(asset.AssetId, f.Metadata()), Json);
        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.UnprocessableEntity, HttpStatusCode.RequestEntityTooLarge });
        Assert.Equal("missing", (await TemplateList(f)).Revision);
        f.Clock.Advance(2); f.Resources.CollectExpired(); Assert.False(f.Resources.TryGetAuthorized(f.Client.ClientId, asset.AssetId, out _));
        Assert.False(File.Exists(temporary.FilePath("escape")));
    }
}
