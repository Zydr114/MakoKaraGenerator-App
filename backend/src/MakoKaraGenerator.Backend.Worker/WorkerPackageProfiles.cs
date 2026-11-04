using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Backend.Worker;

internal static class WorkerPackageProfiles
{
    internal static WorkerPackageProfileDto[] Current => [From(EffectPackageProfiles.V1), From(EffectPackageProfiles.V2)];
    private static WorkerPackageProfileDto From(EffectPackageProfile profile)
        => new(profile.PackageVersion, profile.SdkVersion, profile.ShaderProfile);
}
