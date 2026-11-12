using System.Collections.Immutable;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Execution;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class PreparedMarkFrameTests
{
    private const string Fingerprint = "R2-mark-target-fixed";
    private static readonly Lazy<Task<ScriptCompilationResult>> Probe = new(async () =>
    {
        ScriptCompiler compiler = new(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
        var result = await compiler.CompileAsync([new("src/MarkProbe.cs", """
            using System;
            using System.Linq;
            using MakoKaraGenerator.Scripting;
            public sealed class MarkProbe : EffectBase
            {
                public override void Evaluate(FrameContext frame, FrameWriter output)
                {
                    var future = frame.GeometryQueries.GetBaseAt(37500000, new SourceFilter.Tokens([new("chorus-a")]));
                    if (future.Marks.Length != 3 || future.MarkGeometry.Length != 3 || future.Tokens.Length != 1
                        || future.Groups.Length != future.GroupGeometry.Length || future.Groups.Any(group => !group.IsVisible))
                        throw new Exception("Source queries must include real marks and runtime group ancestors");
                    var empty = frame.GeometryQueries.GetBaseAt(9500000, new SourceFilter.Lines([new("chorus")]));
                    if (empty.Marks.Length != 0 || empty.Groups.Length != 0) throw new Exception("No fake future load");
                    frame.Diagnostics.Report(DiagnosticSeverity.Info, "marks", frame.Presentations.MarksInBinding.Length.ToString());
                    foreach (var line in frame.Project.Content.Lines) frame.Baseline.Get(line.Id);
                    foreach (var group in frame.Presentations.Groups)
                        if (frame.Baseline.Get(group.Target).Opacity != 1) throw new Exception("No fake group fill or inherited static alpha");
                    string mode = frame.Parameters.GetEnum("mode");
                    foreach (var mark in frame.Presentations.MarksInBinding)
                    {
                        var target = mark.Target; var basis = frame.Baseline.Get(target);
                        if (mode == "badCue") target = target with { CueId = new("cue-chorus") };
                        if (mode == "baseline" || mode == "reset")
                        { output.Style.SetUnsungMaterial(target, basis.Unsung.Fill); output.Style.SetSungMaterial(target, basis.Sung.Fill); }
                        else if (mode == "half") output.Style.MultiplyOpacity(target, .5);
                        else if (mode == "alpha2") output.Style.MultiplyOpacity(target, 2);
                        else if (mode == "badAlpha") output.Style.MultiplyOpacity(target, 3);
                        else if (mode == "native") output.Transforms.ApplyAffine(target, Affine2.Translation(1e100, 0));
                        else if (mode == "package") output.Style.SetUnsungMaterial(target,
                            MaterialValue.PackageExport(frame.Resources.GetMaterial("glass"), ParameterValues.Empty));
                        else if (mode == "off")
                        {
                            output.Style.SetUnsungMaterial(target, MaterialValue.Solid(default)); output.Style.SetSungMaterial(target, MaterialValue.Solid(default));
                            output.Style.SetStroke(target, null); output.Style.SetShadow(target, null);
                        }
                        else if (mode == "linear" || mode.StartsWith("stroke", StringComparison.Ordinal))
                        {
                            var material = MaterialValue.Gradient(new([new(0, new(1, 0, 0, 1)), new(1, new(0, 0, 1, .5))],
                                (mode == "strokeRadial" ? GradientKind.Radial : GradientKind.Linear), 25,
                                mode == "strokeCanvas" ? GradientCoordinateSpace.Canvas : GradientCoordinateSpace.Line, GradientColorSpace.Hsv, GradientHuePath.Longer));
                            if (mode.StartsWith("stroke", StringComparison.Ordinal)) output.Style.SetStroke(target,new(4, material, StrokeJoin.Bevel));
                            else { output.Style.SetUnsungMaterial(target, material); output.Style.SetSungMaterial(target, material); }
                        }
                        else
                        {
                            output.Style.SetUnsungMaterial(target, MaterialValue.Solid(new(30/255d, 200/255d, 80/255d, 180/255d)));
                            output.Style.SetSungMaterial(target, MaterialValue.Solid(new(200/255d, 40/255d, 220/255d, 170/255d)));
                            output.Style.SetStroke(target, new(4, MaterialValue.Solid(new(20/255d, 80/255d, 230/255d, 210/255d)), StrokeJoin.Bevel));
                            output.Style.SetShadow(target, new(new(200/255d, 160/255d, 20/255d, 120/255d), .5, 110, 7, 3));
                            output.Style.MultiplyOpacity(target, .5);
                        }
                        if (mode == "affine")
                        {
                            var reference = frame.GroupGeometry.Single(group => group.Target.NodeId == mark.ParentId).ReferenceBounds;
                            var ratio = frame.Project.Scene.GetNode(target.NodeId).ReferenceRatio;
                            output.Transforms.ApplyAffine(target, Affine2.Translation(8, -4));
                            output.Transforms.ApplyAffine(target, Affine2.ScaleAt(1.25, new(reference.Width * ratio.X / 2, reference.Height * ratio.Y / 2)));
                        }
                    }
                }
            }
            """)]);
        Assert.True(result.Succeeded, string.Join('\n', result.Diagnostics)); return result;
    });
    private static async Task<PureFrameSourceFactory> Factory(string[] modes, Fx.EffectBinding? binding = null, Fx.ResourceCatalog? resources = null)
    {
        var artifact = await Probe.Value;
        return new([.. modes.Select((mode, index) => new CompiledFrameEffect(new("mark-" + index), artifact, "MarkProbe",
            new(new Dictionary<string, Fx.ParameterValue> { ["mode"] = new Fx.ParameterValue.Choice(mode) }),
            binding ?? new Fx.EffectBinding.Project(), Fx.EffectCapabilities.Style | Fx.EffectCapabilities.Transforms)
            { Resources = resources ?? Fx.ResourceCatalog.Empty })], Fingerprint, static () => new SkiaRenderPipeline());
    }
    private static Project Project(bool fixedParent)
    {
        var project = LeadInFixture.Create(true, true); var tree = project.LyricPresentation; var program = tree.FindProgram("primary")!;
        var root = tree.FindNode(program.RootContainerId)!; var group = tree.FindNode(program.LeadIns[0].IndicatorContainerId)!;
        var sequence = (CountdownSequenceContent)group.Content!;
        tree = tree with { Nodes = tree.Nodes.Replace(root, root with { Placement = ScenePlacement.Create(new(.58, .44), rotationDegrees: 13, scale: .9) })
            .Replace(group, group with { Attachment = new() { Mode = fixedParent ? SceneAttachmentMode.FixedParent : SceneAttachmentMode.CueTargetText },
                Placement = fixedParent ? ScenePlacement.Create(new(.3, .3)) : group.Placement,
                Content = sequence with { Appearance = sequence.Appearance with { Opacity = .6f } } }),
            LineOverrides = tree.LineOverrides.Add(new() { BranchId = "primary", LineId = "first", LocalTransform = new()
                { OffsetXRatio = .02, OffsetYRatio = -.015, RotationDegrees = 9, Scale = .85 }, EnterAnimation = new()
                { DurationUs = 2_000_000, StartOpacity = .2f, StartScale = .8f, Easing = AnimationEasing.Linear } }) };
        tree = tree with { Nodes = [.. tree.Nodes.Select(node => sequence.MarkNodeIds.Contains(node.Id)
            ? node with { Placement = node.Placement with { RotationDegrees = 7, Scale = .9 } } : node)] };
        return project with { LyricPresentation = tree };
    }
    private static SceneTextAppearance Expected(SceneTextAppearance source, string mode)
    {
        if (mode == "off") return source with { PendingFill = new() { Color = RgbaColor.Transparent }, ElapsedFill = new() { Color = RgbaColor.Transparent }, Stroke = null, Shadow = null };
        if (mode == "linear" || mode.StartsWith("stroke", StringComparison.Ordinal))
        {
            var gradient = GradientSpec.Create([new(0, new(255, 0, 0)), new(1, new(0, 0, 255, 128))], (mode == "strokeRadial" ? GradientKind.Radial : GradientKind.Linear), 25,
                mode == "strokeCanvas" ? GradientCoordinateSpace.Canvas : GradientCoordinateSpace.Line, GradientColorSpace.Hsv, GradientHuePath.Longer);
            if (mode.StartsWith("stroke", StringComparison.Ordinal)) return source with { Stroke = StrokeSpec.Create(null,4,StrokeJoin.Bevel) with { Gradient=gradient } };
            return source with { PendingFill = new() { Color = new(255, 0, 0), Gradient = gradient }, ElapsedFill = new() { Color = new(255, 0, 0), Gradient = gradient } };
        }
        return source with { PendingFill = new() { Color = new(30, 200, 80, 180) }, ElapsedFill = new() { Color = new(200, 40, 220, 170) },
            Stroke = StrokeSpec.Create(new(20, 80, 230, 210), 4, StrokeJoin.Bevel),
            Shadow = ShadowSpec.Create(new(200, 160, 20, 120), .5f, 110, 7, 3), Opacity = source.Opacity * .5f };
    }
    [Theory]
    [InlineData(false, "rgba")] [InlineData(true, "rgba")]
    [InlineData(false, "linear")] [InlineData(true, "off")]
    [InlineData(false, "affine")] [InlineData(true, "affine")]
    [InlineData(false, "strokeLinear")] [InlineData(true, "strokeLinear")]
    [InlineData(false, "strokeRadial")] [InlineData(true, "strokeCanvas")]
    public async Task 实际编译标记两态装饰透明度和仿射像素等于静态编辑且入场只乘一次(bool fixedParent, string mode)
    {
        await Probe.Value;
        var project = Project(fixedParent); var snapshot = ProjectSnapshot.FromProject(project) with { EffectInputsFingerprint = Fingerprint };
        await using var session = await (await Factory([mode])).CreateAsync(snapshot);
        var prepared = await session.PrepareAsync(10_000_000, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
        Assert.Equal(3, prepared.MarkStyles.Length); Assert.Equal(mode == "affine" ? 3 : 0, prepared.MarkTransforms.Length);
        Assert.Empty(prepared.TokenStyles); Assert.Empty(prepared.TokenTransforms);
        var tree = project.LyricPresentation; var group = tree.FindNode(tree.FindProgram("primary")!.LeadIns[0].IndicatorContainerId)!;
        var sequence = (CountdownSequenceContent)group.Content!;
        tree = tree with { Nodes = tree.Nodes.Replace(group, group with { Content = sequence with { Appearance = Expected(sequence.Appearance, mode) } }) };
        if (mode == "affine")
        {
            var reference = prepared.Scene.GroupGeometries.Single(item => item.Target.NodeId.Value == group.Id).ReferenceBounds;
            tree = tree with { Nodes = [.. tree.Nodes.Select(node =>
            {
                if (!sequence.MarkNodeIds.Contains(node.Id)) return node;
                var offset = SceneMatrix.RotateScale(node.Placement.RotationDegrees, node.Placement.Scale).Map(new(8, -4));
                return node with { Placement = node.Placement with { Position = new(node.Placement.Position.X + offset.X / reference.Width,
                    node.Placement.Position.Y + offset.Y / reference.Height), Scale = node.Placement.Scale * 1.25 } };
            })] };
        }
        using SkiaRenderPipeline renderer = new();
        using var expected = renderer.Render(RenderRequest.At(snapshot with { EffectInputsFingerprint = null, LyricPresentation = tree }, prepared.TimeUs) with { TransparentBackground = true });
        using var actual = renderer.Render(RenderRequest.At(snapshot, prepared.TimeUs) with { PreparedFrame = prepared, TransparentBackground = true });
        Assert.Equal(expected.PixelMemory.ToArray(), actual.PixelMemory.ToArray());
        if (mode != "off")
        {
            using var plain = renderer.Render(RenderRequest.At(snapshot with { EffectInputsFingerprint = null }, prepared.TimeUs) with { TransparentBackground = true });
            Assert.False(plain.PixelMemory.Span.SequenceEqual(actual.PixelMemory.Span));
        }
        Assert.Same(project.LyricPresentation, snapshot.LyricPresentation); Assert.Same(project.Lyrics, snapshot.Lyrics);
    }
    [Fact]
    public async Task 标记多实例Set可还原基线alpha最终组合且复用节点不复用装载身份()
    {
        await Probe.Value;
        var snapshot = ProjectSnapshot.FromProject(Project(false)) with { EffectInputsFingerprint = Fingerprint };
        await using var session = await (await Factory(["rgba", "reset", "alpha2"])).CreateAsync(snapshot);
        var target = RenderTarget.FromComposition(snapshot.Composition);
        var first = await session.PrepareAsync(10_000_000, Fx.OutputMode.Transparent, target);
        Assert.All(first.MarkStyles, style => Assert.Equal(.6f, first.GetMarkStyle(style.Target).Opacity));
        // reset restores two fills; decorations intentionally remain from the prior instance.
        Assert.All(first.MarkStyles, style => Assert.Equal(StaticStyleProjection.CreateBaseline(snapshot).Get(style.Target).Sung.Fill,
            StaticStyleProjection.View(first.GetMarkStyle(style.Target)).Sung.Fill));
        var chorus = await session.PrepareAsync(38_000_000, Fx.OutputMode.Transparent, target);
        Assert.Equal(first.MarkStyles.Select(item => item.Target.NodeId), chorus.MarkStyles.Select(item => item.Target.NodeId));
        Assert.All(chorus.MarkStyles, item => Assert.Equal("cue-chorus", item.Target.CueId.Value));
        Assert.NotEqual(first.MarkStyles[0].Target.BindingId, chorus.MarkStyles[0].Target.BindingId);
        Assert.Empty((await session.PrepareAsync(40_000_000, Fx.OutputMode.Transparent, target)).FinalMarkGeometry);
        var repeated = await session.PrepareAsync(10_000_000, Fx.OutputMode.Transparent, target);
        Assert.Equal(first.MarkStyles.ToArray(), repeated.MarkStyles.ToArray());
        await using var neutral = await (await Factory(["baseline"])).CreateAsync(snapshot);
        Assert.Empty((await neutral.PrepareAsync(10_000_000, Fx.OutputMode.Transparent, target)).MarkStyles);
    }
    [Theory]
    [InlineData("badCue", "TargetOutsideBinding")] [InlineData("badAlpha", "InvalidStyleOpacity")]
    [InlineData("package", "PackageDefinitionMissing")]
    public async Task 错误标记装载越界外观或未实现材质明确失败不回退(string mode, string code)
    {
        await Probe.Value;
        var snapshot = ProjectSnapshot.FromProject(Project(false)) with { EffectInputsFingerprint = Fingerprint };
        var resources = new Fx.ResourceCatalog("package", "sha256:" + new string('a', 64), ["glass"], [], []);
        await using var session = await (await Factory([mode], resources: resources)).CreateAsync(snapshot);
        var failure = await Assert.ThrowsAsync<EffectFailureException>(async () => await session.PrepareAsync(10_000_000, Fx.OutputMode.Transparent,
            RenderTarget.FromComposition(snapshot.Composition)));
        Assert.Equal(code, failure.Code);
    }
    [Fact]
    public async Task 双精度有效但原生越限标记变换仍阻止绘制()
    {
        await Probe.Value;
        var snapshot = ProjectSnapshot.FromProject(Project(true)) with { EffectInputsFingerprint = Fingerprint };
        await using var session = await (await Factory(["native"])).CreateAsync(snapshot);
        var prepared = await session.PrepareAsync(10_000_000, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
        using SkiaRenderPipeline renderer = new();
        Assert.Equal("UnsupportedNativeCoordinates", Assert.Throws<PreparedFrameException>(() => renderer.Render(RenderRequest.At(snapshot, prepared.TimeUs)
            with { PreparedFrame = prepared, TransparentBackground = true })).Code);
    }
    [Theory]
    [InlineData("Project", false, 3)] [InlineData("Branch", false, 3)] [InlineData("Lines", false, 3)]
    [InlineData("Tokens", false, 0)] [InlineData("Clip", false, 3)] [InlineData("Mark", false, 1)]
    [InlineData("Group", false, 3)] [InlineData("LineNode", false, 3)] [InlineData("LineNode", true, 0)]
    public async Task 标记绑定依目标源关联或运行父树而元素范围不授予整套提示(string kind, bool fixedParent, int count)
    {
        await Probe.Value;
        var snapshot = ProjectSnapshot.FromProject(Project(fixedParent)) with { EffectInputsFingerprint = Fingerprint };
        long time = kind == "Clip" ? 38_000_000 : 10_000_000;
        using SkiaRenderPipeline renderer = new(); var scene = renderer.GetSceneFrame(snapshot, time); var mark = scene.MarkGeometries[0].Target;
        var line = scene.Lines.Single(item => item.Branch.Id == "primary");
        Fx.EffectBinding binding = kind switch
        {
            "Project" => new Fx.EffectBinding.Project(), "Branch" => new Fx.EffectBinding.Branch(new("primary")),
            "Lines" => new Fx.EffectBinding.Lines([new("first")]), "Tokens" => new Fx.EffectBinding.Tokens([new("first-a")]),
            "Clip" => new Fx.EffectBinding.Clips([new("chorus-clip")]), "Mark" => new Fx.EffectBinding.Node(mark.NodeId, false),
            "Group" => new Fx.EffectBinding.Node(new(scene.TextMarks[0].ContainerId), true),
            "LineNode" => new Fx.EffectBinding.Node(new(line.ElementId), true), _ => throw new InvalidOperationException(),
        };
        await using var session = await (await Factory(["rgba"], binding)).CreateAsync(snapshot);
        var prepared = await session.PrepareAsync(time, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
        Assert.Equal(count, prepared.MarkStyles.Length);
        Assert.Equal(count.ToString(System.Globalization.CultureInfo.InvariantCulture), prepared.Diagnostics.Single(item => item.Diagnostic.Code == "marks").Diagnostic.Message);
    }
    [Fact]
    public void 准备帧拒绝缺少重复和旧提示几何以及失效标记输出()
    {
        var snapshot = ProjectSnapshot.FromProject(Project(false)); using SkiaRenderPipeline renderer = new(); var scene = renderer.GetSceneFrame(snapshot, 10_000_000);
        var target = RenderTarget.FromComposition(snapshot.Composition); var geometry = scene.MarkGeometries[0];
        Assert.Equal("MissingPreparedMarkGeometry", Assert.Throws<PreparedFrameException>(() => new PreparedRenderFrame(snapshot, scene.TimeUs, Fx.OutputMode.Transparent,
            target, scene with { MarkGeometries = [] }, [])).Code);
        Assert.Equal("InvalidPreparedMarkGeometry", Assert.Throws<PreparedFrameException>(() => new PreparedRenderFrame(snapshot, scene.TimeUs, Fx.OutputMode.Transparent,
            target, scene with { MarkGeometries = scene.MarkGeometries.Add(geometry) }, [])).Code);
        Assert.Equal("InvalidPreparedMarkGeometry", Assert.Throws<PreparedFrameException>(() => new PreparedRenderFrame(snapshot, scene.TimeUs, Fx.OutputMode.Transparent,
            target, scene with { MarkGeometries = scene.MarkGeometries.SetItem(0, geometry with { Target = geometry.Target with { CueId = new("cue-chorus") } }) }, [])).Code);
        Assert.Equal("InvalidPreparedMarkTarget", Assert.Throws<PreparedFrameException>(() => new PreparedRenderFrame(snapshot, scene.TimeUs, Fx.OutputMode.Transparent,
            target, scene, [], [], [], [new(geometry.Target with { BindingId = new("old") }, Fx.Affine2.Identity)])).Code);
    }
}
