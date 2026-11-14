using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting.Compilation;

namespace MakoKaraGenerator.Rendering.Tests;

internal static class V2RenderPackageHelper
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

    internal static EffectPackageContents CreateControllerContents(string source, string[] requires,
        string entryType = "PackEffect", string exportId = "effect", JsonArray? parameters = null,
        JsonArray? outputs = null, string[]? bindingKinds = null, string packageId = "demo.render.v2",
        JsonArray? emitters = null, long lineExitUs = 0, Action<JsonObject>? manifestChange = null)
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
            ["lineExit"] = new JsonObject { ["durationUs"] = lineExitUs },
            ["emitters"] = emitters ?? new JsonArray(),
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
        manifestChange?.Invoke(manifest);
        return EffectPackageContents.CreateSupported(EffectPackageFixture.Bytes(manifest), EffectPackageFixture.Inputs(files));
    }

    internal static async Task<CompiledEffectPackage> CompileAsync(EffectPackageContents contents)
    {
        var compiler = new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
        var compiled = await CompiledEffectPackage.CompileAsync(EffectPackageDefinition.Read(contents), compiler,
            new PackageImageValidator(), new PackageShaderValidator(), new PackageImageGraphValidator(), new PackageMaterialValidator());
        Assert.True(compiled.Succeeded, string.Join('\n', compiled.Artifact.Diagnostics));
        return compiled;
    }

    internal static Task<CompiledEffectPackage> CompileControllerAsync(string source, string[] requires,
        string entryType = "PackEffect", string exportId = "effect", JsonArray? parameters = null,
        JsonArray? outputs = null, string[]? bindingKinds = null, string packageId = "demo.render.v2",
        JsonArray? emitters = null, long lineExitUs = 0, Action<JsonObject>? manifestChange = null)
        => CompileAsync(CreateControllerContents(source, requires, entryType, exportId, parameters, outputs, bindingKinds, packageId, emitters, lineExitUs, manifestChange));

    internal static JsonObject Output(string id, string kind, string target, params string[] requires) => new()
    {
        ["id"] = id,
        ["kind"] = kind,
        ["target"] = target,
        ["requires"] = new JsonArray(requires.Select(static item => JsonValue.Create(item)).ToArray()),
        ["dependsOn"] = new JsonArray(),
    };

    internal static JsonObject EnumParameter(string id, string defaultValue, params string[] values) => new()
    {
        ["id"] = id,
        ["type"] = "enum",
        ["label"] = id,
        ["default"] = defaultValue,
        ["values"] = new JsonArray(values.Select(static item => (JsonNode)new JsonObject { ["value"] = item, ["label"] = item }).ToArray()),
    };

    internal static JsonObject NumberParameter(string id, double defaultValue, double min = -1e6, double max = 1e6) => new()
    {
        ["id"] = id,
        ["type"] = "number",
        ["label"] = id,
        ["default"] = defaultValue,
        ["min"] = min,
        ["max"] = max,
    };

    internal static JsonObject BoolParameter(string id, bool defaultValue = false) => new()
    {
        ["id"] = id,
        ["type"] = "boolean",
        ["label"] = id,
        ["default"] = defaultValue,
    };

    internal static JsonObject TextParameter(string id, string defaultValue = "") => new()
    {
        ["id"] = id,
        ["type"] = "text",
        ["label"] = id,
        ["default"] = defaultValue,
    };

    internal static JsonObject ColorParameter(string id, double r = 1, double g = 0, double b = 0, double a = 0.5) => new()
    {
        ["id"] = id,
        ["type"] = "color",
        ["label"] = id,
        ["default"] = new JsonObject { ["r"] = r, ["g"] = g, ["b"] = b, ["a"] = a },
    };
}
