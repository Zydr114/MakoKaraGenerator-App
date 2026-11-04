using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Core.Tests;

internal static class SdkV2PackageFixture
{
    internal static JsonObject Manifest(Dictionary<string, byte[]> files)
    {
        var manifest = EffectPackageFixture.Manifest(files);
        manifest["packageVersion"] = EffectPackageProfiles.V2.PackageVersion;
        manifest["sdkVersion"] = EffectPackageProfiles.V2.SdkVersion;
        manifest["shaderProfile"] = EffectPackageProfiles.V2.ShaderProfile;
        manifest["requires"] = new JsonArray("token.affine", "timeline.index", "line.exit");
        manifest["lineExit"] = new JsonObject { ["durationUs"] = 600_000L };
        manifest["emitters"] = new JsonArray();
        manifest["exports"]![0]!["outputs"]![0]!["requires"] = new JsonArray("token.affine");
        return manifest;
    }
    internal static EffectPackageContents Contents(Action<JsonObject>? change = null)
    {
        var files = EffectPackageFixture.Files(); var manifest = Manifest(files); change?.Invoke(manifest);
        return EffectPackageContents.CreateSupported(EffectPackageFixture.Bytes(manifest), EffectPackageFixture.Inputs(files));
    }
}
