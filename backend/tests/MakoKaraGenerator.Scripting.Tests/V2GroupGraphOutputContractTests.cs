using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Scripting.Tests;

// Source identity is a host concern; the SDK trusts only the
// explicit writableGroups subset and retains transaction poisoning on rejection.
public sealed class V2GroupGraphOutputContractTests
{
    private static readonly ResourceCatalog Resources = new("group", "sha256:" + new string('a', 64), [], ["first", "last"], []);
    private static readonly GroupTarget Group = new(new("source-group"));
    private static TextFrame Frame()
    {
        ContentCatalog content = new([], [], [], []); SceneCatalog scene = new(content, [], [], [], []);
        FrameContext legacy = new() { TimeUs = 100, Project = new() { Content = content, Scene = scene, CanvasSize = new(16, 16) },
            Presentations = new(), Parameters = ParameterValues.Empty, Resources = Resources, Random = new(1, new("group")), Binding = new EffectBinding.Project(),
            GroupGeometry = [new() { Target = Group, ReferenceBounds = new(0, 0, 16, 16), BaseWorldMatrix = Affine2.Identity, WorldPivot = new(8, 8) }] };
        var source = new DeclareContext(content, scene, ParameterValues.Empty, AudioFeatures.NotAnalyzed, Resources, 1, TimelineIndex.Empty, new(16, 16), new("group"));
        return new(source, legacy);
    }
    private static GraphBindingValue Value(string graph = "first") => new(Resources.GetGraph(graph), ParameterValues.Empty, GraphUniformValues.Empty, GraphTransformValues.Empty);
    [Fact]
    public void 组图要求双cap且宿主显式授权并保留完整写事务()
    {
        var frame = Frame(); var output = new TextOutput(frame, ["image.graph", "image.graph.group"], [Group]);
        output.BindGraph(new TextTarget.Group(Group), Value()); output.BindGraph(new TextTarget.Group(Group), Value("last"));
        var writes = output.Complete().Writes.OfType<TextWrite.GraphBinding>().ToArray(); Assert.Equal(2, writes.Length);
        Assert.Equal(new TextTarget.Group(Group), writes[0].Target); Assert.Equal(Resources.GetGraph("last"), writes[1].Value.Graph);
    }
    [Theory]
    [InlineData("basecap", "UndeclaredCapability")] [InlineData("groupcap", "UndeclaredCapability")]
    [InlineData("read-only", "TargetOutsideBinding")] [InlineData("foreign", "TargetOutsideBinding")]
    public void 组读取不授写权且缺cap或越权错误锁存到完成(string kind, string code)
    {
        var frame = Frame(); string[] capabilities = kind == "basecap" ? ["image.graph.group"] : kind == "groupcap" ? ["image.graph"] : ["image.graph", "image.graph.group"];
        var output = new TextOutput(frame, [.. capabilities], kind == "read-only" ? [] : [Group]);
        var target = new TextTarget.Group(kind == "foreign" ? new(new("outside")) : Group);
        var error = Assert.Throws<EffectOutputException>(() => output.BindGraph(target, Value())); Assert.Equal(code, error.Code);
        Assert.Same(error, Assert.Throws<EffectOutputException>(() => output.BindGraph(new TextTarget.Group(Group), Value())));
        Assert.Same(error, Assert.Throws<EffectOutputException>(() => output.Complete()));
    }
    [Fact]
    public void 新增组图能力仍不允许Base图()
    {
        var output = new TextOutput(Frame(), ["image.graph", "image.graph.group"], [Group]);
        var error = Assert.Throws<EffectOutputException>(() => output.BindGraph(new TextTarget.Base(new(new("base"), new("binding"))), Value()));
        Assert.Equal("PropertyTargetUnsupported", error.Code); Assert.Same(error, Assert.Throws<EffectOutputException>(() => output.Complete()));
    }
}
