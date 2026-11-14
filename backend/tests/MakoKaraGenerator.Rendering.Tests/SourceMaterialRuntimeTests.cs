using System.Collections.Immutable;
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
        using MakoKaraGenerator.Scripting.V2;
        public sealed class PackEffect : IPureKaraokeEffect
        {
            public PreparedEffect Declare(DeclareContext source) => PreparedEffect.Empty;
            public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output)
            {
                var material = MaterialValue.PackageExport(frame.Resources.GetMaterial("ink"),
                    new ParameterValues(new[] { new System.Collections.Generic.KeyValuePair<string, ParameterValue>("amount", new ParameterValue.Number(.5)) }));
                foreach (var token in frame.Presentations.TokensInBinding)
                {
                    var target = new TextTarget.Family(token.WithRuby);
                    output.SetFill(target, TextState.Unsung, material); output.SetFill(target, TextState.Sung, material);
                    output.SetStrokes(target, TextState.Both, new StrokeValue(4, material, StrokeJoin.Bevel));
                }
                foreach (var mark in frame.Presentations.MarksInBinding)
                {
                    var target = new TextTarget.Mark(mark.Target);
                    output.SetFill(target, TextState.Unsung, material); output.SetFill(target, TextState.Sung, material);
                    output.SetStrokes(target, TextState.Both, new StrokeValue(4, material, StrokeJoin.Bevel));
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
        new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0")), new PackageImageValidator(), new PackageShaderValidator(), new PackageImageGraphValidator(), new PackageMaterialValidator());
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
        Assert.IsType<Fx.MaterialValue.PackageMaterial>(baseline.Get(prepared.FinalGeometry[0].WithRuby).Unsung.Fill);
        using var native = CompiledPackageGraphExports.Compile(package.Definition);
        using var resources = Resources(native, package); using var budget = new ImageSurfaceBudgetSession();
        using var renderer = new SkiaRenderPipeline(null, null, budget, resources);
        using var actual = renderer.Render(RenderRequest.At(snapshot, time) with { PreparedFrame = prepared, TransparentBackground = transparent, Target = output });
        Assert.Equal(0, budget.Usage.ActiveSurfaces); Assert.Equal(0, budget.Usage.EstimatedBytes);

        var writerContents = V2RenderPackageHelper.UpgradeToV2(
            MixedEffectPackageFixture.MaterialContents(manifest =>
            {
                var outputs = manifest["exports"]![0]!["outputs"]!.AsArray();
                outputs[0]!["requires"] = new JsonArray(Fx.V2.CapabilityNames.StyleFill, Fx.V2.CapabilityNames.StyleStroke);
                outputs.Add(new JsonObject
                {
                    ["id"] = "marks", ["kind"] = "StyleModulation", ["target"] = "textMark",
                    ["requires"] = new JsonArray(Fx.V2.CapabilityNames.StyleFill, Fx.V2.CapabilityNames.StyleStroke),
                    ["dependsOn"] = new JsonArray(),
                });
            }, controllerSource: WriterSource),
            [Fx.V2.CapabilityNames.StyleFill, Fx.V2.CapabilityNames.StyleStroke]);
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
        token = token with { StyleOverride = new() { SungFill = OptionalValue.Set<FillSpecOverride?>(new() { Material = OptionalValue.Set<MaterialSpec?>(material) }), Stroke = new StrokeSpec { Material = material, WidthPx = 8, Join = StrokeJoin.Bevel } } };
        line = line with { Tokens = line.Tokens.Replace(line.Tokens[0], token), StyleOverride = new() { UnsungFill = OptionalValue.Set<FillSpecOverride?>(new() { Material = OptionalValue.Set<MaterialSpec?>(material) }) } };
        primary = primary with { Lines = primary.Lines.Replace(primary.Lines[0], line), StyleOverride = new() { Stroke = new StrokeSpec { Material = material, WidthPx = 4, Join = StrokeJoin.Bevel } } };
        project = project with { Lyrics = project.Lyrics with { Branches = project.Lyrics.Branches.Replace(project.Lyrics.PrimaryBranch!, primary) } };
        var compiled = await Compile(project); var snapshot = ProjectSnapshot.FromProject(project);
        await using var session = await compiled.CreateFactory(static () => new SkiaRenderPipeline(), Fx.ReplayReason.Initial).CreateAsync(snapshot);
        var prepared = await session.PrepareAsync(12_500_000, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
        var bindings = prepared.Materials.Where(binding => binding.Target is Fx.ImageEffectTarget.Token t && t.Target.TokenId.Value == token.Id).ToArray();
        // 已唱描边跟随未唱：已唱描边槽另有一个绑定，引用未唱一侧的同一源路径。
        Assert.Equal(4, bindings.Length);
        Assert.Equal(bindings.Single(binding => binding.Slot == PreparedMaterialSlot.UnsungStroke).Material, bindings.Single(binding => binding.Slot == PreparedMaterialSlot.SungStroke).Material);
        Assert.Equal(((PreparedMaterialOwner.Source)bindings.Single(binding => binding.Slot == PreparedMaterialSlot.UnsungStroke).Owner).Reference,
            ((PreparedMaterialOwner.Source)bindings.Single(binding => binding.Slot == PreparedMaterialSlot.SungStroke).Owner).Reference);
        Assert.EndsWith(".lines.first.styleOverride.unsung.fill", ((PreparedMaterialOwner.Source)bindings.Single(binding => binding.Slot == PreparedMaterialSlot.UnsungFill).Owner).Reference.Path);
        Assert.EndsWith(".tokens.first-a.styleOverride.sung.fill", ((PreparedMaterialOwner.Source)bindings.Single(binding => binding.Slot == PreparedMaterialSlot.SungFill).Owner).Reference.Path);
        Assert.EndsWith(".styleOverride.unsung.strokes.0.material", ((PreparedMaterialOwner.Source)bindings.Single(binding => binding.Slot == PreparedMaterialSlot.UnsungStroke).Owner).Reference.Path);
        Assert.Equal(8, prepared.GetTokenDrawingStyle(((Fx.ImageEffectTarget.Token)bindings[0].Target).Target).Stroke!.WidthPx);
        using var missing = new SkiaRenderPipeline();
        var error = Assert.Throws<SourceMaterialFailureException>(() => { using var unexpected = missing.Render(RenderRequest.At(snapshot, prepared.TimeUs) with { PreparedFrame = prepared, TransparentBackground = true }); });
        Assert.Equal("MaterialResourcesRequired", error.Code); Assert.IsType<PreparedMaterialOwner.Source>(error.Owner); Assert.Contains("源材质", error.Message);
        var changed = project with { LyricStyle = project.LyricStyle with { SungFill = new() { Color = RgbaColor.White } } };
        Assert.Equal("EffectInputsMismatch", (await Assert.ThrowsAsync<PreparedFrameException>(async () => await compiled.CreateFactory(static () => new SkiaRenderPipeline(), Fx.ReplayReason.Initial).CreateAsync(ProjectSnapshot.FromProject(changed)))).Code);
    }

    /// <summary>描边材质槽分两态（P3 第 6 步）：已唱显式描边沿用同一包材质时两槽各自绑定；换成普通材质时只有未唱一槽。</summary>
    [Fact]
    public async Task 已唱描边材质按自己的槽绑定()
    {
        var project = SourceProject(MixedEffectPackageFixture.Contents(pure: true));
        var stroke = project.LyricStyle.Stroke!;
        Project Sung(StrokeSpec sung) => project with { LyricStyle = project.LyricStyle with
            { Sung = project.LyricStyle.Sung with { Strokes = Followable.Set(StrokeLayers.FromStroke(sung)) } } };
        var same = await Prepare(Sung(stroke with { WidthPx = 8 }));
        Assert.Contains(same, binding => binding.Slot == PreparedMaterialSlot.SungStroke
            && ((PreparedMaterialOwner.Source)binding.Owner).Reference.Path.EndsWith(".sung.strokes.0.material", StringComparison.Ordinal));
        var solid = await Prepare(Sung(StrokeSpec.Create(new(0, 0, 255), 4)));
        Assert.DoesNotContain(solid, binding => binding.Slot == PreparedMaterialSlot.SungStroke);
        Assert.Contains(solid, binding => binding.Slot == PreparedMaterialSlot.UnsungStroke);

        static async Task<ImmutableArray<PreparedMaterialBinding>> Prepare(Project project)
        {
            var compiled = await Compile(project); var snapshot = ProjectSnapshot.FromProject(project);
            await using var session = await compiled.CreateFactory(static () => new SkiaRenderPipeline(), Fx.ReplayReason.Initial).CreateAsync(snapshot);
            return (await session.PrepareAsync(12_500_000, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition))).Materials;
        }
    }

    [Theory]
    [InlineData("uniform")] [InlineData("reset")] [InlineData("alpha")]
    public async Task 真实作者读取包基线并按材质操作顺序接管来源(string operation)
    {
        string action = operation switch
        {
            "uniform" => "output.SetMaterialUniform(new TextTarget.Family(token.WithRuby), MaterialSlot.SungFill, frame.Resources.GetMaterial(\"ink\"), \"probe\", \"amount\", UniformValue.Float(.8));",
            "reset" => "output.SetFill(new TextTarget.Family(token.WithRuby), TextState.Sung, MaterialValue.Solid(new(0,1,0,1)));",
            _ => "output.MultiplyOpacity(new TextTarget.Family(token.WithRuby), .5);",
        };
        string capability = operation switch
        {
            "uniform" => Fx.V2.CapabilityNames.MaterialUniform,
            "reset" => Fx.V2.CapabilityNames.StyleFill,
            _ => Fx.V2.CapabilityNames.TokenOpacity,
        };
        string source = "using MakoKaraGenerator.Scripting; using MakoKaraGenerator.Scripting.V2; public sealed class PackEffect : IPureKaraokeEffect { public PreparedEffect Declare(DeclareContext s) => PreparedEffect.Empty; public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output) { foreach(var token in frame.Presentations.TokensInBinding) { if(frame.Baseline.Get(token.WithRuby).Sung.Fill is not MaterialValue.PackageMaterial) throw new System.InvalidOperationException(\"真实包基线缺失\"); " + action + " } } }";
        var contents = V2RenderPackageHelper.UpgradeToV2(
            MixedEffectPackageFixture.MaterialContents(
                manifest => manifest["exports"]![0]!["outputs"]![0]!["requires"] = new JsonArray(capability),
                controllerSource: source),
            [capability]); var project = SourceProject(contents);
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
        var absent = await Assert.ThrowsAsync<SourceMaterialFailureException>(() => CompiledProjectEffects.CompileAsync(project, new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"))));
        Assert.Equal("PackageShaderValidatorMissing", absent.Code); Assert.Equal("lyricStyle.unsung.fill", absent.Owner.Reference.Path);
        var badMaterial = new MaterialSpec.PackageMaterial(contents.PackageId, contents.Revision, "ink", EffectParameterOverrides.Parse("{\"amount\":2}"));
        var bad = project with { LyricStyle = project.LyricStyle with { UnsungFill = new() { Material = badMaterial } } };
        var error = await Assert.ThrowsAsync<SourceMaterialFailureException>(() => Compile(bad));
        Assert.Equal("PackageParameterOutOfRange", error.Code); Assert.Equal("lyricStyle.unsung.fill", error.Owner.Reference.Path);
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
        Assert.Equal(4, bindings.Length);
        Assert.Contains(bindings, binding => binding.Slot == PreparedMaterialSlot.SungStroke);
        Assert.Equal("lyricPresentation.nodes." + node.Id + ".content.appearanceOverride.pending.fill",
            ((PreparedMaterialOwner.Source)bindings.Single(binding => binding.Slot == PreparedMaterialSlot.UnsungFill).Owner).Reference.Path);
        Assert.EndsWith(".content.appearance.elapsed.fill", ((PreparedMaterialOwner.Source)bindings.Single(binding => binding.Slot == PreparedMaterialSlot.SungFill).Owner).Reference.Path);
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
