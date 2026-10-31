using System.Text.Json;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Scripting.Compilation;
using MakoKaraGenerator.Scripting.Execution;
using MakoKaraGenerator.Rendering.Skia;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class PackageGroupTransformTests
{
    private const string Fingerprint = "real-package-group-transform";
    private static readonly Lazy<Task<CompiledEffectPackage>> Package = new(() => Compile(EffectPackageFixture.GroupTransform()));
    private static Task<CompiledEffectPackage> Compile(EffectPackageContents contents)
        => CompiledEffectPackage.CompileAsync(EffectPackageDefinition.Read(contents), new(Path.Combine(AppContext.BaseDirectory, "script-refs", "net10.0")));
    private static CompiledFrameEffect Effect(CompiledEffectPackage package, Fx.EffectBinding binding, string node, double x = 30, bool badWrite = false)
        => package.CreateEffect("group-transform", new("package-group"), binding, 5,
            JsonSerializer.SerializeToUtf8Bytes(new { node, offsetX = x, badWrite }));
    private static PureFrameSourceFactory Factory(CompiledFrameEffect effect) => new([effect], Fingerprint, static () => new SkiaRenderPipeline());
    private static ProjectSnapshot Snapshot() => ProjectSnapshot.FromProject(LeadInFixture.Create(true)) with { EffectInputsFingerprint = Fingerprint };
    private static string Root(ProjectSnapshot snapshot) => snapshot.LyricPresentation.FindProgram("primary")!.RootContainerId;
    private static ProjectSnapshot Shift(ProjectSnapshot snapshot, double x)
    {
        var tree = snapshot.LyricPresentation; var root = tree.FindNode(Root(snapshot))!;
        return snapshot with { EffectInputsFingerprint = null, LyricPresentation = tree with { Nodes = tree.Nodes.Replace(root,
            root with { Placement = root.Placement with { Position = new(root.Placement.Position.X + x / snapshot.Composition.CanvasWidth,
                root.Placement.Position.Y - 16d / snapshot.Composition.CanvasHeight) } }) } };
    }
    [Theory]
    [InlineData(false, false, 10_000_000L)] [InlineData(true, true, 12_500_000L)]
    public async Task 真实包组仿射及参数覆盖在前奏和演唱中与静态父组移动逐像素一致(bool nodeBinding, bool transparent, long time)
    {
        var snapshot = Snapshot(); var package = await Package.Value; Assert.True(package.Artifact.Succeeded);
        var effect = Effect(package, nodeBinding ? new Fx.EffectBinding.Node(new(Root(snapshot)), false) : new Fx.EffectBinding.Project(), Root(snapshot), 60);
        await using var source = await Factory(effect).CreateAsync(snapshot);
        var target = RenderTarget.FromComposition(snapshot.Composition);
        var prepared = await source.PrepareAsync(time, transparent ? Fx.OutputMode.Transparent : Fx.OutputMode.Opaque, target);
        Assert.Single(prepared.GroupTransforms); Assert.Same(snapshot.Lyrics, prepared.Snapshot.Lyrics);
        Assert.All(prepared.HierarchyScene.Lines.Where(line => line.Branch.Id == "translation"), line =>
            Assert.Same(prepared.Scene.Lines.Single(original => original.BindingId == line.BindingId), line));
        using SkiaRenderPipeline renderer = new();
        using var actual = renderer.Render(RenderRequest.At(snapshot, time) with { PreparedFrame = prepared, TransparentBackground = transparent });
        using var expected = renderer.Render(RenderRequest.At(Shift(snapshot, 60), time) with { TransparentBackground = transparent });
        Assert.Equal(expected.PixelMemory.ToArray(), actual.PixelMemory.ToArray());
    }
    [Theory]
    [InlineData("branch")] [InlineData("lines")] [InlineData("tokens")] [InlineData("clips")]
    public async Task 清单允许源筛选不等于授权写组(string kind)
    {
        var snapshot = Snapshot(); var tree = snapshot.LyricPresentation; var program = tree.FindProgram("primary")!;
        snapshot = snapshot with { LyricPresentation = tree with { Nodes = tree.Nodes.Add(new() { Id = "clip-layout", ParentId = program.RootContainerId, Layout = new() }),
            Programs = tree.Programs.Replace(program, program with { Clips = [new() { Id = "clip", ContainerId = "clip-layout", LineIds = ["first"] }] }) } };
        Fx.EffectBinding binding = kind switch { "branch" => new Fx.EffectBinding.Branch(new("primary")), "lines" => new Fx.EffectBinding.Lines([new("first")]),
            "tokens" => new Fx.EffectBinding.Tokens([new("first-a")]), _ => new Fx.EffectBinding.Clips([new("clip")]) };
        var effect = Effect(await Package.Value, binding, Root(snapshot)); await using var source = await Factory(effect).CreateAsync(snapshot);
        var error = await Assert.ThrowsAsync<EffectFailureException>(async () => await source.PrepareAsync(10_000_000, Fx.OutputMode.Transparent,
            RenderTarget.FromComposition(snapshot.Composition)));
        Assert.Equal("TargetOutsideBinding", error.Code); Assert.Equal(effect.Id, error.InstanceId);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task 真实包节点绑定仅在允许运行后代时可单独写提示组(bool descendants)
    {
        var snapshot = Snapshot(); var program = snapshot.LyricPresentation.FindProgram("primary")!;
        var effect = Effect(await Package.Value, new Fx.EffectBinding.Node(new(Root(snapshot)), descendants), program.LeadIns[0].IndicatorContainerId);
        await using var source = await Factory(effect).CreateAsync(snapshot); var target = RenderTarget.FromComposition(snapshot.Composition);
        if (descendants) Assert.Single((await source.PrepareAsync(10_000_000, Fx.OutputMode.Transparent, target)).GroupTransforms);
        else Assert.Equal("TargetOutsideBinding", (await Assert.ThrowsAsync<EffectFailureException>(async () => await source.PrepareAsync(10_000_000,
            Fx.OutputMode.Transparent, target))).Code);
    }
    [Fact]
    public async Task 组能力门禁放行不允许作者额外写未声明元素目标()
    {
        var snapshot = Snapshot(); var effect = Effect(await Package.Value, new Fx.EffectBinding.Project(), Root(snapshot), badWrite: true);
        await using var source = await Factory(effect).CreateAsync(snapshot);
        var error = await Assert.ThrowsAsync<EffectFailureException>(async () => await source.PrepareAsync(12_500_000, Fx.OutputMode.Transparent,
            RenderTarget.FromComposition(snapshot.Composition)));
        Assert.Equal("UndeclaredPackageOutput", error.Code); Assert.Equal(effect.Id, error.InstanceId);
    }
}
