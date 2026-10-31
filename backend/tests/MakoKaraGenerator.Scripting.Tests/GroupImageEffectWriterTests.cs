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
    private static FrameWriter Writer(ResourceCatalog resources, EffectCapabilities capabilities = EffectCapabilities.ImageEffects)
        => new([], capabilities, [Geometry(Group), Geometry(Child)], resources: resources);

    [Fact]
    public void 隐藏真实组同实例最后绑定整体替换且不同组各自保留()
    {
        var catalog = Catalog(); var writer = Writer(catalog);
        var first = new ParameterValues([new("old", new ParameterValue.Number(2))]);
        var last = new ParameterValues([new("amount", new ParameterValue.Number(.5))]);
        writer.ImageEffects.Bind(Group, catalog.GetGraph("first"), first);
        writer.ImageEffects.Bind(Child, catalog.GetGraph("first"), first);
        writer.ImageEffects.Bind(Group, catalog.GetGraph("last"), last);
        var output = writer.Complete(); Assert.Equal(2, output.ImageEffects.Length);
        var group = Assert.Single(output.ImageEffects, item => item.Target == new ImageEffectTarget.Group(Group));
        Assert.Equal("last", group.Graph.Identity.ExportId); Assert.Same(last, group.Parameters);
        Assert.Throws<KeyNotFoundException>(() => group.Parameters.GetNumber("old"));
        Assert.Equal("first", Assert.Single(output.ImageEffects, item => item.Target == new ImageEffectTarget.Group(Child)).Graph.Identity.ExportId);
        Assert.Equal("InvalidOutputStage", Assert.Throws<EffectOutputException>(() => writer.ImageEffects.Bind(Group, group.Graph, last)).Code);
    }

    [Fact]
    public void 组图能力范围图修订和资源参数仍严格校验()
    {
        var catalog = Catalog(); var graph = catalog.GetGraph("first");
        Assert.Equal("UndeclaredCapability", Assert.Throws<EffectOutputException>(() => Writer(catalog, EffectCapabilities.Style)
            .ImageEffects.Bind(Group, graph, ParameterValues.Empty)).Code);
        var writer = Writer(catalog);
        Assert.Equal("TargetOutsideBinding", Assert.Throws<EffectOutputException>(() => writer.ImageEffects.Bind(new GroupTarget(new("missing")), graph, ParameterValues.Empty)).Code);
        Assert.Equal("InvalidGraphResource", Assert.Throws<EffectOutputException>(() => writer.ImageEffects.Bind(Group, Catalog('b').GetGraph("first"), ParameterValues.Empty)).Code);
        var foreignAsset = new ParameterValues([new("image", new ParameterValue.Asset(Catalog('b').GetImage("image")))]);
        Assert.Equal("InvalidParameterResource", Assert.Throws<EffectOutputException>(() => writer.ImageEffects.Bind(Group, graph, foreignAsset)).Code);
        Assert.Empty(writer.Complete().ImageEffects);
    }

    [Fact]
    public void 读取组目录不授写权且失效writer不能登记组图()
    {
        var catalog = Catalog(); var writer = new FrameWriter([], EffectCapabilities.ImageEffects, resources: catalog);
        Assert.Equal("TargetOutsideBinding", Assert.Throws<EffectOutputException>(() => writer.ImageEffects.Bind(Group, catalog.GetGraph("first"), ParameterValues.Empty)).Code);
        var authorized = Writer(catalog); authorized.Invalidate();
        Assert.Equal("InvalidOutputStage", Assert.Throws<EffectOutputException>(() => authorized.ImageEffects.Bind(Group, catalog.GetGraph("first"), ParameterValues.Empty)).Code);
    }
}
