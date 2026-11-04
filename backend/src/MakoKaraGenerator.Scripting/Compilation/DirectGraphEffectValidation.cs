using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Scripting.Compilation;

/// <summary>直接图进入现有生产 writer 前的固定能力门禁，不建立跨输出或组捕获计划。</summary>
internal static class DirectGraphEffectValidation
{
    internal static void Validate(EffectPackageDefinition package, EffectGraphDefinition graph, EffectBinding binding)
    {
        string path = "exports." + graph.Id;
        bool v2 = EffectPackageProfiles.IsV2(package.Contents.Profile);
        if (v2 && !package.Contents.Requires.Contains("image.graph", StringComparer.Ordinal))
            throw new EffectPackageException("PackageCapabilityMissing", path, "v2 直接图必须声明 image.graph。");
        if (!graph.BindingKinds.Contains(PackageSdkParameters.BindingKind(binding), StringComparer.Ordinal))
            throw new EffectPackageException("PackageBindingNotAllowed", path + ".bindingKinds", "图导出未允许此绑定类别。");
        if (graph.Outputs.Any(output => output.Target == "group"))
            throw new EffectPackageException("GroupImageEffectUnsupported", path + ".outputs", "当前正式画布不支持父组图像效果。");
        if (graph.Outputs.IsEmpty || graph.Outputs.Any(output => output.Kind != "ImageGraph"
                || output.Target is not ("tokenFamily" or "line" or "textMark")))
            throw new EffectPackageException("PackageWriterPending", path + ".outputs", "当前直接图只支持元素族、整行或提示标记图输出。");
        if (graph.Outputs.Any(output => (v2 ? output.Requires.Any(requirement => requirement != "image.graph") : !output.Requires.IsEmpty) || !output.DependsOn.IsEmpty)
            || graph.Outputs.GroupBy(output => output.Target, StringComparer.Ordinal).Any(outputs => outputs.Count() != 1))
            throw new EffectPackageException("PackageOutputPlanPending", path + ".outputs", "额外能力、跨输出及同类别多输出执行计划尚未接通。");
        if (v2 && binding is EffectBinding.Tokens && graph.Outputs.Any(output => output.Target == "line"))
            throw new EffectPackageException("PackageBindingNotAllowed", path + ".outputs", "token 绑定不能扩大到整行图输出。");
        if (package.ImageGraphs[graph.GraphPath].Plan.Definition.AnchorMode != GraphAnchorMode.ColorOnly)
            throw new EffectPackageException("PackageGraphMappingPending", path + ".graph", "非 ColorOnly 图仍需实际映射／光标协议，不能冒充恒等。");
    }
}
