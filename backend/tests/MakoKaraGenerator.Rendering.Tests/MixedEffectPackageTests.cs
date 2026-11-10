using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Execution;
using SkiaSharp;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed partial class MixedEffectPackageTests
{
    private const string Fingerprint = "mixed-package";
    private static ScriptCompiler Compiler() => new(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
    private static Task<CompiledEffectPackage> Compile(EffectPackageContents contents)
        => CompiledEffectPackage.CompileAsync(EffectPackageDefinition.Read(contents), Compiler(), new PackageImageValidator(),
            new PackageShaderValidator(), new PackageImageGraphValidator(), new PackageMaterialValidator());
    private static ProjectSnapshot Snapshot() => ProjectSnapshot.FromProject(Project.Create("混合包", Composition.Create(640, 360, FrameRate.Fps30, outputDurationUs: 5_000_000),
        LyricsDocument.Create([LyricLine.Create("row", "歌", new(0, 5_000_000), TimingStatus.Edited,
            [LyricToken.Create("token", "歌", new(0, 4_000_000), TimingStatus.Edited)]) with { RubyAnnotations = [RubyAnnotation.Create("ruby", "token", "か")] }]),
        lyricStyle: LyricStyle.Default with { FontFamilyName = "Noto Sans CJK SC", FontSizePx = 64, Stroke = null, Shadow = null, Opacity = .7f }))
        with { EffectInputsFingerprint = Fingerprint };
    private static byte[] Bytes(SkiaGraphSurface surface) { using var bitmap = SKBitmap.FromImage(surface.Image); return bitmap.Bytes; }

    [Fact]
    public async Task 全部资源验证后纯包无DLL且真实材质与图句柄严格分开()
    {
        var definition = EffectPackageDefinition.Read(MixedEffectPackageFixture.Contents(pure: true));
        var missing = await Assert.ThrowsAsync<EffectPackageException>(() => CompiledEffectPackage.CompileAsync(definition, Compiler(),
            new PackageImageValidator(), new PackageShaderValidator(), new PackageImageGraphValidator()));
        Assert.Equal("PackageMaterialValidatorMissing", missing.Code);
        var compiled = await Compile(definition.Contents); Assert.True(compiled.Succeeded); Assert.Null(compiled.ControllerArtifact);
        Assert.Equal("PackageCSharpArtifactUnavailable", Assert.Throws<EffectPackageException>(() => compiled.Artifact).Code);
        var graph = compiled.CreateEffect("tint", new("graph"), new Fx.EffectBinding.Project(), 1);
        Assert.Null(graph.Artifact); Assert.Null(graph.EntryType);
        Assert.IsType<EffectGraphDefinition>(graph.PackageDeclaration);
        Assert.Equal(new Fx.EffectPackageIdentity(definition.Contents.PackageId, definition.Contents.Revision, "tint"), graph.PackageIdentity);
        Assert.Equal(definition.Contents.Revision, graph.RuntimeHash);
        Assert.Throws<KeyNotFoundException>(() => compiled.Resources.GetGraph(EffectPackageGraphFixture.GraphPath));
        Assert.Throws<KeyNotFoundException>(() => compiled.Resources.GetGraph("ink")); Assert.Throws<KeyNotFoundException>(() => compiled.Resources.GetMaterial("tint"));
        Assert.Equal(new Fx.ColorRgba(51 / 255d, 128 / 255d, 204 / 255d, 160 / 255d),
            Assert.IsType<Fx.MaterialValue.SolidMaterial>(compiled.GetMaterialFallback(compiled.Resources.GetMaterial("ink"))).Color);
        using var native = CompiledPackageGraphExports.Compile(definition);
        Assert.Equal(compiled.Resources.GetGraph("tint"), native.Resources.GetGraph("tint"));
        var program = native.CreateProgram(compiled.Resources.GetGraph("tint"), new(0, 0, 640, 360));
        Assert.Same(program.Graph, native.GetMaterialGraph(compiled.Resources.GetMaterial("ink")));
        var other = await Compile(MixedEffectPackageFixture.Contents(pure: true, fallback: MixedEffectPackageFixture.Gradient()));
        Assert.NotEqual(compiled.Definition.Contents.Revision, other.Definition.Contents.Revision);
        Assert.Equal("InvalidGraphResource", Assert.Throws<EffectPackageException>(() => native.CreateProgram(other.Resources.GetGraph("tint"), program.Bounds)).Code);
        Assert.Equal("InvalidMaterialResource", Assert.Throws<EffectPackageException>(() => compiled.GetMaterialFallback(other.Resources.GetMaterial("ink"))).Code);
        native.Dispose(); native.Dispose();
        Assert.Throws<ObjectDisposedException>(() => native.CreateProgram(compiled.Resources.GetGraph("tint"), program.Bounds));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => CompiledPackageGraphExports.Compile(definition, cancellationToken: cancel.Token));
    }

    [Fact]
    public async Task 真实混合包的控制器绑定真实导出经生产捕获产生像素保持alpha()
    {
        var compiled = await Compile(MixedEffectPackageFixture.Contents());
        Assert.True(compiled.Succeeded, string.Join('\n', compiled.Artifact.Diagnostics));
        var effect = compiled.CreateEffect("controller", new("mixed"), new Fx.EffectBinding.Project(), 1);
        Assert.Equal(Fx.EffectCapabilities.ImageEffects, effect.Capabilities); Assert.Same(compiled.Definition, effect.PackageDefinition);
        var snapshot = Snapshot(); var factory = new PureFrameSourceFactory([effect], Fingerprint, static () => new SkiaRenderPipeline());
        await using var session = await factory.CreateAsync(snapshot);
        var prepared = await session.PrepareAsync(1_000_000, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
        var binding = Assert.Single(prepared.ImageEffects);
        Assert.Equal(effect.Id, binding.InstanceId); Assert.Equal(compiled.Resources.GetGraph("tint"), binding.Binding.Graph);
        Assert.Equal(.5, binding.Binding.Parameters.GetNumber("amount"));
        using var native = CompiledPackageGraphExports.Compile(compiled.Definition); using SkiaRenderPipeline renderer = new();
        var program = native.CreateProgram(binding.Binding.Graph, new(0, 0, 640, 360));
        var request = RenderRequest.At(snapshot, prepared.TimeUs) with { PreparedFrame = prepared, TransparentBackground = true };
        using var result = renderer.ExecuteSceneImageEffect(request, binding, program, new Dictionary<string, SkiaGraphInput>(), null);
        using var original = renderer.CaptureSceneTarget(request, new SceneCaptureTarget.Line(Assert.Single(prepared.FinalLineGeometry).Target),
            program.GetInputRecipe("original"), GraphInputSource.TargetColor, program.Bounds, program.Graph.Plan.Budget);
        byte[] before = Bytes(original), actual = Bytes(result.GraphResult.Output); int visible = 0;
        for (int index = 0; index < actual.Length; index++)
        {
            if (index % 4 == 3) { Assert.Equal(before[index], actual[index]); if (actual[index] > 0) visible++; }
            else Assert.InRange(Math.Abs(before[index] * .5 - actual[index]), 0, 1);
        }
        Assert.True(visible > 100);
        using var expected = program.Graph.Execute(new(prepared.TimeUs, 640, 360, program.Bounds, 1, 1, binding.Binding.Parameters, true),
            new Dictionary<string, SkiaGraphInput> { ["original"] = new(original, GraphInputSource.TargetColor) });
        Assert.Equal(Bytes(expected.Output), actual);
        var gate = Assert.Throws<EffectFailureException>(() => renderer.Render(request));
        Assert.Equal("ImageEffectResourcesRequired", gate.Code); Assert.Equal(effect.Id, gate.InstanceId);
    }

    [Fact]
    public async Task 背景依赖实际混合导出透明执行只返回声明原输入且提供诊断()
    {
        var compiled = await Compile(MixedEffectPackageFixture.Contents(background: true));
        var snapshot = Snapshot(); var factory = new PureFrameSourceFactory([compiled.CreateEffect("controller", new("bypass"), new Fx.EffectBinding.Project(), 1)],
            Fingerprint, static () => new SkiaRenderPipeline());
        await using var session = await factory.CreateAsync(snapshot);
        var prepared = await session.PrepareAsync(1_000_000, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
        var binding = Assert.Single(prepared.ImageEffects); using var native = CompiledPackageGraphExports.Compile(compiled.Definition);
        var program = native.CreateProgram(binding.Binding.Graph, new(0, 0, 640, 360));
        using SkiaRenderPipeline renderer = new(); var request = RenderRequest.At(snapshot, prepared.TimeUs) with { PreparedFrame = prepared, TransparentBackground = true };
        using var result = renderer.ExecuteSceneImageEffect(request, binding, program, new Dictionary<string, SkiaGraphInput>(), null);
        Assert.Single(result.GraphResult.Diagnostics);
        using var original = renderer.CaptureSceneTarget(request, new SceneCaptureTarget.Line(Assert.Single(prepared.FinalLineGeometry).Target),
            program.GetInputRecipe("original"), GraphInputSource.TargetColor, program.Bounds, program.Graph.Plan.Budget);
        Assert.Equal(Bytes(original), Bytes(result.GraphResult.Output));
    }

    [Fact]
    public async Task 背景依赖实际混合导出不透明缺背景媒体时同路旁路并说明原因()
    {
        var compiled = await Compile(MixedEffectPackageFixture.Contents(background: true));
        var snapshot = Snapshot(); var factory = new PureFrameSourceFactory([compiled.CreateEffect("controller", new("bypass"), new Fx.EffectBinding.Project(), 1)],
            Fingerprint, static () => new SkiaRenderPipeline());
        await using var session = await factory.CreateAsync(snapshot);
        var prepared = await session.PrepareAsync(1_000_000, Fx.OutputMode.Opaque, RenderTarget.FromComposition(snapshot.Composition));
        var binding = Assert.Single(prepared.ImageEffects); using var native = CompiledPackageGraphExports.Compile(compiled.Definition);
        var program = native.CreateProgram(binding.Binding.Graph, new(0, 0, 640, 360));
        using SkiaRenderPipeline renderer = new(); var request = RenderRequest.At(snapshot, prepared.TimeUs) with { PreparedFrame = prepared };
        // 工程没有背景媒体：旧实现报 GraphRequiredInputUnavailable 使整帧失败。
        using var result = renderer.ExecuteSceneImageEffect(request, binding, program, new Dictionary<string, SkiaGraphInput>(), null);
        var diagnostic = Assert.Single(result.GraphResult.Diagnostics);
        Assert.Equal(("background", "BackgroundMediaUnavailable"), (diagnostic.InputId, diagnostic.Reason));
        using var original = renderer.CaptureSceneTarget(request, new SceneCaptureTarget.Line(Assert.Single(prepared.FinalLineGeometry).Target),
            program.GetInputRecipe("original"), GraphInputSource.TargetColor, program.Bounds, program.Graph.Plan.Budget);
        Assert.Equal(Bytes(original), Bytes(result.GraphResult.Output));
    }

    [Theory]
    [InlineData("missing", "PackageParameterSetMismatch")]
    [InlineData("extra", "PackageParameterSetMismatch")]
    [InlineData("type", "PackageParameterTypeMismatch")]
    [InlineData("range", "PackageParameterOutOfRange")]
    [InlineData("binding", "PackageBindingNotAllowed")]
    [InlineData("target", "UndeclaredPackageOutput")]
    [InlineData("mapping", "PackageGraphMappingPending")]
    [InlineData("requires", "PackageOutputPlanPending")]
    public async Task 真正DLL不能借图句柄绕过参数绑定或具名输出协议(string change, string code)
    {
        string expression = change switch
        {
            "missing" => "ParameterValues.Empty",
            "extra" => "new([new(\"amount\",new ParameterValue.Number(.5)),new(\"extra\",new ParameterValue.Number(1))])",
            "type" => "new([new(\"amount\",new ParameterValue.Whole(1))])",
            "range" => "new([new(\"amount\",new ParameterValue.Number(2))])",
            _ => "new([new(\"amount\",new ParameterValue.Number(.5))])",
        };
        string source = "using MakoKaraGenerator.Scripting; public sealed class PackEffect : EffectBase { public override void Evaluate(FrameContext f,FrameWriter w) { foreach(var line in f.Presentations.LinesInBinding) w.ImageEffects.Bind(line.Target,f.Resources.GetGraph(\"tint\")," + expression + "); } }";
        var compiled = await Compile(MixedEffectPackageFixture.Contents(manifest =>
        {
            var graph = manifest["exports"]![2]!;
            if (change == "binding") graph["bindingKinds"] = new JsonArray("tokens");
            if (change == "target") graph["outputs"]![0]!["target"] = "tokenFamily";
            if (change == "requires") graph["outputs"]![0]!["requires"] = new JsonArray("DistanceField");
        }, controllerSource: source, graphChange: graph => { if (change == "mapping") graph["anchorMode"] = "ImageOnly"; }));
        Assert.True(compiled.Succeeded, string.Join('\n', compiled.Artifact.Diagnostics));
        var effect = compiled.CreateEffect("controller", new("restricted"), new Fx.EffectBinding.Project(), 1);
        await using var session = await new PureFrameSourceFactory([effect], Fingerprint, static () => new SkiaRenderPipeline()).CreateAsync(Snapshot());
        var error = await Assert.ThrowsAsync<EffectFailureException>(async () => await session.PrepareAsync(1_000_000, Fx.OutputMode.Transparent, new() { PixelWidth = 640, PixelHeight = 360 }));
        Assert.Equal(code, error.Code); Assert.Equal(effect.Id, error.InstanceId); Assert.Equal(effect.Artifact!.ArtifactHash, error.ArtifactHash);
    }
}
