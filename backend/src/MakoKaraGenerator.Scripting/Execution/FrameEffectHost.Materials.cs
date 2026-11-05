using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting.Compilation;

namespace MakoKaraGenerator.Scripting.Execution;

internal sealed partial class FrameEffectHost
{
    private static void TrackMaterials(ImageEffectTarget target, StyleUpdate update, StyleView resolved, ActiveEffect owner,
        Dictionary<(ImageEffectTarget, PreparedMaterialSlot), PreparedMaterialBinding> bindings)
    {
        foreach (var slot in update.MaterialEdits.Select(edit => edit switch
            { MaterialEdit.ReplaceMaterial set => set.Slot, MaterialEdit.Uniform uniform => uniform.Slot, _ => throw new InvalidOperationException("未知材质操作。") }).Distinct())
        {
            var material = slot switch { MaterialSlot.UnsungFill => resolved.UnsungMaterial, MaterialSlot.SungFill => resolved.SungMaterial,
                MaterialSlot.Stroke => resolved.Stroke?.Material, _ => throw new InvalidOperationException("未知材质部位。") };
            var preparedSlot = slot switch { MaterialSlot.UnsungFill => PreparedMaterialSlot.UnsungFill, MaterialSlot.SungFill => PreparedMaterialSlot.SungFill,
                MaterialSlot.Stroke => PreparedMaterialSlot.Stroke, _ => throw new InvalidOperationException("未知材质部位。") };
            if (material is MaterialValue.PackageMaterial package)
                bindings[(target, preparedSlot)] = new(new PreparedMaterialOwner.Script(owner.Definition.Id, owner.Definition.RuntimeHash)
                    { PackageIdentity = owner.Definition.PackageIdentity }, target, preparedSlot, package);
            else bindings.Remove((target, preparedSlot));
        }
    }
    private static void ValidateMaterialOutput(CompiledFrameEffect definition, FrameOutput output)
    {
        foreach (var item in output.TokenStyles.Select(style => (Target: (V2.TextTarget?)null, style.Update))
            .Concat(output.MarkStyles.Select(style => (Target: (V2.TextTarget?)null, style.Update)))
            .Concat(output.BaseStyles.Select(style => (Target: (V2.TextTarget?)new V2.TextTarget.Base(style.Target), style.Update))))
            BaseMaterialAction(item.Target, () =>
            {
                foreach (var edit in item.Update.MaterialEdits)
                    switch (edit)
                    {
                        case MaterialEdit.ReplaceMaterial { Material: MaterialValue.PackageMaterial material } set:
                            ValidateMaterialExport(definition, material.Handle, set.Slot, material.Parameters);
                            break;
                        case MaterialEdit.Uniform uniform:
                            var export = ValidateMaterialExport(definition, uniform.Handle, uniform.Slot, null);
                            MaterialUniformValidation.Validate(definition.PackageDefinition!.ImageGraphs[export.GraphPath].Plan,
                                uniform.NodeId, uniform.Name, uniform.Value);
                            break;
                    }
            });
    }
    private static EffectMaterialDefinition ValidateMaterialExport(CompiledFrameEffect definition, MaterialHandle handle, MaterialSlot slot, ParameterValues? parameters)
    {
        var package = definition.PackageDefinition ?? throw new EffectOutputException("PackageDefinitionMissing", "包材质须有完整固定包定义。");
        if (!definition.Resources.Contains(handle)) throw new EffectOutputException("InvalidMaterialResource", "材质不属于本固定包修订。");
        var export = package.GetMaterial(handle.Identity.ExportId);
        string region = slot == MaterialSlot.Stroke ? "stroke" : "fill";
        if (!export.Regions.Contains(region, StringComparer.Ordinal))
            throw new EffectOutputException("PackageMaterialRegionInvalid", "材质未声明实际写入区域：" + region);
        if (export.Outputs.Any(item => !item.Requires.IsEmpty || !item.DependsOn.IsEmpty))
            throw new EffectOutputException("PackageOutputPlanPending", "材质跨输出执行计划尚未接通。");
        if (package.ImageGraphs[export.GraphPath].Plan.Definition.AnchorMode != GraphAnchorMode.ColorOnly)
            throw new EffectOutputException("PackageGraphMappingPending", "材质实际映射尚未接通。");
        if (parameters is not null) PackageSdkParameters.Validate(package, export, parameters, definition.Resources);
        return export;
    }
}
