using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class GroupImageEffectWriterTests
{
    private static readonly GroupTarget Group = new(new("group"));
    private static readonly GroupTarget Child = new(new("child"));
    private static ResourceCatalog Catalog(char revision = 'a') => new("group-image", "sha256:" + new string(revision, 64), [], ["first", "last"], ["image"]);
    private static GroupGeometrySample Geometry(GroupTarget target) => new()
    {
        Target = target, BaseWorldMatrix = Affine2.Identity, ReferenceBounds = new(0, 0, 100, 50),
        WorldPivot = new(50, 25), WorldInkCorners = [],
    };
    private static TextOutput Output(ResourceCatalog resources, bool grantGroup = true, params string[] capabilities)
    {
        ContentCatalog content = new([], [], [], []);
        SceneCatalog scene = new(content, [], [], [], []);
        FrameContext legacy = new()
        {
            TimeUs = 0,
            Project = new() { Content = content, Scene = scene, CanvasSize = new(1920, 1080) },
            Parameters = ParameterValues.Empty,
            Resources = resources,
            Random = new(1, new("group-image")),
            Binding = new EffectBinding.Project(),
            Presentations = new(),
            GroupGeometry = [Geometry(Group), Geometry(Child)],
        };
        DeclareContext declare = new(content, scene, ParameterValues.Empty, AudioFeatures.NotAnalyzed, resources, 1, TimelineIndex.Empty, new(1920, 1080), new("group-image"));
        TextFrame frame = new(declare, legacy);
        return new(in frame, [.. capabilities.Length == 0 ? [CapabilityNames.ImageGraph, CapabilityNames.ImageGraphGroup] : capabilities],
            grantGroup ? [Group, Child] : []);
    }

    private static GraphBindingValue Binding(GraphHandle graph, ParameterValues? parameters = null)
        => new(graph, parameters ?? ParameterValues.Empty, GraphUniformValues.Empty, GraphTransformValues.Empty);

    [Fact]
    public void 组图写入保留顺序且各自参数独立并在完成后锁定阶段()
    {
        var catalog = Catalog(); var output = Output(catalog);
        var first = new ParameterValues([new("old", new ParameterValue.Number(2))]);
        var last = new ParameterValues([new("amount", new ParameterValue.Number(.5))]);
        output.BindGraph(new TextTarget.Group(Group), Binding(catalog.GetGraph("first"), first));
        output.BindGraph(new TextTarget.Group(Child), Binding(catalog.GetGraph("first"), first));
        output.BindGraph(new TextTarget.Group(Group), Binding(catalog.GetGraph("last"), last));
        var writes = output.Complete().Writes.Cast<TextWrite.GraphBinding>().ToArray();
        Assert.Equal(3, writes.Length);
        Assert.Equal("last", writes[^1].Value.Graph.Identity.ExportId); Assert.Same(last, writes[^1].Value.Parameters);
        Assert.Throws<KeyNotFoundException>(() => writes[^1].Value.Parameters.GetNumber("old"));
        Assert.Equal("InvalidOutputStage", Assert.Throws<EffectOutputException>(() => output.BindGraph(new TextTarget.Group(Group), Binding(catalog.GetGraph("last"), last))).Code);
    }

    [Fact]
    public void 组图能力范围图修订和资源参数仍严格校验()
    {
        var catalog = Catalog(); var graph = catalog.GetGraph("first");
        Assert.Equal("UndeclaredCapability", Assert.Throws<EffectOutputException>(() => Output(catalog, capabilities: [CapabilityNames.ImageGraph])
            .BindGraph(new TextTarget.Group(Group), Binding(graph))).Code);
        Assert.Equal("TargetOutsideBinding", Assert.Throws<EffectOutputException>(() => Output(catalog).BindGraph(new TextTarget.Group(new(new("missing"))), Binding(graph))).Code);
        Assert.Equal("InvalidGraphResource", Assert.Throws<EffectOutputException>(() => Output(catalog).BindGraph(new TextTarget.Group(Group), Binding(Catalog('b').GetGraph("first")))).Code);
        var foreignAsset = new ParameterValues([new("image", new ParameterValue.Asset(Catalog('b').GetImage("image")))]);
        Assert.Equal("InvalidParameterResource", Assert.Throws<EffectOutputException>(() => Output(catalog).BindGraph(new TextTarget.Group(Group), Binding(graph, foreignAsset))).Code);
        Assert.Empty(Output(catalog).Complete().Writes);
    }

    [Fact]
    public void 读取组目录不授写权且失效输出不能登记组图()
    {
        var catalog = Catalog(); var unauthorized = Output(catalog, grantGroup: false);
        Assert.Equal("TargetOutsideBinding", Assert.Throws<EffectOutputException>(() => unauthorized.BindGraph(new TextTarget.Group(Group), Binding(catalog.GetGraph("first")))).Code);
        var authorized = Output(catalog); authorized.Invalidate();
        Assert.Equal("InvalidOutputStage", Assert.Throws<EffectOutputException>(() => authorized.BindGraph(new TextTarget.Group(Group), Binding(catalog.GetGraph("first")))).Code);
    }
}
