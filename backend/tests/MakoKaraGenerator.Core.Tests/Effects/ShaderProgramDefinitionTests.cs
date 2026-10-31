using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Core.Tests.Effects;

public sealed class ShaderProgramDefinitionTests
{
    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("9value")]
    [InlineData("颜色")]
    public void 清单不接受含混标识符(string name)
        => Assert.Throws<ArgumentException>(() => new ShaderUniformDeclaration(name, ShaderUniformKind.Float));

    [Theory]
    [InlineData("/shader.sksl")]
    [InlineData("../shader.sksl")]
    [InlineData("shader/./a.sksl")]
    [InlineData("C:\\shader.sksl")]
    public void 诊断路径只能是包内相对路径(string path)
        => Assert.Throws<ArgumentException>(() => new ShaderProgramDefinition(path, "source", ShaderProgramDefinition.RasterProfile, [], []));

    [Fact]
    public void 声明副本不依赖原列表且标量预算不溢出()
    {
        var declarations = new List<ShaderUniformDeclaration> { new("matrix", ShaderUniformKind.Matrix3x3, int.MaxValue) };
        var program = new ShaderProgramDefinition("shaders/probe.sksl", "source", ShaderProgramDefinition.RasterProfile, declarations, []);
        declarations.Clear();
        Assert.Equal(9L * int.MaxValue, program.ScalarCount);
        Assert.Single(program.Uniforms);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ShaderUniformDeclaration("data", ShaderUniformKind.Float, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ShaderUniformDeclaration("data", (ShaderUniformKind)123));
    }

    [Fact]
    public void uniform与child共用名字空间并拒绝重复()
    {
        Assert.Throws<ArgumentException>(() => new ShaderProgramDefinition("a.sksl", "source", ShaderProgramDefinition.RasterProfile,
            [new("same", ShaderUniformKind.Float), new("same", ShaderUniformKind.Int)], []));
        Assert.Throws<ArgumentException>(() => new ShaderProgramDefinition("a.sksl", "source", ShaderProgramDefinition.RasterProfile,
            [new("same", ShaderUniformKind.Float)], [new("same", ShaderSurfaceKind.Coverage)]));
    }
}
