using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Text;
using SkiaSharp;

namespace MakoKaraGenerator.Spike.Rendering;

/// <summary>R1 的可重复 CPU 能力样本；不是用户工程渲染图的实现。</summary>
public enum ShaderSpikeMode
{
    /// <summary>原始已整形文字。</summary>
    Baseline,
    /// <summary>直接字形材质。</summary>
    Material,
    /// <summary>外部描边材质。</summary>
    StrokeMaterial,
    /// <summary>整行捕获和 RGB 错位。</summary>
    Glitch,
    /// <summary>背景媒体采样。</summary>
    GlassMedia,
    /// <summary>下层合成采样。</summary>
    GlassBackdrop,
    /// <summary>透明模式包内基础外观。</summary>
    TransparentFallback,
}

/// <summary>在锁定 raster native 库上验证字体、蒙版、子图像、滤镜和 Alpha。</summary>
public static class RuntimeShaderFrameRenderer
{
    private const int Width = 640, Height = 360;
    private const string Text = "请照亮我前行";
    private const string InkShader = """
        uniform float time;
        half4 main(float2 p) {
            float wave = 0.5 + 0.5 * sin(p.x * 0.04 + time);
            return half4(wave, 0.2, 1.0 - wave, 1);
        }
        """;
    private const string GlitchShader = """
        uniform shader target;
        uniform float strength;
        uniform float time;
        half4 main(float2 p) {
            float d = strength * sin(floor(p.y / 12) + time);
            half4 r = target.eval(p + float2(d, 0));
            half4 g = target.eval(p);
            half4 b = target.eval(p - float2(d, 0));
            return half4(r.r, g.g, b.b, max(r.a, max(g.a, b.a)));
        }
        """;
    private const string GlassShader = """
        uniform shader coverage;
        uniform shader blurred;
        uniform shader below;
        uniform float time;
        half4 main(float2 p) {
            half a = coverage.eval(p).a * 0.75;
            float2 q = p + float2(4 * sin(p.y * 0.08 + time), 4 * cos(p.x * 0.06 + time));
            half4 glass = blurred.eval(q);
            return glass * a + below.eval(p) * (1 - a);
        }
        """;

