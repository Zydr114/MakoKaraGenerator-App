using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Execution;
using SkiaSharp;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed partial class PreparedGroupTransformTests
{
    private const string Fingerprint = "group-affine-0.11.1";
    private static readonly Lazy<Task<ScriptCompilationResult>> Probe = new(async () =>
    {
        ScriptCompiler compiler = new(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
        var result = await compiler.CompileAsync([new("src/GroupProbe.cs", """
            using System;
            using System.Linq;
            using MakoKaraGenerator.Scripting;
            public sealed class GroupProbe : EffectBase
            {
                public override void Evaluate(FrameContext frame, FrameWriter output)
                {
                    string mode = frame.Parameters.GetEnum("mode"), node = frame.Parameters.GetEnum("node");
                    var group = frame.GroupGeometry.Single(item => item.Target.NodeId.Value == node);
                    var basis = group.BaseWorldMatrix;
                    if (!basis.TryInverse(out var inverse)) throw new Exception("Real invertible group required");
                    var local = mode == "neutral" ? Affine2.Identity
                        : mode == "localMove" ? Affine2.Translation(10, 20)
                        : mode == "localBack" ? Affine2.Translation(-10, -20)
                        : mode == "tiny" ? new Affine2(1e-150, 0, 0, 1e-150, 0, 0)
                        : mode == "large" ? new Affine2(1e150, 0, 0, 1e150, 0, 0)
                        : mode == "affine" ? new Affine2(.95, .15, -.2, 1.05, 7, -4)
                        : inverse.Compose(Affine2.Translation(mode == "back" ? -30 : mode == "native" ? 1e100 : 30,
                            mode == "back" ? 16 : -16)).Compose(basis);
                    var target = mode == "stale" ? new GroupTarget(new("old"))
                        : mode == "text" ? new GroupTarget(frame.Presentations.LinesInBinding.First().NodeId) : group.Target;
                    output.Transforms.ApplyAffine(target, local);
                    if (mode == "combined")
                    {
                        var line = frame.Presentations.LinesInBinding.Single(item => item.LineId.Value == "first");
                        output.Transforms.ApplyAffine(line.Target, Affine2.Translation(4, -2));
                        foreach (var token in frame.Presentations.TokensInBinding.Where(item => item.LineId == line.LineId))
                            output.Transforms.ScaleTokenFamily(token.WithRuby, 1.2, Pivot.BaseAdvanceCenter);
                        foreach (var mark in frame.Presentations.MarksInBinding.Where(item => item.TargetLine.LineId == line.LineId))
                            output.Transforms.ApplyAffine(mark.Target, Affine2.Translation(2, 3));
                    }
                }
                public override void EvaluateEmitters(EmissionFrameContext frame, EmitterWriter output)
                {
                    if (frame.Parameters.GetEnum("mode") != "particle") return;
                    output.Set(new("cursor"), new() { AnchorMode = CursorAnchorMode.AdvanceCenter, RatePerSecond = 10,
                        LifetimeUs = 300000, SizePx = 4, Color = new(1,0,0,1), SeedStreamKey = "group" });
                }
            }
            """)]);
        Assert.True(result.Succeeded, string.Join('\n', result.Diagnostics)); return result;
    });
    private static ProjectSnapshot Snapshot(bool slots = true, bool fixedParent = false)
    {
        var project = LeadInFixture.Create(slots); var tree = project.LyricPresentation; var program = tree.FindProgram("primary")!;
        var root = tree.FindNode(program.RootContainerId)!; var indicator = tree.FindNode(program.LeadIns[0].IndicatorContainerId)!;
        tree = tree with { Nodes = tree.Nodes.Replace(root, root with { Placement = ScenePlacement.Create(new(.57, .44), rotationDegrees: 18, scale: .85) })
            .Replace(indicator, indicator with { Attachment = new() { Mode = fixedParent ? SceneAttachmentMode.FixedParent : SceneAttachmentMode.CueTargetText },
                Placement = fixedParent ? ScenePlacement.Create(new(.3, .2)) : indicator.Placement }) };
        return ProjectSnapshot.FromProject(project with { LyricPresentation = tree }) with { EffectInputsFingerprint = Fingerprint };
    }
    private static string Root(ProjectSnapshot snapshot) => snapshot.LyricPresentation.FindProgram("primary")!.RootContainerId;
    private static async Task<ImmutableArray<CompiledFrameEffect>> Effects(ProjectSnapshot snapshot, string[] modes,
        Fx.EffectBinding? binding = null, string? node = null)
    {
        var artifact = await Probe.Value;
        return [.. modes.Select((mode, index) => new CompiledFrameEffect(new("group-" + index), artifact, "GroupProbe",
            new(new Dictionary<string, Fx.ParameterValue> { ["mode"] = new Fx.ParameterValue.Choice(mode), ["node"] = new Fx.ParameterValue.Choice(node ?? Root(snapshot)) }),
            binding ?? new Fx.EffectBinding.Node(new(Root(snapshot)), true), Fx.EffectCapabilities.Transforms))];
    }
    private static PureFrameSourceFactory Factory(ImmutableArray<CompiledFrameEffect> effects)
        => new(effects, Fingerprint, static () => new SkiaRenderPipeline());
    private static ProjectSnapshot StaticShift(ProjectSnapshot snapshot)
    {
        var tree = snapshot.LyricPresentation; var root = tree.FindNode(Root(snapshot))!;
        return snapshot with { EffectInputsFingerprint = null, LyricPresentation = tree with { Nodes = tree.Nodes.Replace(root, root with
            { Placement = root.Placement with { Position = new(root.Placement.Position.X + 30d / snapshot.Composition.CanvasWidth,
                root.Placement.Position.Y - 16d / snapshot.Composition.CanvasHeight) } }) } };
    }
    [Theory]
    [InlineData(true, false, 10_000_000L)] [InlineData(true, true, 10_000_000L)]
    [InlineData(false, false, 12_500_000L)] [InlineData(false, true, 12_500_000L)]
    public async Task 组仿射与静态父组移动逐像素相同注音前奏跟随且翻译独立(bool slots, bool fixedParent, long time)
    {
        var snapshot = Snapshot(slots, fixedParent); await using var source = await Factory(await Effects(snapshot, ["move"])).CreateAsync(snapshot);
        var prepared = await source.PrepareAsync(time, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
        Assert.Single(prepared.GroupTransforms); Assert.Empty(prepared.LineTransforms);
        Assert.Same(snapshot.Lyrics, prepared.Snapshot.Lyrics); Assert.Same(snapshot.LyricPresentation, prepared.Snapshot.LyricPresentation);
        foreach (var line in prepared.Scene.Lines)
        {
            var actual = prepared.HierarchyScene.Lines.Single(item => item.BindingId == line.BindingId);
            if (line.Branch.Id == "translation") Assert.Same(line, actual);
            else AssertMatrix(Fx.Affine2.Translation(30, -16).Compose(Matrix(line.Geometry!.WorldMatrix!.Value)), Matrix(actual.Geometry!.WorldMatrix!.Value));
        }
        Assert.All(prepared.FinalMarkGeometry, mark => AssertMatrix(Fx.Affine2.Translation(30, -16).Compose(
            prepared.Scene.MarkGeometries.Single(item => item.Target == mark.Target).Geometry.BaseWorldMatrix), mark.Geometry.BaseWorldMatrix));
        var originalCursors = PlaybackProjection.Create(snapshot, prepared.Scene, prepared.Scene.TokenGeometries).GetCursors(Fx.CursorAnchorMode.Baseline);
        foreach (var cursor in prepared.Playback.GetCursors(Fx.CursorAnchorMode.Baseline))
        {
            var original = originalCursors.Single(item => item.Identity == cursor.Identity);
            double x = cursor.Identity.BranchId.Value == "primary" ? 30 : 0, y = x == 30 ? -16 : 0;
            Assert.Equal(original.Position.X + x, cursor.Position.X, 8); Assert.Equal(original.Position.Y + y, cursor.Position.Y, 8);
        }
        using SkiaRenderPipeline renderer = new();
        using var actualPixels = renderer.Render(RenderRequest.At(snapshot, time) with { PreparedFrame = prepared, TransparentBackground = true });
        using var expectedPixels = renderer.Render(RenderRequest.At(StaticShift(snapshot), time) with { TransparentBackground = true });
        Assert.Equal(expectedPixels.PixelMemory.ToArray(), actualPixels.PixelMemory.ToArray());
        var withVisuals = prepared.WithVisualOutput([new(new("overlay"), new("test"), Fx.OverlayLayer.AboveText, [new(new(4, 5), 2, new(1, 0, 0, 1))])], []);
        Assert.Equal(prepared.GroupTransforms, withVisuals.GroupTransforms); Assert.Same(prepared.HierarchyScene, withVisuals.HierarchyScene);
    }
    [Theory]
    [InlineData("Branch")] [InlineData("Lines")] [InlineData("Tokens")] [InlineData("Clips")]
    public async Task 源筛选可读真实组但绝不授予组写权限(string kind)
    {
        var snapshot = Snapshot(); var tree = snapshot.LyricPresentation; var program = tree.FindProgram("primary")!;
        tree = tree with { Nodes = tree.Nodes.Add(new() { Id = "clip-container", ParentId = program.RootContainerId, Layout = new() }),
            Programs = tree.Programs.Replace(program, program with { Clips = [new() { Id = "clip", ContainerId = "clip-container", LineIds = ["first"] }] }) };
        snapshot = snapshot with { LyricPresentation = tree };
        Fx.EffectBinding binding = kind switch { "Branch" => new Fx.EffectBinding.Branch(new("primary")), "Lines" => new Fx.EffectBinding.Lines([new("first")]),
            "Tokens" => new Fx.EffectBinding.Tokens([new("first-a")]), _ => new Fx.EffectBinding.Clips([new("clip")]) };
        await using var source = await Factory(await Effects(snapshot, ["move"], binding)).CreateAsync(snapshot);
        var failure = await Assert.ThrowsAsync<EffectFailureException>(async () => await source.PrepareAsync(10_000_000, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition)));
        Assert.Equal("TargetOutsideBinding", failure.Code); Assert.Equal("group-0", failure.InstanceId.Value);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task 显式节点只授权自身或运行后代提示组包括动态附着(bool fixedParent)
    {
        var snapshot = Snapshot(fixedParent: fixedParent); var program = snapshot.LyricPresentation.FindProgram("primary")!;
        string indicator = program.LeadIns[0].IndicatorContainerId;
        using SkiaRenderPipeline renderer = new(); var scene = renderer.GetSceneFrame(snapshot, 10_000_000);
        string textNode = scene.Lines.Single(item => item.Frame.Line!.Id == "first").ElementId;
        await using var own = await Factory(await Effects(snapshot, ["move"], new Fx.EffectBinding.Node(new(Root(snapshot)), false))).CreateAsync(snapshot);
        Assert.Single((await own.PrepareAsync(scene.TimeUs, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition))).GroupTransforms);
        await using var blocked = await Factory(await Effects(snapshot, ["move"], new Fx.EffectBinding.Node(new(Root(snapshot)), false), indicator)).CreateAsync(snapshot);
        Assert.Equal("TargetOutsideBinding", (await Assert.ThrowsAsync<EffectFailureException>(async () => await blocked.PrepareAsync(scene.TimeUs, Fx.OutputMode.Transparent,
            RenderTarget.FromComposition(snapshot.Composition)))).Code);
        await using var runtime = await Factory(await Effects(snapshot, ["move"], new Fx.EffectBinding.Node(new(textNode), true), indicator)).CreateAsync(snapshot);
        if (fixedParent) Assert.Equal("TargetOutsideBinding", (await Assert.ThrowsAsync<EffectFailureException>(async () => await runtime.PrepareAsync(scene.TimeUs,
            Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition)))).Code);
        else Assert.Single((await runtime.PrepareAsync(scene.TimeUs, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition))).GroupTransforms);
    }
    [Fact]
    public async Task 工程绑定多实例顺序恒等抵消任意重绘与独立会话确定()
    {
        var snapshot = Snapshot(); var target = RenderTarget.FromComposition(snapshot.Composition); var effects = await Effects(snapshot, ["move", "affine"], new Fx.EffectBinding.Project());
        await using var source = await Factory(effects).CreateAsync(snapshot); await using var other = await Factory(effects).CreateAsync(snapshot);
        var first = await source.PrepareAsync(12_500_000, Fx.OutputMode.Transparent, target);
        var basis = first.Scene.GroupGeometries.Single(item => item.Target.NodeId.Value == Root(snapshot)).BaseWorldMatrix;
        Assert.True(basis.TryInverse(out var inverse));
        Assert.Equal(inverse.Compose(Fx.Affine2.Translation(30, -16)).Compose(basis).Compose(new(.95, .15, -.2, 1.05, 7, -4)), Assert.Single(first.GroupTransforms).LocalMatrix);
        await source.PrepareAsync(18_000_000, Fx.OutputMode.Transparent, target);
        var again = await source.PrepareAsync(first.TimeUs, Fx.OutputMode.Transparent, target); var independent = await other.PrepareAsync(first.TimeUs, Fx.OutputMode.Transparent, target);
        using SkiaRenderPipeline renderer = new();
        byte[] Pixels(PreparedRenderFrame frame) { using var pixels = renderer.Render(RenderRequest.At(snapshot, frame.TimeUs) with { PreparedFrame = frame, TransparentBackground = true }); return pixels.PixelMemory.ToArray(); }
        Assert.Equal(Pixels(first), Pixels(again)); Assert.Equal(Pixels(first), Pixels(independent));
        foreach (var modes in new[] { new[] { "neutral" }, new[] { "localMove", "localBack" } })
        {
            await using var neutral = await Factory(await Effects(snapshot, modes)).CreateAsync(snapshot);
            var frame = await neutral.PrepareAsync(first.TimeUs, Fx.OutputMode.Transparent, target);
            Assert.Empty(frame.GroupTransforms); Assert.Same(frame.Scene, frame.HierarchyScene);
        }
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task 组整行族标记分别组合一次且组参考框不被文字墨迹重算(bool fixedParent)
    {
        var snapshot = Snapshot(fixedParent: fixedParent); await using var source = await Factory(await Effects(snapshot, ["combined"], new Fx.EffectBinding.Project())).CreateAsync(snapshot);
        var frame = await source.PrepareAsync(10_000_000, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
        var line = Assert.Single(frame.LineTransforms); var basis = frame.Scene.LineGeometries.Single(item => item.Target == line.Target).BaseGeometry.BaseWorldMatrix;
        var lineWorld = Fx.Affine2.Translation(30, -16).Compose(basis).Compose(line.LocalMatrix);
        foreach (var token in frame.FinalGeometry.Where(item => item.LineId.Value == "first"))
        {
            var local = frame.TokenTransforms.Single(item => item.Target == token.WithRuby).LocalMatrix;
            AssertMatrix(lineWorld.Compose(local), token.BaseGeometry.BaseWorldMatrix);
            Assert.All(token.Ruby, ruby => AssertMatrix(token.BaseGeometry.BaseWorldMatrix, ruby.Geometry.BaseWorldMatrix));
        }
        Assert.True(basis.TryInverse(out var inverse));
        var delta = lineWorld.Compose(inverse);
        foreach (var mark in frame.FinalMarkGeometry)
        {
            var original = frame.Scene.MarkGeometries.Single(item => item.Target == mark.Target).Geometry.BaseWorldMatrix;
            var local = frame.MarkTransforms.Single(item => item.Target == mark.Target).LocalMatrix;
            AssertMatrix((fixedParent ? Fx.Affine2.Translation(30, -16) : delta).Compose(original).Compose(local), mark.Geometry.BaseWorldMatrix);
        }
        var root = frame.FinalGroupGeometry.Single(item => item.Target.NodeId.Value == Root(snapshot));
        Assert.Equal(frame.Scene.GroupGeometries.Single(item => item.Target == root.Target).ReferenceBounds, root.ReferenceBounds);
        Assert.Equal(frame.FinalGeometry.Where(item => item.BranchId.Value == "primary").SelectMany(item => item.FamilyGeometry.WorldInkCorners)
            .Concat(frame.FinalMarkGeometry.SelectMany(item => item.Geometry.WorldInkCorners)), root.WorldInkCorners);
    }
    [Fact]
    public async Task 生产组捕获未来播放头共用最终组矩阵()
    {
        var snapshot = Snapshot(); var effects = await Effects(snapshot, ["move"]); var target = RenderTarget.FromComposition(snapshot.Composition);
        await using var source = await Factory(effects).CreateAsync(snapshot);
        var prepared = await source.PrepareAsync(10_000_000, Fx.OutputMode.Transparent, target);
        using SkiaRenderPipeline renderer = new(); var request = RenderRequest.At(snapshot, prepared.TimeUs) with { PreparedFrame = prepared, TransparentBackground = true };
        using var capture = renderer.CaptureSceneTarget(request, new SceneCaptureTarget.Group(new(new(Root(snapshot)))),
            new(true, true, true, true, SceneCaptureRegion.FillAndStroke), GraphInputSource.TargetColor, new(0, 0, 640, 360), GraphExecutionBudget.Default);
        using var capturedBitmap = SKBitmap.FromImage(capture.Image);
        using var expected = renderer.CaptureSceneTarget(RenderRequest.At(StaticShift(snapshot), prepared.TimeUs) with { TransparentBackground = true },
            new SceneCaptureTarget.Group(new(new(Root(snapshot)))), new(true, true, true, true, SceneCaptureRegion.FillAndStroke),
            GraphInputSource.TargetColor, new(0, 0, 640, 360), GraphExecutionBudget.Default);
        using var expectedBitmap = SKBitmap.FromImage(expected.Image); Assert.Equal(expectedBitmap.Bytes, capturedBitmap.Bytes);
        var singing = await source.PrepareAsync(12_500_000, Fx.OutputMode.Transparent, target);
        var cursor = singing.Playback.GetCursors(Fx.CursorAnchorMode.AdvanceCenter).Single(item => item.Identity.BranchId.Value == "primary");
        SemanticAnchorQuery query = new(snapshot, effects, Fingerprint, static () => new SkiaRenderPipeline());
        var endpoint = await query.ResolveAsync(cursor.Identity, Fx.CursorEndpointKind.NextStart, Fx.CursorAnchorMode.AdvanceCenter, Fx.OutputMode.Transparent, target);
        Assert.True(endpoint.IsAvailable); var direct = await source.PrepareAsync(endpoint.TimeUs, Fx.OutputMode.Transparent, target);
        Assert.Equal(direct.Playback.GetCursors(Fx.CursorAnchorMode.AdvanceCenter).Single(item => item.Identity == cursor.Identity), endpoint.Cursor);
    }
    [Theory]
    [InlineData("stale")] [InlineData("text")]
    public async Task 旧节点和文本不能伪装成真实组(string mode)
    {
        var snapshot = Snapshot(); await using var source = await Factory(await Effects(snapshot, [mode], new Fx.EffectBinding.Project())).CreateAsync(snapshot);
        Assert.Equal("TargetOutsideBinding", (await Assert.ThrowsAsync<EffectFailureException>(async () => await source.PrepareAsync(10_000_000,
            Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition)))).Code);
    }
    [Theory]
    [InlineData("native")] [InlineData("tiny")]
    public async Task 隐藏空组的原生越限或退化也明确失败(string mode)
    {
        var snapshot = Snapshot(); var indicator = snapshot.LyricPresentation.FindProgram("primary")!.LeadIns[0].IndicatorContainerId;
        await using var source = await Factory(await Effects(snapshot, [mode], new Fx.EffectBinding.Project(), indicator)).CreateAsync(snapshot);
        var prepared = await source.PrepareAsync(20_000_000, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
        Assert.Empty(prepared.FinalGroupGeometry.Single(item => item.Target.NodeId.Value == indicator).WorldInkCorners);
        using SkiaRenderPipeline renderer = new();
        Assert.Equal("UnsupportedNativeCoordinates", Assert.Throws<PreparedFrameException>(() => renderer.Render(RenderRequest.At(snapshot, prepared.TimeUs)
            with { PreparedFrame = prepared, TransparentBackground = true })).Code);
    }
    [Fact]
    public void 准备帧拒绝组重复缺几何错误类型循环和不可逆()
    {
        var snapshot = Snapshot(); using SkiaRenderPipeline renderer = new(); var scene = renderer.GetSceneFrame(snapshot, 10_000_000);
        Fx.GroupTransform transform = new(new(new(Root(snapshot))), Fx.Affine2.Translation(10, 0));
        Assert.Same(scene, SceneAffineProjection.Apply(scene, [], [transform with { LocalMatrix = Fx.Affine2.Identity }]));
        foreach (var transforms in new[] { new[] { transform, transform }, new[] { transform with { Target = new(new("old")) } },
            new[] { transform with { Target = new(new(scene.Lines[0].ElementId)) } } })
            Assert.Equal("InvalidPreparedGroupTarget", Assert.Throws<PreparedFrameException>(() => SceneAffineProjection.Apply(scene, [], [.. transforms])).Code);
        Assert.Equal("InvalidPreparedGroupTarget", Assert.Throws<PreparedFrameException>(() => SceneAffineProjection.Apply(scene with { GroupGeometries = [] }, [], [transform])).Code);
        Assert.Equal("InvalidTransform", Assert.Throws<PreparedFrameException>(() => SceneAffineProjection.Apply(scene, [], [transform with { LocalMatrix = default }])).Code);
        var root = scene.Elements.Single(item => item.Id == Root(snapshot));
        Assert.Equal("InvalidSceneTransform", Assert.Throws<PreparedFrameException>(() => SceneAffineProjection.Apply(scene with
            { Elements = scene.Elements.Replace(root, root with { ParentId = root.Id }) }, [], [transform])).Code);
    }
    private static Fx.Affine2 Matrix(SceneMatrix value) => new(value.A, value.B, value.C, value.D, value.Tx, value.Ty);
    private static void AssertMatrix(Fx.Affine2 expected, Fx.Affine2 actual)
    { Assert.Equal(expected.A, actual.A, 8); Assert.Equal(expected.B, actual.B, 8); Assert.Equal(expected.C, actual.C, 8);
        Assert.Equal(expected.D, actual.D, 8); Assert.Equal(expected.Tx, actual.Tx, 8); Assert.Equal(expected.Ty, actual.Ty, 8); }
}
