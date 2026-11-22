using System.Text.Json;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.Execution;

namespace MakoKaraGenerator.Rendering.Tests;

/// <summary>第 12 条：图导出按绑定目标的实际区域执行，像素与整屏框一致，高分辨率下表面只占本行区域。</summary>
public sealed partial class PerfFixturePackageTests
{
    /// <remarks>「blur+passthrough」：模糊图外扩 24 px 后再接不外扩的直通图，直通图的目标框须计入前序外扩，否则会裁掉模糊边缘。</remarks>
    [Theory]
    [InlineData(false, "passthrough", false)] [InlineData(false, "passthrough", true)]
    [InlineData(false, "blur", false)] [InlineData(false, "blur", true)]
    [InlineData(false, "blur+passthrough", false)] [InlineData(false, "blur+passthrough", true)]
    [InlineData(true, "passthrough", false)] [InlineData(true, "passthrough", true)]
    [InlineData(true, "blur", false)] [InlineData(true, "blur", true)]
    [InlineData(true, "blur+passthrough", false)] [InlineData(true, "blur+passthrough", true)]
    public async Task 收紧图目标框与整屏框像素一致(bool gpu, string kind, bool decorated)
    {
        if (gpu && Environment.GetEnvironmentVariable("MAKO_GPU_SMOKE") != "1") return;
        var (full, _) = await RenderGraphRegion(kind, decorated, gpu, false, null);
        var (tight, _) = await RenderGraphRegion(kind, decorated, gpu, true, null);
        Assert.Contains(full, value => value != 0);
        // CPU 逐字节一致。Vulkan 下图内 σ8 模糊靠目标框按缩小倍数对齐与整屏框一致（不对齐时实测差 9／255），
        // 剩下的只有 O7 已知的阴影、辉光差异（Linux 实测 1／255，Windows Vulkan 驱动实测 <= 3／255）。
        if (!gpu || kind == "passthrough" && !decorated) Assert.Equal(full, tight);
        else Assert.InRange(full.Zip(tight, (a, b) => Math.Abs(a - b)).Max(), 0, OperatingSystem.IsWindows() ? 3 : 2);
    }

    [Fact]
    public async Task 高分辨率输出时图只占本行区域()
    {
        var target = new RenderTarget { PixelWidth = 2560, PixelHeight = 1440 };
        const long screen = 2560L * 1440 * 4;
        var (full, fullPeak) = await RenderGraphRegion("blur", true, false, false, target);
        var (tight, tightPeak) = await RenderGraphRegion("blur", true, false, true, target);
        Assert.Equal(full, tight);
        // 整屏框时图输入、模糊临时表面与输出都是整张画布（实测 179,650,560 字节，约 12 屏）；
        // 收紧后剩下层快照的一屏，加上本行区域的各张表面（640×360 画布按 4 倍密度输出，本行区域较大），实测 32,665,600 字节。
        Assert.True(fullPeak >= 8 * screen, $"整屏框峰值 {fullPeak}");
        Assert.True(tightPeak < 3 * screen, $"收紧后峰值 {tightPeak}");
    }

    private static async Task<(byte[] Pixels, long PeakBytes)> RenderGraphRegion(string kind, bool decorated, bool gpu, bool tight, RenderTarget? target)
    {
        var blur = await Read("GraphBlur");
        var passthrough = await Read("GraphPassthrough");
        List<(EffectPackageContents Package, string Export, object Parameters)> chain = [];
        if (kind.Contains("blur", StringComparison.Ordinal)) chain.Add((blur, "blur", new { blurPx = 8 }));
        if (kind.Contains("passthrough", StringComparison.Ordinal)) chain.Add((passthrough, "passthrough", new { }));
        var project = Plain(blur, []);
        var style = project.LyricStyle;
        if (decorated)
            style = style with
            {
                Stroke = new StrokeSpec { WidthPx = 6, Join = MakoKaraGenerator.Core.Projects.StrokeJoin.Miter, Color = new RgbaColor(20, 40, 200) },
                Shadow = new ShadowSpec { DistancePx = 14, AngleDegrees = 135, BlurRadiusPx = 4, Color = new RgbaColor(0, 0, 0, 200) },
                Glow = new GlowSpec { SpreadPx = 3, BlurRadiusPx = 5, Color = new RgbaColor(255, 220, 0, 180) },
            };
        ProjectEffectInstance[] instances = [.. chain.Select((item, index) => new ProjectEffectInstance
        {
            Id = "graph-" + index, Name = item.Export, PackageId = item.Package.PackageId, Revision = item.Package.Revision, ExportId = item.Export,
            Subscription = new("project"), Parameters = EffectParameterOverrides.Parse(JsonSerializer.Serialize(item.Parameters)),
        })];
        project = project with { LyricStyle = style, Effects = new([blur, passthrough], instances) };
        var compiled = await Compile(project);
        var snapshot = ProjectSnapshot.FromProject(project);
        var output = target ?? RenderTarget.FromComposition(snapshot.Composition);
        await using var session = await compiled.CreateFactory(static () => new SkiaRenderPipeline()).CreateAsync(snapshot);
        var frame = await session.PrepareAsync(TimeUs, OutputMode.Opaque, output);
        using var device = BaseMaterialRuntimeTests.Device(gpu);
        List<IDisposable> natives = [];
        try
        {
            List<SceneImageGraphProgram> programs = [];
            foreach (var (package, export, _) in chain)
            {
                var definition = compiled.Packages.Single(item => item.Definition.Contents.PackageId == package.PackageId).Definition;
                var native = CompiledPackageGraphExports.CompileWithDevice(definition, null, device);
                natives.Add(native);
                programs.Add(native.CreateProgram(native.Resources.GetGraph(export), Canvas));
            }
            using var resources = new SceneImageEffectResources(programs, null, []);
            using var budget = new ImageSurfaceBudgetSession();
            using var renderer = new SkiaRenderPipeline(null, null, budget, resources, false, device, false) { TightObjectRegions = tight };
            using var image = renderer.Render(RenderRequest.At(snapshot, frame.TimeUs) with { PreparedFrame = frame, Target = output });
            Assert.Equal(chain.Count, frame.ImageEffects.Length);
            Assert.Empty(image.ImageEffectDiagnostics);
            return (image.PixelMemory.ToArray(), budget.Usage.PeakBytes);
        }
        finally { for (int i = natives.Count - 1; i >= 0; i--) natives[i].Dispose(); }
    }
}
