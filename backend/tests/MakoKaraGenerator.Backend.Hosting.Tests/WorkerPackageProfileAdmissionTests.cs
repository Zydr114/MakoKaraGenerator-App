using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class WorkerPackageProfileAdmissionTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("v1-only")]
    [InlineData("v2-only")]
    [InlineData("future-sdk")]
    [InlineData("wrong-shader")]
    public void 新Host要求实际Worker同时部署精确v1与当前v2合同(string damage)
    {
        var current = Current();
        var profiles = WorkerPackageProfileAdmission.Current;
        var invalid = current with { SupportedPackageProfiles = damage switch
        {
            "missing" => null,
            "v1-only" => [profiles[0]],
            "v2-only" => [profiles[1]],
            "future-sdk" => [profiles[0], profiles[1] with { SdkVersion = "2.1.0" }],
            "wrong-shader" => [profiles[0], profiles[1] with { ShaderProfile = EffectPackageProfiles.V1.ShaderProfile }],
            _ => throw new ArgumentException(damage),
        } };
        WorkerPackageProfileAdmission.RequireCurrent(current, "WorkerPackageUnavailable");
        var error = Assert.Throws<WorkerProtocolException>(() => WorkerPackageProfileAdmission.RequireCurrent(invalid, "WorkerPackageUnavailable"));
        Assert.Equal("WorkerPackageUnavailable", error.Code);
    }

    [Fact]
    public void 固定输入核验精确包身份并允许当前SDK补丁()
    {
        var current = Current();
        EffectPackageProfile patch = EffectPackageProfiles.V2 with { SdkVersion = "2.0.7" };
        WorkerPackageProfileAdmission.RequireInput(current, [EffectPackageProfiles.V1, EffectPackageProfiles.V2, patch]);
        foreach (var profile in new[]
        {
            EffectPackageProfiles.V1 with { SdkVersion = "1.0.1" },
            EffectPackageProfiles.V2 with { SdkVersion = "2.1.0" },
            EffectPackageProfiles.V2 with { ShaderProfile = EffectPackageProfiles.V1.ShaderProfile },
        })
            Assert.Equal("WorkerPackageProfileMismatch", Assert.Throws<WorkerProtocolException>(() =>
                WorkerPackageProfileAdmission.RequireInput(current, [profile])).Code);
        Assert.Equal("WorkerPackageProfileMismatch", Assert.Throws<WorkerProtocolException>(() =>
            WorkerPackageProfileAdmission.RequireInput(current with { SupportedPackageProfiles = [WorkerPackageProfileAdmission.Current[0]] }, [patch])).Code);
    }

    private static PackageRenderWorkerCapabilities Current() => new("engine", PackageProjectSnapshotCodec.Profile,
        EffectPackageProfiles.V1.PackageVersion, EffectPackageProfiles.V1.SdkVersion, EffectPackageProfiles.V1.ShaderProfile,
        new string('a', 64), 1, SupportedPackageProfiles: WorkerPackageProfileAdmission.Current);
}
