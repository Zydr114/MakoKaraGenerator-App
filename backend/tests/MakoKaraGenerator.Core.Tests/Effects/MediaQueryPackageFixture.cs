using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Core.Tests.Effects;

internal static class MediaQueryPackageFixture
{
    internal static JsonObject Query(string id = "query", long? literal = null, string space = "canvasPixels") => new()
    {
        ["id"] = id, ["space"] = space,
        ["window"] = literal is { } duration ? new JsonObject { ["kind"] = "literal", ["durationUs"] = duration }
            : new JsonObject { ["kind"] = "parameter", ["parameter"] = "window" },
    };
    internal static JsonObject Reference(string query = "query", string kind = "authorBranch", params string[] outputs)
    {
        if (outputs.Length == 0) outputs = ["scale"];
        JsonObject State() => new() { ["kind"] = kind, ["outputs"] = new JsonArray([.. outputs.Select(id => (JsonNode?)JsonValue.Create(id))]) };
        return new() { ["query"] = query, ["fallback"] = new JsonObject { ["pending"] = State(), ["unavailable"] = State(), ["noMedia"] = State() } };
    }
    internal static void AddQueries(JsonObject manifest)
    {
        manifest["packageVersion"] = EffectPackageProfiles.V2.PackageVersion;
        manifest["sdkVersion"] = EffectPackageProfiles.V2.SdkVersion;
        manifest["shaderProfile"] = EffectPackageProfiles.V2.ShaderProfile;
        manifest["requires"] = new JsonArray("media.features", "token.affine", "image.graph");
        manifest["lineExit"] = new JsonObject { ["durationUs"] = 0 }; manifest["emitters"] = new JsonArray();
        manifest["parameters"]!.AsArray().Add(new JsonObject { ["id"] = "window", ["type"] = "duration", ["label"] = "窗口", ["default"] = 200_000L, ["min"] = 0, ["max"] = 2_000_000L });
        manifest["mediaQueries"] = new JsonArray(Query());
        foreach (var export in manifest["exports"]!.AsArray())
        {
            export!["parameters"]!.AsArray().Add("window");
            string kind = export["kind"]!.GetValue<string>() == "controller" ? "authorBranch" : "outputBypass";
            string[] outputs = [.. export["outputs"]!.AsArray().Select(output => output!["id"]!.GetValue<string>())];
            export["mediaQueries"] = new JsonArray(Reference("query", kind, outputs));
        }
    }
    internal static EffectPackageContents Controller(Action<JsonObject>? change = null)
    {
        var files = EffectPackageFixture.Files(); var manifest = SdkV2PackageFixture.Manifest(files);
        AddQueries(manifest); change?.Invoke(manifest);
        return EffectPackageContents.CreateSupported(EffectPackageFixture.Bytes(manifest), EffectPackageFixture.Inputs(files));
    }
    internal static EffectPackageContents PureMaterial(Action<JsonObject>? change = null, Action<JsonObject>? graphChange = null)
    {
        var old = MixedEffectPackageFixture.Contents(pure: true, background: true, graphChange: graphChange);
        var manifest = JsonNode.Parse(old.Manifest.AsSpan())!.AsObject(); AddQueries(manifest); change?.Invoke(manifest);
        return Recreate(old, manifest);
    }
    internal static EffectPackageContents Recreate(EffectPackageContents package, JsonObject manifest) =>
        EffectPackageContents.CreateSupported(EffectPackageFixture.Bytes(manifest),
            package.Files.Select(file => KeyValuePair.Create(file.Path, (ReadOnlyMemory<byte>)file.Content.ToArray())));
    internal static ProjectEffectInstance Instance(EffectPackageContents package, string id = "instance", string parameters = "{}", string export = "scale") => new()
    {
        Id = id, Name = id, PackageId = package.PackageId, Revision = package.Revision, ExportId = export,
        Binding = new(ProjectEffectBindingKind.Tokens, ids: ["token"]), Parameters = EffectParameterOverrides.Parse(parameters),
    };
    internal static Project Project(EffectPackageContents[] packages, ProjectEffectInstance[]? instances = null,
        EffectPackageContents? source = null, string sourceParameters = "{}") => MakoKaraGenerator.Core.Projects.Project.Create("媒体依赖") with
    {
        Effects = new(packages, instances ?? []),
        LyricStyle = source is null ? LyricStyle.Default : LyricStyle.Default with
        {
            UnsungFill = new() { Material = new MaterialSpec.PackageMaterial(source.PackageId, source.Revision, "ink", EffectParameterOverrides.Parse(sourceParameters)) },
        },
    };
}
