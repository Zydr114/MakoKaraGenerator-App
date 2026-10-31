using System.Text;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.Compilation;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class PackageShaderProgramsTests
{
    private static UniformValues Values(double value) => new([KeyValuePair.Create("amount", UniformValue.Float(value))]);
    private static SKColor Pixel(SKShader shader)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(4, 4, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap); using var paint = new SKPaint { Shader = shader, BlendMode = SKBlendMode.Src };
        canvas.ClipRect(new(0, 0, 4, 4)); canvas.Clear(SKColors.Transparent); canvas.DrawRect(0, 0, 4, 4, paint);
        return bitmap.GetPixel(1, 1);
    }
    private static ScriptCompiler Compiler() => new(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));

    [Fact]
    public void 包程序真正产生可变化像素且每帧值不改变固定ABI指纹()
    {
        var contents = EffectPackageShaderFixture.Contents();
        using var programs = CompiledPackageShaderPrograms.Compile(EffectPackageShaderPrograms.Read(contents));
        string key = programs.GetProgramKey(EffectPackageShaderFixture.ProgramPath);
        using var first = programs.CreateShader(EffectPackageShaderFixture.ProgramPath, Values(.25), new Dictionary<string, RuntimeShaderInput>());
        using var second = programs.CreateShader(EffectPackageShaderFixture.ProgramPath, Values(.75), new Dictionary<string, RuntimeShaderInput>());
        Assert.InRange(Pixel(first).Red, 63, 65); Assert.InRange(Pixel(second).Red, 190, 192);
        Assert.Equal(key, programs.GetProgramKey(EffectPackageShaderFixture.ProgramPath));
        programs.Dispose(); programs.Dispose(); Assert.InRange(Pixel(first).Red, 63, 65);
        Assert.Throws<ObjectDisposedException>(() => programs.CreateShader(EffectPackageShaderFixture.ProgramPath, Values(1), new Dictionary<string, RuntimeShaderInput>()));
    }

    [Theory]
    [InlineData("source", "ShaderCompilationFailed")]
    [InlineData("type", "ShaderUniformTypeMismatch")]
    [InlineData("name", "ShaderDeclarationMismatch")]
    [InlineData("array", "ShaderUniformTypeMismatch")]
    [InlineData("budget", "ShaderUniformBudgetExceeded")]
    public async Task JSON通过但实际编译反射失败时不能部分启用控制器(string change, string code)
    {
        var program = EffectPackageShaderFixture.Program(); string source = EffectPackageShaderFixture.Source;
        if (change == "source") source = "half4 main(float2 p) { return missing; }";
        if (change == "type") program["uniforms"]![0]!["type"] = "int";
        if (change == "name") program["uniforms"]![0]!["name"] = "other";
        if (change == "array") program["uniforms"]![0]!["arrayLength"] = 1;
        if (change == "budget")
        {
            program["uniforms"]![0]!["arrayLength"] = 129;
            source = "uniform float amount[129]; half4 main(float2 p) { return half4(amount[128],0,0,1); }";
        }
        var definition = EffectPackageDefinition.Read(EffectPackageShaderFixture.Contents(program, source));
        var error = await Assert.ThrowsAsync<EffectPackageException>(() => CompiledEffectPackage.CompileAsync(definition, Compiler(), null, new PackageShaderValidator()));
        Assert.Equal(code, error.Code); Assert.StartsWith(EffectPackageShaderFixture.ProgramPath, error.Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 正式包门禁验证后真实DLL可用且shader资源不扩大SDKwriter能力()
    {
        var definition = EffectPackageDefinition.Read(EffectPackageShaderFixture.Contents());
        var missing = await Assert.ThrowsAsync<EffectPackageException>(() => CompiledEffectPackage.CompileAsync(definition, Compiler()));
        Assert.Equal("PackageShaderValidatorMissing", missing.Code);
        var compiled = await CompiledEffectPackage.CompileAsync(definition, Compiler(), null, new PackageShaderValidator());
        Assert.True(compiled.Artifact.Succeeded);
        var effect = compiled.CreateEffect("scale", new("probe"), new EffectBinding.Tokens([new("token")]), 0);
        Assert.Equal(EffectCapabilities.Transforms, effect.Capabilities);
        Assert.Throws<KeyNotFoundException>(() => effect.Resources.GetGraph(EffectPackageShaderFixture.ProgramPath));
    }

    [Fact]
    public void 未引用程序也编译且失败之后新集合正常创建和取消回收()
    {
        var bad = EffectPackageShaderFixture.Program(); bad["source"] = "shaders/unused.sksl";
        var extra = new Dictionary<string, byte[]>
        {
            ["shaders/unused.program.json"] = EffectPackageFixture.Bytes(bad), ["shaders/unused.sksl"] = Encoding.UTF8.GetBytes("uniform float amount; half4 main(float2 p) { return bad; }")
        };
        var definitions = EffectPackageShaderPrograms.Read(EffectPackageShaderFixture.Contents(extraFiles: extra));
        var error = Assert.Throws<EffectPackageException>(() => CompiledPackageShaderPrograms.Compile(definitions));
        Assert.StartsWith("shaders/unused.program.json", error.Path, StringComparison.Ordinal);
        using var good = CompiledPackageShaderPrograms.Compile(EffectPackageShaderPrograms.Read(EffectPackageShaderFixture.Contents()));
        using var shader = good.CreateShader(EffectPackageShaderFixture.ProgramPath, Values(.5), new Dictionary<string, RuntimeShaderInput>());
        Assert.InRange(Pixel(shader).Red, 127, 129);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => CompiledPackageShaderPrograms.Compile(definitions, cancellationToken: cancel.Token));
        Assert.Throws<OperationCanceledException>(() => good.CreateShader(EffectPackageShaderFixture.ProgramPath, Values(.5), new Dictionary<string, RuntimeShaderInput>(), cancel.Token));
        Assert.Equal("PackageShaderProgramMissing", Assert.Throws<EffectPackageException>(() => good.GetProgramKey("unknown")).Code);
    }

    [Fact]
    public async Task 改源码或ABI建立新资源修订但不使同一CSharp重新编译()
    {
        var original = EffectPackageDefinition.Read(EffectPackageShaderFixture.Contents());
        var changed = EffectPackageDefinition.Read(EffectPackageShaderFixture.Contents(source: EffectPackageShaderFixture.Source.Replace("amount,0", "amount*.5,0", StringComparison.Ordinal)));
        var a = await CompiledEffectPackage.CompileAsync(original, Compiler(), null, new PackageShaderValidator());
        var b = await CompiledEffectPackage.CompileAsync(changed, Compiler(), null, new PackageShaderValidator());
        Assert.NotEqual(original.Contents.Revision, changed.Contents.Revision); Assert.Equal(a.Artifact.ArtifactHash, b.Artifact.ArtifactHash);
        using var first = CompiledPackageShaderPrograms.Compile(original.ShaderPrograms); using var second = CompiledPackageShaderPrograms.Compile(changed.ShaderPrograms);
        Assert.NotEqual(first.GetProgramKey(EffectPackageShaderFixture.ProgramPath), second.GetProgramKey(EffectPackageShaderFixture.ProgramPath));
    }
}
