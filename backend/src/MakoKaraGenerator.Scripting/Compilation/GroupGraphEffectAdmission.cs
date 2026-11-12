using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Scripting.Compilation;

// One narrow admission rule shared by CreateEffect and final graph bindings.
// Runtime source identity and Node ancestry are independently enforced by the
// trusted host writableGroups list and PreparedRenderFrame preflight.
internal static class GroupGraphEffectAdmission
{
    internal const string Capability = V2.CapabilityNames.ImageGraphGroup;
    internal static bool IsAllowedRequirement(EffectOutputDefinition output, string requirement)
        => requirement == V2.CapabilityNames.ImageGraph || output.Kind == "ImageGraph" && output.Target == "group" && requirement == Capability;

    internal static void Validate(EffectPackageDefinition package, EffectOutputDefinition output, EffectBinding binding, string path)
    {
        if (!EffectPackageProfiles.IsV2(package.Contents.Profile)
            || !package.Contents.Requires.Contains(Capability, StringComparer.Ordinal))
            throw new EffectPackageException("GroupImageEffectUnsupported", path, "源组图须使用 v3／SDK v2 并显式声明 image.graph.group。");
        if (!package.Contents.Requires.Contains(V2.CapabilityNames.ImageGraph, StringComparer.Ordinal))
            throw new EffectPackageException("PackageCapabilityMissing", path, "image.graph.group 必须同时声明 image.graph。");
        if (output.Kind != "ImageGraph" || output.Target != "group")
            throw new EffectPackageException("UndeclaredPackageOutput", path, "组图须声明真实 ImageGraph/group 输出。");
        if (!output.Requires.Contains(Capability, StringComparer.Ordinal))
            throw new EffectPackageException("PackageCapabilityMissing", path + ".requires", "组图输出必须显式声明 image.graph.group。");
        if (output.Requires.Any(requirement => !IsAllowedRequirement(output, requirement)) || !output.DependsOn.IsEmpty)
            throw new EffectPackageException("PackageOutputPlanPending", path, "组图仅接通本输出的 ColorOnly 计划；额外能力和跨输出依赖仍被拒绝。");
        if (binding is not (EffectBinding.Project or EffectBinding.Node))
            throw new EffectPackageException("PackageBindingNotAllowed", path, "组图写权只由 Project 或显式 Node 的真实运行父树范围授予。");
    }
}
