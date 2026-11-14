using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Execution;
using MakoKaraGenerator.Scripting.Projection;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class FrameBindingScopeIntegrationTests
{
    private const string Fingerprint = "R7-real-frame-binding-scope";
    private static readonly Lazy<Task<CompiledEffectPackage>> Probe = new(() => V2RenderPackageHelper.CompileControllerAsync("""
        using System;
        using MakoKaraGenerator.Scripting;
        using MakoKaraGenerator.Scripting.V2;

        public sealed record ScopeState : PreparedEffect;

        public sealed class BindingScopeProbe : IPureKaraokeEffect, IPureVisualEffect
        {
            public PreparedEffect Declare(DeclareContext source)
                => new ScopeState { VisualOutputs = [new(new("seen"), VisualPrimitiveKind.Dots, OverlayLayer.AboveText)] };
            public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output)
            {
                string mode = frame.Parameters.GetEnum("mode");
                if (mode == "all")
                {
                    foreach (var token in frame.Presentations.TokensInBinding)
                        output.AppendTransform(new TextTarget.Family(token.WithRuby), new Affine2(1.2, 0, 0, 1.2, 0, 0));
                    foreach (var line in frame.Presentations.LinesInBinding)
                        output.AppendTransform(new TextTarget.Line(line.Target), Affine2.Translation(8, 0));
                    foreach (var mark in frame.Presentations.MarksInBinding)
                        output.AppendTransform(new TextTarget.Mark(mark.Target), Affine2.Translation(0, 5));
                }
                if (mode == "observe")
                {
                    if (frame.Presentations.TokensInBinding.Length != 1 || frame.Presentations.TokensInBinding[0].TokenId.Value != "first-a"
                        || frame.Presentations.LinesInBinding.Length != 1 || frame.Presentations.LinesInBinding[0].LineId.Value != "first"
                        || frame.Presentations.MarksInBinding.Length != 0)
                        throw new InvalidOperationException("unexpected observe scope in Evaluate");
                }
                if (mode == "lineWrite")
                    output.AppendTransform(new TextTarget.Line(frame.Presentations.LinesInBinding[0].Target), Affine2.Translation(8, 0));
                if (mode == "groupWrite")
                    output.AppendTransform(new TextTarget.Group(new GroupTarget(new NodeId(frame.Parameters.GetText("group")))), Affine2.Translation(8, 0));
                if (mode == "markWrite")
                    output.AppendTransform(new TextTarget.Mark(new TextMarkTarget(new NodeId(frame.Parameters.GetText("mark")),
                        new CueId("cue-first"), frame.Presentations.LinesInBinding[0].BindingId)), Affine2.Translation(0, 5));
            }
            public void EvaluateVisuals(in VisualFrame frame, PreparedEffect data, VisualOutput output)
            {
                if (frame.Parameters.GetEnum("mode") == "observe")
                {
                    if (frame.FinalGeometry.Presentations.LinesInBinding.Length != 1
                        || frame.FinalGeometry.Presentations.LinesInBinding[0].LineId.Value != "first")
                        throw new InvalidOperationException("unexpected observe scope in EvaluateVisuals");
                }
                int count = frame.FinalGeometry.Presentations.TokensInBinding.Length
                    + frame.FinalGeometry.Presentations.LinesInBinding.Length * 10
                    + frame.FinalGeometry.Presentations.MarksInBinding.Length * 100;
                output.DrawDots(new("seen"), [new(new Point2(count, 1), 1, new ColorRgba(1, 0, 0, 1))], OverlayLayer.AboveText);
            }
        }
        """,
        [Fx.V2.CapabilityNames.TokenAffine, Fx.V2.CapabilityNames.LineAffine, Fx.V2.CapabilityNames.GroupAffine, Fx.V2.CapabilityNames.MarkAffine, Fx.V2.CapabilityNames.VisualsDots],
        entryType: "BindingScopeProbe",
        exportId: "binding-scope",
        parameters: new System.Text.Json.Nodes.JsonArray(
            V2RenderPackageHelper.EnumParameter("mode", "all", "all", "observe", "lineWrite", "groupWrite", "markWrite"),
            V2RenderPackageHelper.TextParameter("group", ""),
            V2RenderPackageHelper.TextParameter("mark", "")),
        outputs:
        [
            V2RenderPackageHelper.Output("token", "VisualTransform", "tokenFamily", Fx.V2.CapabilityNames.TokenAffine),
            V2RenderPackageHelper.Output("line", "VisualTransform", "line", Fx.V2.CapabilityNames.LineAffine),
            V2RenderPackageHelper.Output("group", "VisualTransform", "group", Fx.V2.CapabilityNames.GroupAffine),
            V2RenderPackageHelper.Output("mark", "VisualTransform", "textMark", Fx.V2.CapabilityNames.MarkAffine),
            V2RenderPackageHelper.Output("seen", "Overlay", "canvas", Fx.V2.CapabilityNames.VisualsDots),
        ],
        packageId: "demo.binding.scope.v2"));
    private static ProjectSnapshot Snapshot(bool fixedParent = false)
    {
        var project = LeadInFixture.Create(true, true);
        if (fixedParent)
        {
            var tree = project.LyricPresentation; var program = tree.FindProgram("primary")!;
            var indicator = tree.FindNode(program.LeadIns[0].IndicatorContainerId)!;
            project = project with { LyricPresentation = tree with { Nodes = tree.Nodes.Replace(indicator,
                indicator with { Attachment = new() { Mode = SceneAttachmentMode.FixedParent } }) } };
        }
        return ProjectSnapshot.FromProject(project) with { EffectInputsFingerprint = Fingerprint };
    }
    private static async Task<PureFrameSourceFactory> Factory(Fx.EffectBinding binding, string mode = "all", string group = "", string mark = "")
    {
        var parameters = new Fx.ParameterValues(new Dictionary<string, Fx.ParameterValue>
        {
            ["mode"] = new Fx.ParameterValue.Choice(mode), ["group"] = new Fx.ParameterValue.Text(group), ["mark"] = new Fx.ParameterValue.Text(mark),
        });
        var package = await Probe.Value;
        return new([package.CreateEffect("binding-scope", new("scope-instance"), binding, 0) with { Parameters = parameters }],
            Fingerprint, static () => new SkiaRenderPipeline());
    }
    private static Fx.SceneCatalog Catalog(ProjectSnapshot snapshot) => SceneProjection.Create(snapshot, ContentProjection.Create(snapshot));
    private static int VisualSeenCount(PreparedRenderFrame frame)
        => (int)Assert.Single(Assert.Single(frame.DotBatches).Dots).Position.X;

    [Fact]
    public async Task 真实槽位轮换查询与生产文字及视觉阶段使用相同当前装载()
    {
        var snapshot = Snapshot(); using SkiaRenderPipeline renderer = new(); var catalog = Catalog(snapshot);
        var first = renderer.GetSceneFrame(snapshot, 12_500_000); var last = renderer.GetSceneFrame(snapshot, 47_000_000);
        var original = FrameProjection.Lines(first, catalog).Single(line => line.LineId.Value == "first");
        var next = FrameProjection.Lines(last, catalog).Single(line => line.LineId.Value == "tail-a");
        Assert.Equal(original.ParentId, next.ParentId); Assert.NotEqual(original.BindingId, next.BindingId);
        var binding = new Fx.EffectBinding.Node(original.ParentId, true);
        await using var source = await (await Factory(binding)).CreateAsync(snapshot);
        foreach (var scene in new[] { first, last })
        {
            var scope = new FrameBindingScope(scene);
            var tokens = FrameProjection.Tokens(scene).Where(token => scope.ContainsToken(binding, token)).ToArray();
            var lines = FrameProjection.Lines(scene, catalog).Where(line => scope.ContainsLine(binding, line)).ToArray();
            Assert.Single(lines); Assert.Equal(2, tokens.Length);
            var prepared = await source.PrepareAsync(scene.TimeUs, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
            Assert.Equal(tokens.Select(token => token.WithRuby), prepared.TokenTransforms.Select(item => item.Target));
            Assert.Equal(lines.Select(line => line.Target), prepared.LineTransforms.Select(item => item.Target));
            Assert.Equal(tokens.Length + lines.Length * 10, VisualSeenCount(prepared));
            using var changed = renderer.Render(RenderRequest.At(snapshot, scene.TimeUs) with { PreparedFrame = prepared, TransparentBackground = true });
            using var plain = renderer.Render(RenderRequest.At(snapshot with { EffectInputsFingerprint = null }, scene.TimeUs) with { TransparentBackground = true });
            Assert.False(changed.PixelMemory.Span.SequenceEqual(plain.PixelMemory.Span));
        }
        Assert.False(new FrameBindingScope(last).ContainsLine(binding, original));
    }

    [Theory]
    [InlineData("lineWrite")] [InlineData("groupWrite")] [InlineData("markWrite")]
    public async Task 元素绑定可读真实所属行但生产writer不能写整行标记或组(string mode)
    {
        var snapshot = Snapshot(); var binding = new Fx.EffectBinding.Tokens([new("first-a")]);
        string root = snapshot.LyricPresentation.FindProgram("primary")!.RootContainerId;
        var program = snapshot.LyricPresentation.FindProgram("primary")!;
        var indicator = snapshot.LyricPresentation.FindNode(program.LeadIns[0].IndicatorContainerId)!;
        string mark = ((CountdownSequenceContent)indicator.Content!).MarkNodeIds[0];
        await using var observed = await (await Factory(binding, "observe")).CreateAsync(snapshot);
        var current = await observed.PrepareAsync(10_000_000, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
        Assert.Equal(11, VisualSeenCount(current));
        await using var writing = await (await Factory(binding, mode, root, mark)).CreateAsync(snapshot);
        var failure = await Assert.ThrowsAsync<EffectFailureException>(async () => await writing.PrepareAsync(10_000_000,
            Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition)));
        Assert.Equal("TargetOutsideBinding", failure.Code); Assert.Equal("Evaluate", failure.Stage); Assert.Equal("scope-instance", failure.InstanceId.Value);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task 跟随和固定前奏的实际祖先区分在查询文字视觉及生产组writer保持一致(bool fixedParent)
    {
        var snapshot = Snapshot(fixedParent); using SkiaRenderPipeline renderer = new(); var scene = renderer.GetSceneFrame(snapshot, 10_000_000);
        var first = FrameProjection.Lines(scene, Catalog(snapshot)).Single(line => line.LineId.Value == "first");
        string indicator = snapshot.LyricPresentation.FindProgram("primary")!.LeadIns[0].IndicatorContainerId;
        var binding = new Fx.EffectBinding.Node(first.NodeId, true); var scope = new FrameBindingScope(scene);
        var marks = FrameProjection.Marks(scene, Catalog(snapshot)).Where(mark => scope.ContainsMark(binding, mark)).ToArray();
        Assert.Equal(fixedParent ? 0 : 3, marks.Length); Assert.Equal(!fixedParent, scope.ContainsGroup(binding, new(new(indicator))));
        Assert.False(new FrameBindingScope(scene).ContainsGroup(new Fx.EffectBinding.Node(first.NodeId, false), new(new(indicator))));
        var runtime = scene.Elements.Single(node => node.Id == indicator);
        var persistent = scene.Definition.Elements.Single(node => node.Id == indicator);
        Assert.Equal(fixedParent ? persistent.ParentId : first.NodeId.Value, runtime.ParentId);
        await using var source = await (await Factory(binding)).CreateAsync(snapshot);
        var prepared = await source.PrepareAsync(scene.TimeUs, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
        Assert.Equal(marks.Select(mark => mark.Target), prepared.MarkTransforms.Select(item => item.Target));
        Assert.Equal(2 + 10 + marks.Length * 100, VisualSeenCount(prepared));
        await using var groupSource = await (await Factory(binding, "groupWrite", indicator)).CreateAsync(snapshot);
        if (fixedParent)
        {
            var failure = await Assert.ThrowsAsync<EffectFailureException>(async () => await groupSource.PrepareAsync(scene.TimeUs,
                Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition)));
            Assert.Equal("TargetOutsideBinding", failure.Code); return;
        }
        var groupFrame = await groupSource.PrepareAsync(scene.TimeUs, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
        Assert.Equal(indicator, Assert.Single(groupFrame.GroupTransforms).Target.NodeId.Value);
    }
}
