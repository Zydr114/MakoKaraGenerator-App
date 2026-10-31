using System.Collections.Immutable;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Execution;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class PreparedEffectFrameTests
{
    private const string Fingerprint = "R2-fixed-source-parameters-binding";
    private static readonly Lazy<Task<ScriptCompilationResult>> Word = new(async () =>
    {
        var compiler = new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
        var result = await compiler.CompileAsync([new("src/WordEmphasis.cs", File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "script-samples", "WordEmphasis.cs")))]);
        Assert.True(result.Succeeded, string.Join('\n', result.Diagnostics));
        return result;
    });
    private static readonly Lazy<Task<ScriptCompilationResult>> ScopeProbe = new(async () =>
    {
        var compiler = new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
        var result = await compiler.CompileAsync([new("src/ScopeProbe.cs", """
            using System;
            using MakoKaraGenerator.Scripting;
            public sealed class ScopeProbe : EffectBase
            {
                public override void Evaluate(FrameContext frame, FrameWriter output)
                {
                    if (frame.Project.Content.Lines.Length != 12 || frame.Project.Scene.Clips.Length != 1 || frame.Project.Scene.LeadIns.Length != 2)
                        throw new InvalidOperationException("Complete catalogs must survive output filtering");
                    foreach (var line in frame.Presentations.LinesInBinding)
                    {
                        frame.Project.Scene.GetNode(line.NodeId);
                        frame.Project.Scene.GetNode(line.ParentId);
                    }
                    foreach (var token in frame.Presentations.TokensInBinding)
                        output.Transforms.ScaleTokenFamily(token.WithRuby, 1.2, Pivot.BaseAdvanceCenter);
                }
            }
            """)]);
        Assert.True(result.Succeeded, string.Join('\n', result.Diagnostics)); return result;
    });
    private static Fx.ParameterValues Parameters(double peak = 1.35) => new(new Dictionary<string, Fx.ParameterValue>
    { ["peakScale"] = new Fx.ParameterValue.Number(peak), ["returnUs"] = new Fx.ParameterValue.Duration(500_000) });
    private static async Task<PureFrameSourceFactory> Factory(Fx.EffectBinding? binding = null, double peak = 1.35, int count = 1)
    {
        var artifact = await Word.Value;
        return new([.. Enumerable.Range(0, count).Select(index => new CompiledFrameEffect(new("word-" + index), artifact,
            "WordEmphasis", Parameters(peak), binding ?? new Fx.EffectBinding.Project(), Fx.EffectCapabilities.Transforms))], Fingerprint, static () => new SkiaRenderPipeline());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 真正作者源码在前奏和父组槽位微调下连续求值并生成匹配最终几何(bool slots)
    {
        await Word.Value;
        var project = LeadInFixture.Create(slots); var tree = project.LyricPresentation;
        var program = tree.FindProgram("primary")!; var root = tree.FindNode(program.RootContainerId)!;
        tree = tree with { Nodes = tree.Nodes.Replace(root, root with { Placement = ScenePlacement.Create(new(.54, .43), rotationDegrees: 17, scale: .9) }),
            LineOverrides = tree.LineOverrides.Add(new() { BranchId = "primary", LineId = "first", LocalTransform = new()
                { OffsetXRatio = .02, OffsetYRatio = -.03, RotationDegrees = 9, Scale = .85 } }) };
        var snapshot = ProjectSnapshot.FromProject(project with { LyricPresentation = tree }) with { EffectInputsFingerprint = Fingerprint };
        var factory = await Factory(new Fx.EffectBinding.Tokens([new("first-a")]));
        await using var source = await factory.CreateAsync(snapshot);
        var target = RenderTarget.FromComposition(snapshot.Composition);
        var before = await source.PrepareAsync(9_500_000, Fx.OutputMode.Transparent, target);
        Assert.Empty(before.TokenTransforms); Assert.Equal(3, before.Scene.TextMarks.Length);
        var singing = await source.PrepareAsync(12_500_000, Fx.OutputMode.Transparent, target);
        var transform = Assert.Single(singing.TokenTransforms);
        Assert.Equal(1 + .35 * .875, transform.LocalMatrix.A, 12);
        Assert.Equal(before.Scene.Lines.Single(item => item.Frame.Line!.Id == "first").BindingId, transform.Target.BindingId.Value);
        var basis = singing.Scene.TokenGeometries.Single(item => item.WithRuby == transform.Target);
        var final = singing.FinalGeometry.Single(item => item.WithRuby == transform.Target);
        Assert.Equal(basis.BaseGeometry.BaseWorldMatrix.Compose(transform.LocalMatrix), final.BaseGeometry.BaseWorldMatrix);
        Assert.Equal(final.BaseGeometry.BaseWorldMatrix, Assert.Single(final.Ruby).Geometry.BaseWorldMatrix);
        Assert.Equal(basis.BaseGeometry.AdvanceBounds, final.BaseGeometry.AdvanceBounds);
        var originalPivot = basis.BaseGeometry.BaseWorldMatrix.Map(basis.BaseGeometry.AdvanceBounds.Center);
        var finalPivot = final.BaseGeometry.BaseWorldMatrix.Map(basis.BaseGeometry.AdvanceBounds.Center);
        Assert.Equal(originalPivot.X, finalPivot.X, 10); Assert.Equal(originalPivot.Y, finalPivot.Y, 10);
        using SkiaRenderPipeline renderer = new();
        using var plain = renderer.Render(RenderRequest.At(snapshot with { EffectInputsFingerprint = null }, singing.TimeUs) with { TransparentBackground = true });
        using var effected = renderer.Render(RenderRequest.At(snapshot, singing.TimeUs) with { PreparedFrame = singing, TransparentBackground = true });
        Assert.False(plain.PixelMemory.Span.SequenceEqual(effected.PixelMemory.Span));
        var ended = await source.PrepareAsync(13_250_000, Fx.OutputMode.Transparent, target);
        Assert.Equal(1.175, Assert.Single(ended.TokenTransforms).LocalMatrix.A, 12);
        var returned = await source.PrepareAsync(13_500_000, Fx.OutputMode.Transparent, target);
        Assert.Empty(returned.TokenTransforms);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task 缩放元素族的像素等于已有整体变换且透明填充描边阴影不重影(bool decorated, bool transparentFill)
    {
        await Word.Value;
        var project = SingleFamily(decorated, transparentFill);
        var snapshot = ProjectSnapshot.FromProject(project) with { EffectInputsFingerprint = Fingerprint };
        var factory = await Factory(); await using var source = await factory.CreateAsync(snapshot);
        var prepared = await source.PrepareAsync(2_000_000, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
        var basis = prepared.Scene.TokenGeometries.Single(); var transformed = prepared.FinalGeometry.Single();
        var rootId = project.LyricPresentation.FindProgram("primary")!.RootContainerId;
        var root = project.LyricPresentation.FindNode(rootId)!;
        var parent = SceneContainerGeometryResolver.Resolve(prepared.Scene.Definition, snapshot.Composition)[rootId].WorldMatrix;
        Fx.Affine2 parentFx = new(parent.A, parent.B, parent.C, parent.D, parent.Tx, parent.Ty);
        Assert.True(parentFx.TryInverse(out var parentInverse));
        var local = parentInverse.Compose(basis.BaseGeometry.BaseWorldMatrix); Assert.True(local.TryInverse(out var localInverse));
        var equivalent = transformed.BaseGeometry.BaseWorldMatrix.Compose(localInverse);
        double scale = Math.Sqrt(equivalent.A * equivalent.A + equivalent.B * equivalent.B);
        var rootPlacement = ScenePlacement.Create(new(equivalent.Tx / snapshot.Composition.CanvasWidth,
            equivalent.Ty / snapshot.Composition.CanvasHeight), ScenePlacementAnchor.TopLeft,
            Math.Atan2(equivalent.B, equivalent.A) * 180 / Math.PI, scale);
        var expectedSnapshot = snapshot with { EffectInputsFingerprint = null, LyricPresentation = project.LyricPresentation with
            { Nodes = project.LyricPresentation.Nodes.Replace(root, root with { Placement = rootPlacement }) } };
        using SkiaRenderPipeline renderer = new();
        var expectedGeometry = renderer.GetSceneFrame(expectedSnapshot, prepared.TimeUs).TokenGeometries.Single();
        Assert.Equal(transformed.BaseGeometry.BaseWorldMatrix.A, expectedGeometry.BaseGeometry.BaseWorldMatrix.A, 10);
        Assert.Equal(transformed.BaseGeometry.BaseWorldMatrix.Tx, expectedGeometry.BaseGeometry.BaseWorldMatrix.Tx, 10);
        Assert.Equal(transformed.BaseGeometry.BaseWorldMatrix.Ty, expectedGeometry.BaseGeometry.BaseWorldMatrix.Ty, 10);
        using var expected = renderer.Render(RenderRequest.At(expectedSnapshot, prepared.TimeUs) with { TransparentBackground = true });
        using var actual = renderer.Render(RenderRequest.At(snapshot, prepared.TimeUs) with { PreparedFrame = prepared, TransparentBackground = true });
        Assert.Equal(expected.PixelMemory.ToArray(), actual.PixelMemory.ToArray());
        Assert.True(actual.PixelMemory.Span.ContainsAnyExcept((byte)0));
    }

    [Fact]
    public async Task 只放大中间字且注音跟随时邻字像素不移动未定时字不获得动画()
    {
        await Word.Value;
        var tokens = new[] { LyricToken.Create("a", "田", new(0, 2_000_000), TimingStatus.Edited),
            LyricToken.Create("b", "田", new(2_000_000, 4_000_000), TimingStatus.Edited), LyricToken.Create("missing", "田") };
        var line = LyricLine.Create("row", "田田田", new(0, 6_000_000), TimingStatus.Edited, tokens.ToImmutableArray()) with
            { RubyAnnotations = [RubyAnnotation.Create("rb", "b", "た")] };
        var project = Project.Create("neighbors", Composition.Create(900, 450, Core.Time.FrameRate.Fps30, outputDurationUs: 6_000_000),
            LyricsDocument.Create([line]), lyricStyle: LyricStyle.Default with { FontFamilyName = "Noto Sans CJK SC", FontSizePx = 60,
                LetterSpacingPx = 60, UnsungColor = RgbaColor.White, SungColor = new(255, 80, 60, 255) });
        var snapshot = ProjectSnapshot.FromProject(project) with { EffectInputsFingerprint = Fingerprint };
        var factory = await Factory(new Fx.EffectBinding.Tokens([new("b")]));
        await using var source = await factory.CreateAsync(snapshot);
        var prepared = await source.PrepareAsync(3_000_000, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
        Assert.Equal("b", Assert.Single(prepared.TokenTransforms).Target.TokenId.Value);
        using SkiaRenderPipeline renderer = new();
        using var normal = renderer.Render(RenderRequest.At(snapshot with { EffectInputsFingerprint = null }, prepared.TimeUs) with { TransparentBackground = true });
        using var actual = renderer.Render(RenderRequest.At(snapshot, prepared.TimeUs) with { PreparedFrame = prepared, TransparentBackground = true });
        Assert.False(normal.PixelMemory.Span.SequenceEqual(actual.PixelMemory.Span));
        foreach (var neighbor in prepared.Scene.TokenGeometries.Where(item => item.WithRuby.TokenId.Value != "b"))
        {
            var corners = neighbor.FamilyGeometry.WorldInkCorners;
            int left = (int)Math.Floor(corners.Min(point => point.X)) - 3, right = (int)Math.Ceiling(corners.Max(point => point.X)) + 3;
            for (int y = 0; y < actual.Height; y++) for (int x = left; x < right; x++)
                Assert.Equal(normal.PixelMemory.Span.Slice(y * normal.Stride + x * 4, 4).ToArray(), actual.PixelMemory.Span.Slice(y * actual.Stride + x * 4, 4).ToArray());
        }
        var missingFactory = await Factory(new Fx.EffectBinding.Tokens([new("missing")]));
        await using var missingSource = await missingFactory.CreateAsync(snapshot);
        Assert.Empty((await missingSource.PrepareAsync(5_000_000, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition))).TokenTransforms);
    }

    [Fact]
    public async Task 准备帧不匹配和缺帧在缓存命中前拒绝且不同效果计划不污染缓存()
    {
        await Word.Value;
        var snapshot = ProjectSnapshot.FromProject(SingleFamily(false)) with { EffectInputsFingerprint = Fingerprint };
        var factory = await Factory(); await using var source = await factory.CreateAsync(snapshot);
        var prepared = await source.PrepareAsync(2_000_000, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
        using SkiaRenderPipeline renderer = new(); CachedRenderPipeline cached = new(renderer);
        var request = RenderRequest.At(snapshot, prepared.TimeUs) with { PreparedFrame = prepared, TransparentBackground = true };
        using var first = cached.Render(request); using var hit = cached.Render(request);
        Assert.Equal(first.PixelMemory.ToArray(), hit.PixelMemory.ToArray());
        foreach (var invalid in new[] { request with { PreparedFrame = null }, request with { Project = snapshot with { Name = "another" } },
            request with { TimeUs = request.TimeUs + 1 }, request with { TransparentBackground = false },
            request with { Target = new() { PixelWidth = 320, PixelHeight = 180 } }, request with { LayoutLineId = "row" } })
            Assert.Throws<PreparedFrameException>(() => cached.Render(invalid));
        var secondFactory = await Factory(peak: 1.8); await using var secondSource = await secondFactory.CreateAsync(snapshot);
        var second = await secondSource.PrepareAsync(prepared.TimeUs, prepared.OutputMode, prepared.Target);
        using var changed = cached.Render(request with { PreparedFrame = second });
        Assert.False(changed.PixelMemory.Span.SequenceEqual(first.PixelMemory.Span));
        var noEffect = new PreparedRenderFrame(snapshot, prepared.TimeUs, prepared.OutputMode, prepared.Target, prepared.Scene, []);
        using var identity = cached.Render(request with { PreparedFrame = noEffect });
        using var normal = renderer.Render(request with { Project = snapshot with { EffectInputsFingerprint = null }, PreparedFrame = null });
        Assert.Equal(normal.PixelMemory.ToArray(), identity.PixelMemory.ToArray());
    }

    [Fact]
    public async Task 合字独立请求返回具名实例失败不会静默使用原画()
    {
        await Word.Value;
        var line = LyricLine.Create("row", "office", new(0, 6_000_000), TimingStatus.Edited,
            [.. "office".Select((character, index) => LyricToken.Create("t" + index, character.ToString(), new(index * 1_000_000, (index + 1) * 1_000_000), TimingStatus.Edited))]);
        var snapshot = ProjectSnapshot.FromProject(Project.Create("ligature", Composition.Create(640, 360, Core.Time.FrameRate.Fps30, outputDurationUs: 6_000_000),
            LyricsDocument.Create([line]), lyricStyle: LyricStyle.Default with { FontFamilyName = "DejaVu Sans", FontSizePx = 56 })) with { EffectInputsFingerprint = Fingerprint };
        await using var source = await (await Factory()).CreateAsync(snapshot);
        var error = await Assert.ThrowsAsync<EffectFailureException>(async () => await source.PrepareAsync(1_500_000, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition)));
        Assert.Equal("UnsupportedGlyphPartition", error.Code); Assert.Equal("Evaluate", error.Stage);
        Assert.Equal(new Fx.EffectInstanceId("word-0"), error.InstanceId); Assert.Equal(1_500_000, error.TimeUs);
    }

    [Fact]
    public async Task 多实例按顺序组合且重绘跳转不重复放大()
    {
        await Word.Value;
        var snapshot = ProjectSnapshot.FromProject(SingleFamily(false)) with { EffectInputsFingerprint = Fingerprint };
        var factory = await Factory(count: 2); await using var source = await factory.CreateAsync(snapshot);
        var target = RenderTarget.FromComposition(snapshot.Composition);
        var first = await source.PrepareAsync(2_000_000, Fx.OutputMode.Transparent, target);
        await source.PrepareAsync(2_500_000, Fx.OutputMode.Transparent, target);
        var again = await source.PrepareAsync(2_000_000, Fx.OutputMode.Transparent, target);
        double once = 1 + .35 * .875;
        Assert.Equal(once * once, Assert.Single(first.TokenTransforms).LocalMatrix.A, 12);
        Assert.Equal(first.TokenTransforms.ToArray(), again.TokenTransforms.ToArray());
        await using var other = await factory.CreateAsync(snapshot);
        Assert.Equal(first.TokenTransforms.ToArray(), (await other.PrepareAsync(2_000_000, Fx.OutputMode.Transparent, target)).TokenTransforms.ToArray());
    }

    [Fact]
    public async Task 有限double却超过原生范围的输出明确拒绝()
    {
        await Word.Value;
        var snapshot = ProjectSnapshot.FromProject(SingleFamily(false)) with { EffectInputsFingerprint = Fingerprint };
        var factory = await Factory(peak: 1e100); await using var source = await factory.CreateAsync(snapshot);
        var prepared = await source.PrepareAsync(2_000_000, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
        using SkiaRenderPipeline renderer = new();
        var error = Assert.Throws<PreparedFrameException>(() => renderer.Render(RenderRequest.At(snapshot, prepared.TimeUs) with { PreparedFrame = prepared, TransparentBackground = true }));
        Assert.Equal("UnsupportedNativeCoordinates", error.Code);
    }

    [Fact]
    public async Task 完整目录与工程分支行元素片段节点输出范围分离且失效绑定拒绝()
    {
        var project = LeadInFixture.Create(slots: true, crossClip: true);
        var snapshot = ProjectSnapshot.FromProject(project) with { EffectInputsFingerprint = Fingerprint };
        var root = project.LyricPresentation.FindProgram("primary")!.RootContainerId;
        var artifact = await ScopeProbe.Value;
        var target = RenderTarget.FromComposition(snapshot.Composition);
        var cases = new (Fx.EffectBinding Binding, long Time, int Count)[]
        {
            (new Fx.EffectBinding.Project(), 12_500_000, 2), (new Fx.EffectBinding.Branch(new("primary")), 12_500_000, 2),
            (new Fx.EffectBinding.Lines([new("first")]), 12_500_000, 2), (new Fx.EffectBinding.Tokens([new("first-a")]), 12_500_000, 1),
            (new Fx.EffectBinding.Clips([new("chorus-clip")]), 12_500_000, 0), (new Fx.EffectBinding.Clips([new("chorus-clip")]), 40_500_000, 2),
            (new Fx.EffectBinding.Node(new(root), true), 12_500_000, 2), (new Fx.EffectBinding.Node(new(root), false), 12_500_000, 0),
            (new Fx.EffectBinding.Node(new(SceneDefinitionBuilder.TextId("primary", "first")), false), 12_500_000, 2),
        };
        foreach (var item in cases)
        {
            PureFrameSourceFactory factory = new([new(new("scope"), artifact, "ScopeProbe", Fx.ParameterValues.Empty,
                item.Binding, Fx.EffectCapabilities.Transforms)], Fingerprint, static () => new SkiaRenderPipeline());
            await using var source = await factory.CreateAsync(snapshot);
            var prepared = await source.PrepareAsync(item.Time, Fx.OutputMode.Transparent, target);
            Assert.Equal(item.Count, prepared.TokenTransforms.Length);
        }
        var invalid = await Factory(new Fx.EffectBinding.Tokens([new("gone")]));
        var error = await Assert.ThrowsAsync<PreparedFrameException>(async () => await invalid.CreateAsync(snapshot));
        Assert.Equal("InvalidEffectBinding", error.Code);
    }

    private static Project SingleFamily(bool decorated, bool transparentFill = false)
    {
        var line = LyricLine.Create("row", "歌", new(0, 5_000_000), TimingStatus.Edited,
            [LyricToken.Create("word", "歌", new(1_000_000, 3_000_000), TimingStatus.Edited)]) with { RubyAnnotations = [RubyAnnotation.Create("ruby", "word", "か")] };
        var style = LyricStyle.Default with { FontFamilyName = "Noto Sans CJK SC", FontSizePx = 64,
            UnsungColor = new(240, 240, 255, transparentFill ? (byte)0 : (byte)190),
            SungColor = new(255, 90, 20, transparentFill ? (byte)0 : (byte)190), Opacity = .65f,
            Stroke = decorated ? StrokeSpec.Create(new(40, 150, 255, 170), 3) : null,
            Shadow = decorated ? new() { Color = new(120, 20, 20, 110), DistancePx = 4, AngleDegrees = 45, BlurRadiusPx = 3 } : null };
        return Project.Create("single family", Composition.Create(640, 360, Core.Time.FrameRate.Fps30, outputDurationUs: 5_000_000), LyricsDocument.Create([line]), lyricStyle: style);
    }
}
