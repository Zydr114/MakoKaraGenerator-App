using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Rendering;

/// <summary>从实际启用实例和源材质闭包判定渲染设备要求；未使用的包不剥夺无效果 CPU 能力。</summary>
public static class ScriptBackendRequirement
{
    /// <summary>固定快照实际使用 SDK v2 时要求 Vulkan，不根据目录中闲置的包判断。</summary>
    public static bool RequiresVulkan(ProjectSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var references = snapshot.Effects.Instances.Where(static instance => instance.Enabled)
            .Select(static instance => (instance.PackageId, instance.Revision))
            .Concat(SourceMaterialCatalog.CollectForRuntime(snapshot.LyricStyle, snapshot.Lyrics, snapshot.LyricPresentation,
                snapshot.PresetApplications).Select(static reference => (reference.Material.PackageId, reference.Material.Revision)));
        return references.Distinct().Any(key => EffectPackageProfiles.IsV2(snapshot.Effects.GetPackage(key.PackageId, key.Revision).Profile));
    }

    /// <summary>精确拒绝不支持的设备；调用方仍必须核验实际创建设备身份。</summary>
    public static void RequireSupported(ProjectSnapshot snapshot, bool isVulkan)
    {
        if (!isVulkan && RequiresVulkan(snapshot))
            throw new EffectPackageException("PackageBackendUnsupported", "renderBackend", "SDK v2 效果仅支持 Vulkan；请显式选择 GPU 后端。");
    }
}
