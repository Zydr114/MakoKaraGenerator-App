using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.Compilation;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class PackageImageGraphsTests
{
    private static ImageGraphFrameContext Frame(bool transparent = false) => new(500_000, 16, 16, new(0, 0, 16, 16), 1, 1, ParameterValues.Empty, transparent);
    private static byte[] Png()
    {
        using var bitmap = new SKBitmap(2, 2); bitmap.Erase(SKColors.Red); bitmap.SetPixel(1, 1, SKColors.Blue);
        using var image = SKImage.FromBitmap(bitmap); using var data = image.Encode(SKEncodedImageFormat.Png, 100); return data.ToArray();
    }
    private static byte[] Bytes(SkiaGraphSurface surface) { using var bitmap = SKBitmap.FromImage(surface.Image); return bitmap.Bytes; }
    private static ScriptCompiler Compiler() => new(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));

    [Fact]
    public async Task 实际CPU图文件产生像素且全部图资源验证后才允许真实DLL不伪造图句柄()
    {
        var contents = EffectPackageGraphFixture.Contents(); var package = EffectPackageDefinition.Read(contents);
        var missing = await Assert.ThrowsAsync<EffectPackageException>(() => CompiledEffectPackage.CompileAsync(package, Compiler(), null, new PackageShaderValidator()));
        Assert.Equal("PackageImageGraphValidatorMissing", missing.Code);
        var compiled = await CompiledEffectPackage.CompileAsync(package, Compiler(), null, new PackageShaderValidator(), new PackageImageGraphValidator());
        Assert.True(compiled.Artifact.Succeeded); Assert.Throws<KeyNotFoundException>(() => compiled.Resources.GetGraph(EffectPackageGraphFixture.GraphPath));
        using var graph = CompiledImageGraph.Compile(package.ImageGraphs[EffectPackageGraphFixture.GraphPath].Plan);
        using var result = graph.Execute(Frame(), new Dictionary<string, SkiaGraphInput>());
        using var pixel = SKBitmap.FromImage(result.Output.Image); Assert.InRange(pixel.GetPixel(8, 8).Red, 63, 65); Assert.Equal(255, pixel.GetPixel(8, 8).Alpha);
        var definition = package.ImageGraphs[EffectPackageGraphFixture.GraphPath].Plan.Definition;
        using var manual = CompiledImageGraph.Compile(ImageGraphCompiler.Compile(definition, package.Parameters.Select(parameter => parameter.Constraint), package.ShaderPrograms));
        using var expected = manual.Execute(Frame(), new Dictionary<string, SkiaGraphInput>()); Assert.Equal(Bytes(expected.Output), Bytes(result.Output));
    }

    [Fact]
    public void 包图片完整解码原尺寸显式变换与释放取消错误严格执行()
    {
        var json = EffectPackageGraphFixture.Graph(); var imageInput = EffectPackageGraphFixture.Input("image", "ResourceImage"); imageInput.Remove("capture"); imageInput["resource"] = "images/probe.png";
        json["inputs"]!.AsArray().Add(imageInput);
        json["nodes"] = new JsonArray(new JsonObject { ["id"] = "move", ["op"] = "Transform", ["input"] = "input:image", ["matrix"] = new JsonArray(1, 0, 0, 1, 5, 6),
            ["outputBounds"] = new JsonArray(0, 0, 16, 16), ["bounds"] = EffectPackageGraphFixture.Bounds(), ["edge"] = "transparent" }); json["output"] = "node:move";
        var contents = EffectPackageGraphFixture.Contents(json, extraFiles: new() { ["images/probe.png"] = Png() });
        var definition = EffectPackageImageGraphs.Read(contents)[EffectPackageGraphFixture.GraphPath];
        using var images = PackageGraphImages.Load(contents); Assert.Equal((2, 2), images.GetPixelSize("images/probe.png"));
        var input = definition.Plan.Definition.Inputs.Single(input => input.Id == "image");
        var descriptor = new GraphSurfaceDescriptor(new(0, 0, 2, 2), GraphCoordinateSpace.Canvas, SceneMatrix.Identity, 1, 1);
        var image = images.GetInput(input, descriptor); using var borrow = image.Surface;
        using var graph = CompiledImageGraph.Compile(definition.Plan); using var result = graph.Execute(Frame(), new Dictionary<string, SkiaGraphInput> { ["image"] = image });
        using var pixel = SKBitmap.FromImage(result.Output.Image); Assert.Equal(SKColors.Red, pixel.GetPixel(5, 6)); Assert.Equal(SKColors.Blue, pixel.GetPixel(6, 7)); Assert.Equal(0, pixel.GetPixel(8, 8).Alpha);
        Assert.Equal("PackageGraphImageGeometryInvalid", Assert.Throws<EffectPackageException>(() => images.GetInput(input, descriptor with { Bounds = new(0, 0, 16, 16) })).Code);
        Assert.Equal("PackageImageBudgetExceeded", Assert.Throws<EffectPackageException>(() => PackageGraphImages.Load(contents, new() { MaximumBytes = 15 })).Code);
        Assert.Equal("PackageImageDecodeFailed", Assert.Throws<EffectPackageException>(() => PackageGraphImages.Load(EffectPackageGraphFixture.Contents(json, extraFiles: new() { ["images/probe.png"] = [1, 2, 3] }))).Code);
        using var cancel = new CancellationTokenSource(); cancel.Cancel(); Assert.Throws<OperationCanceledException>(() => PackageGraphImages.Load(contents, cancellationToken: cancel.Token));
        result.Dispose(); borrow.Dispose(); images.Dispose(); images.Dispose(); Assert.Throws<ObjectDisposedException>(() => images.GetPixelSize("images/probe.png"));
    }

    [Fact]
    public void 背景依赖透明图严格返回声明原输入且不会忽略坏程序()
    {
        var json = EffectPackageGraphFixture.Graph(); var background = EffectPackageGraphFixture.Input("background", "BackgroundMedia"); background.Remove("capture"); background["backgroundRequirement"] = "media";
        json["inputs"]!.AsArray().Add(background); json["output"] = "input:background"; json["bypassInput"] = "input:original";
        var definition = EffectPackageImageGraphs.Read(EffectPackageGraphFixture.Contents(json))[EffectPackageGraphFixture.GraphPath];
        using var graph = CompiledImageGraph.Compile(definition.Plan);
        using var bitmap = new SKBitmap(new SKImageInfo(16, 16, SKColorType.Bgra8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb())); bitmap.Erase(SKColors.Green);
        using var image = SKImage.FromBitmap(bitmap); using var original = SkiaGraphSurface.Borrow(image, new(new(0, 0, 16, 16), GraphCoordinateSpace.Canvas, SceneMatrix.Identity, 1, 1), ShaderSurfaceKind.Color);
        var inputs = new Dictionary<string, SkiaGraphInput> { ["original"] = new(original, GraphInputSource.TargetColor) };
        using var result = graph.Execute(Frame(true), inputs); Assert.Same(original, result.Output); Assert.Single(result.Diagnostics);
        // 不透明却没有背景媒体（2026-11-10 用户确认）：与透明同路旁路，原因写明缺背景媒体，不再使整帧失败。
        using var missing = graph.Execute(Frame(), inputs); Assert.Same(original, missing.Output);
        Assert.Equal(("background", "BackgroundMediaUnavailable"), (Assert.Single(missing.Diagnostics).InputId, missing.Diagnostics[0].Reason));
        var bad = EffectPackageImageGraphs.Read(EffectPackageGraphFixture.Contents(json, source: "bad shader"));
        var error = Assert.Throws<EffectPackageException>(() => new PackageImageGraphValidator().Validate(bad));
        Assert.Equal("ShaderCompilationFailed", error.Code); Assert.StartsWith(EffectPackageGraphFixture.GraphPath, error.Path, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("token")] [InlineData("line")] [InlineData("node")]
    public void 未实施空间不能按Canvas执行(string space)
    {
        var json = EffectPackageGraphFixture.Graph(); json["bounds"]!["space"] = space; json["inputs"]![0]!["space"] = space;
        using var graph = CompiledImageGraph.Compile(EffectPackageImageGraphs.Read(EffectPackageGraphFixture.Contents(json))[EffectPackageGraphFixture.GraphPath].Plan);
        Assert.Equal("UnsupportedGraphExecutionSpace", Assert.Throws<ImageGraphException>(() => graph.Execute(Frame(), new Dictionary<string, SkiaGraphInput>())).Code);
    }

    [Theory]
    [InlineData(false, "PackageMaterialRegionBindingRequired")]
    [InlineData(true, "PackageOwnedOverlayCapturePending")]
    public void 未实施的捕获能力在生产适配时拒绝不偷偷忽略(bool owned, string code)
    {
        var json = EffectPackageGraphFixture.Graph(); var capture = json["inputs"]![0]!["capture"]!;
        if (owned) capture["includeOwnedOverlay"] = true; else capture["region"] = "materialRegion";
        var definition = EffectPackageImageGraphs.Read(EffectPackageGraphFixture.Contents(json))[EffectPackageGraphFixture.GraphPath];
        using var graph = CompiledImageGraph.Compile(definition.Plan);
        var resources = new ResourceCatalog("probe", "sha256:" + new string('a', 64), [], ["graph"], []);
        Assert.Equal(code, Assert.Throws<EffectPackageException>(() => SceneImageGraphProgram.FromPackage(resources.GetGraph("graph"), graph, definition, new(0, 0, 16, 16))).Code);
    }
}
