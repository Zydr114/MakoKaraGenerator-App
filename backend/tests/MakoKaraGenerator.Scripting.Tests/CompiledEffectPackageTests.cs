using System.Text;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Scripting.Compilation;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class CompiledEffectPackageTests
{
    private static readonly Lazy<Task<CompiledEffectPackage>> Word = new(() => CompiledEffectPackage.CompileAsync(V2PackageTestHelper.WordEmphasis(), Compiler()));
    private static ScriptCompiler Compiler() => new(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
    private static EffectPackageDefinition Read(Dictionary<string, byte[]> files, JsonObject manifest)
        => EffectPackageDefinition.Read(EffectPackageContents.Create(V2PackageTestHelper.Bytes(manifest), V2PackageTestHelper.Inputs(files), EffectPackageProfiles.V2));
    [Fact]
    public async Task 真正样例清单默认值覆盖入口绑定及种子进入生产帧描述()
    {
        var compiled = await Word.Value; Assert.True(compiled.Artifact.Succeeded);
        var effect = compiled.CreateEffect("word-emphasis", new("word"), new EffectBinding.Project(), 123);
        Assert.Equal("WordEmphasis", effect.EntryType); Assert.Equal(1.25, effect.Parameters.GetNumber("peakScale"));
        Assert.Equal(120_000, effect.Parameters.GetDuration("returnUs")); Assert.Equal(123UL, effect.Seed);
        Assert.Equal(compiled.Definition.Contents.Revision, effect.Resources.Revision); Assert.Same(compiled.Artifact, effect.Artifact);
        Assert.Equal(compiled.Artifact.ArtifactHash, effect.RuntimeHash);
        Assert.Equal(EffectCapabilities.Transforms, effect.Capabilities); Assert.NotNull(effect.PackageDeclaration);
        var changed = compiled.CreateEffect("word-emphasis", new("word"), new EffectBinding.Project(), 123, "{\"peakScale\":1.8}"u8.ToArray());
        Assert.Equal(1.8, changed.Parameters.GetNumber("peakScale")); Assert.Same(effect.Artifact, changed.Artifact);
        Assert.Equal("PackageParameterOutOfRange", Assert.Throws<EffectPackageException>(() => compiled.CreateEffect("word-emphasis", new("word"), new EffectBinding.Project(), 0, "{\"peakScale\":9}"u8.ToArray())).Code);
    }
    [Theory]
    [InlineData("binding", "PackageBindingNotAllowed")] [InlineData("writer", "PackageWriterPending")]
    [InlineData("requirement", "PackageCapabilityUnsupported")] [InlineData("dependency", "PackageOutputPlanPending")]
    public async Task 清单不扩大绑定范围也不伪造未实施writer及输出执行计划(string change, string code)
    {
        var files = V2PackageTestHelper.Files(); var manifest = V2PackageTestHelper.Manifest(files); var output = manifest["exports"]![0]!["outputs"]![0]!;
        if (change == "writer") { output["target"] = "line"; output["kind"] = "StyleModulation"; }
        if (change == "requirement")
        {
            output["requires"] = new JsonArray("DistanceField");
            Assert.Equal(code, Assert.Throws<EffectPackageException>(() => Read(files, manifest)).Code);
            return;
        }
        if (change == "dependency")
        {
            var other = output.DeepClone(); other["id"] = "parent"; manifest["exports"]![0]!["outputs"]!.AsArray().Add(other); output["dependsOn"] = new JsonArray("parent");
        }
        var package = await CompiledEffectPackage.CompileAsync(Read(files, manifest), Compiler());
        Assert.Equal(code, Assert.Throws<EffectPackageException>(() => package.CreateEffect("scale", new("instance"),
            change == "binding" ? new EffectBinding.Project() : new EffectBinding.Tokens([new("word")]), 0)).Code);
    }
    [Theory]
    [InlineData("Missing")]
    [InlineData("using MakoKaraGenerator.Scripting.V2; public abstract class PackEffect : IPureKaraokeEffect { public PreparedEffect Declare(DeclareContext s) => PreparedEffect.Empty; public void Evaluate(in TextFrame f, PreparedEffect d, TextOutput o) { } }")]
    [InlineData("using MakoKaraGenerator.Scripting.V2; internal class PackEffect : IPureKaraokeEffect { public PreparedEffect Declare(DeclareContext s) => PreparedEffect.Empty; public void Evaluate(in TextFrame f, PreparedEffect d, TextOutput o) { } }")]
    [InlineData("public class PackEffect { }")]
    [InlineData("using MakoKaraGenerator.Scripting.V2; public class PackEffect : IPureKaraokeEffect { private PackEffect() { } public PreparedEffect Declare(DeclareContext s) => PreparedEffect.Empty; public void Evaluate(in TextFrame f, PreparedEffect d, TextOutput o) { } }")]
    [InlineData("Struct")]
    public async Task 真正DLL全部导出入口必须符合公共无参SDK合同(string source)
    {
        var files = V2PackageTestHelper.Files(); var manifest = V2PackageTestHelper.Manifest(files);
        if (source == "Struct") source = """
            using MakoKaraGenerator.Scripting.V2;
            public struct PackEffect : IPureKaraokeEffect
            {
                public PackEffect() { }
                public PreparedEffect Declare(DeclareContext s) => PreparedEffect.Empty;
                public void Evaluate(in TextFrame f, PreparedEffect d, TextOutput o) { }
            }
            """;
        if (source == "Missing") manifest["exports"]![0]!["entryType"] = "Missing";
        else { files["src/PackEffect.cs"] = Encoding.UTF8.GetBytes(source); manifest = V2PackageTestHelper.Manifest(files); }
        var error = await Assert.ThrowsAsync<EffectPackageException>(() => CompiledEffectPackage.CompileAsync(Read(files, manifest), Compiler()));
        Assert.Equal("PackageEntryInvalid", error.Code); Assert.Equal("exports.scale.entryType", error.Path);
    }
    [Fact]
    public async Task 入口反射不调用作者构造且源码失败不能创建帧实例()
    {
        var files = V2PackageTestHelper.Files(); files["src/PackEffect.cs"] = Encoding.UTF8.GetBytes("""
            using MakoKaraGenerator.Scripting.V2;
            public class PackEffect : IPureKaraokeEffect
            {
                public PackEffect() => throw new System.Exception("must not construct during validation");
                public PreparedEffect Declare(DeclareContext s) => PreparedEffect.Empty;
                public void Evaluate(in TextFrame f, PreparedEffect d, TextOutput o) { }
            }
            """);
        var validated = await CompiledEffectPackage.CompileAsync(Read(files, V2PackageTestHelper.Manifest(files)), Compiler()); Assert.True(validated.Artifact.Succeeded);
        _ = validated.CreateEffect("scale", new("instance"), new EffectBinding.Tokens([new("word")]), 0);
        files["src/PackEffect.cs"] = "invalid !"u8.ToArray();
        var failed = await CompiledEffectPackage.CompileAsync(Read(files, V2PackageTestHelper.Manifest(files)), Compiler());
        Assert.False(failed.Artifact.Succeeded); Assert.Empty(failed.Artifact.PeImage);
        Assert.Equal("PackageCompilationFailed", Assert.Throws<EffectPackageException>(() => failed.CreateEffect("scale", new("instance"), new EffectBinding.Tokens([new("word")]), 0)).Code);
    }
    [Fact]
    public async Task 图片未实际解码前不能进入固定SDK目录且取消不变为包错误()
    {
        var files = V2PackageTestHelper.Files(); files["assets/image.png"] = [1, 2]; var definition = Read(files, V2PackageTestHelper.NineParametersManifest(files));
        Assert.Equal("PackageImageValidatorMissing", (await Assert.ThrowsAsync<EffectPackageException>(() => CompiledEffectPackage.CompileAsync(definition, Compiler()))).Code);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => CompiledEffectPackage.CompileAsync(definition, Compiler(), cancellationToken: cancel.Token));
    }
    [Fact]
    public async Task 已实现组仿射从真实清单启用并保留参数与声明约束()
    {
        var compiled = await CompiledEffectPackage.CompileAsync(EffectPackageDefinition.Read(V2PackageTestHelper.GroupTransform()), Compiler());
        Assert.True(compiled.Artifact.Succeeded, string.Join('\n', compiled.Artifact.Diagnostics));
        foreach (EffectBinding binding in new EffectBinding[] { new EffectBinding.Project(), new EffectBinding.Node(new("root"), false) })
        {
            var effect = compiled.CreateEffect("group-transform", new("group"), binding, 3, "{\"node\":\"root\",\"offsetX\":80}"u8.ToArray());
            Assert.Equal(EffectCapabilities.Transforms, effect.Capabilities); Assert.Same(binding, effect.Binding);
            Assert.Same(compiled.Artifact, effect.Artifact); Assert.Equal(80, effect.Parameters.GetNumber("offsetX"));
            Assert.Equal("root", effect.Parameters.GetText("node")); Assert.Equal("group", Assert.Single(effect.PackageDeclaration!.Outputs).Target);
        }
        Assert.Equal("PackageParameterOutOfRange", Assert.Throws<EffectPackageException>(() => compiled.CreateEffect("group-transform", new("group"),
            new EffectBinding.Project(), 0, "{\"offsetX\":301}"u8.ToArray())).Code);
    }
    [Fact]
    public async Task 资源与默认值改变内容修订而入口集合改变编译Artifact()
    {
        var files = V2PackageTestHelper.Files(); var manifest = V2PackageTestHelper.Manifest(files); var original = await CompiledEffectPackage.CompileAsync(Read(files, manifest), Compiler());
        manifest["parameters"]![0]!["default"] = 2.5; var modified = await CompiledEffectPackage.CompileAsync(Read(files, manifest), Compiler());
        Assert.NotEqual(original.Definition.Contents.Revision, modified.Definition.Contents.Revision);
        Assert.Equal(original.Artifact.ArtifactHash, modified.Artifact.ArtifactHash); Assert.True(original.Artifact.PeImage.AsSpan().SequenceEqual(modified.Artifact.PeImage.AsSpan()));
        var sources = EffectPackageSources.Read(original.Definition.Contents); var compiler = Compiler();
        var a = await compiler.CompileAsync(sources, ["PackEffect"]); var b = await compiler.CompileAsync(sources, ["PackEffect", "Other"]);
        var same = await compiler.CompileAsync(sources, ["PackEffect", "PackEffect"]);
        Assert.Equal(a.ArtifactHash, same.ArtifactHash); Assert.NotEqual(a.ArtifactHash, b.ArtifactHash);
    }
}
