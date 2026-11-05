using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Execution;
using MakoKaraGenerator.Scripting.Projection;
using F = MakoKaraGenerator.Rendering.Tests.V2GroupGraphPackageFixture;

namespace MakoKaraGenerator.Rendering.Tests;

// Real v3 package admission and main-canvas group composition.
public sealed class V2GroupGraphPackageTests
{
    private sealed class ResourceOnlyCompiler : IScriptCompilationService
    {
        private readonly ScriptCompiler _real = new(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0"));
        public ScriptCompilationIdentity Identity => _real.Identity;
        public int Calls { get; private set; }
        public Task<ScriptCompilationResult> CompileAsync(IEnumerable<ScriptSource> sources, IEnumerable<string> entryTypes, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("host direct group must never compile an author DLL"); }
    }
    private static async Task<CompiledEffectPackage> Compiled(EffectPackageContents contents, IScriptCompilationService? compiler = null)
        => await CompiledEffectPackage.CompileAsync(EffectPackageDefinition.Read(contents), compiler ?? new ScriptCompiler(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0")),
            new PackageImageValidator(), new PackageShaderValidator(), new PackageImageGraphValidator(), new PackageMaterialValidator());
    private static JsonNode Export(JsonObject manifest, string kind) => manifest["exports"]!.AsArray().Single(item => item!["kind"]!.GetValue<string>() == kind)!;
    private static void Close(byte[] expected, byte[] actual, int tolerance = 2)
    { Assert.Equal(expected.Length, actual.Length); for (int i = 0; i < expected.Length; i++) Assert.InRange(Math.Abs(expected[i] - actual[i]), 0, tolerance); }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task 真实v3组controller和direct以显式Node写一个source组并重复乱序重开保持一致(bool direct)
    {
        var package = F.Package(direct); var basis = F.Snapshot(package, []); string root = F.Root(basis);
        var binding = new ProjectEffectBinding(ProjectEffectBindingKind.Node, root, includeDescendants: false);
        var snapshot = F.Snapshot(package, [F.Instance(package, direct, root, binding: binding)]);
        var resourceOnly = direct ? new ResourceOnlyCompiler() : null;
        var compiled = await F.Compile(snapshot, resourceOnly); var effect = Assert.Single(compiled.Effects);
        if (direct) { Assert.Equal(0, resourceOnly!.Calls); Assert.Null(effect.Artifact); Assert.Null(effect.EntryType); Assert.Equal(package.Revision, effect.RuntimeHash); }
        else Assert.NotNull(effect.Artifact);
        using var native = CompiledPackageGraphExports.Compile(EffectPackageDefinition.Read(package)); using var resources = F.Resources(native);
        using var budget = new ImageSurfaceBudgetSession(); using var renderer = new SkiaRenderPipeline(null, null, budget, resources);
        await using var session = await F.Factory(snapshot, compiled).CreateAsync(snapshot);
        var first = await F.Prepare(session, snapshot); var imageBinding = Assert.Single(first.ImageEffects);
        Assert.Equal(root, Assert.IsType<ImageEffectTarget.Group>(imageBinding.Binding.Target).Target.NodeId.Value);
        Assert.Equal(effect.Id, imageBinding.InstanceId); Assert.Equal(effect.RuntimeHash, imageBinding.ArtifactHash); Assert.Equal(effect.PackageIdentity, imageBinding.PackageIdentity);
        var tinted = F.Pixels(snapshot, first, renderer); Assert.Contains(tinted, value => value != 0);
        foreach (long time in new[] { 1_250_000L, 750_000L, 500_000L }) await F.Prepare(session, snapshot, time);
        var repeated = await F.Prepare(session, snapshot); Assert.Equal(first.ImageEffects.ToArray(), repeated.ImageEffects.ToArray());
        Close(tinted, F.Pixels(snapshot, repeated, renderer), 0);
        await using var reopened = await F.Factory(snapshot, compiled).CreateAsync(snapshot);
        Close(tinted, F.Pixels(snapshot, await F.Prepare(reopened, snapshot), renderer), 0);
        var fullSnapshot = F.Snapshot(package, [F.Instance(package, direct, root, amount: 1, binding: binding)]);
        var fullCompiled = await F.Compile(fullSnapshot, resourceOnly); await using var fullSession = await F.Factory(fullSnapshot, fullCompiled).CreateAsync(fullSnapshot);
        var full = F.Pixels(fullSnapshot, await F.Prepare(fullSession, fullSnapshot), renderer); int changed = 0;
        for (int i = 0; i < full.Length; i++) if (i % 4 == 3) Assert.InRange(Math.Abs(full[i] - tinted[i]), 0, 2);
            else { Assert.InRange(Math.Abs(full[i] * .5 - tinted[i]), 0, 2); if (full[i] != tinted[i]) changed++; }
        Assert.True(changed > 10); Assert.Equal(0, budget.Usage.ActiveSurfaces); Assert.Equal(0, budget.Usage.EstimatedBytes);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Project可写真实组但Node开关只按运行父树授予范围且不含别名(bool direct)
    {
        var package = F.Package(direct); var basis = F.Snapshot(package, []); string root = F.Root(basis);
        foreach (var binding in new[] { new ProjectEffectBinding(ProjectEffectBindingKind.Project), new(ProjectEffectBindingKind.Node, root), new(ProjectEffectBindingKind.Node, root, includeDescendants: true) })
        {
            // Controller deliberately addresses only root for the no-descendants case.
            string selected = binding.Kind == ProjectEffectBindingKind.Node && !binding.IncludeDescendants ? root : "";
            var snapshot = F.Snapshot(package, [F.Instance(package, direct, selected, amount: 1, binding: binding)]);
            var compiled = await F.Compile(snapshot); await using var session = await F.Factory(snapshot, compiled).CreateAsync(snapshot);
            var frame = await F.Prepare(session, snapshot); var scope = new FrameBindingScope(frame.HierarchyScene);
            EffectBinding sdkBinding = binding.Kind == ProjectEffectBindingKind.Project ? new EffectBinding.Project() : new EffectBinding.Node(new(root), binding.IncludeDescendants);
            var expected = frame.FinalGroupGeometry.Where(item => scope.ContainsGroup(sdkBinding, item.Target)).Select(item => item.Target).ToArray();
            var actual = frame.ImageEffects.Select(item => Assert.IsType<ImageEffectTarget.Group>(item.Binding.Target).Target).ToArray();
            Assert.Equal(expected.OrderBy(item => item.NodeId.Value), actual.OrderBy(item => item.NodeId.Value));
            Assert.All(actual, target => Assert.Null(frame.HierarchyScene.Elements.Single(node => node.Id == target.NodeId.Value).SourceNodeId));
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task BranchLinesTokensClips不因全局组几何存在而获得组图权限(bool direct)
    {
        var package = await Compiled(F.Package(direct));
        foreach (EffectBinding binding in new EffectBinding[] { new EffectBinding.Branch(new("branch")), new EffectBinding.Lines([new("line")]), new EffectBinding.Tokens([new("token")]), new EffectBinding.Clips([new("clip")]) })
            Assert.Equal("PackageBindingNotAllowed", Assert.Throws<EffectPackageException>(() => package.CreateEffect(direct ? "tint" : "controller", new("blocked"), binding, 0)).Code);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task 缺新增cap保留具名拒绝且显式group输出不能省略cap(bool direct)
    {
        var missing = F.Package(direct, manifest =>
        {
            manifest["requires"] = new JsonArray("image.graph");
            foreach (var export in manifest["exports"]!.AsArray()) export!["outputs"]![0]!["requires"] = new JsonArray();
        });
        var old = await Compiled(missing);
        Assert.Equal("GroupImageEffectUnsupported", Assert.Throws<EffectPackageException>(() => old.CreateEffect(direct ? "tint" : "controller", new("blocked"), new EffectBinding.Project(), 0)).Code);
        var outputMissing = await Compiled(F.Package(direct, manifest => Export(manifest, direct ? "graph" : "controller")["outputs"]![0]!["requires"] = new JsonArray()));
        Assert.Equal("PackageCapabilityMissing", Assert.Throws<EffectPackageException>(() => outputMissing.CreateEffect(direct ? "tint" : "controller", new("blocked"), new EffectBinding.Project(), 0)).Code);
        var noBase = F.Package(direct, manifest => manifest["requires"] = new JsonArray(F.GroupCapability));
        Assert.Equal("PackageCapabilityMissing", Assert.Throws<EffectPackageException>(() => EffectPackageDefinition.Read(noBase)).Code);
    }

    [Theory]
    [InlineData("extra", "PackageOutputPlanPending")] [InlineData("duplicate", "PackageOutputPlanPending")]
    [InlineData("dependency", "PackageOutputPlanPending")] [InlineData("base", "PackageOutputTargetUnsupported")]
    [InlineData("mapping", "PackageGraphMappingPending")]
    public async Task direct组图不扩额外能力多输出依赖Base或Mapping(string change, string code)
    {
        EffectPackageContents Make() => F.Package(true, manifest =>
        {
            var graph = Export(manifest, "graph"); var output = graph["outputs"]![0]!;
            if (change == "extra") { manifest["requires"]!.AsArray().Add("group.opacity"); output["requires"]!.AsArray().Add("group.opacity"); }
            if (change is "duplicate" or "dependency") { var second = output.DeepClone(); second["id"] = "second"; graph["outputs"]!.AsArray().Add(second); if (change == "dependency") { second["target"] = "line"; second["requires"] = new JsonArray("image.graph"); second["dependsOn"] = new JsonArray("color"); } }
            if (change == "base") output["target"] = "baseToken";
        }, graph => { if (change == "mapping") graph["anchorMode"] = "ImageOnly"; });
        var error = await Assert.ThrowsAsync<EffectPackageException>(async () => { var compiled = await Compiled(Make()); compiled.CreateEffect("tint", new("blocked"), new EffectBinding.Project(), 0); });
        Assert.Equal(code, error.Code);
    }

    [Fact]
    public void 新增组cap不开放实际ownedOverlay捕获配方()
    {
        var package = F.Package(true, graphChange: graph => graph["inputs"]![0]!["capture"]!["includeOwnedOverlay"] = true);
        using var native = CompiledPackageGraphExports.Compile(EffectPackageDefinition.Read(package));
        var error = Assert.Throws<EffectPackageException>(() => native.CreateProgram(native.Resources.GetGraph("tint"), new(0, 0, 640, 360)));
        Assert.Equal("PackageOwnedOverlayCapturePending", error.Code);
    }

    [Theory]
    [InlineData("cap", "PackageCapabilityMissing")] [InlineData("target", "UndeclaredPackageOutput")]
    public async Task controller不能用自身声明替代实际graph导出的group声明(string change, string code)
    {
        var package = F.Package(false, manifest => { var output = Export(manifest, "graph")["outputs"]![0]!;
            if (change == "cap") output["requires"] = new JsonArray(); else output["target"] = "line"; });
        var basis = F.Snapshot(package, []); string root = F.Root(basis);
        var snapshot = F.Snapshot(package, [F.Instance(package, false, root)]); var compiled = await F.Compile(snapshot);
        await using var session = await F.Factory(snapshot, compiled).CreateAsync(snapshot);
        var error = await Assert.ThrowsAsync<EffectFailureException>(async () => await F.Prepare(session, snapshot));
        Assert.Equal(code, error.Code); Assert.Equal("group-instance", error.InstanceId.Value); Assert.NotNull(error.PackageIdentity);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task active与exit同一source组只绑定一次且alias不能成为准备目标(bool direct)
    {
        var package = F.Package(direct, exitUs: 600_000); var basis = F.Snapshot(package, []); string root = F.Root(basis);
        var snapshot = F.Snapshot(package, [F.Instance(package, direct, root, amount: 1, binding: new(ProjectEffectBindingKind.Node, root, includeDescendants: true))]);
        var compiled = await F.Compile(snapshot); await using var session = await F.Factory(snapshot, compiled).CreateAsync(snapshot);
        var frame = await F.Prepare(session, snapshot, 1_250_000);
        Assert.Contains(frame.Scene.Lines, line => line.ExitReplica is not null); Assert.Contains(frame.Scene.Lines, line => line.ExitReplica is null);
        // Node(root,false) owns root but does not extend descendant line lifetimes.
        // The explicit descendants switch grants those lines to the exit owner.
        var binding = Assert.Single(frame.ImageEffects.Where(item => item.Binding.Target is ImageEffectTarget.Group group && group.Target.NodeId.Value == root));
        Assert.Equal(frame.ImageEffects.Length, frame.ImageEffects.Select(item => item.Binding.Target).Distinct().Count());
        Assert.All(frame.ImageEffects, item =>
        {
            var target = Assert.IsType<ImageEffectTarget.Group>(item.Binding.Target);
            Assert.Contains(frame.HierarchyScene.Elements, node => node.Id == target.Target.NodeId.Value && node.Kind == SceneElementKind.Group && node.SourceNodeId is null);
        });
        var alias = frame.HierarchyScene.Elements.First(node => node.Kind == SceneElementKind.Group && node.SourceNodeId == root);
        var rejected = binding with { Binding = binding.Binding with { Target = new ImageEffectTarget.Group(new(new(alias.Id))) } };
        Assert.Equal("InvalidPreparedImageEffectTarget", Assert.Throws<PreparedFrameException>(() => frame.WithImageEffects([rejected])).Code);
        using var native = CompiledPackageGraphExports.Compile(EffectPackageDefinition.Read(package)); using var resources = F.Resources(native);
        using var budget = new ImageSurfaceBudgetSession(); using var renderer = new SkiaRenderPipeline(null, null, budget, resources);
        Close(F.Pixels(snapshot, frame.WithImageEffects([]), renderer), F.Pixels(snapshot, frame, renderer), 2);
        Assert.Equal(0, budget.Usage.ActiveSurfaces); Assert.Equal(0, budget.Usage.EstimatedBytes);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task 空source组依旧验证真实包并Info消费且不凭空执行(bool direct)
    {
        var package = F.Package(direct); var basis = F.Snapshot(package, []); string root = F.Root(basis);
        var snapshot = F.Snapshot(package, [F.Instance(package, direct, root, binding: new(ProjectEffectBindingKind.Node, root))]);
        var compiled = await F.Compile(snapshot); await using var session = await F.Factory(snapshot, compiled).CreateAsync(snapshot);
        var frame = await F.Prepare(session, snapshot, 3_000_000); Assert.Single(frame.ImageEffects);
        Assert.Empty(frame.FinalGroupGeometry.Single(group => group.Target.NodeId.Value == root).WorldInkCorners);
        using var native = CompiledPackageGraphExports.Compile(EffectPackageDefinition.Read(package)); using var resources = F.Resources(native);
        using var budget = new ImageSurfaceBudgetSession(); using var renderer = new SkiaRenderPipeline(null, null, budget, resources);
        using var rendered = renderer.Render(RenderRequest.At(snapshot, frame.TimeUs) with { PreparedFrame = frame, TransparentBackground = true });
        Assert.Contains(rendered.SceneDiagnostics, item => item.Diagnostic.Code == "GroupImageEffectNoVisibleChildren" && item.Diagnostic.Severity == DiagnosticSeverity.Info);
        Assert.DoesNotContain(rendered.PixelMemory.ToArray(), value => value != 0); Assert.Empty(rendered.ImageEffectDiagnostics);
        using var absent = new SceneImageEffectResources([]); using var missingRenderer = new SkiaRenderPipeline(null, null, budget, absent);
        var error = Assert.Throws<EffectFailureException>(() => F.Pixels(snapshot, frame, missingRenderer)); Assert.Equal("ImageEffectResourceUnavailable", error.Code);
        Assert.Equal("group-instance", error.InstanceId.Value); Assert.Equal(0, budget.Usage.ActiveSurfaces); Assert.Equal(0, budget.Usage.EstimatedBytes);
    }
}
