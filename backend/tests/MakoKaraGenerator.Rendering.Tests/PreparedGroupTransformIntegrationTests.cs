using System.Collections.Immutable;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting.Execution;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed partial class PreparedGroupTransformTests
{
    [Fact]
    public async Task 父组与当前槽位各自仿射只传播一次且不改变其他槽位()
    {
        var snapshot = Snapshot(); using SkiaRenderPipeline renderer = new(); var scene = renderer.GetSceneFrame(snapshot, 12_500_000);
        var line = scene.Lines.Single(item => item.Frame.Line!.Id == "first"); string slot = line.ParentId;
        var rootEffects = await Effects(snapshot, ["affine"]); var slotEffects = await Effects(snapshot, ["affine"], new Fx.EffectBinding.Node(new(slot), false), slot);
        ImmutableArray<CompiledFrameEffect> effects = [rootEffects[0], slotEffects[0] with { Id = new("child") }];
        await using var source = await Factory(effects).CreateAsync(snapshot);
        var frame = await source.PrepareAsync(scene.TimeUs, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition));
        Assert.Equal(2, frame.GroupTransforms.Length);
        var rootBasis = scene.GroupGeometries.Single(item => item.Target.NodeId.Value == Root(snapshot)).BaseWorldMatrix;
        Assert.True(rootBasis.TryInverse(out var inverse));
        var rootLocal = frame.GroupTransforms.Single(item => item.Target.NodeId.Value == Root(snapshot)).LocalMatrix;
        var childLocal = frame.GroupTransforms.Single(item => item.Target.NodeId.Value == slot).LocalMatrix;
        var parentDelta = rootBasis.Compose(rootLocal).Compose(inverse);
        var childBasis = scene.GroupGeometries.Single(item => item.Target.NodeId.Value == slot).BaseWorldMatrix;
        var childWorld = parentDelta.Compose(childBasis).Compose(childLocal);
        Assert.True(childBasis.TryInverse(out var childInverse));
        AssertMatrix(childWorld.Compose(childInverse).Compose(Matrix(line.Geometry!.WorldMatrix!.Value)),
            frame.FinalLineGeometry.Single(item => item.Target.BindingId.Value == line.BindingId).BaseGeometry.BaseWorldMatrix);
        foreach (var group in scene.GroupGeometries.Where(item => item.Target.NodeId.Value != slot && item.Target.NodeId.Value != Root(snapshot)
            && snapshot.LyricPresentation.FindNode(item.Target.NodeId.Value)?.ParentId == snapshot.LyricPresentation.FindNode(slot)!.ParentId))
            AssertMatrix(parentDelta.Compose(group.BaseWorldMatrix), frame.FinalGroupGeometry.Single(item => item.Target == group.Target).BaseWorldMatrix);
    }
    [Fact]
    public async Task 父子矩阵组合退化附加到造成失败的真实实例()
    {
        var snapshot = Snapshot(); using SkiaRenderPipeline renderer = new(); var scene = renderer.GetSceneFrame(snapshot, 12_500_000);
        string slot = scene.Lines.Single(item => item.Frame.Line!.Id == "first").ParentId;
        var root = await Effects(snapshot, ["large"]); var child = await Effects(snapshot, ["large"], new Fx.EffectBinding.Node(new(slot), false), slot);
        await using var source = await Factory([root[0], child[0] with { Id = new("child") }]).CreateAsync(snapshot);
        var error = await Assert.ThrowsAsync<EffectFailureException>(async () => await source.PrepareAsync(scene.TimeUs, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition)));
        Assert.Equal("child", error.InstanceId.Value); Assert.Equal("ComposeOutputs", error.Stage); Assert.Equal("InvalidSceneTransform", error.Code);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task 已有控制器声明必须明确允许组变换(bool declaresGroup)
    {
        var files = EffectPackageFixture.Files(); var manifest = EffectPackageFixture.Manifest(files);
        string capability = declaresGroup ? Fx.V2.CapabilityNames.GroupAffine : Fx.V2.CapabilityNames.TokenAffine;
        manifest["exports"]![0]!["outputs"]![0]!["target"] = declaresGroup ? "group" : "tokenFamily";
        manifest["exports"]![0]!["outputs"]![0]!["requires"] = new JsonArray(capability);
        manifest["exports"]![0]!["bindingKinds"] = new JsonArray("project", "node");
        var declaration = EffectPackageDefinition.Read(V2RenderPackageHelper.UpgradeToV2(
            EffectPackageContents.Create(EffectPackageFixture.Bytes(manifest), EffectPackageFixture.Inputs(files), EffectPackageFixture.Profile),
            [Fx.V2.CapabilityNames.GroupAffine, Fx.V2.CapabilityNames.TokenAffine])).Controllers[0];
        var snapshot = Snapshot(); var effects = await Effects(snapshot, ["move"]);
        await using var source = await Factory([effects[0] with { PackageDeclaration = declaration }]).CreateAsync(snapshot);
        if (declaresGroup) Assert.Single((await source.PrepareAsync(10_000_000, Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition))).GroupTransforms);
        else Assert.Equal("UndeclaredPackageOutput", (await Assert.ThrowsAsync<EffectFailureException>(async () => await source.PrepareAsync(10_000_000,
            Fx.OutputMode.Transparent, RenderTarget.FromComposition(snapshot.Composition)))).Code);
    }
    [Fact]
    public async Task 连续粒子在出生时使用真实最终组播放头且之后保留世界位置()
    {
        var snapshot = Snapshot(); var effects = await Effects(snapshot, ["particle"]);
        await using var source = await Factory(effects).CreateAsync(snapshot);
        var target = RenderTarget.FromComposition(snapshot.Composition);
        var frame = await source.PrepareAsync(12_100_000, Fx.OutputMode.Transparent, target);
        var particle = Assert.Single(frame.V2Particles); Assert.Equal(12_100_000, particle.Birth.BirthUs);
        var cursor = frame.Playback.GetCursors(Fx.CursorAnchorMode.AdvanceCenter).Single(item => item.Identity.BranchId.Value == "primary");
        Assert.Equal(cursor.Position, particle.Birth.BirthPosition); Assert.Equal(particle.Birth.BirthPosition, particle.Position);
        var later = await source.PrepareAsync(12_150_000, Fx.OutputMode.Transparent, target);
        Assert.Equal(particle.Birth.BirthPosition, later.V2Particles.Single(item => item.Birth.Id == particle.Birth.Id).Position);
        var copy = await source.PrepareAsync(12_100_000, Fx.OutputMode.Transparent, target);
        Assert.Equal(particle, Assert.Single(copy.V2Particles));
    }
}
