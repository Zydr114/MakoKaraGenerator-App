using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Loading;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class CompiledRuntimeShaderTests
{
    private static ShaderProgramDefinition Program(string source, IEnumerable<ShaderUniformDeclaration>? uniforms = null,
        IEnumerable<ShaderChildDeclaration>? children = null, string profile = ShaderProgramDefinition.RasterProfile)
        => new("shaders/probe.sksl", source, profile, uniforms ?? [], children ?? []);
    private static UniformValues Values(params (string Name, UniformValue Value)[] values)
        => new(values.Select(value => KeyValuePair.Create(value.Name, value.Value)));
    private static SKColor Pixel(SKShader shader, SKColorSpace? space = null)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(4, 4, SKColorType.Bgra8888, SKAlphaType.Premul, space));
        using var canvas = new SKCanvas(bitmap); using var paint = new SKPaint { Shader = shader, BlendMode = SKBlendMode.Src };
        canvas.ClipRect(new SKRect(0, 0, 4, 4)); canvas.Clear(SKColors.Transparent);
        canvas.DrawRect(0, 0, 4, 4, paint); return bitmap.GetPixel(1, 1);
    }
    private static void Close(int expected, byte actual) => Assert.InRange((int)actual, expected - 1, expected + 1);

    [Fact]
    public async Task 真实离线Roslyn消费全部SDK类型并驱动原生像素()
    {
        var artifact = await new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0")).CompileAsync([new("src/UniformProbe.cs", """
            using System;
            using System.Collections.Generic;
            using MakoKaraGenerator.Scripting;
            public sealed class UniformProbe : IDisposable
            {
                public UniformValues Get() => new(new Dictionary<string, UniformValue>
                {
                    ["amount"] = UniformValue.Float(.01), ["pair"] = UniformValue.Float2(.02, .03),
                    ["triple"] = UniformValue.Float3(.04, .05, .06), ["quad"] = UniformValue.Float4(.07, .08, .09, .10),
                    ["count"] = UniformValue.Int(5), ["tint"] = UniformValue.Color(new ColorRgba(.5, .5, .5, .5)),
                    ["matrix"] = UniformValue.Matrix3x3(.1, .4, .7, .2, .5, .8, .3, .6, .9)
                });
                public void Dispose() { }
            }
            """)]);
        Assert.True(artifact.Succeeded, string.Join('\n', artifact.Diagnostics));
        using var module = new ScriptModule(artifact.PeImage.AsMemory(), artifact.PdbImage.AsMemory());
        var author = module.CreateInstance<IDisposable>("UniformProbe");
        var values = Assert.IsType<UniformValues>(author.GetType().GetMethod("Get")!.Invoke(author, null));
        using var program = CompiledRuntimeShader.Compile(Program("""
            uniform float amount; uniform float2 pair; uniform float3 triple; uniform float4 quad;
            uniform int count; layout(color) uniform half4 tint; uniform float3x3 matrix;
            half4 main(float2 point) {
                float3 firstColumn = matrix * float3(1, 0, 0);
                float3 value = firstColumn + float3(amount + pair.x + triple.x + quad.x, float(count)*.01, 0);
                return half4(value*tint.rgb*tint.a, tint.a);
            }
            """, [new("amount", ShaderUniformKind.Float), new("pair", ShaderUniformKind.Float2), new("triple", ShaderUniformKind.Float3),
            new("quad", ShaderUniformKind.Float4), new("count", ShaderUniformKind.Int), new("tint", ShaderUniformKind.Color), new("matrix", ShaderUniformKind.Matrix3x3)]));
        using var srgb = SKColorSpace.CreateSrgb();
        using var shader = program.CreateShader(values, new Dictionary<string, RuntimeShaderInput>());
        var pixel = Pixel(shader, srgb);
        Close(31, pixel.Red); Close(32, pixel.Green); Close(38, pixel.Blue); Close(128, pixel.Alpha);
    }

    [Theory]
    [InlineData("float", ShaderUniformKind.Float)]
    [InlineData("float2", ShaderUniformKind.Float2)]
    [InlineData("float3", ShaderUniformKind.Float3)]
    [InlineData("float4", ShaderUniformKind.Float4)]
    [InlineData("int", ShaderUniformKind.Int)]
    [InlineData("layout(color) uniform half4", ShaderUniformKind.Color)]
    [InlineData("float3x3", ShaderUniformKind.Matrix3x3)]
    public void 所有类型定长数组均实际执行(string sksl, ShaderUniformKind kind)
    {
        string declaration = kind == ShaderUniformKind.Color ? sksl : "uniform " + sksl;
        string valueExpression = kind switch
        {
            ShaderUniformKind.Float or ShaderUniformKind.Int => "float(data[1])",
            ShaderUniformKind.Matrix3x3 => "data[1][0][1]",
            _ => "data[1].x",
        };
        UniformValue Value(double value) => kind switch
        {
            ShaderUniformKind.Float => UniformValue.Float(value), ShaderUniformKind.Float2 => UniformValue.Float2(value, 0),
            ShaderUniformKind.Float3 => UniformValue.Float3(value, 0, 0), ShaderUniformKind.Float4 => UniformValue.Float4(value, 0, 0, 0),
            ShaderUniformKind.Int => UniformValue.Int((int)value), ShaderUniformKind.Color => UniformValue.Color(new(value, 0, 0, 1)),
            ShaderUniformKind.Matrix3x3 => UniformValue.Matrix3x3(0, 0, 0, value, 0, 0, 0, 0, 0), _ => throw new InvalidOperationException(),
        };
        using var program = CompiledRuntimeShader.Compile(Program($"{declaration} data[2]; half4 main(float2 p) {{ return half4({valueExpression},0,0,1); }}", [new("data", kind, 2)]));
        using var shader = program.CreateShader(Values(("data", UniformValue.Array([Value(0), Value(kind == ShaderUniformKind.Int ? 1 : .5)]))), new Dictionary<string, RuntimeShaderInput>());
        Close(kind == ShaderUniformKind.Int ? 255 : 128, Pixel(shader).Red);
    }

    [Theory]
    [InlineData("uniform float2x2 data;", ShaderUniformKind.Float4, null)]
    [InlineData("uniform int4 data;", ShaderUniformKind.Float4, null)]
    [InlineData("uniform float4 data;", ShaderUniformKind.Color, null)]
    [InlineData("layout(color) uniform float4 data;", ShaderUniformKind.Float4, null)]
    [InlineData("uniform float data[1];", ShaderUniformKind.Float, null)]
    [InlineData("uniform float data;", ShaderUniformKind.Float, 1)]
    [InlineData("uniform float data[3];", ShaderUniformKind.Float, 2)]
    public void 同字节数错误类型与数组标记均不能蒙混(string declaration, ShaderUniformKind kind, int? length)
    {
        var error = Assert.Throws<ShaderProgramException>(() => CompiledRuntimeShader.Compile(Program(declaration + "half4 main(float2 p) { return half4(1); }", [new("data", kind, length)])));
        Assert.Equal("ShaderUniformTypeMismatch", error.Code); Assert.Equal("uniforms.data", error.Field);
    }

    [Fact]
    public void 真实布局颜色转换与原始数据向量不同()
    {
        using var linear = SKColorSpace.CreateSrgbLinear();
        using var colorProgram = CompiledRuntimeShader.Compile(Program("layout(color) uniform float4 tint; half4 main(float2 p) { return half4(tint.rgb*tint.a,tint.a); }", [new("tint", ShaderUniformKind.Color)]));
        using var rawProgram = CompiledRuntimeShader.Compile(Program("uniform float4 tint; half4 main(float2 p) { return half4(tint.rgb*tint.a,tint.a); }", [new("tint", ShaderUniformKind.Float4)]));
        using var color = colorProgram.CreateShader(Values(("tint", UniformValue.Color(new(.5, .5, .5, 1)))), new Dictionary<string, RuntimeShaderInput>());
        using var raw = rawProgram.CreateShader(Values(("tint", UniformValue.Float4(.5, .5, .5, 1))), new Dictionary<string, RuntimeShaderInput>());
        Close(55, Pixel(color, linear).Red); Close(128, Pixel(raw, linear).Red);
    }

    [Fact]
    public void 两类图像输入分别保持颜色与raw覆盖语义()
    {
        using var linear = SKColorSpace.CreateSrgbLinear(); using var srgb = SKColorSpace.CreateSrgb();
        using var source = new SKBitmap(new SKImageInfo(2, 2, SKColorType.Rgba8888, SKAlphaType.Premul, linear));
        // 写入实际 linear 编码字节；Erase(SKColor) 本身会从 sRGB 转换，不能拿它冒充 raw 数据。
        System.Runtime.InteropServices.Marshal.Copy(new byte[] { 128, 0, 0, 255, 128, 0, 0, 255, 128, 0, 0, 255, 128, 0, 0, 255 }, 0, source.GetPixels(), 16);
        using var image = SKImage.FromBitmap(source); using var color = image.ToShader(); using var coverage = image.ToRawShader();
        using var program = CompiledRuntimeShader.Compile(Program("uniform shader image; half4 main(float2 p) { half value=image.eval(p).r; return half4(value,0,0,1); }", children: [new("image", ShaderSurfaceKind.Color)]));
        using var coverageProgram = CompiledRuntimeShader.Compile(Program(program.Definition.Source, children: [new("image", ShaderSurfaceKind.Coverage)]));
        using var a = program.CreateShader(UniformValues.Empty, new Dictionary<string, RuntimeShaderInput> { ["image"] = new(color, ShaderSurfaceKind.Color) });
        using var b = coverageProgram.CreateShader(UniformValues.Empty, new Dictionary<string, RuntimeShaderInput> { ["image"] = new(coverage, ShaderSurfaceKind.Coverage) });
        Close(188, Pixel(a, srgb).Red); Close(128, Pixel(b, srgb).Red);
        Assert.NotEqual(program.ProgramKey, coverageProgram.ProgramKey);
        Assert.Equal("ShaderChildValueMismatch", Assert.Throws<ShaderProgramException>(() => program.CreateShader(UniformValues.Empty,
            new Dictionary<string, RuntimeShaderInput> { ["image"] = new(coverage, ShaderSurfaceKind.Coverage) })).Code);
    }

    [Fact]
    public void native子输入只接受shader()
    {
        var error = Assert.Throws<ShaderProgramException>(() => CompiledRuntimeShader.Compile(Program("uniform colorFilter filter; half4 main(float2 p) { return filter.eval(half4(1)); }",
            children: [new("filter", ShaderSurfaceKind.Color)])));
        Assert.Equal("ShaderChildTypeMismatch", error.Code);
    }

    [Fact]
    public void 缺失额外声明及编译错误保留真实位置()
    {
        var source = "uniform float amount; half4 main(float2 p) { return half4(amount); }";
        Assert.Equal("ShaderDeclarationMismatch", Assert.Throws<ShaderProgramException>(() => CompiledRuntimeShader.Compile(Program(source))).Code);
        Assert.Equal("UnsupportedShaderProfile", Assert.Throws<ShaderProgramException>(() => CompiledRuntimeShader.Compile(Program(source, profile: "unknown"))).Code);
        var error = Assert.Throws<ShaderProgramException>(() => CompiledRuntimeShader.Compile(Program("half4 main(float2 p) { unknown value; return half4(value); }")));
        Assert.Equal("ShaderCompilationFailed", error.Code); Assert.Equal("shaders/probe.sksl", error.SourcePath);
        Assert.Contains("unknown", error.Message, StringComparison.Ordinal);
        Assert.Equal("ShaderDeclarationMismatch", Assert.Throws<ShaderProgramException>(() => CompiledRuntimeShader.Compile(Program(source, [new("other", ShaderUniformKind.Float)]))).Code);
    }

    [Fact]
    public void 默认标量预算覆盖全部向量及数组且可明确调整()
    {
        using var exact = CompiledRuntimeShader.Compile(Program("uniform float4 data[32]; half4 main(float2 p) { return data[31]; }", [new("data", ShaderUniformKind.Float4, 32)]));
        Assert.Equal(128, exact.Definition.ScalarCount);
        string source = "uniform float4 data[33]; half4 main(float2 p) { return half4(data[32]); }";
        var definition = Program(source, [new("data", ShaderUniformKind.Float4, 33)]);
        Assert.Equal("ShaderUniformBudgetExceeded", Assert.Throws<ShaderProgramException>(() => CompiledRuntimeShader.Compile(definition)).Code);
        using var program = CompiledRuntimeShader.Compile(definition, 132);
        using var shader = program.CreateShader(Values(("data", UniformValue.Array(Enumerable.Repeat(UniformValue.Float4(1, 0, 0, 1), 33)))), new Dictionary<string, RuntimeShaderInput>());
        Assert.Equal(SKColors.Red, Pixel(shader));
    }

    [Fact]
    public void 长度一数组合法但不能以单值替代且输入名字区分大小写()
    {
        using var program = CompiledRuntimeShader.Compile(Program("uniform float data[1]; half4 main(float2 p) { return half4(data[0],0,0,1); }", [new("data", ShaderUniformKind.Float, 1)]));
        var children = new Dictionary<string, RuntimeShaderInput>();
        using var shader = program.CreateShader(Values(("data", UniformValue.Array([UniformValue.Float(.5)]))), children);
        Close(128, Pixel(shader).Red);
        Assert.Equal("ShaderUniformValueMismatch", Assert.Throws<ShaderProgramException>(() => program.CreateShader(Values(("data", UniformValue.Float(.5))), children)).Code);
        Assert.Equal("ShaderInputMissing", Assert.Throws<ShaderProgramException>(() => program.CreateShader(Values(("Data", UniformValue.Array([UniformValue.Float(.5)]))), children)).Code);
    }

    [Fact]
    public void 子图像输入必须完整且不允许未声明输入()
    {
        using var program = CompiledRuntimeShader.Compile(Program("uniform shader image; half4 main(float2 p) { return image.eval(p); }", children: [new("image", ShaderSurfaceKind.Color)]));
        Assert.Equal("ShaderInputMissing", Assert.Throws<ShaderProgramException>(() => program.CreateShader(UniformValues.Empty, new Dictionary<string, RuntimeShaderInput>())).Code);
        using var image = SKShader.CreateColor(SKColors.Red);
        var inputs = new Dictionary<string, RuntimeShaderInput> { ["image"] = new(image, ShaderSurfaceKind.Color), ["extra"] = new(image, ShaderSurfaceKind.Color) };
        Assert.Equal("ShaderInputUnknown", Assert.Throws<ShaderProgramException>(() => program.CreateShader(UniformValues.Empty, inputs)).Code);
    }

    [Fact]
    public void 帧值不能补零混型或在float32边界静默夹值()
    {
        using var program = CompiledRuntimeShader.Compile(Program("uniform float amount; half4 main(float2 p) { return half4(amount,0,0,1); }", [new("amount", ShaderUniformKind.Float)]));
        var children = new Dictionary<string, RuntimeShaderInput>();
        Assert.Equal("ShaderInputMissing", Assert.Throws<ShaderProgramException>(() => program.CreateShader(UniformValues.Empty, children)).Code);
        Assert.Equal("ShaderInputUnknown", Assert.Throws<ShaderProgramException>(() => program.CreateShader(Values(("amount", UniformValue.Float(0)), ("extra", UniformValue.Float(0))), children)).Code);
        Assert.Equal("ShaderUniformValueMismatch", Assert.Throws<ShaderProgramException>(() => program.CreateShader(Values(("amount", UniformValue.Int(1))), children)).Code);
        Assert.Equal("ShaderUniformValueMismatch", Assert.Throws<ShaderProgramException>(() => program.CreateShader(Values(("amount", UniformValue.Array([UniformValue.Float(1)]))), children)).Code);
        foreach (double value in new[] { double.MaxValue, double.Epsilon })
            Assert.Equal("ShaderUniformNativeRange", Assert.Throws<ShaderProgramException>(() => program.CreateShader(Values(("amount", UniformValue.Float(value))), children)).Code);
        using var zero = program.CreateShader(Values(("amount", UniformValue.Float(0))), children);
        using var one = program.CreateShader(Values(("amount", UniformValue.Float(1))), children);
        Assert.Equal(SKColors.Black, Pixel(zero)); Assert.Equal(SKColors.Red, Pixel(one)); Assert.Equal(SKColors.Black, Pixel(zero));
    }

    [Fact]
    public void 声明顺序不改变键但源码内容改变键()
    {
        string source = "uniform float x; uniform float y; half4 main(float2 p) { return half4(x,y,0,1); }";
        using var a = CompiledRuntimeShader.Compile(Program(source, [new("x", ShaderUniformKind.Float), new("y", ShaderUniformKind.Float)]));
        using var b = CompiledRuntimeShader.Compile(Program(source, [new("y", ShaderUniformKind.Float), new("x", ShaderUniformKind.Float)]));
        using var c = CompiledRuntimeShader.Compile(Program(source.Replace("x,y", "y,x", StringComparison.Ordinal), a.Definition.Uniforms));
        Assert.Equal(a.ProgramKey, b.ProgramKey); Assert.NotEqual(a.ProgramKey, c.ProgramKey);
    }

    [Fact]
    public void 释放不夺取输入所有权且已创建shader继续有效()
    {
        using var child = SKShader.CreateColor(SKColors.Green);
        using var program = CompiledRuntimeShader.Compile(Program("uniform shader image; half4 main(float2 p) { return image.eval(p); }", children: [new("image", ShaderSurfaceKind.Color)]));
        var children = new Dictionary<string, RuntimeShaderInput> { ["image"] = new(child, ShaderSurfaceKind.Color) };
        using var shader = program.CreateShader(UniformValues.Empty, children);
        program.Dispose(); program.Dispose(); Assert.NotEqual(nint.Zero, child.Handle); child.Dispose();
        Assert.Equal(SKColors.Green, Pixel(shader));
        Assert.Throws<ObjectDisposedException>(() => program.CreateShader(UniformValues.Empty, children));
        using var other = CompiledRuntimeShader.Compile(program.Definition);
        Assert.Equal("ShaderChildValueMismatch", Assert.Throws<ShaderProgramException>(() => other.CreateShader(UniformValues.Empty, children)).Code);
    }

    [Fact]
    public void 取消不变成空效果且程序可继续使用()
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var definition = Program("half4 main(float2 p) { return half4(1,0,0,1); }");
        Assert.Throws<OperationCanceledException>(() => CompiledRuntimeShader.Compile(definition, cancellationToken: cancel.Token));
        using var program = CompiledRuntimeShader.Compile(definition); var children = new Dictionary<string, RuntimeShaderInput>();
        Assert.Throws<OperationCanceledException>(() => program.CreateShader(UniformValues.Empty, children, cancel.Token));
        using var shader = program.CreateShader(UniformValues.Empty, children); Assert.Equal(SKColors.Red, Pixel(shader));
    }
}
