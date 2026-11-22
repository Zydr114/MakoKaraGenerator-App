using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting.Compilation;

namespace MakoKaraGenerator.Scripting.Execution;

internal sealed partial class FrameEffectHost
{
    private static void ValidatePackageOutput(CompiledFrameEffect definition, FrameOutput output, CancellationToken token = default)
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
        foreach (var binding in output.ImageEffects) ValidatePackageGraphBinding(definition, binding, true, token);
    }

    private static void ValidatePackageGraphBinding(CompiledFrameEffect definition, ImageEffectBinding binding,
        bool validateInstalledNodes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var declaration = definition.PackageDeclaration
            ?? throw new EffectOutputException("PackageDefinitionMissing", "真实图绑定须有固定包导出声明。");
        if (!definition.Resources.Contains(binding.Graph))
            throw new EffectOutputException("InvalidGraphResource", "图导出不属于本实例固定包修订。");
        string target = binding.Target switch { ImageEffectTarget.Token => "tokenFamily", ImageEffectTarget.Line => "line", ImageEffectTarget.Mark => "textMark", ImageEffectTarget.Group => "group",
            _ => throw new EffectOutputException("UndeclaredPackageOutput", "未知图输出目标。") };
        if (!declaration.Outputs.Any(item => item.Kind == "ImageGraph" && item.Target == target))
            throw new EffectOutputException("UndeclaredPackageOutput", "图目标未在固定包控制器输出中声明：" + target);
        var package = definition.PackageDefinition ?? throw new EffectOutputException("PackageDefinitionMissing", "真实图绑定须有完整固定包定义。");
        if (!package.Contents.Requires.Contains(V2.CapabilityNames.ImageGraph, StringComparer.Ordinal))
            throw new EffectOutputException("UndeclaredCapability", "包未声明能力：image.graph");
        var graph = package.GetGraph(binding.Graph.Identity.ExportId);
        if (!graph.BindingKinds.Contains(PackageSdkParameters.BindingKind(definition.Binding), StringComparer.Ordinal))
            throw new EffectOutputException("PackageBindingNotAllowed", "图导出未允许该实例绑定类别。");
        var declared = graph.Outputs.Where(item => item.Kind == "ImageGraph" && item.Target == target).ToArray();
        if (declared.Length == 0) throw new EffectOutputException("UndeclaredPackageOutput", "图导出未声明本次真实目标类别。");
        if (declared.Length != 1 || declaration.Outputs.Count(item => item.Kind == "ImageGraph" && item.Target == target) != 1)
            throw new EffectOutputException("PackageOutputPlanPending", "多个同类别具名输出须接入完整执行计划，不能猜测。");
        bool RequiresPending(EffectOutputDefinition output) => output.Requires.Any(requirement => !GroupGraphEffectAdmission.IsAllowedRequirement(output, requirement));
        var controllerOutput = declaration.Outputs.Single(item => item.Kind == "ImageGraph" && item.Target == target);
        if (binding.Target is ImageEffectTarget.Group)
        {
            try
            {
                GroupGraphEffectAdmission.Validate(package, controllerOutput, definition.Binding, "exports." + declaration.Id);
                GroupGraphEffectAdmission.Validate(package, declared[0], definition.Binding, "exports." + graph.Id);
            }
            catch (EffectPackageException error) { throw new EffectOutputException(error.Code, error.Message); }
        }
        if (RequiresPending(declared[0]) || !declared[0].DependsOn.IsEmpty
            || RequiresPending(controllerOutput) || !controllerOutput.DependsOn.IsEmpty)
            throw new EffectOutputException("PackageOutputPlanPending", "图导出或控制器的额外能力／跨输出执行计划尚未接通。");
        if (controllerOutput.Bypass is not null && controllerOutput.Bypass != declared[0].Bypass)
            throw new EffectOutputException("PackageGraphBypassMismatch", "控制器旁路与本次绑定的真实图导出不一致。");
        var plan = package.ImageGraphs[graph.GraphPath].Plan;
        if (plan.Definition.AnchorMode != GraphAnchorMode.ColorOnly)
            throw new EffectOutputException("PackageGraphMappingPending", "非 ColorOnly 图仍需实际映射／光标协议，不能冒充恒等。");
        PackageSdkParameters.Validate(package, graph, binding.Parameters, definition.Resources);
        if (!validateInstalledNodes) return;
        if (binding.Uniforms is null || binding.Transforms is null)
            throw new EffectOutputException("InvalidGraphBindingValue", "两类图覆盖必须是完整不可变值集。");
        MaterialUniformValidation.Validate(plan, binding.Uniforms);
        foreach (var pair in binding.Transforms.Values)
        {
            token.ThrowIfCancellationRequested();
            var node = plan.Nodes.FirstOrDefault(node => node.Id == pair.Key);
            if (node is null) throw new EffectOutputException("GraphTransformNodeUnknown", "覆盖节点未在最终固定图中声明：" + pair.Key);
            if (node is not ImageGraphNode.Transform)
                throw new EffectOutputException("GraphTransformNodeTypeMismatch", "覆盖只能指向最终固定图中的 Transform 节点：" + pair.Key);
        }
        // Matrix/ROI/inverse sampling at actual output density is checked by the
        // G1 resolver, at both entry points and again before graph allocation.
    }
}
