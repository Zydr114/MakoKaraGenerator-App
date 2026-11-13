using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

/// <summary>给工程加一个启用的 SDK v2 实例，用来核对工程级任务在未选 GPU 时的拒绝（M13 C1）。</summary>
internal static class SdkV2ProjectFixture
{
    internal static Project WithEnabledV2Instance(Project project)
    {
        var files = EffectPackageFixture.Files(); var manifest = EffectPackageFixture.Manifest(files);
        manifest["packageVersion"] = EffectPackageProfiles.V2.PackageVersion;
        manifest["sdkVersion"] = EffectPackageProfiles.V2.SdkVersion;
        manifest["shaderProfile"] = EffectPackageProfiles.V2.ShaderProfile;
        manifest["requires"] = new JsonArray("token.affine");
        manifest["lineExit"] = new JsonObject { ["durationUs"] = 0 };
        manifest["emitters"] = new JsonArray();
        var package = EffectPackageContents.CreateSupported(EffectPackageFixture.Bytes(manifest), EffectPackageFixture.Inputs(files));
        var instance = new ProjectEffectInstance
        {
            Id = "v2-instance", Name = "纯求值", PackageId = package.PackageId, Revision = package.Revision, ExportId = "scale",
            Binding = new(ProjectEffectBindingKind.Tokens, ids: ["missing-token"]), Subscription = new("project"),
        };
        return project with { Effects = new([package], [instance]) };
    }
}
