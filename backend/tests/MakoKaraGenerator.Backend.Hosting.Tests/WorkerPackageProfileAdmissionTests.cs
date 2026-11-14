using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class WorkerPackageProfileAdmissionTests
{
    private static readonly EffectPackageProfile LegacyV1 = new(2, "1.0.0", ShaderProgramDefinition.RasterProfile);
    private static readonly WorkerPackageProfileDto LegacyV1Dto = new(LegacyV1.PackageVersion, LegacyV1.SdkVersion, LegacyV1.ShaderProfile);

    [Theory]
    [InlineData("missing")]
    [InlineData("v1-only")]
    [InlineData("future-sdk")]
    [InlineData("wrong-shader")]
    public void 新Host要求实际Worker部署精确v2合同并拒绝仅v1或错配能力(string damage)
    {
        var current = Current();
        var v2 = Assert.Single(WorkerPackageProfileAdmission.Current);
        var invalid = current with { SupportedPackageProfiles = damage switch
        {
            "missing" => null,
            "v1-only" => [LegacyV1Dto],
            "future-sdk" => [v2 with { SdkVersion = "2.1.0" }],
            "wrong-shader" => [v2 with { ShaderProfile = LegacyV1.ShaderProfile }],
            _ => throw new ArgumentException(damage),
        } };
        WorkerPackageProfileAdmission.RequireCurrent(current, "WorkerPackageUnavailable");
        var error = Assert.Throws<WorkerProtocolException>(() => WorkerPackageProfileAdmission.RequireCurrent(invalid, "WorkerPackageUnavailable"));
        Assert.Equal("WorkerPackageUnavailable", error.Code);
    }

    [Fact]
    public void 固定输入核验精确v2包身份拒绝v1并允许当前SDK补丁()
    {
        var current = Current();
        EffectPackageProfile patch = EffectPackageProfiles.V2 with { SdkVersion = "2.0.7" };
        WorkerPackageProfileAdmission.RequireInput(current, [EffectPackageProfiles.V2, patch]);
        foreach (var profile in new[]
        {
            LegacyV1,
            LegacyV1 with { SdkVersion = "1.0.1" },
            EffectPackageProfiles.V2 with { SdkVersion = "2.1.0" },
            EffectPackageProfiles.V2 with { ShaderProfile = LegacyV1.ShaderProfile },
        })
            Assert.Equal("WorkerPackageProfileMismatch", Assert.Throws<WorkerProtocolException>(() =>
                WorkerPackageProfileAdmission.RequireInput(current, [profile])).Code);
        Assert.Equal("WorkerPackageProfileMismatch", Assert.Throws<WorkerProtocolException>(() =>
            WorkerPackageProfileAdmission.RequireInput(current with { SupportedPackageProfiles = [LegacyV1Dto] }, [patch])).Code);
    }

    private static PackageRenderWorkerCapabilities Current() => new("engine", PackageProjectSnapshotCodec.Profile,
        EffectPackageProfiles.V2.PackageVersion, EffectPackageProfiles.V2.SdkVersion, EffectPackageProfiles.V2.ShaderProfile,
        new string('a', 64), 1, SupportedPackageProfiles: WorkerPackageProfileAdmission.Current);
}
