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
    private static readonly Lazy<Task<CompiledEffectPackage>> Probe = new(() => V2RenderPackageHelper.CompileControllerAsync("""
        using System;
        using MakoKaraGenerator.Scripting;
        using MakoKaraGenerator.Scripting.V2;

        public sealed record GroupState : PreparedEffect;

        public sealed class GroupProbe : IPureKaraokeEffect
        {
            public PreparedEffect Declare(DeclareContext source)
            {
                if (source.Parameters.GetEnum("mode") != "particle") return PreparedEffect.Empty;
                var particle = new ParticleSpec
                {
                    LifetimeUs = 300_000, SizePx = 4, Color = new(1, 0, 0, 1), SeedStreamKey = "group",
                };
                var continuous = new CursorEmissionPlan(new("cursor"),
                    new RateTrack([new(new(0, 30_000_000), 10, 10)], [new(0, 30_000_000)]),
                    particle, CursorAnchorMode.AdvanceCenter, BridgePolicy.Jump);
                return new GroupState { Particles = new([continuous], []) };
            }
            public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output)
            {
                string mode = frame.Parameters.GetEnum("mode"), node = frame.Parameters.GetText("node");
                GroupGeometrySample? group = null;
                foreach (var item in frame.GroupGeometry)
                    if (item.Target.NodeId.Value == node) { group = item; break; }
                if (group is null) throw new InvalidOperationException("Group not found: " + node);
                var basis = group.BaseWorldMatrix;
                Affine2 inverse;
                if (!basis.TryInverse(out inverse)) throw new InvalidOperationException("Real invertible group required");
                var local = mode == "neutral" ? Affine2.Identity
                    : mode == "localMove" ? Affine2.Translation(10, 20)
                    : mode == "localBack" ? Affine2.Translation(-10, -20)
                    : mode == "tiny" ? new Affine2(1e-30, 0, 0, 1e-30, 0, 0)
                    : mode == "large" ? new Affine2(1e12, 1, 1e12, 2, 0, 0)
                    : mode == "native" ? new Affine2(1e38, 0, 0, 1e38, 0, 0)
                    : mode == "affine" ? new Affine2(.95, .15, -.2, 1.05, 7, -4)
                    : inverse.Compose(Affine2.Translation(mode == "back" ? -30 : 30,
                        mode == "back" ? 16 : -16)).Compose(basis);
                var target = mode == "stale" ? new GroupTarget(new NodeId("old"))
                    : mode == "text" ? new GroupTarget(frame.Presentations.LinesInBinding[0].NodeId) : group.Target;
                output.AppendTransform(new TextTarget.Group(target), local);
                if (mode == "combined")
                {
                    FrameLine? line = null;
                    foreach (var item in frame.Presentations.LinesInBinding)
                        if (item.LineId.Value == "first") { line = item; break; }
                    if (line is null) throw new InvalidOperationException("Line first not found");
                    output.AppendTransform(new TextTarget.Line(line.Target), Affine2.Translation(4, -2));
                    foreach (var token in frame.Presentations.TokensInBinding)
                    {
                        if (token.LineId != line.LineId) continue;
                        foreach (var geom in frame.Geometry)
                            if (geom.WithRuby == token.WithRuby)
                                output.AppendTransform(token.WithRuby, Affine2.ScaleAt(1.2, geom.BaseGeometry.AdvanceBounds.Center));
                    }
                    foreach (var mark in frame.Presentations.MarksInBinding)
                        if (mark.TargetLine.LineId == line.LineId)
                            output.AppendTransform(new TextTarget.Mark(mark.Target), Affine2.Translation(2, 3));
                }
            }
        }
        """,
        [Fx.V2.CapabilityNames.GroupAffine, Fx.V2.CapabilityNames.LineAffine, Fx.V2.CapabilityNames.TokenAffine, Fx.V2.CapabilityNames.MarkAffine, Fx.V2.CapabilityNames.ParticlesContinuous, Fx.V2.CapabilityNames.VisualsDots],
        entryType: "GroupProbe",
        exportId: "group-probe",
        parameters: new System.Text.Json.Nodes.JsonArray(
            V2RenderPackageHelper.EnumParameter("mode", "move", "move", "back", "neutral", "localMove", "localBack", "tiny", "large", "affine", "native", "stale", "text", "combined", "particle"),
            V2RenderPackageHelper.TextParameter("node", "")),
        outputs:
        [
            V2RenderPackageHelper.Output("group", "VisualTransform", "group", Fx.V2.CapabilityNames.GroupAffine),
            V2RenderPackageHelper.Output("line", "VisualTransform", "line", Fx.V2.CapabilityNames.LineAffine),
            V2RenderPackageHelper.Output("token", "VisualTransform", "tokenFamily", Fx.V2.CapabilityNames.TokenAffine),
            V2RenderPackageHelper.Output("mark", "VisualTransform", "textMark", Fx.V2.CapabilityNames.MarkAffine),
            new System.Text.Json.Nodes.JsonObject
            {
                ["id"] = "cursor",
                ["kind"] = "Overlay",
                ["target"] = "canvas",
                ["requires"] = new System.Text.Json.Nodes.JsonArray(Fx.V2.CapabilityNames.ParticlesContinuous, Fx.V2.CapabilityNames.VisualsDots),
                ["dependsOn"] = new System.Text.Json.Nodes.JsonArray(),
            },
        ],
        emitters: new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
        {
            ["id"] = "cursor", ["kind"] = "cursor", ["maxLifetimeUs"] = 500_000,
        }),
        packageId: "demo.group.transform.v2"));
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
        var package = await Probe.Value;
        return [.. modes.Select((mode, index) => package.CreateEffect("group-probe", new("group-" + index),
            binding ?? new Fx.EffectBinding.Node(new(Root(snapshot)), true), 0) with
        {
            Parameters = new Fx.ParameterValues(new Dictionary<string, Fx.ParameterValue>
            {
                ["mode"] = new Fx.ParameterValue.Choice(mode),
                ["node"] = new Fx.ParameterValue.Text(node ?? Root(snapshot)),
            }),
        })];
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
        Assert.Equal(new Fx.Affine2(.95, .15, -.2, 1.05, 7, -4).Compose(inverse.Compose(Fx.Affine2.Translation(30, -16)).Compose(basis)), Assert.Single(first.GroupTransforms).LocalMatrix);
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
        await using var independent = await Factory(effects).CreateAsync(snapshot);
        var endpoint = await independent.PrepareAsync(cursor.Identity.TimeRange.StartUs, Fx.OutputMode.Transparent, target);
        var direct = await source.PrepareAsync(cursor.Identity.TimeRange.StartUs, Fx.OutputMode.Transparent, target);
        Assert.Equal(direct.Playback.GetCursors(Fx.CursorAnchorMode.AdvanceCenter).Single(item => item.Identity == cursor.Identity),
            endpoint.Playback.GetCursors(Fx.CursorAnchorMode.AdvanceCenter).Single(item => item.Identity == cursor.Identity));
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
