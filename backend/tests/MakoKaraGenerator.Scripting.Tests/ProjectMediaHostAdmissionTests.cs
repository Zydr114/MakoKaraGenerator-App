using System.Collections.Immutable;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Rendering.Abstractions;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Execution;
using MakoKaraGenerator.Scripting.Projection;
using static MakoKaraGenerator.Scripting.Tests.ProjectMediaProjectionFixture;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class ProjectMediaHostAdmissionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("legacy-arbitrary-fingerprint")]
    public void 无媒体严格保留旧源身份(string? fingerprint)
    {
        var snapshot = new ProjectSnapshot { Name = "legacy", EffectInputsFingerprint = fingerprint };
        Assert.Equal(fingerprint, ProjectRuntimeEffectInputs.Fingerprint(snapshot));
    }

    [Fact]
    public void 媒体状态和请求修订都进入唯一运行身份()
    {
        var (snapshot, plan, source, context) = Fixed();
        var a = MediaFeatureInput.CreatePending(context, source, new(Hash('a')));
        var same = MediaFeatureInput.CreatePending(Context(plan, source.Profile), Source(), new(Hash('a')));
        var retry = MediaFeatureInput.CreatePending(context, source, new(Hash('b')));
        var available = MediaFeatureInput.CreateAvailableFile(context, source, Artifact(source));
        Assert.True(ProjectRuntimeEffectInputs.SameMediaInput(a, same));
        Assert.Equal(Identity(a), Identity(same));
        Assert.NotEqual(snapshot.EffectInputsFingerprint, Identity(a));
        Assert.Equal(3, new[] { Identity(a), Identity(retry), Identity(available) }.Distinct().Count());
        string? Identity(MediaFeatureInput input) => ProjectRuntimeEffectInputs.Fingerprint(snapshot with { MediaFeatureInput = input });
    }

    [Fact]
    public void 相同规范失败身份仍比较完整安全详情()
    {
        var (_, _, source, context) = Fixed();
        var a = MediaFeatureInput.CreateUnavailable(context, source, new(Hash('a')), MediaFailure.Create(new(Hash('b')), "DecodeFailed", "First detail"));
        var b = MediaFeatureInput.CreateUnavailable(context, Source(), new(Hash('a')), MediaFailure.Create(new(Hash('b')), "DecodeFailed", "Second detail"));
        Assert.Equal(a.InputRevision, b.InputRevision);
        Assert.False(ProjectRuntimeEffectInputs.SameMediaInput(a, b));
    }

    [Fact]
    public void 独立构造解析纯色也以完整值比较()
    {
        var (snapshot, plan, source, _) = Fixed();
        var context = MediaInputContext.Create(source.Profile, MediaMapping.CreateSolid(320, 240), plan.QueryCatalogDigest, plan.FixedDependencyDigest);
        var a = MediaFeatureInput.CreateAvailableSolid(context, MediaFeatureSolidSource.Create(source.Profile, new(.1, .1, .1, .5)));
        var b = MediaFeatureInput.CreateAvailableSolid(context, MediaFeatureSolidSource.Create(Source().Profile, new(.1, .1, .1, .5)));
        Assert.True(ProjectRuntimeEffectInputs.SameMediaInput(a, b));
        Assert.Equal(ProjectRuntimeEffectInputs.Fingerprint(snapshot with { MediaFeatureInput = a }), ProjectRuntimeEffectInputs.Fingerprint(snapshot with { MediaFeatureInput = b }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 缺少绑定按完整闭包拒绝含纯源材质工程(bool sourceOnly)
    {
        var (snapshot, _, _, _) = Fixed(sourceOnly);
        var error = Assert.Throws<EffectOutputException>(() => ProjectMediaFeatureAdmission.Validate(snapshot, [], null, null, default));
        Assert.Equal("MediaFeatureBindingRequired", error.Code);
    }

    [Fact]
    public void 错误输入及变化采样器在阶段和作者前拒绝()
    {
        var (snapshot, plan, source, context) = Fixed();
        var a = MediaFeatureInput.CreatePending(context, source, new(Hash('a')));
        var b = MediaFeatureInput.CreatePending(context, source, new(Hash('b')));
        var sampler = new Sampler(a, plan); var binding = new ProjectMediaFeatureBinding(sampler);
        Assert.Equal("MediaFeatureBindingMismatch", Assert.Throws<EffectOutputException>(() =>
            ProjectMediaFeatureAdmission.Validate(snapshot with { MediaFeatureInput = b }, [], null, binding, default)).Code);
        Assert.Equal("MediaFeatureBindingMismatch", Assert.Throws<EffectOutputException>(() =>
            ProjectMediaFeatureAdmission.Validate(snapshot, [], null, binding, default)).Code);
        sampler.Input = b;
        Assert.Throws<MediaAbException>(() => ProjectMediaFeatureAdmission.Validate(snapshot with { MediaFeatureInput = a }, [], null, binding, default));
        Assert.Equal(0, sampler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 固定输出策略不能随请求切换(bool transparent)
    {
        var (_, plan, source, _) = Fixed(); var context = Context(plan, source.Profile, transparent);
        MediaFeatureInput input = transparent
            ? MediaFeatureInput.CreateNoMedia(context, MediaNoMediaReason.TransparentOutput, Hash('c'))
            : MediaFeatureInput.CreatePending(context, source, new(Hash('a')));
        var binding = new ProjectMediaFeatureBinding(new Sampler(input, plan));
        ProjectMediaFeatureAdmission.ValidateOutput(binding, transparent ? OutputMode.Transparent : OutputMode.Opaque);
        Assert.Equal("MediaFeatureOutputMismatch", Assert.Throws<EffectOutputException>(() =>
            ProjectMediaFeatureAdmission.ValidateOutput(binding, transparent ? OutputMode.Opaque : OutputMode.Transparent)).Code);
    }

    [Fact]
    public void 真实源材质专用闭包在无控制器时经过正式门禁并接受固定绑定()
    {
        var (basis, plan, source, context) = Fixed(sourceOnly: true);
        var input = MediaFeatureInput.CreatePending(context, source, new(Hash('a')));
        var snapshot = basis with { MediaFeatureInput = input };
        var contents = snapshot.Effects.Packages.Single(); var catalog = EffectPackageMediaQueries.Read(contents);
        var resources = Resources(contents);
        var material = (MaterialValue.PackageMaterial)MaterialValue.PackageExport(resources.GetMaterial("ink"), Parameters(catalog, "ink"));
        var references = SourceMaterialCatalog.Collect(snapshot.LyricStyle, snapshot.Lyrics, snapshot.LyricPresentation, snapshot.PresetApplications);
        var prepared = new PreparedSourceMaterials(snapshot, references.Select(reference => KeyValuePair.Create(reference, material)));
        var binding = new ProjectMediaFeatureBinding(new Sampler(input, plan));
        ProjectMediaFeatureAdmission.Validate(snapshot, [], prepared, binding, default);
    }

    [Fact]
    public async Task 实际工厂缺绑定在几何工厂之前拒绝()
    {
        var (snapshot, _, _, _) = Fixed();
        var compiler = new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["src/NoOp.cs"] = System.Text.Encoding.UTF8.GetBytes("""
                using MakoKaraGenerator.Scripting.V2;
                public sealed class NoOp : IPureKaraokeEffect
                {
                    public PreparedEffect Declare(DeclareContext source) => PreparedEffect.Empty;
                    public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output) { }
                }
                """),
        };
        var manifest = V2PackageTestHelper.Manifest(files, ["token.affine"], "NoOp");
        manifest["parameters"] = new JsonArray();
        manifest["exports"]![0]!["parameters"] = new JsonArray();
        manifest["exports"]![0]!["bindingKinds"] = new JsonArray("project");
        var package = await CompiledEffectPackage.CompileAsync(
            EffectPackageDefinition.Read(EffectPackageContents.CreateSupported(V2PackageTestHelper.Bytes(manifest), V2PackageTestHelper.Inputs(files))),
            compiler);
        Assert.True(package.Artifact.Succeeded, string.Join('\n', package.Artifact.Diagnostics));
        bool geometry = false;
        var factory = new PureFrameSourceFactory([package.CreateEffect("scale", new("standalone"), new EffectBinding.Project(), 0)],
            snapshot.EffectInputsFingerprint!, () => { geometry = true; throw new InvalidOperationException(); });
        var error = await Assert.ThrowsAsync<EffectOutputException>(async () => await factory.CreateAsync(snapshot));
        Assert.Equal("MediaFeatureBindingRequired", error.Code); Assert.False(geometry);
    }

    [Fact]
    public void 准备帧及其复制保留唯一媒体运行身份()
    {
        var (basis, _, source, context) = Fixed();
        var snapshot = basis with { MediaFeatureInput = MediaFeatureInput.CreatePending(context, source, new(Hash('a'))) };
        var scene = new SceneFrame { Definition = SceneDefinitionBuilder.Build(snapshot), TimeUs = 0 };
        var target = new RenderTarget { PixelWidth = 320, PixelHeight = 240 };
        var prepared = new PreparedRenderFrame(snapshot, 0, OutputMode.Opaque, target, scene, []);
        var copy = prepared.WithV2ParticleSnapshots([], new(0, 0, 0, 0, 0));
        Assert.NotSame(prepared, copy);
        Assert.Equal(ProjectRuntimeEffectInputs.Fingerprint(snapshot), copy.RuntimeInputsFingerprint);
        Assert.NotEqual(snapshot.EffectInputsFingerprint, copy.RuntimeInputsFingerprint);
        var request = RenderRequest.At(snapshot, 0) with { PreparedFrame = copy, Target = target };
        PreparedRenderFrame.ValidateRequest(request);
        var different = snapshot with { MediaFeatureInput = MediaFeatureInput.CreatePending(context, source, new(Hash('b'))) };
        Assert.Equal("PreparedInputsMismatch", Assert.Throws<PreparedFrameException>(() =>
            PreparedRenderFrame.ValidateRequest(request with { Project = different })).Code);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("params")]
    [InlineData("instance")]
    [InlineData("missing")]
    [InlineData("seed")]
    [InlineData("binding")]
    [InlineData("subscription")]
    [InlineData("overlay")]
    public void 无query导出仍证明真实源实例和完整参数(string mode)
    {
        var files = V2PackageTestHelper.Files(); files["assets/image.png"] = [1, 2, 3];
        var manifest = V2PackageTestHelper.NineParametersManifest(files);
        manifest["exports"]![0]!["bindingKinds"] = new JsonArray("project", "branch");
        var contents = EffectPackageContents.CreateSupported(V2PackageTestHelper.Bytes(manifest), V2PackageTestHelper.Inputs(files));
        var package = EffectPackageDefinition.Read(contents); var catalog = EffectPackageMediaQueries.Read(contents);
        var instance = new ProjectEffectInstance { Id = "plain", Name = "Plain", PackageId = contents.PackageId,
            Revision = contents.Revision, ExportId = "scale", Parameters = EffectParameterOverrides.Parse("{\"peak\":2}") };
        var snapshot = ProjectSnapshot.FromProject(MakoKaraGenerator.Core.Projects.Project.Create("plain") with
            { Composition = new() { CanvasWidth = 320, CanvasHeight = 240 }, Effects = new([contents], [instance]) });
        var plan = ProjectMediaDependencyResolver.Resolve(snapshot.Effects, snapshot.LyricStyle, snapshot.Lyrics, snapshot.LyricPresentation, snapshot.PresetApplications);
        Assert.Empty(plan.Owners);
        var source = Source(); var input = MediaFeatureInput.CreateNoMedia(Context(plan, source.Profile), MediaNoMediaReason.NoBackgroundContribution, Hash('c'));
        snapshot = snapshot with { MediaFeatureInput = input };
        var binding = new ProjectMediaFeatureBinding(new Sampler(input, plan));
        var definition = new CompiledFrameEffect(new("plain"), null, null, Parameters(catalog, overrides: "{\"peak\":2}"),
            new EffectBinding.Project(), EffectCapabilities.Transforms) { Seed = instance.Seed, Resources = Resources(contents),
            PackageDefinition = package, PackageDeclaration = package.GetExport("scale") };
        definition = mode switch
        {
            "params" => definition with { Parameters = Parameters(catalog, overrides: "{\"peak\":3}") },
            "instance" => definition with { Id = new("invented") },
            "seed" => definition with { Seed = 42 },
            "binding" => definition with { Binding = new EffectBinding.Branch(new("other")) },
            "subscription" => definition with { Subscription = new SourceSubscription.Project() },
            "overlay" => definition with { OverlayLayerOverride = OverlayLayer.AboveText },
            _ => definition,
        };
        ImmutableArray<CompiledFrameEffect> effects = mode == "missing" ? [] : [definition];
        Action validate = () => ProjectMediaFeatureAdmission.Validate(snapshot, effects, null, binding, default);
        if (mode == "valid") validate();
        else Assert.Equal("MediaFeatureBindingMismatch", Assert.Throws<EffectOutputException>(validate).Code);
    }

    [Fact]
    public void 正式媒体包能力已开放()
    {
        Assert.NotEmpty(EffectPackageMediaQueries.Read(Package()).Queries);
        Assert.NotNull(EffectPackageDefinition.Read(Package()));
    }

    private static (ProjectSnapshot Snapshot, ProjectMediaDependencyPlan Plan, MediaFeatureFileSource Source, MediaInputContext Context) Fixed(bool sourceOnly = false)
    {
        var snapshot = ProjectSnapshot.FromProject(Project(Package(), sourceOnly));
        var plan = ProjectMediaDependencyResolver.Resolve(snapshot.Effects, snapshot.LyricStyle, snapshot.Lyrics,
            snapshot.LyricPresentation, snapshot.PresetApplications);
        var source = Source(); return (snapshot, plan, source, Context(plan, source.Profile));
    }
}
