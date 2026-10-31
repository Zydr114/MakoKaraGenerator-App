using System.Text;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Core.Tests.Effects;

public sealed class EffectPackageShaderProgramsTests
{
    [Fact]
    public void 全部类型数组和两个child语义从固定闭包读取且不受调用者改变影响()
    {
        string[] kinds = ["float", "float2", "float3", "float4", "int", "color", "matrix3x3"];
        var program = EffectPackageShaderFixture.Program();
        program["uniforms"] = new JsonArray([.. kinds.Select((kind, index) => (JsonNode?)new JsonObject
            { ["name"] = "value" + index, ["type"] = kind, ["arrayLength"] = 2 })]);
        program["children"] = new JsonArray(new JsonObject { ["name"] = "ink", ["type"] = "color" }, new JsonObject { ["name"] = "mask", ["type"] = "coverage" });
        var contents = EffectPackageShaderFixture.Contents(program); program["uniforms"]![0]!["type"] = "invalid";
        var definition = Assert.Single(EffectPackageShaderPrograms.Read(contents)).Value;
        Assert.Equal(EffectPackageShaderFixture.SourcePath, definition.SourcePath);
        Assert.Equal(EffectPackageShaderFixture.Source, definition.Source); Assert.Equal(48, definition.ScalarCount);
        Assert.Equal(Enum.GetValues<ShaderUniformKind>(), definition.Uniforms.Select(value => value.Kind));
        Assert.All(definition.Uniforms, value => Assert.Equal(2, value.ArrayLength));
        Assert.Equal([ShaderSurfaceKind.Color, ShaderSurfaceKind.Coverage], definition.Children.Select(value => value.Kind));
        Assert.Single(EffectPackageDefinition.Read(contents).ShaderPrograms);
    }

    [Theory]
    [InlineData("version", "PackageShaderProgramVersionMismatch")]
    [InlineData("version-number", "PackageParameterTypeMismatch")]
    [InlineData("profile", "PackageShaderProfileMismatch")]
    [InlineData("source", "PackageShaderSourceMissing")]
    [InlineData("source-role", "PackageShaderSourceMissing")]
    [InlineData("relative", "PackagePathInvalid")]
    [InlineData("unknown", "PackageSemanticUnknownField")]
    [InlineData("missing", "PackageSemanticMissingField")]
    [InlineData("type", "PackageShaderUniformTypeUnsupported")]
    [InlineData("name", "PackageShaderDeclarationInvalid")]
    [InlineData("duplicate", "PackageShaderDeclarationDuplicate")]
    [InlineData("child-kind", "PackageShaderChildTypeUnsupported")]
    [InlineData("child-name", "PackageShaderDeclarationDuplicate")]
    [InlineData("array-zero", "PackageShaderArrayLengthInvalid")]
    [InlineData("array-too-large", "PackageShaderArrayLengthInvalid")]
    [InlineData("array-number", "PackageParameterTypeMismatch")]
    [InlineData("uniform-field", "PackageSemanticUnknownField")]
    public void 坏声明按实际文件字段拒绝且不从源码猜测或补值(string change, string code)
    {
        var program = EffectPackageShaderFixture.Program(); var uniform = program["uniforms"]![0]!;
        switch (change)
        {
            case "version": program["programVersion"] = EffectPackageShaderPrograms.CurrentVersion + 1; break;
            case "version-number": program["programVersion"] = JsonNode.Parse("1.0"); break;
            case "profile": program["profile"] = "future"; break;
            case "source": program["source"] = "missing.sksl"; break;
            case "source-role": program["source"] = "README.md"; break;
            case "relative": program["source"] = "../probe.sksl"; break;
            case "unknown": program["inferDeclarations"] = true; break;
            case "missing": program.Remove("children"); break;
            case "type": uniform["type"] = "bool"; break;
            case "name": uniform["name"] = "不是标识符"; break;
            case "duplicate": program["uniforms"]!.AsArray().Add(uniform.DeepClone()); break;
            case "child-kind": program["children"] = new JsonArray(new JsonObject { ["name"] = "child", ["type"] = "blender" }); break;
            case "child-name": program["children"] = new JsonArray(new JsonObject { ["name"] = "amount", ["type"] = "color" }); break;
            case "array-zero": uniform["arrayLength"] = 0; break;
            case "array-too-large": uniform["arrayLength"] = long.MaxValue; break;
            case "array-number": uniform["arrayLength"] = 1.5; break;
            case "uniform-field": uniform["layout"] = "infer"; break;
        }
        var error = Assert.Throws<EffectPackageException>(() => EffectPackageShaderPrograms.Read(EffectPackageShaderFixture.Contents(program)));
        Assert.Equal(code, error.Code);
        if (change != "relative") Assert.StartsWith(EffectPackageShaderFixture.ProgramPath, error.Path, StringComparison.Ordinal);
    }

    [Fact]
    public void 长度一仍是数组且无声明的包为空目录而取消仍为取消()
    {
        var program = EffectPackageShaderFixture.Program(); program["uniforms"]![0]!["arrayLength"] = 1;
        Assert.Equal(1, Assert.Single(EffectPackageShaderPrograms.Read(EffectPackageShaderFixture.Contents(program))).Value.Uniforms[0].ArrayLength);
        Assert.Empty(EffectPackageShaderPrograms.Read(EffectPackageFixture.Contents()));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => EffectPackageShaderPrograms.Read(EffectPackageShaderFixture.Contents(), cancel.Token));
    }

    [Fact]
    public void 程序JSON重复键坏UTF8和未引用错误程序不能藏在闭包中()
    {
        string duplicate = "{\"programVersion\":1,\"programVersion\":1,\"source\":\"shaders/probe.sksl\",\"profile\":\"" + ShaderProgramDefinition.RasterProfile + "\",\"uniforms\":[],\"children\":[]}";
        var extra = new Dictionary<string, byte[]> { ["shaders/unused.program.json"] = Encoding.UTF8.GetBytes(duplicate) };
        var error = Assert.Throws<EffectPackageException>(() => EffectPackageShaderPrograms.Read(EffectPackageShaderFixture.Contents(extraFiles: extra)));
        Assert.Equal("PackageJsonDuplicateKey", error.Code); Assert.StartsWith("shaders/unused.program.json", error.Path, StringComparison.Ordinal);
        extra["shaders/unused.program.json"] = [255];
        Assert.Equal("PackageUtf8Invalid", Assert.Throws<EffectPackageException>(() => EffectPackageShaderFixture.Contents(extraFiles: extra)).Code);
    }
}
