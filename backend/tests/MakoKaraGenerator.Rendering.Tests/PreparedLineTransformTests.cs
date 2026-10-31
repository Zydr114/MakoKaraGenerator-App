using System.Collections.Immutable;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Execution;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class PreparedLineTransformTests
{
    private const string Fingerprint = "R2-line-affine-fixed";
    private static readonly Lazy<Task<ScriptCompilationResult>> Probe = new(async () =>
    {
        ScriptCompiler compiler = new(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
        var result = await compiler.CompileAsync([new("src/LineProbe.cs", """
            using System;
            using System.Linq;
            using MakoKaraGenerator.Scripting;
            public sealed class LineProbe : EffectBase
            {
                public override void Evaluate(FrameContext frame, FrameWriter output)
                {
                    string mode = frame.Parameters.GetEnum("mode");
                    foreach (var line in frame.Presentations.LinesInBinding.Where(line => line.LineId.Value == "first"))
                    {
                        var basis = frame.LineGeometry.Single(item => item.Target == line.Target).BaseGeometry.BaseWorldMatrix;
                        if (!basis.TryInverse(out var inverse)) throw new Exception("Real invertible base matrix required");
                        var local = mode == "tiny" ? new Affine2(1e-150, 0, 0, 1e-150, 0, 0)
                            : mode == "shear" ? new Affine2(1.05, .2, .3, .9, 8, -6)
                            : inverse.Compose(Affine2.Translation(mode == "back" ? -30 : mode == "native" ? 1e100 : 30,
                                mode == "back" ? 16 : -16)).Compose(basis);
                        var target = mode == "stale" ? line.Target with { BindingId = new("old") } : line.Target;
                        if (mode != "localOnly") output.Transforms.ApplyAffine(target, mode == "neutral" ? Affine2.Identity : local);
                        if (mode == "combined" || mode == "localOnly")
                        {
                            foreach (var token in frame.Presentations.TokensInBinding.Where(token => token.LineId == line.LineId))
                                output.Transforms.ScaleTokenFamily(token.WithRuby, 1.3, Pivot.BaseAdvanceCenter);
                            foreach (var mark in frame.Presentations.MarksInBinding.Where(mark => mark.TargetLine.LineId == line.LineId))
                                output.Transforms.ApplyAffine(mark.Target, Affine2.Translation(5, -3));
                        }
                    }
                }
            }
            """)]);
        Assert.True(result.Succeeded, string.Join('\n', result.Diagnostics)); return result;
    });
    private static async Task<ImmutableArray<CompiledFrameEffect>> Effects(string[] modes, Fx.EffectBinding? binding = null)
    {
        var artifact = await Probe.Value;
        return [.. modes.Select((mode, index) => new CompiledFrameEffect(new("line-" + index), artifact, "LineProbe",
            new(new Dictionary<string, Fx.ParameterValue> { ["mode"] = new Fx.ParameterValue.Choice(mode) }),
            binding ?? new Fx.EffectBinding.Lines([new("first")]), Fx.EffectCapabilities.Transforms))];
    }
    private static PureFrameSourceFactory Factory(ImmutableArray<CompiledFrameEffect> effects)
        => new(effects, Fingerprint, static () => new SkiaRenderPipeline());
    private static ProjectSnapshot Snapshot(bool slots = true, bool fixedParent = false)
    {
        var project = LeadInFixture.Create(slots); var tree = project.LyricPresentation; var program = tree.FindProgram("primary")!;
        var root = tree.FindNode(program.RootContainerId)!; var group = tree.FindNode(program.LeadIns[0].IndicatorContainerId)!;
        tree = tree with { Nodes = tree.Nodes.Replace(root, root with { Placement = ScenePlacement.Create(new(.57, .44), rotationDegrees: 18, scale: .85) })
            .Replace(group, group with { Attachment = new() { Mode = fixedParent ? SceneAttachmentMode.FixedParent : SceneAttachmentMode.CueTargetText },
                Placement = fixedParent ? ScenePlacement.Create(new(.3, .2)) : group.Placement }),
            LineOverrides = tree.LineOverrides.Add(new() { BranchId = "primary", LineId = "first", LocalTransform = new()
                { OffsetXRatio = .035, OffsetYRatio = -.02, RotationDegrees = 9, Scale = .9 }, EnterAnimation = new()
                { DurationUs = 2_000_000, StartOpacity = .2f, StartScale = .8f, Easing = AnimationEasing.Linear } }) };
        return ProjectSnapshot.FromProject(project with { LyricPresentation = tree, LyricStyle = project.LyricStyle with
            { Opacity = .7f, UnsungColor = new(240, 245, 255, 180), Stroke = StrokeSpec.Create(new(30, 100, 200, 150), 3),
                Shadow = ShadowSpec.Create(new(120, 60, 30, 100), .7f, 70, 8, 3) } }) with { EffectInputsFingerprint = Fingerprint };
    }
    private static ProjectSnapshot StaticShift(ProjectSnapshot snapshot, SceneFrame scene)
    {
        var tree = snapshot.LyricPresentation; var program = tree.FindProgram("primary")!;
        var root = tree.FindNode(program.RootContainerId)!;
        tree = tree with { Nodes = tree.Nodes.Replace(root, root with { Placement = root.Placement with
            { Position = new(root.Placement.Position.X + 30d / snapshot.Composition.CanvasWidth,
                root.Placement.Position.Y - 16d / snapshot.Composition.CanvasHeight) } }) };
        var group = tree.FindNode(program.LeadIns[0].IndicatorContainerId)!;
        if (group.Attachment?.Mode == SceneAttachmentMode.FixedParent)
        {
            var reference = scene.Elements.Single(item => item.Id == group.Id).PlacementReference!;
            var from = reference.WorldMatrix.Unmap(new(0, 0)); var to = reference.WorldMatrix.Unmap(new(30, -16));
            tree = tree with { Nodes = tree.Nodes.Replace(group, group with { Placement = group.Placement with
                { Position = new(group.Placement.Position.X - (to.X - from.X) / reference.Width,
                    group.Placement.Position.Y - (to.Y - from.Y) / reference.Height) } }) };
        }
        return snapshot with { EffectInputsFingerprint = null, LyricPresentation = tree };
    }
    [Theory]
    [InlineData(true, false, 10_000_000L)] [InlineData(true, true, 10_000_000L)]
    [InlineData(false, false, 12_500_000L)] [InlineData(false, true, 12_500_000L)]
    public async Task 整行注音和附着前奏像素等于已有整体移动且固定前奏和翻译保持位置(bool slots, bool fixedParent, long time)
    {
        var snapshot = Snapshot(slots, fixedParent); var effects = await Effects(["shift"]);
        await using var session = await Factory(effects).CreateAsync(snapshot);
        var prepared = await session.PrepareAsync(time, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
        var transform = Assert.Single(prepared.LineTransforms);
        Assert.Same(snapshot.Lyrics, prepared.Snapshot.Lyrics); Assert.Same(snapshot.LyricPresentation, prepared.Snapshot.LyricPresentation);
        var basis = prepared.Scene.LineGeometries.Single(item => item.Target == transform.Target).BaseGeometry;
        var final = prepared.FinalLineGeometry.Single(item => item.Target == transform.Target).BaseGeometry;
        Assert.Equal(basis.BaseWorldMatrix.Compose(transform.LocalMatrix), final.BaseWorldMatrix);
        Assert.All(prepared.FinalGeometry.Where(item => item.WithRuby.BindingId == transform.Target.BindingId), token =>
        { Assert.Equal(final.BaseWorldMatrix, token.BaseGeometry.BaseWorldMatrix); Assert.All(token.Ruby, ruby => Assert.Equal(final.BaseWorldMatrix, ruby.Geometry.BaseWorldMatrix)); });
        Assert.All(prepared.Scene.Lines.Where(item => item.Branch.Id == "translation"), line =>
            Assert.Same(line, prepared.HierarchyScene.Lines.Single(item => item.BindingId == line.BindingId)));
        foreach (var mark in prepared.FinalMarkGeometry)
        {
            var original = prepared.Scene.MarkGeometries.Single(item => item.Target == mark.Target).Geometry;
            var expected = fixedParent ? original.BaseWorldMatrix : Fx.Affine2.Translation(30, -16).Compose(original.BaseWorldMatrix);
            AssertMatrix(expected, mark.Geometry.BaseWorldMatrix);
        }
        using SkiaRenderPipeline renderer = new();
        using var expectedPixels = renderer.Render(RenderRequest.At(StaticShift(snapshot, prepared.Scene), time) with { TransparentBackground = true });
        using var actual = renderer.Render(RenderRequest.At(snapshot, time) with { PreparedFrame = prepared, TransparentBackground = true });
        Assert.Equal(expectedPixels.PixelMemory.ToArray(), actual.PixelMemory.ToArray());
        if (time >= 12_000_000)
        {
            var cursor = Assert.Single(prepared.Playback.GetCursors(Fx.CursorAnchorMode.Baseline));
            var sourceCursor = Assert.Single(PlaybackProjection.Create(snapshot, prepared.Scene, prepared.Scene.TokenGeometries).GetCursors(Fx.CursorAnchorMode.Baseline));
            Assert.Equal(sourceCursor.Position.X + 30, cursor.Position.X, 8); Assert.Equal(sourceCursor.Position.Y - 16, cursor.Position.Y, 8);
        }
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task 整行与族标记局部矩阵只组合一次且最终组墨迹使用真正输出(bool fixedParent)
    {
        var snapshot = Snapshot(fixedParent: fixedParent); var effects = await Effects(["combined"]);
        await using var session = await Factory(effects).CreateAsync(snapshot);
        var prepared = await session.PrepareAsync(10_000_000, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
        var line = Assert.Single(prepared.LineTransforms); var baseWorld = prepared.Scene.LineGeometries.Single(item => item.Target == line.Target).BaseGeometry.BaseWorldMatrix;
        foreach (var token in prepared.FinalGeometry.Where(item => item.WithRuby.BindingId == line.Target.BindingId))
        {
            var local = prepared.TokenTransforms.Single(item => item.Target == token.WithRuby).LocalMatrix;
            Assert.Equal(baseWorld.Compose(line.LocalMatrix).Compose(local), token.BaseGeometry.BaseWorldMatrix);
            Assert.All(token.Ruby, ruby => Assert.Equal(token.BaseGeometry.BaseWorldMatrix, ruby.Geometry.BaseWorldMatrix));
        }
        foreach (var mark in prepared.FinalMarkGeometry)
        {
            var basis = prepared.Scene.MarkGeometries.Single(item => item.Target == mark.Target).Geometry.BaseWorldMatrix;
            var local = prepared.MarkTransforms.Single(item => item.Target == mark.Target).LocalMatrix;
            AssertMatrix((fixedParent ? basis : Fx.Affine2.Translation(30, -16).Compose(basis)).Compose(local), mark.Geometry.BaseWorldMatrix);
        }
        var program = snapshot.LyricPresentation.FindProgram("primary")!;
        var root = prepared.FinalGroupGeometry.Single(item => item.Target.NodeId.Value == program.RootContainerId);
        var expectedInk = prepared.FinalGeometry.Where(item => item.BranchId.Value == "primary").SelectMany(item => item.FamilyGeometry.WorldInkCorners)
            .Concat(prepared.FinalMarkGeometry.SelectMany(item => item.Geometry.WorldInkCorners));
        Assert.Equal(expectedInk, root.WorldInkCorners);
        Assert.Equal(prepared.Scene.GroupGeometries.Single(item => item.Target == root.Target).ReferenceBounds, root.ReferenceBounds);
        using SkiaRenderPipeline renderer = new(); using var actual = renderer.Render(RenderRequest.At(snapshot, prepared.TimeUs) with
            { PreparedFrame = prepared, TransparentBackground = true });
        var expectedSnapshot = StaticShift(snapshot, prepared.Scene) with { EffectInputsFingerprint = Fingerprint };
        await using var expectedSession = await Factory(await Effects(["localOnly"])).CreateAsync(expectedSnapshot);
        var expectedFrame = await expectedSession.PrepareAsync(prepared.TimeUs, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
        using var expectedPixels = renderer.Render(RenderRequest.At(expectedSnapshot, prepared.TimeUs) with
            { PreparedFrame = expectedFrame, TransparentBackground = true });
        Assert.Equal(expectedPixels.PixelMemory.ToArray(), actual.PixelMemory.ToArray());
    }
    [Fact]
    public async Task 无逐字时间整行依据和共享合字仍可完整仿射且未来端点使用最终矩阵()
    {
        var snapshot = Snapshot(false); var branch = snapshot.Lyrics.PrimaryBranch!; var old = branch.Lines[0];
        var line = old with { Text = "office", Tokens = [], RubyAnnotations = [], StyleOverride = new() { FontFamilyName = "DejaVu Sans" } };
        snapshot = snapshot with { Lyrics = snapshot.Lyrics.ReplaceBranch(branch with { Lines = branch.Lines.SetItem(0, line) }) };
        var effects = await Effects(["shift"]); var target = RenderTarget.FromComposition(snapshot.Composition);
        await using var session = await Factory(effects).CreateAsync(snapshot);
        var prepared = await session.PrepareAsync(12_500_000, Fx.OutputMode.Transparent, target);
        var cursor = Assert.Single(prepared.Playback.GetCursors(Fx.CursorAnchorMode.AdvanceCenter));
        Assert.Equal(Fx.ProgressSource.Line, cursor.ProgressSource); Assert.Null(cursor.TokenId);
        SemanticAnchorQuery query = new(snapshot, effects, Fingerprint, static () => new SkiaRenderPipeline());
        var endpoint = await query.ResolveAsync(cursor.Identity, Fx.CursorEndpointKind.NextStart, Fx.CursorAnchorMode.AdvanceCenter, Fx.OutputMode.Transparent, target);
        Assert.True(endpoint.IsAvailable); Assert.Equal(line.Time!.Value.StartUs, endpoint.TimeUs);
        var direct = await session.PrepareAsync(endpoint.TimeUs, Fx.OutputMode.Transparent, target);
        Assert.Equal(Assert.Single(direct.Playback.GetCursors(Fx.CursorAnchorMode.AdvanceCenter)), endpoint.Cursor);
        using SkiaRenderPipeline renderer = new(); using var actual = renderer.Render(RenderRequest.At(snapshot, prepared.TimeUs) with { PreparedFrame = prepared, TransparentBackground = true });
        using var expected = renderer.Render(RenderRequest.At(StaticShift(snapshot, prepared.Scene), prepared.TimeUs) with { TransparentBackground = true });
        Assert.Equal(expected.PixelMemory.ToArray(), actual.PixelMemory.ToArray());
    }
    [Fact]
    public async Task 分割共享ffi合字的元素禁止独立变换但整行仍可变换且字形不重整形()
    {
        var snapshot = Snapshot(false); var branch = snapshot.Lyrics.PrimaryBranch!; var old = branch.Lines[0];
        var line = old with { Text = "ffi", RubyAnnotations = [], Tokens = [LyricToken.Create("f1", "f"), LyricToken.Create("f2", "f"), LyricToken.Create("i", "i")],
            StyleOverride = new() { FontFamilyName = "DejaVu Sans", FontSizePx = 64 } };
        snapshot = snapshot with { Lyrics = snapshot.Lyrics.ReplaceBranch(branch with { Lines = branch.Lines.SetItem(0, line) }) };
        await using var session = await Factory(await Effects(["shift"])).CreateAsync(snapshot);
        var prepared = await session.PrepareAsync(12_500_000, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
        Assert.Single(prepared.LineTransforms); Assert.All(prepared.FinalGeometry.Where(item => item.LineId.Value == "first"), token => Assert.False(token.BaseGeometry.CanTransformIndependently));
        Assert.Equal(prepared.Scene.LineGeometries[0].BaseGeometry.DrawUnits, prepared.FinalLineGeometry[0].BaseGeometry.DrawUnits);
        using SkiaRenderPipeline renderer = new(); using var actual = renderer.Render(RenderRequest.At(snapshot, prepared.TimeUs) with { PreparedFrame = prepared, TransparentBackground = true });
        using var expected = renderer.Render(RenderRequest.At(StaticShift(snapshot, prepared.Scene), prepared.TimeUs) with { TransparentBackground = true });
        Assert.Equal(expected.PixelMemory.ToArray(), actual.PixelMemory.ToArray());
    }
    [Theory]
    [InlineData("Tokens")] [InlineData("stale")]
    public async Task 只选元素不授予整行权限且旧装载不能写入(string mode)
    {
        var snapshot = Snapshot(); Fx.EffectBinding? binding = mode == "Tokens" ? new Fx.EffectBinding.Tokens([new("first-a")]) : null;
        await using var session = await Factory(await Effects([mode == "Tokens" ? "shift" : mode], binding)).CreateAsync(snapshot);
        var error = await Assert.ThrowsAsync<EffectFailureException>(async () => await session.PrepareAsync(10_000_000, Fx.OutputMode.Transparent,
            RenderTarget.FromComposition(snapshot.Composition)));
        Assert.Equal("TargetOutsideBinding", error.Code);
    }
    [Theory]
    [InlineData("Project")] [InlineData("Branch")] [InlineData("Node")]
    public async Task 明确整行或包含后代的节点绑定可写当前完整行(string kind)
    {
        var snapshot = Snapshot(); var program = snapshot.LyricPresentation.FindProgram("primary")!;
        Fx.EffectBinding binding = kind switch { "Project" => new Fx.EffectBinding.Project(), "Branch" => new Fx.EffectBinding.Branch(new("primary")),
            _ => new Fx.EffectBinding.Node(new(program.RootContainerId), true) };
        await using var session = await Factory(await Effects(["shift"], binding)).CreateAsync(snapshot);
        Assert.Single((await session.PrepareAsync(10_000_000, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition))).LineTransforms);
    }
    [Fact]
    public async Task 显式片段在准备阶段仍按真实装载授予整行权限()
    {
        var snapshot = Snapshot(); var tree = snapshot.LyricPresentation; var program = tree.FindProgram("primary")!;
        tree = tree with { Nodes = tree.Nodes.Add(new() { Id = "first-clip-container", ParentId = program.RootContainerId, Layout = new() }),
            Programs = tree.Programs.Replace(program, program with { Clips = [new() { Id = "first-clip", ContainerId = "first-clip-container", LineIds = ["first"] }] }) };
        snapshot = snapshot with { LyricPresentation = tree };
        await using var session = await Factory(await Effects(["shift"], new Fx.EffectBinding.Clips([new("first-clip")]))).CreateAsync(snapshot);
        var prepared = await session.PrepareAsync(10_000_000, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
        var transform = Assert.Single(prepared.LineTransforms); var source = prepared.Scene.Lines.Single(item => item.BindingId == transform.Target.BindingId.Value);
        Assert.Equal("first-clip", source.DisplayClipId); Assert.Equal(SceneDisplayPhase.Preparation, source.DisplayPhase);
    }
    [Fact]
    public async Task 多实例顺序非均匀仿射中性输出和独立会话重绘保持确定()
    {
        var snapshot = Snapshot(false); var effects = await Effects(["shift", "shear"]); var target = RenderTarget.FromComposition(snapshot.Composition);
        await using var session = await Factory(effects).CreateAsync(snapshot); await using var independent = await Factory(effects).CreateAsync(snapshot);
        var a = await session.PrepareAsync(12_500_000, Fx.OutputMode.Transparent, target);
        var first = a.Scene.LineGeometries.Single(item => item.Target.LineId.Value == "first").BaseGeometry.BaseWorldMatrix;
        Assert.True(first.TryInverse(out var inverse));
        var expected = inverse.Compose(Fx.Affine2.Translation(30, -16)).Compose(first).Compose(new(1.05, .2, .3, .9, 8, -6));
        Assert.Equal(expected, Assert.Single(a.LineTransforms).LocalMatrix);
        await session.PrepareAsync(14_000_000, Fx.OutputMode.Transparent, target);
        var b = await session.PrepareAsync(12_500_000, Fx.OutputMode.Transparent, target); var c = await independent.PrepareAsync(12_500_000, Fx.OutputMode.Transparent, target);
        using SkiaRenderPipeline renderer = new();
        using var pa = renderer.Render(RenderRequest.At(snapshot, a.TimeUs) with { PreparedFrame = a, TransparentBackground = true });
        using var pb = renderer.Render(RenderRequest.At(snapshot, b.TimeUs) with { PreparedFrame = b, TransparentBackground = true });
        using var pc = renderer.Render(RenderRequest.At(snapshot, c.TimeUs) with { PreparedFrame = c, TransparentBackground = true });
        Assert.Equal(pa.PixelMemory.ToArray(), pb.PixelMemory.ToArray()); Assert.Equal(pa.PixelMemory.ToArray(), pc.PixelMemory.ToArray());
        await using var neutral = await Factory(await Effects(["neutral"])).CreateAsync(snapshot);
        var none = await neutral.PrepareAsync(a.TimeUs, Fx.OutputMode.Transparent, target);
        Assert.Empty(none.LineTransforms); Assert.Same(none.Scene, none.HierarchyScene);
    }
    [Theory]
    [InlineData("native")] [InlineData("tiny")]
    public async Task 原生越限整行仿射明确阻止绘制包括无逐字元素的行(string mode)
    {
        var snapshot = Snapshot(false); var branch = snapshot.Lyrics.PrimaryBranch!; var line = branch.Lines[0];
        snapshot = snapshot with { Lyrics = snapshot.Lyrics.ReplaceBranch(branch with { Lines = branch.Lines.SetItem(0, line with { Tokens = [], RubyAnnotations = [] }) }) };
        await using var session = await Factory(await Effects([mode])).CreateAsync(snapshot);
        var prepared = await session.PrepareAsync(12_500_000, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
        using SkiaRenderPipeline renderer = new();
        Assert.Equal("UnsupportedNativeCoordinates", Assert.Throws<PreparedFrameException>(() => renderer.Render(RenderRequest.At(snapshot, prepared.TimeUs)
            with { PreparedFrame = prepared, TransparentBackground = true })).Code);
    }
    [Fact]
    public void 准备帧拒绝重复旧装载缺几何和循环父树而中性集合保留原帧()
    {
        var snapshot = Snapshot(); using SkiaRenderPipeline renderer = new(); var scene = renderer.GetSceneFrame(snapshot, 10_000_000);
        var target = scene.LineGeometries.Single(item => item.Target.LineId.Value == "first").Target;
        Fx.LineTransform move = new(target, Fx.Affine2.Translation(10, 0));
        Assert.Same(scene, SceneAffineProjection.Apply(scene, []));
        Assert.Same(scene, SceneAffineProjection.Apply(scene, [move with { LocalMatrix = Fx.Affine2.Identity }]));
        Assert.Equal("InvalidPreparedLineTarget", Assert.Throws<PreparedFrameException>(() => SceneAffineProjection.Apply(scene, [move, move])).Code);
        Assert.Equal("InvalidPreparedLineTarget", Assert.Throws<PreparedFrameException>(() => SceneAffineProjection.Apply(scene,
            [move with { Target = target with { BindingId = new("old") } }])).Code);
        Assert.Equal("InvalidPreparedLineTarget", Assert.Throws<PreparedFrameException>(() => SceneAffineProjection.Apply(scene with { LineGeometries = [] }, [move])).Code);
        var element = scene.Elements.Single(item => item.SourceBinding.LineId == "first" && item.SourceBinding.RubyId is null);
        var cyclic = scene with { Elements = scene.Elements.Replace(element, element with { ParentId = element.Id }) };
        Assert.Equal("InvalidSceneTransform", Assert.Throws<PreparedFrameException>(() => SceneAffineProjection.Apply(cyclic, [move])).Code);
        Assert.Equal("InvalidTransform", Assert.Throws<PreparedFrameException>(() => SceneAffineProjection.Apply(scene, [move with { LocalMatrix = default }])).Code);
    }
    private static void AssertMatrix(Fx.Affine2 expected, Fx.Affine2 actual)
    { Assert.Equal(expected.A, actual.A, 8); Assert.Equal(expected.B, actual.B, 8); Assert.Equal(expected.C, actual.C, 8);
        Assert.Equal(expected.D, actual.D, 8); Assert.Equal(expected.Tx, actual.Tx, 8); Assert.Equal(expected.Ty, actual.Ty, 8); }
}
