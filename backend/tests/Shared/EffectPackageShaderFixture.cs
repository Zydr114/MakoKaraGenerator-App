using System.Text;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.TestSupport;

internal static class EffectPackageShaderFixture
{
    public const string ProgramPath = "shaders/probe.program.json";
    public const string SourcePath = "shaders/probe.sksl";
    public const string Source = "uniform float amount; half4 main(float2 p) { return half4(amount,0,0,1); }";
    public static JsonObject Program() => new()
    {
        ["programVersion"] = EffectPackageShaderPrograms.CurrentVersion, ["source"] = SourcePath,
        ["profile"] = ShaderProgramDefinition.RasterProfile,
        ["uniforms"] = new JsonArray(new JsonObject { ["name"] = "amount", ["type"] = "float" }), ["children"] = new JsonArray(),
    };
    public static EffectPackageContents Contents(JsonObject? program = null, string? source = null,
        Action<JsonObject>? changeManifest = null, Dictionary<string, byte[]>? extraFiles = null)
    {
        var files = EffectPackageFixture.Files();
        files[ProgramPath] = EffectPackageFixture.Bytes(program ?? Program());
        files[SourcePath] = Encoding.UTF8.GetBytes(source ?? Source);
        if (extraFiles is not null) foreach (var pair in extraFiles) files.Add(pair.Key, pair.Value);
        var manifest = Manifest(files); changeManifest?.Invoke(manifest);
        return EffectPackageContents.Create(EffectPackageFixture.Bytes(manifest), EffectPackageFixture.Inputs(files), EffectPackageFixture.Profile);
    }
    public static JsonObject Manifest(Dictionary<string, byte[]> files)
    {
        var manifest = EffectPackageFixture.Manifest(files);
        foreach (var item in manifest["files"]!.AsArray())
        {
            string path = item!["path"]!.GetValue<string>();
            if (path.EndsWith(".program.json", StringComparison.Ordinal)) item["role"] = "shaderProgram";
            if (path.EndsWith(".sksl", StringComparison.Ordinal)) item["role"] = "shader";
        }
        return manifest;
    }
}
