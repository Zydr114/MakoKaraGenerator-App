using System.Globalization;
using System.Text.Json;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSessionRegistry
{
    /// <summary>完整试样样式，包括未选材质参数；不读取或修改个人库。</summary>
    public static CanonicalStyleDto ExportFullTemplateStyleForPreview(StyleTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return ProjectSession.ExportTemplateLibraryStyle(template.Style, fullTemplate: true);
    }

    /// <summary>固定完整草稿闭包并经原模板工厂验证，不写库或工程。</summary>
    public async Task<BackendOperationResult<StyleTemplate>> CreateFullDraftTemplateForPreviewAsync(string clientId,
        string draftRevision, FullTemplateDefinitionDto definition, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(draftRevision) || draftRevision.Length > TemplatePreviewLimits.MaxDraftRevisionLength)
            return TemplateReject<StyleTemplate>(BackendErrorCode.InvalidRequest, "草稿修订不能为空且长度受限。", "source.draftRevision");
        FullTemplateDefinitionDto captured;
        try
        {
            captured = JsonSerializer.Deserialize<FullTemplateDefinitionDto>(JsonSerializer.SerializeToUtf8Bytes(definition,
                TemplateLibraryResponseBudget.Options), TemplateLibraryResponseBudget.Options)!;
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        { return TemplateReject<StyleTemplate>(BackendErrorCode.InvalidRequest, "完整草稿形状或数值非法。", "source.definition"); }
        var prepared = await ResolveFullTemplateDefinitionAsync(clientId, captured, cancellationToken).ConfigureAwait(false);
        if (!prepared.Accepted) return BackendOperationResult<StyleTemplate>.Rejected(prepared.Error!);
        try
        {
            var full = prepared.Response!;
            return BackendOperationResult<StyleTemplate>.Success(StyleTemplate.Create("draft:" + draftRevision, full.Name,
                full.Kind, full.Style, full.Properties, full.EffectRecipes, full.Packages));
        }
        catch (EffectPackageException error)
        { return PackageReject<StyleTemplate>(BackendErrorCode.CommandRejected, error.Message, error.Path, error.Code); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        { return PackageReject<StyleTemplate>(BackendErrorCode.CommandRejected, error.Message, "definition", "InvalidDefinition"); }
    }

    private async Task<BackendOperationResult<FullTemplateDefinition>> ResolveFullTemplateDefinitionAsync(string clientId,
        FullTemplateDefinitionDto definition, CancellationToken token = default)
    {
        if (definition is null || definition.Style is null || definition.Properties is null
            || definition.Packages is null || definition.EffectRecipes is null)
            return TemplateReject<FullTemplateDefinition>(BackendErrorCode.InvalidRequest, "必须提供完整模板定义。", "definition");
        List<EffectPackageContents> packages = [];
        foreach (var reference in definition.Packages)
        {
            var resolved = await ResolveEffectPackageReferenceAsync(clientId, reference, token).ConfigureAwait(false);
            if (!resolved.Accepted) return BackendOperationResult<FullTemplateDefinition>.Rejected(resolved.Error!);
            packages.Add(resolved.Response!);
        }
        FullTemplateDefinition? full = null;
        string field = "definition.style";
        try
        {
            var style = ProjectSession.ImportTemplateLibraryStyle(definition.Style, fullTemplate: true);
            List<TemplateEffectRecipe> recipes = [];
            for (int index = 0; index < definition.EffectRecipes.Length; index++)
            {
                field = "definition.effectRecipes[" + index.ToString(CultureInfo.InvariantCulture) + "]";
                recipes.Add(ImportTemplateRecipe(definition.EffectRecipes[index]));
            }
            full = new(definition.Name, (StyleTemplateKind)definition.Kind, style,
                ProjectSession.ImportTemplateProperties(definition.Properties),
                [.. recipes], [.. packages]);
            field = "definition";
            // The same domain factory validates preview and save before the library service can flatten its errors.
            _ = StyleTemplate.Create("full-definition-validation", full.Name, full.Kind, full.Style,
                full.Properties, full.EffectRecipes, full.Packages);
            return BackendOperationResult<FullTemplateDefinition>.Success(full);
        }
        catch (EffectPackageException error)
        { return PackageReject<FullTemplateDefinition>(BackendErrorCode.CommandRejected, error.Message, error.Path, error.Code); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or JsonException)
        {
            var location = full is null ? (field, error.Message) : FullTemplateValidationLocation(full, error);
            return PackageReject<FullTemplateDefinition>(BackendErrorCode.CommandRejected, location.Item2,
                location.Item1, "InvalidDefinition");
        }
    }

    private static (string Field, string Message) FullTemplateValidationLocation(FullTemplateDefinition definition, Exception error)
    {
        if (error is ArgumentException { ParamName: "name" }) return ("definition.name", error.Message);
        if (error is ArgumentException { ParamName: "kind" }) return ("definition.kind", error.Message);
        if (error is ArgumentException { ParamName: "style" }) return ("definition.style", error.Message);
        if (error is ArgumentException { ParamName: "properties" }) return ("definition.properties", error.Message);
        HashSet<string> recipes = new(StringComparer.Ordinal);
        for (int index = 0; index < definition.EffectRecipes.Length; index++)
            if (!recipes.Add(definition.EffectRecipes[index].RecipeId))
                return ("definition.effectRecipes[" + index.ToString(CultureInfo.InvariantCulture) + "].recipeId", "模板配方身份重复。");
        HashSet<(string PackageId, string Revision)> packages = [];
        for (int index = 0; index < definition.Packages.Length; index++)
        {
            var package = definition.Packages[index];
            if (!packages.Add((package.PackageId, package.Revision)))
                return ("definition.packages[" + index.ToString(CultureInfo.InvariantCulture) + "]", "同一固定包修订不能重复引用。");
        }
        var needed = definition.EffectRecipes.Select(recipe => (recipe.PackageId, recipe.Revision))
            .Concat(SourceMaterialCatalog.Collect(definition.Style).Select(reference =>
                (reference.Material.PackageId, reference.Material.Revision))).ToHashSet();
        if (!needed.IsSubsetOf(packages)) return ("definition.packages", "模板缺少材质或配方所需的完整固定包修订。");
        for (int index = 0; index < definition.Packages.Length; index++)
        {
            var package = definition.Packages[index];
            if (!needed.Contains((package.PackageId, package.Revision)))
                return ("definition.packages[" + index.ToString(CultureInfo.InvariantCulture) + "]", "模板包含材质或配方未使用的固定包修订。");
        }
        return ("definition", error.Message);
    }

    private static TemplateEffectRecipe ImportTemplateRecipe(TemplateEffectRecipeDto recipe) => new(recipe.RecipeId,
        recipe.Name, (TemplateEffectTarget)recipe.Target, recipe.PackageId, recipe.Revision, recipe.ExportId,
        EffectParameterOverrides.Parse(recipe.ParametersJson), (EffectOverlayLayer)recipe.OverlayLayer, recipe.Enabled,
        (ScriptReadScope)recipe.ReadScope, recipe.BranchInput,
        recipe.FixedSeed is null ? null : ulong.Parse(recipe.FixedSeed, NumberStyles.None, CultureInfo.InvariantCulture), recipe.ConfigurationId);

    private static TemplateEffectRecipeDto ExportTemplateRecipe(TemplateEffectRecipe recipe) => new(recipe.RecipeId,
        recipe.Name, (TemplateEffectTargetDto)recipe.Target, recipe.PackageId, recipe.Revision, recipe.ExportId,
        recipe.Parameters.Json, (TemplateEffectOverlayLayerDto)recipe.OverlayLayer,
        recipe.Enabled, (TemplateScriptReadScopeDto)recipe.ReadScope, recipe.BranchInput,
        recipe.FixedSeed?.ToString(CultureInfo.InvariantCulture), recipe.ConfigurationId);

    private static TemplateDetailResponse FullTemplateDetail(string libraryId, string libraryRevision, StyleTemplate template)
    {
        var summary = TemplateSummary(template);
        var references = template.Packages.Select(package => CreateLibraryEffectPackageReference(libraryId,
            libraryRevision, template.Id, summary.TemplateRevision, package)).ToArray();
        var definition = new FullTemplateDefinitionDto(template.Name, (TemplateKind)template.Kind,
            ExportFullTemplateStyleForPreview(template), TemplateProperties(template),
            [.. template.EffectRecipes.Select(ExportTemplateRecipe)], references);
        return new(libraryId, libraryRevision, summary, "fullAvailable", definition, DefinitionFormat: "full",
            PackageClosureDetails: [.. template.Packages.Zip(references, (package, reference) => CreateEffectPackageInspection(package, reference))]);
    }
}
