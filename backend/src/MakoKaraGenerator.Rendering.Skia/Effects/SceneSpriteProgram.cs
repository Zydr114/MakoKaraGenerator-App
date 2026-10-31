using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Scripting;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Skia;

// 管线创建时固定宿主程序，帧内只绑定图片与裁片值；不分配图片副本或离屏存储。
internal sealed class SceneSpriteProgram : IDisposable
{
    private readonly CompiledRuntimeShader _program = CompiledRuntimeShader.Compile(new("host/sprite-crop.sksl",
        "uniform shader image; uniform float4 bounds; half4 main(float2 p) { float2 size=bounds.zw-bounds.xy; float2 inset=min(float2(.5),size*.5); float2 q=clamp(bounds.xy+p*size,bounds.xy+inset,bounds.zw-inset); return image.eval(q); }",
        ShaderProgramDefinition.RasterProfile, [new("bounds", ShaderUniformKind.Float4)], [new("image", ShaderSurfaceKind.Color)]));
    public SKShader CreateShader(SKImage image, Rect2 source, CancellationToken token)
    {
        using var child = image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        return _program.CreateShader(new([new("bounds", UniformValue.Float4(source.Left * image.Width, source.Top * image.Height,
            source.Right * image.Width, source.Bottom * image.Height))]),
            new Dictionary<string, RuntimeShaderInput> { ["image"] = new(child, ShaderSurfaceKind.Color) }, token);
    }
    public void Dispose() => _program.Dispose();
}
