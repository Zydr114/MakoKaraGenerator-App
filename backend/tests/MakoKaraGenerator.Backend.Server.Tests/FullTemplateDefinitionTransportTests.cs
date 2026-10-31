using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    public async Task HTTP完整高级定义导入读取编辑创建与归档再导入保真()
    {
        await using var f = await TemplateFixture();
        var materialPackage = MixedEffectPackageFixture.MaterialContents();
        var files = EffectPackageFixture.Files(); var manifest = EffectPackageFixture.Manifest(files);
        manifest["packageId"] = "full.recipe";
        manifest["parameters"]!.AsArray().Add(JsonNode.Parse("""{"id":"whole","type":"integer","label":"完整整数","default":9007199254740993,"min":0,"max":9223372036854775807}"""));
        manifest["exports"]![0]!["parameters"]!.AsArray().Add("whole");
        var recipePackage = EffectPackageContents.Create(EffectPackageFixture.Bytes(manifest), EffectPackageFixture.Inputs(files), EffectPackageFixture.Profile);
        var style = LyricStyle.Default with
        {
            Typography = LyricStyle.Default.Typography with { FontFamilyName = "未选字体", LetterSpacingPx = 2.125f },
            UnsungFill = new() { Material = new MaterialSpec.PackageMaterial(materialPackage.PackageId, materialPackage.Revision, "ink",
                EffectParameterOverrides.Parse("{\"amount\":0.12345678901234567890123456789}")) },
            Shadow = new() { Color = new(1, 2, 3, 4), DistancePx = 7.5f, BlurRadiusPx = 3.25f },
        };
        TemplateEffectRecipe[] recipes =
        [
            new("first", "第一配方", TemplateEffectTarget.TokenFamily, recipePackage.PackageId, recipePackage.Revision, "scale",
                EffectParameterOverrides.Parse("{\"peak\":1.25,\"whole\":9007199254740993}"), enabled: false,
                fixedSeed: ulong.MaxValue, configurationId: "source-config"),
            new("second", "第二配方", TemplateEffectTarget.TokenFamily, recipePackage.PackageId, recipePackage.Revision, "scale",
                EffectParameterOverrides.Parse("{\"peak\":2.5,\"whole\":9223372036854775807}")),
        ];
        var original = StyleTemplate.Create("full", "完整高级", StyleTemplateKind.Composite, style,
            [LyricStyleProperty.FontSizePx], recipes, [materialPackage, recipePackage]);
        var preflight = await TransferPreflight(f, await TransferBytes([original]));
        using var importedResponse = await f.Http.PostAsJsonAsync(TemplateRoot + "/personal/imports",
            new TemplateImportRequest("missing", preflight.PreflightId, f.Metadata()), Json);
        Assert.Equal(HttpStatusCode.OK, importedResponse.StatusCode);
        var imported = (await importedResponse.Content.ReadFromJsonAsync<TemplateImportResponse>(Json))!;
        string id = Assert.Single(imported.ImportedTemplates).TemplateId;
        string Detail(string revision, string templateId) => TemplateRoot + "/personal/templates/detail?expectedRevision=" + revision
            + "&templateId=" + Uri.EscapeDataString(templateId);
        var legacy = (await f.Http.GetFromJsonAsync<TemplateDetailResponse>(Detail(imported.Revision, id), Json))!;
        Assert.Null(legacy.Definition); Assert.Null(legacy.DefinitionFormat);
        var detail = (await f.Http.GetFromJsonAsync<TemplateDetailResponse>(Detail(imported.Revision, id) + "&definitionFormat=full", Json))!;
        var definition = Assert.IsType<FullTemplateDefinitionDto>(detail.Definition);
        Assert.Equal("fullAvailable", detail.DefinitionAvailability);
        Assert.Equal(["first", "second"], definition.EffectRecipes.Select(value => value.RecipeId));
        Assert.Equal("18446744073709551615", definition.EffectRecipes[0].FixedSeed);
        Assert.Contains("9007199254740993", definition.EffectRecipes[0].ParametersJson);
        Assert.Equal("{\"amount\":0.12345678901234567890123456789}", Assert.IsType<PackageStyleMaterialDto>(definition.Style.UnsungFill.Material).ParametersJson);
        Assert.Equal("未选字体", definition.Style.Typography.FontFamilyName); Assert.Equal(2, definition.Packages.Length);
        Assert.All(definition.Packages, value => Assert.Equal("library", value.Kind));
        Assert.Equal(2, detail.PackageClosureDetails!.Length);
        Assert.All(detail.PackageClosureDetails, value => { Assert.True(value.StructureValid); Assert.NotEmpty(value.ManifestJson); Assert.NotEmpty(value.Files); });

        foreach (string invalidDefinition in new[] { "null", JsonSerializer.Serialize(definition, Json).Replace("\"effectRecipes\":[", "\"effectRecipes\":[null,", StringComparison.Ordinal) })
        {
            using var invalid = await TemplatePost(f, imported.Revision, "{\"type\":\"replace\",\"definitionFormat\":\"full\",\"templateId\":"
                + JsonSerializer.Serialize(id) + ",\"definition\":" + invalidDefinition + "}");
            await Error(invalid, HttpStatusCode.BadRequest, BackendErrorCode.InvalidRequest);
        }
        Assert.Equal(imported.Revision, (await TemplateList(f)).Revision);

        var edited = definition with { Name = "完整修改", EffectRecipes = [definition.EffectRecipes[1], definition.EffectRecipes[0] with { Enabled = true }] };
        using var replacedResponse = await TemplatePost(f, imported.Revision, JsonSerializer.Serialize<TemplateLibraryCommandDto>(new ReplaceTemplateCommandDto(id, edited, "full"), Json));
        var replaced = await TemplateSuccess(replacedResponse);
        var after = (await f.Http.GetFromJsonAsync<TemplateDetailResponse>(Detail(replaced.Revision, id) + "&definitionFormat=full", Json))!;
        var refreshed = Assert.IsType<FullTemplateDefinitionDto>(after.Definition);
        Assert.Equal(["second", "first"], refreshed.EffectRecipes.Select(value => value.RecipeId));
        Assert.True(refreshed.EffectRecipes[1].Enabled);
        using var createdResponse = await TemplatePost(f, replaced.Revision, JsonSerializer.Serialize<TemplateLibraryCommandDto>(new CreateTemplateCommandDto(refreshed with { Name = "完整新建" }, "full"), Json));
        var created = await TemplateSuccess(createdResponse);
        var archive = await TransferExport(f, created.Revision, [created.TemplateId]);
        using TemporaryDirectory temporary = new(); string archivePath = temporary.FilePath("full.mktpl");
        await File.WriteAllBytesAsync(archivePath, await f.Http.GetByteArrayAsync(archive.ContentUrl));
        var roundTrip = Assert.Single(await StyleTemplateTransfer.ReadArchiveAsync(archivePath));
        var expected = StyleTemplate.Create(created.TemplateId, "完整新建", original.Kind, style, original.Properties,
            [recipes[1], new("first", "第一配方", TemplateEffectTarget.TokenFamily, recipePackage.PackageId, recipePackage.Revision, "scale",
                recipes[0].Parameters, enabled: true, fixedSeed: ulong.MaxValue, configurationId: "source-config")], [materialPackage, recipePackage]);
        Assert.Equal(StyleTemplateLibrary.EncodeForIdentity([expected]), StyleTemplateLibrary.EncodeForIdentity([roundTrip]));
        await using var fresh = await TemplateFixture(); var next = await TransferPreflight(fresh, await File.ReadAllBytesAsync(archivePath));
        using var reimported = await fresh.Http.PostAsJsonAsync(TemplateRoot + "/personal/imports",
            new TemplateImportRequest("missing", next.PreflightId, fresh.Metadata()), Json);
        Assert.Equal(HttpStatusCode.OK, reimported.StatusCode);
        Assert.Equal("0", (await f.Projects.ReadSummaryAsync(f.Client.ClientId, f.Project.ProjectSessionId))!.ProjectRevision);
    }
}
