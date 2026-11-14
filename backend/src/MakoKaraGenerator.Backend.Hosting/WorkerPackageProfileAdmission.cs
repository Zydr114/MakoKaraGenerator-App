using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Backend.Hosting;

internal static class WorkerPackageProfileAdmission
{
    internal static WorkerPackageProfileDto[] Current => [From(EffectPackageProfiles.V2)];
    internal static void RequireCurrent(PackageRenderWorkerCapabilities capabilities, string failureCode)
    {
        if (capabilities.SupportedPackageProfiles is not { } supported
            || !Current.All(profile => supported.Contains(profile)))
            throw new WorkerProtocolException("实际 Worker 未声明当前 v2 包与 SDK 部署能力。", failureCode);
    }
    internal static void RequireInput(PackageRenderWorkerCapabilities capabilities, IEnumerable<EffectPackageProfile> required)
    {
        var supported = capabilities.SupportedPackageProfiles;
        foreach (var profile in required)
        {
            if (!EffectPackageProfiles.IsSupported(profile) || supported is null || !supported.Any(value => Matches(value, profile)))
                throw new WorkerProtocolException("实际 Worker 未声明固定输入所需的包／SDK／shader 身份。", "WorkerPackageProfileMismatch");
        }
    }
    private static bool Matches(WorkerPackageProfileDto supported, EffectPackageProfile required)
    {
        // Core 已严格检查必需版本；当前 SDK2 次版本为 0，补丁不改变公开合同。
        return EffectPackageProfiles.IsSupported(required) && supported == From(EffectPackageProfiles.V2);
    }
    private static WorkerPackageProfileDto From(EffectPackageProfile profile)
        => new(profile.PackageVersion, profile.SdkVersion, profile.ShaderProfile);
}
