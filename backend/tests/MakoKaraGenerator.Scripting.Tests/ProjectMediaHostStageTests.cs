using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Rendering.Abstractions;
using MakoKaraGenerator.Scripting.Projection;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting.Execution;
using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class ProjectMediaHostStageTests
{
    // Test-assembly authors deliberately retain handles to inspect the real Host
    // call scopes. They never enter package compilation or change capability admission.
    [Theory]
    [InlineData("success")]
    [InlineData("throw")]
    [InlineData("cancel")]
    public void 实际文字调用退出后媒体和输出都失效(string mode)
    {
        using var cancellation = new CancellationTokenSource();
        var author = new Probe(mode, cancellation); var (active, _, frame, scene) = Active(author);
        Action invoke = () => Call("EvaluatePure", active, frame, scene, ImmutableArray<GroupGeometrySample>.Empty, cancellation.Token);
        if (mode == "success") invoke();
        else if (mode == "cancel") Assert.Throws<OperationCanceledException>(invoke);
        else Assert.Throws<InvalidOperationException>(invoke);
        Assert.Equal(1001, author.TimeUs);
        Assert.Equal("InvalidMediaFeatureStage", Assert.Throws<EffectOutputException>(() => author.Media!.Catalog).Code);
        Assert.Equal("InvalidOutputStage", Assert.Throws<EffectOutputException>(() => author.Text!.Complete()).Code);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("throw")]
    [InlineData("cancel")]
    [InlineData("accept-throw")]
    [InlineData("swallowed-budget")]
    public void 实际视觉调用及发布失败都关闭三个阶段句柄(string mode)
    {
        using var cancellation = new CancellationTokenSource();
        var author = new Probe(mode, cancellation); var (active, _, _, _) = Active(author);
        ImmutableArray<ParticleView> alive = [];
        var births = new ParticleBirthWindow(new(0, 1001), []);
        Func<ParticleIdentity, ImmutableArray<ParticleTrailSample>> trail = _ => [];
        Action<long> reserve = _ => { if (mode == "swallowed-budget") throw new InvalidOperationException("budget"); };
        Func<FinalSceneGeometry> geometry = () => new() { TimeUs = 1001, Presentations = new() };
        Action<OverlayBatch> accept = _ => { if (mode == "accept-throw") throw new InvalidOperationException("publish"); };
        Action invoke = () => Call("EvaluatePureVisualStage", active, author, 1001L, OutputMode.Opaque, alive, births,
            trail, reserve, geometry, accept, VisualOutputBudget.Default, cancellation.Token);
        if (mode == "success") invoke();
        else if (mode == "cancel") Assert.Throws<OperationCanceledException>(invoke);
        else Assert.Throws<InvalidOperationException>(invoke);
        Assert.Equal(1001, author.TimeUs);
        Assert.Equal("InvalidMediaFeatureStage", Assert.Throws<EffectOutputException>(() => author.Media!.Catalog).Code);
        Assert.Equal("InvalidVisualOutputStage", Assert.Throws<EffectOutputException>(() => author.Visual!.Complete()).Code);
        Assert.Equal("InvalidVisualOutputStage", Assert.Throws<EffectOutputException>(() => author.Particles!.Alive).Code);
    }

    [Fact]
    public void 重复及乱序文字请求获得彼此独立句柄()
    {
        using var cancellation = new CancellationTokenSource();
        var author = new Probe("success", cancellation); var (active, _, frame, scene) = Active(author);
        List<MediaFeatures> handles = [];
        foreach (long timeUs in new long[] { 1001, 0, 1001, 3333 })
        {
            Call("EvaluatePure", active, frame with { TimeUs = timeUs }, scene with { TimeUs = timeUs },
                ImmutableArray<GroupGeometrySample>.Empty, cancellation.Token);
            Assert.Equal(timeUs, author.TimeUs); handles.Add(author.Media!);
        }
        Assert.Equal(4, handles.Distinct(ReferenceEqualityComparer.Instance).Count());
        foreach (var handle in handles)
            Assert.Equal("InvalidMediaFeatureStage", Assert.Throws<EffectOutputException>(() => handle.Catalog).Code);
    }

    [Fact]
    public void 实际文字和视觉阶段将精确nodeUs传递给固定sampler且彼此独立()
    {
        using var cancellation = new CancellationTokenSource();
        var contents = ProjectMediaProjectionFixture.Package(); var project = ProjectMediaProjectionFixture.Project(contents);
        var plan = ProjectMediaDependencyResolver.Resolve(project); var source = ProjectMediaProjectionFixture.Source();
        var input = MediaFeatureInput.CreatePending(ProjectMediaProjectionFixture.Context(plan, source.Profile), source,
            new(ProjectMediaProjectionFixture.Hash('a')));
        var sampler = new ProjectMediaProjectionFixture.Sampler(input, plan); List<long> sampled = [];
        sampler.OnSample = (dependency, rect, timeUs, token) => { sampled.Add(timeUs); return new(input, dependency, rect, timeUs); };
        var binding = new ProjectMediaFeatureBinding(sampler); var catalog = EffectPackageMediaQueries.Read(contents);
        var projection = ProjectMediaFeatureProjection.ResolveInstance(binding, new("owner-0"), catalog, "scale",
            ProjectMediaProjectionFixture.Parameters(catalog), catalog.ResolveParameters("scale"));
        var author = new Probe("sample", cancellation); var (active, _, frame, scene) = Active(author, projection);
        List<MediaFeatures> handles = [];
        foreach (long nodeUs in new long[] { 3333, 1001, 3333 })
        {
            Call("EvaluatePure", active, frame with { TimeUs = nodeUs }, scene with { TimeUs = nodeUs },
                ImmutableArray<GroupGeometrySample>.Empty, CancellationToken.None); handles.Add(author.Media!);
        }
        Func<ParticleIdentity, ImmutableArray<ParticleTrailSample>> trail = _ => [];
        Action<long> reserve = _ => { };
        Func<FinalSceneGeometry> geometry = () => new() { TimeUs = 1001, Presentations = new() };
        Action<OverlayBatch> accept = _ => { };
        Call("EvaluatePureVisualStage", active, author, 1001L, OutputMode.Opaque, ImmutableArray<ParticleView>.Empty,
            new ParticleBirthWindow(new(0, 1001), []), trail, reserve, geometry, accept, VisualOutputBudget.Default, CancellationToken.None);
        handles.Add(author.Media!);
        Assert.Equal(new long[] { 3333, 1001, 3333, 1001 }, sampled);
        Assert.Equal(4, handles.Distinct(ReferenceEqualityComparer.Instance).Count());
        Assert.All(handles, handle => Assert.Equal("InvalidMediaFeatureStage", Assert.Throws<EffectOutputException>(() => handle.Catalog).Code));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 实际锚点策略即使声明连续也对媒体owner强制Exact(bool mediaDependent)
    {
        using var cancellation = new CancellationTokenSource();
        ProjectMediaFeatureProjection? projection = null;
        if (mediaDependent)
        {
            // Metadata projection is not executable package admission. The tested
            // region reducer only reads this real plan owner and calls no author.
            var contents = ProjectMediaProjectionFixture.Package();
            var mediaProject = ProjectMediaProjectionFixture.Project(contents);
            var plan = ProjectMediaDependencyResolver.Resolve(mediaProject);
            var source = ProjectMediaProjectionFixture.Source();
            var input = MediaFeatureInput.CreatePending(ProjectMediaProjectionFixture.Context(plan, source.Profile), source,
                new(ProjectMediaProjectionFixture.Hash('a')));
            var binding = new ProjectMediaFeatureBinding(new ProjectMediaProjectionFixture.Sampler(input, plan));
            var catalog = EffectPackageMediaQueries.Read(contents);
            projection = ProjectMediaFeatureProjection.ResolveInstance(binding, new("owner-0"), catalog, "scale",
                ProjectMediaProjectionFixture.Parameters(catalog), catalog.ResolveParameters("scale"));
        }
        var author = new Probe("success", cancellation); var (active, project, _, scene) = Active(author, projection, continuous: true);
        var snapshot = new ProjectSnapshot { Name = "anchor" };
        using var host = new FrameEffectHost(snapshot, project, new EmptyGeometry(), [], [], VisualOutputBudget.Default);
        var instances = (System.Collections.IList)typeof(FrameEffectHost).GetField("_instances", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
        instances.Add(active);
        typeof(FrameEffectHost).GetField("_v2AnchorBreakpoints", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(host, ImmutableArray.Create(0L));
        var particle = new ParticleSource.Line(new(new("line"), new("binding")));
        var result = ((bool Continuous, long Start, long End))typeof(FrameEffectHost).GetMethod("V2AnchorRegion", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(host, [active, particle, 1001L, scene, CancellationToken.None])!;
        Assert.Equal(!mediaDependent, result.Continuous);
        if (mediaDependent) { Assert.Equal(1001, result.Start); Assert.Equal(1001, result.End); }
        Assert.Null(author.Media);
    }

    private static (object Active, HostProjectInputs Project, TextFrameHostInputs Frame, SceneFrame Scene) Active(Probe author, ProjectMediaFeatureProjection? projection = null, bool continuous = false)
    {
        var files = EffectPackageFixture.Files(); var manifest = EffectPackageFixture.Manifest(files);
        manifest["packageVersion"] = EffectPackageProfiles.V2.PackageVersion;
        manifest["sdkVersion"] = EffectPackageProfiles.V2.SdkVersion;
        manifest["shaderProfile"] = EffectPackageProfiles.V2.ShaderProfile;
        manifest["requires"] = new JsonArray("token.affine", "visuals.dots");
        manifest["lineExit"] = new JsonObject { ["durationUs"] = 0 }; manifest["emitters"] = new JsonArray();
        var package = EffectPackageDefinition.Read(EffectPackageContents.CreateSupported(EffectPackageFixture.Bytes(manifest), EffectPackageFixture.Inputs(files)));
        ContentCatalog content = new([], [], [], []); SceneCatalog scene = new(content, [], [], [], []);
        HostProjectInputs project = new(content, scene, new(320, 240));
        DeclareContext source = new(content, scene, ParameterValues.Empty, AudioFeatures.NotAnalyzed, ResourceCatalog.Empty,
            42, TimelineIndex.Empty, project.CanvasSize, new("test"), new SourceIndexes(TimelineIndex.Empty, new EmptyIndexes()), projection?.Catalog ?? MediaQueryCatalog.Empty);
        var prepared = PreparedEffect.Empty with { GeometrySampling = continuous ? new(GeometrySamplingMode.Continuous120Hz, [new(0, 10000)], []) : GeometrySamplingPolicy.Exact, VisualOutputs = [new(new("manual"), VisualPrimitiveKind.Dots, OverlayLayer.AboveText)] };
        var definition = new CompiledFrameEffect(new("test"), null, null, ParameterValues.Empty, new EffectBinding.Project(), EffectCapabilities.Transforms)
            { PackageDefinition = package, PackageDeclaration = package.GetExport("scale") };
        var type = typeof(FrameEffectHost);
        object pure = New(type.GetNestedType("PureEffectRuntime", BindingFlags.NonPublic)!, author, source, prepared, null, null, null, projection);
        object active = New(type.GetNestedType("ActiveEffect", BindingFlags.NonPublic)!, definition, null,
            new DeterministicRandom(42, new("test")), null, null, pure, null);
        TextFrameHostInputs frame = new(1001, BaselineStyles.Empty, new(), [], [], [], [], definition.Binding, OutputMode.Opaque);
        var snapshot = new ProjectSnapshot { Name = "stage" };
        return (active, project, frame, new() { TimeUs = 1001, Definition = SceneDefinitionBuilder.Build(snapshot) });
    }

    private static object New(Type type, params object?[] args) => type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
        .Single(ctor => ctor.GetParameters().Length == args.Length).Invoke(args);
    private static object? Call(string name, params object?[] args)
    {
        try { return typeof(FrameEffectHost).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }

    private sealed class EmptyIndexes : ISourceIndexQuery
    {
        public void SetCancellation(CancellationToken token) => token.ThrowIfCancellationRequested();
        public SourcePresentationLoad? Presentation(BindingId bindingId) => bindingId.Value == "binding"
            ? new(new(new("line"), new("branch"), bindingId, new("node"), new("group"), new("program"), null, null, DisplayPhase.Performing, 1), 0, 10000, 0) : null;
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

    private sealed class Probe(string mode, CancellationTokenSource cancellation) : IPureKaraokeEffect, IPureVisualEffect
    {
        internal MediaFeatures? Media; internal TextOutput? Text; internal VisualOutput? Visual; internal ParticleFrame? Particles;
        internal long TimeUs;
        public PreparedEffect Declare(DeclareContext source) => PreparedEffect.Empty;
        public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output)
        { Media = frame.Media; Text = output; TimeUs = frame.TimeUs; Finish(); }
        public void EvaluateVisuals(in VisualFrame frame, PreparedEffect data, VisualOutput output)
        {
            Media = frame.Media; Visual = output; Particles = frame.Particles; TimeUs = frame.TimeUs;
            Finish();
            try { output.DrawDots(new("manual"), [new(default, 1, new(1, 1, 1, 1))], OverlayLayer.AboveText); }
            catch (InvalidOperationException) when (mode == "swallowed-budget") { }
        }
        private void Finish()
        {
            if (mode == "sample") { var media = Media!; media.Sample(media.Catalog.GetId("source"), new(0, 0, 1, 1), TimeUs); }
            if (mode == "throw") throw new InvalidOperationException("author");
            if (mode == "cancel") cancellation.Cancel();
        }
    }
}
