using System.Text.Json;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Execution;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class GroupImageEffectHostTests
{
    private const string Fingerprint = "group-image-host";
    private static readonly string[] HiddenInstanceIds = ["first", "last"];
    private static readonly double[] HiddenAmounts = [.2, .8];
    private static readonly Lazy<Task<CompiledEffectPackage>> Package = new(() => Compile());
    private const string Source = """
        using MakoKaraGenerator.Scripting;
        using MakoKaraGenerator.Scripting.V2;
        public sealed record GroupData(string Node, double Amount) : PreparedEffect;
        public sealed class PackEffect : IPureKaraokeEffect
        {
            public PreparedEffect Declare(DeclareContext c) => new GroupData(c.Parameters.GetText("node"), c.Parameters.GetNumber("amount"));
            public void Evaluate(in TextFrame f, PreparedEffect p, TextOutput o)
            {
                var d = (GroupData)p;
                var target = new TextTarget.Group(new(new(d.Node)));
                var graph = f.Resources.GetGraph("tint");
                GraphBindingValue Value(double amount) => new(graph, new ParameterValues(new[] {
                    new System.Collections.Generic.KeyValuePair<string, ParameterValue>("amount", new ParameterValue.Number(amount))
                }), GraphUniformValues.Empty, GraphTransformValues.Empty);
                o.BindGraph(target, Value(.25));
                o.BindGraph(target, Value(d.Amount));
            }
        }
        """;
    private static async Task<CompiledEffectPackage> Compile(string controllerTarget = "group", string graphTarget = "group", bool graphAllowsNode = true)
    {
        var original = MixedEffectPackageFixture.Contents(manifest =>
        {
            manifest["parameters"]!.AsArray().Add(new JsonObject { ["id"] = "node", ["type"] = "text", ["label"] = "目标组", ["default"] = "" });
            var controller = manifest["exports"]!.AsArray().Single(item => item!["kind"]!.GetValue<string>() == "controller")!;
            controller["parameters"]!.AsArray().Add("node"); controller["outputs"]![0]!["target"] = controllerTarget;
            var graph = manifest["exports"]!.AsArray().Single(item => item!["kind"]!.GetValue<string>() == "graph")!;
            graph["outputs"]![0]!["target"] = graphTarget;
            if (!graphAllowsNode) graph["bindingKinds"] = new JsonArray("project");
        }, controllerSource: Source);
        var manifest = JsonNode.Parse(original.Manifest.AsSpan())!.AsObject();
        manifest["packageVersion"] = 3; manifest["sdkVersion"] = "2.0.0"; manifest["shaderProfile"] = "skia-ganesh-3.119.4-v2";
        manifest["requires"] = new JsonArray("image.graph", "image.graph.group");
        manifest["lineExit"] = new JsonObject { ["durationUs"] = 0 }; manifest["emitters"] = new JsonArray();
        var exports = manifest["exports"]!.AsArray();
        for (int index = exports.Count - 1; index >= 0; index--)
            if (exports[index]!["kind"]!.GetValue<string>() == "material") exports.RemoveAt(index);
        foreach (var export in exports)
        {
            var output = export!["outputs"]![0]!;
            output["requires"] = output["target"]!.GetValue<string>() == "group" ? new JsonArray("image.graph.group") : new JsonArray();
        }
        var contents = EffectPackageContents.CreateSupported(EffectPackageFixture.Bytes(manifest), original.Files.Select(file =>
            KeyValuePair.Create(file.Path, (ReadOnlyMemory<byte>)file.Content.ToArray())));
        var package = await CompiledEffectPackage.CompileAsync(EffectPackageDefinition.Read(contents),
            new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0")), new PackageImageValidator(),
            new PackageShaderValidator(), new PackageImageGraphValidator(), new PackageMaterialValidator());
        Assert.True(package.Succeeded, string.Join('\n', package.Artifact.Diagnostics)); return package;
    }
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
    private static string Root(ProjectSnapshot snapshot) => snapshot.LyricPresentation.FindProgram("primary")!.RootContainerId;
    private static CompiledFrameEffect Effect(CompiledEffectPackage package, string id, Fx.EffectBinding binding, string node, double amount = .5)
        => package.CreateEffect("controller", new(id), binding, 17, JsonSerializer.SerializeToUtf8Bytes(new { node, amount }));
    private static PureFrameSourceFactory Factory(params CompiledFrameEffect[] effects)
        => new([.. effects], Fingerprint, static () => new SkiaRenderPipeline());

    [Theory]
    [InlineData("project", true)] [InlineData("node", true)] [InlineData("branch", false)]
    [InlineData("lines", false)] [InlineData("tokens", false)] [InlineData("clips", false)]
    public async Task 真实包六类绑定只有工程和显式节点可写组(string kind, bool allowed)
    {
        var snapshot = Snapshot(); string root = Root(snapshot);
        Fx.EffectBinding binding = kind switch
        {
            "project" => new Fx.EffectBinding.Project(), "node" => new Fx.EffectBinding.Node(new(root), false),
            "branch" => new Fx.EffectBinding.Branch(new("primary")), "lines" => new Fx.EffectBinding.Lines([new("first")]),
            "tokens" => new Fx.EffectBinding.Tokens([new("first-a")]), _ => new Fx.EffectBinding.Clips([new("chorus-clip")]),
        };
        var package = await Package.Value;
        if (!allowed)
        {
            var failure = Assert.Throws<EffectPackageException>(() => Effect(package, "group", binding, root));
            Assert.Equal("PackageBindingNotAllowed", failure.Code); return;
        }
        var effect = Effect(package, "group", binding, root); Assert.Equal(Fx.EffectCapabilities.ImageEffects, effect.Capabilities);
        await using var source = await Factory(effect).CreateAsync(snapshot);
        var frame = await source.PrepareAsync(10_000_000, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
        var output = Assert.Single(frame.ImageEffects); Assert.Equal(new Fx.ImageEffectTarget.Group(new(new(root))), output.Binding.Target);
        Assert.Equal(.5, output.Binding.Parameters.GetNumber("amount")); Assert.Equal(effect.Artifact!.ArtifactHash, output.ArtifactHash);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task 显式节点后代开关控制真实孩子组写权(bool descendants)
    {
        var snapshot = Snapshot(); var program = snapshot.LyricPresentation.FindProgram("primary")!;
        await using var source = await Factory(Effect(await Package.Value, "child", new Fx.EffectBinding.Node(new(Root(snapshot)), descendants),
            program.DefaultContainerId)).CreateAsync(snapshot);
        if (!descendants)
        {
            var failure = await Assert.ThrowsAsync<EffectFailureException>(async () => await source.PrepareAsync(10_000_000,
                Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition)));
            Assert.Equal("TargetOutsideBinding", failure.Code); return;
        }
        var frame = await source.PrepareAsync(10_000_000, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
        Assert.Equal(new Fx.ImageEffectTarget.Group(new(new(program.DefaultContainerId))), Assert.Single(frame.ImageEffects).Binding.Target);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task 组图后代权限按真实运行附着关系而非源树推断(bool fixedParent)
    {
        var snapshot = Snapshot(fixedParent); using var renderer = new SkiaRenderPipeline();
        var scene = renderer.GetSceneFrame(snapshot, 10_000_000);
        string text = scene.Lines.Single(item => item.Frame.Line!.Id == "first").ElementId;
        string indicator = snapshot.LyricPresentation.FindProgram("primary")!.LeadIns[0].IndicatorContainerId;
        await using var source = await Factory(Effect(await Package.Value, "attached", new Fx.EffectBinding.Node(new(text), true), indicator)).CreateAsync(snapshot);
        if (fixedParent)
        {
            var failure = await Assert.ThrowsAsync<EffectFailureException>(async () => await source.PrepareAsync(scene.TimeUs,
                Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition)));
            Assert.Equal("TargetOutsideBinding", failure.Code); return;
        }
        Assert.Single((await source.PrepareAsync(scene.TimeUs, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition))).ImageEffects);
    }

    [Theory]
    [InlineData("line", "group", true, "UndeclaredPackageOutput")]
    [InlineData("group", "line", true, "UndeclaredPackageOutput")]
    [InlineData("group", "group", false, "PackageBindingNotAllowed")]
    public async Task 控制器及图导出分别声明组目标和绑定范围(string controllerTarget, string graphTarget, bool graphAllowsNode, string code)
    {
        var snapshot = Snapshot(); var package = await Compile(controllerTarget, graphTarget, graphAllowsNode);
        await using var source = await Factory(Effect(package, "declaration", new Fx.EffectBinding.Node(new(Root(snapshot)), false), Root(snapshot))).CreateAsync(snapshot);
        var failure = await Assert.ThrowsAsync<EffectFailureException>(async () => await source.PrepareAsync(10_000_000,
            Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition)));
        Assert.Equal(code, failure.Code); Assert.Equal("ComposeOutputs", failure.Stage); Assert.Equal("declaration", failure.InstanceId.Value);
    }

    [Fact]
    public async Task 隐藏真实组保持最后完整参数跨实例顺序及重放副本()
    {
        var snapshot = Snapshot(); var package = await Package.Value; var target = RenderTarget.FromComposition(snapshot.Composition);
        string hidden = snapshot.LyricPresentation.FindProgram("primary")!.LeadIns[0].IndicatorContainerId;
        var binding = new Fx.EffectBinding.Project();
        await using var source = await Factory(Effect(package, "first", binding, hidden, .2), Effect(package, "last", binding, hidden, .8)).CreateAsync(snapshot);
        var frame = await source.PrepareAsync(20_000_000, Fx.OutputMode.Transparent, target);
        Assert.Empty(frame.FinalGroupGeometry.Single(item => item.Target.NodeId.Value == hidden).WorldInkCorners);
        Assert.Equal(HiddenInstanceIds, frame.ImageEffects.Select(item => item.InstanceId.Value));
        Assert.Equal(HiddenAmounts, frame.ImageEffects.Select(item => item.Binding.Parameters.GetNumber("amount")));
        Assert.Same(frame, frame.WithImageEffects(frame.ImageEffects));
        var copy = frame.WithVisualOutput([], [new(new("info"), "artifact", "Evaluate", frame.TimeUs, new(Fx.DiagnosticSeverity.Info, "info", "副本"))]);
        Assert.Equal(frame.ImageEffects, copy.ImageEffects); Assert.Equal(frame.FinalGroupGeometry, copy.FinalGroupGeometry);
        Assert.Same(frame.HierarchyScene, copy.HierarchyScene);
        await source.PrepareAsync(21_000_000, Fx.OutputMode.Transparent, target);
        var replay = await source.PrepareAsync(frame.TimeUs, Fx.OutputMode.Transparent, target);
        Assert.Equal(frame.ImageEffects.Select(item => (item.InstanceId, item.ArtifactHash, item.Binding.Target, item.Binding.Graph.Identity,
                Amount: item.Binding.Parameters.GetNumber("amount"))),
            replay.ImageEffects.Select(item => (item.InstanceId, item.ArtifactHash, item.Binding.Target, item.Binding.Graph.Identity,
                Amount: item.Binding.Parameters.GetNumber("amount"))));
    }

    [Fact]
    public void 准备帧严格拒绝旧节点文本角色和同实例重复组绑定()
    {
        var snapshot = Snapshot() with { EffectInputsFingerprint = null }; using var renderer = new SkiaRenderPipeline();
        var scene = renderer.GetSceneFrame(snapshot, 10_000_000);
        var frame = new PreparedRenderFrame(snapshot, scene.TimeUs, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition), scene, []);
        var resources = new Fx.ResourceCatalog("group", "sha256:" + new string('a', 64), [], ["graph"], []);
        PreparedImageEffectBinding Output(string node) => new(new("instance"), "artifact",
            new(new Fx.ImageEffectTarget.Group(new(new(node))), resources.GetGraph("graph"), Fx.ParameterValues.Empty));
        foreach (string node in new[] { "missing", scene.Lines[0].ElementId, scene.TextMarks[0].NodeId })
            Assert.Equal("InvalidPreparedImageEffectTarget", Assert.Throws<PreparedFrameException>(() => frame.WithImageEffects([Output(node)])).Code);
        var output = Output(Root(snapshot));
        Assert.Equal("InvalidPreparedImageEffect", Assert.Throws<PreparedFrameException>(() => frame.WithImageEffects([output, output])).Code);
        Assert.Equal(2, frame.WithImageEffects([output, output with { InstanceId = new("second") }]).ImageEffects.Length);
    }
}
