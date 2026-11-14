using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting.Compilation;

namespace MakoKaraGenerator.Export.Tests;

internal static class V2ExportPackageHelper
{
    internal static EffectPackageContents UpgradeToV2(EffectPackageContents contents, string[] requires, Action<JsonObject>? manifestChange = null)
    {
        var manifest = JsonNode.Parse(contents.Manifest.AsSpan())!.AsObject();
        manifest["packageVersion"] = EffectPackageProfiles.V2.PackageVersion;
        manifest["sdkVersion"] = EffectPackageProfiles.V2.SdkVersion;
        manifest["shaderProfile"] = EffectPackageProfiles.V2.ShaderProfile;
        manifest["requires"] = new JsonArray(requires.Select(static item => JsonValue.Create(item)).ToArray());
        manifest["lineExit"] ??= new JsonObject { ["durationUs"] = 0 };
        manifest["emitters"] ??= new JsonArray();
        manifestChange?.Invoke(manifest);
        return EffectPackageContents.CreateSupported(EffectPackageFixture.Bytes(manifest),
            contents.Files.Select(static file => KeyValuePair.Create(file.Path, (ReadOnlyMemory<byte>)file.Content.ToArray())));
    }

    internal static async Task<CompiledEffectPackage> CompileControllerAsync(string source, string[] requires,
        string entryType = "PackEffect", string exportId = "effect", JsonArray? parameters = null,
        JsonArray? outputs = null, string[]? bindingKinds = null, string packageId = "demo.export.v2")
    {
        var sourceBytes = Encoding.UTF8.GetBytes(source);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [$"src/{entryType}.cs"] = sourceBytes,
        };
        var fileEntries = new JsonArray(new JsonObject
        {
            ["path"] = $"src/{entryType}.cs",
            ["role"] = "csharp",
            ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(sourceBytes)),
        });
        var manifest = new JsonObject
        {
            ["packageVersion"] = EffectPackageProfiles.V2.PackageVersion,
            ["sdkVersion"] = EffectPackageProfiles.V2.SdkVersion,
            ["shaderProfile"] = EffectPackageProfiles.V2.ShaderProfile,
            ["packageId"] = packageId,
            ["name"] = packageId,
            ["version"] = "1.0.0",
            ["requires"] = new JsonArray(requires.Select(static item => JsonValue.Create(item)).ToArray()),
            ["lineExit"] = new JsonObject { ["durationUs"] = 0 },
            ["emitters"] = new JsonArray(),
            ["files"] = fileEntries,
            ["parameters"] = parameters ?? new JsonArray(),
            ["exports"] = new JsonArray(new JsonObject
            {
                ["id"] = exportId,
                ["kind"] = "controller",
                ["name"] = exportId,
                ["entryType"] = entryType,
                ["parameters"] = new JsonArray((parameters ?? new JsonArray()).Select(item => JsonValue.Create(item!["id"]!.GetValue<string>())).ToArray()),
                ["bindingKinds"] = new JsonArray((bindingKinds ?? ["project", "node", "branch", "lines", "tokens", "clips"]).Select(static item => JsonValue.Create(item)).ToArray()),
                ["outputs"] = outputs ?? new JsonArray(),
            }),
        };
        var contents = EffectPackageContents.CreateSupported(EffectPackageFixture.Bytes(manifest), EffectPackageFixture.Inputs(files));
        var compiler = new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
        var compiled = await CompiledEffectPackage.CompileAsync(EffectPackageDefinition.Read(contents), compiler,
            new PackageImageValidator(), new PackageShaderValidator(), new PackageImageGraphValidator(), new PackageMaterialValidator());
        Assert.True(compiled.Succeeded, string.Join('\n', compiled.Artifact.Diagnostics));
        return compiled;
    }
}
