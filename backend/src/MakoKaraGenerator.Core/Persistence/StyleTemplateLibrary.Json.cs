using System.Globalization;
using System.Text.Json;
using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using static MakoKaraGenerator.Core.Effects.EffectPackageJson;

namespace MakoKaraGenerator.Core.Persistence;

public static partial class StyleTemplateLibrary
{
    /// <summary>完整入口编码，包资源由内容闭包提供；不保存外部路径。</summary>
    public static byte[] Encode(IEnumerable<StyleTemplate> templates)
    {
        return EncodeItems(Validate(templates));
    }

    private static byte[] EncodeItems(StyleTemplate[] items)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new() { Indented = true }))
        {
            writer.WriteStartObject(); writer.WriteNumber("schemaVersion", CurrentSchemaVersion); writer.WriteStartArray("packages");
            foreach (var package in Packages(items))
            { writer.WriteStartObject(); writer.WriteString("packageId", package.PackageId); writer.WriteString("revision", package.Revision); writer.WriteString("path", PackagePath(package.Revision)); writer.WriteEndObject(); }
            writer.WriteEndArray(); writer.WriteStartArray("templates");
            foreach (var template in items) WriteDefinition(writer, template);
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        byte[] bytes = stream.ToArray(); if (bytes.Length > MaximumIndexBytes) throw new IOException("模板入口超过显式16MiB限额。"); return bytes;
    }

    internal static void WriteDefinition(Utf8JsonWriter writer, StyleTemplate template)
    {
        writer.WriteStartObject(); writer.WriteString("id", template.Id); writer.WriteString("name", template.Name); writer.WriteString("kind", template.Kind.ToString());
        writer.WriteStartArray("properties"); foreach (var property in template.Properties) writer.WriteStringValue(LyricStylePropertyExtensions.ToJsonName(property)); writer.WriteEndArray();
        writer.WritePropertyName("style"); JsonSerializer.Serialize(writer, ProjectDtoMapper.ToDto(template.Style), ProjectJson.Options);
        writer.WriteStartArray("effectRecipes");
        foreach (var recipe in template.EffectRecipes)
        {
            writer.WriteStartObject(); writer.WriteString("recipeId", recipe.RecipeId); writer.WriteString("name", recipe.Name);
            writer.WriteString("target", recipe.Target switch { TemplateEffectTarget.Application => "application", TemplateEffectTarget.Line => "line", TemplateEffectTarget.TokenFamily => "tokenFamily", _ => throw Type("recipe.target") });
            writer.WriteString("packageId", recipe.PackageId); writer.WriteString("revision", recipe.Revision); writer.WriteString("exportId", recipe.ExportId);
            writer.WritePropertyName("parameters"); using var parameters = JsonDocument.Parse(recipe.Parameters.Json); parameters.RootElement.WriteTo(writer);
            writer.WriteString("overlayLayer", ProjectEffectsJson.OverlayName(recipe.OverlayLayer)); writer.WriteBoolean("enabled", recipe.Enabled);
            writer.WriteString("readScope", recipe.ReadScope.ToString()); writer.WriteString("branchInput", recipe.BranchInput);
            writer.WriteString("fixedSeed", recipe.FixedSeed?.ToString("x16", CultureInfo.InvariantCulture)); writer.WriteString("configurationId", recipe.ConfigurationId);
            writer.WriteEndObject();
        }
        writer.WriteEndArray(); writer.WriteEndObject();
    }

    internal static StyleTemplate ReadDefinition(JsonElement item, IReadOnlyDictionary<(string, string), EffectPackageContents> packages)
    {
        Keys(item, "template", ["id", "name", "kind", "properties", "style", "effectRecipes"], []);
        var kind = RequiredText(item, "kind", "template") switch { "Line" => StyleTemplateKind.Line, "Token" => StyleTemplateKind.Token,
            "Composite" => StyleTemplateKind.Composite, _ => throw Type("template.kind") };
        var properties = Strings(item.GetProperty("properties"), "properties").Select(name => LyricStylePropertyExtensions.TryParseJsonName(name, out var property) ? property : throw new ArgumentException("未知模板属性：" + name));
        if (item.GetProperty("style").ValueKind != JsonValueKind.Object) throw Type("template.style");
        var styleDto = JsonSerializer.Deserialize<LyricStyleDto>(item.GetProperty("style").GetRawText(), ProjectJson.Options); List<ProjectIssue> issues = [];
        // A template library preserves the complete GradientSpec data contract, including
        // coordinate/color spaces that the current project renderer has not implemented yet.
        // Project persistence keeps its stricter renderer-support validation path.
        var style = ProjectDtoMapper.MapLyricStyle(styleDto, issues, allowUnsupportedGradients: true);
        if (issues.Any(i => i.Severity == IssueSeverity.Error)) throw new ArgumentException("模板静态样式无效。");
        var recipes = ReadRecipes(item.GetProperty("effectRecipes"));
        var needed = recipes.Select(r => (r.PackageId, r.Revision)).Concat(SourceMaterialCatalog.Collect(style).Select(r => (r.Material.PackageId, r.Material.Revision))).Distinct()
            .Select(key => packages.TryGetValue(key, out var package) ? package : throw new ArgumentException("模板缺少固定包修订。"))
            .OrderBy(package => package.PackageId, StringComparer.Ordinal).ThenBy(package => package.Revision, StringComparer.Ordinal);
        return StyleTemplate.Create(RequiredText(item, "id", "template"), RequiredText(item, "name", "template"), kind, style, properties, recipes, needed);
    }

    private static TemplateEffectRecipe[] ReadRecipes(JsonElement value)
    {
        List<TemplateEffectRecipe> recipes = [];
        foreach (var item in Array(value, "effectRecipes"))
        {
            Keys(item, "effectRecipe", ["recipeId", "name", "target", "packageId", "revision", "exportId", "parameters", "overlayLayer", "enabled", "readScope", "branchInput", "fixedSeed", "configurationId"], []);
            var target = RequiredText(item, "target", "effectRecipe") switch { "application" => TemplateEffectTarget.Application, "line" => TemplateEffectTarget.Line, "tokenFamily" => TemplateEffectTarget.TokenFamily, _ => throw Type("effectRecipe.target") };
            string scope = RequiredText(item, "readScope", "effectRecipe");
            if (!Enum.TryParse<ScriptReadScope>(scope, out var readScope) || !Enum.IsDefined(readScope) || readScope.ToString() != scope) throw Type("effectRecipe.readScope");
            recipes.Add(new(RequiredText(item, "recipeId", "effectRecipe"), RequiredText(item, "name", "effectRecipe"), target,
                RequiredText(item, "packageId", "effectRecipe"), RequiredText(item, "revision", "effectRecipe"), RequiredText(item, "exportId", "effectRecipe"),
                EffectParameterOverrides.Parse(item.GetProperty("parameters").GetRawText()), ProjectEffectsJson.ReadOverlay(RequiredText(item, "overlayLayer", "effectRecipe")),
                ReadBoolean(item.GetProperty("enabled"), "effectRecipe.enabled"), readScope, ReadNullableText(item.GetProperty("branchInput"), "effectRecipe.branchInput"),
                ReadSeed(item.GetProperty("fixedSeed")), ReadNullableText(item.GetProperty("configurationId"), "effectRecipe.configurationId")));
        }
        return [.. recipes];
    }

    internal static bool ReadBoolean(JsonElement value, string path)
        => value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : throw Type(path);
    internal static string? ReadNullableText(JsonElement value, string path) => value.ValueKind == JsonValueKind.Null ? null : String(value, path);
    internal static ulong? ReadSeed(JsonElement value)
    {
        string? text = ReadNullableText(value, "fixedSeed"); if (text is null) return null;
        return text.Length == 16 && ulong.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong seed)
            && text == seed.ToString("x16", CultureInfo.InvariantCulture) ? seed : throw Type("fixedSeed");
    }
}
