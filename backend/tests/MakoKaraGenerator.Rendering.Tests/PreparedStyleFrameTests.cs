using System.Collections.Immutable;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Execution;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class PreparedStyleFrameTests
{
    private const string Fingerprint = "R2-style-source-fixed";
    private static readonly Lazy<Task<ScriptCompilationResult>> Probe = new(async () =>
    {
        var compiler = new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
        var result = await compiler.CompileAsync([new("src/StyleProbe.cs", """
            using System;
            using MakoKaraGenerator.Scripting;
            public sealed class StyleProbe : EffectBase
            {
                public override void Evaluate(FrameContext frame, FrameWriter output)
                {
                    string mode = frame.Parameters.GetEnum("mode");
                    foreach (var token in frame.Presentations.TokensInBinding)
                    {
                        var target = token.WithRuby; var basis = frame.Baseline.Get(target);
                        if (mode == "baseline" || mode == "reset")
                        { output.Style.SetSungMaterial(target, basis.SungMaterial); if (mode == "baseline") output.Style.SetUnsungMaterial(target, basis.UnsungMaterial); }
                        else if (mode == "red") output.Style.SetSungMaterial(target, MaterialValue.Solid(new(1, 0, 0, 1)));
                        else if (mode == "alpha2") output.Style.MultiplyOpacity(target, 2);
                        else if (mode == "half") output.Style.MultiplyOpacity(target, .5);
                        else if (mode == "badAlpha") output.Style.MultiplyOpacity(target, 3);
                        else if (mode == "wide") output.Style.SetStroke(target, new(1e100, MaterialValue.Solid(new(1, 1, 1, 1))));
                        else if (mode == "off")
                        { output.Style.SetStroke(target, null); output.Style.SetShadow(target, null); output.Style.SetUnsungMaterial(target, MaterialValue.Solid(default)); output.Style.SetSungMaterial(target, MaterialValue.Solid(default)); }
                        else if (mode == "rgba" || mode == "scale")
                        {
                            output.Style.SetUnsungMaterial(target, MaterialValue.Solid(new(30/255d, 200/255d, 80/255d, 180/255d)));
                            output.Style.SetSungMaterial(target, MaterialValue.Solid(new(200/255d, 40/255d, 220/255d, 170/255d)));
                            output.Style.SetStroke(target, new(4, MaterialValue.Solid(new(20/255d, 80/255d, 230/255d, 210/255d)), StrokeJoin.Bevel));
                            output.Style.SetShadow(target, new(new(200/255d, 160/255d, 20/255d, 120/255d), .5, 110, 7, 3));
                            output.Style.MultiplyOpacity(target, .5);
                            if (mode == "scale") output.Transforms.ScaleTokenFamily(target, 1.4, Pivot.BaseAdvanceCenter);
                        }
                        else
                        {
                            var gradient = new GradientValue([new(0, new(1, 0, 0, 1)), new(.3, new(0, 1, 0, 180/255d)), new(1, new(0, 0, 1, 1))],
                                (mode == "radial" || mode == "strokeRadial") ? GradientKind.Radial : GradientKind.Linear, 25,
                                (mode == "canvas" || mode == "strokeCanvas") ? GradientCoordinateSpace.Canvas : GradientCoordinateSpace.Line,
                                (mode == "unsupported" || mode == "strokeUnsupported") ? GradientColorSpace.Oklch : GradientColorSpace.Hsv, GradientHuePath.Longer, .3, .7, .4, .8);
                            if (mode.StartsWith("stroke", StringComparison.Ordinal)) output.Style.SetStroke(target, new(4, MaterialValue.Gradient(gradient),
                                mode == "strokeMiter" ? StrokeJoin.Miter : mode == "strokeBevel" ? StrokeJoin.Bevel : StrokeJoin.Round));
                            else { output.Style.SetSungMaterial(target, MaterialValue.Gradient(gradient)); output.Style.SetUnsungMaterial(target, MaterialValue.Gradient(gradient)); }
                        }
                    }
                }
            }
            """)]);
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics)); return result;
    });
    private static async Task<PureFrameSourceFactory> Factory(string[] modes, Fx.EffectBinding? binding = null)
    {
        var artifact = await Probe.Value;
        return new([.. modes.Select((mode, index) => new CompiledFrameEffect(new("style-" + index), artifact, "StyleProbe",
            new(new Dictionary<string, Fx.ParameterValue> { ["mode"] = new Fx.ParameterValue.Choice(mode) }), binding ?? new Fx.EffectBinding.Project(),
            Fx.EffectCapabilities.Style | Fx.EffectCapabilities.Transforms))], Fingerprint, static () => new SkiaRenderPipeline());
    }
    private static Project Single()
    {
        var line = LyricLine.Create("row", "歌", new(0, 5_000_000), TimingStatus.Edited,
            [LyricToken.Create("word", "歌", new(1_000_000, 3_000_000), TimingStatus.Edited)]) with
            { RubyAnnotations = [RubyAnnotation.Create("ruby", "word", "か")] };
        return Project.Create("style family", Composition.Create(640, 360, Core.Time.FrameRate.Fps30, outputDurationUs: 5_000_000), LyricsDocument.Create([line]),
            lyricStyle: LyricStyle.Default with { FontFamilyName = "Noto Sans CJK SC", FontSizePx = 64,
                UnsungColor = new(240, 240, 255, 190), SungColor = new(255, 90, 20, 190), Opacity = .65f,
                Stroke = StrokeSpec.Create(new(40, 150, 255, 170), 3), Shadow = ShadowSpec.Create(new(120, 20, 20, 110), blurRadiusPx: 3) });
    }
    private static LyricStyle Expected(LyricStyle source, string mode)
    {
        if (mode == "rgba" || mode == "scale") return source with { UnsungFill = new() { Color = new(30, 200, 80, 180) },
            SungFill = new() { Color = new(200, 40, 220, 170) }, Stroke = StrokeSpec.Create(new(20, 80, 230, 210), 4, StrokeJoin.Bevel),
            Shadow = ShadowSpec.Create(new(200, 160, 20, 120), .5f, 110, 7, 3), Opacity = source.Opacity * .5f };
        if (mode == "off") return source with { Stroke = null, Shadow = null, UnsungFill = new() { Color = RgbaColor.Transparent }, SungFill = new() { Color = RgbaColor.Transparent } };
        var gradient = GradientSpec.Create([new(0, new(255, 0, 0)), new(.3, new(0, 255, 0, 180)), new(1, new(0, 0, 255))],
            (mode == "radial" || mode == "strokeRadial") ? GradientKind.Radial : GradientKind.Linear, 25,
            (mode == "canvas" || mode == "strokeCanvas") ? GradientCoordinateSpace.Canvas : GradientCoordinateSpace.Line, GradientColorSpace.Hsv, GradientHuePath.Longer, .3, .7, .4, .8);
        if (mode.StartsWith("stroke", StringComparison.Ordinal)) return source with { Stroke = StrokeSpec.Create(null, 4,
            mode == "strokeMiter" ? StrokeJoin.Miter : mode == "strokeBevel" ? StrokeJoin.Bevel : StrokeJoin.Round) with { Gradient = gradient } };
        return source with { UnsungFill = new() { Color = new(255, 0, 0), Gradient = gradient }, SungFill = new() { Color = new(255, 0, 0), Gradient = gradient } };
    }
    [Theory]
    [InlineData("rgba", 500_000L)]
    [InlineData("rgba", 2_000_000L)]
    [InlineData("rgba", 3_500_000L)]
    [InlineData("off", 2_000_000L)]
    [InlineData("linear", 2_000_000L)]
    [InlineData("radial", 2_000_000L)]
    [InlineData("canvas", 2_000_000L)]
    [InlineData("strokeGradient", 500_000L)] [InlineData("strokeGradient", 2_000_000L)] [InlineData("strokeGradient", 3_500_000L)]
    [InlineData("strokeRadial", 2_000_000L)] [InlineData("strokeCanvas", 2_000_000L)]
    [InlineData("strokeMiter", 2_000_000L)] [InlineData("strokeBevel", 2_000_000L)]
    public async Task 实际作者改色描边阴影透明度和渐变的像素等于静态外观且注音跟随(string mode, long time)
    {
        var project = Single(); var snapshot = ProjectSnapshot.FromProject(project) with { EffectInputsFingerprint = Fingerprint };
        await using var source = await (await Factory([mode])).CreateAsync(snapshot);
        var prepared = await source.PrepareAsync(time, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
        var effect = Assert.Single(prepared.TokenStyles);
        Assert.Equal(Expected(snapshot.LyricStyle, mode), prepared.GetTokenStyle(effect.Target));
        Assert.Equal(prepared.Scene.TokenGeometries.ToArray(), prepared.FinalGeometry.ToArray());
        using SkiaRenderPipeline renderer = new();
        using var expected = renderer.Render(RenderRequest.At(snapshot with { EffectInputsFingerprint = null, LyricStyle = Expected(snapshot.LyricStyle, mode) }, time) with { TransparentBackground = true });
        using var actual = renderer.Render(RenderRequest.At(snapshot, time) with { PreparedFrame = prepared, TransparentBackground = true });
        Assert.Equal(expected.PixelMemory.ToArray(), actual.PixelMemory.ToArray());
        Assert.Same(project.LyricStyle, snapshot.LyricStyle);
    }
    [Fact]
    public async Task 多实例读静态基线而后项Set可还原前项颜色且alpha系数最终合并()
    {
        var snapshot = ProjectSnapshot.FromProject(Single()) with { EffectInputsFingerprint = Fingerprint };
        var target = RenderTarget.FromComposition(snapshot.Composition);
        await using var source = await (await Factory(["red", "reset", "alpha2", "half"])).CreateAsync(snapshot);
        var first = await source.PrepareAsync(2_000_000, Fx.OutputMode.Transparent, target);
        Assert.Empty(first.TokenStyles);
        using SkiaRenderPipeline renderer = new();
        using var actual = renderer.Render(RenderRequest.At(snapshot, first.TimeUs) with { PreparedFrame = first, TransparentBackground = true });
        using var plain = renderer.Render(RenderRequest.At(snapshot with { EffectInputsFingerprint = null }, first.TimeUs) with { TransparentBackground = true });
        Assert.Equal(plain.PixelMemory.ToArray(), actual.PixelMemory.ToArray());
        await source.PrepareAsync(3_000_000, Fx.OutputMode.Transparent, target);
        Assert.Empty((await source.PrepareAsync(2_000_000, Fx.OutputMode.Transparent, target)).TokenStyles);
    }
    [Theory]
    [InlineData("unsupported", "UnsupportedEffectGradient")]
    [InlineData("strokeUnsupported", "UnsupportedEffectGradient")]
    [InlineData("wide", "UnsupportedNativeStyleValues")]
    [InlineData("badAlpha", "InvalidStyleOpacity")]
    public async Task 不支持的输出在准备阶段具名失败而不绘制原画(string mode, string code)
    {
        var snapshot = ProjectSnapshot.FromProject(Single()) with { EffectInputsFingerprint = Fingerprint };
        await using var source = await (await Factory([mode])).CreateAsync(snapshot);
        var error = await Assert.ThrowsAsync<EffectFailureException>(async () => await source.PrepareAsync(2_000_000, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition)));
        Assert.Equal(code, error.Code); Assert.Equal("ComposeOutputs", error.Stage); Assert.Equal(new Fx.EffectInstanceId("style-0"), error.InstanceId);
    }
    [Fact]
    public async Task 样式与视觉缩放共用一次固定字形绘制()
    {
        var project = Single(); var snapshot = ProjectSnapshot.FromProject(project) with { EffectInputsFingerprint = Fingerprint };
        await using var source = await (await Factory(["scale"])).CreateAsync(snapshot);
        var prepared = await source.PrepareAsync(2_000_000, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
        var rootId = project.LyricPresentation.FindProgram("primary")!.RootContainerId;
        var root = project.LyricPresentation.FindNode(rootId)!;
        var parent = SceneContainerGeometryResolver.Resolve(prepared.Scene.Definition, snapshot.Composition)[rootId].WorldMatrix;
        Fx.Affine2 parentFx = new(parent.A, parent.B, parent.C, parent.D, parent.Tx, parent.Ty);
        Assert.True(parentFx.TryInverse(out var parentInverse));
        var basis = Assert.Single(prepared.Scene.TokenGeometries); var final = Assert.Single(prepared.FinalGeometry);
        var local = parentInverse.Compose(basis.BaseGeometry.BaseWorldMatrix); Assert.True(local.TryInverse(out var localInverse));
        var equivalent = final.BaseGeometry.BaseWorldMatrix.Compose(localInverse);
        var placement = ScenePlacement.Create(new(equivalent.Tx / snapshot.Composition.CanvasWidth, equivalent.Ty / snapshot.Composition.CanvasHeight),
            ScenePlacementAnchor.TopLeft, Math.Atan2(equivalent.B, equivalent.A) * 180 / Math.PI, Math.Sqrt(equivalent.A * equivalent.A + equivalent.B * equivalent.B));
        var expectedSnapshot = snapshot with { EffectInputsFingerprint = null, LyricStyle = Expected(snapshot.LyricStyle, "rgba"),
            LyricPresentation = project.LyricPresentation with { Nodes = project.LyricPresentation.Nodes.Replace(root, root with { Placement = placement }) } };
        using SkiaRenderPipeline renderer = new();
        using var expected = renderer.Render(RenderRequest.At(expectedSnapshot, prepared.TimeUs) with { TransparentBackground = true });
        using var actual = renderer.Render(RenderRequest.At(snapshot, prepared.TimeUs) with { PreparedFrame = prepared, TransparentBackground = true });
        Assert.Equal(expected.PixelMemory.ToArray(), actual.PixelMemory.ToArray());
    }
    [Fact]
    public void 完整静态基线按分支行元素继承且未定时隐藏源仍可读()
    {
        var project = Single(); var branch = project.Lyrics.PrimaryBranch!; var line = branch.Lines[0];
        var token = line.Tokens[0] with { StyleOverride = new() { SungColor = new(10, 20, 30, 180), Stroke = StrokeSpec.Create(null, 7f) } };
        line = line with { Tokens = [token], StyleOverride = new() { UnsungColor = new(20, 40, 60, 150), Opacity = .4f } };
        branch = branch with { Lines = [line], StyleOverride = new() { SungColor = new(80, 90, 100, 200), Shadow = OptionalValue.Set<ShadowSpecOverride?>(new() { BlurRadiusPx = 5 }) } };
        var hidden = LyricBranch.Create("hidden", "hidden", LyricBranchRole.Custom) with
            { Lines = [LyricLine.Create("hidden-row", "字", tokens: [LyricToken.Create("missing", "字")])] };
        var snapshot = ProjectSnapshot.FromProject(project with { Lyrics = project.Lyrics with { Branches = [branch, hidden] } });
        var baseline = StaticStyleProjection.CreateBaseline(snapshot);
        var view = baseline.Get(new Fx.TokenId("word"));
        Assert.Equal(new Fx.ColorRgba(10/255d, 20/255d, 30/255d, 180/255d), view.RequireSungSolidColor());
        Assert.Equal(new Fx.ColorRgba(20/255d, 40/255d, 60/255d, 150/255d), ((Fx.MaterialValue.SolidMaterial)view.UnsungMaterial).Color);
        Assert.Equal(7, view.Stroke!.WidthPx); Assert.Equal(5, view.Shadow!.BlurRadiusPx); Assert.Equal((double).4f, view.Opacity);
        Assert.Equal(StaticStyleProjection.View(snapshot.LyricStyle), baseline.Get(new Fx.TokenId("missing")));
        using SkiaRenderPipeline renderer = new(); Assert.DoesNotContain(renderer.GetSceneFrame(snapshot, 2_000_000).Lines, item => item.Branch.Id == "hidden");
        Assert.Equal("MissingBaselineTarget", Assert.Throws<Fx.EffectOutputException>(() => baseline.Get(new Fx.TokenId("gone"))).Code);
    }
    [Fact]
    public async Task 不支持的源渐变原样读取且无变化写入不改变画面或真实首色标()
    {
        var project = Single(); var gradient = GradientSpec.Create([new(0, RgbaColor.White), new(1, RgbaColor.Black)], coordinateSpace: GradientCoordinateSpace.Glyph,
            colorSpace: GradientColorSpace.Oklch);
        var style = project.LyricStyle with { SungFill = new() { Color = new(20, 40, 80, 120), Gradient = gradient } };
        var snapshot = ProjectSnapshot.FromProject(project with { LyricStyle = style }) with { EffectInputsFingerprint = Fingerprint };
        Assert.Same(style, StaticStyleProjection.Apply(style, new()));
        var view = StaticStyleProjection.View(style);
        Assert.Equal(Fx.GradientCoordinateSpace.Glyph, ((Fx.MaterialValue.GradientMaterial)view.SungMaterial).Value.CoordinateSpace);
        await using var source = await (await Factory(["baseline"])).CreateAsync(snapshot);
        var prepared = await source.PrepareAsync(2_000_000, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
        Assert.Empty(prepared.TokenStyles);
        using SkiaRenderPipeline renderer = new();
        using var actual = renderer.Render(RenderRequest.At(snapshot, prepared.TimeUs) with { PreparedFrame = prepared, TransparentBackground = true });
        using var expected = renderer.Render(RenderRequest.At(snapshot with { EffectInputsFingerprint = null }, prepared.TimeUs) with { TransparentBackground = true });
        Assert.Equal(expected.PixelMemory.ToArray(), actual.PixelMemory.ToArray());
        Assert.Equal(gradient.Stops[0].Color, snapshot.LyricStyle.SungFill.Color);
    }
    [Fact]
    public async Task 共享字簇的无变化输出可保留原画而独立改色具名失败()
    {
        var line = LyricLine.Create("row", "office", new(0, 6_000_000), TimingStatus.Edited,
            [.. "office".Select((character, index) => LyricToken.Create("t" + index, character.ToString(), new(index * 1_000_000, (index + 1) * 1_000_000), TimingStatus.Edited))]);
        var snapshot = ProjectSnapshot.FromProject(Project.Create("ligature", Composition.Create(640, 360, Core.Time.FrameRate.Fps30, outputDurationUs: 6_000_000),
            LyricsDocument.Create([line]), lyricStyle: LyricStyle.Default with { FontFamilyName = "DejaVu Sans", FontSizePx = 56 })) with { EffectInputsFingerprint = Fingerprint };
        await using var noOp = await (await Factory(["baseline"])).CreateAsync(snapshot);
        var prepared = await noOp.PrepareAsync(1_500_000, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
        Assert.Empty(prepared.TokenStyles);
        await using var changed = await (await Factory(["red"], new Fx.EffectBinding.Tokens([new("t1")]))).CreateAsync(snapshot);
        var error = await Assert.ThrowsAsync<EffectFailureException>(async () => await changed.PrepareAsync(1_500_000, Fx.OutputMode.Transparent, prepared.Target));
        Assert.Equal("UnsupportedGlyphPartition", error.Code); Assert.Equal("Evaluate", error.Stage);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 前奏父组槽位和入场alpha下基字注音共用外观而邻句保持原样(bool slots)
    {
        var project = LeadInFixture.Create(slots); var tree = project.LyricPresentation;
        var root = tree.FindNode(tree.FindProgram("primary")!.RootContainerId)!;
        tree = tree with { Nodes = tree.Nodes.Replace(root, root with { Placement = ScenePlacement.Create(new(.54, .43), rotationDegrees: 17, scale: .9) }),
            LineOverrides = tree.LineOverrides.Add(new() { BranchId = "primary", LineId = "first", LocalTransform = new() { OffsetXRatio = .02, OffsetYRatio = -.03, RotationDegrees = 9, Scale = .85 } }) };
        var snapshot = ProjectSnapshot.FromProject(project with { LyricPresentation = tree, LyricStyle = project.LyricStyle with { SungColor = new(80, 130, 220) }, EnterAnimation = new() { DurationUs = 2_000_000, StartOpacity = .2f, StartScale = .8f } }) with { EffectInputsFingerprint = Fingerprint };
        await using var source = await (await Factory(["red"], new Fx.EffectBinding.Tokens([new("first-a")]))).CreateAsync(snapshot);
        foreach (long time in new[] { 9_500_000L, 12_500_000L })
        {
            var prepared = await source.PrepareAsync(time, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
            var changed = Assert.Single(prepared.TokenStyles);
            var branch = snapshot.Lyrics.PrimaryBranch!; var line = branch.Lines.First(item => item.Id == "first");
            var token = line.Tokens.First(item => item.Id == "first-a");
            var expectedToken = token with { StyleOverride = (token.StyleOverride ?? new()) with { SungColor = new(255, 0, 0) } };
            var expectedLine = line with { Tokens = line.Tokens.Replace(token, expectedToken) };
            var expectedSnapshot = snapshot with { EffectInputsFingerprint = null, Lyrics = snapshot.Lyrics.ReplaceBranch(branch with { Lines = branch.Lines.Replace(line, expectedLine) }) };
            using SkiaRenderPipeline renderer = new();
            using var expected = renderer.Render(RenderRequest.At(expectedSnapshot, time) with { TransparentBackground = true });
            using var actual = renderer.Render(RenderRequest.At(snapshot, time) with { PreparedFrame = prepared, TransparentBackground = true });
            Assert.Equal(expected.PixelMemory.ToArray(), actual.PixelMemory.ToArray());
            Assert.Equal(prepared.Scene.TokenGeometries.ToArray(), prepared.FinalGeometry.ToArray());
            Assert.Equal("first-a", changed.Target.TokenId.Value);
        }
    }
    [Fact]
    public async Task 缺时间字只改未唱外观且相邻字像素不移动()
    {
        var tokens = new[] { LyricToken.Create("a", "田", new(0, 2_000_000), TimingStatus.Edited), LyricToken.Create("missing", "田"),
            LyricToken.Create("c", "田", new(2_000_000, 4_000_000), TimingStatus.Edited) };
        var line = LyricLine.Create("row", "田田田", new(0, 6_000_000), TimingStatus.Edited, [.. tokens]) with { RubyAnnotations = [RubyAnnotation.Create("ruby", "missing", "た")] };
        var snapshot = ProjectSnapshot.FromProject(Project.Create("neighbors", Composition.Create(900, 450, Core.Time.FrameRate.Fps30, outputDurationUs: 6_000_000),
            LyricsDocument.Create([line]), lyricStyle: LyricStyle.Default with { FontFamilyName = "Noto Sans CJK SC", FontSizePx = 60, LetterSpacingPx = 80,
                UnsungColor = RgbaColor.White, SungColor = new(255, 80, 60), Stroke = null, Shadow = null })) with { EffectInputsFingerprint = Fingerprint };
        await using var source = await (await Factory(["rgba"], new Fx.EffectBinding.Tokens([new("missing")]))).CreateAsync(snapshot);
        var prepared = await source.PrepareAsync(3_000_000, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
        var target = Assert.Single(prepared.TokenStyles).Target;
        Assert.Equal(Fx.ProgressSource.Missing, Scripting.Projection.FrameProjection.Tokens(prepared.Scene).Single(item => item.TokenId == target.TokenId).Performance.ProgressSource);
        using SkiaRenderPipeline renderer = new();
        using var plain = renderer.Render(RenderRequest.At(snapshot with { EffectInputsFingerprint = null }, prepared.TimeUs) with { TransparentBackground = true });
        using var actual = renderer.Render(RenderRequest.At(snapshot, prepared.TimeUs) with { PreparedFrame = prepared, TransparentBackground = true });
        Assert.False(plain.PixelMemory.Span.SequenceEqual(actual.PixelMemory.Span));
        foreach (var neighbor in prepared.Scene.TokenGeometries.Where(item => item.WithRuby != target))
        {
            var corners = neighbor.FamilyGeometry.WorldInkCorners;
            int left = (int)Math.Floor(corners.Min(point => point.X)) - 3, right = (int)Math.Ceiling(corners.Max(point => point.X)) + 3;
            for (int y = 0; y < actual.Height; y++) for (int x = left; x < right; x++)
                Assert.Equal(plain.PixelMemory.Span.Slice(y * plain.Stride + x * 4, 4).ToArray(), actual.PixelMemory.Span.Slice(y * actual.Stride + x * 4, 4).ToArray());
        }
        var expectedStyle = Expected(snapshot.LyricStyle, "rgba") with { SungFill = new() { Color = new(0, 0, 255) } };
        var overriddenToken = tokens[1] with { StyleOverride = new() { UnsungColor = expectedStyle.UnsungColor, SungColor = expectedStyle.SungColor,
            Stroke = OptionalValue.Set<StrokeSpec?>(expectedStyle.Stroke), ShadowSpec = OptionalValue.Set<ShadowSpec?>(expectedStyle.Shadow), Opacity = expectedStyle.Opacity } };
        var expectedLine = line with { Tokens = line.Tokens.Replace(tokens[1], overriddenToken) };
        using var expected = renderer.Render(RenderRequest.At(snapshot with { EffectInputsFingerprint = null, Lyrics = snapshot.Lyrics with { Lines = [expectedLine] } }, prepared.TimeUs) with { TransparentBackground = true });
        Assert.Equal(expected.PixelMemory.ToArray(), actual.PixelMemory.ToArray());
    }
    [Fact]
    public void 手工构造非法准备外观拒绝目标重复alpha越界和未接入材质()
    {
        var snapshot = ProjectSnapshot.FromProject(Single()); using SkiaRenderPipeline renderer = new();
        var scene = renderer.GetSceneFrame(snapshot, 2_000_000); var target = scene.TokenGeometries.Single().WithRuby;
        PreparedRenderFrame Make(ImmutableArray<Fx.TokenFamilyStyle> styles) => new(snapshot, scene.TimeUs, Fx.OutputMode.Transparent,
            RenderTarget.FromComposition(snapshot.Composition), scene, [], styles);
        Assert.Equal("InvalidPreparedStyleTarget", Assert.Throws<PreparedFrameException>(() => Make([new(new(target.TokenId, new("fake")), new())])).Code);
        Assert.Equal("InvalidPreparedStyleTarget", Assert.Throws<PreparedFrameException>(() => Make([new(target, new()), new(target, new())])).Code);
        Assert.Equal("InvalidStyleOpacity", Assert.Throws<PreparedFrameException>(() => Make([new(target, new(opacityFactor: 3))])).Code);
        var resources = new Fx.ResourceCatalog("package", "sha256:" + new string('a', 64), ["glass"], [], []);
        Assert.Equal("MaterialOwnershipRequired", Assert.Throws<PreparedFrameException>(() => Make([new(target,
            new(sung: Fx.MaterialValue.PackageExport(resources.GetMaterial("glass"), Fx.ParameterValues.Empty)))])).Code);
    }

    [Theory]
    [InlineData(1e-20, false)] [InlineData(1e37, false)]
    [InlineData(1e-20, true)] [InlineData(1e37, true)]
    public void 径向渐变实际半径不能被后端夹值或溢出(double radius, bool stroke)
    {
        var snapshot = ProjectSnapshot.FromProject(Single()); using SkiaRenderPipeline renderer = new();
        var scene = renderer.GetSceneFrame(snapshot, 2_000_000); var target = scene.TokenGeometries.Single().WithRuby;
        var gradient = new Fx.GradientValue([new(0, new(1, 1, 1, 1)), new(1, new(0, 0, 0, 1))], Fx.GradientKind.Radial,
            coordinateSpace: Fx.GradientCoordinateSpace.Canvas, radiusX: radius, radiusY: radius);
        var prepared = new PreparedRenderFrame(snapshot, scene.TimeUs, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition), scene, [],
            [new(target, stroke ? new(stroke: new(new Fx.StrokeValue(4, Fx.MaterialValue.Gradient(gradient))))
                : new(sung: Fx.MaterialValue.Gradient(gradient)))]);
        var error = Assert.Throws<PreparedFrameException>(() => renderer.Render(RenderRequest.At(snapshot, scene.TimeUs) with { PreparedFrame = prepared, TransparentBackground = true }));
        Assert.Equal("UnsupportedNativeStyleValues", error.Code);
    }

}
