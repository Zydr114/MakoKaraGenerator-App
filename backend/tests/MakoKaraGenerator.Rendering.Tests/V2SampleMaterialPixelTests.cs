using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting.Compilation;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

// M13 A3 d：S07 CustomInk 与 S09 GlassInk 只有准入与原生编译用例，缺样例级像素断言。
// 这里把真实样例包作为未唱／已唱源材质，按 README 公式在字形内部像素上核对固定时刻的颜色。
public sealed class V2SampleMaterialPixelTests
{
    private const int Width = 640, Height = 360, Tolerance = 3;
    private const long TimeUs = 300_000;

    [Theory]
    [InlineData(300_000L, false)] [InlineData(425_000L, false)] [InlineData(300_000L, true)]
    public async Task CustomInk字形内部等于双色波纹公式且随歌曲时间变化(long time, bool gpu)
    {
        if (gpu && Environment.GetEnvironmentVariable("MAKO_GPU_SMOKE") != "1") return;
        var package = await Read("CustomInk");
        byte[] pixels = await RenderAsync(package, "custom-ink", time, transparent: true, gpu);
        double phase = 2 * Math.PI * 2 * time / 1_000_000d;
        int checkedPixels = 0;
        foreach (var (x, y) in Interior())
        {
            // 默认 frequencyHz=2、amplitude=.15，colorA=(.15,.4,.85,1)、colorB=(.4,.9,.95,1)，按像素中心取逻辑画布坐标。
            double blend = .5 + .15 * Math.Sin(.035 * (x + .5) + .022 * (y + .5) + phase);
            AssertPixel(pixels, x, y, .15 + .25 * blend, .4 + .5 * blend, .85 + .1 * blend, 1);
            checkedPixels++;
        }
        Assert.True(checkedPixels > 500, "字形内部像素太少，无法比较。");
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task GlassInk真实下层玻璃在不透明纯色底上等于公式而透明输出改用包内底色(bool gpu)
    {
        if (gpu && Environment.GetEnvironmentVariable("MAKO_GPU_SMOKE") != "1") return;
        var package = await Read("GlassInk");
        byte[] opaque = await RenderAsync(package, "glass-backdrop", TimeUs, transparent: false, gpu);
        double phase = 2 * Math.PI * 2 * TimeUs / 1_000_000d;
        double[] background = [Background.Red / 255d, Background.Green / 255d, Background.Blue / 255d];
        int checkedPixels = 0;
        foreach (var (x, y) in Interior())
        {
            // 纯色下层模糊后仍是同一颜色，折射偏移不改变取样；默认 highlight=.2、opacity=.75，按 sourceOver 叠在下层上。
            double shine = .2 * (.5 + .5 * Math.Sin(.075 * (x + .5) - .055 * (y + .5) + phase));
            double Channel(double value) => Math.Clamp(value + shine, 0, 1) * .75 + value * .25;
            AssertPixel(opaque, x, y, Channel(background[0]), Channel(background[1]), Channel(background[2]), 1);
            checkedPixels++;
        }
        Assert.True(checkedPixels > 500, "字形内部像素太少，无法比较。");

        // 透明输出跳过整个背景依赖图，改用包内 fallback 纯色 #3380CCBF 乘字形覆盖（白字静态渲染的 alpha）。
        // 注：直接用该色画静态字时 Skia 的字形抗锯齿随文字颜色变化，边缘与此不同，所以不拿同色静态填充逐字节对照。
        byte[] transparent = await RenderAsync(package, "glass-backdrop", TimeUs, transparent: true, gpu);
        using var renderer = Renderer(null, gpu);
        using var white = renderer.Render(RenderRequest.At(ProjectSnapshot.FromProject(TextProject()), TimeUs) with { TransparentBackground = true });
        byte[] coverage = white.PixelMemory.ToArray(); double[] premultiplied = [0xCC, 0x80, 0x33, 0xFF];
        int covered = 0;
        for (int i = 0; i < coverage.Length; i++)
        {
            double expected = premultiplied[i % 4] * (0xBF / 255d) * (coverage[i - i % 4 + 3] / 255d);
            Assert.True(Math.Abs(transparent[i] - expected) <= 1, $"字节{i}：实际 {transparent[i]}，兜底 {expected:F1}");
            if (i % 4 == 3 && coverage[i] > 0) covered++;
        }
        Assert.True(covered > 1000, "字形覆盖像素太少，无法比较。");
    }

    private static readonly RgbaColor Background = new(40, 90, 160);

    private static Project TextProject() => Project.Create("样例材质像素", Composition.Create(Width, Height, FrameRate.Fps30, outputDurationUs: 2_000_000)
            with { BackgroundColor = Background },
        LyricsDocument.Create([LyricLine.Create("line", "MW", new(0, 2_000_000), TimingStatus.Edited,
            [LyricToken.Create("token", "MW", new(0, 600_000), TimingStatus.Edited)])]),
        lyricStyle: LyricStyle.Default with { FontFamilyName = "DejaVu Sans", FontSizePx = 120, SungColor = RgbaColor.White,
            UnsungColor = RgbaColor.White, Stroke = null, Shadow = null, Glow = null }) with { EnterAnimation = null };

    private static async Task<byte[]> RenderAsync(EffectPackageContents package, string export, long time, bool transparent, bool gpu)
    {
        var material = new MaterialSpec.PackageMaterial(package.PackageId, package.Revision, export, EffectParameterOverrides.Parse("{}"));
        var source = TextProject();
        var project = source with { Effects = new([package], []), LyricStyle = source.LyricStyle with
            { UnsungFill = new() { Material = material }, SungFill = new() { Material = material } } };
        var snapshot = ProjectSnapshot.FromProject(project);
        var compiled = await CompiledProjectEffects.CompileAsync(snapshot, new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0")),
            new PackageImageValidator(), new PackageShaderValidator(), new PackageImageGraphValidator(), new PackageMaterialValidator());
        Assert.True(compiled.Succeeded); Assert.Empty(compiled.Effects); Assert.NotNull(compiled.SourceMaterials);
        await using var session = await compiled.CreateFactory(static () => new SkiaRenderPipeline()).CreateAsync(snapshot);
        var target = RenderTarget.FromComposition(snapshot.Composition);
        var prepared = await session.PrepareAsync(time, transparent ? Fx.OutputMode.Transparent : Fx.OutputMode.Opaque, target);
        Assert.NotEmpty(prepared.Materials);
        var compiledPackage = Assert.Single(compiled.Packages);
        using var native = CompiledPackageGraphExports.Compile(compiledPackage.Definition);
        using var resources = new SceneImageEffectResources([], null,
            [native.CreateMaterialProgram(compiledPackage.Resources.GetMaterial(export), new(0, 0, Width, Height))]);
        using var renderer = Renderer(resources, gpu);
        using var frame = renderer.Render(RenderRequest.At(snapshot, time) with { PreparedFrame = prepared, TransparentBackground = transparent });
        return frame.PixelMemory.ToArray();
    }

    private static SkiaRenderPipeline Renderer(SceneImageEffectResources? resources, bool gpu) => gpu
        ? new SkiaRenderPipeline(null, null, null, resources, false, BaseMaterialRuntimeTests.Device(true), false)
        : new SkiaRenderPipeline(null, null, null, resources);

    // 白色静态填充下 3×3 邻域全不透明的像素：字形完整覆盖，不含抗锯齿边缘。
    private static List<(int X, int Y)> Interior()
    {
        using SkiaRenderPipeline renderer = new();
        using var plain = renderer.Render(RenderRequest.At(ProjectSnapshot.FromProject(TextProject()), TimeUs) with { TransparentBackground = true });
        var alpha = plain.PixelMemory.ToArray();
        List<(int, int)> result = [];
        for (int y = 1; y < Height - 1; y++)
        for (int x = 1; x < Width - 1; x++)
        {
            bool full = true;
            for (int dy = -1; dy <= 1 && full; dy++)
            for (int dx = -1; dx <= 1 && full; dx++)
                full = alpha[((y + dy) * Width + x + dx) * 4 + 3] == 255;
            if (full) result.Add((x, y));
        }
        return result;
    }

    private static void AssertPixel(byte[] pixels, int x, int y, double red, double green, double blue, double opacity)
    {
        int offset = (y * Width + x) * 4;
        double[] expected = [blue * opacity * 255, green * opacity * 255, red * opacity * 255, opacity * 255];
        for (int channel = 0; channel < 4; channel++)
            Assert.True(Math.Abs(pixels[offset + channel] - expected[channel]) <= Tolerance,
                $"({x},{y}) 通道{channel}：实际 {pixels[offset + channel]}，公式 {expected[channel]:F1}");
    }

    private static Task<EffectPackageContents> Read(string name)
        => EffectPackageStorage.ReadSupportedDirectoryAsync(Path.Combine(AppContext.BaseDirectory, "effect-packages-v2", name));
}
