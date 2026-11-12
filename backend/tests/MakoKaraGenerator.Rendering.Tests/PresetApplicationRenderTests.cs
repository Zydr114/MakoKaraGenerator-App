using System.Text.Json;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting.Compilation;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class PresetApplicationRenderTests
{
    private static Project Apply(Project project, StyleTemplate preset, ProjectEffectBinding target)
    {
        Assert.True(PresetApplicationsEditor.TryApply(project, preset, [target], null, out var result, out var reason), reason);
        return result;
    }

    private static StyleTemplate Static(string id, LyricStyle style, params LyricStyleProperty[] properties)
        => StyleTemplate.Create(id, id, StyleTemplateKind.Composite, style, properties);

    private static Project WithLine(Project project, string id, Func<LyricLine, LyricLine> update)
        => project with { Lyrics = project.Lyrics with { Branches = [.. project.Lyrics.Branches.Select(branch => branch with
            { Lines = [.. branch.Lines.Select(line => line.Id == id ? update(line) : line)] })] } };

    private static void SamePixels(SkiaRenderPipeline actualRenderer, ProjectSnapshot actual, SkiaRenderPipeline expectedRenderer,
        ProjectSnapshot expected, long time, bool transparent, PreparedRenderFrame? actualFrame = null, PreparedRenderFrame? expectedFrame = null)
    {
        using var a = actualRenderer.Render(RenderRequest.At(actual, time) with { TransparentBackground = transparent, PreparedFrame = actualFrame });
        using var b = expectedRenderer.Render(RenderRequest.At(expected, time) with { TransparentBackground = transparent, PreparedFrame = expectedFrame });
        Assert.Equal(b.PixelMemory.ToArray(), a.PixelMemory.ToArray());
    }

    [Theory]
    [InlineData(10_000_000L, false)] [InlineData(13_000_000L, false)] [InlineData(13_000_000L, true)]
    public void 工程与文字组合预设保持基础字注音标记及透明像素(long time, bool transparent)
    {
        var source = LeadInFixture.Create(slots: true);
        var color = new RgbaColor(30, 120, 240, 180);
        var global = source.LyricStyle with { FontSizePx = 44, UnsungColor = color };
        var local = source.LyricStyle with { UnsungColor = new(220, 60, 40, 160), Opacity = .5f };
        var actual = Apply(source, Static("global", global, LyricStyleProperty.FontSizePx, LyricStyleProperty.UnsungFill), new(ProjectEffectBindingKind.Project));
        actual = Apply(actual, Static("local", local, LyricStyleProperty.UnsungFill, LyricStyleProperty.Opacity),
            new(ProjectEffectBindingKind.Tokens, ids: ["first-a"]));
        var expected = source with { LyricStyle = global, LyricPresentation = source.LyricPresentation with
        { Nodes = [.. source.LyricPresentation.Nodes.Select(node => node.Content is CountdownSequenceContent sequence ? node with
            { Content = sequence with { Appearance = sequence.Appearance with { Typography = sequence.Appearance.Typography with { FontSizePx = 44 }, PendingFill = new() { Color = color } } } } : node)] } };
        expected = WithLine(expected, "first", line => line with { Tokens = line.Tokens.SetItem(0, line.Tokens[0] with
            { StyleOverride = new() { UnsungFill = OptionalValue.Set<FillSpecOverride?>(new() { Material = local.UnsungFill.Material }), Opacity = .5f } }) });
        using var a = new SkiaRenderPipeline(); using var b = new SkiaRenderPipeline();
        var snapshot = ProjectSnapshot.FromProject(actual);
        Assert.Same(source.Lyrics, actual.Lyrics);
        SamePixels(a, snapshot, b, ProjectSnapshot.FromProject(expected), time, transparent);
        var frame = a.GetSceneFrame(snapshot, time);
        var first = frame.Lines.Single(line => line.Frame.Line!.Id == "first");
        Assert.Equal(44, LyricStyleResolver.Resolve(snapshot.LyricStyle, lineOverride: first.Frame.Line!.StyleOverride).Style.FontSizePx);
        Assert.Equal(JsonSerializer.Serialize(b.GetSceneFrame(ProjectSnapshot.FromProject(expected), time).TokenGeometries),
            JsonSerializer.Serialize(frame.TokenGeometries));
    }

    [Theory]
    [InlineData(13_000_000L)] [InlineData(18_000_000L)] [InlineData(40_500_000L)]
    public void 动态槽位与片段预设按实际装载解析且几何像素与明确源覆盖一致(long time)
    {
        var source = LeadInFixture.Create(slots: true, crossClip: true);
        var program = source.LyricPresentation.FindProgram("primary")!;
        var slot = source.LyricPresentation.FindNode(program.DefaultContainerId)!.Layout!.SlotIds[0];
        var slotStyle = source.LyricStyle with { FontSizePx = 52, SungColor = new(30, 240, 90, 255) };
        var clipStyle = source.LyricStyle with { FontSizePx = 64, UnsungColor = new(200, 70, 180, 255) };
        var actual = Apply(source, Static("slot", slotStyle, LyricStyleProperty.FontSizePx, LyricStyleProperty.SungFill),
            new(ProjectEffectBindingKind.Node, id: slot, includeDescendants: true));
        actual = Apply(actual, Static("clip", clipStyle, LyricStyleProperty.FontSizePx, LyricStyleProperty.UnsungFill),
            new(ProjectEffectBindingKind.Clips, ids: ["chorus-clip"]));
        using var a = new SkiaRenderPipeline(); using var b = new SkiaRenderPipeline();
        var plain = b.GetSceneFrame(ProjectSnapshot.FromProject(source), time);
        var expected = source;
        foreach (var sample in plain.Lines)
        {
            LyricStyleOverride patch = LyricStyleOverride.Empty;
            if (sample.ParentId == slot) patch = new() { FontSizePx = 52, SungColor = slotStyle.SungColor };
            if (sample.DisplayClipId == "chorus-clip") patch = patch with { FontSizePx = 64, UnsungColor = clipStyle.UnsungColor };
            if (!patch.IsEmpty) expected = WithLine(expected, sample.Frame.Line!.Id, line => line with { StyleOverride = patch });
        }
        var snapshot = ProjectSnapshot.FromProject(actual);
        SamePixels(a, snapshot, b, ProjectSnapshot.FromProject(expected), time, false);
        var scene = a.GetSceneFrame(snapshot, time);
        var baseline = StaticStyleProjection.LineStyles(snapshot, scene);
        foreach (var sample in scene.Lines)
        {
            var style = PresetSceneStyleProjection.Lyrics(snapshot, sample.Frame.Line!.Id, frame: scene).Style;
            Assert.Equal(style, baseline[new(sample.Frame.Line.Id)]);
            Assert.Equal(sample.DisplayClipId == "chorus-clip" ? 64 : sample.ParentId == slot ? 52 : 32, style.FontSizePx);
        }
        Assert.Equal(JsonSerializer.Serialize(b.GetSceneFrame(ProjectSnapshot.FromProject(expected), time).LineGeometries),
            JsonSerializer.Serialize(scene.LineGeometries));
    }

    private const string OpacitySource = """
        using MakoKaraGenerator.Scripting;
        public sealed class PackEffect : EffectBase
        {
            public override void Evaluate(FrameContext frame, FrameWriter output)
            {
                foreach (var token in frame.Presentations.TokensInBinding)
                    output.Style.MultiplyOpacity(token.WithRuby, .75);
            }
        }
        """;

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task 全局单实例与局部包材质消费正式基线并保持同帧及真实来源(bool transparent)
    {
        var source = LeadInFixture.Create(slots: true);
        var package = MixedEffectPackageFixture.MaterialContents(controllerSource: OpacitySource);
        var configuration = ScriptConfiguration.Create("global", "全局效果", package, "controller", readScope: ScriptReadScope.Project);
        var global = StyleTemplate.Create("global", "global", StyleTemplateKind.Composite, source.LyricStyle, [],
            [TemplateEffectRecipe.FromConfiguration("controller", configuration)], [package]);
        var actual = Apply(source, global, new(ProjectEffectBindingKind.Project));
        var material = new MaterialSpec.PackageMaterial(package.PackageId, package.Revision, "ink");
        var localStyle = source.LyricStyle with { FontSizePx = 38, UnsungFill = new() { Material = material },
            Stroke = new() { Material = material, WidthPx = 4, Join = StrokeJoin.Bevel } };
        actual = Apply(actual, StyleTemplate.Create("local", "local", StyleTemplateKind.Composite, localStyle,
            [LyricStyleProperty.FontSizePx, LyricStyleProperty.UnsungFill, LyricStyleProperty.UnsungStrokes], packages: [package]),
            new(ProjectEffectBindingKind.Lines, ids: ["first"]));
        var identity = Assert.Single(actual.PresetApplications.Items[0].Identities);
        var expected = source with { Effects = new([package], [new() { Id = identity.InstanceId, Name = "全局效果", PackageId = package.PackageId,
            Revision = package.Revision, ExportId = "controller", Seed = identity.Seed, Subscription = new("project") }]) };
        expected = WithLine(expected, "first", line => line with { StyleOverride = new() { FontSizePx = 38,
            UnsungFill = OptionalValue.Set<FillSpecOverride?>(new() { Material = material }), Stroke = OptionalValue.Set<StrokeSpec?>(localStyle.Stroke) } });
        var compiled = await SourceMaterialRuntimeTests.Compile(actual);
        Assert.True(compiled.Succeeded); Assert.Single(compiled.Effects); Assert.NotNull(compiled.SourceMaterials);
        var expectedCompiled = await SourceMaterialRuntimeTests.Compile(expected); Assert.True(expectedCompiled.Succeeded);
        var snapshot = ProjectSnapshot.FromProject(actual); var expectedSnapshot = ProjectSnapshot.FromProject(expected);
        Assert.Single(snapshot.Effects.Instances); Assert.Empty(actual.Effects.Instances);
        await using var session = await compiled.CreateFactory(static () => new SkiaRenderPipeline(), Fx.ReplayReason.Export).CreateAsync(snapshot);
        await using var expectedSession = await expectedCompiled.CreateFactory(static () => new SkiaRenderPipeline(), Fx.ReplayReason.Export).CreateAsync(expectedSnapshot);
        var target = new RenderTarget { PixelWidth = 640, PixelHeight = 360 };
        var mode = transparent ? Fx.OutputMode.Transparent : Fx.OutputMode.Opaque;
        var prepared = await session.PrepareAsync(13_000_000, mode, target);
        var expectedFrame = await expectedSession.PrepareAsync(13_000_000, mode, target);
        Assert.NotEmpty(prepared.Materials);
        var firstBinding = prepared.Materials[0];
        var owner = Assert.IsType<PreparedMaterialOwner.Source>(firstBinding.Owner);
        Assert.False(compiled.SourceMaterials.MatchesSource(firstBinding with { Owner = owner with
            { Reference = owner.Reference with { Path = "presetApplications.wrong.snapshot.style.unsung.fill" } } }, prepared.HierarchyScene));
        Assert.All(prepared.Materials, binding => Assert.StartsWith("presetApplications.", Assert.IsType<PreparedMaterialOwner.Source>(binding.Owner).Reference.Path));
        using var native = CompiledPackageGraphExports.Compile(Assert.Single(compiled.Packages).Definition);
        using var resources = SourceMaterialRuntimeTests.Resources(native, Assert.Single(compiled.Packages));
        using var a = new SkiaRenderPipeline(null, null, null, resources);
        using var b = new SkiaRenderPipeline(null, null, null, resources);
        SamePixels(a, snapshot, b, expectedSnapshot, 13_000_000, transparent, prepared, expectedFrame);
        await using var previewSession = await compiled.CreateFactory(static () => new SkiaRenderPipeline(), Fx.ReplayReason.Initial).CreateAsync(snapshot);
        var previewFrame = await previewSession.PrepareAsync(13_000_000, mode, target);
        SamePixels(a, snapshot, b, snapshot, 13_000_000, transparent, prepared, previewFrame);
        var changed = Apply(actual, global, new(ProjectEffectBindingKind.Project));
        Assert.Same(actual, changed);
        var local = actual.PresetApplications.Items[1];
        Assert.True(PresetApplicationsEditor.TryUpdate(actual, local.Id, StyleTemplate.Create("local", "local", StyleTemplateKind.Composite,
            localStyle with { FontSizePx = 39 }, local.Snapshot.Properties, packages: [package]), null, out changed, out var reason), reason);
        Assert.NotEqual(snapshot.EffectInputsFingerprint, ProjectSnapshot.FromProject(changed).EffectInputsFingerprint);
        var error = await Assert.ThrowsAsync<PreparedFrameException>(async () =>
            await compiled.CreateFactory(static () => new SkiaRenderPipeline(), Fx.ReplayReason.Export).CreateAsync(ProjectSnapshot.FromProject(changed)));
        Assert.Equal("EffectInputsMismatch", error.Code);
    }

    [Theory]
    [InlineData(10_000_000L, true)] [InlineData(40_500_000L, false)]
    public async Task 工程材质预设覆盖真实前奏与跨片段行且保留来源(long time, bool transparent)
    {
        var package = MixedEffectPackageFixture.Contents(pure: true);
        var expected = SourceMaterialRuntimeTests.SourceProject(package);
        var source = LeadInFixture.Create(slots: true, crossClip: true);
        var properties = LyricStylePropertyExtensions.All.Where(property => property is not
            (LyricStyleProperty.VerticalCenterRatio or LyricStyleProperty.HorizontalAlignment));
        var preset = StyleTemplate.Create("material", "material", StyleTemplateKind.Composite, expected.LyricStyle,
            properties, packages: [package]);
        var actual = Apply(source, preset, new(ProjectEffectBindingKind.Project));
        var compiled = await SourceMaterialRuntimeTests.Compile(actual);
        var expectedCompiled = await SourceMaterialRuntimeTests.Compile(expected);
        Assert.True(compiled.Succeeded); Assert.True(expectedCompiled.Succeeded); Assert.Empty(compiled.Effects);
        var snapshot = ProjectSnapshot.FromProject(actual); var expectedSnapshot = ProjectSnapshot.FromProject(expected);
        await using var session = await compiled.CreateFactory(static () => new SkiaRenderPipeline(), Fx.ReplayReason.Export).CreateAsync(snapshot);
        await using var expectedSession = await expectedCompiled.CreateFactory(static () => new SkiaRenderPipeline(), Fx.ReplayReason.Export).CreateAsync(expectedSnapshot);
        var target = new RenderTarget { PixelWidth = 640, PixelHeight = 360 };
        var mode = transparent ? Fx.OutputMode.Transparent : Fx.OutputMode.Opaque;
        var frame = await session.PrepareAsync(time, mode, target);
        var expectedFrame = await expectedSession.PrepareAsync(time, mode, target);
        Assert.All(frame.Materials, binding => Assert.StartsWith("presetApplications.", Assert.IsType<PreparedMaterialOwner.Source>(binding.Owner).Reference.Path));
        if (time == 10_000_000) Assert.Contains(frame.Materials, binding => binding.Target is Fx.ImageEffectTarget.Mark);
        using var native = CompiledPackageGraphExports.Compile(Assert.Single(compiled.Packages).Definition);
        using var resources = SourceMaterialRuntimeTests.Resources(native, Assert.Single(compiled.Packages));
        using var a = new SkiaRenderPipeline(null, null, null, resources); using var b = new SkiaRenderPipeline(null, null, null, resources);
        SamePixels(a, snapshot, b, expectedSnapshot, time, transparent, frame, expectedFrame);
    }

}
