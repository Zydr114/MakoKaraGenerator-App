using System.Text.Json;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Execution;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Tests;

/// <summary>M12 测速用测试包（tests/Fixtures/ScriptEffectsV2Perf）：准入、原生编译与最小像素检查。</summary>
public sealed partial class PerfFixturePackageTests
{
    private const long TimeUs = 1_250_000;
    private static readonly GraphRect Canvas = new(0, 0, 640, 360);

    private sealed class NoCompiler : IScriptCompilationService
    {
        private readonly ScriptCompiler _real = new(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
        public ScriptCompilationIdentity Identity => _real.Identity;
        public Task<ScriptCompilationResult> CompileAsync(IEnumerable<ScriptSource> sources, IEnumerable<string> entryTypes, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("resource-only fixture must never compile a DLL");
    }

    [Theory]
    [InlineData("MaterialConstant", "constant")]
    [InlineData("MaterialCoverage", "coverage")]
    [InlineData("MaterialBackdrop", "backdrop")]
    [InlineData("MaterialMediaRequired", "media-required")]
    [InlineData("MaterialMediaOptional", "media-optional")]
    [InlineData("GraphPassthrough", "passthrough")]
    [InlineData("GraphBlur", "blur")]
    public async Task 测试包按v2准入且唯一导出可原生编译(string name, string export)
    {
        var package = await Read(name);
        Assert.True(EffectPackageProfiles.IsSupported(package.Profile));
        Assert.DoesNotContain(package.Files, file => file.Role == "csharp");
        var definition = EffectPackageDefinition.Read(package);
        Assert.Equal(export, Assert.Single(definition.Exports).Id);
        var compiled = await CompiledEffectPackage.CompileAsync(definition, new NoCompiler(), new PackageImageValidator(),
            new PackageShaderValidator(), new PackageImageGraphValidator(), new PackageMaterialValidator());
        Assert.True(compiled.Succeeded);
        using var native = CompiledPackageGraphExports.Compile(definition);
        if (definition.Materials.Any(material => material.Id == export))
            Assert.NotNull(native.CreateMaterialProgram(native.Resources.GetMaterial(export), Canvas));
        else Assert.NotNull(native.CreateProgram(native.Resources.GetGraph(export), Canvas));
    }

    [Fact]
    public async Task 常量色材质把填充画成参数默认色()
    {
        var package = await Read("MaterialConstant");
        var material = new MaterialSpec.PackageMaterial(package.PackageId, package.Revision, "constant", EffectParameterOverrides.Parse("{}"));
        var project = Plain(package, []);
        project = project with { LyricStyle = project.LyricStyle with { UnsungFill = new() { Material = material }, SungFill = new() { Material = material } } };
        var compiled = await Compile(project);
        var snapshot = ProjectSnapshot.FromProject(project);
        await using var session = await compiled.CreateFactory(static () => new SkiaRenderPipeline(), ReplayReason.Export).CreateAsync(snapshot);
        var frame = await session.PrepareAsync(TimeUs, OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
        var compiledPackage = Assert.Single(compiled.Packages);
        using var native = CompiledPackageGraphExports.Compile(compiledPackage.Definition);
        using var resources = new SceneImageEffectResources([], null, [native.CreateMaterialProgram(compiledPackage.Resources.GetMaterial("constant"), Canvas)]);
        using var renderer = new SkiaRenderPipeline(null, null, null, resources);
        using var image = renderer.Render(RenderRequest.At(snapshot, TimeUs) with { PreparedFrame = frame, TransparentBackground = true });
        var pixels = Pixels(image.PixelMemory.ToArray());
        // 默认色 (0.95, 0.55, 0.2, 1) 的 BGRA 预乘字节；原本的白色填充不应残留。
        Assert.Contains(((byte)51, (byte)140, (byte)242, (byte)255), pixels);
        Assert.DoesNotContain(((byte)255, (byte)255, (byte)255, (byte)255), pixels);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task 关闭像素检查时材质帧的场景表面只提交不逐张等待GPU(bool gpu)
    {
        if (gpu && Environment.GetEnvironmentVariable("MAKO_GPU_SMOKE") != "1") return;
        var package = await Read("MaterialConstant");
        var material = new MaterialSpec.PackageMaterial(package.PackageId, package.Revision, "constant", EffectParameterOverrides.Parse("{}"));
        var project = Plain(package, []);
        project = project with { LyricStyle = project.LyricStyle with { UnsungFill = new() { Material = material }, SungFill = new() { Material = material } } };
        var compiled = await Compile(project);
        var snapshot = ProjectSnapshot.FromProject(project);
        await using var session = await compiled.CreateFactory(static () => new SkiaRenderPipeline(), ReplayReason.Export).CreateAsync(snapshot);
        var frame = await session.PrepareAsync(TimeUs, OutputMode.Opaque, RenderTarget.FromComposition(snapshot.Composition));
        var compiledPackage = Assert.Single(compiled.Packages);
        using var device = new GraphPixelCheckModeTests.CountingDevice(BaseMaterialRuntimeTests.Device(gpu));
        using var native = CompiledPackageGraphExports.CompileWithDevice(compiledPackage.Definition, null, device, pixelChecks: false);
        using var resources = new SceneImageEffectResources([], null, [native.CreateMaterialProgram(compiledPackage.Resources.GetMaterial("constant"), Canvas)]);
        using var renderer = new SkiaRenderPipeline(null, null, null, resources, false, device, false, graphPixelChecks: false);
        using var image = renderer.Render(RenderRequest.At(snapshot, TimeUs) with { PreparedFrame = frame });
        // 只有帧交付读回前同步一次；下层快照、部件、材质区域、层组装等场景表面都只提交不等待（旧实现逐张 Submit(true)）。
        // F3、F4 之后本帧场景表面约 9 张（F2 时 ≥10）。
        Assert.Equal(1, device.Submits);
        Assert.True(device.SubmitsWithoutWait >= 5, $"不等待提交 {device.SubmitsWithoutWait} 次");
        Assert.Contains(((byte)51, (byte)140, (byte)242, (byte)255), Pixels(image.PixelMemory.ToArray()));
    }

    [Fact]
    public async Task 背景媒体材质每帧只向背景来源取一次帧()
    {
        var package = await Read("MaterialMediaRequired");
        var material = new MaterialSpec.PackageMaterial(package.PackageId, package.Revision, "media-required", EffectParameterOverrides.Parse("{}"));
        var project = Plain(package, []);
        project = project with
        {
            Background = BackgroundSource.ForVideo("/tmp/perf-fixture-background.mp4"),
            LyricStyle = project.LyricStyle with { UnsungFill = new() { Material = material }, SungFill = new() { Material = material } },
        };
        var compiled = await Compile(project);
        var snapshot = ProjectSnapshot.FromProject(project);
        await using var session = await compiled.CreateFactory(static () => new SkiaRenderPipeline(), ReplayReason.Export).CreateAsync(snapshot);
        var frame = await session.PrepareAsync(TimeUs, OutputMode.Opaque, RenderTarget.FromComposition(snapshot.Composition));
        var compiledPackage = Assert.Single(compiled.Packages);
        using var native = CompiledPackageGraphExports.Compile(compiledPackage.Definition);
        using var resources = new SceneImageEffectResources([], null, [native.CreateMaterialProgram(compiledPackage.Resources.GetMaterial("media-required"), Canvas)]);
        var background = new CountingBackground(new SKColor(0, 224, 64));
        using var renderer = new SkiaRenderPipeline(null, background, null, resources);
        using var image = renderer.Render(RenderRequest.At(snapshot, TimeUs) with { PreparedFrame = frame });
        // 主画布背景与未唱／已唱、正文／注音四次材质执行共用同一帧；旧实现每次执行各取一次（共 5 次）。
        Assert.Equal(1, background.Calls);
        var pixels = Pixels(image.PixelMemory.ToArray());
        Assert.DoesNotContain(((byte)204, (byte)102, (byte)51, (byte)255), pixels); // 兜底色 #3366CC 不应出现
        Assert.DoesNotContain(((byte)255, (byte)255, (byte)255, (byte)255), pixels);
    }

    [Fact]
    public async Task 必需背景媒体缺失时材质退回包内兜底并说明原因()
    {
        // 设置了视频背景但该时刻无帧（文件丢失、解码失败同理）：旧实现报 GraphRequiredInputUnavailable 使整帧失败。
        var (image, diagnostics) = await RenderMediaMaterial("MaterialMediaRequired", "media-required",
            BackgroundSource.ForVideo("/tmp/perf-fixture-background.mp4"), new CountingBackground(null));
        using (image)
        {
            Assert.NotEmpty(diagnostics);
            Assert.All(diagnostics, diagnostic => Assert.Equal(("background", "BackgroundMediaUnavailable"), (diagnostic.InputId, diagnostic.Code)));
            Assert.Contains(((byte)204, (byte)102, (byte)51, (byte)255), Pixels(image.PixelMemory.ToArray())); // 兜底色 #3366CC
        }
    }

    [Fact]
    public async Task 可选背景媒体缺失时以工程底色代替且不报诊断()
    {
        // 没有背景媒体时画面背景就是底色：与「必需输入读到一张底色铺满的媒体」逐字节一致。
        var (optional, optionalDiagnostics) = await RenderMediaMaterial("MaterialMediaOptional", "media-optional", BackgroundSource.Color, null);
        var color = Composition.Create(640, 360, FrameRate.Fps30).BackgroundColor;
        var (required, requiredDiagnostics) = await RenderMediaMaterial("MaterialMediaRequired", "media-required",
            BackgroundSource.ForVideo("/tmp/perf-fixture-background.mp4"), new CountingBackground(new SKColor(color.Red, color.Green, color.Blue, color.Alpha)));
        using (optional) using (required)
        {
            Assert.Empty(optionalDiagnostics); Assert.Empty(requiredDiagnostics);
            Assert.Equal(required.PixelMemory.ToArray(), optional.PixelMemory.ToArray());
            Assert.DoesNotContain(((byte)204, (byte)102, (byte)51, (byte)255), Pixels(optional.PixelMemory.ToArray()));
        }
    }

    private static async Task<(RenderFrame Image, RenderMaterialDiagnostic[] Diagnostics)> RenderMediaMaterial(string name, string export,
        BackgroundSource backgroundSource, IBackgroundFrameSourceResolver? resolver)
    {
        var package = await Read(name);
        var material = new MaterialSpec.PackageMaterial(package.PackageId, package.Revision, export, EffectParameterOverrides.Parse("{}"));
        var project = Plain(package, []);
        project = project with
        {
            Background = backgroundSource,
            LyricStyle = project.LyricStyle with { UnsungFill = new() { Material = material }, SungFill = new() { Material = material } },
        };
        var compiled = await Compile(project);
        var snapshot = ProjectSnapshot.FromProject(project);
        await using var session = await compiled.CreateFactory(static () => new SkiaRenderPipeline(), ReplayReason.Export).CreateAsync(snapshot);
        var frame = await session.PrepareAsync(TimeUs, OutputMode.Opaque, RenderTarget.FromComposition(snapshot.Composition));
        var compiledPackage = Assert.Single(compiled.Packages);
        using var native = CompiledPackageGraphExports.Compile(compiledPackage.Definition);
        using var resources = new SceneImageEffectResources([], null, [native.CreateMaterialProgram(compiledPackage.Resources.GetMaterial(export), Canvas)]);
        using var renderer = new SkiaRenderPipeline(null, resolver, null, resources);
        var image = renderer.Render(RenderRequest.At(snapshot, TimeUs) with { PreparedFrame = frame });
        return (image, [.. image.MaterialDiagnostics]);
    }

    /// <summary>像视频一样每次返回同一张可变位图，并记下被取帧的次数。</summary>
    /// <remarks>颜色为 null 时模拟「该时刻无帧」。</remarks>
    private sealed class CountingBackground : IBackgroundFrameSourceResolver, IBackgroundFrameSource
    {
        private readonly SKBitmap? _bitmap;
        public CountingBackground(SKColor? color)
        {
            if (color is not { } value) return;
            _bitmap = new(new SKImageInfo(64, 36, SKColorType.Bgra8888, SKAlphaType.Unpremul));
            _bitmap.Erase(value);
        }
        public int Calls { get; private set; }
        public IBackgroundFrameSource? Resolve(BackgroundSource background) => this;
        public SKBitmap? GetFrame(long timeUs) { Calls++; return _bitmap; }
        public void Dispose() => _bitmap?.Dispose();
    }

    [Fact]
    public async Task 直通图与无效果逐字节一致且模糊图改变像素()
    {
        var passthrough = await Read("GraphPassthrough");
        byte[] plain = await RenderGraph(passthrough, null, null);
        Assert.Contains(plain, value => value != 0);
        Assert.Equal(plain, await RenderGraph(passthrough, "passthrough", new { }));
        Assert.NotEqual(plain, await RenderGraph(await Read("GraphBlur"), "blur", new { blurPx = 4 }));
    }

    private static async Task<byte[]> RenderGraph(EffectPackageContents package, string? export, object? parameters)
    {
        ProjectEffectInstance[] instances = export is null ? [] : [new()
        {
            Id = "graph", Name = "graph", PackageId = package.PackageId, Revision = package.Revision, ExportId = export,
            Subscription = new("project"), Parameters = EffectParameterOverrides.Parse(JsonSerializer.Serialize(parameters)),
        }];
        var project = Plain(package, instances);
        var snapshot = ProjectSnapshot.FromProject(project);
        if (export is null)
        {
            using var plain = new SkiaRenderPipeline().Render(RenderRequest.At(snapshot, TimeUs) with { TransparentBackground = true });
            return plain.PixelMemory.ToArray();
        }
        var compiled = await Compile(project);
        await using var session = await compiled.CreateFactory(static () => new SkiaRenderPipeline(), ReplayReason.Export).CreateAsync(snapshot);
        var frame = await session.PrepareAsync(TimeUs, OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
        using var native = CompiledPackageGraphExports.Compile(Assert.Single(compiled.Packages).Definition);
        using var resources = new SceneImageEffectResources([native.CreateProgram(native.Resources.GetGraph(export), Canvas)], null, []);
        using var renderer = new SkiaRenderPipeline(null, null, null, resources);
        using var image = renderer.Render(RenderRequest.At(snapshot, frame.TimeUs) with { PreparedFrame = frame, TransparentBackground = true });
        return image.PixelMemory.ToArray();
    }

    private static Project Plain(EffectPackageContents package, ProjectEffectInstance[] instances)
    {
        var line = LyricLine.Create("line", "AB", new(0, 602_000_000), TimingStatus.Edited,
            [LyricToken.Create("token", "AB", new(0, 602_000_000), TimingStatus.Edited)]);
        return Project.Create("Perf fixture", Composition.Create(640, 360, FrameRate.Fps30, outputDurationUs: 603_000_000),
            LyricsDocument.Create([line]), lyricStyle: LyricStyle.Default with { FontFamilyName = "DejaVu Sans", FontSizePx = 64,
                SungColor = RgbaColor.White, UnsungColor = RgbaColor.White, Stroke = null, Shadow = null, Glow = null })
            with { Effects = new([package], instances), EnterAnimation = null };
    }

    private static async Task<CompiledProjectEffects> Compile(Project project)
    {
        var compiled = await CompiledProjectEffects.CompileAsync(project, new NoCompiler(), new PackageImageValidator(),
            new PackageShaderValidator(), new PackageImageGraphValidator(), new PackageMaterialValidator());
        Assert.True(compiled.Succeeded); return compiled;
    }

    private static HashSet<(byte, byte, byte, byte)> Pixels(byte[] bgra)
    {
        var set = new HashSet<(byte, byte, byte, byte)>();
        for (int i = 0; i < bgra.Length; i += 4) set.Add((bgra[i], bgra[i + 1], bgra[i + 2], bgra[i + 3]));
        return set;
    }

    private static Task<EffectPackageContents> Read(string name)
        => EffectPackageStorage.ReadSupportedDirectoryAsync(Path.Combine(AppContext.BaseDirectory, "effect-packages-v2-perf", name));
}
