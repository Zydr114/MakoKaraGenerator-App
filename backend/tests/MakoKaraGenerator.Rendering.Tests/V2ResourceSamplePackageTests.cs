using System.Text.Json;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.Compilation;

namespace MakoKaraGenerator.Rendering.Tests;

/// <summary>真实纯资源 v2 样例（CustomInk／LineGlitch／GlassInk）：无 DLL 准入、原生程序与整行图像素。</summary>
public sealed class V2ResourceSamplePackageTests
{
    private sealed class NoCompiler : IScriptCompilationService
    {
        private readonly ScriptCompiler _real = new(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
        public ScriptCompilationIdentity Identity => _real.Identity;
        public Task<ScriptCompilationResult> CompileAsync(IEnumerable<ScriptSource> sources, IEnumerable<string> entryTypes, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("resource-only sample must never compile a DLL");
    }

    [Theory]
    [InlineData("CustomInk", new[] { "custom-ink" })]
    [InlineData("LineGlitch", new[] { "line-glitch" })]
    [InlineData("GlassInk", new[] { "glass-media", "glass-backdrop" })]
    public async Task 纯资源样例按v2准入且全部图导出可原生编译(string name, string[] exports)
    {
        var package = await Read(name);
        Assert.True(EffectPackageProfiles.IsSupported(package.Profile));
        Assert.DoesNotContain(package.Files, file => file.Role == "csharp");
        var definition = EffectPackageDefinition.Read(package);
        var compiled = await CompiledEffectPackage.CompileAsync(definition, new NoCompiler(), new PackageImageValidator(),
            new PackageShaderValidator(), new PackageImageGraphValidator(), new PackageMaterialValidator());
        Assert.True(compiled.Succeeded);
        using var native = CompiledPackageGraphExports.Compile(definition);
        foreach (string export in exports)
        {
            if (definition.Materials.Any(material => material.Id == export))
                Assert.NotNull(native.CreateMaterialProgram(native.Resources.GetMaterial(export), new(0, 0, 640, 360)));
            else Assert.NotNull(native.CreateProgram(native.Resources.GetGraph(export), new(0, 0, 640, 360)));
        }
    }

    [Fact]
    public async Task LineGlitch整行分片真实位移且强度为零时与无效果一致()
    {
        var package = await Read("LineGlitch");
        using var native = CompiledPackageGraphExports.Compile(EffectPackageDefinition.Read(package));
        byte[] plain = await RenderAsync(package, native, null);
        byte[] zero = await RenderAsync(package, native, 0);
        byte[] glitch = await RenderAsync(package, native, 6);
        Assert.Contains(plain, value => value != 0);
        Assert.Equal(plain, zero);
        Assert.NotEqual(plain, glitch);
    }

    private static async Task<byte[]> RenderAsync(EffectPackageContents package, CompiledPackageGraphExports native, double? strength)
    {
        ProjectEffectInstance[] instances = strength is { } value ? [new()
        {
            Id = "glitch", Name = "glitch", PackageId = package.PackageId, Revision = package.Revision, ExportId = "line-glitch",
            Subscription = new("project"), Parameters = EffectParameterOverrides.Parse(JsonSerializer.Serialize(new { strengthPx = value })),
        }] : [];
        var snapshot = V2DirectAndMaterialPackageFixture.Snapshot(package, instances);
        if (instances.Length == 0)
        {
            using var plain = new SkiaRenderPipeline().Render(RenderRequest.At(snapshot, 1_250_000) with { TransparentBackground = true });
            return plain.PixelMemory.ToArray();
        }
        var compiled = await V2DirectAndMaterialPackageFixture.Compile(snapshot, new NoCompiler());
        await using var session = await V2DirectAndMaterialPackageFixture.Factory(snapshot, compiled).CreateAsync(snapshot);
        var frame = await V2DirectAndMaterialPackageFixture.Prepare(session, snapshot, 1_250_000);
        using var resources = new SceneImageEffectResources([native.CreateProgram(native.Resources.GetGraph("line-glitch"), new(0, 0, 640, 360))], null, []);
        using var renderer = new SkiaRenderPipeline(null, null, null, resources);
        using var image = renderer.Render(RenderRequest.At(snapshot, frame.TimeUs) with { PreparedFrame = frame, TransparentBackground = true });
        return image.PixelMemory.ToArray();
    }

    private static Task<EffectPackageContents> Read(string name)
        => EffectPackageStorage.ReadSupportedDirectoryAsync(Path.Combine(AppContext.BaseDirectory, "effect-packages-v2", name));
}
