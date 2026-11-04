using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Core.Projects;

internal static class StyleTemplateEffects
{
    internal static void Validate(StyleTemplateKind kind, LyricStyle style, ImmutableArray<TemplateEffectRecipe> recipes, ImmutableArray<EffectPackageContents> packages)
    {
        if (recipes.IsDefault || packages.IsDefault || recipes.Any(r => r is null) || packages.Any(p => p is null)
            || recipes.Select(r => r.RecipeId).Distinct(StringComparer.Ordinal).Count() != recipes.Length
            || packages.Select(p => (p.PackageId, p.Revision)).Distinct().Count() != packages.Length)
            throw new ArgumentException("模板配方或包身份无效／重复。");
        var materials = SourceMaterialCatalog.Collect(style);
        var needed = recipes.Select(r => (r.PackageId, r.Revision)).Concat(materials.Select(r => (r.Material.PackageId, r.Material.Revision))).ToHashSet();
        if (packages.Any(p => !EffectPackageProfiles.IsSupported(p.Profile))) throw new ArgumentException("模板包profile与当前程序不符。");
        if (!needed.SetEquals(packages.Select(p => (p.PackageId, p.Revision)))) throw new ArgumentException("模板必须携带恰好完整的材质／配方修订闭包。");
        var definitions = packages.ToDictionary(p => (p.PackageId, p.Revision), p => EffectPackageDefinition.Read(p));
        foreach (var reference in materials) SourceMaterialCatalog.Validate(reference, definitions[(reference.Material.PackageId, reference.Material.Revision)]);
        foreach (var recipe in recipes)
        {
            if (kind == StyleTemplateKind.Token && recipe.Target == TemplateEffectTarget.Line) throw new ArgumentException("元素模板只允许本对象或元素族配方。");
            var definition = definitions[(recipe.PackageId, recipe.Revision)]; var export = definition.GetExport(recipe.ExportId);
            var configuration = recipe.ToConfiguration(packages.Single(p => p.PackageId == recipe.PackageId && p.Revision == recipe.Revision));
            if (configuration.Kind != ScriptConfigurationKind.Effect) throw new ArgumentException("材质通过材质源字段使用，不能伪装为效果配方。");
            string? binding = kind == StyleTemplateKind.Composite ? null : recipe.Target == TemplateEffectTarget.Application
                ? kind switch { StyleTemplateKind.Line => "lines", StyleTemplateKind.Token => "tokens", _ => null }
                : recipe.Target == TemplateEffectTarget.Line ? "lines" : "tokens";
            if (binding is not null && !configuration.BindingKinds.Contains(binding, StringComparer.Ordinal))
                throw new ArgumentException("配方导出不支持模板的相对目标。");
            if (kind != StyleTemplateKind.Composite && export.Outputs.Any(o => o.Target == "group")) throw new ArgumentException("行／元素模板不能写入组。");
            if ((recipe.Target == TemplateEffectTarget.TokenFamily || kind == StyleTemplateKind.Token) && export.Outputs.Any(o => o.Kind != "Overlay" && o.Target != "tokenFamily"))
                throw new ArgumentException("元素族配方的非图元输出只允许元素族目标。");
            _ = definition.ResolveParameters(recipe.ExportId, recipe.Parameters.Encode());
            if (recipe.OverlayLayer != EffectOverlayLayer.Package && !export.Outputs.Any(o => o.Kind == "Overlay")) throw new ArgumentException("非图元效果不能设置图元层覆盖。");
        }
    }
}