    /// <summary>渲染一个独立技术样本；位图归调用方释放。</summary>
    public static SKBitmap Render(ShaderSpikeMode mode, long timeUs = 500_000, bool transformGroup = false,
        int pixelWidth = Width, int pixelHeight = Height, float glitchStrength = 8, bool transparentBackground = false)
    {
        using SKSurface surface = CreateSurface(pixelWidth, pixelHeight);
        SKCanvas canvas = surface.Canvas;
        canvas.Scale((float)pixelWidth / Width, (float)pixelHeight / Height);
        canvas.ClipRect(new(0, 0, Width, Height));
        canvas.Clear(SKColors.Transparent);
        if (transparentBackground && mode is ShaderSpikeMode.GlassMedia or ShaderSpikeMode.GlassBackdrop)
            mode = ShaderSpikeMode.TransparentFallback;
        bool transparent = mode == ShaderSpikeMode.TransparentFallback || transparentBackground;
        using SkiaTextLayoutEngine engine = new();
        TextLayoutOptions options = new() { FontSizePx = 64 };
        ShapedLine shaped = engine.Shape(Text, options);
        float originX = (Width - shaped.Width) / 2;
        float time = (float)((double)timeUs / 1_000_000);
        if (!transparent) DrawBackground(canvas);
        using SKImage media = surface.Snapshot();
        if (mode is ShaderSpikeMode.GlassBackdrop or ShaderSpikeMode.GlassMedia)
            engine.DrawLine(canvas, engine.Shape("下层歌词", options), options, 110, 180, new SKColor(0, 240, 80));
        using SKImage below = surface.Snapshot();
        using SKSurface coverage = CreateSurface(pixelWidth, pixelHeight);
        SKCanvas maskCanvas = coverage.Canvas;
        maskCanvas.Scale((float)pixelWidth / Width, (float)pixelHeight / Height);
        maskCanvas.ClipRect(new(0, 0, Width, Height));
        maskCanvas.Clear(SKColors.Transparent);
        ApplyGroup(maskCanvas, transformGroup);
        DrawTextWithRuby(engine, maskCanvas, shaped, options, originX, SKColors.White);
        using SKImage mask = coverage.Snapshot();
        SKMatrix inputMatrix = SKMatrix.CreateScale((float)Width / pixelWidth, (float)Height / pixelHeight);

        if (mode is ShaderSpikeMode.GlassMedia or ShaderSpikeMode.GlassBackdrop)
        {
            using SKSurface blurSurface = CreateSurface(pixelWidth, pixelHeight);
            blurSurface.Canvas.ClipRect(new(0, 0, pixelWidth, pixelHeight));
            blurSurface.Canvas.Clear(SKColors.Transparent);
            using SKImageFilter blur = SKImageFilter.CreateBlur(8f * pixelWidth / Width, 8f * pixelHeight / Height);
            using SKPaint blurPaint = new() { ImageFilter = blur };
            blurSurface.Canvas.DrawImage(mode == ShaderSpikeMode.GlassMedia ? media : below, 0, 0, blurPaint);
            using SKImage blurred = blurSurface.Snapshot();
            using SKShader maskShader = mask.ToRawShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, inputMatrix);
            using SKShader blurShader = blurred.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, inputMatrix);
            using SKShader belowShader = below.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, inputMatrix);
            using SKRuntimeEffect effect = Compile(GlassShader);
            var uniforms = new SKRuntimeEffectUniforms(effect) { ["time"] = time };
            using var children = new SKRuntimeEffectChildren(effect) { ["coverage"] = maskShader, ["blurred"] = blurShader, ["below"] = belowShader };
            using SKShader shader = effect.ToShader(uniforms, children);
            using SKPaint paint = new() { Shader = shader, BlendMode = SKBlendMode.Src };
            canvas.DrawRect(new(0, 0, Width, Height), paint);
        }
        else if (mode == ShaderSpikeMode.Glitch)
        {
            using SKShader target = mask.ToShader(SKShaderTileMode.Decal, SKShaderTileMode.Decal, inputMatrix);
            using SKRuntimeEffect effect = Compile(GlitchShader);
            var uniforms = new SKRuntimeEffectUniforms(effect) { ["time"] = time, ["strength"] = glitchStrength };
            using var children = new SKRuntimeEffectChildren(effect) { ["target"] = target };
            using SKShader shader = effect.ToShader(uniforms, children);
            using SKPaint paint = new() { Shader = shader };
            canvas.DrawRect(new(0, 0, Width, Height), paint);
        }
        else
        {
            canvas.Save();
            ApplyGroup(canvas, transformGroup);
            using SKRuntimeEffect effect = Compile(InkShader);
            var uniforms = new SKRuntimeEffectUniforms(effect) { ["time"] = time };
            using SKShader ink = effect.ToShader(uniforms);
            if (mode == ShaderSpikeMode.StrokeMaterial)
            {
                using SKSurface strokeSurface = CreateSurface(pixelWidth, pixelHeight);
                SKCanvas strokeCanvas = strokeSurface.Canvas;
                strokeCanvas.Scale((float)pixelWidth / Width, (float)pixelHeight / Height);
                strokeCanvas.ClipRect(new(0, 0, Width, Height));
                strokeCanvas.Clear(SKColors.Transparent);
                ApplyGroup(strokeCanvas, transformGroup);
                engine.DrawLine(strokeCanvas, shaped, options, originX, 210, SKColors.White,
                    style: SKPaintStyle.Stroke, strokeWidth: 14, shader: ink);
                engine.DrawLine(strokeCanvas, shaped, options, originX, 210, SKColors.White, blendMode: SKBlendMode.DstOut);
                using SKImage stroke = strokeSurface.Snapshot();
                // stroke 已经包含共同父变换，仅恢复画布矩阵后合成一次。
                canvas.Restore();
                canvas.DrawImage(stroke, new SKRect(0, 0, Width, Height));
                canvas.Save(); ApplyGroup(canvas, transformGroup);
            }
            DrawTextWithRuby(engine, canvas, shaped, options, originX,
                transparent ? new SKColor(0x4C, 0x8D, 0xFF, 192) : SKColors.White,
                mode == ShaderSpikeMode.Material ? ink : null);
            canvas.Restore();
        }
        using SKImage image = surface.Snapshot();
        return SKBitmap.FromImage(image);
    }

    /// <summary>无背景滤镜的透明原图，用于验证滤镜 bypass 保持输入。</summary>
    public static SKBitmap CapturePlainText()
    {
        using SKSurface surface = CreateSurface(Width, Height);
        surface.Canvas.ClipRect(new(0, 0, Width, Height)); surface.Canvas.Clear(SKColors.Transparent);
        using SkiaTextLayoutEngine engine = new();
        TextLayoutOptions options = new() { FontSizePx = 64 };
        ShapedLine shaped = engine.Shape(Text, options);
        DrawTextWithRuby(engine, surface.Canvas, shaped, options, (Width - shaped.Width) / 2, SKColors.White);
        using SKImage image = surface.Snapshot(); return SKBitmap.FromImage(image);
    }

    /// <summary>执行 native shader 编译；错误包含原生的行列信息。</summary>
    public static SKRuntimeEffect Compile(string source) => SKRuntimeEffect.CreateShader(source, out string error)
        ?? throw new InvalidOperationException(error);

    private static SKSurface CreateSurface(int width, int height) => SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul))
        ?? throw new InvalidOperationException("CPU raster surface 创建失败。" );
    private static void ApplyGroup(SKCanvas canvas, bool transformed)
    {
        if (!transformed) return;
        canvas.Translate(30, -18); canvas.RotateDegrees(7, Width / 2f, Height / 2f);
    }
    private static void DrawTextWithRuby(SkiaTextLayoutEngine engine, SKCanvas canvas, ShapedLine line,
        TextLayoutOptions options, float originX, SKColor color, SKShader? shader = null)
    {
        engine.DrawLine(canvas, line, options, originX, 210, color, shader: shader);
        TextLayoutOptions rubyOptions = options with { FontSizePx = 24 };
        ShapedLine ruby = engine.Shape("qǐng", rubyOptions);
        engine.DrawLine(canvas, ruby, rubyOptions, originX + (64 - ruby.Width) / 2, 140, color, shader: shader);
    }
    private static void DrawBackground(SKCanvas canvas)
    {
        using SKPaint paint = new();
        for (int x = 0; x < Width; x += 16)
        {
            paint.Color = x % 32 == 0 ? new SKColor(20, 45, 95) : new SKColor(100, 55, 30);
            canvas.DrawRect(x, 0, 16, Height, paint);
        }
    }
}
