using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting.Projection;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class FrameBindingScopeTests
{
    private static SceneFrame RuntimeFrame(ProjectSnapshot snapshot, long timeUs)
    {
        var sampled = LyricSceneSampler.SampleFrame(snapshot, timeUs);
        return sampled with { Elements = [.. sampled.Definition.Elements.Select(node => new SceneElementFrame
        {
            Id = node.Id, ParentId = sampled.Lines.FirstOrDefault(line => line.ElementId == node.Id)?.ParentId ?? node.ParentId,
            Kind = node.Kind, SourceBinding = node.SourceBinding,
        })] };
    }
    private static FrameLine Line(ProjectSnapshot snapshot, SceneFrame scene, string id)
        => FrameProjection.Lines(scene, SceneProjection.Create(snapshot, ContentProjection.Create(snapshot))).Single(line => line.LineId.Value == id);

    [Theory]
    [InlineData(ProjectEffectBindingKind.Project)] [InlineData(ProjectEffectBindingKind.Branch)]
    [InlineData(ProjectEffectBindingKind.Lines)] [InlineData(ProjectEffectBindingKind.Tokens)]
    [InlineData(ProjectEffectBindingKind.Clips)] [InlineData(ProjectEffectBindingKind.Node)]
    public void 六类持久绑定投影与当前帧行元素范围保持真实身份(ProjectEffectBindingKind kind)
    {
        var snapshot = ProjectSnapshot.FromProject(LeadInFixture.Create(true, true));
        var scene = RuntimeFrame(snapshot, 40_500_000); var scope = new FrameBindingScope(scene);
        string root = snapshot.LyricPresentation.FindProgram("primary")!.RootContainerId;
        ProjectEffectBinding source = kind switch
        {
            ProjectEffectBindingKind.Project => new(kind), ProjectEffectBindingKind.Branch => new(kind, "primary"),
            ProjectEffectBindingKind.Lines => new(kind, ids: ["chorus"]), ProjectEffectBindingKind.Tokens => new(kind, ids: ["chorus-a"]),
            ProjectEffectBindingKind.Clips => new(kind, ids: ["chorus-clip"]), _ => new(kind, root, includeDescendants: true),
        };
        var binding = ProjectEffectBindingProjection.ToBinding(source);
        var token = FrameProjection.Tokens(scene).Single(item => item.TokenId.Value == "chorus-a");
        Assert.True(scope.ContainsToken(binding, token)); Assert.True(scope.ContainsLine(binding, Line(snapshot, scene, "chorus")));
        Assert.Equal(kind is ProjectEffectBindingKind.Project or ProjectEffectBindingKind.Node, scope.ContainsGroup(binding, new(new(root))));
        if (kind == ProjectEffectBindingKind.Tokens)
            Assert.False(scope.ContainsToken(binding, FrameProjection.Tokens(scene).Single(item => item.TokenId.Value == "chorus-b")));
        Assert.False(scope.ContainsToken(binding, token with { BindingId = new("old-loading") }));
        Assert.False(scope.ContainsLine(binding, Line(snapshot, scene, "chorus") with { BindingId = new("old-loading") }));
        Assert.False(scope.ContainsGroup(binding, new(new("missing-group"))));
    }

    [Fact]
    public void 同槽位轮换使用当前装载而不把之前歌词保留在范围内()
    {
        var snapshot = ProjectSnapshot.FromProject(LeadInFixture.Create(true));
        var before = RuntimeFrame(snapshot, 12_500_000); var after = RuntimeFrame(snapshot, 47_000_000);
        var first = Line(snapshot, before, "first"); var next = Line(snapshot, after, "tail-a");
        Assert.Equal(first.ParentId, next.ParentId); Assert.NotEqual(first.BindingId, next.BindingId);
        var binding = new EffectBinding.Node(first.ParentId, true); var direct = new EffectBinding.Node(first.ParentId, false);
        var firstScope = new FrameBindingScope(before); var nextScope = new FrameBindingScope(after);
        Assert.True(firstScope.ContainsLine(binding, first)); Assert.True(nextScope.ContainsLine(binding, next));
        Assert.False(nextScope.ContainsLine(binding, first)); Assert.False(firstScope.ContainsLine(binding, next));
        Assert.False(firstScope.ContainsLine(direct, first)); Assert.False(nextScope.ContainsLine(direct, next));
        Assert.True(firstScope.ContainsGroup(direct, new(first.ParentId)));
        Assert.NotEqual(first.ParentId.Value, before.Definition.Elements.Single(node => node.Id == first.NodeId.Value).ParentId);
    }

    [Fact]
    public void 运行父树循环缺失或与实际装载不符明确拒绝而不使用持久树兜底()
    {
        var snapshot = ProjectSnapshot.FromProject(LeadInFixture.Create(true)); var scene = RuntimeFrame(snapshot, 12_500_000);
        var line = Line(snapshot, scene, "first"); var slot = scene.Elements.Single(node => node.Id == line.ParentId.Value);
        var binding = new EffectBinding.Node(new("not-an-ancestor"), true);
        SceneFrame Changed(SceneElementFrame replacement) => scene with { Elements = scene.Elements.Replace(slot, replacement) };
        Assert.Equal("InvalidSceneTransform", Assert.Throws<PreparedFrameException>(() => new FrameBindingScope(Changed(slot with { ParentId = slot.Id }))
            .ContainsLine(binding, line)).Code);
        Assert.Equal("InvalidSceneTransform", Assert.Throws<PreparedFrameException>(() => new FrameBindingScope(Changed(slot with { ParentId = "missing" }))
            .ContainsLine(binding, line)).Code);
        var actual = scene.Elements.Single(node => node.Id == line.NodeId.Value);
        var inconsistent = scene with { Elements = scene.Elements.Replace(actual, actual with { ParentId = slot.ParentId }) };
        Assert.Equal("InvalidSceneTransform", Assert.Throws<PreparedFrameException>(() => new FrameBindingScope(inconsistent)
            .ContainsLine(binding, line)).Code);
        Assert.Equal("InvalidSceneTransform", Assert.Throws<PreparedFrameException>(() => new FrameBindingScope(scene with { Elements = scene.Elements.Add(slot) })).Code);
        Assert.Equal("InvalidSceneBinding", Assert.Throws<PreparedFrameException>(() => new FrameBindingScope(scene with { Lines = scene.Lines.Add(scene.Lines[0]) })).Code);
    }

    [Fact]
    public void 持久绑定投影保留集合次序和节点后代开关且拒绝空对象()
    {
        var lines = Assert.IsType<EffectBinding.Lines>(ProjectEffectBindingProjection.ToBinding(new(ProjectEffectBindingKind.Lines, ids: ["b", "a"])));
        Assert.Equal([new LineId("b"), new LineId("a")], lines.Ids.ToArray());
        var tokens = Assert.IsType<EffectBinding.Tokens>(ProjectEffectBindingProjection.ToBinding(new(ProjectEffectBindingKind.Tokens, ids: ["t2", "t1"])));
        Assert.Equal([new TokenId("t2"), new TokenId("t1")], tokens.Ids.ToArray());
        var clips = Assert.IsType<EffectBinding.Clips>(ProjectEffectBindingProjection.ToBinding(new(ProjectEffectBindingKind.Clips, ids: ["c2", "c1"])));
        Assert.Equal([new ClipId("c2"), new ClipId("c1")], clips.Ids.ToArray());
        Assert.False(Assert.IsType<EffectBinding.Node>(ProjectEffectBindingProjection.ToBinding(new(ProjectEffectBindingKind.Node, "group"))).IncludeDescendants);
        Assert.True(Assert.IsType<EffectBinding.Node>(ProjectEffectBindingProjection.ToBinding(new(ProjectEffectBindingKind.Node, "group", includeDescendants: true))).IncludeDescendants);
        Assert.Throws<ArgumentNullException>(() => ProjectEffectBindingProjection.ToBinding(null!));
        Assert.Throws<ArgumentNullException>(() => new FrameBindingScope(null!));
    }
}
