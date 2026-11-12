using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Rendering.Skia;

// 固定宿主程序随编译图创建／释放；不在求值或 Render 内编译作者源码。
internal sealed class CompositionPayloadPrograms : IDisposable
{
    internal CompiledRuntimeShader Extract { get; }
    internal CompiledRuntimeShader Color { get; }
    internal CompiledRuntimeShader Erasure { get; }
    internal CompiledRuntimeShader CoverageProduct { get; }
    internal CompiledRuntimeShader OpacityColor { get; }
    internal CompiledRuntimeShader OpacityErasure { get; }
    /// <summary>扫字两侧前景按遮罩插值；m 取已唱覆盖 ÷ 字形覆盖（P3 设计 §10.2 第 2 条）。</summary>
    internal CompiledRuntimeShader WipeColor { get; }
    /// <summary>扫字两侧擦除量按同一遮罩插值。</summary>
    internal CompiledRuntimeShader WipeErasure { get; }
    internal CompositionPayloadPrograms(CancellationToken cancellationToken)
    {
        CompiledRuntimeShader? extract = null, color = null, erasure = null, coverageProduct = null, opacityColor = null, opacityErasure = null,
            wipeColor = null, wipeErasure = null;
        try
        {
            extract = CompiledRuntimeShader.Compile(new("host/payload-extract.sksl",
                "uniform shader color; uniform shader coverage; uniform int sourceOver; uniform float opacity; half4 main(float2 p) { return half4(0,0,0,max(coverage.eval(p).a,sourceOver==1 ? color.eval(p).a : half(0))*opacity); }",
                ShaderProgramDefinition.RasterProfile, [new("sourceOver", ShaderUniformKind.Int), new("opacity", ShaderUniformKind.Float)],
                [new("color", ShaderSurfaceKind.Color), new("coverage", ShaderSurfaceKind.Coverage)]), cancellationToken: cancellationToken);
            color = CompiledRuntimeShader.Compile(new("host/payload-color.sksl",
                "uniform shader first; uniform shader next; uniform shader erase; half4 main(float2 p) { return next.eval(p)+first.eval(p)*(1-erase.eval(p).a); }",
                ShaderProgramDefinition.RasterProfile, [], [new("first", ShaderSurfaceKind.Color), new("next", ShaderSurfaceKind.Color), new("erase", ShaderSurfaceKind.Coverage)]), cancellationToken: cancellationToken);
            erasure = CompiledRuntimeShader.Compile(new("host/payload-erasure.sksl",
                "uniform shader first; uniform shader next; half4 main(float2 p) { half a=first.eval(p).a; half b=next.eval(p).a; return half4(0,0,0,b+a*(1-b)); }",
                ShaderProgramDefinition.RasterProfile, [], [new("first", ShaderSurfaceKind.Coverage), new("next", ShaderSurfaceKind.Coverage)]), cancellationToken: cancellationToken);
            coverageProduct = CompiledRuntimeShader.Compile(new("host/coverage-product.sksl",
                "uniform shader first; uniform shader region; half4 main(float2 p) { return half4(0,0,0,first.eval(p).a*region.eval(p).a); }",
                ShaderProgramDefinition.RasterProfile, [], [new("first", ShaderSurfaceKind.Coverage), new("region", ShaderSurfaceKind.Coverage)]), cancellationToken: cancellationToken);
            const string opacitySource = "uniform shader source; uniform shader suffix; uniform float opacity; half4 main(float2 p) { float t=1-suffix.eval(p).a; float denominator=(1-opacity)+opacity*t; float factor=denominator==0 ? 0 : opacity*t/denominator; return source.eval(p)*factor; }";
            opacityColor = CompiledRuntimeShader.Compile(new("host/payload-opacity-color.sksl", opacitySource,
                ShaderProgramDefinition.RasterProfile, [new("opacity", ShaderUniformKind.Float)],
                [new("source", ShaderSurfaceKind.Color), new("suffix", ShaderSurfaceKind.Coverage)]), cancellationToken: cancellationToken);
            opacityErasure = CompiledRuntimeShader.Compile(new("host/payload-opacity-erasure.sksl", opacitySource,
                ShaderProgramDefinition.RasterProfile, [new("opacity", ShaderUniformKind.Float)],
                [new("source", ShaderSurfaceKind.Coverage), new("suffix", ShaderSurfaceKind.Coverage)]), cancellationToken: cancellationToken);
            // 遮罩 m = 已唱覆盖 ÷ 字形覆盖，两张覆盖与两侧部件用同一字形绘制。字形覆盖为 0 处两侧本不重叠（GPU 上直绘与离屏字形
            // 栅格原点可能差一点，边缘可能只有一侧有像素），直接相加。
            const string wipeInputs = "uniform shader unsungSide; uniform shader sungSide; uniform shader glyph; uniform shader sung; ";
            wipeColor = CompiledRuntimeShader.Compile(new("host/payload-wipe-color.sksl", wipeInputs
                    + "half4 main(float2 p) { half4 u=unsungSide.eval(p); half4 s=sungSide.eval(p); float g=glyph.eval(p).a; "
                    + "return g>0 ? mix(u, s, half(clamp(sung.eval(p).a/g, 0, 1))) : u+s; }",
                ShaderProgramDefinition.RasterProfile, [], [new("unsungSide", ShaderSurfaceKind.Color), new("sungSide", ShaderSurfaceKind.Color),
                    new("glyph", ShaderSurfaceKind.Coverage), new("sung", ShaderSurfaceKind.Coverage)]), cancellationToken: cancellationToken);
            wipeErasure = CompiledRuntimeShader.Compile(new("host/payload-wipe-erasure.sksl", wipeInputs
                    + "half4 main(float2 p) { half u=unsungSide.eval(p).a; half s=sungSide.eval(p).a; float g=glyph.eval(p).a; "
                    + "return half4(0, 0, 0, g>0 ? mix(u, s, half(clamp(sung.eval(p).a/g, 0, 1))) : min(u+s, 1)); }",
                ShaderProgramDefinition.RasterProfile, [], [new("unsungSide", ShaderSurfaceKind.Coverage), new("sungSide", ShaderSurfaceKind.Coverage),
                    new("glyph", ShaderSurfaceKind.Coverage), new("sung", ShaderSurfaceKind.Coverage)]), cancellationToken: cancellationToken);
            Extract = extract; Color = color; Erasure = erasure; CoverageProduct = coverageProduct;
            OpacityColor = opacityColor; OpacityErasure = opacityErasure; WipeColor = wipeColor; WipeErasure = wipeErasure;
        }
        catch
        {
            extract?.Dispose(); color?.Dispose(); erasure?.Dispose(); coverageProduct?.Dispose(); opacityColor?.Dispose(); opacityErasure?.Dispose();
            wipeColor?.Dispose(); wipeErasure?.Dispose(); throw;
        }
    }
    public void Dispose()
    {
        Extract.Dispose(); Color.Dispose(); Erasure.Dispose(); CoverageProduct.Dispose(); OpacityColor.Dispose(); OpacityErasure.Dispose();
        WipeColor.Dispose(); WipeErasure.Dispose();
    }
}
