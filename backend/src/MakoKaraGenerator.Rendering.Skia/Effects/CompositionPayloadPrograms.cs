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
    internal CompositionPayloadPrograms(CancellationToken cancellationToken)
    {
        CompiledRuntimeShader? extract = null, color = null, erasure = null, coverageProduct = null, opacityColor = null, opacityErasure = null;
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
            Extract = extract; Color = color; Erasure = erasure; CoverageProduct = coverageProduct;
            OpacityColor = opacityColor; OpacityErasure = opacityErasure;
        }
        catch { extract?.Dispose(); color?.Dispose(); erasure?.Dispose(); coverageProduct?.Dispose(); opacityColor?.Dispose(); opacityErasure?.Dispose(); throw; }
    }
    public void Dispose() { Extract.Dispose(); Color.Dispose(); Erasure.Dispose(); CoverageProduct.Dispose(); OpacityColor.Dispose(); OpacityErasure.Dispose(); }
}
