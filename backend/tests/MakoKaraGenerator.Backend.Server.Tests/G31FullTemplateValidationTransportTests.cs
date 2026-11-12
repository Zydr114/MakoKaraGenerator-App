using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed partial class PreviewTransportTests
{
    [Theory]
    [InlineData("duplicate-package", "definition.packages[1]", "InvalidDefinition")]
    [InlineData("duplicate-recipe", "definition.effectRecipes[1].recipeId", "InvalidDefinition")]
    [InlineData("missing-package", "definition.packages", "InvalidDefinition")]
    [InlineData("extra-package", "definition.packages[0]", "InvalidDefinition")]
    [InlineData("parameter-range", "parameters.peak", "PackageParameterOutOfRange")]
    [InlineData("parameter-unknown", "parameters.nope", "PackageSemanticUnknownField")]
    [InlineData("parameter-curve", "parameters.curve", "PackageCurveInvalid")]
    [InlineData("parameter-image", "parameters.asset", "PackageParameterImageMissing")]
    [InlineData("parameter-duplicate", "peak", "EffectFieldDuplicate")]
    [InlineData("export-missing", "exports.missing", "PackageExportMissing")]
    public async Task G31_完整模板预览与新建替换保留具体校验错误且拒绝不改状态(string defect,
        string field, string failureCode)
    {
        await using var f = await TemplateFixture();
        var files = EffectPackageFixture.Files();
        files["assets/image.png"] = [1, 2, 3];
        var package = EffectPackageContents.Create(EffectPackageFixture.Bytes(EffectPackageFixture.NineParametersManifest(files)),
            EffectPackageFixture.Inputs(files), EffectPackageFixture.Profile);
        var reference = await G31UploadPackage(f, package, "full.mkfx");
        var style = ProjectSessionRegistry.ExportFullTemplateStyleForPreview(StyleTemplate.Create("style", "样式",
            StyleTemplateKind.Composite, LyricStyle.Default, [LyricStyleProperty.Opacity]));
        TemplateEffectRecipeDto recipe = new("recipe", "配方", TemplateEffectTargetDto.TokenFamily,
            package.PackageId, package.Revision, "scale", "{}", TemplateEffectOverlayLayerDto.Package, true,
            TemplateScriptReadScopeDto.BoundSources, null, null, null);
        FullTemplateDefinitionDto valid = new("完整模板", TemplateKind.Composite, style, ["opacity"],
            [recipe], [reference]);
        using var baselineResponse = await TemplatePost(f, "missing",
            JsonSerializer.Serialize<TemplateLibraryCommandDto>(new CreateTemplateCommandDto(valid, "full"), Json));
        var baseline = await TemplateSuccess(baselineResponse);
        var before = await TemplateList(f);
        var historyBefore = await f.Projects.ReadHistoryAsync(f.Client.ClientId, f.Project.ProjectSessionId);

        FullTemplateDefinitionDto invalid = defect switch
        {
            "duplicate-package" => valid with { Packages = [reference, await ConfigurationReference()] },
            "duplicate-recipe" => valid with { EffectRecipes = [recipe, recipe with { Name = "重复身份" }] },
            "missing-package" => valid with { Packages = [] },
            "extra-package" => valid with { EffectRecipes = [] },
            "parameter-range" => WithParameters("{\"peak\":99}"),
            "parameter-unknown" => WithParameters("{\"nope\":1}"),
            "parameter-curve" => WithParameters("{\"curve\":{\"kind\":\"easing\",\"easing\":\"unknown\"}}"),
            "parameter-image" => WithParameters("{\"asset\":\"assets/missing.png\"}"),
            "parameter-duplicate" => WithParameters("{\"peak\":1,\"peak\":2}"),
            "export-missing" => valid with { EffectRecipes = [recipe with { ExportId = "missing" }] },
            _ => throw new ArgumentOutOfRangeException(nameof(defect)),
        };

        var source = new TemplatePreviewSourceDto("draft", DraftRevision: "g31-draft", Definition: invalid,
            DefinitionFormat: "full");
        using var preview = await f.Http.PostAsJsonAsync("/api/v1/template-preview-options",
            new TemplatePreviewOptionsRequest(source), Json);
        BackendErrorResponse previewError = await AssertValidation(preview);
        foreach (TemplateLibraryCommandDto command in new TemplateLibraryCommandDto[]
        {
            new CreateTemplateCommandDto(invalid, "full"),
            new ReplaceTemplateCommandDto(baseline.TemplateId, invalid, "full"),
        })
        {
            using var rejected = await TemplatePost(f, baseline.Revision,
                JsonSerializer.Serialize(command, Json));
            var error = await AssertValidation(rejected);
            Assert.Equal(previewError.Message, error.Message);
        }

        var after = await TemplateList(f);
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(JsonSerializer.Serialize(before.Templates, Json), JsonSerializer.Serialize(after.Templates, Json));
        Assert.Equal(JsonSerializer.Serialize(historyBefore, Json),
            JsonSerializer.Serialize(await f.Projects.ReadHistoryAsync(f.Client.ClientId, f.Project.ProjectSessionId), Json));
        Assert.Equal(0, f.Previews.Count);
        Assert.Equal(0, f.Factory.Created);

        FullTemplateDefinitionDto WithParameters(string parameters) => valid with
        { EffectRecipes = [recipe with { ParametersJson = parameters }] };
        async Task<BackendErrorResponse> AssertValidation(HttpResponseMessage response)
        {
            var error = await Error(response, HttpStatusCode.UnprocessableEntity, BackendErrorCode.CommandRejected);
            Assert.Equal(field, error.Field);
            Assert.Equal(failureCode, error.FailureCode);
            Assert.DoesNotContain(f._directory.Path, error.Message, StringComparison.Ordinal);
            return error;
        }
        async Task<EffectPackageReferenceDto> ConfigurationReference()
        {
            f.Projects.ConfigureScriptConfigurations();
            ScriptConfigurationInputDto input = new("重复包配置", "scale", "{}", TemplateEffectTargetDto.TokenFamily,
                TemplateScriptReadScopeDto.BoundSources, null, null, TemplateEffectOverlayLayerDto.Package,
                new(Package: reference));
            using var response = await f.Http.PostAsJsonAsync("/api/v1/script-configurations/commands",
                new ScriptConfigurationCommandRequest("missing", f.Metadata(), new ScriptConfigurationCreateCommandDto("create", input)), Json);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var created = (await response.Content.ReadFromJsonAsync<ScriptConfigurationCommandResponse>(Json))!;
            return new("configuration", package.PackageId, package.Revision, ConfigurationId: created.ConfigurationId,
                ConfigurationRevision: created.Revision);
        }
    }

    [Fact]
    public async Task G31_未选材质槽参数仍由原包语义验证并保留定位()
    {
        await using var f = await TemplateFixture();
        var package = MixedEffectPackageFixture.MaterialContents();
        var reference = await G31UploadPackage(f, package, "material.mkfx");
        var style = ProjectSessionRegistry.ExportFullTemplateStyleForPreview(StyleTemplate.Create("style", "样式",
            StyleTemplateKind.Composite, LyricStyle.Default, [LyricStyleProperty.Opacity]));
        style = style with { Unsung = style.Unsung with { Fill = new PackageStyleMaterialDto(package.PackageId, package.Revision, "ink", "{\"amount\":2}") } };
        FullTemplateDefinitionDto invalid = new("未选材质", TemplateKind.Composite, style, ["opacity"], [], [reference]);
        using var preview = await f.Http.PostAsJsonAsync("/api/v1/template-preview-options",
            new TemplatePreviewOptionsRequest(new("draft", DraftRevision: "g31-material", Definition: invalid,
                DefinitionFormat: "full")), Json);
        var previewError = await Error(preview, HttpStatusCode.UnprocessableEntity, BackendErrorCode.CommandRejected);
        Assert.Equal("parameters.amount", previewError.Field);
        Assert.Equal("PackageParameterOutOfRange", previewError.FailureCode);
        using var save = await TemplatePost(f, "missing",
            JsonSerializer.Serialize<TemplateLibraryCommandDto>(new CreateTemplateCommandDto(invalid, "full"), Json));
        var saveError = await Error(save, HttpStatusCode.UnprocessableEntity, BackendErrorCode.CommandRejected);
        Assert.Equal(previewError.Field, saveError.Field);
        Assert.Equal(previewError.FailureCode, saveError.FailureCode);
        Assert.Equal("missing", (await TemplateList(f)).Revision);
        Assert.Equal("0", (await f.Projects.ReadHistoryAsync(f.Client.ClientId, f.Project.ProjectSessionId))!.ProjectRevision);
    }

    private static async Task<EffectPackageReferenceDto> G31UploadPackage(Fixture fixture,
        EffectPackageContents package, string name)
    {
        string path = fixture._directory.FilePath(name);
        await EffectPackageTransfer.WriteArchiveAsync(path, package);
        await using var stream = File.OpenRead(path);
        var asset = await fixture.Resources.UploadAsync(fixture.Client.ClientId, name, "application/octet-stream", stream);
        return new("asset", package.PackageId, package.Revision, asset.AssetId);
    }
}
