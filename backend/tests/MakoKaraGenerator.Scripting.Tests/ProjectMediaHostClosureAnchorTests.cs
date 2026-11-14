using System.Collections;
using System.Collections.Immutable;
using System.Reflection;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Rendering.Abstractions;
using MakoKaraGenerator.Scripting.Execution;
using MakoKaraGenerator.Scripting.Projection;
using MakoKaraGenerator.Scripting.V2;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class ProjectMediaHostClosureAnchorTests
{
    private const long TimeUs = 40_500_000;

    [Theory]
    [InlineData("token", false, false)] [InlineData("line", false, false)]
    [InlineData("token", true, false)] [InlineData("line", true, false)]
    [InlineData("token", false, true)] [InlineData("line", false, true)]
    [InlineData("token", true, true)] [InlineData("line", true, true)]
    public void 第二媒体writer的祖先组仿射强制Exact而无关组仍连续(string kind, bool alias, bool unrelated)
    {
        var snapshot = ProjectSnapshot.FromProject(LeadInFixture.Create(true, true));
        var scene = Geometry(snapshot, alias);
        var content = ContentProjection.Create(snapshot);
        var catalog = SceneProjection.Create(snapshot, content);
        var project = new HostProjectInputs(content, catalog, new(640, 360));
        var line = FrameProjection.Lines(scene, catalog).Single(item => item.LineId.Value == "chorus");
        var token = FrameProjection.Tokens(scene).Single(item => item.TokenId.Value == "chorus-a");
        ParticleSource source = kind == "token" ? new ParticleSource.Token(token.WithRuby) : new ParticleSource.Line(line.Target);
        string groupId = unrelated ? snapshot.LyricPresentation.FindProgram("translation")!.RootContainerId : "center";
        var group = new GroupTarget(new(groupId));
        var binding = new EffectBinding.Node(new(groupId), false);
        var scope = new FrameBindingScope(scene);
        Assert.True(scope.ContainsGroup(binding, group));
        Assert.False(scope.ContainsToken(binding, token));
        Assert.False(scope.ContainsLine(binding, line));

        // Prove the dependency against the production affine projector, including
        // a runtime group alias. Node(false) writes the group and moves descendants.
        var moved = SceneAffineProjection.Apply(scene, [], [new(group, Affine2.Translation(31, -7))]);
        var before = scene.TokenGeometries.Single(item => item.WithRuby == token.WithRuby).BaseGeometry;
        var after = moved.TokenGeometries.Single(item => item.WithRuby == token.WithRuby).BaseGeometry;
        var point = before.MapPoint(before.AdvanceBounds.Center);
        var expected = unrelated ? point : new Point2(point.X + 31, point.Y - 7);
        Assert.Equal(expected, after.MapPoint(after.AdvanceBounds.Center));

        var owner = Active("requester", new EffectBinding.Project(), project, line, null);
        var writer = Active("writer", binding, project, line, MediaProjection());
        using var host = new FrameEffectHost(snapshot, project, new EmptyGeometry(), [], [], VisualOutputBudget.Default);
        var instances = (IList)typeof(FrameEffectHost).GetField("_instances", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
        instances.Add(owner); instances.Add(writer);
        typeof(FrameEffectHost).GetField("_v2AnchorBreakpoints", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(host, ImmutableArray.Create(0L));
        var result = ((bool Continuous, long Start, long End))typeof(FrameEffectHost)
            .GetMethod("V2AnchorRegion", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(host, [owner, source, TimeUs, scene, CancellationToken.None])!;
        Assert.Equal(unrelated, result.Continuous);
        if (!unrelated) { Assert.Equal(TimeUs, result.Start); Assert.Equal(TimeUs, result.End); }
    }

    [Fact]
    public void 无媒体祖先组连续策略不会被全局降为Exact()
    {
        var snapshot = ProjectSnapshot.FromProject(LeadInFixture.Create(true, true));
        var scene = Geometry(snapshot, false);
        var content = ContentProjection.Create(snapshot); var catalog = SceneProjection.Create(snapshot, content);
        var project = new HostProjectInputs(content, catalog, new(640, 360));
        var line = FrameProjection.Lines(scene, catalog).Single(item => item.LineId.Value == "chorus");
        var owner = Active("requester", new EffectBinding.Project(), project, line, null);
        var writer = Active("writer", new EffectBinding.Node(new("center"), false), project, line, null);
        using var host = new FrameEffectHost(snapshot, project, new EmptyGeometry(), [], [], VisualOutputBudget.Default);
        var instances = (IList)typeof(FrameEffectHost).GetField("_instances", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
        instances.Add(owner); instances.Add(writer);
        typeof(FrameEffectHost).GetField("_v2AnchorBreakpoints", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(host, ImmutableArray.Create(0L));
        var result = ((bool Continuous, long Start, long End))typeof(FrameEffectHost)
            .GetMethod("V2AnchorRegion", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(host, [owner, new ParticleSource.Line(line.Target), TimeUs, scene, CancellationToken.None])!;
        Assert.True(result.Continuous);
    }

    [Theory]
    [InlineData("token")] [InlineData("line")]
    public void 第二媒体writer直接写入范围继续强制Exact(string kind)
    {
        var snapshot = ProjectSnapshot.FromProject(LeadInFixture.Create(true, true)); var scene = Geometry(snapshot, false);
        var content = ContentProjection.Create(snapshot); var catalog = SceneProjection.Create(snapshot, content);
        var project = new HostProjectInputs(content, catalog, new(640, 360));
        var line = FrameProjection.Lines(scene, catalog).Single(item => item.LineId.Value == "chorus");
        var token = FrameProjection.Tokens(scene).Single(item => item.TokenId.Value == "chorus-a");
        ParticleSource source = kind == "token" ? new ParticleSource.Token(token.WithRuby) : new ParticleSource.Line(line.Target);
        var owner = Active("requester", new EffectBinding.Project(), project, line, null);
        var writer = Active("writer", new EffectBinding.Lines([line.LineId]), project, line, MediaProjection(), kind == "token" ? "tokenFamily" : "line");
        using var host = new FrameEffectHost(snapshot, project, new EmptyGeometry(), [], [], VisualOutputBudget.Default);
        var instances = (IList)typeof(FrameEffectHost).GetField("_instances", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
        instances.Add(owner); instances.Add(writer);
        typeof(FrameEffectHost).GetField("_v2AnchorBreakpoints", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(host, ImmutableArray.Create(0L));
        var result = ((bool Continuous, long Start, long End))typeof(FrameEffectHost)
            .GetMethod("V2AnchorRegion", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(host, [owner, source, TimeUs, scene, CancellationToken.None])!;
        Assert.False(result.Continuous); Assert.Equal(TimeUs, result.Start); Assert.Equal(TimeUs, result.End);
    }

    private static ProjectMediaFeatureProjection MediaProjection()
    {
        var contents = ProjectMediaProjectionFixture.Package();
        var plan = ProjectMediaDependencyResolver.Resolve(ProjectMediaProjectionFixture.Project(contents));
        var source = ProjectMediaProjectionFixture.Source();
        var input = MediaFeatureInput.CreatePending(ProjectMediaProjectionFixture.Context(plan, source.Profile), source, new(ProjectMediaProjectionFixture.Hash('a')));
        var binding = new ProjectMediaFeatureBinding(new ProjectMediaProjectionFixture.Sampler(input, plan));
        var catalog = EffectPackageMediaQueries.Read(contents);
        // Metadata owner only: this test never admits/executes the pending package.
        return ProjectMediaFeatureProjection.ResolveInstance(binding, new("owner-0"), catalog, "scale",
            ProjectMediaProjectionFixture.Parameters(catalog), catalog.ResolveParameters("scale"));
    }

    private static object Active(string id, EffectBinding binding, HostProjectInputs project, FrameLine line, ProjectMediaFeatureProjection? projection, string target = "group")
    {
        var original = EffectPackageFixture.GroupTransform();
        var manifest = System.Text.Json.Nodes.JsonNode.Parse(original.Manifest.AsSpan())!.AsObject();
        manifest["packageVersion"] = EffectPackageProfiles.V2.PackageVersion;
        manifest["sdkVersion"] = EffectPackageProfiles.V2.SdkVersion;
        manifest["shaderProfile"] = EffectPackageProfiles.V2.ShaderProfile;
        string capability = target == "group" ? "group.affine" : target == "line" ? "line.affine" : "token.affine";
        manifest["requires"] = new System.Text.Json.Nodes.JsonArray(capability);
        manifest["exports"]![0]!["outputs"]![0]!["target"] = target;
        manifest["exports"]![0]!["outputs"]![0]!["requires"] = new System.Text.Json.Nodes.JsonArray(capability);
        manifest["lineExit"] = new System.Text.Json.Nodes.JsonObject { ["durationUs"] = 0 };
        manifest["emitters"] = new System.Text.Json.Nodes.JsonArray();
        var package = EffectPackageDefinition.Read(EffectPackageContents.CreateSupported(EffectPackageFixture.Bytes(manifest),
            original.Files.Select(file => KeyValuePair.Create(file.Path, (ReadOnlyMemory<byte>)file.Content.ToArray()))));
        DeclareContext source = new(project.Content, project.Scene, ParameterValues.Empty, AudioFeatures.NotAnalyzed, ResourceCatalog.Empty,
            42, TimelineIndex.Empty, project.CanvasSize, new(id), new SourceIndexes(TimelineIndex.Empty, new Indexes(line)), projection?.Catalog ?? MediaQueryCatalog.Empty);
        var prepared = PreparedEffect.Empty with { GeometrySampling = new(GeometrySamplingMode.Continuous120Hz, [new(0, 60_000_000)], []) };
        var definition = new CompiledFrameEffect(new(id), null, null, ParameterValues.Empty, binding, EffectCapabilities.Transforms)
            { PackageDefinition = package, PackageDeclaration = package.GetExport("group-transform") };
        var type = typeof(FrameEffectHost);
        object pure = New(type.GetNestedType("PureEffectRuntime", BindingFlags.NonPublic)!, new NoOp(), source, prepared, null, null, null, projection);
        return New(type.GetNestedType("ActiveEffect", BindingFlags.NonPublic)!, definition, null,
            new DeterministicRandom(42, new(id)), null, null, pure, null);
    }

    private static SceneFrame Geometry(ProjectSnapshot snapshot, bool alias)
    {
        var scene = LyricSceneSampler.SampleFrame(snapshot, TimeUs);
        var catalog = SceneProjection.Create(snapshot, ContentProjection.Create(snapshot));
        var tokens = FrameProjection.Tokens(scene); var lines = FrameProjection.Lines(scene, catalog);
        scene = scene with
        {
            Elements = [.. scene.Definition.Elements.Select(node => new SceneElementFrame
            { Id = node.Id, ParentId = scene.Lines.FirstOrDefault(line => line.ElementId == node.Id)?.ParentId ?? node.ParentId,
                Kind = node.Kind, SourceBinding = node.SourceBinding })],
            Lines = [.. scene.Lines.Select(line => line with { Geometry = new()
            { ElementId = line.ElementId, ContainerId = line.ContainerId, BindingId = line.BindingId, LineId = line.Frame.Line!.Id,
                Placement = ScenePlacement.Default, WorldMatrix = SceneMatrix.Identity } })],
            TokenGeometries = [.. tokens.Select(token => new TokenGeometrySample { BranchId = token.BranchId, LineId = token.LineId,
                WithRuby = token.WithRuby, BaseOnly = token.BaseOnly, BaseGeometry = Glyph(), FamilyGeometry = Glyph() })],
            LineGeometries = [.. lines.Select(line => new LineGeometrySample { BranchId = line.BranchId, Target = line.Target,
                BaseGeometry = Glyph(), ProgressPath = new(0, 20, 15, 10) })],
        };
        if (alias)
        {
            var original = scene.Elements.Single(node => node.Id == "center");
            scene = scene with
            {
                Elements = [.. scene.Elements.Select(node => node.ParentId == "center" ? node with { ParentId = "center/runtime" } : node),
                    original with { Id = "center/runtime", SourceNodeId = "center" }],
                Lines = [.. scene.Lines.Select(line => line.ParentId == "center" ? line with { ParentNodeId = "center/runtime" } : line)],
            };
        }
        return scene with { GroupGeometries = [.. scene.Elements.Where(node => node.Kind == SceneElementKind.Group && node.SourceNodeId is null)
            .Select(node => new GroupGeometrySample { Target = new(new(node.Id)), ReferenceBounds = new(0, 0, 640, 360),
                BaseWorldMatrix = Affine2.Identity, WorldPivot = default })] };
    }

    private static GeometrySample Glyph() => new() { AdvanceBounds = new(0, 0, 20, 20), InkBounds = new(1, 1, 18, 18),
        Baseline = new(0, 15), BaseWorldMatrix = Affine2.Identity, CanTransformIndependently = true };
    private static object New(Type type, params object?[] args) => type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
        .Single(ctor => ctor.GetParameters().Length == args.Length).Invoke(args);
    private sealed class NoOp : IPureKaraokeEffect
    {
        public PreparedEffect Declare(DeclareContext context) => PreparedEffect.Empty;
        public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output) { }
    }
    private sealed class Indexes(FrameLine line) : ISourceIndexQuery
    {
        public void SetCancellation(CancellationToken token) => token.ThrowIfCancellationRequested();
        public SourcePresentationLoad? Presentation(BindingId bindingId) => bindingId == line.BindingId ? new(line, 0, 60_000_000, 0) : null;
        public ImmutableArray<SourcePresentationSample> PresentationsAt(long timeUs, SourceFilter filter) => [];
        public ImmutableArray<SourcePresentationLoad> PresentationsIn(TimeRange range, SourceFilter filter) => [];
        public BaseSceneGeometry LayoutAt(long timeUs, SourceFilter filter) => new() { TimeUs = timeUs };
        public ImmutableArray<SourceCursorSegment> CursorSegments(CursorTrackId trackId, TimeRange range) => [];
        public ImmutableArray<PlaybackCursor> CursorsAt(long timeUs, SourceFilter filter, CursorAnchorMode anchorMode) => [];
    }
    private sealed class EmptyGeometry : ILineGeometryProvider
    {
        public LineRenderGeometry? GetLineGeometry(ProjectSnapshot snapshot, string lineId) => null;
        public SceneFrame GetSceneFrame(ProjectSnapshot snapshot, long timeUs, string? editingLineId = null)
            => new() { Definition = SceneDefinitionBuilder.Build(snapshot), TimeUs = timeUs };
    }
}
