using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Scripting.Compilation;

namespace MakoKaraGenerator.Scripting.Execution;

internal sealed partial class FrameEffectHost
{
    private static void ValidatePackageOutput(CompiledFrameEffect definition, FrameOutput output)
    {
        ValidateMaterialOutput(definition, output);
        if (definition.PackageDeclaration is not { } declaration) return;
        void Require(bool hasOutput, string kind, string target)
        {
            if (hasOutput && !declaration.Outputs.Any(item => item.Kind == kind && item.Target == target))
                throw new EffectOutputException("UndeclaredPackageOutput", "输出类别未在固定包导出中声明：" + kind + "/" + target);
        }
        Require(!output.TokenTransforms.IsEmpty, "VisualTransform", "tokenFamily");
        Require(!output.BaseTransforms.IsEmpty, "VisualTransform", "baseToken");
        Require(!output.ClusterTransforms.IsEmpty, "VisualTransform", "cluster");
        Require(!output.LineTransforms.IsEmpty, "VisualTransform", "line");
        Require(!output.GroupTransforms.IsEmpty, "VisualTransform", "group");
        Require(!output.MarkTransforms.IsEmpty, "VisualTransform", "textMark");
        Require(!output.TokenStyles.IsEmpty, "StyleModulation", "tokenFamily");
        Require(!output.BaseStyles.IsEmpty, "StyleModulation", "baseToken");
        Require(!output.MarkStyles.IsEmpty, "StyleModulation", "textMark");
        Require(!output.GroupOpacities.IsEmpty, "StyleModulation", "group");
        foreach (var binding in output.ImageEffects)
        {
            string target = binding.Target switch { ImageEffectTarget.Token => "tokenFamily", ImageEffectTarget.Line => "line", ImageEffectTarget.Mark => "textMark", ImageEffectTarget.Group => "group",
                _ => throw new EffectOutputException("UndeclaredPackageOutput", "未知图输出目标。") };
            Require(true, "ImageGraph", target);
            var package = definition.PackageDefinition ?? throw new EffectOutputException("PackageDefinitionMissing", "真实图绑定须有完整固定包定义。");
            var graph = package.GetGraph(binding.Graph.Identity.ExportId);
            if (!graph.BindingKinds.Contains(PackageSdkParameters.BindingKind(definition.Binding), StringComparer.Ordinal))
                throw new EffectOutputException("PackageBindingNotAllowed", "图导出未允许该实例绑定类别。");
            var declared = graph.Outputs.Where(item => item.Target == target).ToArray();
            if (declared.Length == 0) throw new EffectOutputException("UndeclaredPackageOutput", "图导出未声明本次真实目标类别。");
            if (declared.Length != 1 || declaration.Outputs.Count(item => item.Kind == "ImageGraph" && item.Target == target) != 1)
                throw new EffectOutputException("PackageOutputPlanPending", "多个同类别具名输出须接入完整执行计划，不能猜测。");
            if (!declared[0].Requires.IsEmpty || !declared[0].DependsOn.IsEmpty)
                throw new EffectOutputException("PackageOutputPlanPending", "图导出的额外能力／跨输出执行计划尚未接通。");
            var controllerOutput = declaration.Outputs.Single(item => item.Kind == "ImageGraph" && item.Target == target);
            if (controllerOutput.Bypass is not null && controllerOutput.Bypass != declared[0].Bypass)
                throw new EffectOutputException("PackageGraphBypassMismatch", "控制器旁路与本次绑定的真实图导出不一致。");
            if (package.ImageGraphs[graph.GraphPath].Plan.Definition.AnchorMode != GraphAnchorMode.ColorOnly)
                throw new EffectOutputException("PackageGraphMappingPending", "非 ColorOnly 图仍需实际映射／光标协议，不能冒充恒等。");
            PackageSdkParameters.Validate(package, graph, binding.Parameters, definition.Resources);
        }
    }
}
