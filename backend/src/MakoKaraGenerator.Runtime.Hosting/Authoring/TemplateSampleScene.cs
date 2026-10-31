using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Runtime.Hosting.Authoring;

/// <summary>模板卡和工坊共用固定试样及正式预设应用投影，与当前工程无关。</summary>
public static partial class TemplateSampleScene
{
    /// <summary>卡片试样通过正式预设应用投影建立固定快照。</summary>
    public static ProjectSnapshot Build(StyleTemplateKind kind, LyricStyle style,
        IReadOnlyList<LyricStyleProperty> properties,
        IReadOnlyList<EffectPackageContents>? packages = null,
        IReadOnlyList<TemplateEffectRecipe>? effectRecipes = null)
    {
        Project scene = WorkshopScene(false, null);
        if (properties.Count == 0 && effectRecipes is not { Count: > 0 }) return ProjectSnapshot.FromProject(scene);
        var template = StyleTemplate.Create("template-preview", "template-preview", kind, style, properties, effectRecipes, packages);
        var target = Targets(scene, template).FirstOrDefault() ?? throw new TemplateSampleException("TemplateSampleNoTarget");
        return ProjectSnapshot.FromProject(ApplyWorkshop(scene, template, target, null));
    }
}
