using System.Collections.Immutable;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Execution;
using MakoKaraGenerator.Scripting.Projection;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class DirectGraphEffectTests
{
    [Fact]
    public async Task v2直接图缺少能力声明时提前拒绝而不误装载为CSharp入口()
    {
        var original = Definition().Contents;
        var manifest = JsonNode.Parse(original.Manifest.AsSpan())!.AsObject();
        manifest["packageVersion"] = EffectPackageProfiles.V2.PackageVersion;
        manifest["sdkVersion"] = EffectPackageProfiles.V2.SdkVersion;
        manifest["shaderProfile"] = EffectPackageProfiles.V2.ShaderProfile;
        manifest["requires"] = new JsonArray();
        manifest["lineExit"] = new JsonObject { ["durationUs"] = 0 };
        manifest["emitters"] = new JsonArray();
        var definition = EffectPackageDefinition.Read(EffectPackageContents.CreateSupported(EffectPackageFixture.Bytes(manifest),
            original.Files.Select(file => KeyValuePair.Create(file.Path, (ReadOnlyMemory<byte>)file.Content.ToArray()))));
        var compiled = await Compile(definition);
        Assert.True(compiled.Succeeded);
        Assert.Equal("PackageCapabilityMissing", Assert.Throws<EffectPackageException>(() =>
            compiled.CreateEffect(ExportId, new("v2-graph"), new EffectBinding.Project(), 0)).Code);
    }

    private static readonly string[] SingleChorusTokenIds = ["chorus-a"];
    private static readonly string[] ChorusTokenIds = ["chorus-a", "chorus-b"];
    private static readonly string[] RotatedInstanceIds = ["a", "a", "b", "b"];
    private static readonly string[] RotatedTokenIds = ["tail-a-a", "tail-a-b", "tail-a-a", "tail-a-b"];
    private const string GraphPath = "graphs/direct.graph.json";
    private const string ExportId = "direct-graph";
    private const string Fingerprint = "direct-graph-fixed-inputs";
    private static readonly Lazy<ScriptCompiler> Compiler = new(() =>
        new(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0")));

    // This fixture exercises the production package and prepared-binding protocol, not native pixels.
    // Its identity graph has no shader, image, C# file or native pass.
    private static EffectPackageDefinition Definition(string target = "tokenFamily", string anchor = "ColorOnly",
        Action<JsonObject>? changeExport = null)
    {
        JsonObject graph = new()
        {
            ["graphVersion"] = ImageGraphDefinition.CurrentVersion, ["id"] = "direct",
            ["inputs"] = new JsonArray(new JsonObject
            {
                ["id"] = "original", ["source"] = "TargetColor", ["type"] = "color",
                ["availability"] = "required", ["backgroundRequirement"] = "none", ["space"] = "canvas",
                ["capture"] = new JsonObject
                {
                    ["includeRuby"] = true, ["includeStroke"] = true, ["includeShadow"] = true,
                    ["includeDescendantGroups"] = true, ["includeOwnedOverlay"] = false, ["region"] = "fillAndStroke",
                },
            }),
            ["parameters"] = new JsonArray("peak"), ["nodes"] = new JsonArray(), ["output"] = "input:original",
            ["anchorMode"] = anchor,
            ["bounds"] = new JsonObject { ["space"] = "canvas", ["source"] = "targetRegion", ["maxExpandPx"] = 0 },
            ["composite"] = new JsonObject { ["mode"] = "sourceOver" },
        };
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal) { [GraphPath] = EffectPackageFixture.Bytes(graph) };
        JsonObject manifest = EffectPackageFixture.Manifest(files);
        manifest["files"]![0]!["role"] = "graph";
        JsonObject export = manifest["exports"]![0]!.AsObject();
        export["id"] = ExportId; export["name"] = "直接图"; export["kind"] = "graph";
        export.Remove("entryType"); export["graph"] = GraphPath;
        export["bindingKinds"] = new JsonArray("project", "branch", "lines", "tokens", "clips", "node");
        export["outputs"]![0]!["id"] = "image";
        export["outputs"]![0]!["kind"] = "ImageGraph"; export["outputs"]![0]!["target"] = target;
        changeExport?.Invoke(export);
        return EffectPackageDefinition.Read(EffectPackageContents.Create(EffectPackageFixture.Bytes(manifest),
            EffectPackageFixture.Inputs(files), EffectPackageFixture.Profile));
    }

    private sealed class RecordingGraphValidator : IPackageImageGraphValidator
    {
        public ImmutableDictionary<string, EffectPackageImageGraph>? Seen { get; private set; }
        public void Validate(ImmutableDictionary<string, EffectPackageImageGraph> graphs, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Seen = graphs;
            var graph = Assert.Single(graphs).Value.Plan;
            Assert.Empty(graph.Nodes); Assert.Empty(graph.Programs);
            Assert.Equal(GraphReference.Input("original"), graph.Definition.Output);
        }
    }

    private static Task<CompiledEffectPackage> Compile(EffectPackageDefinition definition,
        RecordingGraphValidator? validator = null, CancellationToken cancellationToken = default) =>
        CompiledEffectPackage.CompileAsync(definition, Compiler.Value, null, null,
            validator ?? new RecordingGraphValidator(), cancellationToken);

    [Fact]
    public async Task 纯图真实闭包不编译DLL且固定参数身份及资源修订进入帧实例()
    {
        var definition = Definition(); var validator = new RecordingGraphValidator();
        var compiled = await Compile(definition, validator);
        Assert.True(compiled.Succeeded); Assert.Null(compiled.ControllerArtifact);
        Assert.Same(definition.ImageGraphs, validator.Seen);
        Assert.Equal("PackageCSharpArtifactUnavailable", Assert.Throws<EffectPackageException>(() => compiled.Artifact).Code);
        var binding = new EffectBinding.Tokens([new("first-a")]); var subscription = new SourceSubscription.Project();
        var effect = compiled.CreateEffect(ExportId, new("direct-instance"), binding, 0xfedcba9876543210,
            "{\"peak\":2.25}"u8.ToArray(), subscription);
        Assert.Null(effect.Artifact); Assert.Null(effect.EntryType); Assert.Equal(definition.Contents.Revision, effect.RuntimeHash);
        Assert.Same(binding, effect.Binding); Assert.Same(subscription, effect.Subscription);
        Assert.Equal(0xfedcba9876543210UL, effect.Seed); Assert.Equal(2.25, effect.Parameters.GetNumber("peak"));
        Assert.Equal(EffectCapabilities.ImageEffects, effect.Capabilities); Assert.False(effect.RequiresAudio);
        Assert.Same(definition.GetGraph(ExportId), effect.PackageDeclaration); Assert.Same(definition, effect.PackageDefinition);
        Assert.Equal(new EffectPackageIdentity(definition.Contents.PackageId, definition.Contents.Revision, ExportId), effect.PackageIdentity);
        Assert.Same(compiled.Resources, effect.Resources);
        Assert.Equal(new ResourceIdentity(definition.Contents.PackageId, definition.Contents.Revision, ExportId),
            effect.Resources.GetGraph(ExportId).Identity);
        Assert.Equal(1.5, compiled.CreateEffect(ExportId, new("defaults"), binding, 1).Parameters.GetNumber("peak"));
        Assert.Equal("PackageParameterOutOfRange", Assert.Throws<EffectPackageException>(() => compiled.CreateEffect(ExportId,
            new("invalid-parameter"), binding, 1, "{\"peak\":9}"u8.ToArray())).Code);
        Assert.Equal("PackageExportMissing", Assert.Throws<EffectPackageException>(() => compiled.CreateEffect("missing",
            new("missing-export"), binding, 1)).Code);
    }

    [Theory]
    [InlineData("binding", "PackageBindingNotAllowed")]
    [InlineData("group", "GroupImageEffectUnsupported")]
    [InlineData("ImageOnly", "PackageGraphMappingPending")]
    [InlineData("MappedWarp", "PackageGraphMappingPending")]
    [InlineData("requires", "PackageOutputPlanPending")]
    [InlineData("dependsOn", "PackageOutputPlanPending")]
    [InlineData("sameTarget", "PackageOutputPlanPending")]
    public async Task 直接图不扩大绑定也不跳过组映射需求依赖或重复目标门禁(string change, string code)
    {
        var definition = Definition(change == "group" ? "group" : "tokenFamily",
            change is "ImageOnly" or "MappedWarp" ? change : "ColorOnly", export =>
            {
                if (change == "binding") export["bindingKinds"] = new JsonArray("tokens");
                var output = export["outputs"]![0]!;
                if (change == "requires") output["requires"] = new JsonArray("AudioFeatures");
                if (change is "dependsOn" or "sameTarget")
                {
                    var other = output.DeepClone(); other["id"] = "other";
                    if (change == "dependsOn") { other["target"] = "line"; output["dependsOn"] = new JsonArray("other"); }
                    export["outputs"]!.AsArray().Add(other);
                }
            });
        var compiled = await Compile(definition);
        var failure = Assert.Throws<EffectPackageException>(() => compiled.CreateEffect(ExportId, new("blocked"),
            new EffectBinding.Project(), 0));
        Assert.Equal(code, failure.Code); Assert.Contains(ExportId, failure.Path, StringComparison.Ordinal);
        Assert.Null(compiled.ControllerArtifact);
    }

    [Fact]
    public async Task 没有图后端门禁不能制造可用直接图且取消仍是取消()
    {
        var definition = Definition();
        var missing = await Assert.ThrowsAsync<EffectPackageException>(() =>
            CompiledEffectPackage.CompileAsync(definition, Compiler.Value));
        Assert.Equal("PackageImageGraphValidatorMissing", missing.Code);
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Compile(definition, cancellationToken: cancellation.Token));
        var compiled = await Compile(definition);
        Assert.ThrowsAny<OperationCanceledException>(() => compiled.CreateEffect(ExportId, new("cancelled"),
            new EffectBinding.Project(), 0, cancellationToken: cancellation.Token));
    }

    [Theory]
    [InlineData("foreign", "InvalidGraphResource")]
    [InlineData("empty", "InvalidGraphResource")]
    [InlineData("extraParameter", "PackageParameterSetMismatch")]
    public async Task 无DLL初始化拒绝篡改固定目录及额外参数并保留真实实例修订(string change, string code)
    {
        var compiled = await Compile(Definition());
        var original = compiled.CreateEffect(ExportId, new("tampered-direct"), new EffectBinding.Lines([new("first")]), 1);
        var tampered = change switch
        {
            "foreign" => original with { Resources = new("other-package", "sha256:" + new string('b', 64), [], [ExportId], []) },
            "empty" => original with { Resources = ResourceCatalog.Empty },
            _ => original with { Parameters = new([new("peak", new ParameterValue.Number(1.5)),
                new("extra", new ParameterValue.Text("not declared"))]) },
        };
        var factory = new PureFrameSourceFactory([tampered], Fingerprint, static () => new RuntimeGeometry());
        var failure = await Assert.ThrowsAsync<EffectFailureException>(() => factory.CreateAsync(Snapshot()).AsTask());
        Assert.Equal(code, failure.Code); Assert.Equal("ValidateInputs", failure.Stage); Assert.Equal(0, failure.TimeUs);
        Assert.Equal(original.Id, failure.InstanceId); Assert.Equal(original.RuntimeHash, failure.ArtifactHash);
        Assert.Equal(original.PackageIdentity, failure.PackageIdentity); Assert.Null(tampered.Artifact);
    }

    private static GeometrySample Geometry() => new()
    {
        AdvanceBounds = new(0, 0, 20, 20), InkBounds = new(1, 1, 18, 18), Baseline = new(0, 15),
        BaseWorldMatrix = Affine2.Identity, CanTransformIndependently = true,
    };

    private sealed class RuntimeGeometry : ILineGeometryProvider
    {
        public LineRenderGeometry? GetLineGeometry(ProjectSnapshot snapshot, string lineId) => null;
        public SceneFrame GetSceneFrame(ProjectSnapshot snapshot, long timeUs, string? editingLineId = null)
        {
            var scene = LyricSceneSampler.SampleFrame(snapshot, timeUs);
            var catalog = SceneProjection.Create(snapshot, ContentProjection.Create(snapshot));
            var tokens = FrameProjection.Tokens(scene); var lines = FrameProjection.Lines(scene, catalog);
            List<SceneTextMarkSample> marks = [];
            foreach (var cue in scene.LeadIns)
            {
                var sequence = (CountdownSequenceContent)snapshot.LyricPresentation.FindNode(cue.ContainerId)!.Content!;
                foreach (string id in sequence.MarkNodeIds)
                {
                    var content = (TextMarkContent)snapshot.LyricPresentation.FindNode(id)!.Content!;
                    marks.Add(new()
                    {
                        NodeId = id, ContainerId = cue.ContainerId, CueId = cue.CueId, Text = content.Text,
                        Appearance = sequence.Appearance.Resolve(content.AppearanceOverride),
                        Geometry = new() { ElementId = id, ContainerId = cue.ContainerId, BindingId = cue.TargetBindingId,
                            LineId = id, Placement = ScenePlacement.Default, WorldMatrix = SceneMatrix.Identity },
                    });
                }
            }
            return scene with
            {
                Elements = [.. scene.Definition.Elements.Select(node => new SceneElementFrame
                {
                    Id = node.Id,
                    ParentId = scene.LeadIns.FirstOrDefault(cue => cue.ContainerId == node.Id)?.TargetElementId
                        ?? scene.Lines.FirstOrDefault(line => line.ElementId == node.Id)?.ParentId ?? node.ParentId,
                    Kind = node.Kind, SourceBinding = node.SourceBinding,
                })],
                TokenGeometries = [.. tokens.Select(token => new TokenGeometrySample
                {
                    BranchId = token.BranchId, LineId = token.LineId, WithRuby = token.WithRuby, BaseOnly = token.BaseOnly,
                    BaseGeometry = Geometry(), FamilyGeometry = Geometry(), ProgressPath = new(0, 20, 15, 10),
                })],
                LineGeometries = [.. lines.Select(line => new LineGeometrySample
                {
                    BranchId = line.BranchId, Target = new(line.LineId, line.BindingId), BaseGeometry = Geometry(),
                    ProgressPath = new(0, 20, 15, 10),
                })],
                TextMarks = [.. marks],
                MarkGeometries = [.. marks.Select(mark => new TextMarkGeometrySample(
                    new(new(mark.NodeId), new(mark.CueId), new(mark.Geometry.BindingId!)), Geometry()))],
            };
        }
    }

    private static ProjectSnapshot Snapshot(bool slots = false) =>
        ProjectSnapshot.FromProject(LeadInFixture.Create(slots, true)) with { EffectInputsFingerprint = Fingerprint };

    [Theory]
    [InlineData(ProjectEffectBindingKind.Project)] [InlineData(ProjectEffectBindingKind.Branch)]
    [InlineData(ProjectEffectBindingKind.Lines)] [InlineData(ProjectEffectBindingKind.Tokens)]
    [InlineData(ProjectEffectBindingKind.Clips)] [InlineData(ProjectEffectBindingKind.Node)]
    public async Task 六类直接图绑定在准备帧中保留当前源元素装载身份(ProjectEffectBindingKind kind)
    {
        var snapshot = Snapshot(); var scene = new RuntimeGeometry().GetSceneFrame(snapshot, 40_500_000);
        var line = scene.Lines.Single(item => item.Frame.Line!.Id == "chorus");
        EffectBinding binding = kind switch
        {
            ProjectEffectBindingKind.Project => new EffectBinding.Project(),
            ProjectEffectBindingKind.Branch => new EffectBinding.Branch(new("primary")),
            ProjectEffectBindingKind.Lines => new EffectBinding.Lines([new("chorus")]),
            ProjectEffectBindingKind.Tokens => new EffectBinding.Tokens([new("chorus-a")]),
            ProjectEffectBindingKind.Clips => new EffectBinding.Clips([new("chorus-clip")]),
            _ => new EffectBinding.Node(new(line.ElementId), false),
        };
        var compiled = await Compile(Definition()); var effect = compiled.CreateEffect(ExportId, new("graph-instance"), binding, 7,
            "{\"peak\":2.25}"u8.ToArray());
        var factory = new PureFrameSourceFactory([effect], Fingerprint, static () => new RuntimeGeometry());
        await using var session = await factory.CreateAsync(snapshot);
        var prepared = await session.PrepareAsync(scene.TimeUs, OutputMode.Opaque, RenderTarget.FromComposition(snapshot.Composition));
        Assert.Equal(kind == ProjectEffectBindingKind.Tokens ? SingleChorusTokenIds : ChorusTokenIds,
            prepared.ImageEffects.Select(item => Assert.IsType<ImageEffectTarget.Token>(item.Binding.Target).Target.TokenId.Value).ToArray());
        Assert.All(prepared.ImageEffects, item =>
        {
            Assert.Equal(effect.Id, item.InstanceId); Assert.Equal(effect.RuntimeHash, item.ArtifactHash);
            Assert.Equal(effect.PackageIdentity, item.PackageIdentity);
            Assert.Equal(line.BindingId, Assert.IsType<ImageEffectTarget.Token>(item.Binding.Target).Target.BindingId.Value);
            Assert.Equal(effect.Resources.GetGraph(ExportId), item.Binding.Graph);
            Assert.Same(effect.Parameters, item.Binding.Parameters); Assert.Equal(2.25, item.Binding.Parameters.GetNumber("peak"));
        });
        Assert.Empty(prepared.TokenTransforms); Assert.Empty(prepared.TokenStyles); Assert.Empty(prepared.Diagnostics);
    }

    [Fact]
    public async Task 直接行图和真实前奏标记分别绑定声明目标而元素范围不获得行图写权()
    {
        var snapshot = Snapshot(); var target = RenderTarget.FromComposition(snapshot.Composition);
        var linePackage = await Compile(Definition("line"));
        var lineEffect = linePackage.CreateEffect(ExportId, new("line-image"), new EffectBinding.Lines([new("first")]), 1);
        await using (var session = await new PureFrameSourceFactory([lineEffect], Fingerprint,
            static () => new RuntimeGeometry()).CreateAsync(snapshot))
        {
            var prepared = await session.PrepareAsync(12_500_000, OutputMode.Transparent, target);
            var image = Assert.Single(prepared.ImageEffects);
            Assert.Equal("first", Assert.IsType<ImageEffectTarget.Line>(image.Binding.Target).Target.LineId.Value);
            Assert.Equal(prepared.Scene.Lines.Single(line => line.Frame.Line!.Id == "first").BindingId,
                Assert.IsType<ImageEffectTarget.Line>(image.Binding.Target).Target.BindingId.Value);
        }
        var tokenEffect = linePackage.CreateEffect(ExportId, new("invalid-line-image"), new EffectBinding.Tokens([new("first-a")]), 1);
        await using (var session = await new PureFrameSourceFactory([tokenEffect], Fingerprint,
            static () => new RuntimeGeometry()).CreateAsync(snapshot))
        {
            var failure = await Assert.ThrowsAsync<EffectFailureException>(() => session.PrepareAsync(12_500_000, OutputMode.Opaque, target).AsTask());
            Assert.Equal("TargetOutsideBinding", failure.Code); Assert.Equal(tokenEffect.Id, failure.InstanceId);
            Assert.Equal(tokenEffect.PackageIdentity, failure.PackageIdentity);
        }
        var markPackage = await Compile(Definition("textMark"));
        var markEffect = markPackage.CreateEffect(ExportId, new("mark-image"), new EffectBinding.Lines([new("first")]), 1);
        await using (var session = await new PureFrameSourceFactory([markEffect], Fingerprint,
            static () => new RuntimeGeometry()).CreateAsync(snapshot))
        {
            var prepared = await session.PrepareAsync(10_000_000, OutputMode.Opaque, target);
            Assert.Equal(3, prepared.ImageEffects.Length);
            Assert.All(prepared.ImageEffects, item => Assert.Equal("cue-first", Assert.IsType<ImageEffectTarget.Mark>(item.Binding.Target).Target.CueId.Value));
            Assert.Equal(prepared.FinalMarkGeometry.Select(mark => mark.Target),
                prepared.ImageEffects.Select(item => Assert.IsType<ImageEffectTarget.Mark>(item.Binding.Target).Target));
        }
    }

    [Fact]
    public async Task 槽位轮换的直接图仅消费当前装载且两实例保持有序独立参数()
    {
        var snapshot = Snapshot(true); var geometry = new RuntimeGeometry();
        var first = geometry.GetSceneFrame(snapshot, 12_500_000).Lines.Single(line => line.Frame.Line!.Id == "first");
        var compiled = await Compile(Definition()); var binding = new EffectBinding.Node(new(first.ParentId), true);
        var a = compiled.CreateEffect(ExportId, new("a"), binding, 1, "{\"peak\":2}"u8.ToArray());
        var b = compiled.CreateEffect(ExportId, new("b"), binding, 2, "{\"peak\":3}"u8.ToArray());
        await using var session = await new TimelineFrameSourceFactory([a, b], Fingerprint,
            static () => new RuntimeGeometry(), ReplayReason.Initial).CreateAsync(snapshot);
        var target = RenderTarget.FromComposition(snapshot.Composition);
        var before = await session.PrepareAsync(12_500_000, OutputMode.Opaque, target);
        var after = await session.PrepareAsync(47_000_000, OutputMode.Transparent, target);
        Assert.Equal(RotatedInstanceIds, after.ImageEffects.Select(item => item.InstanceId.Value).ToArray());
        Assert.Equal(RotatedTokenIds,
            after.ImageEffects.Select(item => Assert.IsType<ImageEffectTarget.Token>(item.Binding.Target).Target.TokenId.Value).ToArray());
        Assert.Equal(new double[] { 2, 2, 3, 3 }, after.ImageEffects.Select(item => item.Binding.Parameters.GetNumber("peak")).ToArray());
        Assert.All(before.ImageEffects, item => Assert.Equal(first.BindingId,
            Assert.IsType<ImageEffectTarget.Token>(item.Binding.Target).Target.BindingId.Value));
        Assert.DoesNotContain(after.ImageEffects, item => Assert.IsType<ImageEffectTarget.Token>(item.Binding.Target).Target.BindingId.Value == first.BindingId);
        var replay = await session.PrepareAsync(12_500_000, OutputMode.Opaque, target);
        Assert.Equal(before.ImageEffects.ToArray(), replay.ImageEffects.ToArray());
    }
}
