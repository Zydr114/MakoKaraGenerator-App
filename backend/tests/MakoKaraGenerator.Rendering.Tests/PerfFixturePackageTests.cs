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

namespace MakoKaraGenerator.Rendering.Tests;

/// <summary>M12 测速用测试包（tests/Fixtures/ScriptEffectsV2Perf）：准入、原生编译与最小像素检查。</summary>
public sealed class PerfFixturePackageTests
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
        Assert.True(EffectPackageProfiles.IsV2(package.Profile));
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
