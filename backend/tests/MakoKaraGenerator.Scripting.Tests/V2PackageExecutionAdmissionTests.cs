using System.Text;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Scripting.Compilation;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class V2PackageExecutionAdmissionTests
{
    [Theory]
    [InlineData("VisualTransform", true)]
    [InlineData("Overlay", false)]
    [InlineData("ImageGraph", false)]
    public async Task v2已支持输出能力进入执行且未实现阶段提前具名拒绝(string kind, bool supported)
    {
        var files = EffectPackageFixture.Files();
        files["src/PackEffect.cs"] = Encoding.UTF8.GetBytes("""
            using MakoKaraGenerator.Scripting.V2;
            public sealed class PackEffect : IPureKaraokeEffect
            {
                public PreparedEffect Declare(DeclareContext source) => PreparedEffect.Empty;
                public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output) { }
            }
            """);
        var manifest = EffectPackageFixture.Manifest(files);
        manifest["packageVersion"] = EffectPackageProfiles.V2.PackageVersion;
        manifest["sdkVersion"] = EffectPackageProfiles.V2.SdkVersion;
        manifest["shaderProfile"] = EffectPackageProfiles.V2.ShaderProfile;
        manifest["requires"] = new JsonArray("token.affine");
        manifest["lineExit"] = new JsonObject { ["durationUs"] = 0 };
        manifest["emitters"] = new JsonArray();
        manifest["exports"]![0]!["outputs"]![0]!["kind"] = kind;
        manifest["exports"]![0]!["outputs"]![0]!["requires"] = supported ? new JsonArray("token.affine") : new JsonArray();
        var package = EffectPackageDefinition.Read(EffectPackageContents.CreateSupported(EffectPackageFixture.Bytes(manifest), EffectPackageFixture.Inputs(files)));
        var compiler = new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
        var compiled = await CompiledEffectPackage.CompileAsync(package, compiler);
        Assert.True(compiled.Succeeded, string.Join('\n', compiled.Artifact.Diagnostics));
        if (supported) Assert.True(compiled.CreateEffect("scale", new("test"), new EffectBinding.Tokens([new("word")]), 0).IsV2);
        else Assert.Equal("PackageWriterPending", Assert.Throws<EffectPackageException>(() =>
            compiled.CreateEffect("scale", new("test"), new EffectBinding.Tokens([new("word")]), 0)).Code);
    }
}
