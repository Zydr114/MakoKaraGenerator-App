using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Execution;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class SourceMaterialRuntimeTests
{
    private const string WriterSource = """
        using MakoKaraGenerator.Scripting;
        public sealed class PackEffect : EffectBase
        {
            public override void Evaluate(FrameContext frame, FrameWriter output)
            {
                var material=MaterialValue.PackageExport(frame.Resources.GetMaterial("ink"),new([new("amount",new ParameterValue.Number(.5))]));
                foreach(var token in frame.Presentations.TokensInBinding)
                {
                    output.Style.SetUnsungMaterial(token.WithRuby,material); output.Style.SetSungMaterial(token.WithRuby,material);
                    output.Style.SetStroke(token.WithRuby,new(4,material,StrokeJoin.Bevel));
                }
                foreach(var mark in frame.Presentations.MarksInBinding)
                {
                    output.Style.SetUnsungMaterial(mark.Target,material); output.Style.SetSungMaterial(mark.Target,material);
                    output.Style.SetStroke(mark.Target,new(4,material,StrokeJoin.Bevel));
                }
            }
        }
        """;
    internal static Project SourceProject(EffectPackageContents contents)
    {
        var project = LeadInFixture.Create(slots: true, crossClip: true);
        var material = new MaterialSpec.PackageMaterial(contents.PackageId, contents.Revision, "ink", EffectParameterOverrides.Parse("{\"amount\":0.5}"));
        var style = project.LyricStyle with { UnsungFill = new() { Material = material }, SungFill = new() { Material = material },
            Stroke = new() { Material = material, WidthPx = 4, Join = StrokeJoin.Bevel }, Shadow = null, Opacity = .65f };
        return project with { Effects = new([contents], []), LyricStyle = style,
            LyricPresentation = project.LyricPresentation with { Nodes = [.. project.LyricPresentation.Nodes.Select(node =>
                node.Content is CountdownSequenceContent sequence ? node with { Content = sequence with { Appearance = SceneTextAppearance.FromStyle(style) } } : node)] } };
    }
    internal static Task<CompiledProjectEffects> Compile(Project project) => CompiledProjectEffects.CompileAsync(project,
        new(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0")), new PackageImageValidator(), new PackageShaderValidator(), new PackageImageGraphValidator(), new PackageMaterialValidator());
    internal static SceneImageEffectResources Resources(CompiledPackageGraphExports native, CompiledEffectPackage package)
        => new([], null, [native.CreateMaterialProgram(package.Resources.GetMaterial("ink"), new(0, 0, 640, 360))]);

    [Theory]
    [InlineData(10_000_000L, true, 1)] [InlineData(13_000_000L, true, 2)]
    [InlineData(10_000_000L, false, 2)] [InlineData(40_500_000L, false, 1)]
    public async Task 纯源材质无DLL实例且与真实作者写入的字形注音描边前奏像素一致(long time, bool transparent, int density)
    {
        using TemporaryDirectory temp = new();
        var source = SourceProject(MixedEffectPackageFixture.Contents(pure: true));
        ProjectStore.Save(source, temp.Path); source = ProjectStore.Load(temp.Path).Project!;
        var compiled = await Compile(source); Assert.True(compiled.Succeeded); Assert.Empty(compiled.Effects);
        var package = Assert.Single(compiled.Packages); Assert.Null(package.ControllerArtifact); Assert.NotNull(compiled.SourceMaterials);
        var snapshot = ProjectSnapshot.FromProject(source); var output = new RenderTarget { PixelWidth = 640 * density, PixelHeight = 360 * density };
        await using var session = await compiled.CreateFactory(static () => new SkiaRenderPipeline(), Fx.ReplayReason.Export).CreateAsync(snapshot);
        var mode = transparent ? Fx.OutputMode.Transparent : Fx.OutputMode.Opaque;
        var prepared = await session.PrepareAsync(time, mode, output);
        Assert.NotEmpty(prepared.Materials);
        Assert.All(prepared.Materials, binding => { Assert.IsType<PreparedMaterialOwner.Source>(binding.Owner); Assert.Null(binding.InstanceId); Assert.Null(binding.ArtifactHash); });
        var baseline = StaticStyleProjection.CreateBaseline(snapshot, compiled.SourceMaterials);
        Assert.IsType<Fx.MaterialValue.PackageMaterial>(baseline.Get(prepared.FinalGeometry[0].WithRuby).UnsungMaterial);
        using var native = CompiledPackageGraphExports.Compile(package.Definition);
        using var resources = Resources(native, package); using var budget = new ImageSurfaceBudgetSession();
        using var renderer = new SkiaRenderPipeline(null, null, budget, resources);
        using var actual = renderer.Render(RenderRequest.At(snapshot, time) with { PreparedFrame = prepared, TransparentBackground = transparent, Target = output });
        Assert.Equal(0, budget.Usage.ActiveSurfaces); Assert.Equal(0, budget.Usage.EstimatedBytes);

        var writerContents = MixedEffectPackageFixture.MaterialContents(manifest => manifest["exports"]![0]!["outputs"]!.AsArray().Add(new JsonObject
            { ["id"] = "marks", ["kind"] = "StyleModulation", ["target"] = "textMark", ["requires"] = new JsonArray(), ["dependsOn"] = new JsonArray() }), controllerSource: WriterSource);
        var plain = SourceProject(writerContents);
        var plainStyle = plain.LyricStyle with { UnsungFill = new() { Color = RgbaColor.White }, SungFill = new() { Color = RgbaColor.White },
            Stroke = new() { Color = RgbaColor.White, WidthPx = 4, Join = StrokeJoin.Bevel } };
        plain = plain with { LyricStyle = plainStyle, Effects = new([writerContents], [new() { Id = "real-writer", Name = "作者", PackageId = writerContents.PackageId,
            Revision = writerContents.Revision, ExportId = "controller" }]),
            LyricPresentation = plain.LyricPresentation with { Nodes = [.. plain.LyricPresentation.Nodes.Select(node => node.Content is CountdownSequenceContent sequence
                ? node with { Content = sequence with { Appearance = SceneTextAppearance.FromStyle(plainStyle) } } : node)] } };
        var written = await Compile(plain); var writtenPackage = Assert.Single(written.Packages); var writtenSnapshot = ProjectSnapshot.FromProject(plain);
        await using var writtenSession = await written.CreateFactory(static () => new SkiaRenderPipeline(), Fx.ReplayReason.Export).CreateAsync(writtenSnapshot);
        var writtenFrame = await writtenSession.PrepareAsync(time, mode, output);
        using var writtenNative = CompiledPackageGraphExports.Compile(writtenPackage.Definition); using var writtenResources = Resources(writtenNative, writtenPackage);
        using var writtenRenderer = new SkiaRenderPipeline(null, null, null, writtenResources);
        using var expected = writtenRenderer.Render(RenderRequest.At(writtenSnapshot, time) with { PreparedFrame = writtenFrame, TransparentBackground = transparent, Target = output });
        Assert.Equal(expected.PixelMemory.ToArray(), actual.PixelMemory.ToArray());
        var replayed = await session.PrepareAsync(time - 100_000, mode, output);
        var repeated = await session.PrepareAsync(time, mode, output);
        using var repeat = renderer.Render(RenderRequest.At(snapshot, time) with { PreparedFrame = repeated, TransparentBackground = transparent, Target = output });
        Assert.Equal(actual.PixelMemory.ToArray(), repeat.PixelMemory.ToArray());
        Assert.Equal(prepared.Materials.Length, repeated.Materials.Length);
        foreach (var binding in prepared.Materials)
        {
            var again = Assert.Single(repeated.Materials.Where(other => other.Target == binding.Target && other.Slot == binding.Slot));
            Assert.Equal(binding.Owner, again.Owner); Assert.Equal(binding.Material, again.Material);
        }
    }

    [Fact]
    public async Task 材质填充基础外观共享raw区域栅格且独立设备执行不返回空图()
    {
        var contents = MixedEffectPackageFixture.Contents(pure: true);
        var definition = EffectPackageDefinition.Read(contents);
        var project = SourceProject(contents);
        var compiled = await Compile(project);
        Assert.True(compiled.Succeeded);
        var snapshot = ProjectSnapshot.FromProject(project);
        await using var session = await compiled.CreateFactory(static () => new SkiaRenderPipeline(), Fx.ReplayReason.Export).CreateAsync(snapshot);
        var prepared = await session.PrepareAsync(40_500_000, Fx.OutputMode.Opaque, new() { PixelWidth = 640, PixelHeight = 360 });
        using var renderer = new SkiaRenderPipeline();
        using var native = CompiledPackageGraphExports.Compile(definition);
        var scene = prepared.HierarchyScene;
        var request = RenderRequest.At(snapshot, scene.TimeUs) with { PreparedFrame = prepared };
        var context = new ImageGraphFrameContext(scene.TimeUs, 640, 360, new(0, 0, 640, 360), 1, 1,
            new([new("amount", new Fx.ParameterValue.Number(.5))]), false);
        int checkedPixels = 0;
        foreach (var geometry in scene.TokenGeometries)
        {
            using var capture = renderer.CaptureSceneMaterialRegion(request, new SceneCaptureTarget.Token(geometry.WithRuby),
                SceneColorPart.BaseFill, definition.GetMaterial("ink").Fallback, false, new(0, 0, 640, 360), GraphExecutionBudget.Default);
            byte[] region = SkiaTestReadback.Pixels(capture.Region.Image), fallback = SkiaTestReadback.Pixels(capture.TargetColor.Image);
            for (int i = 3; i < region.Length; i += 4)
            {
                Assert.InRange(fallback[i], 0, region[i] == 0 ? 0 : region[i] + 1);
                if (fallback[i] > 0) checkedPixels++;
            }
            using var result = native.ExecuteMaterial(native.Resources.GetMaterial("ink"), SceneCaptureRegion.Fill, context,
                capture.Region, capture.TargetColor, new Dictionary<string, SkiaGraphInput>());
            byte[] actual = SkiaTestReadback.Pixels(result.Output.Image);
            for (int i = 3; i < fallback.Length; i += 4) Assert.Equal(fallback[i], actual[i]);
        }
        Assert.True(checkedPixels > 0);
    }

    [Fact]
    public async Task 源继承路径独立于描边几何覆盖且缺少native资源错误保留源身份()
    {
        var project = SourceProject(MixedEffectPackageFixture.Contents(pure: true));
        var primary = project.Lyrics.PrimaryBranch!; var line = primary.Lines[0]; var token = line.Tokens[0];
        var material = project.LyricStyle.UnsungFill.Material;
        token = token with { StyleOverride = new() { SungFill = OptionalValue.Set<FillSpecOverride?>(new() { Material = OptionalValue.Set<MaterialSpec?>(material) }), StrokeWidthPx = 8 } };
        line = line with { Tokens = line.Tokens.Replace(line.Tokens[0], token), StyleOverride = new() { UnsungFill = OptionalValue.Set<FillSpecOverride?>(new() { Material = OptionalValue.Set<MaterialSpec?>(material) }) } };
        primary = primary with { Lines = primary.Lines.Replace(primary.Lines[0], line), StyleOverride = new() { Stroke = OptionalValue.Set<StrokeSpecOverride?>(new() { Material = OptionalValue.Set<MaterialSpec?>(material) }) } };
        project = project with { Lyrics = project.Lyrics with { Branches = project.Lyrics.Branches.Replace(project.Lyrics.PrimaryBranch!, primary) } };
        var compiled = await Compile(project); var snapshot = ProjectSnapshot.FromProject(project);
        await using var session = await compiled.CreateFactory(static () => new SkiaRenderPipeline(), Fx.ReplayReason.Initial).CreateAsync(snapshot);
        var prepared = await session.PrepareAsync(12_500_000, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
        var bindings = prepared.Materials.Where(binding => binding.Target is Fx.ImageEffectTarget.Token t && t.Target.TokenId.Value == token.Id).ToArray();
        Assert.Equal(3, bindings.Length);
        Assert.EndsWith(".lines.first.styleOverride.unsungFill", ((PreparedMaterialOwner.Source)bindings.Single(binding => binding.Slot == PreparedMaterialSlot.UnsungFill).Owner).Reference.Path);
        Assert.EndsWith(".tokens.first-a.styleOverride.sungFill", ((PreparedMaterialOwner.Source)bindings.Single(binding => binding.Slot == PreparedMaterialSlot.SungFill).Owner).Reference.Path);
        Assert.EndsWith(".styleOverride.stroke.material", ((PreparedMaterialOwner.Source)bindings.Single(binding => binding.Slot == PreparedMaterialSlot.Stroke).Owner).Reference.Path);
        Assert.Equal(8, prepared.GetTokenDrawingStyle(((Fx.ImageEffectTarget.Token)bindings[0].Target).Target).Stroke!.WidthPx);
        using var missing = new SkiaRenderPipeline();
        var error = Assert.Throws<SourceMaterialFailureException>(() => { using var unexpected = missing.Render(RenderRequest.At(snapshot, prepared.TimeUs) with { PreparedFrame = prepared, TransparentBackground = true }); });
        Assert.Equal("MaterialResourcesRequired", error.Code); Assert.IsType<PreparedMaterialOwner.Source>(error.Owner); Assert.Contains("源材质", error.Message);
        var changed = project with { LyricStyle = project.LyricStyle with { SungFill = new() { Color = RgbaColor.White } } };
        Assert.Equal("EffectInputsMismatch", (await Assert.ThrowsAsync<PreparedFrameException>(async () => await compiled.CreateFactory(static () => new SkiaRenderPipeline(), Fx.ReplayReason.Initial).CreateAsync(ProjectSnapshot.FromProject(changed)))).Code);
    }

    [Theory]
    [InlineData("uniform")] [InlineData("reset")] [InlineData("alpha")]
    public async Task 真实作者读取包基线并按材质操作顺序接管来源(string operation)
    {
        string action = operation switch
        {
            "uniform" => "output.Materials.SetUniform(frame.Resources.GetMaterial(\"ink\"),token.WithRuby,MaterialSlot.SungFill,\"probe\",\"amount\",UniformValue.Float(.8));",
            "reset" => "output.Style.SetSungMaterial(token.WithRuby,MaterialValue.Solid(new(0,1,0,1)));",
            _ => "output.Style.MultiplyOpacity(token.WithRuby,.5);",
        };
        string source = "using MakoKaraGenerator.Scripting; public sealed class PackEffect:EffectBase { public override void Evaluate(FrameContext frame,FrameWriter output) { foreach(var token in frame.Presentations.TokensInBinding) { if(frame.Baseline.Get(token.WithRuby).SungMaterial is not MaterialValue.PackageMaterial) throw new System.Exception(\"真实包基线缺失\"); " + action + " } } }";
        var contents = MixedEffectPackageFixture.MaterialContents(controllerSource: source); var project = SourceProject(contents);
        project = project with { Effects = new([contents], [new() { Id = "actual", Name = "调制", PackageId = contents.PackageId, Revision = contents.Revision, ExportId = "controller" }]) };
        var compiled = await Compile(project); var snapshot = ProjectSnapshot.FromProject(project); var package = Assert.Single(compiled.Packages);
        await using var session = await compiled.CreateFactory(static () => new SkiaRenderPipeline(), Fx.ReplayReason.Initial).CreateAsync(snapshot);
        var prepared = await session.PrepareAsync(12_500_000, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
        foreach (var token in prepared.FinalGeometry)
        {
            var bindings = prepared.Materials.Where(binding => binding.Target == new Fx.ImageEffectTarget.Token(token.WithRuby)).ToArray();
            if (operation == "uniform") { var binding = bindings.Single(binding => binding.Slot == PreparedMaterialSlot.SungFill); Assert.IsType<PreparedMaterialOwner.Script>(binding.Owner); Assert.Equal(new Fx.EffectInstanceId("actual"), binding.InstanceId); Assert.Single(binding.Material.Uniforms.Values); }
            else if (operation == "reset") Assert.DoesNotContain(bindings, binding => binding.Slot == PreparedMaterialSlot.SungFill);
            else Assert.All(bindings, binding => Assert.IsType<PreparedMaterialOwner.Source>(binding.Owner));
        }
        using var native = CompiledPackageGraphExports.Compile(package.Definition); using var resources = Resources(native, package); using var renderer = new SkiaRenderPipeline(null, null, null, resources);
        using var pixels = renderer.Render(RenderRequest.At(snapshot, prepared.TimeUs) with { PreparedFrame = prepared, TransparentBackground = true });
        Assert.Contains(pixels.PixelMemory.ToArray().Where((_, i) => i % 4 == 3), alpha => alpha > 0);
    }

    [Fact]
    public async Task 源包原生验证与参数错误在会话前保留真实来源()
    {
        var contents = MixedEffectPackageFixture.Contents(pure: true); var project = SourceProject(contents);
        var absent = await Assert.ThrowsAsync<SourceMaterialFailureException>(() => CompiledProjectEffects.CompileAsync(project, new(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"))));
        Assert.Equal("PackageShaderValidatorMissing", absent.Code); Assert.Equal("lyricStyle.unsungFill", absent.Owner.Reference.Path);
        var badMaterial = new MaterialSpec.PackageMaterial(contents.PackageId, contents.Revision, "ink", EffectParameterOverrides.Parse("{\"amount\":2}"));
        var bad = project with { LyricStyle = project.LyricStyle with { UnsungFill = new() { Material = badMaterial } } };
        var error = await Assert.ThrowsAsync<SourceMaterialFailureException>(() => Compile(bad));
        Assert.Equal("PackageParameterOutOfRange", error.Code); Assert.Equal("lyricStyle.unsungFill", error.Owner.Reference.Path);
    }

    [Fact]
    public async Task 源材质纯帧和独立端点共用固定投影且取消不产生伪造结果()
    {
        var project = SourceProject(MixedEffectPackageFixture.Contents(pure: true));
        var compiled = await Compile(project); var snapshot = ProjectSnapshot.FromProject(project);
        var target = RenderTarget.FromComposition(snapshot.Composition);
        await using var pure = await new PureFrameSourceFactory([], snapshot.EffectInputsFingerprint!, static () => new SkiaRenderPipeline(),
            sourceMaterials: compiled.SourceMaterials).CreateAsync(snapshot);
        var frame = await pure.PrepareAsync(12_500_000, Fx.OutputMode.Transparent, target);
        var cursor = frame.Playback.GetCursors(Fx.CursorAnchorMode.AdvanceCenter).First();
        SemanticAnchorQuery query = new(snapshot, [], snapshot.EffectInputsFingerprint!, static () => new SkiaRenderPipeline(), sourceMaterials: compiled.SourceMaterials);
        var endpoint = await query.ResolveAsync(cursor.Identity, Fx.CursorEndpointKind.NextStart, Fx.CursorAnchorMode.AdvanceCenter, Fx.OutputMode.Transparent, target);
        Assert.True(endpoint.IsAvailable); Assert.Equal(cursor.Identity.TimeRange.StartUs, endpoint.TimeUs);
        var reference = await pure.PrepareAsync(endpoint.TimeUs, Fx.OutputMode.Transparent, target);
        Assert.Equal(reference.Playback.GetCursors(Fx.CursorAnchorMode.AdvanceCenter).Single(item => item.Identity == cursor.Identity), endpoint.Cursor);
        Assert.All(reference.Materials, binding => Assert.IsType<PreparedMaterialOwner.Source>(binding.Owner));
        using CancellationTokenSource canceled = new(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await query.ResolveAsync(cursor.Identity, Fx.CursorEndpointKind.NextStart,
            Fx.CursorAnchorMode.AdvanceCenter, Fx.OutputMode.Transparent, target, canceled.Token));
        Assert.Contains("PackageMaterial", project.LyricStyle.UnsungFill.Material.ToString());
    }

    [Fact]
    public async Task 前奏源覆盖保留真实JSON路径且透明缓存与预算失败不丢失来源()
    {
        var project = SourceProject(MixedEffectPackageFixture.Contents(pure: true, background: true));
        var node = project.LyricPresentation.Nodes.First(node => node.Content is TextMarkContent);
        var mark = (TextMarkContent)node.Content!;
        node = node with { Content = mark with { AppearanceOverride = new() {
            PendingFill = OptionalValue.Set<FillSpecOverride?>(new() { Material = OptionalValue.Set<MaterialSpec?>(project.LyricStyle.UnsungFill.Material) }) } } };
        project = project with { LyricPresentation = project.LyricPresentation with { Nodes = project.LyricPresentation.Nodes.Replace(project.LyricPresentation.FindNode(node.Id)!, node) } };
        var compiled = await Compile(project); var snapshot = ProjectSnapshot.FromProject(project); var package = Assert.Single(compiled.Packages);
        await using var session = await compiled.CreateFactory(static () => new SkiaRenderPipeline(), Fx.ReplayReason.Initial).CreateAsync(snapshot);
        var prepared = await session.PrepareAsync(10_000_000, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
        var bindings = prepared.Materials.Where(binding => binding.Target is Fx.ImageEffectTarget.Mark target && target.Target.NodeId.Value == node.Id).ToArray();
        Assert.Equal(3, bindings.Length);
        Assert.Equal("lyricPresentation.nodes." + node.Id + ".content.appearanceOverride.pendingFill",
            ((PreparedMaterialOwner.Source)bindings.Single(binding => binding.Slot == PreparedMaterialSlot.UnsungFill).Owner).Reference.Path);
        Assert.EndsWith(".content.appearance.elapsedFill", ((PreparedMaterialOwner.Source)bindings.Single(binding => binding.Slot == PreparedMaterialSlot.SungFill).Owner).Reference.Path);
        using var native = CompiledPackageGraphExports.Compile(package.Definition); using var resources = Resources(native, package);
        using var renderer = new SkiaRenderPipeline(null, null, null, resources); var cached = new CachedRenderPipeline(renderer);
        var request = RenderRequest.At(snapshot, prepared.TimeUs) with { PreparedFrame = prepared, TransparentBackground = true };
        using var first = cached.Render(request); using var second = cached.Render(request);
        Assert.Equal(first.PixelMemory.ToArray(), second.PixelMemory.ToArray());
        Assert.NotEmpty(second.MaterialDiagnostics); Assert.Equal(first.MaterialDiagnostics.ToArray(), second.MaterialDiagnostics.ToArray());
        Assert.All(second.MaterialDiagnostics, warning => { Assert.IsType<PreparedMaterialOwner.Source>(warning.Owner); Assert.Null(warning.InstanceId); Assert.Null(warning.ArtifactHash); });
        Assert.Contains(second.MaterialDiagnostics, warning => warning.Owner == bindings.Single(binding => binding.Slot == PreparedMaterialSlot.UnsungFill).Owner);
        using var budget = new ImageSurfaceBudgetSession(new() { MaximumSurfaces = 2 });
        using var limited = new SkiaRenderPipeline(null, null, budget, resources);
        var failure = Assert.Throws<SourceMaterialFailureException>(() => { using var unexpected = limited.Render(request); });
        Assert.Equal("ImageSurfaceBudgetExceeded", failure.Code); Assert.Equal(prepared.TimeUs, failure.TimeUs);
        Assert.Contains(prepared.Materials, binding => binding.Owner == failure.Owner);
        Assert.Equal(0, budget.Usage.ActiveSurfaces); Assert.Equal(0, budget.Usage.EstimatedBytes);
    }
}
