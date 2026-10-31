using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Scripting;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Skia;

// 帧外宿主合成程序，与借用的作者资源生命周期无关。
internal sealed class SceneOpacityProgram : IDisposable
{
    private readonly CompiledRuntimeShader _program;
    private bool _disposed;
    internal SceneOpacityProgram()
    {
        _program = CompiledRuntimeShader.Compile(new("host/object-opacity.sksl",
            "uniform shader processed; uniform shader below; uniform float opacity; half4 main(float2 p) { return mix(below.eval(p),processed.eval(p),opacity); }",
            ShaderProgramDefinition.RasterProfile, [new("opacity", ShaderUniformKind.Float)],
            [new("processed", ShaderSurfaceKind.Color), new("below", ShaderSurfaceKind.Color)]));
    }
    internal SKShader CreateShader(SkiaGraphSurface processed, SkiaGraphSurface below, double opacity, ISkiaRenderDevice device, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var a = Shader(processed, device); using var b = Shader(below, device);
        return _program.CreateShader(new([new("opacity", UniformValue.Float(opacity))]),
            new Dictionary<string, RuntimeShaderInput> { ["processed"] = new(a, ShaderSurfaceKind.Color), ["below"] = new(b, ShaderSurfaceKind.Color) }, cancellationToken);
        static SKShader Shader(SkiaGraphSurface surface, ISkiaRenderDevice device)
        {
            var d = surface.Descriptor;
            var image = surface.ImageForDevice(device, out var raster);
            try
            {
                return image.ToShader(SKShaderTileMode.Decal, SKShaderTileMode.Decal,
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None),
                    new SKMatrix((float)(1 / d.DensityX), 0, (float)d.Bounds.Left, 0, (float)(1 / d.DensityY), (float)d.Bounds.Top, 0, 0, 1));
            }
            finally { if (raster is not null && !ReferenceEquals(raster, surface.Image)) raster.Dispose(); }
        }
    }
    public void Dispose() { if (_disposed) return; _disposed = true; _program.Dispose(); }
}
