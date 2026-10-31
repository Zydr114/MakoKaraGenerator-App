using System.Text;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Execution;
using SkiaSharp;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class PackageControllerRenderingTests
{
    private static ScriptCompiler Compiler() => new(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
    private static EffectPackageDefinition Read(Dictionary<string, byte[]> files, JsonObject manifest)
        => EffectPackageDefinition.Read(EffectPackageContents.Create(EffectPackageFixture.Bytes(manifest), EffectPackageFixture.Inputs(files), EffectPackageFixture.Profile));
    private static byte[] Image()
    {
        using var bitmap = new SKBitmap(2, 2); bitmap.Erase(SKColors.Red);
        using var image = SKImage.FromBitmap(bitmap); using var encoded = image.Encode(SKEncodedImageFormat.Png, 100); return encoded.ToArray();
    }
    private static (Dictionary<string, byte[]> Files, JsonObject Manifest) Nine(string? source = null)
    {
        var files = EffectPackageFixture.Files(); files["assets/image.png"] = Image();
        files["src/PackEffect.cs"] = Encoding.UTF8.GetBytes(source ?? """
            using System;
            using MakoKaraGenerator.Scripting;
            public sealed class PackEffect : EffectBase
            {
                private bool initialized;
                public override void Initialize(EffectInitializationContext context)
                {
                    var p = context.Parameters;
                    if (p.GetInteger("integer") != 9007199254740993L || p.GetDuration("duration") != 120000
                        || !p.GetBoolean("boolean") || p.GetText("text") != "" || p.GetEnum("enum") != "branch"
                        || p.GetColor("color").Red != .123456789 || p.GetCurve("curve").Evaluate(.5) != .875
                        || p.GetAsset("asset") != context.Resources.GetImage("assets/image.png")) throw new Exception("WrongManifestProjection");
                    initialized = true;
                }
                public override void Evaluate(FrameContext frame, FrameWriter output)
                {
                    if (!initialized) throw new Exception("InitializeRequired");
                    foreach (var token in frame.Presentations.TokensInBinding)
                        output.Transforms.ScaleTokenFamily(token.WithRuby, frame.Parameters.GetNumber("peak"), Pivot.BaseAdvanceCenter);
                }
            }
            """);
        return (files, EffectPackageFixture.NineParametersManifest(files));
    }
    [Fact]
    public async Task 工程重开后模板控制器保持字形排版且注音随基础字缩放()
    {
        using TemporaryDirectory temp = new(); var project = LeadInFixture.Create(true); var package = EffectPackageFixture.Contents();
        var template = MakoKaraGenerator.Core.Projects.StyleTemplate.Create("t", "字族", MakoKaraGenerator.Core.Projects.StyleTemplateKind.Token,
            project.LyricStyle, [], [new("r", "放大", MakoKaraGenerator.Core.Projects.TemplateEffectTarget.TokenFamily, package.PackageId, package.Revision, "scale",
                EffectParameterOverrides.Parse("{\"peak\":2}"))], [package]);
        Assert.True(MakoKaraGenerator.Core.Projects.StyleTemplateApplicator.TryPreview(project, template, "first", "first-a", false, out var applied, out var reason), reason);
        string directory = temp.FilePath("scaled.mkgproj"); MakoKaraGenerator.Core.Persistence.ProjectStore.Save(applied.Result, directory);
        var restored = MakoKaraGenerator.Core.Persistence.ProjectStore.Load(directory).Project!;
        var compiled = await CompiledProjectEffects.CompileAsync(restored, Compiler()); Assert.True(compiled.Succeeded);
        var snapshot = ProjectSnapshot.FromProject(restored); var target = RenderTarget.FromComposition(snapshot.Composition);
        await using var source = await compiled.CreateFactory(static () => new SkiaRenderPipeline(), Fx.ReplayReason.Initial).CreateAsync(snapshot);
        var frame = await source.PrepareAsync(12500000, Fx.OutputMode.Transparent, target);
        Assert.Equal(2, Assert.Single(frame.TokenTransforms).LocalMatrix.A);
        var transformed = frame.FinalGeometry.Single(g => g.WithRuby == frame.TokenTransforms[0].Target);
        Assert.Equal(transformed.BaseGeometry.BaseWorldMatrix, Assert.Single(transformed.Ruby).Geometry.BaseWorldMatrix);
        var basis = frame.Scene.TokenGeometries.Single(g => g.WithRuby == transformed.WithRuby);
        Assert.Equal(basis.BaseGeometry.AdvanceBounds, transformed.BaseGeometry.AdvanceBounds);
        using SkiaRenderPipeline renderer = new(); using var actual = renderer.Render(RenderRequest.At(snapshot, frame.TimeUs) with { PreparedFrame = frame, TransparentBackground = true });
        using var unscaled = renderer.Render(RenderRequest.At(ProjectSnapshot.FromProject(project), frame.TimeUs) with { TransparentBackground = true });
        Assert.False(actual.PixelMemory.Span.SequenceEqual(unscaled.PixelMemory.Span));
        Assert.Equal("t", Assert.Single(restored.Effects.Instances).TemplateOrigin!.TemplateId);
    }

    [Theory]
    [InlineData(640, 360)] [InlineData(320, 180)]
    public async Task 真实样例包默认值与覆盖经过准备帧改变像素且注音不重排(int width, int height)
    {
        var compiled = await CompiledEffectPackage.CompileAsync(EffectPackageFixture.WordEmphasis(), Compiler()); Assert.True(compiled.Artifact.Succeeded);
        const string fingerprint = "package-word-default";
        var snapshot = ProjectSnapshot.FromProject(LeadInFixture.Create(true)) with { EffectInputsFingerprint = fingerprint };
        var binding = new Fx.EffectBinding.Tokens([new("first-a")]);
        var defaultEffect = compiled.CreateEffect("word-emphasis", new("word"), binding, 1);
        var overrideEffect = compiled.CreateEffect("word-emphasis", new("word"), binding, 1, "{\"peakScale\":1.8}"u8.ToArray());
        var manual = defaultEffect with { Parameters = new([new("peakScale", new Fx.ParameterValue.Number(1.25)), new("returnUs", new Fx.ParameterValue.Duration(120000))]), PackageDeclaration = null };
        var target = new RenderTarget { PixelWidth = width, PixelHeight = height };
        async Task<PreparedRenderFrame> Prepare(CompiledFrameEffect effect)
        {
            var factory = new PureFrameSourceFactory([effect], fingerprint, static () => new SkiaRenderPipeline());
            await using var source = await factory.CreateAsync(snapshot);
            return await source.PrepareAsync(12_500_000, Fx.OutputMode.Transparent, target);
        }
        var initial = await Prepare(defaultEffect); var expected = await Prepare(manual); var changed = await Prepare(overrideEffect);
        Assert.Equal(1.21875, Assert.Single(initial.TokenTransforms).LocalMatrix.A);
        Assert.Equal(1.7, Assert.Single(changed.TokenTransforms).LocalMatrix.A, 12);
        var transformed = changed.FinalGeometry.Single(geometry => geometry.WithRuby == changed.TokenTransforms[0].Target);
        Assert.Equal(transformed.BaseGeometry.BaseWorldMatrix, Assert.Single(transformed.Ruby).Geometry.BaseWorldMatrix);
        var basis = changed.Scene.TokenGeometries.Single(geometry => geometry.WithRuby == transformed.WithRuby);
        Assert.Equal(basis.BaseGeometry.AdvanceBounds, transformed.BaseGeometry.AdvanceBounds);
        using SkiaRenderPipeline renderer = new();
        RenderRequest Request(PreparedRenderFrame prepared) => RenderRequest.At(snapshot, prepared.TimeUs) with { PreparedFrame = prepared, TransparentBackground = true, Target = target };
        using var first = renderer.Render(Request(initial)); using var known = renderer.Render(Request(expected)); using var updated = renderer.Render(Request(changed));
        Assert.True(first.PixelMemory.Span.SequenceEqual(known.PixelMemory.Span)); Assert.False(first.PixelMemory.Span.SequenceEqual(updated.PixelMemory.Span));
        Assert.Contains(updated.PixelMemory.ToArray().Where((_, index) => index % 4 == 3), alpha => alpha > 0);
    }
    [Fact]
    public async Task 九类清单输入及真实解码图片在Initialize和生产Evaluate中一致()
    {
        var (files, manifest) = Nine(); var package = await CompiledEffectPackage.CompileAsync(Read(files, manifest), Compiler(), new PackageImageValidator());
        Assert.True(package.Artifact.Succeeded, string.Join('\n', package.Artifact.Diagnostics));
        var effect = package.CreateEffect("scale", new("nine"), new Fx.EffectBinding.Tokens([new("first-a")]), 10);
        Assert.Equal(package.Definition.Contents.Revision, effect.Parameters.GetAsset("asset").Identity.Revision);
        const string fingerprint = "nine-package"; var snapshot = ProjectSnapshot.FromProject(LeadInFixture.Create()) with { EffectInputsFingerprint = fingerprint };
        var factory = new PureFrameSourceFactory([effect], fingerprint, static () => new SkiaRenderPipeline());
        await using var source = await factory.CreateAsync(snapshot);
        var prepared = await source.PrepareAsync(12_500_000, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
        Assert.Equal(1.5, Assert.Single(prepared.TokenTransforms).LocalMatrix.A);
        using SkiaRenderPipeline renderer = new();
        using var pixels = renderer.Render(RenderRequest.At(snapshot, prepared.TimeUs) with { PreparedFrame = prepared, TransparentBackground = true });
        using var plain = renderer.Render(RenderRequest.At(snapshot with { EffectInputsFingerprint = null }, prepared.TimeUs) with { TransparentBackground = true });
        Assert.False(pixels.PixelMemory.Span.SequenceEqual(plain.PixelMemory.Span));
    }
    [Theory]
    [InlineData("color", "PackageImageDecodeFailed")] [InlineData("budget", "PackageImageBudgetExceeded")]
    [InlineData("truncated", "PackageImageDecodeFailed")]
    public async Task 图片必须实际完整解码且前置容量拒绝不进入SDK目录(string failure, string code)
    {
        var (files, _) = Nine();
        if (failure == "color") files["assets/image.png"] = "not an image"u8.ToArray();
        if (failure == "truncated") files["assets/image.png"] = files["assets/image.png"][..40];
        var package = Read(files, EffectPackageFixture.NineParametersManifest(files));
        var validator = new PackageImageValidator(failure == "budget" ? new() { MaximumBytes = 15 } : null);
        var error = await Assert.ThrowsAsync<EffectPackageException>(() => CompiledEffectPackage.CompileAsync(package, Compiler(), validator));
        Assert.Equal(code, error.Code); Assert.Equal("assets/image.png", error.Path);
    }
    [Theory]
    [InlineData("VisualTransform", "line")]
    [InlineData("StyleModulation", "textMark")]
    [InlineData("Overlay", "tokenFamily")]
    public async Task 清单粗粒度能力不能扩大到未声明目标或视觉批次(string kind, string targetKind)
    {
        string source = kind switch
        {
            "VisualTransform" => EffectPackageFixture.EffectSource,
            "StyleModulation" => "public class PackEffect : MakoKaraGenerator.Scripting.EffectBase { public override void Evaluate(MakoKaraGenerator.Scripting.FrameContext f, MakoKaraGenerator.Scripting.FrameWriter w) { foreach(var t in f.Presentations.TokensInBinding) w.Style.MultiplyOpacity(t.WithRuby,.5); } }",
            _ => "public class PackEffect : MakoKaraGenerator.Scripting.EffectBase { public override void EvaluateVisuals(MakoKaraGenerator.Scripting.VisualFrameContext f, MakoKaraGenerator.Scripting.VisualWriter w) { w.Overlay.DrawDots(new(\"undeclared\"),[new(new(10,10),2,new(1,0,0,1))],MakoKaraGenerator.Scripting.OverlayLayer.AboveText); } }",
        };
        var files = EffectPackageFixture.Files(); files["src/PackEffect.cs"] = Encoding.UTF8.GetBytes(source);
        var manifest = EffectPackageFixture.Manifest(files); manifest["exports"]![0]!["outputs"]![0]!["kind"] = kind; manifest["exports"]![0]!["outputs"]![0]!["target"] = targetKind;
        var compiled = await CompiledEffectPackage.CompileAsync(Read(files, manifest), Compiler()); Assert.True(compiled.Artifact.Succeeded);
        var effect = compiled.CreateEffect("scale", new("restricted"), new Fx.EffectBinding.Tokens([new("first-a")]), 1);
        const string fingerprint = "restricted-package"; var snapshot = ProjectSnapshot.FromProject(LeadInFixture.Create()) with { EffectInputsFingerprint = fingerprint };
        var factory = new PureFrameSourceFactory([effect], fingerprint, static () => new SkiaRenderPipeline());
        await using var frameSource = await factory.CreateAsync(snapshot);
        var error = await Assert.ThrowsAsync<EffectFailureException>(async () => await frameSource.PrepareAsync(12_500_000, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition)));
        Assert.Equal("UndeclaredPackageOutput", error.Code); Assert.Equal(effect.Id, error.InstanceId);
    }
}
