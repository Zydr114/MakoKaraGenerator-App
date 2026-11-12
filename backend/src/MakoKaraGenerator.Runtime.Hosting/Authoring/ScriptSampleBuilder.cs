using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Runtime.Hosting.Authoring;

/// <summary>独立脚本验证只接受配置和工坊试样选择，不接受当前歌曲或工程对象。</summary>
public sealed record ScriptSampleRequest(ScriptConfiguration Configuration, TemplateSampleTarget Target,
    TemplateMaterialSlot MaterialSlot, bool MultiLine = true);

/// <summary>固定脚本配置到独立作者试样的纯投影。</summary>
public static class ScriptSampleBuilder
{
    /// <summary>未指定作者种子时的固定试样种子，不写回配置。</summary>
    public const ulong Seed = 0x4D414B4F53414D50;

    /// <summary>按固定材质声明开放部位。</summary>
    public static TemplateMaterialSlot[] MaterialSlots(ScriptConfiguration configuration)
    {
        if (configuration.Kind != ScriptConfigurationKind.Material) return [];
        var material = (EffectMaterialDefinition)EffectPackageDefinition.Read(configuration.Package).GetExport(configuration.ExportId);
        List<TemplateMaterialSlot> slots = [];
        if (material.Regions.Contains("fill")) { slots.Add(TemplateMaterialSlot.UnsungFill); slots.Add(TemplateMaterialSlot.SungFill); }
        if (material.Regions.Contains("stroke")) slots.Add(TemplateMaterialSlot.Stroke);
        return [.. slots];
    }

    /// <summary>固定配置能够写入的真实试样目标。</summary>
    public static TemplateSampleTarget[] Targets(ScriptConfiguration configuration, TemplateMaterialSlot slot, bool multiLine = true) =>
        TemplateSampleScene.Targets(TemplateSampleScene.WorkshopScene(multiLine, null), Template(configuration, slot));

    /// <summary>校验稳定选区后通过正式预设应用投影生成快照。</summary>
    public static ProjectSnapshot Build(ScriptSampleRequest request, TemplateSampleResources resources)
    {
        StyleTemplate template = Template(request.Configuration, request.MaterialSlot);
        Project scene = TemplateSampleScene.WorkshopScene(request.MultiLine, resources);
        var target = TemplateSampleScene.Targets(scene, template)
            .SingleOrDefault(item => item.Key == request.Target.Key && item.Binding.Equals(request.Target.Binding))
            ?? throw new TemplateSampleException("TemplateSampleNoTarget");
        return ProjectSnapshot.FromProject(TemplateSampleScene.ApplyWorkshop(scene, template, target, null));
    }

    private static StyleTemplate Template(ScriptConfiguration configuration, TemplateMaterialSlot slot)
    {
        var style = LyricStyle.Default;
        LyricStyleProperty[] properties = [];
        TemplateEffectRecipe[] recipes = [];
        if (configuration.Kind == ScriptConfigurationKind.Material)
        {
            if (!MaterialSlots(configuration).Contains(slot))
                throw new TemplateSampleException("ScriptRuntimeMaterialUnsupported");
            var material = configuration.ToMaterial();
            (style, properties) = slot switch
            {
                TemplateMaterialSlot.UnsungFill => (style with { UnsungFill = new() { Material = material } },
                    new LyricStyleProperty[] { LyricStyleProperty.UnsungFill }),
                TemplateMaterialSlot.SungFill => (style with { SungFill = new() { Material = material } },
                    new LyricStyleProperty[] { LyricStyleProperty.SungFill }),
                TemplateMaterialSlot.Stroke => (style with { Stroke = new() { Material = material, WidthPx = 3 } },
                    new LyricStyleProperty[] { LyricStyleProperty.UnsungStrokes }),
                _ => throw new ArgumentOutOfRangeException(nameof(slot)),
            };
        }
        else
        {
            // 试样固定种子不写回配置；显式作者种子优先。
            var sampled = ScriptConfiguration.Create(configuration.Id, configuration.Name, configuration.Package,
                configuration.ExportId, configuration.Parameters, configuration.Target, configuration.ReadScope,
                configuration.BranchInput, configuration.FixedSeed ?? Seed, configuration.OverlayLayer);
            recipes = [TemplateEffectRecipe.FromConfiguration("script-validation:recipe", sampled)];
        }
        return StyleTemplate.Create("script-validation", configuration.Name, StyleTemplateKind.Composite,
            style, properties, recipes, [configuration.Package]);
    }
}
